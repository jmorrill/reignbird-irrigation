using Microsoft.EntityFrameworkCore;
using RainBird.Protocol;
using RainBird.Server.Data;
using RainBird.Server.Services;

namespace RainBird.Server.Api;

public static class Endpoints
{
    public static void MapRainBirdApi(this IEndpointRouteBuilder app)
    {
        // Everything hanging off here needs a signed-in user. Applied at the group so
        // that a route added later is protected by default rather than by memory.
        var api = app.MapGroup("/api").WithTags("RainBird").RequireAuthorization();

        MapControllers(api);
        MapZones(api);
        MapControl(api);
        MapPrograms(api);
        MapHistory(api);
        MapWeather(api);
        MapSettings(api);
        MapDiagnostics(api);
    }

    /// <summary>
    /// How stale the polled state may be before a caller reads through to the device.
    /// The polling loop runs every five seconds while anyone is watching, so this is
    /// comfortably inside it.
    /// </summary>
    private static readonly TimeSpan StateFreshness = TimeSpan.FromSeconds(8);

    /// <summary>
    /// How long a program read stays good for. Longer than the state cache because
    /// programs only change when somebody edits one, and every edit made through this
    /// app clears the cache outright.
    /// </summary>
    private static readonly TimeSpan ProgramFreshness = TimeSpan.FromMinutes(5);

    // -------------------------------------------------------------- helpers

    /// <summary>
    /// Loads a controller row, or produces the 404 for the caller. Every endpoint
    /// starts this way, so it lives here rather than being repeated.
    /// </summary>
    private static async Task<(ControllerRecord? Record, IResult? Error)> LoadAsync(
        AppDbContext db, int id, CancellationToken ct)
    {
        var record = await db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        return record is null
            ? (null, Results.NotFound(new { message = $"No controller with id {id}." }))
            : (record, null);
    }

