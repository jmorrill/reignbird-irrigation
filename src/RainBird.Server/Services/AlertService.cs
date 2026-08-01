using System.Net;
using System.Text.Json;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.EntityFrameworkCore;
using RainBird.Server.Data;

namespace RainBird.Server.Services;

/// <summary>
/// Which alerts are wanted. Stored as one settings row.
///
/// Trouble is on by default and confirmations are off: an app that tells you every
/// time something went right is one whose notifications get muted, and then the one
/// that mattered goes unread with the rest.
/// </summary>
public sealed record AlertPreferences
{
    public bool PlanFailed { get; init; } = true;
    public bool PlanCompleted { get; init; }
    public bool ControllerOffline { get; init; } = true;
    public bool ControllerRecovered { get; init; } = true;
    public bool ZoneFault { get; init; } = true;
    public bool WeatherSkip { get; init; } = true;

    public bool Wants(AlertKind kind) => kind switch
    {
        AlertKind.PlanFailed => PlanFailed,
        AlertKind.PlanCompleted => PlanCompleted,
        AlertKind.ControllerOffline => ControllerOffline,
        AlertKind.ControllerRecovered => ControllerRecovered,
        AlertKind.ZoneFault => ZoneFault,
        AlertKind.WeatherSkip => WeatherSkip,
        // A test the user explicitly asked for is never filtered — being told "not
        // subscribed to that" when you pressed the button would be its own puzzle.
        AlertKind.Test => true,
        _ => false,
    };
}

/// <summary>
/// Raises alerts, records them, and pushes them to whatever has subscribed.
///
/// Recording comes first and delivery second, deliberately. Push can fail for
/// reasons this server never learns about — permission revoked on a phone, a
/// subscription quietly expired, no network at the moment it mattered — and an alert
/// that exists only as a notification nobody received may as well not have happened.
/// The stored list is what makes "did it tell me?" answerable.
/// </summary>
public sealed class AlertService
{
    private const string PreferencesKey = "alerts.preferences";

    /// <summary>Enough to see what happened without the table growing without bound.</summary>
    private const int KeepMostRecent = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly AppDbContext _db;
    private readonly PushServiceClient _push;
    private readonly VapidKeys _vapid;
    private readonly ILogger<AlertService> _logger;

    public AlertService(
        AppDbContext db,
        PushServiceClient push,
        VapidKeys vapid,
        ILogger<AlertService> logger)
    {
        _db = db;
        _push = push;
        _vapid = vapid;
        _logger = logger;

        // Set here rather than at registration so it cannot be forgotten: without it
        // every push is rejected by the push service as unauthenticated, and the only
        // symptom is notifications that never arrive.
        _push.DefaultAuthentication = new VapidAuthentication(vapid.PublicKey, vapid.PrivateKey)
        {
            Subject = vapid.Subject,
        };
    }

    // ------------------------------------------------------------ preferences

    public async Task<AlertPreferences> GetPreferencesAsync(CancellationToken ct = default)
    {
        var row = await _db.Settings.FirstOrDefaultAsync(s => s.Key == PreferencesKey, ct);
        if (row is null) return new AlertPreferences();

        try
        {
            return JsonSerializer.Deserialize<AlertPreferences>(row.Value, Json) ?? new AlertPreferences();
        }
        catch (JsonException)
        {
            return new AlertPreferences();
        }
    }

