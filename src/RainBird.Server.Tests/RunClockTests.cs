using RainBird.Protocol;
using RainBird.Server.Api;
using RainBird.Server.Services;

namespace RainBird.Server.Tests;

/// <summary>
/// The countdown for hardware that cannot report one.
///
/// Firmware without SIP <c>4C</c> — including the ESP-ME3 this was built against —
/// has no command that returns seconds remaining, so it always reads zero. The zone
/// waters correctly and the plan advances; only the timer is wrong, showing 0:00
/// beside a sprinkler that is visibly running. Every run this app starts is a timed
/// one, so the app can answer from what it scheduled.
/// </summary>
public class RunClockTests
{
    private static CombinedState StateWith(int activeStation, int remainingSeconds) => new()
    {
        ControllerTime = new TimeOnly(6, 0),
        ControllerDate = new DateOnly(2026, 8, 1),
        RainDelayDays = 0,
        SensorState = RainSensorState.Dry,
        ControllerEnabled = true,
        SeasonalAdjustPercent = 100,
        RemainingRuntimeSeconds = remainingSeconds,
        ActiveStation = activeStation,
    };

    [Fact]
    public void Nothing_started_means_nothing_to_report()
    {
        var clock = new RunClock();
        Assert.Equal(0, clock.RemainingSeconds(controllerId: 1, activeStation: 3));
    }

    [Fact]
    public void A_started_run_counts_down()
    {
        var clock = new RunClock();
        clock.Started(controllerId: 1, station: 3, minutes: 10);

        var remaining = clock.RemainingSeconds(1, 3);

        // Allowing a second of slack rather than asserting exactly 600: the clock is
        // wall-clock based, which is the point of it.
        Assert.InRange(remaining, 598, 600);
    }

    [Fact]
    public void An_expectation_for_a_different_zone_is_not_reported()
    {
        var clock = new RunClock();
        clock.Started(controllerId: 1, station: 3, minutes: 10);

        // Zone 3 was scheduled, but zone 4 is the one watering — the plan moved on
        // without this being updated. Reporting zone 3's remaining time against zone
        // 4 would be worse than reporting nothing.
        Assert.Equal(0, clock.RemainingSeconds(1, 4));
    }

    [Fact]
    public void Clearing_forgets_the_run()
    {
        var clock = new RunClock();
        clock.Started(controllerId: 1, station: 3, minutes: 10);
        clock.Cleared(1);

        Assert.Equal(0, clock.RemainingSeconds(1, 3));
    }

    [Fact]
    public void Controllers_do_not_borrow_each_other_countdowns()
    {
        var clock = new RunClock();
        clock.Started(controllerId: 1, station: 3, minutes: 10);

        Assert.Equal(0, clock.RemainingSeconds(controllerId: 2, activeStation: 3));
    }

    [Fact]
    public void An_elapsed_run_reports_zero_rather_than_a_negative()
    {
        var clock = new RunClock();
        clock.Started(controllerId: 1, station: 3, TimeSpan.FromSeconds(-30));

        Assert.Equal(0, clock.RemainingSeconds(1, 3));
    }

    // ------------------------------------------------- what the client is sent

    [Fact]
    public void The_device_wins_whenever_it_has_an_answer()
    {
        var clock = new RunClock();
        clock.Started(controllerId: 1, station: 3, minutes: 10);

        // Firmware that reports 42 seconds is right and the app's own arithmetic is
        // not consulted, even though it would have said 600.
        var dto = ControllerStateDto.From(StateWith(activeStation: 3, remainingSeconds: 42), clock, 1);

        Assert.Equal(42, dto.RemainingRuntimeSeconds);
    }

    [Fact]
    public void The_app_fills_in_what_the_device_leaves_blank()
    {
        var clock = new RunClock();
        clock.Started(controllerId: 1, station: 3, minutes: 10);

        // This is the bug: the device says zero because it has no way to say anything
        // else, and the UI showed 0:00 through the whole run.
        var dto = ControllerStateDto.From(StateWith(activeStation: 3, remainingSeconds: 0), clock, 1);

        Assert.InRange(dto.RemainingRuntimeSeconds, 598, 600);
    }

    [Fact]
    public void Nothing_watering_means_no_countdown_invented()
    {
        var clock = new RunClock();
        clock.Started(controllerId: 1, station: 3, minutes: 10);

        var dto = ControllerStateDto.From(StateWith(activeStation: 0, remainingSeconds: 0), clock, 1);

        Assert.Equal(0, dto.RemainingRuntimeSeconds);
    }

    [Fact]
    public void Without_a_clock_the_mapping_is_unchanged()
    {
        var dto = ControllerStateDto.From(StateWith(activeStation: 3, remainingSeconds: 0));
        Assert.Equal(0, dto.RemainingRuntimeSeconds);
    }
}