    /// <summary>
    /// Runs a device operation, translating protocol failures into HTTP responses the
    /// UI can show verbatim. A NAK means the model doesn't support what was asked; a
    /// connection failure means the controller is unreachable. Both are expected
    /// conditions in normal use, not server faults.
    /// </summary>
    private static async Task<IResult> DeviceAsync(Func<Task<IResult>> operation)
    {
        try
        {
            return await operation();
        }
        catch (RainBirdNakException ex)
        {
            return Results.Problem(
                title: "The controller rejected the command",
                detail: ex.Message,
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        catch (RainBirdAuthenticationException ex)
        {
            return Results.Problem(
                title: "The controller password is wrong",
                detail: ex.Message,
                statusCode: StatusCodes.Status401Unauthorized);
        }
        catch (RainBirdConnectionException ex)
        {
            return Results.Problem(
                title: "The controller is unreachable",
                detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (RainBirdProtocolException ex)
        {
            return Results.Problem(
                title: "The controller sent an unexpected response",
                detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(
                title: "Stored credentials could not be used",
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<ControllerSummary> SummarizeAsync(
        AppDbContext db, ControllerRecord record, ControllerRegistry registry, RunClock clock,
        CancellationToken ct)
    {
        var connection = registry.Find(record.Id);
        _ = db; _ = ct;

        return new ControllerSummary(
            record.Id,
            record.Name,
            record.Host,
            record.ModelId,
            ControllerModels.Lookup(record.ModelId).Series,
            record.SerialNumber,
            record.FirmwareVersion,
            connection?.IsOnline ?? false,
            connection?.LastError,
            connection?.LastSeenUtc ?? record.LastSeenUtc,
            record.Latitude,
            record.Longitude,
            record.TimeZoneId,
            connection?.LastState is { } state ? ControllerStateDto.From(state, clock, record.Id) : null);
    }

    private static async Task<ScheduleClient?> ScheduleForAsync(
        ControllerService controllers, ControllerRecord record, CancellationToken ct)
    {
        var connection = controllers.Connect(record);
        var capabilities = connection.Capabilities
            ?? ControllerService.DeserializeCapabilities(record.CapabilitiesJson)
            ?? await controllers.RefreshCapabilitiesAsync(record, ct);

        connection.Capabilities = capabilities;

        // Gate on what the controller actually answered to, not on the model table:
        // firmware within a model varies in whether the schedule pages exist at all.
        return capabilities.SupportsSchedulePages
            ? new ScheduleClient(connection.Client, capabilities)
            : null;
    }

    // ---------------------------------------------------------- controllers

    private static void MapControllers(RouteGroupBuilder api)
    {
        var group = api.MapGroup("/controllers");

        group.MapGet("/", async (AppDbContext db, ControllerRegistry registry, RunClock clock, CancellationToken ct) =>
        {
            var records = await db.Controllers.OrderBy(c => c.Id).ToListAsync(ct);
            var summaries = new List<ControllerSummary>(records.Count);
            foreach (var record in records)
                summaries.Add(await SummarizeAsync(db, record, registry, clock, ct));
            return Results.Ok(summaries);
        });

        group.MapGet("/{id:int}", async (
            int id, AppDbContext db, ControllerRegistry registry, RunClock clock, CancellationToken ct) =>
        {
            var (record, error) = await LoadAsync(db, id, ct);
            return error ?? Results.Ok(await SummarizeAsync(db, record!, registry, clock, ct));
        });

        group.MapPost("/", async (
            AddControllerRequest request,
            AppDbContext db,
            ControllerService controllers,
            ControllerRegistry registry,
            RunClock clock,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Host))
                return Results.BadRequest(new { message = "A host or IP address is required." });

            var record = new ControllerRecord
            {
                Name = string.IsNullOrWhiteSpace(request.Name) ? "My Controller" : request.Name.Trim(),
                Host = request.Host.Trim(),
                ProtectedPassword = controllers.Protect(request.Password ?? ""),
                Latitude = request.Latitude,
                Longitude = request.Longitude,
                TimeZoneId = string.IsNullOrWhiteSpace(request.TimeZoneId)
                    ? TimeZoneInfo.Local.Id
                    : request.TimeZoneId,
            };

            db.Controllers.Add(record);
            await db.SaveChangesAsync(ct);

            // Probe immediately so a bad address or password fails while the user is
            // still looking at the form, rather than silently later.
            return await DeviceAsync(async () =>
            {
                try
                {
                    await controllers.ProbeAsync(record, ct);
                    await db.SaveChangesAsync(ct);
                }
                catch
                {
                    db.Controllers.Remove(record);
                    await db.SaveChangesAsync(ct);
                    registry.Remove(record.Id);
                    throw;
                }

                return Results.Created(
                    $"/api/controllers/{record.Id}",
                    await SummarizeAsync(db, record, registry, clock, ct));
            });
        });

        group.MapPut("/{id:int}", async (
            int id,
            UpdateControllerRequest request,
            AppDbContext db,
            ControllerService controllers,
            ControllerRegistry registry,
            RunClock clock,
            CancellationToken ct) =>
        {
            var (record, error) = await LoadAsync(db, id, ct);
            if (error is not null) return error;

            if (request.Name is not null) record!.Name = request.Name.Trim();
            if (request.Host is not null) record!.Host = request.Host.Trim();
            if (request.Password is not null) record!.ProtectedPassword = controllers.Protect(request.Password);
            if (request.Latitude is not null) record!.Latitude = request.Latitude;
            if (request.Longitude is not null) record!.Longitude = request.Longitude;
            if (request.TimeZoneId is not null) record!.TimeZoneId = request.TimeZoneId;

            await db.SaveChangesAsync(ct);
            return Results.Ok(await SummarizeAsync(db, record!, registry, clock, ct));
        });

        group.MapDelete("/{id:int}", async (
            int id, AppDbContext db, ControllerRegistry registry, CancellationToken ct) =>
        {
            var (record, error) = await LoadAsync(db, id, ct);
            if (error is not null) return error;

            db.Controllers.Remove(record!);
            await db.SaveChangesAsync(ct);
            registry.Remove(id);
            return Results.NoContent();
        });

        group.MapGet("/{id:int}/state", async (
            int id, AppDbContext db, ControllerService controllers, RunClock clock, CancellationToken ct) =>
        {
            var (record, error) = await LoadAsync(db, id, ct);
            if (error is not null) return error;

            var connection = controllers.Connect(record!);

            // Serve what the polling loop already read, if it is recent.
            //
            // Firmware without SIP 4C needs six round trips to assemble this, and every
            // request is serialised onto the device. Reading through on each call means
            // the polling loop, the plan engine and the browser queue behind each other
            // and all three become slow. One reader, everyone else shares the result.
            if (connection.LastState is { } cached
                && connection.LastSeenUtc is { } seen
                && DateTimeOffset.UtcNow - seen < StateFreshness)
            {
                return Results.Ok(ControllerStateDto.From(cached, clock, id));
            }

            return await DeviceAsync(async () =>
            {
                var state = await connection.Client.GetCombinedStateAsync(ct);
                connection.LastState = state;
                connection.LastSeenUtc = DateTimeOffset.UtcNow;
                return Results.Ok(ControllerStateDto.From(state, clock, id));
            });
        });

        group.MapGet("/{id:int}/capabilities", async (
            int id, AppDbContext db, ControllerService controllers, CancellationToken ct) =>
        {
            var (record, error) = await LoadAsync(db, id, ct);
            if (error is not null) return error;

            var cached = ControllerService.DeserializeCapabilities(record!.CapabilitiesJson);
            if (cached is not null) return Results.Ok(CapabilitiesDto.From(cached));

            return await DeviceAsync(async () =>
                Results.Ok(CapabilitiesDto.From(await controllers.RefreshCapabilitiesAsync(record, ct))));
        });

        group.MapPost("/{id:int}/refresh", async (
            int id, AppDbContext db, ControllerService controllers, CancellationToken ct) =>
        {
            var (record, error) = await LoadAsync(db, id, ct);
            if (error is not null) return error;

            return await DeviceAsync(async () =>
            {
                // Re-detect the scheme too: a firmware update can move a controller
                // from plain HTTP to TLS.
                var capabilities = await controllers.ProbeAsync(record!, ct);
                await db.SaveChangesAsync(ct);
                return Results.Ok(CapabilitiesDto.From(capabilities));
            });
        });
    }

    // ---------------------------------------------------------------- zones

    private static void MapZones(RouteGroupBuilder api)
    {
        var group = api.MapGroup("/controllers/{id:int}/zones");

        group.MapGet("/", async (
            int id, AppDbContext db, ControllerRegistry registry, CancellationToken ct) =>
        {
            var (record, error) = await LoadAsync(db, id, ct);
            if (error is not null) return error;

            var zones = await db.Zones
                .Where(z => z.ControllerId == id)
                .OrderBy(z => z.SortOrder).ThenBy(z => z.StationNumber)
                .ToListAsync(ct);

            // One query for the whole page rather than one per zone. Grouping happens
            // in memory: SQLite cannot translate a per-group First(), and the window
            // here is small and bounded.
            var since = DateTimeOffset.UtcNow.AddDays(-60);
            var recentRuns = await db.Runs
                .Where(r => r.ControllerId == id && r.StartedUtc >= since)
                .OrderByDescending(r => r.StartedUtc)
                .ToListAsync(ct);

            var lastRuns = recentRuns
                .GroupBy(r => r.StationNumber)
                .Select(g => g.First())
                .ToList();

            var state = registry.Find(id)?.LastState;

            return Results.Ok(zones
                .Select(z => ZoneDto.From(z, lastRuns.FirstOrDefault(r => r.StationNumber == z.StationNumber), state))
                .ToList());
        });

        group.MapPut("/{station:int}", async (
            int id, int station, UpdateZoneRequest request,
            AppDbContext db, ControllerRegistry registry, CancellationToken ct) =>
        {
            var zone = await db.Zones.FirstOrDefaultAsync(
                z => z.ControllerId == id && z.StationNumber == station, ct);

            if (zone is null)
                return Results.NotFound(new { message = $"Controller {id} has no station {station}." });

            if (request.Name is not null) zone.Name = request.Name.Trim();
            if (request.PlantType is not null) zone.PlantType = request.PlantType.Value;
            if (request.SoilType is not null) zone.SoilType = request.SoilType.Value;
            if (request.SunExposure is not null) zone.SunExposure = request.SunExposure.Value;
            if (request.Slope is not null) zone.Slope = request.Slope.Value;
            if (request.SprinklerType is not null) zone.SprinklerType = request.SprinklerType.Value;
            if (request.NozzleFlowGpm is not null) zone.NozzleFlowGpm = Math.Max(0, request.NozzleFlowGpm.Value);
            if (request.Enabled is not null)
            {
                zone.Enabled = request.Enabled.Value;
                // An explicit choice, either way, is no longer an automatic one.
                zone.AutoDisabled = false;
            }
            if (request.SortOrder is not null) zone.SortOrder = request.SortOrder.Value;

            await db.SaveChangesAsync(ct);

            var lastRun = await db.Runs
                .Where(r => r.ControllerId == id && r.StationNumber == station)
                .OrderByDescending(r => r.StartedUtc)
                .FirstOrDefaultAsync(ct);

            return Results.Ok(ZoneDto.From(zone, lastRun, registry.Find(id)?.LastState));
        });

        group.MapPost("/{station:int}/photo", async (
            int id, int station, IFormFile file,
            AppDbContext db, StoragePaths storage, CancellationToken ct) =>
        {
            var zone = await db.Zones.FirstOrDefaultAsync(
                z => z.ControllerId == id && z.StationNumber == station, ct);

            if (zone is null)
                return Results.NotFound(new { message = $"Controller {id} has no station {station}." });

            if (file.Length == 0)
                return Results.BadRequest(new { message = "The uploaded file is empty." });

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (extension is not (".jpg" or ".jpeg" or ".png" or ".webp"))
                return Results.BadRequest(new { message = "Photos must be JPEG, PNG or WebP." });

            // The same directory the static file branch serves from, resolved once at
            // startup — recomputing it here is how the two used to drift apart.
            Directory.CreateDirectory(storage.Media);

            var fileName = $"zone-{id}-{station}{extension}";
            await using (var stream = File.Create(Path.Combine(storage.Media, fileName)))
                await file.CopyToAsync(stream, ct);

            zone.PhotoPath = fileName;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { photoUrl = $"/media/{fileName}" });
        }).DisableAntiforgery();
    }

    // -------------------------------------------------------------- control

    private static void MapControl(RouteGroupBuilder api)
    {
        var group = api.MapGroup("/controllers/{id:int}");

        group.MapPost("/zones/{station:int}/run", async (
            int id, int station, RunZoneRequest request,
            AppDbContext db, ControllerService controllers,
            PlanExecutionService plans, RunClock clock, CancellationToken ct) =>
        {
            var (record, error) = await LoadAsync(db, id, ct);
            if (error is not null) return error;

            if (request.Minutes is < 1 or > 255)
                return Results.BadRequest(new { message = "Run time must be between 1 and 255 minutes." });

            // A running plan owns the controller. Starting a zone by hand takes it
            // back, rather than the two of them fighting over the valves.
            await plans.CancelAsync(id, "A zone was started by hand.", ct);

            return await DeviceAsync(async () =>
            {
                var connection = controllers.Connect(record!);
                await connection.Client.RunStationAsync(station, request.Minutes, ct);
                // The device may not report a countdown; this is what fills it in.
                clock.Started(id, station, request.Minutes);
                // So the history can say this was started by hand rather than by the
                // controller's own schedule.
                connection.NoteCommandedRun(station, RunTrigger.Manual,
                    TimeSpan.FromMinutes(request.Minutes + 2));
                return Results.Accepted();
            });
        });

        group.MapPost("/zones/{station:int}/queue", async (
            int id, int station, RunZoneRequest request,
            AppDbContext db, ControllerService controllers,
            PlanExecutionService plans, CancellationToken ct) =>
        {
            var (record, error) = await LoadAsync(db, id, ct);
            if (error is not null) return error;

            // Same bounds as the immediate run beside it. Without these the duration
            // was masked to a byte further down, so -1 asked for 255 minutes.
            if (request.Minutes is < 1 or > 255)
                return Results.BadRequest(new { message = "Run time must be between 1 and 255 minutes." });

            // Same reasoning as starting a zone by hand: queueing behind a plan
            // produces watering the plan does not know about, and its next step would
            // interrupt it anyway. Taking the controller means taking it.
            await plans.CancelAsync(id, "A zone was queued by hand.", ct);

            return await DeviceAsync(async () =>
            {
                var connection = controllers.Connect(record!);
                await connection.Client.StackStationAsync(station, request.Minutes, ct);
                connection.NoteCommandedRun(station, RunTrigger.Manual,
                    TimeSpan.FromMinutes(request.Minutes + 30));
                return Results.Accepted();
            });
        });

        group.MapPost("/stop", async (
            int id, AppDbContext db, ControllerService controllers,
            PlanExecutionService plans, RunClock clock, CancellationToken ct) =>
        {
            var (record, error) = await LoadAsync(db, id, ct);
            if (error is not null) return error;

            // Stop means stop: a plan part way through its queue would otherwise open
            // the next zone a few seconds later.
            await plans.CancelAsync(id, "Watering was stopped.", ct);

            // Nothing is running now, so no countdown should be reported.
            clock.Cleared(id);

            return await DeviceAsync(async () =>
            {
                await controllers.Connect(record!).Client.StopIrrigationAsync(ct);
                return Results.Accepted();
            });
        });

        group.MapPost("/advance", async (
            int id, AppDbContext db, ControllerService controllers,
            PlanExecutionService plans, CancellationToken ct) =>
        {
            var (record, error) = await LoadAsync(db, id, ct);
            if (error is not null) return error;

            // Skipping a zone by hand moves the hardware on while the plan keeps
            // counting its own steps, so the two disagree from then on about which
            // zone is running.
            await plans.CancelAsync(id, "Watering was advanced by hand.", ct);

            return await DeviceAsync(async () =>
            {
                await controllers.Connect(record!).Client.AdvanceStationAsync(0, ct);
                return Results.Accepted();
            });
        });

        group.MapPost("/test", async (
            int id, RunAllRequest request,
            AppDbContext db, ControllerService controllers,
            PlanExecutionService plans, CancellationToken ct) =>
        {
            var (record, error) = await LoadAsync(db, id, ct);
            if (error is not null) return error;

            await plans.CancelAsync(id, "A zone test was started.", ct);

            return await DeviceAsync(async () =>
            {
                var minutes = Math.Clamp(request.Minutes, 1, 255);
                var connection = controllers.Connect(record!);
                await connection.Client.TestAllStationsAsync(minutes, ct);

                // A test walks every station in turn, so the attribution has to cover
                // the whole sequence, not just the first zone.
                var stations = connection.Capabilities?.StationCount ?? 16;
                connection.NoteCommandedRun(null, RunTrigger.Test,
                    TimeSpan.FromMinutes(minutes * stations + 5));
                return Results.Accepted();
            });
        });

        group.MapGet("/rain-delay", async (
            int id, AppDbContext db, ControllerService controllers, CancellationToken ct) =>
        {
            var (record, error) = await LoadAsync(db, id, ct);
            if (error is not null) return error;

            return await DeviceAsync(async () =>
                Results.Ok(new { days = await controllers.Connect(record!).Client.GetRainDelayAsync(ct) }));
        });

        group.MapPut("/rain-delay", async (
            int id, RainDelayRequest request,
            AppDbContext db, ControllerService controllers, CancellationToken ct) =>
        {
            var (record, error) = await LoadAsync(db, id, ct);
            if (error is not null) return error;

            if (request.Days is < 0 or > 14)
                return Results.BadRequest(new { message = "Rain delay must be between 0 and 14 days." });

            return await DeviceAsync(async () =>
            {
                await controllers.Connect(record!).Client.SetRainDelayAsync(request.Days, ct);
                return Results.Ok(new { days = request.Days });
            });
        });

        group.MapPut("/seasonal-adjust", async (
            int id, SeasonalAdjustRequest request,
            AppDbContext db, ControllerService controllers, CancellationToken ct) =>
        {
            var (record, error) = await LoadAsync(db, id, ct);
            if (error is not null) return error;

            if (request.Percent is < 0 or > 300)
                return Results.BadRequest(new { message = "Seasonal adjust must be between 0% and 300%." });

            return await DeviceAsync(async () =>
            {
                await controllers.Connect(record!).Client.SetWaterBudgetAsync(
                    request.Program, request.Percent, ct);
                return Results.Ok(new { program = request.Program, percent = request.Percent });
            });
        });

        group.MapPut("/enabled", async (
            int id, ControllerEnabledRequest request,
            AppDbContext db, ControllerService controllers, CancellationToken ct) =>
        {
            var (record, error) = await LoadAsync(db, id, ct);
            if (error is not null) return error;

            return await DeviceAsync(async () =>
            {
                await controllers.Connect(record!).Client.SetControllerEnabledAsync(request.Enabled, ct);
                return Results.Ok(new { enabled = request.Enabled });
            });
        });

        group.MapPut("/clock", async (
            int id, SetClockRequest request,
            AppDbContext db, ControllerService controllers, CancellationToken ct) =>
        {
            var (record, error) = await LoadAsync(db, id, ct);
            if (error is not null) return error;

            return await DeviceAsync(async () =>
            {
                var target = request.UseServerTime || request.Value is null
                    ? LocalNow(record!.TimeZoneId)
                    : request.Value.Value;

                var client = controllers.Connect(record!).Client;
                await client.SetControllerDateAsync(DateOnly.FromDateTime(target.DateTime), ct);
                await client.SetControllerTimeAsync(TimeOnly.FromDateTime(target.DateTime), ct);

                return Results.Ok(new { synced = target });
            });
        });
    }

    private static DateTimeOffset LocalNow(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.ConvertTime(
                DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return DateTimeOffset.Now;
        }
    }

    // ------------------------------------------------------------- programs

    private static void MapPrograms(RouteGroupBuilder api)
    {
        var group = api.MapGroup("/controllers/{id:int}/programs");

        group.MapGet("/", async (
            int id, AppDbContext db, ControllerService controllers, CancellationToken ct) =>
        {
            var (record, error) = await LoadAsync(db, id, ct);
            if (error is not null) return error;

            var connection = controllers.Connect(record!);

            // Serve what was last read, if it is recent. This is a SIP exchange per
            // program on a device that answers one request at a time, which made it
            // the slowest thing the app asked for and something every screen load
            // waited on. Writing a program clears this, so the only staleness left is
            // somebody editing at the panel on the wall.
            if (connection.TryGetFreshPrograms(ProgramFreshness, out var cachedPrograms))
                return Results.Ok(cachedPrograms.Select(ProgramDto.From).ToList());

            return await DeviceAsync(async () =>
            {
                var schedule = await ScheduleForAsync(controllers, record!, ct);
                if (schedule is null)
                    return Results.Ok(Array.Empty<ProgramDto>());

                var programs = await schedule.GetAllProgramsAsync(ct);
                connection.RememberPrograms(programs);

                return Results.Ok(programs.Select(ProgramDto.From).ToList());
            });
        });

        group.MapGet("/{program:int}", async (
            int id, int program, AppDbContext db, ControllerService controllers, CancellationToken ct) =>
        {
            var (record, error) = await LoadAsync(db, id, ct);
            if (error is not null) return error;

            return await DeviceAsync(async () =>
            {
                var schedule = await ScheduleForAsync(controllers, record!, ct);
                if (schedule is null)
                    return Results.BadRequest(new { message = "This controller model does not use programs." });

                return Results.Ok(ProgramDto.From(await schedule.GetProgramAsync(program, ct)));
            });
        });

        group.MapPut("/{program:int}", async (
            int id, int program, SaveProgramRequest request,
            AppDbContext db, ControllerService controllers, CancellationToken ct) =>
        {
            var (record, error) = await LoadAsync(db, id, ct);
            if (error is not null) return error;

            return await DeviceAsync(async () =>
            {
                var schedule = await ScheduleForAsync(controllers, record!, ct);
                if (schedule is null)
                    return Results.BadRequest(new { message = "This controller model does not use programs." });

                var existing = await schedule.GetProgramAsync(program, ct);

                await schedule.SaveProgramAsync(existing with
                {
                    Frequency = request.Frequency,
                    CustomDays = request.CustomDays,
                    CyclicDays = request.CyclicDays,
                    SeasonalAdjustPercent = request.SeasonalAdjustPercent,
                    StartTimes = request.StartTimes,
                    StationRunTimes = request.StationRunTimes,
                }, ct);

                // The cached copy now describes the program as it was before this edit.
                controllers.Connect(record!).InvalidatePrograms();

                return Results.Ok(ProgramDto.From(await schedule.GetProgramAsync(program, ct)));
            });
        });

        group.MapPost("/{program:int}/run", async (
            int id, int program, AppDbContext db, ControllerService controllers,
            PlanExecutionService plans, CancellationToken ct) =>
        {
            var (record, error) = await LoadAsync(db, id, ct);
            if (error is not null) return error;

            // The controller's own program and an app plan watering the same zones at
            // once is the exact situation the plan engine exists to avoid.
            await plans.CancelAsync(id, "A controller program was started by hand.", ct);

            return await DeviceAsync(async () =>
            {
                var connection = controllers.Connect(record!);
                await connection.Client.RunProgramAsync(program, ct);
                connection.NoteCommandedRun(null, RunTrigger.Program, TimeSpan.FromHours(4));
                return Results.Accepted();
            });
        });
    }

    // -------------------------------------------------------------- history

    private static void MapHistory(RouteGroupBuilder api)
    {
        var group = api.MapGroup("/controllers/{id:int}");

        group.MapGet("/history", async (
            int id, DateTimeOffset? from, DateTimeOffset? to,
            AppDbContext db, CancellationToken ct) =>
        {
            var start = from ?? DateTimeOffset.UtcNow.AddDays(-30);
            var end = to ?? DateTimeOffset.UtcNow.AddDays(1);

            var runs = await db.Runs
                .Where(r => r.ControllerId == id && r.StartedUtc >= start && r.StartedUtc <= end)
                .OrderByDescending(r => r.StartedUtc)
                .ToListAsync(ct);

            var zoneNames = await db.Zones
                .Where(z => z.ControllerId == id)
                .ToDictionaryAsync(z => z.StationNumber, z => z.Name, ct);

            return Results.Ok(runs.Select(r => new RunDto(
                r.Id,
                r.StationNumber,
                zoneNames.GetValueOrDefault(r.StationNumber, $"Zone {r.StationNumber}"),
                r.StartedUtc,
                r.EndedUtc,
                r.DurationSeconds,
                r.Trigger,
                r.EstimatedGallons)).ToList());
        });

        group.MapGet("/calendar", async (
            int id, int? year, int? month, AppDbContext db, CancellationToken ct) =>
        {
            var now = DateTime.UtcNow;
            var targetYear = year ?? now.Year;
            var targetMonth = month ?? now.Month;

            var start = new DateTimeOffset(new DateTime(targetYear, targetMonth, 1), TimeSpan.Zero);
            var end = start.AddMonths(1);

            var runs = await db.Runs
                .Where(r => r.ControllerId == id && r.StartedUtc >= start && r.StartedUtc < end)
                .ToListAsync(ct);

            var skips = await db.SkipEvents
                .Where(s => s.ControllerId == id
                            && s.Date >= DateOnly.FromDateTime(start.DateTime)
                            && s.Date < DateOnly.FromDateTime(end.DateTime))
                .ToListAsync(ct);

            var days = runs
                .GroupBy(r => DateOnly.FromDateTime(r.StartedUtc.UtcDateTime))
                .Select(g => new CalendarDayDto(
                    g.Key.ToString("yyyy-MM-dd"),
                    g.Count(),
                    g.Sum(r => r.DurationSeconds) / 60,
                    Math.Round(g.Sum(r => r.EstimatedGallons), 1),
                    skips.FirstOrDefault(s => s.Date == g.Key)?.Reason.ToString(),
                    Scheduled: false))
                .ToList();

            // Days with a skip but no runs still need a cell.
            foreach (var skip in skips.Where(s => days.All(d => d.Date != s.Date.ToString("yyyy-MM-dd"))))
                days.Add(new CalendarDayDto(
                    skip.Date.ToString("yyyy-MM-dd"), 0, 0, 0, skip.Reason.ToString(), Scheduled: false));

            return Results.Ok(days.OrderBy(d => d.Date).ToList());
        });

        group.MapGet("/usage", async (
            int id, int? year, int? month, AppDbContext db, CancellationToken ct) =>
        {
            var now = DateTime.UtcNow;
            var targetYear = year ?? now.Year;
            var targetMonth = month ?? now.Month;

            var start = new DateTimeOffset(new DateTime(targetYear, targetMonth, 1), TimeSpan.Zero);
            var end = start.AddMonths(1);

            var runs = await db.Runs
                .Where(r => r.ControllerId == id && r.StartedUtc >= start && r.StartedUtc < end)
                .ToListAsync(ct);

            var zoneNames = await db.Zones
                .Where(z => z.ControllerId == id)
                .ToDictionaryAsync(z => z.StationNumber, z => z.Name, ct);

            var byZone = runs
                .GroupBy(r => r.StationNumber)
                .Select(g => new ZoneUsageDto(
                    g.Key,
                    zoneNames.GetValueOrDefault(g.Key, $"Zone {g.Key}"),
                    Math.Round(g.Sum(r => r.EstimatedGallons), 1),
                    g.Sum(r => r.DurationSeconds) / 60))
                .OrderByDescending(z => z.Gallons)
                .ToList();

            return Results.Ok(new UsageDto(
                start.ToString("yyyy-MM"),
                Math.Round(runs.Sum(r => r.EstimatedGallons), 1),
                runs.Sum(r => r.DurationSeconds) / 60,
                runs.Count,
                byZone));
        });
    }

    // -------------------------------------------------------------- weather

    private static void MapWeather(RouteGroupBuilder api)
    {
        var group = api.MapGroup("/controllers/{id:int}");

        group.MapGet("/weather", async (
            int id, AppDbContext db, WeatherService weather, CancellationToken ct) =>
        {
            var (record, error) = await LoadAsync(db, id, ct);
            if (error is not null) return error;

            var forecast = await weather.GetForecastAsync(db, record!, ct);
            if (forecast.Count == 0) return Results.Ok(Array.Empty<WeatherDayDto>());

            // Bounded to the days actually being returned. Both of these used to load
            // the entire history of the installation to answer a question about a
            // twelve-day window, so the cost of rendering the forecast strip grew for
            // ever even though the answer never got bigger.
            var first = forecast.Min(day => day.Date);
            var last = forecast.Max(day => day.Date);

            var skips = await db.SkipEvents
                .Where(s => s.ControllerId == id && s.Date >= first && s.Date <= last)
                .ToListAsync(ct);

            var windowStart = first.ToDateTime(TimeOnly.MinValue);
            var windowEnd = last.ToDateTime(TimeOnly.MaxValue);

            var runDates = await db.Runs
                .Where(r => r.ControllerId == id
                    && r.StartedUtc >= new DateTimeOffset(windowStart, TimeSpan.Zero)
                    && r.StartedUtc <= new DateTimeOffset(windowEnd, TimeSpan.Zero))
                .Select(r => r.StartedUtc)
                .ToListAsync(ct);

            var daysWithRuns = runDates.Select(d => DateOnly.FromDateTime(d.UtcDateTime)).ToHashSet();

            return Results.Ok(forecast
                .Select(day => WeatherDayDto.From(
                    day,
                    daysWithRuns.Contains(day.Date),
                    skips.FirstOrDefault(s => s.Date == day.Date)))
                .ToList());
        });

        group.MapGet("/skips", async (int id, AppDbContext db, CancellationToken ct) =>
        {
            var skips = await db.SkipEvents
                .Where(s => s.ControllerId == id)
                .OrderByDescending(s => s.Date)
                .Take(60)
                .ToListAsync(ct);

            return Results.Ok(skips.Select(s => new
            {
                date = s.Date.ToString("yyyy-MM-dd"),
                reason = s.Reason.ToString(),
                s.Details,
            }).ToList());
        });

        group.MapPost("/evaluate-skip", async (
            int id,
            AppDbContext db,
            ControllerService controllers,
            WeatherService weather,
            SkipEvaluator evaluator,
            SettingsService settings,
            CancellationToken ct) =>
        {
            var (record, error) = await LoadAsync(db, id, ct);
            if (error is not null) return error;

            var forecast = await weather.GetForecastAsync(db, record!, ct);
            var skipSettings = await settings.GetSkipSettingsAsync(ct);
            var today = DateOnly.FromDateTime(LocalNow(record!.TimeZoneId).DateTime);

            return await DeviceAsync(async () =>
            {
                var connection = controllers.Connect(record);
                var decision = await evaluator.ApplyAsync(
                    db, record, connection, forecast, skipSettings, today, ct);

                return Results.Ok(new
                {
                    shouldSkip = decision.ShouldSkip,
                    reason = decision.Reason?.ToString(),
                    decision.Details,
                });
            });
        });
    }

    // ------------------------------------------------------------- settings

    private static void MapSettings(RouteGroupBuilder api)
    {
        var group = api.MapGroup("/settings");

        group.MapGet("/skip", async (SettingsService settings, CancellationToken ct) =>
            Results.Ok(await settings.GetSkipSettingsAsync(ct)));

        group.MapPut("/skip", async (
            SkipSettings request, SettingsService settings, CancellationToken ct) =>
        {
            await settings.SetSkipSettingsAsync(request, ct);
            return Results.Ok(request);
        });

        group.MapGet("/units", async (SettingsService settings, CancellationToken ct) =>
            Results.Ok(await settings.GetUnitsAsync(ct)));

        group.MapPut("/units", async (
            UnitPreferences request, SettingsService settings, CancellationToken ct) =>
        {
            await settings.SetUnitsAsync(request, ct);
            return Results.Ok(request);
        });

        group.MapGet("/timezones", () => Results.Ok(
            TimeZoneInfo.GetSystemTimeZones()
                .Select(tz => new { id = tz.Id, name = tz.DisplayName })
                .ToList()));

        // Takes a postcode or a place name, because nobody knows their latitude. Comes
        // back with the time zone too, which saves picking it out of four hundred.
        group.MapGet("/places", async (
            string? q, GeocodingService geocoding, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q)) return Results.Ok(Array.Empty<object>());

            var places = await geocoding.SearchAsync(q, ct: ct);

            return Results.Ok(places.Select(place => new
            {
                label = GeocodingService.Describe(place),
                latitude = place.Latitude,
                longitude = place.Longitude,
                timeZoneId = place.TimeZoneId,
            }));
        });
    }

    // ---------------------------------------------------------- diagnostics

    private static void MapDiagnostics(RouteGroupBuilder api)
    {
        api.MapGet("/controllers/{id:int}/diagnostics", (int id, ControllerRegistry registry) =>
        {
            var connection = registry.Find(id);
            if (connection is null)
                return Results.Ok(Array.Empty<SipExchangeDto>());

            return Results.Ok(connection.RecentExchanges()
                .Select(e => new SipExchangeDto(e.At, e.Method, e.RequestHex, e.ResponseHex, e.Error))
                .ToList());
        });

        api.MapPost("/controllers/{id:int}/diagnostics/raw", async (
            int id, RawSipRequest request,
            AppDbContext db, ControllerService controllers, CancellationToken ct) =>
        {
            var (record, error) = await LoadAsync(db, id, ct);
            if (error is not null) return error;

            if (string.IsNullOrWhiteSpace(request.Hex))
                return Results.BadRequest(new { message = "A hex command is required." });

            return await DeviceAsync(async () =>
            {
                var response = await controllers.Connect(record!).Client
                    .TunnelAsync(request.Hex.Trim().ToUpperInvariant(), ct);

                return Results.Ok(new { response.Name, response.Hex, response.Fields });
            });
        });
    }
}

/// <summary>Lets the diagnostics panel send an arbitrary SIP command.</summary>
public sealed record RawSipRequest(string Hex);
