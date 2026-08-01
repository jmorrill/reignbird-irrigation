using RainBird.Protocol.Universal;

namespace RainBird.Protocol.Tests;

/// <summary>
/// The Universal Message Transport, checked against two independent sources: known-
/// good request templates, and frames captured from a physical ESP-ME3.
///
/// Matching a known-good template byte for byte is the strongest available evidence
/// that the encoder is right, since a single wrong byte anywhere in the twenty-byte
/// routing header changes nothing visible until the controller silently NAKs.
/// </summary>
public class UniversalProtocolTests
{
    // Known-good frames for an ESP-ME3.
    private const string AppMe3Program1RunTimes =
        "0C200001000805000000000C0000000000050000000E00070B011500020000000000001500";
    private const string AppMe3Program2RunTimes =
        "0C200001000805000000000C0000000000050000000E00070B011500020100010000001500";
    private const string AppMe3Program4RunTimes =
        "0C200001000805000000000C0000000000050000000E00070B011500020300030000001500";
    private const string AppMe3StartTimes =
        "0C200001000805000000000C0000000000050000001200070B011D0003000003000000000000000500";
    private const string AppMe3CycleSoak =
        "0C200001000805000000000C0000000000050000001100070B020A0001000015000B000100001500";
    private const string AppMe3GlobalSensorBypass =
        "0C200001000805000000000C0000000000050000000600070B010D0000";

    [Theory]
    [InlineData(0, AppMe3Program1RunTimes)]
    [InlineData(1, AppMe3Program2RunTimes)]
    [InlineData(3, AppMe3Program4RunTimes)]
    public void Run_time_requests_match_the_apps_own_constants(int programIndex, string expected)
    {
        // The ME3 addresses 22 stations, hence the 0..21 second dimension.
        var encoded = UniversalProtocol.EncodeGet([CdtRange.RunTimesForProgram(programIndex, 21)]);

        Assert.Equal(expected, encoded);
    }

    [Fact]
    public void Start_time_request_matches_the_apps_own_constant()
    {
        // Four programs, six start times each.
        var encoded = UniversalProtocol.EncodeGet([CdtRange.StartTimes(3, 5)]);

        Assert.Equal(AppMe3StartTimes, encoded);
    }

    [Fact]
    public void Cycle_and_soak_request_matches_the_apps_own_constant()
    {
        var encoded = UniversalProtocol.EncodeGet(
        [
            CdtRange.Of(CdtDataId.IrrigationCycleTime, 0, 21),
            CdtRange.Of(CdtDataId.IrrigationSoakTime, 0, 21),
        ]);

        Assert.Equal(AppMe3CycleSoak, encoded);
    }

    /// <summary>
    /// Some entries are scalars with no index at all � the app encodes them as rank 0
    /// with no range, not as a one-element range.
    /// </summary>
    [Fact]
    public void Scalar_request_matches_the_apps_own_constant()
    {
        var encoded = UniversalProtocol.EncodeGet([CdtRange.Scalar(CdtDataId.GlobalSensorBypass)]);

        Assert.Equal(AppMe3GlobalSensorBypass, encoded);
    }

    [Fact]
    public void Decodes_a_rank_zero_response_captured_from_hardware()
    {
        const string captured = "8C200000000C00000000000805000000000500FFFF0800080B010D00000100";

        var block = Assert.Single(UniversalProtocol.DecodeGet(captured));

        Assert.Equal(CdtDataId.GlobalSensorBypass, block.Id);
        Assert.Empty(block.Bounds);
        Assert.Equal([0], block.Values);
    }

    // ------------------------------------------------------------- decoding

    /// <summary>
    /// Captured from hardware: program 1 run times, every station zero. This is the
    /// state that means the controller will not water on its own.
    /// </summary>
    private const string CapturedRunTimesResponse =
        "8C200000000C0000000000080500000000050000006700080B01150002000000000000150004" +
        "0000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "000000000000000000000000";

    [Fact]
    public void Decodes_a_run_time_response_captured_from_hardware()
    {
        var blocks = UniversalProtocol.DecodeGet(CapturedRunTimesResponse);

        var block = Assert.Single(blocks);
        Assert.Equal(CdtDataId.RunTimes, block.Id);
        Assert.Equal(4, block.ValueWidth);
        Assert.Equal([(0, 0), (0, 21)], block.Bounds);
        Assert.Equal(22, block.Values.Count);
        Assert.All(block.Values, seconds => Assert.Equal(0, seconds));
    }

