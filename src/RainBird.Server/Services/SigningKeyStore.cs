using System.Security.Cryptography;

namespace RainBird.Server.Services;

/// <summary>
/// The secret that signs and verifies tokens.
///
/// Kept in a file beside the database rather than in configuration, so that a fresh
/// install needs no setup and a restart does not sign everyone out. It is read
/// before the host is built — which is why this is a plain file rather than a row in
/// the database: authentication has to be configured before there is a DbContext to
/// ask.
///
/// Deleting the file rotates the key, which invalidates every outstanding token.
/// That is the intended way to sign every session out at once.
/// </summary>
public static class SigningKeyStore
{
    private const string FileName = "jwt-signing.key";

    /// <summary>512 bits, comfortably beyond the 256 that HMAC-SHA256 actually consumes.</summary>
    private const int KeyBytes = 64;

    /// <summary>
    /// Returns the signing key, creating one on first run.
    ///
    /// An explicit key in configuration wins, which is what lets several instances
    /// behind a load balancer accept each other's tokens. Nothing here needs that,
    /// but silently generating a private key per instance would be a confusing way
    /// to find out.
    /// </summary>
    public static byte[] LoadOrCreate(string dataDirectory, IConfiguration configuration)
    {
        var configured = configuration["Auth:SigningKey"];
        if (!string.IsNullOrWhiteSpace(configured))
            return Convert.FromBase64String(configured);

        var path = Path.Combine(dataDirectory, FileName);

        if (File.Exists(path))
        {
            var existing = Convert.FromBase64String(File.ReadAllText(path).Trim());
            if (existing.Length >= 32) return existing;
            // Too short to be one of ours: treat it as absent and write a real one.
        }

        var key = RandomNumberGenerator.GetBytes(KeyBytes);
        File.WriteAllText(path, Convert.ToBase64String(key));

        // Best effort: on Unix, keep it to the owner. No-op on Windows, where the
        // containing directory's ACL is what matters.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        return key;
    }
}
