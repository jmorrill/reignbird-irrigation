using System.ComponentModel.DataAnnotations;
using RainBird.Protocol;

namespace RainBird.Server.Data;

/// <summary>A controller we know about. Credentials are encrypted at rest.</summary>
public class ControllerRecord
{
    public int Id { get; set; }

    [MaxLength(120)]
    public string Name { get; set; } = "My Controller";

    /// <summary>Host or host:port on the LAN.</summary>
    [MaxLength(200)]
    public string Host { get; set; } = "";

    /// <summary>Device password, protected with ASP.NET Core Data Protection.</summary>
    public string ProtectedPassword { get; set; } = "";

    /// <summary>
    /// True when the controller serves the protocol over TLS on 443. Newer LNK
    /// firmware does; older units listen on plain HTTP port 80. Detected when the
    /// controller is added.
    /// </summary>
    public bool UseHttps { get; set; }

    [MaxLength(8)]
    public string ModelId { get; set; } = "";

    [MaxLength(40)]
    public string SerialNumber { get; set; } = "";

    [MaxLength(20)]
    public string FirmwareVersion { get; set; } = "";

    /// <summary>Serialised <see cref="ControllerCapabilities"/>, cached from the last probe.</summary>
    public string CapabilitiesJson { get; set; } = "{}";

    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    /// <summary>Used to label weather and usage; defaults to the server's zone.</summary>
    [MaxLength(80)]
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;

    public DateTimeOffset? LastSeenUtc { get; set; }
    public DateTimeOffset AddedUtc { get; set; } = DateTimeOffset.UtcNow;

    public List<ZoneRecord> Zones { get; set; } = [];
}

/// <summary>
/// Per-zone metadata. The controller has nowhere to store any of this, so it lives
/// here — the role a cloud service would otherwise play, kept local.
/// </summary>
public class ZoneRecord
{
    public int Id { get; set; }
    public int ControllerId { get; set; }
    public ControllerRecord? Controller { get; set; }

    /// <summary>1-based station number on the controller.</summary>
    public int StationNumber { get; set; }

    [MaxLength(120)]
    public string Name { get; set; } = "";

    /// <summary>Relative path of the zone photo under the server's media directory.</summary>
    [MaxLength(300)]
    public string? PhotoPath { get; set; }

    public PlantType PlantType { get; set; } = PlantType.CoolSeasonGrass;
    public SoilType SoilType { get; set; } = SoilType.Loam;
    public SunExposure SunExposure { get; set; } = SunExposure.FullSun;
    public SlopeGrade Slope { get; set; } = SlopeGrade.Flat;
    public SprinklerType SprinklerType { get; set; } = SprinklerType.FixedSpray;

    /// <summary>
    /// Nozzle output in gallons per minute, used to estimate water usage. Defaults to
    /// a typical fixed-spray head; the user can correct it per zone.
    /// </summary>
    public double NozzleFlowGpm { get; set; } = 1.5;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// True when this zone was switched off because the controller stopped reporting
    /// its station, rather than because the user turned it off.
    ///
    /// Without the distinction, a station that comes back � a module refitted, or a
    /// mis-read station mask corrected � would stay invisible forever, while
    /// re-enabling everything on each probe would keep overriding a zone the user
    /// deliberately disabled.
    /// </summary>
    public bool AutoDisabled { get; set; }

    /// <summary>Display order, so the user can arrange zones independently of station number.</summary>
    public int SortOrder { get; set; }
}

public enum PlantType { CoolSeasonGrass, WarmSeasonGrass, Shrubs, Trees, Flowers, GroundCover, Garden, Xeriscape }
public enum SoilType { Clay, Loam, Sand, Silt, ClayLoam, SandyLoam }
public enum SunExposure { FullSun, PartialShade, FullShade }
public enum SlopeGrade { Flat, Slight, Moderate, Steep }
public enum SprinklerType { FixedSpray, Rotor, RotaryNozzle, Drip, Bubbler, Emitter }

/// <summary>
/// A completed watering run, observed by the polling loop. The controller does not
/// retain history, so this table is the only record that a run happened.
/// </summary>
public class RunRecord
{
    public long Id { get; set; }
    public int ControllerId { get; set; }
    public int StationNumber { get; set; }

    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? EndedUtc { get; set; }
    public int DurationSeconds { get; set; }

    public RunTrigger Trigger { get; set; }

    /// <summary>Estimated from duration and the zone's nozzle flow rate.</summary>
    public double EstimatedGallons { get; set; }
}

/// <summary>A watering day our skip logic suppressed, and why.</summary>
public class SkipEventRecord
{
    public long Id { get; set; }
    public int ControllerId { get; set; }
    public DateOnly Date { get; set; }
    public SkipReason Reason { get; set; }

    [MaxLength(300)]
    public string Details { get; set; } = "";

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public enum SkipReason { Rain, Freeze, Wind, Saturation, Manual }

/// <summary>One day of cached forecast for a controller's location.</summary>
public class WeatherDayRecord
{
    public long Id { get; set; }
    public int ControllerId { get; set; }
    public DateOnly Date { get; set; }

    public double TempHighC { get; set; }
    public double TempLowC { get; set; }
    public double PrecipitationMm { get; set; }
    public int PrecipitationProbability { get; set; }
    public double WindKph { get; set; }

    /// <summary>WMO weather code, as returned by Open-Meteo.</summary>
    public int ConditionCode { get; set; }

    public DateTimeOffset FetchedUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Free-form key/value settings.</summary>
public class SettingRecord
{
    [MaxLength(100)]
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>
/// Someone who may sign in. Every account is equal: anyone signed in can water,
/// schedule, and add or remove other accounts.
/// </summary>
public class UserRecord
{
    public int Id { get; set; }

    /// <summary>Compared case-insensitively, so "Sam" and "sam" are the same person.</summary>
    [MaxLength(64)]
    public string Username { get; set; } = "";

    /// <summary>
    /// PBKDF2 via ASP.NET Core's own password hasher, which carries its salt,
    /// iteration count and format version inside the string. Never the password.
    /// </summary>
    public string PasswordHash { get; set; } = "";

    /// <summary>
    /// Changes whenever the account's credentials do. Every token carries the stamp
    /// it was issued under, so changing a password or deleting an account takes
    /// effect immediately instead of whenever the last token happens to expire.
    /// </summary>
    public Guid SecurityStamp { get; set; } = Guid.NewGuid();

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSignInUtc { get; set; }
}
