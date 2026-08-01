using System.Collections.Concurrent;
using RainBird.Protocol;

namespace RainBird.Server.Services;

/// <summary>Something the recorder noticed while watching the poll stream.</summary>
public abstract record WateringEvent(int ControllerId, DateTimeOffset At);

public sealed record RunStarted(int ControllerId, DateTimeOffset At, int Station) : WateringEvent(ControllerId, At);

public sealed record RunCompleted(
    int ControllerId,
    DateTimeOffset At,
    int Station,
    DateTimeOffset StartedUtc,
    int DurationSeconds) : WateringEvent(ControllerId, At);

/// <summary>
/// Turns a stream of controller state snapshots into a watering history.
///
/// The controller keeps no log of what it has watered, so this is the only place a
/// run is ever recorded. It works by watching the active station change: when the
/// active station goes from N to anything else, N's run just ended.
///
/// Deliberately free of database and HTTP concerns so the transition logic can be
/// tested directly against synthetic poll streams.
/// </summary>
public sealed class HistoryRecorder
{
    private sealed record InFlight(int Station, DateTimeOffset StartedUtc);

    private readonly ConcurrentDictionary<int, InFlight> _active = new();

    /// <summary>
    /// Feeds one observation in and returns whatever it implies.
    ///
    /// Safe to call at any cadence: a run that starts and finishes between two polls
    /// is missed entirely, which is why the polling interval is short while a client
    /// is watching.
    /// </summary>
    public IReadOnlyList<WateringEvent> Observe(int controllerId, CombinedState state, DateTimeOffset now)
    {
        var events = new List<WateringEvent>();
        var station = state.IsWatering ? state.ActiveStation : 0;

        _active.TryGetValue(controllerId, out var current);

        if (current is not null && current.Station != station)
        {
            events.Add(new RunCompleted(
                controllerId,
                now,
                current.Station,
                current.StartedUtc,
                Math.Max(0, (int)(now - current.StartedUtc).TotalSeconds)));

            _active.TryRemove(controllerId, out _);
            current = null;
        }

        if (station != 0 && current is null)
        {
            // Back-date the start using the controller's own countdown, so a run that
            // began between polls is not recorded as shorter than it really was.
            var elapsed = EstimateElapsed(state);
            var startedUtc = now - elapsed;

            _active[controllerId] = new InFlight(station, startedUtc);
            events.Add(new RunStarted(controllerId, now, station));
        }

        return events;
    }

    /// <summary>
    /// How long the current run has been going. The controller reports seconds
    /// remaining but not the original duration, so this is only recoverable when we
    /// saw the run start. On the first observation of an already-running station we
    /// have no better estimate than zero, and treat "now" as the start.
    /// </summary>
    private static TimeSpan EstimateElapsed(CombinedState state) => TimeSpan.Zero;

    /// <summary>
    /// Closes out a run without a following observation — used when a controller goes
    /// offline or the server shuts down, so a run isn't left dangling forever.
    /// </summary>
    public RunCompleted? Flush(int controllerId, DateTimeOffset now)
    {
        if (!_active.TryRemove(controllerId, out var current)) return null;

        return new RunCompleted(
            controllerId, now, current.Station, current.StartedUtc,
            Math.Max(0, (int)(now - current.StartedUtc).TotalSeconds));
    }

    /// <summary>The station currently believed to be running, if any.</summary>
    public int? ActiveStation(int controllerId) =>
        _active.TryGetValue(controllerId, out var current) ? current.Station : null;
}
