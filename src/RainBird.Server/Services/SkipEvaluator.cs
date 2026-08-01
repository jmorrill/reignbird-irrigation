using Microsoft.EntityFrameworkCore;
using RainBird.Protocol;
using RainBird.Server.Data;

namespace RainBird.Server.Services;

/// <summary>Thresholds for the weather skips. Editable from the settings screen.</summary>
public sealed record SkipSettings
{
    public bool RainSkipEnabled { get; init; } = true;
    public bool FreezeSkipEnabled { get; init; } = true;
    public bool WindSkipEnabled { get; init; } = true;
    public bool SaturationSkipEnabled { get; init; } = true;

    /// <summary>Skip when today's forecast rain reaches this much.</summary>
    public double RainThresholdMm { get; init; } = 3.0;

    /// <summary>Skip when the forecast low is at or below this.</summary>
    public double FreezeThresholdC { get; init; } = 2.0;

    /// <summary>Skip when peak wind reaches this.</summary>
    public double WindThresholdKph { get; init; } = 32.0;

    /// <summary>Skip when the preceding few days have already delivered this much rain.</summary>
    public double SaturationThresholdMm { get; init; } = 12.0;

    public int SaturationLookbackDays { get; init; } = 3;
}

/// <summary>The verdict for one day.</summary>
public sealed record SkipDecision(bool ShouldSkip, SkipReason? Reason, string Details);

/// <summary>
/// Decides whether today's watering should be suppressed, and if so applies a
/// one-day rain delay to the controller.
///
/// Products in this category generally run this in a cloud service; here it runs on
/// your own machine, which means it keeps working when the internet doesn't — the
/// rules simply evaluate against whatever forecast is cached.
///
/// The decision logic is a pure function so it can be tested directly.
/// </summary>
public sealed class SkipEvaluator
{
    private readonly ILogger<SkipEvaluator> _logger;

    public SkipEvaluator(ILogger<SkipEvaluator> logger) => _logger = logger;

    /// <summary>
    /// Evaluates the rules against a forecast. Ordered by how strongly each reason
    /// justifies not watering: freeze is a safety issue, rain makes watering
    /// pointless, saturation means the soil cannot absorb more, wind wastes it.
    /// </summary>
    public static SkipDecision Evaluate(
        DateOnly today,
        IReadOnlyList<WeatherDayRecord> forecast,
        SkipSettings settings)
    {
        var day = forecast.FirstOrDefault(w => w.Date == today);
        if (day is null)
            return new SkipDecision(false, null, "No forecast available for today.");

        if (settings.FreezeSkipEnabled && day.TempLowC <= settings.FreezeThresholdC)
            return new SkipDecision(true, SkipReason.Freeze,
                $"Low of {day.TempLowC:0.#}°C is at or below the {settings.FreezeThresholdC:0.#}°C freeze threshold.");

        if (settings.RainSkipEnabled && day.PrecipitationMm >= settings.RainThresholdMm)
            return new SkipDecision(true, SkipReason.Rain,
                $"{day.PrecipitationMm:0.#} mm of rain forecast, at or above the {settings.RainThresholdMm:0.#} mm threshold.");

        if (settings.SaturationSkipEnabled)
        {
            var since = today.AddDays(-settings.SaturationLookbackDays);
            var recent = forecast
                .Where(w => w.Date >= since && w.Date < today)
                .Sum(w => w.PrecipitationMm);

            if (recent >= settings.SaturationThresholdMm)
                return new SkipDecision(true, SkipReason.Saturation,
                    $"{recent:0.#} mm of rain over the last {settings.SaturationLookbackDays} days has already saturated the soil.");
        }

        if (settings.WindSkipEnabled && day.WindKph >= settings.WindThresholdKph)
            return new SkipDecision(true, SkipReason.Wind,
                $"Winds to {day.WindKph:0.#} km/h would blow most of the water off target.");

        return new SkipDecision(false, null, "Conditions are suitable for watering.");
    }

    /// <summary>
    /// Applies the decision to a controller: records the skip and sets a one-day rain
    /// delay so the controller's own schedule does not fire.
    /// </summary>
    public async Task<SkipDecision> ApplyAsync(
        AppDbContext db,
        ControllerRecord record,
        ControllerConnection connection,
        IReadOnlyList<WeatherDayRecord> forecast,
        SkipSettings settings,
        DateOnly today,
        CancellationToken ct = default)
    {
        var decision = Evaluate(today, forecast, settings);
        if (!decision.ShouldSkip || decision.Reason is null) return decision;

        var alreadyRecorded = await db.SkipEvents
            .AnyAsync(s => s.ControllerId == record.Id && s.Date == today, ct);

        if (alreadyRecorded) return decision;

        db.SkipEvents.Add(new SkipEventRecord
        {
            ControllerId = record.Id,
            Date = today,
            Reason = decision.Reason.Value,
            Details = decision.Details,
        });

        try
        {
            // A one-day delay is the mechanism the hardware gives us. It expires on its
            // own, so a missed evaluation tomorrow cannot leave watering suppressed.
            await connection.Client.SetRainDelayAsync(1, ct);
            _logger.LogInformation(
                "Skipped watering on controller {Id}: {Reason} — {Details}",
                record.Id, decision.Reason, decision.Details);
        }
        catch (RainBirdProtocolException ex)
        {
            _logger.LogWarning(ex, "Could not apply a rain delay to controller {Id}", record.Id);
        }

        await db.SaveChangesAsync(ct);
        return decision;
    }
}

/// <summary>Runs the skip evaluation once a day, early, before schedules typically fire.</summary>
public sealed class SkipEvaluationService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SkipEvaluationService> _logger;

    public SkipEvaluationService(IServiceScopeFactory scopeFactory, ILogger<SkipEvaluationService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EvaluateAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Skip evaluation pass failed");
            }

            try { await Task.Delay(CheckInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task EvaluateAllAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var provider = scope.ServiceProvider;

        var db = provider.GetRequiredService<AppDbContext>();
        var controllers = provider.GetRequiredService<ControllerService>();
        var weather = provider.GetRequiredService<WeatherService>();
        var evaluator = provider.GetRequiredService<SkipEvaluator>();
        var settings = provider.GetRequiredService<SettingsService>();

        var skipSettings = await settings.GetSkipSettingsAsync(ct);

        foreach (var record in await db.Controllers.ToListAsync(ct))
        {
            if (record.Latitude is null || record.Longitude is null) continue;

            var today = TodayIn(record.TimeZoneId);

            // Only evaluate in the early morning, before schedules would normally run.
            var localHour = NowIn(record.TimeZoneId).Hour;
            if (localHour is < 3 or > 6) continue;

            var forecast = await weather.GetForecastAsync(db, record, ct);

            try
            {
                var connection = controllers.Connect(record);
                await evaluator.ApplyAsync(db, record, connection, forecast, skipSettings, today, ct);
            }
            catch (Exception ex) when (ex is RainBirdProtocolException or InvalidOperationException)
            {
                _logger.LogDebug(ex, "Skip evaluation could not reach controller {Id}", record.Id);
            }
        }
    }

    private static DateTimeOffset NowIn(string timeZoneId)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return DateTimeOffset.Now;
        }
    }

    private static DateOnly TodayIn(string timeZoneId) => DateOnly.FromDateTime(NowIn(timeZoneId).DateTime);
}
