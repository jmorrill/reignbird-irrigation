using Microsoft.EntityFrameworkCore;
using RainBird.Protocol;
using RainBird.Protocol.Universal;
using RainBird.Server.Data;
using RainBird.Server.Services;

namespace RainBird.Server.Api;

// Requests --------------------------------------------------------------------

public sealed record PlanZoneRequest(int StationNumber, int Minutes, int SortOrder);

public sealed record SavePlanRequest(
    string Name,
    string? Description,
    bool Enabled,
    PlanFrequency Frequency,
    IReadOnlyList<bool> DaysOfWeek,
    int IntervalDays,
    IReadOnlyList<int> StartTimes,
    int? LatestStartMinute,
    int SeasonalAdjustPercent,
    bool CycleSoakEnabled,
    int Cycles,
    int SoakMinutes,
    bool WeatherSkipEnabled,
    IReadOnlyList<PlanZoneRequest> Zones);

public sealed record CreateFromPresetRequest(string Preset);

// Responses -------------------------------------------------------------------

public sealed record PlanStepDto(int? StationNumber, string? ZoneName, int Minutes, int Cycle, bool IsSoak);

public sealed record PlanDto(
    int Id,
    string Name,
    string Description,
    bool Enabled,
    PlanFrequency Frequency,
    IReadOnlyList<bool> DaysOfWeek,
    int IntervalDays,
    IReadOnlyList<int> StartTimes,
    int? LatestStartMinute,
    int SeasonalAdjustPercent,
    bool CycleSoakEnabled,
    int Cycles,
    int SoakMinutes,
    bool WeatherSkipEnabled,
    int SortOrder,
    IReadOnlyList<PlanZoneRequest> Zones,
    int WateringMinutesPerPass,
    int ElapsedMinutesPerPass,
    int PassesPerDay,
    int WateringMinutesPerDay,
    DateTimeOffset? NextRunUtc,
    IReadOnlyList<PlanStepDto> Timeline);

public sealed record PlanPresetDto(string Key, string Name, string Summary, string Rationale);

public sealed record ActivePlanDto(
    long RunId,
    int PlanId,
    string PlanName,
    int StepIndex,
    int StepCount,
    int? CurrentStation,
    string? CurrentZoneName,
    bool Soaking,
    int StepMinutes,
    int RemainingSteps);

public sealed record PlanRunDto(
    long Id,
    string PlanName,
    DateTimeOffset StartedUtc,
    DateTimeOffset? EndedUtc,
    string Status,
    string? Detail,
    int StepCount,
    int CompletedSteps,
    int WateringMinutes);

public sealed record ArmedStateDto(
    bool CanDisarm,
    bool ControllerScheduleCleared,
    string Explanation,
    IReadOnlyList<int> ProgramRunTimeTotals);

public static class PlanEndpoints
{
    public static void MapPlanApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/controllers/{id:int}").WithTags("Plans");

