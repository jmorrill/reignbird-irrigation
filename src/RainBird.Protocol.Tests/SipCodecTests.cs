using RainBird.Protocol;

namespace RainBird.Protocol.Tests;

public class SipCodecTests
{
    [Fact]
    public void Encode_writes_the_command_byte_alone_when_there_are_no_parameters()
    {
        Assert.Equal("4C", SipCodec.Encode(SipCommand.CombinedControllerState));
        Assert.Equal("40", SipCodec.Encode(SipCommand.StopIrrigation));
    }

    [Fact]
    public void Encode_appends_parameters_as_hex_bytes()
    {
        // Run station 3 for 10 minutes.
        Assert.Equal("39030A", SipCodec.Encode(SipCommand.ManuallyRunStation, 3, 10));
    }

    [Fact]
    public void Encode_pads_single_digit_values()
    {
        Assert.Equal("0300", SipCodec.Encode(SipCommand.AvailableStations, 0));
    }

    [Fact]
    public void EncodeUInt16_writes_a_big_endian_word()
    {
        // Rain delay of 3 days: 37 0003
        Assert.Equal("370003", SipCodec.EncodeUInt16(SipCommand.SetRainDelay, 3));
        Assert.Equal("37FFFF", SipCodec.EncodeUInt16(SipCommand.SetRainDelay, 0xFFFF));
    }

    [Fact]
    public void Decode_reads_the_model_and_version_response()
    {
        // 82 | modelID 0009 | major 02 | minor 01
        var message = SipCodec.Decode("820009 0201".Replace(" ", ""));

        Assert.Equal("ModelAndVersionResponse", message.Name);
        Assert.Equal(0x0009u, message["modelID"]);
        Assert.Equal(2, message.Int("protocolRevisionMajor"));
        Assert.Equal(1, message.Int("protocolRevisionMinor"));
    }

    [Fact]
    public void Decode_reads_the_combined_controller_state()
    {
        // CC | 0E 1E 05 | 1F 7 7EA | 0000 | 00 | 01 | 0064 | 012C | 03
        //      14:30:05   31/7/2026  delay0 dry  on  100%   300s  st3
        var message = SipCodec.Decode("CC0E1E051F77EA00000001006401 2C03".Replace(" ", ""));

        Assert.Equal("CombinedControllerStateResponse", message.Name);
        Assert.Equal(14, message.Int("hour"));
        Assert.Equal(30, message.Int("minute"));
        Assert.Equal(5, message.Int("second"));
        Assert.Equal(31, message.Int("day"));
        Assert.Equal(7, message.Int("month"));
        Assert.Equal(2026, message.Int("year"));
        Assert.Equal(0, message.Int("delaySetting"));
        Assert.Equal(0, message.Int("sensorState"));
        Assert.Equal(1, message.Int("irrigationState"));
        Assert.Equal(100, message.Int("seasonalAdjust"));
        Assert.Equal(300, message.Int("remainingRuntime"));
        Assert.Equal(3, message.Int("activeStation"));
    }

    [Fact]
    public void Decode_handles_the_twelve_bit_year_packing()
    {
        // The year occupies three nibbles, sharing a byte with the single-nibble month.
        // 92 | day 0F | month 7 | year 7EA  → 15 July 2026
        var message = SipCodec.Decode("920F77EA");

        Assert.Equal(15, message.Int("day"));
        Assert.Equal(7, message.Int("month"));
        Assert.Equal(2026, message.Int("year"));
    }

