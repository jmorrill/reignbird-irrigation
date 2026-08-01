using System.Collections.Concurrent;
using RainBird.Protocol;

namespace RainBird.Server.Services;

/// <summary>
/// Creates transports for controllers. Abstracted so tests and demo mode can
/// substitute the simulator without changing anything else.
/// </summary>
public interface IControllerTransportFactory
{
    IRainBirdTransport Create(string host, string password, bool useHttps);
}

/// <summary>
/// The real thing: HTTP or HTTPS to <c>/stick</c> on the LAN.
///
/// TLS controllers get a client that pins Rain Bird's own certificates, so their
/// self-signed certificate is genuinely verified rather than waved through.
/// </summary>
public sealed class HttpControllerTransportFactory : IControllerTransportFactory
{
    private readonly IHttpClientFactory _httpClientFactory;

    public HttpControllerTransportFactory(IHttpClientFactory httpClientFactory) =>
        _httpClientFactory = httpClientFactory;

    public IRainBirdTransport Create(string host, string password, bool useHttps) =>
        new HttpRainBirdTransport(
            host,
            password,
            _httpClientFactory.CreateClient(useHttps ? "rainbird-tls" : "rainbird"),
            useHttps);
}

/// <summary>
/// A live connection to one controller: the client, its capabilities, the most recent
/// state, and a bounded log of raw SIP traffic for the diagnostics view.
/// </summary>
public sealed class ControllerConnection : IDisposable
{
    private const int MaxLoggedExchanges = 200;

    private readonly ConcurrentQueue<SipExchange> _exchanges = new();
    private readonly IRainBirdTransport _transport;

    public ControllerConnection(int controllerId, string host, IRainBirdTransport transport)
    {
        ControllerId = controllerId;
        Host = host;
        _transport = transport;
        Client = new LnkClient(transport);

        // Every transport we ship can report its traffic; capture it for diagnostics.
        if (transport is ISipExchangeSource source)
            source.OnExchange = Record;
    }

    public int ControllerId { get; }
    public string Host { get; }
    public LnkClient Client { get; }

    public ControllerCapabilities? Capabilities { get; set; }
    public CombinedState? LastState { get; set; }
    public DateTimeOffset? LastSeenUtc { get; set; }
    public string? LastError { get; set; }

    // --------------------------------------------------------- run attribution

    /// <summary>
    /// A run this app started, remembered just long enough for the polling loop to
    /// notice the station and label the history entry correctly.
    /// </summary>
    /// <param name="Station">The station, or null when the command covers any station.</param>
    private sealed record CommandedRun(int? Station, RunTrigger Trigger, DateTimeOffset ExpiresAt);

    private readonly List<CommandedRun> _commanded = [];

    /// <summary>
    /// Records that this app initiated a run, so the history can say how it started.
    /// The protocol never reports this, so a run we did not issue is genuinely unknown
    /// in origin â€” it could be the controller's own schedule or someone at the panel.
    /// </summary>
    /// <param name="station">Null for commands that will run several stations.</param>
    /// <param name="window">
    /// How long the attribution holds. A program or test walks many stations in
    /// sequence, so it has to outlive the first one.
    /// </param>
    public void NoteCommandedRun(int? station, RunTrigger trigger, TimeSpan window)
    {
        lock (_commanded)
        {
            var now = DateTimeOffset.UtcNow;
            _commanded.RemoveAll(c => c.ExpiresAt <= now);
            _commanded.Add(new CommandedRun(station, trigger, now + window));
        }
    }

    /// <summary>How a run on this station most likely started.</summary>
    public RunTrigger TriggerFor(int station)
    {
        lock (_commanded)
        {
            var now = DateTimeOffset.UtcNow;
            _commanded.RemoveAll(c => c.ExpiresAt <= now);

            // Most recent wins: a manual run started during a program is a manual run.
            for (var i = _commanded.Count - 1; i >= 0; i--)
            {
                var commanded = _commanded[i];
                if (commanded.Station is null || commanded.Station == station)
                    return commanded.Trigger;
            }

            return RunTrigger.Unknown;
        }
    }

    public bool IsOnline => LastSeenUtc is { } seen
                            && DateTimeOffset.UtcNow - seen < TimeSpan.FromMinutes(2);

    private void Record(SipExchange exchange)
    {
        _exchanges.Enqueue(exchange);
        while (_exchanges.Count > MaxLoggedExchanges)
            _exchanges.TryDequeue(out _);
    }

    public IReadOnlyList<SipExchange> RecentExchanges() => _exchanges.Reverse().ToList();

    /// <summary>
    /// Releases the transport. Only safe once nothing can still be using this
    /// connection — see <see cref="ControllerRegistry"/> for why replacement does not
    /// call this.
    /// </summary>
    public void Dispose() => (_transport as IDisposable)?.Dispose();
}

/// <summary>
/// Owns the live connections. One per controller, created on demand and reused, which
/// matters because the transport enforces the device's one-request-at-a-time rule.
/// </summary>
public sealed class ControllerRegistry : IDisposable
{
    private readonly ConcurrentDictionary<int, ControllerConnection> _connections = new();
    private readonly IControllerTransportFactory _factory;
    private readonly ILogger<ControllerRegistry> _logger;

    public ControllerRegistry(IControllerTransportFactory factory, ILogger<ControllerRegistry> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public ControllerConnection GetOrCreate(int controllerId, string host, string password, bool useHttps)
    {
        var key = useHttps ? $"https://{host}" : $"http://{host}";

        return _connections.AddOrUpdate(
            controllerId,
            _ =>
            {
                _logger.LogInformation("Opening connection to controller {Id} at {Endpoint}", controllerId, key);
                return new ControllerConnection(controllerId, key, _factory.Create(host, password, useHttps));
            },
            (_, existing) =>
            {
                if (existing.Host == key) return existing;

                // The controller moved, or switched scheme; rebuild against it.
                //
                // The old connection is deliberately *not* disposed. Another caller —
                // the polling loop, most likely — may be mid-request on it, and
                // disposing the transport pulls its serialisation gate out from under
                // that request. Everything the transport holds is either shared and
                // factory-owned (the HttpClient) or cheap and collectable, so letting
                // it fall out of scope is the safe choice.
                _logger.LogInformation(
                    "Controller {Id} moved from {Old} to {New}; reconnecting", controllerId, existing.Host, key);
                return new ControllerConnection(controllerId, key, _factory.Create(host, password, useHttps));
            });
    }

    public ControllerConnection? Find(int controllerId) =>
        _connections.TryGetValue(controllerId, out var connection) ? connection : null;

    public IReadOnlyCollection<ControllerConnection> All => _connections.Values.ToList();

    /// <summary>
    /// Forgets a controller's connection. As with replacement, the connection is not
    /// disposed: an in-flight request on another thread must be allowed to finish.
    /// </summary>
    public void Remove(int controllerId) => _connections.TryRemove(controllerId, out _);

    public void Dispose()
    {
        foreach (var connection in _connections.Values) connection.Dispose();
        _connections.Clear();
    }
}
