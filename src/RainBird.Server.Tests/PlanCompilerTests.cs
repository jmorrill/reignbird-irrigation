using RainBird.Server.Data;
using RainBird.Server.Services;

namespace RainBird.Server.Tests;

/// <summary>
/// The scheduling logic: what a plan expands into, and which days and times it runs.
///
/// This is where the subtle mistakes would live — losing a minute when splitting a
/// zone across passes, soaking for the wrong length, firing a pass twice — so it is
/// written as pure functions and tested directly.
/// </summary>
public class PlanCompilerTests
{
    private static WateringPlan Plan(
        int seasonalAdjust = 100,
        bool cycleSoak = false,
        int cycles = 2,
        int soakMinutes = 15) => new()
    {
        Name = "Test",
        SeasonalAdjustPercent = seasonalAdjust,
        CycleSoakEnabled = cycleSoak,
        Cycles = cycles,
        SoakMinutes = soakMinutes,
    };

    private static List<PlanZone> Zones(params (int Station, int Minutes)[] zones) =>
        zones.Select((z, i) => new PlanZone
        {
            StationNumber = z.Station,
            Minutes = z.Minutes,
            SortOrder = i,
        }).ToList();

    // ------------------------------------------------------------ basic shape

    [Fact]
    public void A_plain_plan_runs_each_zone_once_in_order()
    {
        var steps = PlanCompiler.Compile(Plan(), Zones((1, 10), (2, 15), (3, 5)));

        Assert.Equal(3, steps.Count);
        Assert.Equal([1, 2, 3], steps.Select(s => s.StationNumber));
        Assert.Equal([10, 15, 5], steps.Select(s => s.Minutes));
        Assert.DoesNotContain(steps, s => s.IsSoak);
    }

    [Fact]
    public void Zones_with_no_run_time_are_left_out()
    {
        var steps = PlanCompiler.Compile(Plan(), Zones((1, 10), (2, 0), (3, 5)));

        Assert.Equal([1, 3], steps.Select(s => s.StationNumber));
    }

    [Fact]
    public void A_plan_with_nothing_to_water_compiles_to_nothing()
    {
        Assert.Empty(PlanCompiler.Compile(Plan(), Zones((1, 0), (2, 0))));
        Assert.Empty(PlanCompiler.Compile(Plan(), []));
    }

    // -------------------------------------------------------- seasonal adjust

    [Theory]
    [InlineData(100, 20)]
    [InlineData(50, 10)]
    [InlineData(150, 30)]
    [InlineData(75, 15)]
    public void Seasonal_adjust_scales_every_zone(int percent, int expected)
    {
        var steps = PlanCompiler.Compile(Plan(seasonalAdjust: percent), Zones((1, 20)));

        Assert.Equal(expected, steps[0].Minutes);
    }

    [Fact]
    public void Seasonal_adjust_never_rounds_a_zone_away_to_nothing()
    {
        // 10% of 4 minutes is 0.4, which must not become a zero-length run.
        var steps = PlanCompiler.Compile(Plan(seasonalAdjust: 10), Zones((1, 4)));

        Assert.Equal(1, steps[0].Minutes);
    }

    [Fact]
    public void Seasonal_adjust_of_zero_waters_nothing()
    {
        Assert.Empty(PlanCompiler.Compile(Plan(seasonalAdjust: 0), Zones((1, 20), (2, 10))));
    }

    // --------------------------------------------------------- cycle and soak

    /// <summary>
    /// Passes are interleaved across zones, which is the whole point: zone 1 is
    /// resting while zones 2 and 3 water, so the soak costs no extra time.
    /// </summary>
    [Fact]
    public void Cycle_and_soak_interleaves_zones_by_pass()
    {
        var steps = PlanCompiler.Compile(
            Plan(cycleSoak: true, cycles: 2, soakMinutes: 5),
            Zones((1, 20), (2, 20), (3, 20)));

        var watering = steps.Where(s => !s.IsSoak).ToList();

        // First pass of every zone, then the second pass of every zone.
        Assert.Equal([1, 2, 3, 1, 2, 3], watering.Select(s => s.StationNumber));
        Assert.Equal([1, 1, 1, 2, 2, 2], watering.Select(s => s.Cycle));
        Assert.All(watering, s => Assert.Equal(10, s.Minutes));
    }

