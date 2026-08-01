using System.Globalization;
using System.Text;

namespace RainBird.Protocol.Universal;

/// <summary>
/// The Universal Message Transport (SIP <c>0C</c>) and the Controller Data Table it
/// carries.
///
/// This is the modern configuration interface. Current firmware — an ESP-ME3 on
/// protocol 2.12, for instance — drops the legacy schedule pages (SIP <c>20</c>/
/// <c>21</c>) entirely and exposes everything through here instead, so without this
/// there is no way to read or write such a controller's schedule at all.
///
/// Verified against physical hardware. See
/// <c>docs/rainbird-universal-protocol.md</c>.
/// </summary>
public static class UniversalProtocol
{
    /// <summary>
    /// Routing header, twenty bytes, sent after the SIP <c>0C</c> command byte.
    ///
    /// Two six-byte addresses sit in the middle and swap places in the reply, which
    /// is how they were identified as source and destination. The destination's first
    /// byte selects the manager on the controller: <c>0C</c> is the Controller Data
    /// Table, <c>09</c> irrigation, <c>04</c> field devices, <c>13</c> the GUI,
    /// <c>0D</c> firmware. The remaining bytes are constant in every request the app
    /// makes and are reproduced verbatim.
    /// </summary>
    private const string RoutingHeader = "20000100" + "08050000" + "0000" + "0C000000" + "000005000000";

    /// <summary>Handler byte that follows the payload type for every CDT message.</summary>
    private const byte CdtHandler = 0x0B;

    private const byte BunchGetRequest = 0x07;
    private const byte BunchGetResponse = 0x08;
    private const byte BunchSetRequest = 0x05;
    private const byte BunchSetResponse = 0x06;

    /// <summary>Builds the SIP payload for reading one or more table entries.</summary>
    public static string EncodeGet(IReadOnlyList<CdtRange> ranges)
    {
        ArgumentOutOfRangeException.ThrowIfZero(ranges.Count);

        var payload = new StringBuilder();
        payload.Append($"{BunchGetRequest:X2}{CdtHandler:X2}");
        payload.Append($"{ranges.Count:X2}");

        foreach (var range in ranges)
            payload.Append(range.Encode());

        return Frame(payload.ToString());
    }

    /// <summary>Builds the SIP payload for writing one table entry.</summary>
    public static string EncodeSet(CdtRange range, int valueWidth, IReadOnlyList<int> values)
    {
        ArgumentOutOfRangeException.ThrowIfZero(values.Count);
        if (valueWidth is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(valueWidth), "Value width must be 1-4 bytes.");

        if (values.Count != range.Count)
        {
            throw new ArgumentException(
                $"{range.DataId} covers {range.Count} slots but {values.Count} values were supplied.",
                nameof(values));
        }

        var payload = new StringBuilder();
        payload.Append($"{BunchSetRequest:X2}{CdtHandler:X2}");
        payload.Append("01");
        payload.Append(range.Encode());
        payload.Append($"{valueWidth:X2}");

        foreach (var value in values)
            payload.Append(LittleEndian(value, valueWidth));

        return Frame(payload.ToString());
    }

    /// <summary>Wraps a payload in the routing header and its little-endian length.</summary>
    private static string Frame(string payloadHex)
    {
        var lengthBytes = payloadHex.Length / 2;
        return $"{(byte)SipCommand.UniversalMessage:X2}{RoutingHeader}{LittleEndian(lengthBytes, 2)}{payloadHex}";
    }

    internal static string LittleEndian(int value, int width)
    {
        var builder = new StringBuilder(width * 2);
        for (var i = 0; i < width; i++)
            builder.Append(((value >> (8 * i)) & 0xFF).ToString("X2", CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    /// <summary>
    /// Decodes a bunch-get response into its blocks.
    /// </summary>
    public static IReadOnlyList<CdtValues> DecodeGet(string responseHex)
    {
        var reader = OpenPayload(responseHex, BunchGetResponse);

        var blocks = new List<CdtValues>();
        var count = reader.Byte();

        for (var i = 0; i < count; i++)
        {
            var dataId = reader.UInt16();
            var rank = reader.Byte();

            var bounds = new List<(int Start, int End)>(rank);
            for (var r = 0; r < rank; r++)
            {
                var start = reader.UInt16();
                var end = reader.UInt16();
                bounds.Add((start, end));
            }

            var width = reader.Byte();
            var slots = bounds.Aggregate(1, (total, b) => total * (b.End - b.Start + 1));

            var values = new List<int>(slots);
            for (var v = 0; v < slots && reader.Remaining >= width; v++)
                values.Add(reader.Value(width));

            blocks.Add(new CdtValues(dataId, bounds, width, values));
        }

        return blocks;
    }

    /// <summary>
    /// Confirms a bunch-set was accepted.
    ///
    /// The controller answers with a single status byte: zero means every entry was
    /// written, anything else introduces a list of per-entry failures.
    /// </summary>
    public static void EnsureSetAccepted(string responseHex)
    {
        var reader = OpenPayload(responseHex, BunchSetResponse);

        if (reader.Remaining == 0)
            throw new RainBirdProtocolException("Controller returned an empty set response.");

        var status = reader.Byte();
        if (status == 0) return;

        var failures = reader.Remaining > 0 ? reader.Byte() : 0;
        throw new RainBirdProtocolException(
            $"Controller rejected the write ({failures} of the requested entries failed).");
    }

    /// <summary>
    /// Strips the SIP response code, routing header, length and payload type, leaving
    /// a reader positioned at the first block.
    /// </summary>
    private static HexReader OpenPayload(string responseHex, byte expectedType)
    {
        responseHex = responseHex.Trim().ToUpperInvariant();

        // 0x8C + 20-byte routing header + 2-byte length + type + handler.
        const int minimum = 2 + 40 + 4 + 4;
        if (responseHex.Length < minimum)
            throw new RainBirdProtocolException($"Universal response too short: '{responseHex}'.");

        if (!responseHex.StartsWith("8C", StringComparison.Ordinal))
        {
            throw new RainBirdProtocolException(
                $"Expected a universal transport response (8C) but got '{responseHex[..2]}'.");
        }

        var type = Convert.ToByte(responseHex.Substring(2 + 40 + 4, 2), 16);
        if (type != expectedType)
        {
            throw new RainBirdProtocolException(
                $"Expected universal payload type 0x{expectedType:X2} but got 0x{type:X2}.");
        }

        return new HexReader(responseHex, minimum);
    }

    /// <summary>Little-endian cursor over a hex string.</summary>
    private sealed class HexReader
    {
        private readonly string _hex;
        private int _position;

        public HexReader(string hex, int startNibble)
        {
            _hex = hex;
            _position = startNibble;
        }

        public int Remaining => Math.Max(0, (_hex.Length - _position) / 2);

        public byte Byte()
        {
            Require(1);
            var value = Convert.ToByte(_hex.Substring(_position, 2), 16);
            _position += 2;
            return value;
        }

        public int UInt16() => Value(2);

        public int Value(int width)
        {
            Require(width);
            var value = 0;
            for (var i = 0; i < width; i++)
                value |= Convert.ToInt32(_hex.Substring(_position + i * 2, 2), 16) << (8 * i);
            _position += width * 2;
            return value;
        }

        private void Require(int bytes)
        {
            if (_position + bytes * 2 > _hex.Length)
                throw new RainBirdProtocolException("Universal response ended unexpectedly.");
        }
    }
}
