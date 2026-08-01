using RainBird.Protocol;
using RainBird.Simulator;

namespace RainBird.Protocol.Tests;

public class LnkClientTests
{
    private static (LnkClient Client, VirtualController Controller) Build(
        ControllerModel? model = null, int stations = 8, TimeProvider? time = null)
    {
        var controller = new VirtualController(model, stations, time);
        return (new LnkClient(new SimulatorTransport(controller)), controller);
    }

    [Fact]
    public async Task Reads_model_and_version()
    {
        var (client, _) = Build();

        var result = await client.GetModelAndVersionAsync();

        Assert.Equal("0009", result.ModelId);
        Assert.Equal("ESP-ME3", result.Model.Series);
        Assert.True(result.Model.IsProgramBased);
    }

    [Fact]
    public async Task Reads_serial_number_as_raw_hex()
    {
        var (client, controller) = Build();
        controller.SerialNumber = "DEADBEEFCAFE0001";

        Assert.Equal("DEADBEEFCAFE0001", await client.GetSerialNumberAsync());
    }

    [Fact]
    public async Task Reads_firmware_version()
    {
        var (client, controller) = Build();
        controller.Firmware = new FirmwareVersion(2, 4, 17);

        var firmware = await client.GetFirmwareVersionAsync();

        Assert.Equal(2, firmware.Major);
        Assert.Equal(17, firmware.Patch);
        Assert.Equal("2.4.17", firmware.ToString());
    }

    [Fact]
    public async Task Discovers_the_available_stations()
    {
        var (client, _) = Build(stations: 12);

        var stations = await client.GetAvailableStationsAsync();

        Assert.Equal(12, stations.Count);
        Assert.Equal(Enumerable.Range(1, 12), stations.Stations);
    }

    [Fact]
    public async Task Combined_state_reports_an_idle_controller()
    {
        var (client, _) = Build();

        var state = await client.GetCombinedStateAsync();

        Assert.True(state.ControllerEnabled);
        Assert.Equal(0, state.ActiveStation);
        Assert.Equal(0, state.RemainingRuntimeSeconds);
        Assert.False(state.IsWatering);
    }

