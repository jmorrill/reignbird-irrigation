using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using RainBird.Server.Data;
using RainBird.Server.Services;

namespace RainBird.Server.Api;

public record LoginRequest(string? Username, string? Password);
public record CreateUserRequest(string? Username, string? Password);
public record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public record UserResponse(int Id, string Username, DateTimeOffset CreatedUtc, DateTimeOffset? LastSignInUtc)
{
    public static UserResponse From(UserRecord user) =>
        new(user.Id, user.Username, user.CreatedUtc, user.LastSignInUtc);
}

public record SessionResponse(string Token, DateTimeOffset ExpiresUtc, UserResponse User);

/// <summary>
/// Signing in, and managing who can.
///
/// Only three routes here are anonymous, and each has to be: the status probe that
/// tells the app whether to show a login or a first-run setup screen, the login
/// itself, and that setup call. Everything else in the app requires a token.
/// </summary>
public static class AuthEndpoints
{
    public static void MapAuthApi(this WebApplication app)
    {
        // Authorised by default; the three routes that cannot be are marked
        // individually below, so adding a route here fails closed rather than open.
        var auth = app.MapGroup("/api/auth").WithTags("Auth").RequireAuthorization();

        // ------------------------------------------------------------- anonymous

        auth.MapGet("/status", async (AuthService users, CancellationToken ct) =>
            Results.Ok(new { setupRequired = !await users.AnyUsersAsync(ct) }))
            .AllowAnonymous();

        auth.MapPost("/login", async (
            [FromBody] LoginRequest request, AuthService users, CancellationToken ct) =>
        {
            var user = await users.AuthenticateAsync(request.Username?.Trim() ?? "", request.Password ?? "", ct);

            // One message for both "no such account" and "wrong password". Telling
            // them apart is a free username oracle.
            if (user is null)
                return Results.Json(new { message = "That username and password do not match." }, statusCode: 401);

            var token = users.IssueToken(user);
            return Results.Ok(new SessionResponse(token.Token, token.ExpiresUtc, UserResponse.From(user)));
        }).AllowAnonymous();

        // Creates the very first account, and only ever that one. Once any account
        // exists this route is closed, so it cannot be used to add a second.
        auth.MapPost("/setup", async (
            [FromBody] CreateUserRequest request, AuthService users, CancellationToken ct) =>
        {
            if (await users.AnyUsersAsync(ct))
                return Results.Conflict(new { message = "Setup has already been completed. Sign in instead." });

            if (Invalid(request.Username, request.Password) is { } problem)
                return Results.BadRequest(new { message = problem });

            var user = await users.CreateAsync(request.Username!.Trim(), request.Password!, ct);
            var token = users.IssueToken(user);

            return Results.Ok(new SessionResponse(token.Token, token.ExpiresUtc, UserResponse.From(user)));
        }).AllowAnonymous();

        // ---------------------------------------------------------- authenticated

        auth.MapGet("/me", async (ClaimsPrincipal principal, AuthService users, CancellationToken ct) =>
        {
            var user = await users.FindAsync(CurrentUserId(principal), ct);
            return user is null ? Results.Unauthorized() : Results.Ok(UserResponse.From(user));
        });

        auth.MapPost("/password", async (
            [FromBody] ChangePasswordRequest request,
            ClaimsPrincipal principal,
            AuthService users,
            CancellationToken ct) =>
        {
            var user = await users.FindAsync(CurrentUserId(principal), ct);
            if (user is null) return Results.Unauthorized();

            // Re-checked even though they are already signed in: it is what stops a
            // borrowed unlocked laptop becoming a permanent takeover.
            if (await users.AuthenticateAsync(user.Username, request.CurrentPassword ?? "", ct) is null)
                return Results.BadRequest(new { message = "Your current password is not correct." });

            if (AuthService.ValidatePassword(request.NewPassword) is { } problem)
                return Results.BadRequest(new { message = problem });

            await users.SetPasswordAsync(user, request.NewPassword!, ct);

            // Changing the password revoked every token including this one, so hand
            // back a fresh session rather than signing the user out of their own
            // password change.
            var token = users.IssueToken(user);
            return Results.Ok(new SessionResponse(token.Token, token.ExpiresUtc, UserResponse.From(user)));
        });

        var accounts = app.MapGroup("/api/users").WithTags("Auth").RequireAuthorization();

        accounts.MapGet("/", async (AuthService users, CancellationToken ct) =>
            Results.Ok((await users.ListAsync(ct)).Select(UserResponse.From)));

        accounts.MapPost("/", async (
            [FromBody] CreateUserRequest request, AuthService users, CancellationToken ct) =>
        {
            if (Invalid(request.Username, request.Password) is { } problem)
                return Results.BadRequest(new { message = problem });

            if (await users.FindAsync(request.Username!.Trim(), ct) is not null)
                return Results.Conflict(new { message = "That username is taken." });

            var user = await users.CreateAsync(request.Username!.Trim(), request.Password!, ct);
            return Results.Ok(UserResponse.From(user));
        });

        accounts.MapDelete("/{id:int}", async (
            int id, ClaimsPrincipal principal, AuthService users, CancellationToken ct) =>
        {
            var user = await users.FindAsync(id, ct);
            if (user is null) return Results.NotFound(new { message = "No such account." });

            // Both guards exist for the same reason: every account is equal, so the
            // only way to end up locked out of your own sprinklers is to remove the
            // account you are using, or the last one there is.
            if (id == CurrentUserId(principal))
                return Results.BadRequest(new { message = "You cannot delete the account you are signed in with." });

            if ((await users.ListAsync(ct)).Count <= 1)
                return Results.BadRequest(new { message = "This is the only account. Add another before removing it." });

            await users.DeleteAsync(user, ct);
            return Results.NoContent();
        });
    }

    private static string? Invalid(string? username, string? password) =>
        AuthService.ValidateUsername(username) ?? AuthService.ValidatePassword(password);

    /// <summary>
    /// Reads "sub" as written. Inbound claim mapping is switched off, so the standard
    /// JWT names survive rather than being rewritten into WS-Federation URIs.
    /// </summary>
    private static int CurrentUserId(ClaimsPrincipal principal) =>
        int.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id) ? id : 0;
}
