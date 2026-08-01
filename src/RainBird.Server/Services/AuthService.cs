using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using RainBird.Server.Data;

namespace RainBird.Server.Services;

/// <summary>Signed proof of who someone is, and when it stops being true.</summary>
public sealed record IssuedToken(string Token, DateTimeOffset ExpiresUtc);

/// <summary>
/// Accounts and the tokens that represent them.
///
/// Every account is equal — there are no roles. Anyone signed in can water,
/// schedule, and add or remove accounts, which suits a household and is the whole
/// model. The two guards that exist are structural rather than hierarchical: an
/// account cannot delete itself, and the last account cannot be deleted, because
/// either one would lock everybody out of their own sprinklers.
/// </summary>
public sealed class AuthService
{
    /// <summary>Claim carrying the security stamp the token was issued under.</summary>
    public const string SecurityStampClaim = "sstamp";

    private const string Issuer = "reignbird";
    private const string Audience = "reignbird";

    /// <summary>
    /// Short enough to be a real boundary, long enough that a phone on the sofa is
    /// not asked to sign in every time someone wants to water the lawn.
    /// </summary>
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(30);

    private static readonly PasswordHasher<UserRecord> Hasher = new();

    private readonly AppDbContext _db;
    private readonly byte[] _signingKey;
    private readonly ILogger<AuthService> _logger;

    public AuthService(AppDbContext db, byte[] signingKey, ILogger<AuthService> logger)
    {
        _db = db;
        _signingKey = signingKey;
        _logger = logger;
    }

    public static SymmetricSecurityKey KeyFrom(byte[] key) => new(key);

    public Task<bool> AnyUsersAsync(CancellationToken ct = default) => _db.Users.AnyAsync(ct);

    public Task<UserRecord?> FindAsync(string username, CancellationToken ct = default) =>
        _db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);

    public Task<UserRecord?> FindAsync(int id, CancellationToken ct = default) =>
        _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<List<UserRecord>> ListAsync(CancellationToken ct = default) =>
        _db.Users.OrderBy(u => u.Username).ToListAsync(ct);

    /// <summary>
    /// Checks a username and password.
    ///
    /// Returns null for both "no such account" and "wrong password" on purpose: told
    /// apart, the two answers let anyone enumerate which usernames exist. The hash is
    /// verified even when the account does not, so that a missing account does not
    /// answer measurably faster than a wrong password.
    /// </summary>
    public async Task<UserRecord?> AuthenticateAsync(string username, string password, CancellationToken ct = default)
    {
        var user = await FindAsync(username, ct);

        if (user is null)
        {
            Hasher.VerifyHashedPassword(new UserRecord(), DummyHash, password);
            return null;
        }

        var result = Hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed) return null;

        // The hasher tells us when a stored hash predates its current defaults. Taking
        // the offer means work factors improve as people sign in, rather than only for
        // accounts created after an upgrade.
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = Hasher.HashPassword(user, password);

        user.LastSignInUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return user;
    }

    /// <summary>A hash of nothing anyone knows, used to keep failed logins costing the same.</summary>
    private static readonly string DummyHash = Hasher.HashPassword(new UserRecord(), Guid.NewGuid().ToString());

    /// <summary>
    /// Creates the first account, and only if there is genuinely none.
    ///
    /// The check and the insert run in one transaction because they were previously
    /// two statements with a gap between them: two setup requests arriving together
    /// with different usernames could both see an empty table and both succeed,
    /// leaving a stranger with an equal account. Returns null when somebody got there
    /// first.
    /// </summary>
    public async Task<UserRecord?> CreateFirstAsync(
        string username, string password, CancellationToken ct = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        if (await _db.Users.AnyAsync(ct)) return null;

        var user = await CreateAsync(username, password, ct);
        await transaction.CommitAsync(ct);

        return user;
    }

    public async Task<UserRecord> CreateAsync(string username, string password, CancellationToken ct = default)
    {
        var user = new UserRecord { Username = username.Trim() };
        user.PasswordHash = Hasher.HashPassword(user, password);

        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Created account {Username}", user.Username);
        return user;
    }

    /// <summary>
    /// Changes a password and rolls the security stamp, which is what makes every
    /// token issued under the old password stop working immediately. Without that,
    /// changing a password because it leaked would leave whoever has the old token
    /// signed in for up to a month.
    /// </summary>
    public async Task SetPasswordAsync(UserRecord user, string password, CancellationToken ct = default)
    {
        user.PasswordHash = Hasher.HashPassword(user, password);
        user.SecurityStamp = Guid.NewGuid();
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Password changed for {Username}; existing sessions revoked", user.Username);
    }

    public async Task DeleteAsync(UserRecord user, CancellationToken ct = default)
    {
        _db.Users.Remove(user);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Deleted account {Username}", user.Username);
    }

    /// <summary>
    /// Confirms the token's stamp still matches the account's.
    ///
    /// This is what turns "delete the account" and "change the password" into things
    /// that take effect now rather than whenever the token would have expired. It
    /// costs one indexed read per authenticated request against a local SQLite file,
    /// which is not worth caching away at this scale.
    /// </summary>
    public async Task<bool> IsStillValidAsync(int userId, string stamp, CancellationToken ct = default)
    {
        var current = await _db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.SecurityStamp)
            .FirstOrDefaultAsync(ct);

        return current != Guid.Empty && current.ToString() == stamp;
    }

    public IssuedToken IssueToken(UserRecord user)
    {
        var expires = DateTimeOffset.UtcNow.Add(TokenLifetime);

        var descriptor = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.UniqueName, user.Username),
                new Claim(SecurityStampClaim, user.SecurityStamp.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            ],
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(KeyFrom(_signingKey), SecurityAlgorithms.HmacSha256));

        return new IssuedToken(new JwtSecurityTokenHandler().WriteToken(descriptor), expires);
    }

    public static TokenValidationParameters ValidationParameters(byte[] signingKey) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = Issuer,
        ValidateAudience = true,
        ValidAudience = Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = KeyFrom(signingKey),
        ValidateLifetime = true,
        // The default five minutes of slack is generous for a token this app issues
        // to itself against its own clock.
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = JwtRegisteredClaimNames.UniqueName,
    };

    /// <summary>
    /// Rejects a password that is too short to be worth hashing. Deliberately the
    /// only rule: composition requirements push people toward "Passw0rd!" and a
    /// length floor is the part that actually helps.
    /// </summary>
    public static string? ValidatePassword(string? password) =>
        string.IsNullOrEmpty(password) ? "A password is required."
        : password.Length < 8 ? "Passwords must be at least 8 characters."
        : null;

    public static string? ValidateUsername(string? username)
    {
        var trimmed = username?.Trim();

        return string.IsNullOrEmpty(trimmed) ? "A username is required."
            : trimmed.Length > 64 ? "Usernames must be 64 characters or fewer."
            : trimmed.Any(char.IsWhiteSpace) ? "Usernames cannot contain spaces."
            : null;
    }
}
