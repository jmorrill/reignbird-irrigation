using System.Security.Cryptography;
using System.Text.Json;

namespace RainBird.Server.Services;

/// <summary>The application server's identity to the push services. Public half is not secret.</summary>
public sealed record VapidKeys(string PublicKey, string PrivateKey, string Subject);

/// <summary>
/// The keypair that identifies this server to a browser's push service.
///
/// Generated once and kept beside the database, for the same reason as the token
/// signing key: a fresh install should need no setup, and a restart must not
/// invalidate anything. Here the stakes are higher than an inconvenience — a
/// subscription is bound to the public key it was created with, so regenerating
/// silently breaks every device that had already agreed to notifications, and they
/// stay broken until each one re-subscribes.
/// </summary>
public static class VapidKeyStore
{
    private const string FileName = "vapid.json";

    public static VapidKeys LoadOrCreate(string dataDirectory, IConfiguration configuration)
    {
        var path = Path.Combine(dataDirectory, FileName);

        // "mailto:" or a URL, sent to the push service so it has somebody to contact
        // about a misbehaving application server. Never shown to the user.
        var subject = configuration["Push:Subject"] is { Length: > 0 } configured
            ? configured
            : "mailto:reignbird@localhost";

        if (File.Exists(path))
        {
            try
            {
                var existing = JsonSerializer.Deserialize<VapidKeys>(File.ReadAllText(path));
                if (existing is { PublicKey.Length: > 0, PrivateKey.Length: > 0 })
                    return existing with { Subject = subject };
            }
            catch (JsonException)
            {
                // Unreadable. Falls through and writes a fresh pair, which costs the
                // existing subscriptions — better than refusing to start.
            }
        }

        var keys = Generate(subject);

        File.WriteAllText(path, JsonSerializer.Serialize(keys, new JsonSerializerOptions { WriteIndented = true }));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        return keys;
    }

    /// <summary>
    /// A P-256 keypair in the shape the Web Push spec asks for: the public key as an
    /// uncompressed point, the private key as its raw scalar, both base64url with no
    /// padding.
    /// </summary>
    private static VapidKeys Generate(string subject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(includePrivateParameters: true);

        // 0x04 marks an uncompressed point, then X and Y.
        var publicKey = new byte[65];
        publicKey[0] = 0x04;
        parameters.Q.X!.CopyTo(publicKey, 1);
        parameters.Q.Y!.CopyTo(publicKey, 33);

        return new VapidKeys(Base64Url(publicKey), Base64Url(parameters.D!), subject);
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
