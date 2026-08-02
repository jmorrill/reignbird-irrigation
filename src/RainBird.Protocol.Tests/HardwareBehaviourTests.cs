using RainBird.Protocol;
using RainBird.Simulator;

namespace RainBird.Protocol.Tests;

/// <summary>
/// Behaviour observed on a physical ESP-ME3 (model 0009, protocol 2.12, firmware
/// 3.11).
///
/// The community protocol tables in circulation describe an older firmware
/// generation, and several of their assumptions are simply untrue on current
/// hardware. Each test here records a frame captured from the real device.
/// </summary>
public class HardwareBehaviourTests
{
    /// <summary>
    /// The station mask is little-endian: byte 0 covers stations 1-8.
    ///
    /// Captured frame: a ten-station ESP-ME3 answers <c>8300FF030000</c>. Read as a
    /// big-endian integer — which the field table's "8 nibbles" invites — that decodes
    /// to stations 17, 18 and 25-32, which is nonsense for a ten-zone controller.
    /// </summary>
    [Fact]
    public void Station_mask_is_little_endian()
    {
        const string captured = "8300FF030000";

        var message = SipCodec.Decode(captured);
        var stations = new AvailableStations(
            message.Int("pageNumber"), SipCodec.ReadStationMask(message.Hex, 4));

        Assert.Equal(Enumerable.Range(1, 10), stations.Stations);
        Assert.Equal(10, stations.Count);
    }

    [Fact]
    public void Big_endian_reading_of_the_same_frame_is_visibly_wrong()
    {
        // Guards the fix: if someone "simplifies" back to a plain integer read, this
        // records exactly what breaks.
        var message = SipCodec.Decode("8300FF030000");
        var bigEndian = new AvailableStations(0, (uint)message["setStations"]);

        Assert.Equal([17, 18, 25, 26, 27, 28, 29, 30, 31, 32], bigEndian.Stations);
    }

    /// <summary>
    /// SIP <c>3D</c> answers with <c>0xBD</c>, a response code absent from the app's
    /// resource table. Captured: <c>BD0000000000</c> — no faulted stations.
    /// </summary>
    [Fact]
    public void Station_error_response_is_decodable()
    {
        var message = SipCodec.Decode("BD0000000000");

        Assert.Equal("CurrentStationErrorResponse", message.Name);
        Assert.Equal(0, message.Int("pageNumber"));
        Assert.Empty(new ActiveStations(0, SipCodec.ReadStationMask(message.Hex, 4)).Stations);
    }

    [Fact]
    public void Station_error_response_reports_faulted_stations()
    {
        // Same layout as the active mask: stations 2 and 5 faulted.
        var message = SipCodec.Decode("BD0012000000");
        var faulted = new ActiveStations(0, SipCodec.ReadStationMask(message.Hex, 4));

        Assert.Equal([2, 5], faulted.Stations);
    }

    /// <summary>
    /// Firmware 3.11 answers SIP <c>0B</c> with nine bytes, not the five the app's
    /// table specifies. Captured: <c>8B030B0001B1001900</c>.
    /// </summary>
    [Fact]
    public void Firmware_response_length_varies_by_generation()
    {
        var message = SipCodec.Decode("8B030B0001B1001900");

        Assert.Equal(3, message.Int("major"));
        Assert.Equal(11, message.Int("minor"));
    }

    [Fact]
    public void Model_and_version_reports_protocol_revision()
    {
        // Captured: 820009020C — ESP-ME3 speaking protocol 2.12.
        var message = SipCodec.Decode("820009020C");

        Assert.Equal(0x0009u, message["modelID"]);
        Assert.Equal(2, message.Int("protocolRevisionMajor"));
        Assert.Equal(12, message.Int("protocolRevisionMinor"));
    }

    [Fact]
    public void Serial_number_frame_decodes()
    {
        // Captured: 8502C03E075002E786
        var message = SipCodec.Decode("8502C03E075002E786");
        Assert.Equal("02C03E075002E786", message.Hex.Substring(2, 16));
    }

    /// <summary>
    /// The date frame packs a 12-bit year alongside a single-nibble month.
    /// Captured: <c>921F77EA</c> — 31 July 2026.
    /// </summary>
    [Fact]
    public void Date_frame_from_hardware_decodes()
    {
        var message = SipCodec.Decode("921F77EA");

        Assert.Equal(31, message.Int("day"));
        Assert.Equal(7, message.Int("month"));
        Assert.Equal(2026, message.Int("year"));
    }

    /// <summary>
    /// SIP <c>39</c> takes a 16-bit station and an 8-bit duration — four bytes, as the
    /// app's own table says and as <c>SIPCommandTwoParam</c> encodes it. A three-byte
    /// form, one byte per parameter, is rejected outright by real hardware with a
    /// bad-length NAK. This is the mistake that silently broke every scheduled run.
    /// </summary>
    [Fact]
    public async Task Running_a_station_uses_a_sixteen_bit_station_number()
    {
        var controller = new VirtualController(stationCount: 10);
        var client = new LnkClient(new SimulatorTransport(controller));

        await client.RunStationAsync(3, 12);

        // 39 | 0003 | 0C
        Assert.Contains("390003 0C".Replace(" ", ""), controller.CommandLog);
    }

