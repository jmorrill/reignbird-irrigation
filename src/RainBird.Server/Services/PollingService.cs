using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RainBird.Protocol;
using RainBird.Server.Data;
using RainBird.Server.Hubs;

namespace RainBird.Server.Services;

/// <summary>
/// Polls every known controller, pushes state to connected clients, and turns the
/// observed transitions into watering history.
///
/// The cadence adapts: fast while a browser is watching, slow otherwise. This is a
/// small embedded device on the end of a WiFi link, and there is nothing to gain from
/// polling it every few seconds when nobody is looking.
/// </summary>
public sealed class PollingService : BackgroundService
{
    private static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ControllerRegistry _registry;
    private readonly HistoryRecorder _recorder;
    private readonly IHubContext<ControllerHub> _hub;
    private readonly ILogger<PollingService> _logger;

    public PollingService(
        IServiceScopeFactory scopeFactory,
        ControllerRegistry registry,
        HistoryRecorder recorder,
        IHubContext<ControllerHub> hub,
        ILogger<PollingService> logger)
    {
        _scopeFactory = scopeFactory;
        _registry = registry;
        _recorder = recorder;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish starting before the first poll.
        try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failure here must never take the loop down; the next pass retries.
                _logger.LogError(ex, "Polling pass failed");
            }

