using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using RainBird.Server.Data;
using RainBird.Server.Services;

namespace RainBird.Server.Tests;

/// <summary>
/// Accounts and tokens, against a real SQLite database rather than a substitute —
/// the collation that makes usernames case-insensitive is a property of the schema,
/// so a fake store would answer differently from the real one on exactly the
/// question worth asking.
/// </summary>
public sealed class AuthTests : IDisposable
{
    private static readonly byte[] SigningKey = new byte[64];

    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly AuthService _auth;

    public AuthTests()
    {
        // Held open deliberately: an in-memory SQLite database exists only as long as
        // a connection to it does.
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options);

        _db.Database.EnsureCreated();
        _auth = new AuthService(_db, SigningKey, NullLogger<AuthService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ------------------------------------------------------------- validation

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("short", false)]
    [InlineData("1234567", false)]
    [InlineData("12345678", true)]
    [InlineData("a much longer passphrase", true)]
    public void Password_must_be_at_least_eight_characters(string? password, bool allowed) =>
        Assert.Equal(allowed, AuthService.ValidatePassword(password) is null);

    [Theory]
    [InlineData(null, false)]
    [InlineData("   ", false)]
    [InlineData("has space", false)]
    [InlineData("sam", true)]
    public void Username_rules(string? username, bool allowed) =>
        Assert.Equal(allowed, AuthService.ValidateUsername(username) is null);

    // ------------------------------------------------------------- accounts

    [Fact]
    public async Task Correct_password_authenticates_and_wrong_one_does_not()
    {
        await _auth.CreateAsync("sam", "hedgerows99");

        Assert.NotNull(await _auth.AuthenticateAsync("sam", "hedgerows99"));
        Assert.Null(await _auth.AuthenticateAsync("sam", "hedgerows98"));
    }

    [Fact]
    public async Task Unknown_account_is_rejected_without_saying_so()
    {
        await _auth.CreateAsync("sam", "hedgerows99");

        // Same answer as a wrong password: null. Anything more specific would let
        // someone ask the login form which usernames exist.
        Assert.Null(await _auth.AuthenticateAsync("nobody", "hedgerows99"));
    }

    [Fact]
    public async Task Usernames_are_matched_without_regard_to_case()
    {
        await _auth.CreateAsync("Sam", "hedgerows99");

        Assert.NotNull(await _auth.FindAsync("sam"));
        Assert.NotNull(await _auth.FindAsync("SAM"));
    }

    [Fact]
    public async Task The_stored_hash_is_not_the_password()
    {
        var user = await _auth.CreateAsync("sam", "hedgerows99");

        Assert.DoesNotContain("hedgerows99", user.PasswordHash);
        Assert.NotEmpty(user.PasswordHash);
    }

    // --------------------------------------------------------------- tokens

    [Fact]
    public async Task Issued_token_validates_and_carries_the_account()
    {
        var user = await _auth.CreateAsync("sam", "hedgerows99");
        var issued = _auth.IssueToken(user);

        // Mapping off, matching the server, so the claims read back under the names
        // they were written with rather than as WS-Federation URIs.
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var principal = handler.ValidateToken(
            issued.Token, AuthService.ValidationParameters(SigningKey), out _);

        Assert.Equal(user.Id.ToString(), principal.FindFirstValue(JwtRegisteredClaimNames.Sub));
        Assert.Equal("sam", principal.Identity?.Name);
        Assert.Equal(
            user.SecurityStamp.ToString(), principal.FindFirstValue(AuthService.SecurityStampClaim));
        Assert.True(issued.ExpiresUtc > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task A_token_signed_with_a_different_key_is_refused()
    {
        var user = await _auth.CreateAsync("sam", "hedgerows99");
        var issued = _auth.IssueToken(user);

        var otherKey = new byte[64];
        otherKey[0] = 1;

        Assert.ThrowsAny<SecurityTokenException>(() =>
            new JwtSecurityTokenHandler { MapInboundClaims = false }
                .ValidateToken(issued.Token, AuthService.ValidationParameters(otherKey), out _));
    }

    // ---------------------------------------------------------- revocation

    [Fact]
    public async Task Changing_a_password_revokes_tokens_issued_before_it()
    {
        var user = await _auth.CreateAsync("sam", "hedgerows99");
        var stampWhenIssued = user.SecurityStamp.ToString();

        Assert.True(await _auth.IsStillValidAsync(user.Id, stampWhenIssued));

        await _auth.SetPasswordAsync(user, "different-one");

        // The signature on the old token is still perfectly good. This is the check
        // that makes changing a leaked password mean something before it expires.
        Assert.False(await _auth.IsStillValidAsync(user.Id, stampWhenIssued));
        Assert.True(await _auth.IsStillValidAsync(user.Id, user.SecurityStamp.ToString()));
    }

    [Fact]
    public async Task Deleting_an_account_invalidates_its_tokens_immediately()
    {
        var user = await _auth.CreateAsync("sam", "hedgerows99");
        var stamp = user.SecurityStamp.ToString();

        await _auth.DeleteAsync(user);

        Assert.False(await _auth.IsStillValidAsync(user.Id, stamp));
    }

    [Fact]
    public async Task Setup_is_only_available_while_there_are_no_accounts()
    {
        Assert.False(await _auth.AnyUsersAsync());

        await _auth.CreateAsync("sam", "hedgerows99");

        Assert.True(await _auth.AnyUsersAsync());
    }
}
