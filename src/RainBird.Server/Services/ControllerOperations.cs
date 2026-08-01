using System.Collections.Concurrent;

namespace RainBird.Server.Services;

/// <summary>
/// Serialises whole watering decisions for one controller.
///
/// The transport already serialises individual wire requests, and that is not the
/// same thing. A logical operation — start this plan, advance to the next zone, stop
/// everything — is several requests and a database write, and nothing stopped two of
/// them interleaving.
///
/// The case that matters: the engine decides to advance, and while its run command
/// is in flight the user presses stop. Cancellation stops the valve and clears the
/// tracker, then the advance's command arrives and opens the next zone. Nothing is
/// tracking it any more, so nothing will ever close it early — it waters for its
/// full duration, minutes after the app said watering had stopped. The transport
/// gate cannot prevent that, because both requests were individually well-behaved.
///
/// One lock per controller, held across the whole transition. Different controllers
/// never wait on each other; the device is the resource, not the process.
/// </summary>
public sealed class ControllerOperations
{
    /// <summary>
    /// A ceiling, not an expectation. Waiting forever would turn one wedged operation
    /// into an app that never waters again, and the alternative — proceeding without
    /// the lock — is the race this exists to prevent. Long enough to outlast the
    /// transport's own retries.
    /// </summary>
    private static readonly TimeSpan AcquireTimeout = TimeSpan.FromSeconds(45);

    private readonly ConcurrentDictionary<int, SemaphoreSlim> _gates = new();
    private readonly ILogger<ControllerOperations> _logger;

    public ControllerOperations(ILogger<ControllerOperations> logger) => _logger = logger;

    /// <summary>
    /// Runs an operation with exclusive use of the controller.
    ///
    /// The lock is per controller and outlives any individual connection, so
    /// replacing a transport — which a re-probe does — cannot let an old and a new one
    /// overlap on the same device.
    /// </summary>
    public async Task<T> ExclusivelyAsync<T>(
        int controllerId, Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
    {
        var gate = _gates.GetOrAdd(controllerId, _ => new SemaphoreSlim(1, 1));

        if (!await gate.WaitAsync(AcquireTimeout, ct).ConfigureAwait(false))
        {
            throw new TimeoutException(
                $"Controller {controllerId} was busy with another operation for "
                + $"{AcquireTimeout.TotalSeconds:0} seconds.");
        }

        try
        {
            return await operation(ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ExclusivelyAsync(
        int controllerId, Func<CancellationToken, Task> operation, CancellationToken ct = default) =>
        await ExclusivelyAsync<bool>(controllerId, async token =>
        {
            await operation(token).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);

    /// <summary>
    /// Runs an operation only if the controller is idle, and says so rather than
    /// waiting when it is not.
    ///
    /// For the polling loop and the scheduler tick: both run on a timer, and a tick
    /// that queues up behind a long operation would arrive late and act on a picture
    /// that had already changed. Skipping is the right answer — another tick is along
    /// shortly.
    /// </summary>
    public async Task<bool> TryExclusivelyAsync(
        int controllerId, Func<CancellationToken, Task> operation, CancellationToken ct = default)
    {
        var gate = _gates.GetOrAdd(controllerId, _ => new SemaphoreSlim(1, 1));

        if (!await gate.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
        {
            _logger.LogDebug("Controller {Id} is busy; skipping this tick", controllerId);
            return false;
        }

        try
        {
            await operation(ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }
}