    [Fact]
    public void Decode_reads_the_available_stations_bitmask()
    {
        // 83 | page 00 | mask 000000FF → stations 1-8
        var message = SipCodec.Decode("8300000000FF");
        var stations = new AvailableStations(message.Int("pageNumber"), (uint)message["setStations"]);

        Assert.Equal(8, stations.Count);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], stations.Stations);
    }

    [Fact]
    public void Station_bitmask_paging_offsets_by_thirty_two()
    {
        var page1 = new AvailableStations(1, 0b0000_0011);
        Assert.Equal([33, 34], page1.Stations);
    }

    [Fact]
    public void Decode_reads_an_acknowledgement()
    {
        var message = SipCodec.Decode("0139");
        Assert.Equal("AcknowledgeResponse", message.Name);
        Assert.Equal(0x39, message.Int("commandEcho"));
    }

    [Fact]
    public void Decode_turns_a_nak_into_an_exception_carrying_the_reason()
    {
        // 00 | echo 63 | code 01 (command not supported)
        var ex = Assert.Throws<RainBirdNakException>(() => SipCodec.Decode("006301"));

        Assert.Equal(0x63, ex.EchoedCommand);
        Assert.Equal(NakReason.CommandNotSupported, ex.Reason);
        Assert.Contains("not supported", ex.Message);
    }

    [Theory]
    [InlineData("02", NakReason.BadLength)]
    [InlineData("04", NakReason.IncompatibleData)]
    [InlineData("08", NakReason.ChecksumError)]
    public void Decode_maps_every_documented_nak_code(string code, NakReason expected)
    {
        var ex = Assert.Throws<RainBirdNakException>(() => SipCodec.Decode($"0039{code}"));
        Assert.Equal(expected, ex.Reason);
    }

    [Fact]
    public void Decode_rejects_a_response_of_the_wrong_length()
    {
        // ModelAndVersion is specified as 5 bytes; give it 4.
        var ex = Assert.Throws<RainBirdProtocolException>(() => SipCodec.Decode("8200090201".Substring(0, 8)));
        Assert.Contains("expected 5 bytes", ex.Message);
    }

    [Fact]
    public void Decode_rejects_an_unknown_response_code()
    {
        var ex = Assert.Throws<RainBirdProtocolException>(() => SipCodec.Decode("7700"));
        Assert.Contains("Unknown SIP response code", ex.Message);
    }

    [Fact]
    public void Decode_rejects_a_half_byte()
    {
        Assert.Throws<RainBirdProtocolException>(() => SipCodec.Decode("4C1"));
    }

    [Fact]
    public void Decode_rejects_an_empty_payload()
    {
        Assert.Throws<RainBirdProtocolException>(() => SipCodec.Decode(""));
    }

    [Fact]
    public void Decode_is_case_insensitive()
    {
        var lower = SipCodec.Decode("8200090201".ToLowerInvariant());
        Assert.Equal(0x0009u, lower["modelID"]);
    }

    [Fact]
    public void Accessing_a_field_that_does_not_exist_names_the_response()
    {
        var message = SipCodec.Decode("0139");
        var ex = Assert.Throws<RainBirdProtocolException>(() => message["nonsense"]);
        Assert.Contains("AcknowledgeResponse", ex.Message);
    }

    [Fact]
    public void Hex_helpers_round_trip()
    {
        var bytes = new byte[] { 0x4C, 0x00, 0xFF, 0x39 };
        var hex = SipCodec.BytesToHex(bytes);
        Assert.Equal("4C00FF39", hex);
        Assert.Equal(bytes, SipCodec.HexToBytes(hex));
    }

    [Fact]
    public void Every_response_in_the_table_has_a_unique_code()
    {
        var codes = SipResponseTable.All.Keys.ToList();
        Assert.Equal(codes.Count, codes.Distinct().Count());
    }

    [Fact]
    public void Fixed_length_specs_have_fields_that_fit_within_their_declared_length()
    {
        foreach (var spec in SipResponseTable.All.Values.Where(s => s.LengthBytes is not null))
        {
            var availableNibbles = spec.LengthBytes!.Value * 2;
            foreach (var (name, field) in spec.Fields)
            {
                Assert.True(
                    field.NibblePosition + field.NibbleLength <= availableNibbles,
                    $"{spec.Name}.{name} runs past the declared {spec.LengthBytes} bytes.");
            }
        }
    }
}