    [Fact]
    public void Cycle_and_soak_preserves_each_zones_total_time()
    {
        var zones = Zones((1, 21), (2, 10), (3, 7));
        var steps = PlanCompiler.Compile(Plan(cycleSoak: true, cycles: 3, soakMinutes: 10), zones);

        foreach (var zone in zones)
        {
            var total = steps.Where(s => s.StationNumber == zone.StationNumber).Sum(s => s.Minutes);
            Assert.Equal(zone.Minutes, total);
        }
    }

    [Fact]
    public void Splitting_puts_the_remainder_in_the_earliest_passes()
    {
        // 7 minutes over 2 passes is 4 then 3, not 3 and 3 with a minute lost.
        Assert.Equal([4, 3], PlanCompiler.Split(7, 2));
        Assert.Equal([4, 3, 3], PlanCompiler.Split(10, 3));
        Assert.Equal([1, 1, 0], PlanCompiler.Split(2, 3));
    }

    /// <summary>
    /// With enough zones the other zones cover the soak, so no waiting is needed.
    /// </summary>
    [Fact]
    public void No_wait_is_inserted_when_the_other_zones_cover_the_soak()
    {
        var steps = PlanCompiler.Compile(
            Plan(cycleSoak: true, cycles: 2, soakMinutes: 10),
            Zones((1, 20), (2, 20), (3, 20)));

        // Zone 1 rests for 20 minutes while zones 2 and 3 run — well past the 10 asked for.
        Assert.DoesNotContain(steps, s => s.IsSoak);
    }

    /// <summary>
    /// A single-zone plan has nothing to fill the soak with, so the wait has to be
    /// real or the "soak" would not happen at all.
    /// </summary>
    [Fact]
    public void A_wait_is_inserted_when_there_is_nothing_else_to_run()
    {
        var steps = PlanCompiler.Compile(
            Plan(cycleSoak: true, cycles: 2, soakMinutes: 15),
            Zones((1, 20)));

        var soak = Assert.Single(steps.Where(s => s.IsSoak));
        Assert.Equal(15, soak.Minutes);
        Assert.Equal([10, 15, 10], steps.Select(s => s.Minutes));
    }

    [Fact]
    public void A_partial_wait_covers_only_the_shortfall()
    {
        // Zone 2 gives 5 minutes of natural rest; only 10 more are needed.
        var steps = PlanCompiler.Compile(
            Plan(cycleSoak: true, cycles: 2, soakMinutes: 15),
            Zones((1, 20), (2, 10)));

        var soak = Assert.Single(steps.Where(s => s.IsSoak));
        Assert.Equal(10, soak.Minutes);
    }

    [Fact]
    public void No_soak_is_added_after_the_final_pass()
    {
        var steps = PlanCompiler.Compile(
            Plan(cycleSoak: true, cycles: 3, soakMinutes: 20),
            Zones((1, 30)));

        Assert.False(steps[^1].IsSoak);
        Assert.Equal(2, steps.Count(s => s.IsSoak));
    }

    [Fact]
    public void One_cycle_is_the_same_as_not_splitting_at_all()
    {
        var steps = PlanCompiler.Compile(
            Plan(cycleSoak: true, cycles: 1, soakMinutes: 30),
            Zones((1, 20), (2, 10)));

        Assert.Equal(2, steps.Count);
        Assert.DoesNotContain(steps, s => s.IsSoak);
    }

    [Fact]
    public void Totals_separate_watering_from_elapsed_time()
    {
        var steps = PlanCompiler.Compile(
            Plan(cycleSoak: true, cycles: 2, soakMinutes: 15),
            Zones((1, 20)));

        Assert.Equal(20, PlanCompiler.WateringMinutes(steps));
        Assert.Equal(35, PlanCompiler.ElapsedMinutes(steps));
    }

    // ------------------------------------------------------------- which days

    [Fact]
    public void Days_of_week_follow_the_flags_sunday_first()
    {
        var plan = Plan();
        plan.Frequency = PlanFrequency.DaysOfWeek;
        plan.DaysOfWeek = "0101010"; // Mon, Wed, Fri

        Assert.False(PlanCompiler.RunsOn(plan, new DateOnly(2026, 8, 2)));  // Sunday
        Assert.True(PlanCompiler.RunsOn(plan, new DateOnly(2026, 8, 3)));   // Monday
        Assert.False(PlanCompiler.RunsOn(plan, new DateOnly(2026, 8, 4)));  // Tuesday
        Assert.True(PlanCompiler.RunsOn(plan, new DateOnly(2026, 8, 5)));   // Wednesday
        Assert.True(PlanCompiler.RunsOn(plan, new DateOnly(2026, 8, 7)));   // Friday
    }