            var interval = ControllerHub.HasListeners ? ActiveInterval : IdleInterval;
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        await FlushOpenRunsAsync();
    }

    private async Task PollAllAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var controllers = scope.ServiceProvider.GetRequiredService<ControllerService>();
        var alerts = scope.ServiceProvider.GetRequiredService<AlertService>();

        var records = await db.Controllers.AsNoTracking().ToListAsync(ct);

        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            await PollOneAsync(db, controllers, record, ct, alerts);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// When each controller was last heard from, and whether we have already said it
    /// went quiet.
    ///
    /// A single missed poll is ordinary — the device is small and drops requests when
    /// busy. Alerting on that would cry wolf several times a day, so nothing is said
    /// until it has been silent for a good while.
    /// </summary>
    private static readonly TimeSpan SilenceBeforeAlerting = TimeSpan.FromMinutes(15);

    private readonly Dictionary<int, DateTimeOffset> _lastSeen = [];
    private readonly HashSet<int> _reportedOffline = [];

    private async Task PollOneAsync(
        AppDbContext db, ControllerService controllers, ControllerRecord record, CancellationToken ct,
        AlertService? alerts = null)
    {
        ControllerConnection connection;
        try
        {
            connection = controllers.Connect(record);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Cannot connect to controller {Id}", record.Id);
            return;
        }

        try
        {
            var state = await connection.Client.GetCombinedStateAsync(ct);
            var now = DateTimeOffset.UtcNow;

            connection.LastState = state;
            connection.LastSeenUtc = now;
            connection.LastError = null;

            var events = _recorder.Observe(record.Id, state, now);
            await PersistAsync(db, record.Id, events, ct);

            await _hub.Clients
                .Group(ControllerHub.GroupFor(record.Id))
                .SendAsync("stateChanged", new { controllerId = record.Id, state, online = true }, ct);

            foreach (var wateringEvent in events)
                await NotifyAsync(record.Id, wateringEvent, ct);

            _lastSeen[record.Id] = now;

            if (_reportedOffline.Remove(record.Id) && alerts is not null)
            {
                await alerts.RaiseAsync(
                    AlertKind.ControllerRecovered,
                    AlertSeverity.Info,
                    $"{record.Name} is back",
                    "The controller is responding again.",
                    record.Id, ct);
            }
        }
        catch (Exception ex) when (ex is RainBirdProtocolException or HttpRequestException or TaskCanceledException)
        {
            connection.LastError = ex.Message;
            _logger.LogDebug(ex, "Controller {Id} did not respond", record.Id);

            await _hub.Clients
                .Group(ControllerHub.GroupFor(record.Id))
                .SendAsync("stateChanged", new { controllerId = record.Id, state = (object?)null, online = false }, ct);

            await MaybeAlertOfflineAsync(record, alerts, ct);
        }
    }

    /// <summary>
    /// Says something once the silence has gone on long enough to matter.
    ///
    /// Said once per outage, not once per poll — the alert is that the controller
    /// stopped answering, and repeating it every few seconds for a device that is
    /// unplugged for the winter would be worse than saying nothing.
    /// </summary>
    private async Task MaybeAlertOfflineAsync(
        ControllerRecord record, AlertService? alerts, CancellationToken ct)
    {
        if (alerts is null || _reportedOffline.Contains(record.Id)) return;

        // Never seen this controller answer since startup: start the clock now rather
        // than firing immediately, so a restart while it is down stays quiet for the
        // same grace period as anything else.
        if (!_lastSeen.TryGetValue(record.Id, out var seen))
        {
            _lastSeen[record.Id] = DateTimeOffset.UtcNow;
            return;
        }

        if (DateTimeOffset.UtcNow - seen < SilenceBeforeAlerting) return;

        _reportedOffline.Add(record.Id);

        await alerts.RaiseAsync(
            AlertKind.ControllerOffline,
            AlertSeverity.Problem,
            $"{record.Name} is not responding",
            $"No answer for {SilenceBeforeAlerting.TotalMinutes:0} minutes. Scheduled watering will not run while it is unreachable.",
            record.Id, ct);
    }

    private async Task PersistAsync(
        AppDbContext db, int controllerId, IReadOnlyList<WateringEvent> events, CancellationToken ct)
    {
        foreach (var wateringEvent in events)
        {
            if (wateringEvent is not RunCompleted completed) continue;

            // A run shorter than this is almost certainly the tail of a zone advancing,
            // not real watering; recording it would clutter the history.
            if (completed.DurationSeconds < 5) continue;

            var zone = await db.Zones
                .AsNoTracking()
                .FirstOrDefaultAsync(z => z.ControllerId == controllerId && z.StationNumber == completed.Station, ct);

            db.Runs.Add(new RunRecord
            {
                ControllerId = controllerId,
                StationNumber = completed.Station,
                StartedUtc = completed.StartedUtc,
                EndedUtc = completed.At,
                DurationSeconds = completed.DurationSeconds,
                // Unknown unless this app issued the command; the protocol never says.
                Trigger = _registry.Find(controllerId)?.TriggerFor(completed.Station) ?? RunTrigger.Unknown,
                EstimatedGallons = UsageEstimator.Gallons(completed.DurationSeconds, zone?.NozzleFlowGpm ?? 1.5),
            });
        }
    }

    private async Task NotifyAsync(int controllerId, WateringEvent wateringEvent, CancellationToken ct)
    {
        var group = _hub.Clients.Group(ControllerHub.GroupFor(controllerId));

        switch (wateringEvent)
        {
            case RunStarted started:
                await group.SendAsync("runStarted", new { controllerId, station = started.Station }, ct);
                break;
            case RunCompleted completed:
                await group.SendAsync("runCompleted", new
                {
                    controllerId,
                    station = completed.Station,
                    durationSeconds = completed.DurationSeconds,
                }, ct);
                break;
        }
    }

    /// <summary>
    /// On shutdown, close out anything still marked running so it isn't stranded as an
    /// open run forever.
    /// </summary>
    private async Task FlushOpenRunsAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTimeOffset.UtcNow;

            foreach (var connection in _registry.All)
            {
                if (_recorder.Flush(connection.ControllerId, now) is not { } completed) continue;
                if (completed.DurationSeconds < 5) continue;

                db.Runs.Add(new RunRecord
                {
                    ControllerId = completed.ControllerId,
                    StationNumber = completed.Station,
                    StartedUtc = completed.StartedUtc,
                    EndedUtc = completed.At,
                    DurationSeconds = completed.DurationSeconds,
                    Trigger = connection.TriggerFor(completed.Station),
                    EstimatedGallons = UsageEstimator.Gallons(completed.DurationSeconds, 1.5),
                });
            }

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not flush open runs during shutdown");
        }
    }
}

/// <summary>
/// Estimates water usage from run time and nozzle flow rate.
///
/// This is an estimate and the UI says so. Rain Bird residential controllers have no
/// flow meter, so the only honest thing to do is derive it from a rate the user
/// configured per zone.
/// </summary>
public static class UsageEstimator
{
    public static double Gallons(int durationSeconds, double nozzleFlowGpm) =>
        Math.Round(durationSeconds / 60.0 * nozzleFlowGpm, 2);

    public static double Litres(int durationSeconds, double nozzleFlowGpm) =>
        Math.Round(Gallons(durationSeconds, nozzleFlowGpm) * 3.785411784, 2);
}
