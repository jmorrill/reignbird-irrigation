using System.Security.Cryptography;
using System.Text;
using RainBird.Protocol;

namespace RainBird.Protocol.Tests;

public class RainBirdCipherTests
{
    private const string Password = "sprinkler";

    [Fact]
    public void DeriveKey_is_the_raw_sha256_of_the_password()
    {
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(Password));
        Assert.Equal(expected, RainBirdCipher.DeriveKey(Password));
    }

    [Fact]
    public void DeriveKey_produces_a_256_bit_key()
    {
        // The app uses SecretKeySpec(sha256(password), "AES"), which is AES-256 —
        // not AES-128, as some public write-ups claim.
        Assert.Equal(32, RainBirdCipher.DeriveKey(Password).Length);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 16)]
    [InlineData(15, 16)]
    [InlineData(16, 16)]
    [InlineData(17, 32)]
    [InlineData(32, 32)]
    public void ZeroPad_rounds_up_to_the_block_size(int inputLength, int expectedLength)
    {
        var padded = RainBirdCipher.ZeroPad(new byte[inputLength]);
        Assert.Equal(expectedLength, padded.Length);
    }

    [Fact]
    public void ZeroPad_pads_with_nuls_not_pkcs7()
    {
        var padded = RainBirdCipher.ZeroPad("abc"u8.ToArray());

        Assert.Equal(16, padded.Length);
        Assert.Equal((byte)'a', padded[0]);
        // PKCS#7 would put 13 (0x0D) in every pad byte. The device expects zeroes.
        Assert.All(padded[3..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Encrypt_lays_out_hash_then_iv_then_ciphertext()
    {
        const string plaintext = """{"jsonrpc":"2.0","method":"tunnelSip"}""";
        var body = RainBirdCipher.Encrypt(Password, plaintext);

        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(plaintext));
        Assert.Equal(expectedHash, body[..32]);

        // Hash + IV + at least one block.
        Assert.True(body.Length >= 32 + 16 + 16);
        // Ciphertext is block aligned.
        Assert.Equal(0, (body.Length - 48) % 16);
    }

    [Fact]
    public void Encrypt_uses_a_fresh_iv_each_time()
    {
        const string plaintext = "the same message every time";
        var first = RainBirdCipher.Encrypt(Password, plaintext);
        var second = RainBirdCipher.Encrypt(Password, plaintext);

        Assert.Equal(first[..32], second[..32]);          // same plaintext, same hash
        Assert.NotEqual(first[32..48], second[32..48]);   // different IV
        Assert.NotEqual(first[48..], second[48..]);       // therefore different ciphertext
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"id":1,"jsonrpc":"2.0","method":"tunnelSip","params":{"length":1,"data":"4C"}}""")]
    [InlineData("exactly sixteen!")]
    [InlineData("a string that is quite a lot longer than a single AES block, so it spans several")]
    public void Round_trip_recovers_the_plaintext(string plaintext)
    {
        var body = RainBirdCipher.Encrypt(Password, plaintext);
        var recovered = RainBirdCipher.Decrypt(Password, body);
        Assert.Equal(plaintext, recovered);
    }

    [Fact]
    public void Round_trip_survives_non_ascii()
    {
        const string plaintext = """{"name":"Jardín trasero — 40°"}""";
        var body = RainBirdCipher.Encrypt(Password, plaintext);
        Assert.Equal(plaintext, RainBirdCipher.Decrypt(Password, body));
    }

    /// <summary>
    /// The protocol is asymmetric: the app's <c>encryptRequest</c> hashes the unpadded
    /// plaintext, while its <c>decryptRequest</c> verifies against the decrypted string
    /// with the NUL padding still attached. Both conventions appear on the wire, so
    /// both must verify.
    /// </summary>
    [Fact]
    public void Both_hash_conventions_verify()
    {
        const string payload = "needs padding"; // 13 bytes, so 3 NULs are added

        var padded = Encoding.UTF8.GetString(RainBirdCipher.ZeroPad(Encoding.UTF8.GetBytes(payload)));
        Assert.Equal(16, padded.Length);
        Assert.NotEqual(payload, padded); // the two hashes genuinely differ here

        // Controller convention: hash covers the padded text.
        Assert.Equal(payload, RainBirdCipher.Decrypt(
            Password, BuildResponseFrame(Password, payload, hashOver: padded)));

        // App convention: hash covers the unpadded text.
        Assert.Equal(payload, RainBirdCipher.Decrypt(
            Password, BuildResponseFrame(Password, payload, hashOver: payload)));
    }

    [Fact]
    public void A_hash_over_neither_convention_still_fails()
    {
        // Accepting both conventions must not degrade into accepting anything.
        var body = BuildResponseFrame(Password, "needs padding", hashOver: "something else entirely");

        Assert.Throws<RainBirdAuthenticationException>(() => RainBirdCipher.Decrypt(Password, body));
    }

    [Fact]
    public void Block_aligned_payloads_hash_identically_either_way()
    {
        // With no padding the two conventions coincide, which is why the asymmetry is
        // easy to miss until a payload happens not to be block aligned.
        const string payload = "exactly sixteen!";
        var body = BuildResponseFrame(Password, payload, hashOver: payload);
        Assert.Equal(payload, RainBirdCipher.Decrypt(Password, body));
    }

    [Fact]
    public void EncryptAsController_hashes_the_padded_plaintext()
    {
        const string payload = "needs padding";
        var body = RainBirdCipher.EncryptAsController(Password, payload);

        var padded = Encoding.UTF8.GetString(RainBirdCipher.ZeroPad(Encoding.UTF8.GetBytes(payload)));
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(padded)), body[..32]);
        Assert.Equal(payload, RainBirdCipher.Decrypt(Password, body));
    }

    [Fact]
    public void Decrypt_with_the_wrong_password_is_an_authentication_failure()
    {
        var body = RainBirdCipher.Encrypt(Password, """{"result":{}}""");

        var ex = Assert.Throws<RainBirdAuthenticationException>(
            () => RainBirdCipher.Decrypt("not-the-password", body));

        Assert.Contains("password", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decrypt_can_skip_verification_for_provisioning()
    {
        var body = RainBirdCipher.Encrypt(Password, "hello");
        // Garble the integrity prefix; with verification off it should still decrypt.
        body[0] ^= 0xFF;

        Assert.Equal("hello", RainBirdCipher.Decrypt(Password, body, verifyHash: false));
    }

    [Fact]
    public void Decrypt_rejects_a_truncated_frame()
    {
        Assert.Throws<RainBirdProtocolException>(
            () => RainBirdCipher.Decrypt(Password, new byte[20]));
    }

    [Fact]
    public void Decrypt_rejects_a_frame_whose_ciphertext_is_not_block_aligned()
    {
        var body = new byte[32 + 16 + 17];
        Assert.Throws<RainBirdProtocolException>(() => RainBirdCipher.Decrypt(Password, body));
    }

    /// <summary>
    /// Builds a response frame the way the controller would, with control over what
    /// the integrity hash is computed against.
    /// </summary>
    private static byte[] BuildResponseFrame(string password, string payload, string hashOver)
    {
        using var aes = Aes.Create();
        aes.Key = RainBirdCipher.DeriveKey(password);
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.GenerateIV();

        var padded = RainBirdCipher.ZeroPad(Encoding.UTF8.GetBytes(payload));
        using var encryptor = aes.CreateEncryptor();
        var ciphertext = encryptor.TransformFinalBlock(padded, 0, padded.Length);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(hashOver));

        var body = new byte[32 + 16 + ciphertext.Length];
        hash.CopyTo(body, 0);
        aes.IV.CopyTo(body, 32);
        ciphertext.CopyTo(body, 48);
        return body;
    }
}
