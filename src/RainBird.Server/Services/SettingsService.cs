using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RainBird.Server.Data;

namespace RainBird.Server.Services;

/// <summary>Typed access to the key/value settings table.</summary>
public sealed class SettingsService
{
    private const string SkipSettingsKey = "skip-settings";
    private const string UnitsKey = "units";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AppDbContext _db;

    public SettingsService(AppDbContext db) => _db = db;

    public async Task<SkipSettings> GetSkipSettingsAsync(CancellationToken ct = default)
    {
        var stored = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == SkipSettingsKey, ct);
        if (stored is null) return new SkipSettings();

        try
        {
            return JsonSerializer.Deserialize<SkipSettings>(stored.Value, JsonOptions) ?? new SkipSettings();
        }
        catch (JsonException)
        {
            // A settings row we can't read shouldn't break watering; fall back to defaults.
            return new SkipSettings();
        }
    }

    public Task SetSkipSettingsAsync(SkipSettings settings, CancellationToken ct = default) =>
        UpsertAsync(SkipSettingsKey, JsonSerializer.Serialize(settings, JsonOptions), ct);

    public async Task<UnitPreferences> GetUnitsAsync(CancellationToken ct = default)
    {
        var stored = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == UnitsKey, ct);
        if (stored is null) return new UnitPreferences();

        try
        {
            return JsonSerializer.Deserialize<UnitPreferences>(stored.Value, JsonOptions) ?? new UnitPreferences();
        }
        catch (JsonException)
        {
            return new UnitPreferences();
        }
    }

    public Task SetUnitsAsync(UnitPreferences units, CancellationToken ct = default) =>
        UpsertAsync(UnitsKey, JsonSerializer.Serialize(units, JsonOptions), ct);

    private async Task UpsertAsync(string key, string value, CancellationToken ct)
    {
        var existing = await _db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (existing is null)
            _db.Settings.Add(new SettingRecord { Key = key, Value = value });
        else
            existing.Value = value;

        await _db.SaveChangesAsync(ct);
    }
}

/// <summary>Display units: the US/metric split, applied to temperature and water use.</summary>
public sealed record UnitPreferences
{
    public bool UseMetric { get; init; }

    /// <summary>Show usage as volume rather than minutes.</summary>
    public bool ShowVolume { get; init; } = true;
}
