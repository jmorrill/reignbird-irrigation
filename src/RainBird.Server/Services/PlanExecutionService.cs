using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RainBird.Protocol;
using RainBird.Server.Data;
using RainBird.Server.Hubs;

namespace RainBird.Server.Services;

/// <summary>
/// Executes watering plans: decides when a plan should start, and drives the
/// controller zone by zone through it.
///
/// <para><b>Every zone run is bounded by the controller's own timer.</b> A step is
/// issued as a manual run of exactly its length, so if this server stops — crash,
/// restart, power cut — the valve still closes on schedule. Nothing here can leave
/// water running. That is the single most important property of this class, and the
/// reason it never issues an open-ended run.</para>
///
/// <para>The engine only advances the queue. It does not hold valves open.</para>
/// </summary>
public sealed class PlanExecutionService : BackgroundService
{
    /// <summary>How often to re-examine the world.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A start time is only picked up within this window of its scheduled minute, so
    /// a server that was off all morning does not fire a stale 6am pass at noon.
    /// </summary>
    private static readonly TimeSpan StartWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Grace beyond a step's own length before the engine gives up waiting for the
    /// controller and moves on.
    /// </summary>
    private static readonly TimeSpan StepGrace = TimeSpan.FromSeconds(45);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ControllerRegistry _registry;
    private readonly IHubContext<ControllerHub> _hub;
    private readonly ILogger<PlanExecutionService> _logger;
    private readonly PlanRunTracker _tracker;
    private readonly RunClock _clock;
    private readonly ControllerOperations _operations;

    public PlanExecutionService(
        IServiceScopeFactory scopeFactory,
        ControllerRegistry registry,
        IHubContext<ControllerHub> hub,
        PlanRunTracker tracker,
        RunClock clock,
        ControllerOperations operations,
        ILogger<PlanExecutionService> logger)
    {
        _scopeFactory = scopeFactory;
        _registry = registry;
        _hub = hub;
        _tracker = tracker;
        _clock = clock;
        _operations = operations;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
        catch (OperationCanceledException) { return; }

        // Before the first tick: a run left Running by a restart owns nothing and will
        // never advance, so it has to be closed out rather than sit there for ever.
        try { await ReconcileInterruptedRunsAsync(stoppingToken); }
        catch (Exception ex) { _logger.LogError(ex, "Could not reconcile interrupted plan runs"); }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let the scheduler die; the next tick tries again.
                _logger.LogError(ex, "Plan execution tick failed");
            }

