using System.Globalization;

namespace RainBird.Protocol;

/// <summary>A decoded SIP response: the raw hex plus its named fields.</summary>
public sealed class SipMessage
{
    public required byte Code { get; init; }
    public required string Hex { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyDictionary<string, ulong> Fields { get; init; }

    /// <summary>Field value as an unsigned integer.</summary>
    public ulong this[string field] => Fields.TryGetValue(field, out var v)
        ? v
        : throw new RainBirdProtocolException($"Response {Name} (0x{Code:X2}) has no field '{field}'.");

    public int Int(string field) => checked((int)this[field]);

    public bool Has(string field) => Fields.ContainsKey(field);

    /// <summary>
    /// A field's raw hex, needed for values that aren't numbers — the serial number,
    /// for instance, which is an ASCII-ish 8-byte blob.
    /// </summary>
    public string RawField(string field, SipResponseSpec spec)
    {
        var f = spec.Fields[field];
        return Hex.Substring(f.NibblePosition, f.NibbleLength);
    }

    public override string ToString() => $"{Name} [{Hex}]";
}

/// <summary>
/// Encodes SIP commands to hex payloads and decodes responses using the layout
/// table. All offsets in the table are nibble-based; that conversion happens here
/// and nowhere else.
/// </summary>
public static class SipCodec
{
    /// <summary>Builds a command payload: the command byte followed by hex parameters.</summary>
    public static string Encode(SipCommand command, params byte[] parameters)
    {
        var hex = string.Create(2 + parameters.Length * 2, (command, parameters), (span, state) =>
        {
            var (cmd, args) = state;
            WriteByte(span, 0, (byte)cmd);
            for (var i = 0; i < args.Length; i++)
                WriteByte(span, 2 + i * 2, args[i]);
        });
        return hex;
    }

    /// <summary>Command with a raw hex tail — used for the schedule pages, which are not byte lists.</summary>
    public static string EncodeRaw(SipCommand command, string hexTail) =>
        $"{(byte)command:X2}{hexTail.ToUpperInvariant()}";

    /// <summary>
    /// Encodes a 16-bit parameter big-endian, matching the app's
    /// <c>String.format("%04X", value)</c> usage for run times and rain delays.
    /// </summary>
    public static string EncodeUInt16(SipCommand command, ushort value) =>
        $"{(byte)command:X2}{value:X4}";

    private static void WriteByte(Span<char> span, int index, byte value)
    {
        const string digits = "0123456789ABCDEF";
        span[index] = digits[value >> 4];
        span[index + 1] = digits[value & 0x0F];
    }

    /// <summary>
    /// Decodes a response payload.
    /// </summary>
    /// <exception cref="RainBirdNakException">The controller rejected the command.</exception>
    public static SipMessage Decode(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            throw new RainBirdProtocolException("Empty SIP response.");

        hex = hex.Trim().ToUpperInvariant();

        if (hex.Length < 2 || hex.Length % 2 != 0)
            throw new RainBirdProtocolException($"SIP response is not a whole number of bytes: '{hex}'.");

        var code = byte.Parse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var spec = SipResponseTable.Find(code)
            ?? throw new RainBirdProtocolException($"Unknown SIP response code 0x{code:X2} in '{hex}'.");

        if (code == (byte)SipResponse.Nak)
        {
            var echo = ReadField(hex, spec.Fields["commandEcho"]);
            var reason = ReadField(hex, spec.Fields["NAKCode"]);
            throw new RainBirdNakException((byte)echo, (NakReason)(byte)reason);
        }

        if (spec.LengthBytes is { } expected && hex.Length / 2 != expected)
            throw new RainBirdProtocolException(
                $"{spec.Name} expected {expected} bytes but got {hex.Length / 2}: '{hex}'.");

        var fields = new Dictionary<string, ulong>(spec.Fields.Count);
        foreach (var (name, field) in spec.Fields)
            fields[name] = ReadField(hex, field);

        return new SipMessage
        {
            Code = code,
            Hex = hex,
            Name = spec.Name,
            Fields = fields,
        };
    }

    private static ulong ReadField(string hex, SipField field)
    {
        if (field.NibblePosition + field.NibbleLength > hex.Length)
            throw new RainBirdProtocolException(
                $"Field at nibble {field.NibblePosition}+{field.NibbleLength} runs past the end of '{hex}'.");

        var slice = hex.AsSpan(field.NibblePosition, field.NibbleLength);

        // 16 nibbles is the widest field in the table (the serial number). Anything
        // wider than 16 is a blob, not a number, and callers read it as raw hex.
        if (field.NibbleLength > 16)
            return 0;

        return ulong.Parse(slice, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads a station bitmask.
    ///
    /// The mask is transmitted least-significant byte first: the first byte covers
    /// stations 1-8, the second 9-16, and so on. Reading it as a big-endian integer —
    /// which the field table's "length 8 nibbles" invites — scrambles the station
    /// numbers. A physical ESP-ME3 with ten stations reports <c>FF030000</c>, which is
    /// stations 1-10 read this way and a nonsensical 17,18,25-32 read the other.
    /// </summary>
    /// <param name="hex">The full response hex.</param>
    /// <param name="nibblePosition">Where the mask starts, from the field table.</param>
    public static uint ReadStationMask(string hex, int nibblePosition)
    {
        uint mask = 0;
        for (var byteIndex = 0; byteIndex < 4; byteIndex++)
        {
            var offset = nibblePosition + byteIndex * 2;
            if (offset + 2 > hex.Length) break;

            var value = byte.Parse(
                hex.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            mask |= (uint)value << (byteIndex * 8);
        }
        return mask;
    }

    /// <summary>Converts a hex string to bytes.</summary>
    public static byte[] HexToBytes(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return bytes;
    }

    /// <summary>Converts bytes to an uppercase hex string.</summary>
    public static string BytesToHex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);
}
