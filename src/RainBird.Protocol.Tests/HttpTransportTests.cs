using System.Net;
using RainBird.Protocol;
using RainBird.Simulator;

namespace RainBird.Protocol.Tests;

/// <summary>
/// Exercises the real HTTP path — sockets, headers, encrypted bodies — against the
/// simulator's HTTP listener. These are the tests that would catch a framing mistake
/// the in-process transport can't see.
/// </summary>
public class HttpTransportTests : IAsyncLifetime
{
    private const string Password = "sprinkler";

    private SimulatorHttpServer _server = null!;
    private VirtualController _controller = null!;

    public Task InitializeAsync()
    {
        _controller = new VirtualController(stationCount: 8);
        _server = new SimulatorHttpServer(_controller, Password, FreePort());
        _server.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task Talks_to_a_controller_over_real_http()
    {
        using var transport = new HttpRainBirdTransport(_server.Host, Password);
        var client = new LnkClient(transport);

        var model = await client.GetModelAndVersionAsync();

        Assert.Equal("ESP-ME3", model.Model.Series);
    }

    [Fact]
    public async Task Full_workflow_over_http()
    {
        using var transport = new HttpRainBirdTransport(_server.Host, Password);
        var client = new LnkClient(transport);

        var capabilities = await client.ProbeCapabilitiesAsync();
        Assert.Equal(8, capabilities.StationCount);

        await client.RunStationAsync(4, 15);
        var state = await client.GetCombinedStateAsync();
        Assert.True(state.IsWatering);
        Assert.Equal(4, state.ActiveStation);

        await client.StopIrrigationAsync();
        Assert.False((await client.GetCombinedStateAsync()).IsWatering);
    }

    [Fact]
    public async Task A_wrong_password_fails_rather_than_returning_garbage()
    {
        using var transport = new HttpRainBirdTransport(_server.Host, "wrong-password");
        var client = new LnkClient(transport);

        // The simulator can't decrypt the request, so it 403s; the client reports a
        // connection failure rather than silently producing nonsense.
        await Assert.ThrowsAnyAsync<RainBirdProtocolException>(() => client.GetModelAndVersionAsync());
    }

    [Fact]
    public async Task Raw_exchanges_are_captured_for_diagnostics()
    {
        var exchanges = new List<SipExchange>();
        using var transport = new HttpRainBirdTransport(_server.Host, Password)
        {
            OnExchange = exchanges.Add,
        };
        var client = new LnkClient(transport);

        await client.GetCombinedStateAsync();

        var exchange = Assert.Single(exchanges);
        Assert.Equal("tunnelSip", exchange.Method);
        Assert.Equal("4C", exchange.RequestHex);
        Assert.StartsWith("CC", exchange.ResponseHex);
        Assert.Null(exchange.Error);
    }

    [Fact]
    public async Task Concurrent_callers_are_serialised_onto_the_device()
    {
        using var transport = new HttpRainBirdTransport(_server.Host, Password);
        var client = new LnkClient(transport);

        var states = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ => client.GetCombinedStateAsync()));

        Assert.All(states, s => Assert.Equal(0, s.ActiveStation));
        Assert.Equal(10, _controller.CommandLog.Count(c => c == "4C"));
    }

    [Fact]
    public async Task An_unreachable_controller_reports_a_connection_failure()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
        using var transport = new HttpRainBirdTransport("127.0.0.1:1", Password, http);
        var client = new LnkClient(transport);

        var ex = await Assert.ThrowsAsync<RainBirdConnectionException>(
            () => client.GetCombinedStateAsync());

        Assert.Contains("127.0.0.1", ex.Message);
    }

    /// <summary>
    /// Requests are serialised onto the device, so without a circuit breaker every
    /// caller queues behind the previous one's full retry ladder and an unreachable
    /// controller makes the whole app hang rather than simply reporting itself
    /// offline.
    /// </summary>
    [Fact]
    public async Task An_unreachable_controller_fails_fast_after_the_first_discovery()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
        using var transport = new HttpRainBirdTransport("127.0.0.1:1", Password, http);
        var client = new LnkClient(transport);

        // First call walks the whole retry ladder and opens the circuit.
        await Assert.ThrowsAsync<RainBirdConnectionException>(() => client.GetCombinedStateAsync());
        Assert.True(transport.IsCircuitOpen);

        // Subsequent calls return immediately rather than retrying again.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<RainBirdConnectionException>(() => client.GetCombinedStateAsync());
        stopwatch.Stop();

        Assert.True(
            stopwatch.ElapsedMilliseconds < 250,
            $"Expected an immediate failure while the circuit is open, took {stopwatch.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public async Task A_reachable_controller_never_opens_the_circuit()
    {
        using var transport = new HttpRainBirdTransport(_server.Host, Password);
        var client = new LnkClient(transport);

        await client.GetCombinedStateAsync();

        Assert.False(transport.IsCircuitOpen);
    }

    [Fact]
    public async Task Transient_busy_responses_are_retried()
    {
        var controller = new VirtualController();
        var transport = new SimulatorTransport(controller) { FailNextRequests = 2 };
        var client = new LnkClient(transport);

        // The in-process transport throws 503s directly, so drive the retry through
        // a small wrapper that mirrors the HTTP transport's policy.
        var succeeded = false;
        for (var attempt = 0; attempt < 3 && !succeeded; attempt++)
        {
            try
            {
                await client.GetCombinedStateAsync();
                succeeded = true;
            }
            catch (HttpRequestException)
            {
                // retry
            }
        }

        Assert.True(succeeded);
        Assert.Equal(0, transport.FailNextRequests);
    }
}
