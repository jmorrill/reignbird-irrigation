using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RainBird.Server.Data;

namespace RainBird.Server.Services;

/// <summary>
/// Fetches the forecast for a controller's location.
///
/// Uses Open-Meteo: no API key, no account, free for non-commercial use. That matters
/// for a local-first app — requiring the user to register for a weather key to see a
/// five-day strip would undercut the whole point.
/// </summary>
public sealed class WeatherService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(1);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WeatherService> _logger;

    public WeatherService(IHttpClientFactory httpClientFactory, ILogger<WeatherService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Returns cached forecast days, refreshing from the API when the cache is stale.
    /// Falls back to whatever is cached if the network call fails — a stale forecast is
    /// far better than an empty screen.
    /// </summary>
    public async Task<IReadOnlyList<WeatherDayRecord>> GetForecastAsync(
        AppDbContext db, ControllerRecord controller, CancellationToken ct = default)
    {
        var cached = await db.WeatherDays
            .Where(w => w.ControllerId == controller.Id)
            .OrderBy(w => w.Date)
            .ToListAsync(ct);

        if (controller.Latitude is null || controller.Longitude is null)
            return cached;

        var newest = cached.Count == 0 ? DateTimeOffset.MinValue : cached.Max(w => w.FetchedUtc);
        if (DateTimeOffset.UtcNow - newest < CacheLifetime)
            return cached;

        try
        {
            var fetched = await FetchAsync(controller.Latitude.Value, controller.Longitude.Value, ct);
            return await MergeAsync(db, controller.Id, fetched, cached, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Weather fetch failed for controller {Id}; serving cached data", controller.Id);
            return cached;
        }
    }

    private async Task<List<WeatherDayRecord>> FetchAsync(double latitude, double longitude, CancellationToken ct)
    {
        var url =
            "https://api.open-meteo.com/v1/forecast" +
            $"?latitude={latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
            $"&longitude={longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
            "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_sum," +
            "precipitation_probability_max,wind_speed_10m_max" +
            "&past_days=5&forecast_days=7&timezone=auto";

        var http = _httpClientFactory.CreateClient("weather");
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var daily = document.RootElement.GetProperty("daily");
        var dates = daily.GetProperty("time");

        var days = new List<WeatherDayRecord>(dates.GetArrayLength());
        for (var i = 0; i < dates.GetArrayLength(); i++)
        {
            days.Add(new WeatherDayRecord
            {
                Date = DateOnly.Parse(dates[i].GetString()!),
                ConditionCode = ReadInt(daily, "weather_code", i),
                TempHighC = ReadDouble(daily, "temperature_2m_max", i),
                TempLowC = ReadDouble(daily, "temperature_2m_min", i),
                PrecipitationMm = ReadDouble(daily, "precipitation_sum", i),
                PrecipitationProbability = ReadInt(daily, "precipitation_probability_max", i),
                WindKph = ReadDouble(daily, "wind_speed_10m_max", i),
                FetchedUtc = DateTimeOffset.UtcNow,
            });
        }

        return days;
    }

    private static double ReadDouble(JsonElement daily, string field, int index) =>
        daily.TryGetProperty(field, out var array) && array[index].ValueKind == JsonValueKind.Number
            ? array[index].GetDouble()
            : 0;

    private static int ReadInt(JsonElement daily, string field, int index) =>
        daily.TryGetProperty(field, out var array) && array[index].ValueKind == JsonValueKind.Number
            ? array[index].GetInt32()
            : 0;

    private static async Task<List<WeatherDayRecord>> MergeAsync(
        AppDbContext db,
        int controllerId,
        List<WeatherDayRecord> fetched,
        List<WeatherDayRecord> cached,
        CancellationToken ct)
    {
        foreach (var day in fetched)
        {
            var existing = cached.FirstOrDefault(w => w.Date == day.Date);
            if (existing is null)
            {
                day.ControllerId = controllerId;
                db.WeatherDays.Add(day);
                cached.Add(day);
                continue;
            }

            var tracked = await db.WeatherDays.FirstAsync(w => w.Id == existing.Id, ct);
            tracked.ConditionCode = day.ConditionCode;
            tracked.TempHighC = day.TempHighC;
            tracked.TempLowC = day.TempLowC;
            tracked.PrecipitationMm = day.PrecipitationMm;
            tracked.PrecipitationProbability = day.PrecipitationProbability;
            tracked.WindKph = day.WindKph;
            tracked.FetchedUtc = day.FetchedUtc;
        }

        // Keep the table bounded; nothing looks further back than the history view.
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30));
        var stale = await db.WeatherDays
            .Where(w => w.ControllerId == controllerId && w.Date < cutoff)
            .ToListAsync(ct);
        db.WeatherDays.RemoveRange(stale);

        await db.SaveChangesAsync(ct);
        return cached.Where(w => w.Date >= cutoff).OrderBy(w => w.Date).ToList();
    }

    /// <summary>Maps a WMO weather code to a small set the UI draws icons for.</summary>
    public static string ConditionOf(int wmoCode) => wmoCode switch
    {
        0 => "clear",
        1 or 2 => "partly-cloudy",
        3 => "cloudy",
        45 or 48 => "fog",
        51 or 53 or 55 or 56 or 57 => "drizzle",
        61 or 63 or 65 or 66 or 67 => "rain",
        71 or 73 or 75 or 77 => "snow",
        80 or 81 or 82 => "showers",
        85 or 86 => "snow-showers",
        95 or 96 or 99 => "thunderstorm",
        _ => "cloudy",
    };
}