    [Fact]
    public void Odd_and_even_follow_the_day_of_the_month()
    {
        var odd = Plan();
        odd.Frequency = PlanFrequency.OddDays;
        var even = Plan();
        even.Frequency = PlanFrequency.EvenDays;

        Assert.True(PlanCompiler.RunsOn(odd, new DateOnly(2026, 8, 7)));
        Assert.False(PlanCompiler.RunsOn(odd, new DateOnly(2026, 8, 8)));
        Assert.True(PlanCompiler.RunsOn(even, new DateOnly(2026, 8, 8)));
    }

    [Fact]
    public void Every_n_days_counts_from_the_anchor()
    {
        var plan = Plan();
        plan.Frequency = PlanFrequency.EveryNDays;
        plan.IntervalDays = 3;
        plan.IntervalAnchor = new DateOnly(2026, 8, 1);

        Assert.True(PlanCompiler.RunsOn(plan, new DateOnly(2026, 8, 1)));
        Assert.False(PlanCompiler.RunsOn(plan, new DateOnly(2026, 8, 2)));
        Assert.False(PlanCompiler.RunsOn(plan, new DateOnly(2026, 8, 3)));
        Assert.True(PlanCompiler.RunsOn(plan, new DateOnly(2026, 8, 4)));
        Assert.True(PlanCompiler.RunsOn(plan, new DateOnly(2026, 8, 7)));
    }

    [Fact]
    public void Every_n_days_does_not_run_before_its_anchor()
    {
        var plan = Plan();
        plan.Frequency = PlanFrequency.EveryNDays;
        plan.IntervalDays = 2;
        plan.IntervalAnchor = new DateOnly(2026, 8, 10);

        Assert.False(PlanCompiler.RunsOn(plan, new DateOnly(2026, 8, 8)));
    }

    // ------------------------------------------------------------ which times

    [Fact]
    public void Start_times_are_parsed_sorted_and_bounds_checked()
    {
        var plan = Plan();
        plan.StartTimes = "1140, 360,660, 900, 9999, -5";

        Assert.Equal([360, 660, 900, 1140], plan.StartTimeMinutes);
    }

    [Fact]
    public void A_pass_that_would_begin_after_the_window_closes_is_dropped()
    {
        var plan = Plan();
        plan.Frequency = PlanFrequency.EveryDay;
        plan.StartTimes = "360,660,900,1140";
        plan.LatestStartMinute = 700;

        Assert.Equal([360, 660], PlanCompiler.StartTimesOn(plan, new DateOnly(2026, 8, 3)));
    }

    [Fact]
    public void No_start_times_apply_on_a_day_the_plan_does_not_run()
    {
        var plan = Plan();
        plan.Frequency = PlanFrequency.DaysOfWeek;
        plan.DaysOfWeek = "0000000";

        Assert.Empty(PlanCompiler.StartTimesOn(plan, new DateOnly(2026, 8, 3)));
    }

    // --------------------------------------------------------------- next run

