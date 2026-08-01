using RainBird.Protocol;
using RainBird.Server.Services;
using RainBird.Simulator;

namespace RainBird.Server.Tests;

/// <summary>
/// Caching the controller's own programs.
///
/// Reading them costs a SIP exchange per program on a device that answers one request
/// at a time, which made it comfortably the slowest request the app issued — and every
/// screen load waited on it. Caching is worth it, but it introduces the one failure
/// that matters: showing somebody the program they just replaced. These tests pin the
/// invalidation, not the speed.
/// </summary>
public class ProgramCacheTests
{
    private static ControllerConnection NewConnection() =>
        new(1, "192.168.1.50", new SimulatorTransport(new VirtualController(stationCount: 8), "simulator"));

    private static IReadOnlyList<ProgramSchedule> Programs(int seasonalAdjust) =>
    [
        new ProgramSchedule
        {
            ProgramNumber = 0,
            Frequency = FrequencyType.CustomDays,
            CustomDays = [true, false, true, false, true, false, false],
            CyclicDays = 0,
            DaysRemaining = 0,
            SeasonalAdjustPercent = seasonalAdjust,
            StartTimes = [315],
            StationRunTimes = new Dictionary<int, int> { [1] = 20 },
        },
    ];

    [Fact]
    public void Nothing_is_cached_before_a_read()
    {
        using var connection = NewConnection();

        Assert.False(connection.TryGetFreshPrograms(TimeSpan.FromMinutes(5), out _));
    }

    [Fact]
    public void A_recent_read_is_served_from_the_cache()
    {
        using var connection = NewConnection();
        connection.RememberPrograms(Programs(100));

        Assert.True(connection.TryGetFreshPrograms(TimeSpan.FromMinutes(5), out var programs));
        Assert.Equal(100, Assert.Single(programs).SeasonalAdjustPercent);
    }

    [Fact]
    public void A_read_older_than_the_freshness_window_is_not_reused()
    {
        using var connection = NewConnection();
        connection.RememberPrograms(Programs(100));

        // Nothing read in the past zero seconds, however recently it happened.
        Assert.False(connection.TryGetFreshPrograms(TimeSpan.Zero, out _));
    }

    /// <summary>
    /// The one that matters. Saving a program must send the next reader to the
    /// controller, or the user is shown the values they just overwrote.
    /// </summary>
    [Fact]
    public void Saving_a_program_drops_the_cache()
    {
        using var connection = NewConnection();
        connection.RememberPrograms(Programs(100));

        connection.InvalidatePrograms();

        Assert.False(connection.TryGetFreshPrograms(TimeSpan.FromMinutes(5), out _));
    }

    [Fact]
    public void A_read_after_an_edit_caches_the_new_values()
    {
        using var connection = NewConnection();
        connection.RememberPrograms(Programs(100));
        connection.InvalidatePrograms();

        connection.RememberPrograms(Programs(80));

        Assert.True(connection.TryGetFreshPrograms(TimeSpan.FromMinutes(5), out var programs));
        Assert.Equal(80, Assert.Single(programs).SeasonalAdjustPercent);
    }
}
