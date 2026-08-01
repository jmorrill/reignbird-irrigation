using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using RainBird.Protocol;
using RainBird.Server.Data;
using RainBird.Simulator;

namespace RainBird.Server.Services;

/// <summary>
/// Hands out in-process virtual controllers instead of HTTP transports.
///
/// Enabled with <c>RainBird:UseSimulator</c>. This is what lets the app be run,
/// demonstrated and tested end to end without a sprinkler controller present.
/// </summary>
public sealed class SimulatorTransportFactory : IControllerTransportFactory
{
    private readonly ConcurrentDictionary<string, VirtualController> _controllers = new();

    /// <summary>The virtual controllers created so far, keyed by host.</summary>
    public IReadOnlyDictionary<string, VirtualController> Controllers => _controllers;

    public IRainBirdTransport Create(string host, string password, bool useHttps)
    {
        _ = useHttps; // The simulator is in-process; the scheme is irrelevant.
        var controller = _controllers.GetOrAdd(host, _ => new VirtualController(stationCount: 8));

        // A little latency keeps the UI honest: loading states have to actually work.
        return new SimulatorTransport(controller, password)
        {
            Latency = TimeSpan.FromMilliseconds(40),
        };
    }
}

/// <summary>
/// Puts a controller, named zones and a plausible watering history in place the first
/// time the app starts in simulator mode, so every screen has something real to show.
/// </summary>
public static class SimulatorSeed
{
    public const string SimulatedHost = "192.168.1.50";
    public const string SimulatedPassword = "simulator";

    private static readonly (string Name, PlantType Plant, SoilType Soil, SunExposure Sun, SprinklerType Head, double Gpm)[] ZoneSeeds =
    [
        ("Front Lawn",       PlantType.CoolSeasonGrass, SoilType.Loam,      SunExposure.FullSun,      SprinklerType.Rotor,        2.4),
        ("Back Lawn",        PlantType.CoolSeasonGrass, SoilType.ClayLoam,  SunExposure.FullSun,      SprinklerType.Rotor,        2.6),
        ("Side Strip",       PlantType.WarmSeasonGrass, SoilType.SandyLoam, SunExposure.PartialShade, SprinklerType.FixedSpray,   1.4),
        ("Rose Bed",         PlantType.Flowers,         SoilType.Loam,      SunExposure.FullSun,      SprinklerType.Drip,         0.6),
        ("Vegetable Garden", PlantType.Garden,          SoilType.Loam,      SunExposure.FullSun,      SprinklerType.Drip,         0.8),
        ("Front Shrubs",     PlantType.Shrubs,          SoilType.Clay,      SunExposure.PartialShade, SprinklerType.RotaryNozzle, 1.1),
        ("Patio Planters",   PlantType.Flowers,         SoilType.Loam,      SunExposure.FullShade,    SprinklerType.Emitter,      0.4),
        ("Parkway Trees",    PlantType.Trees,           SoilType.SandyLoam, SunExposure.FullSun,      SprinklerType.Bubbler,      1.8),
    ];

    public static async Task EnsureSeededAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        if (await db.Controllers.AnyAsync()) return;

        var controllers = services.GetRequiredService<ControllerService>();

        var record = new ControllerRecord
        {
            Name = "Backyard Controller",
            Host = SimulatedHost,
            ProtectedPassword = controllers.Protect(SimulatedPassword),
            // Denver: a climate where seasonal adjust and freeze skips are meaningful.
            Latitude = 39.7392,
            Longitude = -104.9903,
            TimeZoneId = TimeZoneInfo.Local.Id,
        };

        db.Controllers.Add(record);
        await db.SaveChangesAsync();

        await controllers.RefreshCapabilitiesAsync(record);

        await NameZonesAsync(db, record.Id);
        await SeedHistoryAsync(db, record.Id);
    }

    private static async Task NameZonesAsync(AppDbContext db, int controllerId)
    {
        var zones = await db.Zones
            .Where(z => z.ControllerId == controllerId)
            .OrderBy(z => z.StationNumber)
            .ToListAsync();

        for (var i = 0; i < zones.Count && i < ZoneSeeds.Length; i++)
        {
            var (name, plant, soil, sun, head, gpm) = ZoneSeeds[i];
            zones[i].Name = name;
            zones[i].PlantType = plant;
            zones[i].SoilType = soil;
            zones[i].SunExposure = sun;
            zones[i].SprinklerType = head;
            zones[i].NozzleFlowGpm = gpm;
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Six weeks of watering on a Mon/Wed/Fri pattern, so the calendar, history and
    /// usage screens have something meaningful in them from the first launch.
    /// </summary>
    private static async Task SeedHistoryAsync(AppDbContext db, int controllerId)
    {
        var zones = await db.Zones
            .Where(z => z.ControllerId == controllerId)
            .OrderBy(z => z.StationNumber)
            .ToListAsync();

        if (zones.Count == 0) return;

        // Deterministic, so repeated fresh starts produce the same demo data.
        var random = new Random(20260731);
        var runs = new List<RunRecord>();
        var today = DateTime.UtcNow.Date;

        for (var dayOffset = 42; dayOffset >= 1; dayOffset--)
        {
            var date = today.AddDays(-dayOffset);
            if (date.DayOfWeek is not (DayOfWeek.Monday or DayOfWeek.Wednesday or DayOfWeek.Friday))
                continue;

            // The occasional missed day, as would happen after a rain skip.
            if (random.NextDouble() < 0.15) continue;

            // 5:15am *local*, so the demo history reads as early-morning watering
            // rather than drifting across midnight in the viewer's timezone.
            var localStart = new DateTime(date.Year, date.Month, date.Day, 5, 15, 0, DateTimeKind.Unspecified);
            var start = new DateTimeOffset(localStart, TimeZoneInfo.Local.GetUtcOffset(localStart));

            foreach (var zone in zones)
            {
                var minutes = zone.SprinklerType switch
                {
                    SprinklerType.Rotor => random.Next(18, 26),
                    SprinklerType.Drip or SprinklerType.Emitter => random.Next(25, 40),
                    SprinklerType.Bubbler => random.Next(12, 18),
                    _ => random.Next(8, 14),
                };

                var duration = minutes * 60;

                runs.Add(new RunRecord
                {
                    ControllerId = controllerId,
                    StationNumber = zone.StationNumber,
                    StartedUtc = start,
                    EndedUtc = start.AddSeconds(duration),
                    DurationSeconds = duration,
                    Trigger = RunTrigger.Program,
                    EstimatedGallons = UsageEstimator.Gallons(duration, zone.NozzleFlowGpm),
                });

                start = start.AddSeconds(duration + 30);
            }
        }

        db.Runs.AddRange(runs);

        db.SkipEvents.AddRange(
            new SkipEventRecord
            {
                ControllerId = controllerId,
                Date = DateOnly.FromDateTime(today.AddDays(-9)),
                Reason = SkipReason.Rain,
                Details = "8.4 mm of rain forecast, at or above the 3 mm threshold.",
            },
            new SkipEventRecord
            {
                ControllerId = controllerId,
                Date = DateOnly.FromDateTime(today.AddDays(-4)),
                Reason = SkipReason.Wind,
                Details = "Winds to 41 km/h would blow most of the water off target.",
            });

        await db.SaveChangesAsync();
    }
}
