using System.Security.Cryptography;
using System.Text;

namespace RainBird.Protocol;

/// <summary>
/// The LNK WiFi module's body encryption.
///
/// AES-256-CBC with a SHA-256-derived key, zero padding, and a SHA-256 integrity
/// prefix.
///
/// Wire format, both directions:
/// <code>
///   SHA256(plaintext)  ||  IV  ||  AES256-CBC(padded plaintext)
///        32 bytes          16
/// </code>
/// </summary>
public static class RainBirdCipher
{
    public const int HashLength = 32;
    public const int IvLength = 16;
    public const int BlockSize = 16;

    /// <summary>Key derivation: the raw SHA-256 of the UTF-8 password.</summary>
    public static byte[] DeriveKey(string password) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(password));

    /// <summary>
    /// Zero-pads to the AES block size. Note this is *not* PKCS#7 — the device
    /// expects NUL padding, and the receiver strips NULs rather than reading a
    /// pad length.
    /// </summary>
    public static byte[] ZeroPad(byte[] data)
    {
        var remainder = data.Length % BlockSize;
        if (remainder == 0) return data;

        var padded = new byte[data.Length + (BlockSize - remainder)];
        data.CopyTo(padded, 0);
        return padded;
    }

    /// <summary>
    /// Encrypts a JSON-RPC request body the way a <b>client</b> does: the integrity
    /// hash covers the <b>unpadded</b> plaintext.
    /// </summary>
    public static byte[] Encrypt(string password, string plaintext) =>
        EncryptCore(password, plaintext, hashPaddedPlaintext: false);

    /// <summary>
    /// Encrypts the way a <b>controller</b> does: the integrity hash covers the
    /// <b>padded</b> plaintext, NULs included.
    ///
    /// The protocol is genuinely asymmetric here — see <see cref="Decrypt"/> — and the
    /// simulator has to reproduce the device side faithfully or it isn't testing
    /// anything real.
    /// </summary>
    public static byte[] EncryptAsController(string password, string plaintext) =>
        EncryptCore(password, plaintext, hashPaddedPlaintext: true);

    private static byte[] EncryptCore(string password, string plaintext, bool hashPaddedPlaintext)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var padded = ZeroPad(plainBytes);

        using var aes = Aes.Create();
        aes.Key = DeriveKey(password);
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var ciphertext = encryptor.TransformFinalBlock(padded, 0, padded.Length);

        var hash = hashPaddedPlaintext
            ? SHA256.HashData(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(padded)))
            : SHA256.HashData(plainBytes);

        var body = new byte[HashLength + IvLength + ciphertext.Length];
        hash.CopyTo(body, 0);
        aes.IV.CopyTo(body, HashLength);
        ciphertext.CopyTo(body, HashLength + IvLength);
        return body;
    }

    /// <summary>
    /// Decrypts a response body.
    /// </summary>
    /// <param name="verifyHash">
    /// When true, the integrity prefix is checked and a mismatch throws
    /// <see cref="RainBirdAuthenticationException"/>. A hash mismatch is the only
    /// signal the protocol gives for a wrong password, so this is the
    /// authentication check. Provisioning flows run with verification off.
    /// </param>
    public static string Decrypt(string password, byte[] body, bool verifyHash = true)
    {
        if (body.Length < HashLength + IvLength + BlockSize)
            throw new RainBirdProtocolException(
                $"Response body too short to be a valid encrypted frame ({body.Length} bytes).");

        var expectedHash = body.AsSpan(0, HashLength).ToArray();
        var iv = body.AsSpan(HashLength, IvLength).ToArray();
        var ciphertext = body.AsSpan(HashLength + IvLength).ToArray();

        if (ciphertext.Length % BlockSize != 0)
            throw new RainBirdProtocolException(
                $"Ciphertext length {ciphertext.Length} is not a multiple of the AES block size.");

        using var aes = Aes.Create();
        aes.Key = DeriveKey(password);
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        byte[] decrypted;
        try
        {
            decrypted = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
        }
        catch (CryptographicException ex)
        {
            throw new RainBirdAuthenticationException("Failed to decrypt the controller response.", ex);
        }

        // The app converts the decrypted bytes to a string and hashes *that* — trailing
        // NUL padding included — before stripping the NULs. Its own encryptRequest,
        // however, hashes the unpadded plaintext. The protocol is asymmetric, so we
        // accept either form.
        //
        // This is not a weakening. The prefix is an unkeyed SHA-256 sent in the clear;
        // it cannot authenticate against an active attacker, and its real job in this
        // protocol is to tell us the password was wrong. Both forms answer that
        // question equally well, and rejecting a controller whose firmware picked the
        // other convention would be a far worse failure than accepting both.
        var paddedText = Encoding.UTF8.GetString(decrypted);
        var strippedText = paddedText.Replace("\0", string.Empty);

        if (verifyHash)
        {
            var paddedHash = SHA256.HashData(Encoding.UTF8.GetBytes(paddedText));
            var strippedHash = SHA256.HashData(Encoding.UTF8.GetBytes(strippedText));

            if (!CryptographicOperations.FixedTimeEquals(paddedHash, expectedHash)
                && !CryptographicOperations.FixedTimeEquals(strippedHash, expectedHash))
            {
                throw new RainBirdAuthenticationException(
                    "Response integrity check failed. The controller password is probably wrong.");
            }
        }

        return strippedText;
    }
}
