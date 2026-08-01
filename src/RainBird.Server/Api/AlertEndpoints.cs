using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using RainBird.Server.Data;
using RainBird.Server.Services;

namespace RainBird.Server.Api;

public record SubscribeRequest(string? Endpoint, string? P256dh, string? Auth, string? Description);
public record UnsubscribeRequest(string? Endpoint);

public record AlertResponse(
    int Id, string Kind, string Severity, string Title, string Detail,
    DateTimeOffset CreatedUtc, int DeliveredCount)
{
    public static AlertResponse From(AlertRecord alert) => new(
        alert.Id, alert.Kind.ToString(), alert.Severity.ToString(),
        alert.Title, alert.Detail, alert.CreatedUtc, alert.DeliveredCount);
}

/// <summary>Notifications: who receives them, which ones, and what has been sent.</summary>
public static class AlertEndpoints
{
    public static void MapAlertApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/alerts").WithTags("Alerts").RequireAuthorization();

        // The browser needs this to subscribe. It is the public half of the pair and
        // is meant to be handed out.
        group.MapGet("/key", (AlertService alerts) => Results.Ok(new { publicKey = alerts.PublicKey }));

        group.MapGet("/", async (AlertService alerts, CancellationToken ct) =>
            Results.Ok((await alerts.RecentAsync(50, ct)).Select(AlertResponse.From)));

        group.MapGet("/preferences", async (AlertService alerts, CancellationToken ct) =>
            Results.Ok(new
            {
                preferences = await alerts.GetPreferencesAsync(ct),
                subscriptions = await alerts.SubscriptionCountAsync(ct),
            }));

        group.MapPut("/preferences", async (
            [FromBody] AlertPreferences request, AlertService alerts, CancellationToken ct) =>
        {
            await alerts.SetPreferencesAsync(request, ct);
            return Results.Ok(request);
        });

        group.MapPost("/subscribe", async (
            [FromBody] SubscribeRequest request,
            ClaimsPrincipal principal,
            AlertService alerts,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Endpoint)
                || string.IsNullOrWhiteSpace(request.P256dh)
                || string.IsNullOrWhiteSpace(request.Auth))
            {
                return Results.BadRequest(new { message = "That subscription is missing its endpoint or keys." });
            }

            await alerts.SubscribeAsync(
                request.Endpoint, request.P256dh, request.Auth,
                CurrentUserId(principal),
                request.Description?.Trim() ?? "A browser",
                ct);

            return Results.Ok(new { subscriptions = await alerts.SubscriptionCountAsync(ct) });
        });

        group.MapPost("/unsubscribe", async (
            [FromBody] UnsubscribeRequest request, AlertService alerts, CancellationToken ct) =>
        {
            if (!string.IsNullOrWhiteSpace(request.Endpoint))
                await alerts.UnsubscribeAsync(request.Endpoint, ct);

            return Results.Ok(new { subscriptions = await alerts.SubscriptionCountAsync(ct) });
        });

        // Proves the whole path in one press: keys, subscription, the push service,
        // the service worker and the phone's own notification settings. Every one of
        // those can fail silently, which is exactly why this button exists.
        group.MapPost("/test", async (AlertService alerts, CancellationToken ct) =>
        {
            await alerts.RaiseAsync(
                AlertKind.Test,
                AlertSeverity.Info,
                "Reignbird test",
                "If you can read this, notifications are working.",
                ct: ct);

            var latest = (await alerts.RecentAsync(1, ct)).FirstOrDefault();

            return Results.Ok(new
            {
                delivered = latest?.DeliveredCount ?? 0,
                subscriptions = await alerts.SubscriptionCountAsync(ct),
            });
        });
    }

    private static int CurrentUserId(ClaimsPrincipal principal) =>
        int.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id) ? id : 0;
}
