using System.Collections.Concurrent;

namespace RainBird.Server.Services;

/// <summary>
/// How long is left on a run this app started.
///
/// Some firmware — including the ESP-ME3 this project was built against — does not
/// answer SIP <c>4C</c>, and none of the individual commands that replace it reports
/// a countdown. On that hardware the controller waters correctly and advances zone by
/// zone, but has no way to say how long is left, so the app showed 0:00 for the whole
/// run: watering visibly happening beside a timer reading zero.
///
/// It does not need the controller to tell it. Every run this app issues is a timed
/// one — a manual run of N minutes, or a plan step of N minutes — so the end is known
/// at the moment the command goes out. This records that, and the state the app
/// reports fills in the gap when the device leaves it blank.
///
/// What it cannot cover is a run somebody started at the controller's own panel.
/// Nothing here knows about those, and nothing can: the countdown stays zero, which
/// is at least honest.
/// </summary>
public sealed class RunClock
{
    private sealed record Expectation(int Station, DateTimeOffset EndsAtUtc);

    private readonly ConcurrentDictionary<int, Expectation> _expected = new();

    /// <summary>Records that a station has just been told to run for a set time.</summary>
    public void Started(int controllerId, int station, int minutes) =>
        Started(controllerId, station, TimeSpan.FromMinutes(minutes));

    public void Started(int controllerId, int station, TimeSpan duration) =>
        _expected[controllerId] = new Expectation(station, DateTimeOffset.UtcNow + duration);

    /// <summary>Forgets any expectation, after a stop or when a plan ends.</summary>
    public void Cleared(int controllerId) => _expected.TryRemove(controllerId, out _);

    /// <summary>
    /// Seconds left on the station the controller says is running, or zero when that
    /// is not something this app can know.
    ///
    /// Checked against the station actually watering, so an expectation left over
    /// from a previous zone is never reported against the current one — the answer
    /// has to be about what is happening now, or it is worse than no answer.
    /// </summary>
    public int RemainingSeconds(int controllerId, int activeStation)
    {
        if (activeStation <= 0) return 0;
        if (!_expected.TryGetValue(controllerId, out var expectation)) return 0;
        if (expectation.Station != activeStation) return 0;

        var remaining = expectation.EndsAtUtc - DateTimeOffset.UtcNow;
        return remaining <= TimeSpan.Zero ? 0 : (int)remaining.TotalSeconds;
    }
}
