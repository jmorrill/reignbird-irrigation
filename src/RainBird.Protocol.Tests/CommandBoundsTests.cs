using RainBird.Protocol;

namespace RainBird.Protocol.Tests;

/// <summary>
/// Bounds on the commands that open valves.
///
/// The duration travels as a single byte. Masking an out-of-range value to fit meant
/// a request for -1 minutes became a request for 255 — a valve open for four and a
/// quarter hours in answer to nonsense.
/// </summary>
public class CommandBoundsTests
{
    private static LnkClient Client() => new(new FakeTransport());

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(256)]
    [InlineData(int.MinValue)]
    public async Task Queueing_a_station_refuses_a_duration_it_cannot_send(int minutes)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Client().StackStationAsync(1, minutes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(256)]
    public async Task Running_a_station_refuses_the_same(int minutes)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Client().RunStationAsync(1, minutes));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(255)]
    public async Task The_whole_range_a_byte_can_carry_is_allowed(int minutes)
    {
        // Not throwing is the assertion; the fake transport answers everything.
        await Client().StackStationAsync(1, minutes);
        await Client().RunStationAsync(1, minutes);
    }

    private sealed class FakeTransport : IRainBirdTransport
    {
        public Task<System.Text.Json.Nodes.JsonObject> SendAsync(
            string method, System.Text.Json.Nodes.JsonObject parameters, CancellationToken ct = default) =>
            Task.FromResult(new System.Text.Json.Nodes.JsonObject
            {
                // An ACK for whatever was asked.
                ["data"] = "0100",
            });
    }
}
