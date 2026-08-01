using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;

namespace RainBird.Protocol;

/// <summary>
/// Certificate pinning for controllers that serve the protocol over TLS.
///
/// Newer LNK firmware listens on 443 with a self-signed certificate
/// (<c>CN=fw-cc20.rainbird.com</c>, issued by Rain Bird Corporation). It will never
/// validate against a public root, so the choice is between pinning and disabling
/// verification altogether.
///
/// Pinning is the better of the two: the certificates here were captured from a
/// physical ESP-ME3, which presents exactly this certificate, so verification stays
/// real rather than accepting whatever happens to answer on port 443.
/// </summary>
public static class RainBirdCertificates
{
    private static readonly Lazy<X509Certificate2Collection> Pinned = new(Load);

    /// <summary>The certificates a controller is allowed to present.</summary>
    public static X509Certificate2Collection Trusted => Pinned.Value;

    /// <summary>SHA-256 thumbprints of the pinned certificates, uppercase hex.</summary>
    public static IReadOnlySet<string> Thumbprints { get; } =
        Pinned.Value.Select(c => c.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static X509Certificate2Collection Load()
    {
        var collection = new X509Certificate2Collection();
        var assembly = typeof(RainBirdCertificates).Assembly;

        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.EndsWith(".pem", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;

            using var reader = new StreamReader(stream);
            try
            {
                collection.Add(X509Certificate2.CreateFromPem(reader.ReadToEnd()));
            }
            catch (Exception)
            {
                // A certificate we cannot parse is simply not pinned; the connection
                // will fail closed rather than silently trusting something else.
            }
        }

        return collection;
    }

    /// <summary>
    /// Validates a controller's TLS certificate against the pinned set.
    ///
    /// Chain and name errors are expected and ignored — the certificate is
    /// self-signed and its CN is a Rain Bird hostname rather than the device's IP.
    /// What must hold is that it is exactly one of the certificates we shipped.
    /// </summary>
    public static bool Validate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        _ = sender;
        _ = chain;
        _ = errors;

        if (certificate is null) return false;

        var presented = new X509Certificate2(certificate);
        var thumbprint = presented.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
        return Thumbprints.Contains(thumbprint);
    }

    /// <summary>A handler that trusts only the pinned Rain Bird certificates.</summary>
    public static HttpMessageHandler CreatePinnedHandler() =>
        new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
                Validate(message, cert, chain, errors),
        };
}