    [Fact]
    public async Task Running_a_station_shows_up_in_combined_state()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 31, 14, 30, 0, TimeSpan.Zero));
        var (client, _) = Build(time: time);

        await client.RunStationAsync(station: 3, minutes: 5);
        var state = await client.GetCombinedStateAsync();

        Assert.True(state.IsWatering);
        Assert.Equal(3, state.ActiveStation);
        Assert.Equal(300, state.RemainingRuntimeSeconds);
    }

    [Fact]
    public async Task Remaining_runtime_counts_down_and_the_run_ends()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 31, 14, 30, 0, TimeSpan.Zero));
        var (client, _) = Build(time: time);

        await client.RunStationAsync(station: 1, minutes: 10);

        time.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(360, (await client.GetCombinedStateAsync()).RemainingRuntimeSeconds);

        time.Advance(TimeSpan.FromMinutes(7));
        var finished = await client.GetCombinedStateAsync();
        Assert.False(finished.IsWatering);
        Assert.Equal(0, finished.ActiveStation);
    }

    [Fact]
    public async Task Stop_halts_a_running_station()
    {
        var (client, _) = Build();

        await client.RunStationAsync(2, 30);
        Assert.True((await client.GetCombinedStateAsync()).IsWatering);

        await client.StopIrrigationAsync();
        Assert.False((await client.GetCombinedStateAsync()).IsWatering);
    }

    [Fact]
    public async Task Active_stations_bitmask_tracks_the_running_station()
    {
        var (client, _) = Build();

        await client.RunStationAsync(5, 10);
        var active = await client.GetActiveStationsAsync();

        Assert.Equal([5], active.Stations);
    }

    [Fact]
    public async Task Running_a_program_queues_every_zone_in_order()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 31, 6, 0, 0, TimeSpan.Zero));
        var (client, _) = Build(stations: 4, time: time);

        await client.RunProgramAsync(0);

        // Default simulated program runs every station for 10 minutes.
        Assert.Equal(1, (await client.GetCombinedStateAsync()).ActiveStation);

        time.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(2, (await client.GetCombinedStateAsync()).ActiveStation);

        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(3, (await client.GetCombinedStateAsync()).ActiveStation);
    }

    [Fact]
    public async Task Advance_skips_to_the_next_queued_station()
    {
        var (client, _) = Build(stations: 4);

        await client.RunProgramAsync(0);
        Assert.Equal(1, (await client.GetCombinedStateAsync()).ActiveStation);

        await client.AdvanceStationAsync();
        Assert.Equal(2, (await client.GetCombinedStateAsync()).ActiveStation);
    }

    [Fact]
    public async Task Test_all_stations_walks_every_zone()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 31, 9, 0, 0, TimeSpan.Zero));
        var (client, _) = Build(stations: 3, time: time);

        await client.TestAllStationsAsync(2);

        Assert.Equal(1, (await client.GetCombinedStateAsync()).ActiveStation);
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(2, (await client.GetCombinedStateAsync()).ActiveStation);
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(3, (await client.GetCombinedStateAsync()).ActiveStation);
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(0, (await client.GetCombinedStateAsync()).ActiveStation);
    }

    [Fact]
    public async Task Stacking_a_station_queues_it_behind_the_current_run()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 31, 9, 0, 0, TimeSpan.Zero));
        var (client, _) = Build(time: time);

        await client.RunStationAsync(1, 5);
        await client.StackStationAsync(7, 3);

        Assert.Equal(1, (await client.GetCombinedStateAsync()).ActiveStation);
        time.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(7, (await client.GetCombinedStateAsync()).ActiveStation);
    }

    [Fact]
    public async Task Rain_delay_round_trips()
    {
        var (client, _) = Build();

        Assert.Equal(0, await client.GetRainDelayAsync());

        await client.SetRainDelayAsync(3);

        Assert.Equal(3, await client.GetRainDelayAsync());
        Assert.Equal(3, (await client.GetCombinedStateAsync()).RainDelayDays);
    }

    [Fact]
    public async Task Rain_delay_rejects_out_of_range_values()
    {
        var (client, _) = Build();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SetRainDelayAsync(-1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SetRainDelayAsync(15));
    }

    [Fact]
    public async Task Rain_sensor_state_is_reported()
    {
        var (client, controller) = Build();

        Assert.Equal(RainSensorState.Dry, await client.GetRainSensorStateAsync());

        controller.SensorState = RainSensorState.Wet;
        Assert.Equal(RainSensorState.Wet, await client.GetRainSensorStateAsync());
        Assert.Equal(RainSensorState.Wet, (await client.GetCombinedStateAsync()).SensorState);
    }

    [Fact]
    public async Task Seasonal_adjust_round_trips()
    {
        var (client, _) = Build();

        await client.SetWaterBudgetAsync(program: 0, percent: 125);

        var budget = await client.GetWaterBudgetAsync(0);
        Assert.Equal(125, budget.SeasonalAdjustPercent);
        Assert.Equal(125, (await client.GetCombinedStateAsync()).SeasonalAdjustPercent);
    }

    [Fact]
    public async Task Per_zone_seasonal_adjust_returns_one_factor_per_station()
    {
        var (client, controller) = Build();
        controller.ZoneSeasonalAdjustFactors[0] = 80;
        controller.ZoneSeasonalAdjustFactors[1] = 120;

        var adjust = await client.GetZoneSeasonalAdjustAsync(0);

        Assert.Equal(16, adjust.FactorsByStation.Count);
        Assert.Equal(80, adjust.FactorsByStation[0]);
        Assert.Equal(120, adjust.FactorsByStation[1]);
    }

    [Fact]
    public async Task Controller_clock_can_be_read()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 31, 14, 30, 45, TimeSpan.Zero));
        var (client, _) = Build(time: time);

        Assert.Equal(new TimeOnly(14, 30, 45), await client.GetControllerTimeAsync());
        Assert.Equal(new DateOnly(2026, 7, 31), await client.GetControllerDateAsync());
    }

    [Fact]
    public async Task Setting_the_clock_is_acknowledged()
    {
        var (client, _) = Build();

        // These would throw on a NAK; reaching the assertion means they were accepted.
        await client.SetControllerTimeAsync(new TimeOnly(6, 15, 0));
        await client.SetControllerDateAsync(new DateOnly(2026, 7, 31));
    }

    [Fact]
    public async Task Running_an_invalid_station_is_rejected_by_the_controller()
    {
        var (client, _) = Build(stations: 4);

        var ex = await Assert.ThrowsAsync<RainBirdNakException>(() => client.RunStationAsync(9, 5));
        Assert.Equal(NakReason.IncompatibleData, ex.Reason);
    }

    [Fact]
    public async Task Unsupported_commands_surface_as_a_nak_naming_the_command()
    {
        var (client, controller) = Build();
        controller.UnsupportedCommands.Add(SipCommand.IrrigationStatistics);

        var ex = await Assert.ThrowsAsync<RainBirdNakException>(
            () => client.TunnelAsync(SipCodec.Encode(SipCommand.IrrigationStatistics, 0, 0)));

        Assert.Equal((byte)SipCommand.IrrigationStatistics, ex.EchoedCommand);
        Assert.Equal(NakReason.CommandNotSupported, ex.Reason);
    }

    [Fact]
    public async Task Command_support_probing_reflects_the_model()
    {
        var (client, _) = Build();

        Assert.True(await client.IsCommandSupportedAsync(SipCommand.IrrigationStatistics));
        // Flow monitoring is an LX-series feature; an ESP-ME3 should report no support.
        Assert.False(await client.IsCommandSupportedAsync(SipCommand.FlowMonitorStatus));
    }

    [Fact]
    public async Task Capability_probe_builds_a_complete_profile()
    {
        var (client, _) = Build(stations: 6);

        var capabilities = await client.ProbeCapabilitiesAsync();

        Assert.Equal("ESP-ME3", capabilities.Model.Series);
        Assert.Equal(6, capabilities.StationCount);
        Assert.Equal(Enumerable.Range(1, 6), capabilities.Stations);
        Assert.True(capabilities.SupportsSchedulePages);
        Assert.False(capabilities.SupportsFlowMonitoring);
        Assert.True(capabilities.SupportsIrrigationStatistics);
    }

    [Fact]
    public async Task Capability_probe_marks_flow_monitoring_on_an_lx_controller()
    {
        var (client, _) = Build(ControllerModels.Lookup("000E")); // LX-IVM Pro

        var capabilities = await client.ProbeCapabilitiesAsync();

        Assert.Equal("LX-IVM Pro", capabilities.Model.Series);
        Assert.True(capabilities.SupportsFlowMonitoring);
    }

    [Fact]
    public async Task Non_program_based_models_report_no_schedule_support()
    {
        var (client, _) = Build(ControllerModels.Lookup("0003")); // ESP-RZXe

        var capabilities = await client.ProbeCapabilitiesAsync();

        Assert.False(capabilities.Model.IsProgramBased);
        Assert.False(capabilities.SupportsSchedulePages);
    }

    [Fact]
    public async Task Schedule_pages_round_trip()
    {
        var (client, _) = Build();

        // Program 0 info page: Mon/Wed/Fri, cyclic 3, 0 remaining, no perm-off, 110%, custom days
        const string payload = "2A0300006E00";
        await client.SetSchedulePageAsync(15, payload);

        var read = await client.GetSchedulePageAsync(15);

        Assert.StartsWith("A0000F", read);
        Assert.Contains(payload, read);
    }

    [Fact]
    public async Task Writing_a_program_page_updates_the_controllers_program()
    {
        var (client, controller) = Build();

        // dayMask 2A = Mon/Wed/Fri, cyclic 03, remaining 00, permOff 00, SA 6E (110), freq 00
        await client.SetSchedulePageAsync(15, "2A03000 06E00".Replace(" ", ""));

        var program = controller.Programs[0];
        Assert.Equal(FrequencyType.CustomDays, program.Frequency);
        Assert.Equal(110, program.SeasonalAdjustPercent);
        Assert.Equal(3, program.CyclicDays);
        Assert.Equal([false, true, false, true, false, true, false], program.CustomDays);
    }

    [Fact]
    public async Task Non_program_based_models_reject_schedule_reads()
    {
        var (client, _) = Build(ControllerModels.Lookup("0003"));

        var ex = await Assert.ThrowsAsync<RainBirdNakException>(() => client.GetSchedulePageAsync(15));
        Assert.Equal(NakReason.CommandNotSupported, ex.Reason);
    }

    [Fact]
    public async Task Disabling_the_controller_stops_watering()
    {
        var (client, controller) = Build();

        await client.RunStationAsync(1, 20);
        await client.SetControllerEnabledAsync(false);

        Assert.False(controller.Enabled);
        Assert.False((await client.GetCombinedStateAsync()).IsWatering);
    }

    [Fact]
    public async Task Requests_are_serialised_even_when_issued_concurrently()
    {
        var controller = new VirtualController();
        var transport = new SimulatorTransport(controller) { Latency = TimeSpan.FromMilliseconds(1) };
        var client = new LnkClient(transport);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => client.GetCombinedStateAsync()));

        // Every request reached the controller and none corrupted another's response.
        Assert.Equal(20, controller.CommandLog.Count(c => c == "4C"));
    }
}

/// <summary>A controllable clock, so run timing is deterministic in tests.</summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public FakeTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