    public async Task SetPreferencesAsync(AlertPreferences preferences, CancellationToken ct = default)
    {
        var row = await _db.Settings.FirstOrDefaultAsync(s => s.Key == PreferencesKey, ct);
        var value = JsonSerializer.Serialize(preferences, Json);

        if (row is null) _db.Settings.Add(new SettingRecord { Key = PreferencesKey, Value = value });
        else row.Value = value;

        await _db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- raising

    /// <summary>
    /// Records an alert and delivers it, unless the user has switched that kind off.
    ///
    /// Never throws. This is called from the scheduler and the polling loop, and an
    /// alert about a failure must not be able to cause a second one.
    /// </summary>
    public async Task RaiseAsync(
        AlertKind kind,
        AlertSeverity severity,
        string title,
        string detail,
        int? controllerId = null,
        CancellationToken ct = default)
    {
        try
        {
            var preferences = await GetPreferencesAsync(ct);
            if (!preferences.Wants(kind)) return;

            var alert = new AlertRecord
            {
                Kind = kind,
                Severity = severity,
                Title = Truncate(title, 120),
                Detail = Truncate(detail, 500),
                ControllerId = controllerId,
            };

            _db.Alerts.Add(alert);
            await _db.SaveChangesAsync(ct);

            alert.DeliveredCount = await DeliverAsync(alert, ct);
            await _db.SaveChangesAsync(ct);

            await TrimAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not raise the {Kind} alert", kind);
        }
    }

    public Task<List<AlertRecord>> RecentAsync(int limit = 50, CancellationToken ct = default) =>
        _db.Alerts.OrderByDescending(a => a.CreatedUtc).Take(limit).ToListAsync(ct);

    // -------------------------------------------------------------- delivery

    private async Task<int> DeliverAsync(AlertRecord alert, CancellationToken ct)
    {
        var subscriptions = await _db.PushSubscriptions.ToListAsync(ct);
        if (subscriptions.Count == 0) return 0;

        var payload = JsonSerializer.Serialize(new
        {
            title = alert.Title,
            body = alert.Detail,
            kind = alert.Kind.ToString(),
            severity = alert.Severity.ToString(),
            controllerId = alert.ControllerId,
        }, Json);

        var delivered = 0;
        var dead = new List<PushSubscriptionRecord>();

        foreach (var record in subscriptions)
        {
            var subscription = new PushSubscription
            {
                Endpoint = record.Endpoint,
                Keys = new Dictionary<string, string>
                {
                    ["p256dh"] = record.P256dh,
                    ["auth"] = record.Auth,
                },
            };

            try
            {
                await _push.RequestPushMessageDeliveryAsync(
                    subscription,
                    new PushMessage(payload) { Topic = alert.Kind.ToString(), Urgency = UrgencyFor(alert.Severity) },
                    ct);

                record.Failures = 0;
                delivered++;
            }
            catch (PushServiceClientException ex) when (
                ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                // The push service is telling us this device is gone for good — the
                // browser was uninstalled, or permission was revoked. Retrying it for
                // ever would just be noise in the log.
                dead.Add(record);
            }
            catch (Exception ex)
            {
                record.Failures++;
                _logger.LogWarning(ex, "Push delivery failed ({Failures} in a row)", record.Failures);
            }
        }

        if (dead.Count > 0)
        {
            _db.PushSubscriptions.RemoveRange(dead);
            _logger.LogInformation("Removed {Count} expired push subscriptions", dead.Count);
        }

        return delivered;
    }

    /// <summary>
    /// A problem is worth waking a sleeping phone for; a confirmation is not.
    /// </summary>
    private static PushMessageUrgency UrgencyFor(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Problem => PushMessageUrgency.High,
        AlertSeverity.Warning => PushMessageUrgency.Normal,
        _ => PushMessageUrgency.Low,
    };

    // ---------------------------------------------------------- subscriptions

    public async Task<PushSubscriptionRecord> SubscribeAsync(
        string endpoint, string p256dh, string auth, int userId, string description,
        CancellationToken ct = default)
    {
        // Keyed on the endpoint, because a browser re-subscribing hands back the same
        // one. Inserting instead would notify the same phone twice.
        var existing = await _db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == endpoint, ct);

        if (existing is not null)
        {
            existing.P256dh = p256dh;
            existing.Auth = auth;
            existing.UserId = userId;
            existing.Description = description;
            existing.Failures = 0;
            await _db.SaveChangesAsync(ct);
            return existing;
        }

        var record = new PushSubscriptionRecord
        {
            Endpoint = endpoint,
            P256dh = p256dh,
            Auth = auth,
            UserId = userId,
            Description = description,
        };

        _db.PushSubscriptions.Add(record);
        await _db.SaveChangesAsync(ct);
        return record;
    }

    public async Task UnsubscribeAsync(string endpoint, CancellationToken ct = default)
    {
        var existing = await _db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == endpoint, ct);
        if (existing is null) return;

        _db.PushSubscriptions.Remove(existing);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Removes every device belonging to an account.
    ///
    /// Called when an account is deleted. Without it a removed user's phone carries on
    /// receiving plan, outage and garden notifications indefinitely — the record even
    /// stored which account a subscription belonged to, and then never used it.
    /// </summary>
    public async Task RemoveSubscriptionsForUserAsync(int userId, CancellationToken ct = default)
    {
        var owned = await _db.PushSubscriptions.Where(s => s.UserId == userId).ToListAsync(ct);
        if (owned.Count == 0) return;

        _db.PushSubscriptions.RemoveRange(owned);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Removed {Count} push subscription(s) belonging to deleted account {UserId}",
            owned.Count, userId);
    }

    public Task<int> SubscriptionCountAsync(CancellationToken ct = default) =>
        _db.PushSubscriptions.CountAsync(ct);

    public string PublicKey => _vapid.PublicKey;

    // ----------------------------------------------------------------- upkeep

    private async Task TrimAsync(CancellationToken ct)
    {
        var total = await _db.Alerts.CountAsync(ct);
        if (total <= KeepMostRecent) return;

        var stale = await _db.Alerts
            .OrderByDescending(a => a.CreatedUtc)
            .Skip(KeepMostRecent)
            .ToListAsync(ct);

        _db.Alerts.RemoveRange(stale);
        await _db.SaveChangesAsync(ct);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
