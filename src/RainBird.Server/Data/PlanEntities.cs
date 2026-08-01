using System.ComponentModel.DataAnnotations;

namespace RainBird.Server.Data;

/// <summary>
/// A named watering plan this app executes itself.
///
/// The controller's own programs are deliberately left empty; this app opens and
/// closes valves on its own schedule. That is what makes the arrangements the
/// hardware cannot express possible — several passes a day, cycle and soak, a
/// watering window — and on firmware that will not expose its schedule at all, it is
/// the only way to schedule anything.
/// </summary>
public class WateringPlan
{
    public int Id { get; set; }
    public int ControllerId { get; set; }

    [MaxLength(80)]
    public string Name { get; set; } = "New plan";

    /// <summary>What this plan is for, in the user's words.</summary>
    [MaxLength(300)]
    public string Description { get; set; } = "";

    public bool Enabled { get; set; } = true;

    // ------------------------------------------------------------ which days

    public PlanFrequency Frequency { get; set; } = PlanFrequency.DaysOfWeek;

    /// <summary>Seven flags, Sunday first. Used when <see cref="Frequency"/> is DaysOfWeek.</summary>
    [MaxLength(7)]
    public string DaysOfWeek { get; set; } = "0101010";

    /// <summary>Interval for <see cref="PlanFrequency.EveryNDays"/>.</summary>
    public int IntervalDays { get; set; } = 2;

    /// <summary>Anchor for the interval, so "every 3 days" is stable across restarts.</summary>
    public DateOnly? IntervalAnchor { get; set; }

    // ----------------------------------------------------------- what times

    /// <summary>
    /// Start times as minutes from midnight, comma separated.
    ///
    /// Several a day is the point: seed germination wants short, frequent passes,
    /// which no Rain Bird program can express because a program's start times all run
    /// the same durations and the hardware caps them.
    /// </summary>
    [MaxLength(200)]
    public string StartTimes { get; set; } = "360";

    /// <summary>
    /// Latest a pass may *begin*, as minutes from midnight. Null means no limit.
    /// A pass already running is never cut off by this.
    /// </summary>
    public int? LatestStartMinute { get; set; }

    // ------------------------------------------------------------ how long

    /// <summary>Scales every zone's duration. 100 runs them as configured.</summary>
    public int SeasonalAdjustPercent { get; set; } = 100;

    /// <summary>
    /// Split each zone's time into several shorter passes with a soak between, so
    /// water has time to absorb instead of running off on slopes and clay.
    /// </summary>
    public bool CycleSoakEnabled { get; set; }

    /// <summary>How many passes to split each zone into.</summary>
    public int Cycles { get; set; } = 2;

    /// <summary>Rest between a zone's passes, in minutes.</summary>
    public int SoakMinutes { get; set; } = 15;

    /// <summary>Let the weather rules skip this plan.</summary>
    public bool WeatherSkipEnabled { get; set; } = true;

    public int SortOrder { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public List<PlanZone> Zones { get; set; } = [];

    // ---------------------------------------------------------- convenience

    public IReadOnlyList<int> StartTimeMinutes =>
        StartTimes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var value) ? value : -1)
            .Where(value => value is >= 0 and < 1440)
            .Order()
            .ToList();

    public IReadOnlyList<bool> DayFlags =>
        Enumerable.Range(0, 7).Select(i => i < DaysOfWeek.Length && DaysOfWeek[i] == '1').ToList();
}

public enum PlanFrequency
{
    DaysOfWeek,
    EveryNDays,
    OddDays,
    EvenDays,
    EveryDay,
}

/// <summary>How long one zone runs in a plan.</summary>
public class PlanZone
{
    public int Id { get; set; }
    public int WateringPlanId { get; set; }
    public WateringPlan? Plan { get; set; }

    public int StationNumber { get; set; }

    /// <summary>Total minutes per pass, before seasonal adjust and cycle splitting.</summary>
    public int Minutes { get; set; }

    public int SortOrder { get; set; }
}

/// <summary>
/// One execution of a plan: the queue of steps and how far through it we are.
///
/// Persisted rather than kept in memory so a restart mid-run is visible and
/// recoverable, and so history shows what the plan actually did.
/// </summary>
public class PlanRun
{
    public long Id { get; set; }
    public int ControllerId { get; set; }
    public int WateringPlanId { get; set; }

    [MaxLength(80)]
    public string PlanName { get; set; } = "";

    /// <summary>The start time this execution belongs to, so a pass runs once only.</summary>
    public DateOnly ScheduledDate { get; set; }
    public int ScheduledStartMinute { get; set; }

    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? EndedUtc { get; set; }

    public PlanRunStatus Status { get; set; } = PlanRunStatus.Running;

    [MaxLength(300)]
    public string? Detail { get; set; }

    /// <summary>Index of the step currently running, or the one that was reached.</summary>
    public int StepIndex { get; set; }

    public List<PlanRunStep> Steps { get; set; } = [];
}

public enum PlanRunStatus
{
    Running,
    Completed,
    Cancelled,
    Skipped,
    Failed,
}

/// <summary>One zone pass within a plan run.</summary>
public class PlanRunStep
{
    public long Id { get; set; }
    public long PlanRunId { get; set; }
    public PlanRun? Run { get; set; }

    public int Ordinal { get; set; }
    public int StationNumber { get; set; }

    /// <summary>Which pass of this zone, when cycle and soak splits it.</summary>
    public int Cycle { get; set; }

    public int Minutes { get; set; }

    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? EndedUtc { get; set; }
    public PlanStepStatus Status { get; set; } = PlanStepStatus.Pending;
}

public enum PlanStepStatus
{
    Pending,
    Running,
    Completed,
    Skipped,
    Failed,
}