    /// <summary>
    /// Captured from hardware: seasonal adjust for four programs, all 100%. Cross-checks
    /// against SIP 0x30, which independently reports 100 on the same controller.
    /// </summary>
    [Fact]
    public void Decodes_seasonal_adjust_captured_from_hardware()
    {
        const string captured =
            "8C200000000C00000000000805000000000500FFFF1300080B0118000100000300026400640064006400";

        var block = Assert.Single(UniversalProtocol.DecodeGet(captured));

        Assert.Equal(CdtDataId.SeasonalAdjustByProgram, block.Id);
        Assert.Equal(2, block.ValueWidth);
        Assert.Equal([100, 100, 100, 100], block.Values);
    }

    /// <summary>Captured from hardware: an accepted write.</summary>
    [Fact]
    public void Accepts_a_successful_set_response()
    {
        const string captured = "8C200000000C00000000000805000000000500FFFF0300060B00";

        // Must not throw.
        UniversalProtocol.EnsureSetAccepted(captured);
    }

    [Fact]
    public void Rejects_a_failed_set_response()
    {
        // Non-zero status introduces a per-entry failure list.
        const string failed = "8C200000000C00000000000805000000000500FFFF0300060B0102";

        var ex = Assert.Throws<RainBirdProtocolException>(() => UniversalProtocol.EnsureSetAccepted(failed));
        Assert.Contains("rejected", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_get_response_is_not_mistaken_for_a_set_response()
    {
        var ex = Assert.Throws<RainBirdProtocolException>(
            () => UniversalProtocol.EnsureSetAccepted(CapturedRunTimesResponse));

        Assert.Contains("payload type", ex.Message);
    }

    [Fact]
    public void A_non_universal_response_is_rejected()
    {
        // Long enough to pass the length check, but the wrong SIP response code.
        var notUniversal = "CC" + new string('0', 60);

        var ex = Assert.Throws<RainBirdProtocolException>(() => UniversalProtocol.DecodeGet(notUniversal));
        Assert.Contains("universal transport response", ex.Message);
    }

    [Fact]
    public void A_truncated_response_is_rejected()
    {
        var ex = Assert.Throws<RainBirdProtocolException>(() => UniversalProtocol.DecodeGet("8C01"));
        Assert.Contains("too short", ex.Message);
    }

    // ------------------------------------------------------------- encoding

    [Fact]
    public void Set_encodes_values_little_endian_at_the_declared_width()
    {
        // Two stations, 600 and 900 seconds, as 32-bit values.
        var encoded = UniversalProtocol.EncodeSet(
            CdtRange.Of(CdtDataId.RunTimes, 0, 0, 0, 1), 4, [600, 900]);

        Assert.Contains("58020000", encoded);  // 600
        Assert.Contains("84030000", encoded);  // 900
        Assert.Contains("050B", encoded);      // bunch-set request
    }

    [Fact]
    public void Set_refuses_a_value_count_that_does_not_match_the_range()
    {
        var range = CdtRange.RunTimesForProgram(0, 9); // ten stations

        var ex = Assert.Throws<ArgumentException>(
            () => UniversalProtocol.EncodeSet(range, 4, [1, 2, 3]));

        Assert.Contains("10 slots", ex.Message);
        Assert.Contains("3 values", ex.Message);
    }

    [Fact]
    public void Ranges_report_how_many_slots_they_cover()
    {
        Assert.Equal(22, CdtRange.RunTimesForProgram(0, 21).Count);
        Assert.Equal(24, CdtRange.StartTimes(3, 5).Count);   // 4 programs x 1 x 6
        Assert.Equal(1, CdtRange.Of(CdtDataId.InterStationDelay, 0, 0).Count);
    }

    [Fact]
    public void Frame_length_counts_only_the_payload()
    {
        var encoded = UniversalProtocol.EncodeGet([CdtRange.Scalar(CdtDataId.GlobalSensorBypass)]);

        // 0x0C command byte + 20-byte routing header = 42 hex characters, then a
        // little-endian 16-bit payload length.
        var declared = Convert.ToInt32(encoded.Substring(42, 2), 16)
                       | (Convert.ToInt32(encoded.Substring(44, 2), 16) << 8);

        var actualPayloadBytes = (encoded.Length - 42 - 4) / 2;
        Assert.Equal(actualPayloadBytes, declared);
        Assert.Equal(6, actualPayloadBytes);
    }

    [Fact]
    public void An_invalid_range_is_rejected_at_construction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CdtRange.Of(CdtDataId.RunTimes, 5, 2));
    }
}
