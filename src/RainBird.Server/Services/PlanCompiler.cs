using RainBird.Server.Data;

namespace RainBird.Server.Services;

/// <summary>One instruction in a compiled plan.</summary>
/// <param name="StationNumber">Zone to open, or null for a soak.</param>
/// <param name="Minutes">How long, always at least one.</param>
/// <param name="Cycle">Which pass of this zone, 1-based.</param>
public sealed record PlanStep(int? StationNumber, int Minutes, int Cycle)
{
    public bool IsSoak => StationNumber is null;

    public override string ToString() =>
        IsSoak ? $"soak {Minutes}m" : $"zone {StationNumber} for {Minutes}m (pass {Cycle})";
}

/// <summary>
/// Turns a plan into the exact sequence of zone runs to execute, and decides which
/// days and times it applies to.
///
/// Pure functions with no database or device access, because this is where the
/// fiddly parts live — cycle-and-soak interleaving, seasonal adjust, interval
/// anchoring — and they are much easier to trust when they can be tested directly.
/// </summary>
public static class PlanCompiler
{
    /// <summary>
    /// Expands a plan into its run queue.
    ///
    /// With cycle and soak on, zones are interleaved by pass — every zone's first
    /// pass, then every zone's second — because that is what makes the soak free:
    /// zone 1 is resting while zones 2 and 3 water. A gap is only inserted when the
    /// other zones do not take long enough on their own, which in practice means a
    /// plan with very few zones.
    /// </summary>
    public static IReadOnlyList<PlanStep> Compile(WateringPlan plan, IReadOnlyList<PlanZone> zones)
    {
        var active = zones
            .Where(zone => zone.Minutes > 0)
            .OrderBy(zone => zone.SortOrder)
            .ThenBy(zone => zone.StationNumber)
            .ToList();

        if (active.Count == 0) return [];

        var adjusted = active
            .Select(zone => (zone.StationNumber, Minutes: ApplySeasonalAdjust(zone.Minutes, plan.SeasonalAdjustPercent)))
            .Where(entry => entry.Minutes > 0)
            .ToList();

        if (adjusted.Count == 0) return [];

        if (!plan.CycleSoakEnabled || plan.Cycles <= 1)
            return adjusted.Select(entry => new PlanStep(entry.StationNumber, entry.Minutes, 1)).ToList();

        var cycles = Math.Clamp(plan.Cycles, 2, 10);
        var split = adjusted.ToDictionary(entry => entry.StationNumber, entry => Split(entry.Minutes, cycles));

        var steps = new List<PlanStep>();
        for (var pass = 0; pass < cycles; pass++)
        {
            var passStart = steps.Count;

            foreach (var (station, _) in adjusted)
            {
                var minutes = split[station][pass];
                if (minutes > 0) steps.Add(new PlanStep(station, minutes, pass + 1));
            }

            // Nothing to soak after the final pass.
            if (pass == cycles - 1) continue;

            // The rest of this pass is the soak for the zone that ran first. If the
            // other zones do not cover it, wait out the difference.
            var passMinutes = steps.Skip(passStart).Sum(step => step.Minutes);
            var firstZoneMinutes = steps.Count > passStart ? steps[passStart].Minutes : 0;
            var naturalSoak = passMinutes - firstZoneMinutes;
            var shortfall = plan.SoakMinutes - naturalSoak;

            if (shortfall > 0) steps.Add(new PlanStep(null, shortfall, pass + 1));
        }

        return steps;
    }

    /// <summary>
    /// Splits a duration into whole minutes across passes, keeping the total exact.
    /// The remainder goes to the earliest passes, so a 7-minute zone over 2 passes
    /// runs 4 then 3 rather than losing a minute.
    /// </summary>
    internal static int[] Split(int totalMinutes, int cycles)
    {
        var result = new int[cycles];
        var each = totalMinutes / cycles;
        var remainder = totalMinutes % cycles;

        for (var i = 0; i < cycles; i++)
            result[i] = each + (i < remainder ? 1 : 0);

        // A duration shorter than the cycle count cannot be split evenly; run what
        // there is in the earliest passes rather than rounding it away to nothing.
        return result;
    }

    internal static int ApplySeasonalAdjust(int minutes, int percent)
    {
        if (percent == 100) return minutes;
        if (percent <= 0) return 0;

        var scaled = (int)Math.Round(minutes * percent / 100.0, MidpointRounding.AwayFromZero);
        return Math.Max(1, scaled);
    }

    /// <summary>Total watering time, excluding soaks.</summary>
    public static int WateringMinutes(IReadOnlyList<PlanStep> steps) =>
        steps.Where(step => !step.IsSoak).Sum(step => step.Minutes);

    /// <summary>Wall-clock length of a pass, soaks included.</summary>
    public static int ElapsedMinutes(IReadOnlyList<PlanStep> steps) =>
        steps.Sum(step => step.Minutes);

    // ------------------------------------------------------------- calendar

    /// <summary>Whether a plan waters on a given date.</summary>
    public static bool RunsOn(WateringPlan plan, DateOnly date) => plan.Frequency switch
    {
        PlanFrequency.EveryDay => true,
        PlanFrequency.OddDays => date.Day % 2 == 1,
        PlanFrequency.EvenDays => date.Day % 2 == 0,
        PlanFrequency.DaysOfWeek => plan.DayFlags[(int)date.DayOfWeek],
        PlanFrequency.EveryNDays => RunsOnInterval(plan, date),
        _ => false,
    };

    private static bool RunsOnInterval(WateringPlan plan, DateOnly date)
    {
        var interval = Math.Max(1, plan.IntervalDays);
        var anchor = plan.IntervalAnchor ?? DateOnly.FromDateTime(plan.CreatedUtc.UtcDateTime);

        var elapsed = date.DayNumber - anchor.DayNumber;
        if (elapsed < 0) return false;

        return elapsed % interval == 0;
    }

    /// <summary>
    /// The start times that apply on a date, in order. Times past the plan's latest
    /// start are dropped — a pass that would begin after the window closes is not
    /// worth starting, though one already running is never cut short.
    /// </summary>
    public static IReadOnlyList<int> StartTimesOn(WateringPlan plan, DateOnly date)
    {
        if (!RunsOn(plan, date)) return [];

        var times = plan.StartTimeMinutes;
        if (plan.LatestStartMinute is not { } latest) return times;

        return times.Where(minute => minute <= latest).ToList();
    }

    /// <summary>
    /// The next time this plan would water, at or after a given moment. Looks a
    /// fortnight ahead, which is long enough for any interval the UI offers.
    /// </summary>
    public static DateTimeOffset? NextRun(WateringPlan plan, DateTimeOffset from, TimeSpan offset)
    {
        if (!plan.Enabled) return null;

        for (var dayOffset = 0; dayOffset <= 14; dayOffset++)
        {
            var date = DateOnly.FromDateTime(from.DateTime).AddDays(dayOffset);

            foreach (var minute in StartTimesOn(plan, date))
            {
                var candidate = new DateTimeOffset(
                    date.Year, date.Month, date.Day, minute / 60, minute % 60, 0, offset);

                if (candidate > from) return candidate;
            }
        }

        return null;
    }
}