            try { await Task.Delay(Tick, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Settles plan runs left mid-flight by a restart.
    ///
    /// Ownership of a running plan lives in memory, so a restart loses it while the
    /// database row stays `Running` for ever and the remaining zones never water. The
    /// entity comment claimed a restart was "recoverable", which was not true of the
    /// code — this makes it at least honest, and tells somebody.
    ///
    /// Resuming is deliberately not attempted. The step that was in flight is bounded
    /// by the controller's own timer and has long since closed; picking the queue back
    /// up would mean guessing how much of it happened, and guessing wrong means
    /// watering a zone twice. Marking it failed and saying so is the answer that
    /// cannot be wrong.
    /// </summary>
    private async Task ReconcileInterruptedRunsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var alerts = scope.ServiceProvider.GetRequiredService<AlertService>();

        var interrupted = await db.PlanRuns
            .Where(run => run.Status == PlanRunStatus.Running)
            .ToListAsync(ct);

        if (interrupted.Count == 0) return;

        foreach (var run in interrupted)
        {
            run.Status = PlanRunStatus.Failed;
            run.EndedUtc = DateTimeOffset.UtcNow;
            run.Detail = "Interrupted — the server restarted while this plan was running.";
        }

        await db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "Marked {Count} plan run(s) as interrupted; they were still Running at startup",
            interrupted.Count);

        foreach (var run in interrupted)
        {
            await alerts.RaiseAsync(
                AlertKind.PlanFailed,
                AlertSeverity.Problem,
                $"{run.PlanName} was interrupted",
                "The server restarted part way through this plan. The zone that was running "
                + "stopped on the controller's own timer, but the rest did not water.",
                run.ControllerId, ct);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var controllers = scope.ServiceProvider.GetRequiredService<ControllerService>();
        var alerts = scope.ServiceProvider.GetRequiredService<AlertService>();

        foreach (var record in await db.Controllers.ToListAsync(ct))
        {
            // Skipped rather than queued when something else holds the controller —
            // a cancellation, or a run started by hand. Another tick is along in a
            // moment, and acting on a decision made before that operation finished is
            // exactly how a cancelled plan used to open one more zone.
            await _operations.TryExclusivelyAsync(record.Id, async token =>
            {
                if (_tracker.TryGet(record.Id, out var active))
                    await AdvanceAsync(db, controllers, record, active!, token, alerts);
                else
                    await MaybeStartAsync(db, controllers, record, token, alerts);
            }, ct);
        }
    }

    // ------------------------------------------------------------- starting

    private async Task MaybeStartAsync(
        AppDbContext db, ControllerService controllers, ControllerRecord record, CancellationToken ct,
        AlertService? alerts = null)
    {
        var plans = await db.WateringPlans
            .Include(plan => plan.Zones)
            .Where(plan => plan.ControllerId == record.Id && plan.Enabled)
            .OrderBy(plan => plan.SortOrder)
            .ToListAsync(ct);

        if (plans.Count == 0) return;

        var offset = ZoneOffset(record.TimeZoneId);
        var localNow = DateTimeOffset.UtcNow.ToOffset(offset);
        var today = DateOnly.FromDateTime(localNow.DateTime);
        var nowMinute = localNow.Hour * 60 + localNow.Minute;

        foreach (var plan in plans)
        {
            foreach (var startMinute in PlanCompiler.StartTimesOn(plan, today))
            {
                var minutesLate = nowMinute - startMinute;
                if (minutesLate < 0 || minutesLate > StartWindow.TotalMinutes) continue;

                var alreadyRan = await db.PlanRuns.AnyAsync(
                    run => run.WateringPlanId == plan.Id
                           && run.ScheduledDate == today
                           && run.ScheduledStartMinute == startMinute, ct);

                if (alreadyRan) continue;

                await StartAsync(db, controllers, record, plan, today, startMinute, ct, alerts);
                return;
            }
        }
    }

    /// <summary>
    /// Begins a plan run. Also used for "run now", which records a run against the
    /// current minute so the scheduled pass is not double-fired.
    /// </summary>
    /// <summary>
    /// Starts a plan on demand, taking the controller's operation lock.
    ///
    /// Separate from <see cref="StartAsync"/> because the scheduler tick calls that
    /// one already holding the lock, and the lock is not reentrant. Anything reached
    /// from an HTTP handler has to come through here, or two "run now" presses can
    /// both persist a run and one tracker entry silently replaces the other.
    /// </summary>
    public Task<PlanRun?> StartNowAsync(
        AppDbContext db,
        ControllerService controllers,
        ControllerRecord record,
        WateringPlan plan,
        DateOnly scheduledDate,
        int scheduledStartMinute,
        CancellationToken ct = default,
        AlertService? alerts = null) =>
        _operations.ExclusivelyAsync(
            record.Id,
            token => StartAsync(db, controllers, record, plan, scheduledDate, scheduledStartMinute, token, alerts),
            ct);

    public async Task<PlanRun?> StartAsync(
        AppDbContext db,
        ControllerService controllers,
        ControllerRecord record,
        WateringPlan plan,
        DateOnly scheduledDate,
        int scheduledStartMinute,
        CancellationToken ct,
        AlertService? alerts = null)
    {
        // Read at the moment of running, not when the plan was written. A zone
        // switched off since — or automatically disabled because its station stopped
        // being reported — must not water just because a plan still lists it.
        var runnable = await db.Zones
            .Where(zone => zone.ControllerId == record.Id && zone.Enabled)
            .Select(zone => zone.StationNumber)
            .ToListAsync(ct);

        var steps = PlanCompiler.Compile(plan, plan.Zones, runnable.ToHashSet());
        if (steps.Count == 0)
        {
            _logger.LogInformation(
                "Plan {Plan} has nothing to water — every zone is disabled or set to zero; skipping",
                plan.Name);
            return null;
        }

        var run = new PlanRun
        {
            ControllerId = record.Id,
            WateringPlanId = plan.Id,
            PlanName = plan.Name,
            ScheduledDate = scheduledDate,
            ScheduledStartMinute = scheduledStartMinute,
            StartedUtc = DateTimeOffset.UtcNow,
            Status = PlanRunStatus.Running,
        };

        for (var i = 0; i < steps.Count; i++)
        {
            run.Steps.Add(new PlanRunStep
            {
                Ordinal = i,
                StationNumber = steps[i].StationNumber ?? 0,
                Cycle = steps[i].Cycle,
                Minutes = steps[i].Minutes,
            });
        }

        // A plan the weather says to skip is recorded rather than silently dropped, so
        // the history explains why nothing watered.
        if (plan.WeatherSkipEnabled)
        {
            var skip = await db.SkipEvents.FirstOrDefaultAsync(
                s => s.ControllerId == record.Id && s.Date == scheduledDate, ct);

            if (skip is not null)
            {
                run.Status = PlanRunStatus.Skipped;
                run.EndedUtc = DateTimeOffset.UtcNow;
                run.Detail = $"Skipped — {skip.Reason.ToString().ToLowerInvariant()}. {skip.Details}";
                db.PlanRuns.Add(run);
                await db.SaveChangesAsync(ct);
                await NotifyAsync(record.Id, "planSkipped", run, ct);
                return run;
            }
        }

        db.PlanRuns.Add(run);
        await db.SaveChangesAsync(ct);

        _tracker.Set(record.Id, new ActivePlanRun(run.Id, plan.Id, plan.Name, steps, -1, DateTimeOffset.UtcNow));
        _logger.LogInformation(
            "Starting plan {Plan} on controller {Id}: {Steps} steps, {Minutes} min of watering",
            plan.Name, record.Id, steps.Count, PlanCompiler.WateringMinutes(steps));

        await NotifyAsync(record.Id, "planStarted", run, ct);
        await AdvanceAsync(db, controllers, record, _tracker.Get(record.Id)!, ct, alerts);
        return run;
    }

    // ------------------------------------------------------------ advancing

    private async Task AdvanceAsync(
        AppDbContext db,
        ControllerService controllers,
        ControllerRecord record,
        ActivePlanRun active,
        CancellationToken ct,
        AlertService? alerts = null)
    {
        var now = DateTimeOffset.UtcNow;

        // Still within the current step's time? Nothing to do.
        if (active.StepIndex >= 0)
        {
            var current = active.Steps[active.StepIndex];
            var due = active.StepStartedUtc.AddMinutes(current.Minutes);
            if (now < due) return;

            // Watering steps get a little grace: the controller's own countdown is
            // authoritative and can lag ours by a few seconds.
            if (!current.IsSoak && now < due + StepGrace)
            {
                var connection = _registry.Find(record.Id);
                if (connection?.LastState?.ActiveStation == current.StationNumber) return;
            }

            await CompleteStepAsync(db, active, active.StepIndex, PlanStepStatus.Completed, ct);
        }

        var nextIndex = active.StepIndex + 1;

        if (nextIndex >= active.Steps.Count)
        {
            await FinishAsync(db, record, active, PlanRunStatus.Completed, null, ct, alerts);
            return;
        }

        var next = active.Steps[nextIndex];
        _tracker.Set(record.Id, active with { StepIndex = nextIndex, StepStartedUtc = now });

        if (next.IsSoak)
        {
            await MarkStepAsync(db, active.RunId, nextIndex, PlanStepStatus.Running, now, ct);
            return;
        }

        try
        {
            var connection = controllers.Connect(record);

            // Bounded by the controller's own timer: if this server disappears, the
            // valve still closes. The engine advances the queue; it never holds a
            // valve open.
            // No clamp here. PlanCompiler already bounds every step to what a run
            // command can express, so this is the same number the queue was built
            // from — which is what the countdown and the advance both key off.
            var minutes = next.Minutes;
            await connection.Client.RunStationAsync(next.StationNumber!.Value, minutes, ct);
            _clock.Started(record.Id, next.StationNumber!.Value, minutes);
            connection.NoteCommandedRun(next.StationNumber, RunTrigger.Program, TimeSpan.FromMinutes(minutes + 2));

            await MarkStepAsync(db, active.RunId, nextIndex, PlanStepStatus.Running, now, ct);
            await NotifyStepAsync(record.Id, active, nextIndex, ct);
        }
        catch (Exception ex) when (ex is RainBirdProtocolException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Plan {Plan} could not start zone {Station}", active.PlanName, next.StationNumber);
            await MarkStepAsync(db, active.RunId, nextIndex, PlanStepStatus.Failed, now, ct);

            // One zone failing should not abandon the rest of the plan; the next tick
            // moves on to the following step.
            _tracker.Set(record.Id, active with { StepIndex = nextIndex, StepStartedUtc = now.AddMinutes(-next.Minutes) });
        }
    }

    private async Task CompleteStepAsync(
        AppDbContext db, ActivePlanRun active, int index, PlanStepStatus status, CancellationToken ct)
    {
        var step = await db.PlanRunSteps.FirstOrDefaultAsync(
            s => s.PlanRunId == active.RunId && s.Ordinal == index, ct);

        if (step is null) return;

        // Only a step that actually ran becomes Completed. A step that failed to start
        // keeps saying so — overwriting it here would make a plan that watered nothing
        // look like a clean run.
        if (step.Status == PlanStepStatus.Running) step.Status = status;

        step.EndedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task MarkStepAsync(
        AppDbContext db, long runId, int index, PlanStepStatus status, DateTimeOffset at, CancellationToken ct)
    {
        var step = await db.PlanRunSteps.FirstOrDefaultAsync(s => s.PlanRunId == runId && s.Ordinal == index, ct);
        if (step is null) return;

        step.Status = status;
        if (status == PlanStepStatus.Running) step.StartedUtc = at;
        else step.EndedUtc = at;

        var run = await db.PlanRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is not null) run.StepIndex = index;

        await db.SaveChangesAsync(ct);
    }

    private async Task FinishAsync(
        AppDbContext db,
        ControllerRecord record,
        ActivePlanRun active,
        PlanRunStatus status,
        string? detail,
        CancellationToken ct,
        AlertService? alerts = null)
    {
        _tracker.Clear(record.Id);
        _clock.Cleared(record.Id);

        var run = await db.PlanRuns.FirstOrDefaultAsync(r => r.Id == active.RunId, ct);
        if (run is not null)
        {
            run.Status = status;
            run.EndedUtc = DateTimeOffset.UtcNow;
            run.Detail = detail;
            await db.SaveChangesAsync(ct);
        }

        _logger.LogInformation("Plan {Plan} {Status}", active.PlanName, status.ToString().ToLowerInvariant());
        if (run is not null) await NotifyAsync(record.Id, "planFinished", run, ct);

        if (alerts is not null) await AlertForFinishAsync(alerts, record, active, status, detail, ct);
    }

    /// <summary>
    /// Tells the user how a plan ended.
    ///
    /// A plan that failed is the case worth interrupting someone for: the lawn did
    /// not get watered and nothing else will say so. Completion is reported too, but
    /// is off by default — an app that announces every success is one whose
    /// notifications get muted, taking the useful ones with them.
    /// </summary>
    private static async Task AlertForFinishAsync(
        AlertService alerts,
        ControllerRecord record,
        ActivePlanRun active,
        PlanRunStatus status,
        string? detail,
        CancellationToken ct)
    {
        switch (status)
        {
            case PlanRunStatus.Completed:
                await alerts.RaiseAsync(
                    AlertKind.PlanCompleted,
                    AlertSeverity.Info,
                    $"{active.PlanName} finished",
                    $"Watering on {record.Name} completed as planned.",
                    record.Id, ct);
                break;

            case PlanRunStatus.Failed:
                await alerts.RaiseAsync(
                    AlertKind.PlanFailed,
                    AlertSeverity.Problem,
                    $"{active.PlanName} did not finish",
                    detail is { Length: > 0 }
                        ? $"On {record.Name}: {detail}"
                        : $"Watering on {record.Name} stopped before it was done.",
                    record.Id, ct);
                break;
        }
    }

    /// <summary>
    /// Stops a plan part way through and closes the valve it opened.
    ///
    /// Called when the user stops watering, starts something by hand, or cancels the
    /// plan — anything that means the queue should no longer own the controller.
    /// </summary>
    public Task CancelAsync(int controllerId, string reason, CancellationToken ct = default) =>
        _operations.ExclusivelyAsync(controllerId, token => CancelCoreAsync(controllerId, reason, token), ct);

    /// <summary>
    /// The body of a cancellation, which must only ever run while holding the
    /// controller's operation lock — otherwise an advance already in flight lands
    /// after the stop and opens a zone nothing is tracking.
    /// </summary>
    private async Task CancelCoreAsync(int controllerId, string reason, CancellationToken ct)
    {
        if (!_tracker.TryGet(controllerId, out var active) || active is null) return;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var controllers = scope.ServiceProvider.GetRequiredService<ControllerService>();
        var alerts = scope.ServiceProvider.GetRequiredService<AlertService>();

        var record = await db.Controllers.FirstOrDefaultAsync(c => c.Id == controllerId, ct);
        if (record is null)
        {
            _tracker.Clear(controllerId);
            return;
        }

        if (active.StepIndex >= 0 && !active.Steps[active.StepIndex].IsSoak)
        {
            try
            {
                await controllers.Connect(record).Client.StopIrrigationAsync(ct);
            }
            catch (Exception ex) when (ex is RainBirdProtocolException or InvalidOperationException)
            {
                // The step is bounded anyway, so the valve closes regardless.
                _logger.LogWarning(ex, "Could not stop the controller while cancelling a plan");
            }
        }

        await FinishAsync(db, record, active, PlanRunStatus.Cancelled, reason, ct, alerts);
    }

    // --------------------------------------------------------------- helpers

    internal static TimeSpan ZoneOffset(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId).GetUtcOffset(DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow);
        }
    }

    private Task NotifyAsync(int controllerId, string eventName, PlanRun run, CancellationToken ct) =>
        _hub.Clients.Group(ControllerHub.GroupFor(controllerId)).SendAsync(
            eventName,
            new { controllerId, runId = run.Id, plan = run.PlanName, status = run.Status.ToString(), run.Detail },
            ct);

    private Task NotifyStepAsync(int controllerId, ActivePlanRun active, int index, CancellationToken ct)
    {
        var step = active.Steps[index];
        return _hub.Clients.Group(ControllerHub.GroupFor(controllerId)).SendAsync(
            "planStep",
            new
            {
                controllerId,
                plan = active.PlanName,
                station = step.StationNumber,
                minutes = step.Minutes,
                cycle = step.Cycle,
                step = index + 1,
                of = active.Steps.Count,
            },
            ct);
    }
}

/// <summary>A plan run in flight.</summary>
public sealed record ActivePlanRun(
    long RunId,
    int PlanId,
    string PlanName,
    IReadOnlyList<PlanStep> Steps,
    int StepIndex,
    DateTimeOffset StepStartedUtc)
{
    public PlanStep? CurrentStep => StepIndex >= 0 && StepIndex < Steps.Count ? Steps[StepIndex] : null;

    public int RemainingSteps => Math.Max(0, Steps.Count - StepIndex - 1);
}

/// <summary>
/// Which plan is running on which controller.
///
/// A singleton so the background engine and the API agree on it: the API needs to
/// cancel a run, and the engine needs to know one is in flight.
/// </summary>
public sealed class PlanRunTracker
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, ActivePlanRun> _active = new();

    public bool TryGet(int controllerId, out ActivePlanRun? run) => _active.TryGetValue(controllerId, out run);

    public ActivePlanRun? Get(int controllerId) => _active.GetValueOrDefault(controllerId);

    public void Set(int controllerId, ActivePlanRun run) => _active[controllerId] = run;

    public void Clear(int controllerId) => _active.TryRemove(controllerId, out _);

    public bool IsRunning(int controllerId) => _active.ContainsKey(controllerId);
}