    [Fact]
    public async Task A_three_byte_run_command_is_rejected_as_real_hardware_rejects_it()
    {
        var controller = new VirtualController(stationCount: 10);
        var client = new LnkClient(new SimulatorTransport(controller));

        var ex = await Assert.ThrowsAsync<RainBirdNakException>(
            () => client.TunnelAsync(SipCodec.Encode(SipCommand.ManuallyRunStation, 3, 12)));

        Assert.Equal(NakReason.BadLength, ex.Reason);
    }

    // ------------------------------------------------------------- fallbacks

    /// <summary>
    /// Protocol 2.12 rejects SIP <c>4C</c> outright, so the state read has to be
    /// assembled from individual commands. Everything the UI needs must still arrive.
    /// </summary>
    [Fact]
    public async Task State_is_still_readable_without_the_combined_command()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 31, 14, 30, 0, TimeSpan.Zero));
        var controller = new VirtualController(stationCount: 10, timeProvider: time)
        {
            SupportsCombinedState = false,
        };
        var client = new LnkClient(new SimulatorTransport(controller));

        await client.RunStationAsync(4, 12);
        var state = await client.GetCombinedStateAsync();

        Assert.True(state.IsWatering);
        Assert.Equal(4, state.ActiveStation);
        Assert.Equal(new DateOnly(2026, 7, 31), state.ControllerDate);
        Assert.Equal(new TimeOnly(14, 30, 0), state.ControllerTime);
        Assert.True(state.ControllerEnabled);
    }

    [Fact]
    public async Task The_fallback_is_only_discovered_once()
    {
        var controller = new VirtualController(stationCount: 10) { SupportsCombinedState = false };
        var client = new LnkClient(new SimulatorTransport(controller));

        await client.GetCombinedStateAsync();
        controller.CommandLog.Clear();
        await client.GetCombinedStateAsync();

        // Having learned 4C is unsupported, the client must not keep asking.
        Assert.DoesNotContain("4C", controller.CommandLog);
    }

    [Fact]
    public async Task Capability_probe_reports_what_protocol_2_12_lacks()
    {
        var controller = new VirtualController(stationCount: 10)
        {
            SupportsCombinedState = false,
            SupportsSchedulePages = false,
            SupportsControllerToggle = false,
        };
        var client = new LnkClient(new SimulatorTransport(controller));

        var capabilities = await client.ProbeCapabilitiesAsync();

        Assert.False(capabilities.SupportsCombinedState);
        Assert.False(capabilities.SupportsSchedulePages);
        Assert.False(capabilities.SupportsControllerToggle);
        Assert.Equal(10, capabilities.StationCount);

        // With no way to read or write the controller's own schedule, this app has to
        // own scheduling itself.
        Assert.True(capabilities.RequiresSoftwareScheduling);
    }

    /// <summary>
    /// The event log and event timestamps are asked about, and nothing more.
    ///
    /// SIP <c>70</c> has no response layout described anywhere available — not in the
    /// app's own resource tables, not in any community library — and while <c>4A</c>
    /// is specified in both directions, the event ids it is keyed by are not. Neither
    /// can be decoded from a specification, only from a recording of a real
    /// controller answering. The probe exists so that the first question — is there
    /// anything here to record — can be answered without guessing.
    /// </summary>
    [Fact]
    public async Task The_event_log_is_probed_for_but_not_assumed()
    {
        var controller = new VirtualController(stationCount: 8);
        var client = new LnkClient(new SimulatorTransport(controller));

        var capabilities = await client.ProbeCapabilitiesAsync();

        // False here is the simulator being honest rather than the commands being
        // unavailable in general: it cannot emulate a reply nobody has seen, and
        // inventing one would mean testing a decoder against our own fiction.
        Assert.False(capabilities.SupportsControllerLog);
        Assert.False(capabilities.SupportsEventTimestamps);
    }

    [Fact]
    public async Task A_controller_with_schedule_pages_does_not_require_software_scheduling()
    {
        var controller = new VirtualController(stationCount: 8);
        var client = new LnkClient(new SimulatorTransport(controller));

        var capabilities = await client.ProbeCapabilitiesAsync();

        Assert.True(capabilities.SupportsSchedulePages);
        Assert.False(capabilities.RequiresSoftwareScheduling);
    }

    /// <summary>
    /// SIP <c>48</c> reports whether automatic watering is enabled, not whether a zone
    /// is open: an idle ESP-ME3 answers <c>C801</c>.
    /// </summary>
    [Fact]
    public async Task Irrigation_state_means_enabled_not_watering()
    {
        var controller = new VirtualController(stationCount: 10);
        var client = new LnkClient(new SimulatorTransport(controller));

        var state = await client.GetCombinedStateAsync();

        Assert.True(state.ControllerEnabled);
        Assert.False(state.IsWatering);
    }
}