    [Fact]
    public void Next_run_finds_the_following_start_time_today()
    {
        var plan = Plan();
        plan.Frequency = PlanFrequency.EveryDay;
        plan.StartTimes = "360,660,900";

        var from = new DateTimeOffset(2026, 8, 3, 7, 0, 0, TimeSpan.Zero);
        var next = PlanCompiler.NextRun(plan, from, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2026, 8, 3, 11, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void Next_run_rolls_over_to_the_following_watering_day()
    {
        var plan = Plan();
        plan.Frequency = PlanFrequency.DaysOfWeek;
        plan.DaysOfWeek = "0101010"; // Mon, Wed, Fri
        plan.StartTimes = "360";

        // Monday evening: the next pass is Wednesday morning.
        var from = new DateTimeOffset(2026, 8, 3, 20, 0, 0, TimeSpan.Zero);
        var next = PlanCompiler.NextRun(plan, from, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2026, 8, 5, 6, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void A_disabled_plan_has_no_next_run()
    {
        var plan = Plan();
        plan.Enabled = false;
        plan.Frequency = PlanFrequency.EveryDay;

        Assert.Null(PlanCompiler.NextRun(plan, DateTimeOffset.UtcNow, TimeSpan.Zero));
    }

    // ------------------------------------------------------------- the presets

    /// <summary>
    /// The case that motivated all of this: four short passes a day, which no Rain
    /// Bird program can express.
    /// </summary>
    [Fact]
    public void The_grass_seed_preset_waters_four_times_a_day_for_ten_minutes()
    {
        var zones = new List<ZoneRecord>
        {
            new() { StationNumber = 1, Enabled = true, SprinklerType = SprinklerType.Rotor },
            new() { StationNumber = 2, Enabled = true, SprinklerType = SprinklerType.FixedSpray },
        };

        var preset = PlanPresets.Find("grass-seed");
        Assert.NotNull(preset);

        var plan = preset!.Build(zones);

        Assert.Equal(PlanFrequency.EveryDay, plan.Frequency);
        Assert.Equal([360, 660, 900, 1140], plan.StartTimeMinutes);
        Assert.All(plan.Zones, zone => Assert.Equal(10, zone.Minutes));

        // Deliberately opts out of weather skips: a seed bed that dries out is a dead
        // seed bed, rain forecast or not.
        Assert.False(plan.WeatherSkipEnabled);

        var steps = PlanCompiler.Compile(plan, plan.Zones);
        Assert.Equal(20, PlanCompiler.WateringMinutes(steps));           // per pass
        Assert.Equal(80, PlanCompiler.WateringMinutes(steps) * 4);       // per day
    }

    [Fact]
    public void Every_preset_produces_a_runnable_plan()
    {
        var zones = Enumerable.Range(1, 6)
            .Select(i => new ZoneRecord { StationNumber = i, Enabled = true })
            .ToList();

        foreach (var preset in PlanPresets.All)
        {
            var plan = preset.Build(zones);

            Assert.False(string.IsNullOrWhiteSpace(plan.Name));
            Assert.NotEmpty(plan.StartTimeMinutes);
            Assert.NotEmpty(plan.Zones);

            var steps = PlanCompiler.Compile(plan, plan.Zones);
            Assert.NotEmpty(steps);
            Assert.All(steps, step => Assert.True(step.Minutes > 0, $"{preset.Key} produced a zero-length step."));
        }
    }

    [Fact]
    public void Presets_size_run_times_to_the_sprinkler_type()
    {
        // A rotor throws far less water per minute than a spray head, so a single
        // default across every zone would badly mis-water most of them.
        var rotor = new ZoneRecord { StationNumber = 1, Enabled = true, SprinklerType = SprinklerType.Rotor };
        var spray = new ZoneRecord { StationNumber = 2, Enabled = true, SprinklerType = SprinklerType.FixedSpray };

        Assert.True(PlanPresets.DefaultMinutes(rotor) > PlanPresets.DefaultMinutes(spray));
    }

    // ------------------------------------------- what the hardware can be told

    [Fact]
    public void A_step_is_never_longer_than_a_run_command_can_express()
    {
        // 240 minutes is allowed by the editor, and 200% seasonal adjust doubles it.
        // The command carries its duration in one byte, so 480 cannot be sent.
        var steps = PlanCompiler.Compile(Plan(seasonalAdjust: 200), Zones((1, 240)));

        Assert.Equal(PlanCompiler.MaxStepMinutes, Assert.Single(steps).Minutes);
    }

    [Fact]
    public void The_step_length_is_what_will_actually_be_commanded()
    {
        // The engine used to clamp the command to 255 while still waiting out the
        // unclamped 480 before advancing, so the yard sat idle for the difference.
        // Whatever the queue says is now what gets sent.
        var steps = PlanCompiler.Compile(Plan(seasonalAdjust: 200), Zones((1, 240), (2, 10)));

        Assert.All(steps, step => Assert.InRange(step.Minutes, 1, PlanCompiler.MaxStepMinutes));
    }

    // ------------------------------------------------------- disabled zones

    [Fact]
    public void A_zone_that_is_no_longer_available_is_not_watered()
    {
        // Zone 2 has been switched off, or its station stopped being reported. The
        // plan still lists it; it must not run.
        var steps = PlanCompiler.Compile(
            Plan(), Zones((1, 10), (2, 15), (3, 5)), availableStations: new HashSet<int> { 1, 3 });

        Assert.Equal([1, 3], steps.Where(s => !s.IsSoak).Select(s => s.StationNumber));
    }

    [Fact]
    public void A_plan_whose_zones_are_all_unavailable_runs_nothing()
    {
        var steps = PlanCompiler.Compile(
            Plan(), Zones((1, 10), (2, 15)), availableStations: new HashSet<int>());

        Assert.Empty(steps);
    }

    [Fact]
    public void No_zone_list_means_no_opinion_about_availability()
    {
        // Callers with no zone table to consult — the DTO mapper — get the old
        // behaviour rather than an empty plan.
        var steps = PlanCompiler.Compile(Plan(), Zones((1, 10), (2, 15)));

        Assert.Equal(2, steps.Count(s => !s.IsSoak));
    }
}
