using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RainBird.Protocol;
using RainBird.Server.Data;

namespace RainBird.Server.Services;

/// <summary>
/// Glue between the database and the live connections: unprotects credentials, opens
/// connections, probes capabilities and keeps the zone rows in step with the stations
/// the controller actually reports.
/// </summary>
public sealed class ControllerService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AppDbContext _db;
    private readonly ControllerRegistry _registry;
    private readonly IDataProtector _protector;
    private readonly ILogger<ControllerService> _logger;

    public ControllerService(
        AppDbContext db,
        ControllerRegistry registry,
        IDataProtectionProvider protectionProvider,
        ILogger<ControllerService> logger)
    {
        _db = db;
        _registry = registry;
        _protector = protectionProvider.CreateProtector("RainBird.ControllerPassword");
        _logger = logger;
    }

    public string Protect(string password) => _protector.Protect(password);

    public string Unprotect(string protectedPassword)
    {
        try
        {
            return _protector.Unprotect(protectedPassword);
        }
        catch (Exception ex)
        {
            // Data Protection keys were rotated or lost. The controller has to be
            // re-added; say so plainly rather than failing as a bad password.
            throw new InvalidOperationException(
                "Stored controller credentials could not be read. Remove and re-add the controller.", ex);
        }
    }

    /// <summary>Opens (or reuses) the connection for a stored controller.</summary>
    public ControllerConnection Connect(ControllerRecord record) =>
        _registry.GetOrCreate(record.Id, record.Host, Unprotect(record.ProtectedPassword), record.UseHttps);

    /// <summary>
    /// Probes a controller, trying both schemes.
    ///
    /// Older LNK modules serve the protocol on plain HTTP port 80; newer firmware
    /// serves it over TLS on 443. Nothing in the protocol announces which, so the only
    /// reliable way to tell is to try. The working scheme is stored so subsequent
    /// connections go straight to it.
    ///
    /// A rejected password is not a reason to try the other scheme — it means we
    /// found the controller — so that propagates immediately.
    /// </summary>
    public async Task<ControllerCapabilities> ProbeAsync(ControllerRecord record, CancellationToken ct = default)
    {
        var attempts = record.UseHttps ? new[] { true, false } : new[] { false, true };
        Exception? firstFailure = null;

        foreach (var useHttps in attempts)
        {
            record.UseHttps = useHttps;

            // Drop any cached connection so it is rebuilt against this scheme.
            _registry.Remove(record.Id);

            try
            {
                return await RefreshCapabilitiesAsync(record, ct);
            }
            catch (RainBirdAuthenticationException)
            {
                throw;
            }
            catch (RainBirdProtocolException ex)
            {
                _logger.LogDebug(
                    "Controller {Id} did not answer over {Scheme}", record.Id, useHttps ? "HTTPS" : "HTTP");
                firstFailure ??= ex;
            }
        }

        throw firstFailure!;
    }

    /// <summary>
    /// Probes a controller, persists what it reports, and makes sure there is a zone
    /// row for every station it exposes.
    /// </summary>
    public async Task<ControllerCapabilities> RefreshCapabilitiesAsync(
        ControllerRecord record, CancellationToken ct = default)
    {
        var connection = Connect(record);
        var capabilities = await connection.Client.ProbeCapabilitiesAsync(ct);

        connection.Capabilities = capabilities;
        connection.LastSeenUtc = DateTimeOffset.UtcNow;
        connection.LastError = null;

        record.ModelId = capabilities.Model.ModelId;
        record.SerialNumber = capabilities.SerialNumber;
        record.FirmwareVersion = capabilities.Firmware.ToString();
        record.CapabilitiesJson = JsonSerializer.Serialize(capabilities, JsonOptions);
        record.LastSeenUtc = connection.LastSeenUtc;

        await SyncZonesAsync(record, capabilities, ct);
        await _db.SaveChangesAsync(ct);

        return capabilities;
    }

    /// <summary>
    /// Creates a zone row for each station the controller reports, and marks any row
    /// whose station has disappeared as disabled rather than deleting it — the user's
    /// name and photo for that zone are worth keeping if the station comes back.
    /// </summary>
    private async Task SyncZonesAsync(
        ControllerRecord record, ControllerCapabilities capabilities, CancellationToken ct)
    {
        var existing = await _db.Zones
            .Where(z => z.ControllerId == record.Id)
            .ToListAsync(ct);

        var reported = capabilities.Stations.ToHashSet();

        foreach (var station in capabilities.Stations)
        {
            var zone = existing.FirstOrDefault(z => z.StationNumber == station);

            if (zone is null)
            {
                _db.Zones.Add(new ZoneRecord
                {
                    ControllerId = record.Id,
                    StationNumber = station,
                    Name = $"Zone {station}",
                    SortOrder = station,
                });
                continue;
            }

            // The station is back. Undo an automatic disable, but leave alone a zone
            // the user switched off themselves.
            if (zone.AutoDisabled)
            {
                _logger.LogInformation(
                    "Station {Station} on controller {Id} is reported again; re-enabling its zone",
                    station, record.Id);
                zone.Enabled = true;
                zone.AutoDisabled = false;
            }
        }

        foreach (var zone in existing.Where(z => !reported.Contains(z.StationNumber)))
        {
            // A row the user never touched is an artefact — of a mis-read station mask,
            // or of a module that has been removed — and keeping it only clutters the
            // zone list. Anything they named or photographed is kept and disabled
            // instead, because that work is worth preserving if the station returns.
            if (IsUntouched(zone))
            {
                _logger.LogInformation(
                    "Station {Station} on controller {Id} is not reported and was never edited; removing it",
                    zone.StationNumber, record.Id);
                _db.Zones.Remove(zone);
                continue;
            }

            if (!zone.Enabled) continue;

            _logger.LogInformation(
                "Station {Station} on controller {Id} is no longer reported; disabling its zone",
                zone.StationNumber, record.Id);
            zone.Enabled = false;
            zone.AutoDisabled = true;
        }
    }

    /// <summary>
    /// True when a zone still holds only its generated defaults, so removing it loses
    /// nothing the user put there.
    /// </summary>
    private static bool IsUntouched(ZoneRecord zone) =>
        zone.PhotoPath is null
        && string.Equals(zone.Name, $"Zone {zone.StationNumber}", StringComparison.Ordinal);

    /// <summary>Reads cached capabilities without touching the device.</summary>
    public static ControllerCapabilities? DeserializeCapabilities(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return null;
        try
        {
            return JsonSerializer.Deserialize<ControllerCapabilities>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