        MapPresets(group);
        MapCrud(group);
        MapExecution(group);
        MapArming(group);
    }

    // ---------------------------------------------------------------- presets

    private static void MapPresets(RouteGroupBuilder group)
    {
        group.MapGet("/plan-presets", () => Results.Ok(
            PlanPresets.All
                .Select(preset => new PlanPresetDto(preset.Key, preset.Name, preset.Summary, preset.Rationale))
                .ToList()));

        group.MapPost("/plans/from-preset", async (
            int id, CreateFromPresetRequest request, AppDbContext db, CancellationToken ct) =>
        {
            var preset = PlanPresets.Find(request.Preset);
            if (preset is null)
                return Results.BadRequest(new { message = $"No preset named '{request.Preset}'." });

            var zones = await db.Zones
                .Where(zone => zone.ControllerId == id)
                .OrderBy(zone => zone.SortOrder)
                .ToListAsync(ct);

            if (zones.Count == 0)
                return Results.BadRequest(new { message = "This controller has no zones yet." });

            var plan = preset.Build(zones);
            plan.ControllerId = id;
            plan.IntervalAnchor = DateOnly.FromDateTime(DateTime.UtcNow);
            plan.SortOrder = await db.WateringPlans.CountAsync(p => p.ControllerId == id, ct);

            // A new plan arrives switched off. Creating one should never start water
            // running before the user has looked at the durations.
            plan.Enabled = false;

            db.WateringPlans.Add(plan);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/controllers/{id}/plans/{plan.Id}", await ToDtoAsync(db, plan, ct));
        });
    }

    // ------------------------------------------------------------------- crud

    private static void MapCrud(RouteGroupBuilder group)
    {
        group.MapGet("/plans", async (int id, AppDbContext db, CancellationToken ct) =>
        {
            var plans = await db.WateringPlans
                .Include(plan => plan.Zones)
                .Where(plan => plan.ControllerId == id)
                .OrderBy(plan => plan.SortOrder)
                .ToListAsync(ct);

            var dtos = new List<PlanDto>(plans.Count);
            foreach (var plan in plans) dtos.Add(await ToDtoAsync(db, plan, ct));
            return Results.Ok(dtos);
        });

        group.MapGet("/plans/{planId:int}", async (
            int id, int planId, AppDbContext db, CancellationToken ct) =>
        {
            var plan = await LoadPlanAsync(db, id, planId, ct);
            return plan is null
                ? Results.NotFound(new { message = $"No plan with id {planId}." })
                : Results.Ok(await ToDtoAsync(db, plan, ct));
        });

        group.MapPost("/plans", async (
            int id, SavePlanRequest request, AppDbContext db, CancellationToken ct) =>
        {
            var plan = new WateringPlan
            {
                ControllerId = id,
                IntervalAnchor = DateOnly.FromDateTime(DateTime.UtcNow),
                SortOrder = await db.WateringPlans.CountAsync(p => p.ControllerId == id, ct),
            };

            if (Apply(plan, request) is { } error) return error;

            db.WateringPlans.Add(plan);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/controllers/{id}/plans/{plan.Id}", await ToDtoAsync(db, plan, ct));
        });

        group.MapPut("/plans/{planId:int}", async (
            int id, int planId, SavePlanRequest request, AppDbContext db, CancellationToken ct) =>
        {
            var plan = await LoadPlanAsync(db, id, planId, ct);
            if (plan is null) return Results.NotFound(new { message = $"No plan with id {planId}." });

            db.PlanZones.RemoveRange(plan.Zones);
            plan.Zones.Clear();

            if (Apply(plan, request) is { } error) return error;

            await db.SaveChangesAsync(ct);
            return Results.Ok(await ToDtoAsync(db, plan, ct));
        });

        group.MapDelete("/plans/{planId:int}", async (
            int id, int planId, AppDbContext db, PlanRunTracker tracker,
            PlanExecutionService engine, CancellationToken ct) =>
        {
            var plan = await LoadPlanAsync(db, id, planId, ct);
            if (plan is null) return Results.NotFound(new { message = $"No plan with id {planId}." });

            if (tracker.Get(id)?.PlanId == planId)
                await engine.CancelAsync(id, "The plan was deleted while it was running.", ct);

            db.WateringPlans.Remove(plan);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    // -------------------------------------------------------------- execution

    private static void MapExecution(RouteGroupBuilder group)
    {
        group.MapPost("/plans/{planId:int}/run", async (
            int id,
            int planId,
            AppDbContext db,
            ControllerService controllers,
            PlanExecutionService engine,
            PlanRunTracker tracker,
            CancellationToken ct) =>
        {
            var record = await db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (record is null) return Results.NotFound(new { message = $"No controller with id {id}." });

            var plan = await LoadPlanAsync(db, id, planId, ct);
            if (plan is null) return Results.NotFound(new { message = $"No plan with id {planId}." });

            if (tracker.IsRunning(id))
                return Results.Conflict(new { message = "A plan is already running on this controller." });

            var offset = PlanExecutionService.ZoneOffset(record.TimeZoneId);
            var localNow = DateTimeOffset.UtcNow.ToOffset(offset);

            // Recorded against the current minute so a manual run does not also block
            // a genuinely scheduled pass later in the day.
            var run = await engine.StartAsync(
                db, controllers, record, plan,
                DateOnly.FromDateTime(localNow.DateTime),
                localNow.Hour * 60 + localNow.Minute,
                ct);

            return run is null
                ? Results.BadRequest(new { message = "This plan has no zones with a run time." })
                : Results.Accepted($"/api/controllers/{id}/plans/active");
        });

        group.MapGet("/plans/active", async (
            int id, AppDbContext db, PlanRunTracker tracker, CancellationToken ct) =>
        {
            var active = tracker.Get(id);
            if (active is null) return Results.Ok((ActivePlanDto?)null);

            var step = active.CurrentStep;
            var zoneName = step?.StationNumber is { } station
                ? (await db.Zones.FirstOrDefaultAsync(
                    z => z.ControllerId == id && z.StationNumber == station, ct))?.Name
                : null;

            return Results.Ok(new ActivePlanDto(
                active.RunId,
                active.PlanId,
                active.PlanName,
                active.StepIndex + 1,
                active.Steps.Count,
                step?.StationNumber,
                zoneName,
                step?.IsSoak ?? false,
                step?.Minutes ?? 0,
                active.RemainingSteps));
        });

        group.MapPost("/plans/cancel", async (
            int id, PlanExecutionService engine, PlanRunTracker tracker, CancellationToken ct) =>
        {
            if (!tracker.IsRunning(id))
                return Results.Ok(new { cancelled = false });

            await engine.CancelAsync(id, "Cancelled from the app.", ct);
            return Results.Ok(new { cancelled = true });
        });

        group.MapGet("/plan-runs", async (int id, AppDbContext db, CancellationToken ct) =>
        {
            var runs = await db.PlanRuns
                .Include(run => run.Steps)
                .Where(run => run.ControllerId == id)
                .OrderByDescending(run => run.StartedUtc)
                .Take(40)
                .ToListAsync(ct);

            return Results.Ok(runs.Select(run => new PlanRunDto(
                run.Id,
                run.PlanName,
                run.StartedUtc,
                run.EndedUtc,
                run.Status.ToString(),
                run.Detail,
                run.Steps.Count,
                run.Steps.Count(step => step.Status == PlanStepStatus.Completed),
                run.Steps.Where(step => step.StationNumber > 0).Sum(step => step.Minutes))).ToList());
        });
    }

    // ----------------------------------------------------------------- arming

    private static void MapArming(RouteGroupBuilder group)
    {
        group.MapGet("/armed-state", async (
            int id, AppDbContext db, ControllerService controllers, CancellationToken ct) =>
        {
            var record = await db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (record is null) return Results.NotFound(new { message = $"No controller with id {id}." });

            return await ArmedStateAsync(controllers, record, ct);
        });

        group.MapPost("/disarm", async (
            int id, AppDbContext db, ControllerService controllers, CancellationToken ct) =>
        {
            var record = await db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (record is null) return Results.NotFound(new { message = $"No controller with id {id}." });

            var connection = controllers.Connect(record);
            var capabilities = connection.Capabilities
                ?? ControllerService.DeserializeCapabilities(record.CapabilitiesJson);

            if (capabilities is null)
                return Results.BadRequest(new { message = "Probe the controller before disarming it." });

            if (!capabilities.SupportsUniversalTransport)
            {
                return Results.BadRequest(new
                {
                    message = "This controller does not support the universal transport, so its "
                              + "run times cannot be cleared from here. Set every zone to 0 minutes "
                              + "on the controller itself.",
                });
            }

            try
            {
                var universal = new UniversalClient(connection.Client);
                var cleared = await universal.ClearAllRunTimesAsync(
                    capabilities.Model.MaxPrograms, capabilities.Model.MaxStations, ct);

                return Results.Ok(new
                {
                    cleared,
                    message = cleared == 0
                        ? "The controller was already clear — nothing waters automatically."
                        : $"Cleared the run times on {cleared} {(cleared == 1 ? "program" : "programs")}. "
                          + "This app now owns the schedule.",
                });
            }
            catch (RainBirdProtocolException ex)
            {
                return Results.Problem(
                    title: "Could not clear the controller's schedule",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }

    /// <summary>
    /// Reports whether the controller still has a schedule of its own.
    ///
    /// This matters because the controller and this app would otherwise both be
    /// watering, on different schedules, with no indication that anything is wrong.
    /// </summary>
    private static async Task<IResult> ArmedStateAsync(
        ControllerService controllers, ControllerRecord record, CancellationToken ct)
    {
        var connection = controllers.Connect(record);
        var capabilities = connection.Capabilities
            ?? ControllerService.DeserializeCapabilities(record.CapabilitiesJson);

        if (capabilities is null)
        {
            return Results.Ok(new ArmedStateDto(
                false, false, "Probe the controller to find out whether it has a schedule of its own.", []));
        }

        if (!capabilities.SupportsUniversalTransport)
        {
            return Results.Ok(new ArmedStateDto(
                false,
                false,
                "This controller cannot report its schedule over the wire. Set every zone to "
                + "0 minutes on the controller itself so it does not water on its own.",
                []));
        }

        try
        {
            var universal = new UniversalClient(connection.Client);
            var totals = new List<int>();

            for (var program = 0; program < capabilities.Model.MaxPrograms; program++)
            {
                var runTimes = await universal.GetRunTimesAsync(
                    program, capabilities.Model.MaxStations, ct);
                totals.Add(runTimes.Values.Sum());
            }

            var cleared = totals.All(total => total == 0);

            return Results.Ok(new ArmedStateDto(
                true,
                cleared,
                cleared
                    ? "The controller has no schedule of its own, so this app is the only thing watering."
                    : "The controller still has run times programmed and will water on its own as well "
                      + "as following this app's plans. Clear them so there is one schedule, not two.",
                totals));
        }
        catch (RainBirdProtocolException ex)
        {
            return Results.Ok(new ArmedStateDto(
                false, false, $"Could not read the controller's schedule: {ex.Message}", []));
        }
    }

    // ---------------------------------------------------------------- helpers

    private static Task<WateringPlan?> LoadPlanAsync(
        AppDbContext db, int controllerId, int planId, CancellationToken ct) =>
        db.WateringPlans
            .Include(plan => plan.Zones)
            .FirstOrDefaultAsync(plan => plan.Id == planId && plan.ControllerId == controllerId, ct);

    /// <summary>Applies a save request, or returns the error response.</summary>
    private static IResult? Apply(WateringPlan plan, SavePlanRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.BadRequest(new { message = "A plan needs a name." });

        var startTimes = request.StartTimes.Where(minute => minute is >= 0 and < 1440).Distinct().Order().ToList();
        if (startTimes.Count == 0)
            return Results.BadRequest(new { message = "A plan needs at least one start time." });

        if (request.Cycles is < 1 or > 10)
            return Results.BadRequest(new { message = "Cycles must be between 1 and 10." });

        if (request.SoakMinutes is < 0 or > 240)
            return Results.BadRequest(new { message = "Soak time must be between 0 and 240 minutes." });

        if (request.SeasonalAdjustPercent is < 0 or > 300)
            return Results.BadRequest(new { message = "Seasonal adjust must be between 0% and 300%." });

        plan.Name = request.Name.Trim();
        plan.Description = request.Description?.Trim() ?? "";
        plan.Enabled = request.Enabled;
        plan.Frequency = request.Frequency;
        plan.DaysOfWeek = string.Concat(
            Enumerable.Range(0, 7).Select(i => i < request.DaysOfWeek.Count && request.DaysOfWeek[i] ? '1' : '0'));
        plan.IntervalDays = Math.Clamp(request.IntervalDays, 1, 31);
        plan.StartTimes = string.Join(',', startTimes);
        plan.LatestStartMinute = request.LatestStartMinute is >= 0 and < 1440 ? request.LatestStartMinute : null;
        plan.SeasonalAdjustPercent = request.SeasonalAdjustPercent;
        plan.CycleSoakEnabled = request.CycleSoakEnabled;
        plan.Cycles = request.Cycles;
        plan.SoakMinutes = request.SoakMinutes;
        plan.WeatherSkipEnabled = request.WeatherSkipEnabled;

        plan.IntervalAnchor ??= DateOnly.FromDateTime(DateTime.UtcNow);

        foreach (var zone in request.Zones.Where(zone => zone.Minutes > 0))
        {
            plan.Zones.Add(new PlanZone
            {
                StationNumber = zone.StationNumber,
                Minutes = Math.Clamp(zone.Minutes, 1, 600),
                SortOrder = zone.SortOrder,
            });
        }

        return null;
    }

    private static async Task<PlanDto> ToDtoAsync(AppDbContext db, WateringPlan plan, CancellationToken ct)
    {
        var steps = PlanCompiler.Compile(plan, plan.Zones);

        var zoneNames = await db.Zones
            .Where(zone => zone.ControllerId == plan.ControllerId)
            .ToDictionaryAsync(zone => zone.StationNumber, zone => zone.Name, ct);

        var controller = await db.Controllers
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == plan.ControllerId, ct);

        var offset = PlanExecutionService.ZoneOffset(controller?.TimeZoneId ?? TimeZoneInfo.Local.Id);
        var passes = plan.StartTimeMinutes.Count;
        var wateringPerPass = PlanCompiler.WateringMinutes(steps);

        return new PlanDto(
            plan.Id,
            plan.Name,
            plan.Description,
            plan.Enabled,
            plan.Frequency,
            plan.DayFlags,
            plan.IntervalDays,
            plan.StartTimeMinutes,
            plan.LatestStartMinute,
            plan.SeasonalAdjustPercent,
            plan.CycleSoakEnabled,
            plan.Cycles,
            plan.SoakMinutes,
            plan.WeatherSkipEnabled,
            plan.SortOrder,
            plan.Zones
                .OrderBy(zone => zone.SortOrder)
                .Select(zone => new PlanZoneRequest(zone.StationNumber, zone.Minutes, zone.SortOrder))
                .ToList(),
            wateringPerPass,
            PlanCompiler.ElapsedMinutes(steps),
            passes,
            wateringPerPass * passes,
            PlanCompiler.NextRun(plan, DateTimeOffset.UtcNow.ToOffset(offset), offset),
            steps.Select(step => new PlanStepDto(
                step.StationNumber,
                step.StationNumber is { } station ? zoneNames.GetValueOrDefault(station, $"Zone {station}") : null,
                step.Minutes,
                step.Cycle,
                step.IsSoak)).ToList());
    }
}
