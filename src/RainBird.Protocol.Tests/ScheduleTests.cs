using RainBird.Protocol;
using RainBird.Simulator;

namespace RainBird.Protocol.Tests;

public class SchedulePageLayoutTests
{
    [Theory]
    [InlineData(1, 128)]
    [InlineData(2, 128)]
    [InlineData(3, 129)]
    [InlineData(4, 129)]
    [InlineData(5, 130)]
    [InlineData(22, 138)]
    public void Run_time_pages_hold_station_pairs(int station, int expectedPage)
    {
        Assert.Equal(expectedPage, SchedulePages.RunTimes(station));
    }

    [Theory]
    [InlineData(1, 1, 2)]
    [InlineData(2, 1, 2)]
    [InlineData(7, 7, 8)]
    [InlineData(8, 7, 8)]
    public void Station_pair_always_leads_with_the_odd_station(int station, int odd, int even)
    {
        Assert.Equal((odd, even), SchedulePages.StationPair(station));
    }

    [Fact]
    public void Odd_stations_are_first_of_their_pair()
    {
        Assert.True(SchedulePages.IsFirstOfPair(1));
        Assert.False(SchedulePages.IsFirstOfPair(2));
    }

    [Fact]
    public void Program_and_start_time_pages_use_their_documented_bases()
    {
        Assert.Equal(15, SchedulePages.ProgramInfo(0));
        Assert.Equal(18, SchedulePages.ProgramInfo(3));
        Assert.Equal(95, SchedulePages.StartTimes(0));
        Assert.Equal(98, SchedulePages.StartTimes(3));
    }

    [Fact]
    public void Program_info_round_trips()
    {
        var page = new SchedulePages.ProgramInfoPage(
            CustomDays: [false, true, false, true, false, true, false], // Mon/Wed/Fri
            CyclicDays: 3,
            DaysRemaining: 1,
            PermanentDaysOff: 0,
            SeasonalAdjustPercent: 110,
            Frequency: FrequencyType.CustomDays);

        var encoded = SchedulePages.EncodeProgramInfo(page);
        var decoded = SchedulePages.DecodeProgramInfo(encoded);

        Assert.Equal(page.CustomDays, decoded.CustomDays);
        Assert.Equal(3, decoded.CyclicDays);
        Assert.Equal(1, decoded.DaysRemaining);
        Assert.Equal(110, decoded.SeasonalAdjustPercent);
        Assert.Equal(FrequencyType.CustomDays, decoded.Frequency);
    }

    [Fact]
    public void Program_info_day_mask_is_little_endian_by_weekday()
    {
        // Sunday only → bit 0.
        var sunday = SchedulePages.EncodeProgramInfo(new SchedulePages.ProgramInfoPage(
            [true, false, false, false, false, false, false], 0, 0, 0, 100, FrequencyType.CustomDays));
        Assert.StartsWith("01", sunday);

        // Saturday only → bit 6.
        var saturday = SchedulePages.EncodeProgramInfo(new SchedulePages.ProgramInfoPage(
            [false, false, false, false, false, false, true], 0, 0, 0, 100, FrequencyType.CustomDays));
        Assert.StartsWith("40", saturday);
    }

    [Theory]
    [InlineData(FrequencyType.CustomDays, 0)]
    [InlineData(FrequencyType.Cyclic, 1)]
    [InlineData(FrequencyType.OddDays, 2)]
    [InlineData(FrequencyType.EvenDays, 3)]
    public void Frequency_type_uses_the_apps_encoding(FrequencyType frequency, int expected)
    {
        var encoded = SchedulePages.EncodeProgramInfo(new SchedulePages.ProgramInfoPage(
            [false, false, false, false, false, false, false], 0, 0, 0, 100, frequency));

        Assert.EndsWith($"{expected:X2}", encoded);
    }

    [Fact]
    public void Start_times_are_sixteen_bit_minutes_from_midnight()
    {
        // 05:15 = 315 minutes = 0x013B
        var encoded = SchedulePages.EncodeStartTimes([315, 0, SchedulePages.UnsetStartTime]);

        Assert.Equal("013B0000FFFF", encoded);
        Assert.Equal([315, 0, 65535], SchedulePages.DecodeStartTimes(encoded));
    }

    [Fact]
    public void Unset_start_times_are_recognised()
    {
        Assert.True(SchedulePages.IsStartTimeSet(315));
        Assert.True(SchedulePages.IsStartTimeSet(0));
        Assert.False(SchedulePages.IsStartTimeSet(SchedulePages.UnsetStartTime));
        Assert.False(SchedulePages.IsStartTimeSet(1440));
    }

    [Fact]
    public void Run_time_page_holds_one_value_per_program_for_two_stations()
    {
        var page = new SchedulePages.RunTimePage(
            OddStationRunTimes: [10, 20, 0, 0],
            EvenStationRunTimes: [5, 0, 0, 15]);

        var encoded = SchedulePages.EncodeRunTimes(page, maxPrograms: 4);

        // 4 programs x 2 bytes x 2 stations = 16 bytes.
        Assert.Equal(32, encoded.Length);
        Assert.Equal("000A0014000000000005000000000 00F".Replace(" ", ""), encoded);

        var decoded = SchedulePages.DecodeRunTimes(encoded, maxPrograms: 4);
        Assert.Equal([10, 20, 0, 0], decoded.OddStationRunTimes);
        Assert.Equal([5, 0, 0, 15], decoded.EvenStationRunTimes);
    }

    [Fact]
    public void Global_info_round_trips_including_the_sensor_bypass_flag()
    {
        var page = new SchedulePages.GlobalInfoPage(
            InterStationDelaySeconds: 30, Snooze: 2, RainSensorBypassed: true);

        var encoded = SchedulePages.EncodeGlobalInfo(page);
        Assert.Equal("001E0201", encoded);

        var decoded = SchedulePages.DecodeGlobalInfo(encoded);
        Assert.Equal(30, decoded.InterStationDelaySeconds);
        Assert.Equal(2, decoded.Snooze);
        Assert.True(decoded.RainSensorBypassed);
    }

    [Fact]
    public void Payload_strips_the_schedule_response_header()
    {
        // A0 | 00 | page 0F | payload
        Assert.Equal("2A0300006E00", SchedulePages.PayloadOf("A0000F2A0300006E00"));
    }

    [Fact]
    public void A_truncated_page_is_reported_rather_than_silently_misread()
    {
        var ex = Assert.Throws<RainBirdProtocolException>(() => SchedulePages.DecodeProgramInfo("2A03"));
        Assert.Contains("needs 12 hex characters", ex.Message);
    }
}

public class ScheduleClientTests
{
    private static async Task<(ScheduleClient Schedule, VirtualController Controller, LnkClient Client)> BuildAsync(
        int stations = 8)
    {
        var controller = new VirtualController(stationCount: stations);
        var client = new LnkClient(new SimulatorTransport(controller));
        var capabilities = await client.ProbeCapabilitiesAsync();
        return (new ScheduleClient(client, capabilities), controller, client);
    }

    [Fact]
    public async Task Reads_a_program_across_all_of_its_pages()
    {
        var (schedule, _, _) = await BuildAsync();

        var program = await schedule.GetProgramAsync(0);

        Assert.Equal(0, program.ProgramNumber);
        Assert.Equal(FrequencyType.CustomDays, program.Frequency);
        Assert.Equal([false, true, false, true, false, true, false], program.CustomDays);
        Assert.Equal(315, program.StartTimes[0]); // 05:15, the simulator's default
        Assert.Equal(8, program.StationRunTimes.Count);
        Assert.Equal(10, program.StationRunTimes[1]);
    }

    [Fact]
    public async Task Saving_a_program_round_trips_every_field()
    {
        var (schedule, _, _) = await BuildAsync(stations: 4);

        var original = await schedule.GetProgramAsync(0);
        var edited = original with
        {
            Frequency = FrequencyType.Cyclic,
            CyclicDays = 4,
            CustomDays = [true, false, true, false, true, false, true],
            SeasonalAdjustPercent = 125,
            StartTimes = [360, 1080],                      // 06:00 and 18:00
            StationRunTimes = new Dictionary<int, int> { [1] = 12, [2] = 7, [3] = 0, [4] = 20 },
        };

        await schedule.SaveProgramAsync(edited);
        var reloaded = await schedule.GetProgramAsync(0);

        Assert.Equal(FrequencyType.Cyclic, reloaded.Frequency);
        Assert.Equal(4, reloaded.CyclicDays);
        Assert.Equal(125, reloaded.SeasonalAdjustPercent);
        Assert.Equal([true, false, true, false, true, false, true], reloaded.CustomDays);
        Assert.Equal(360, reloaded.StartTimes[0]);
        Assert.Equal(1080, reloaded.StartTimes[1]);
        Assert.Equal(12, reloaded.StationRunTimes[1]);
        Assert.Equal(7, reloaded.StationRunTimes[2]);
        Assert.Equal(0, reloaded.StationRunTimes[3]);
        Assert.Equal(20, reloaded.StationRunTimes[4]);
    }

    /// <summary>
    /// Run-time pages carry values for every program, so writing one program must be a
    /// read-modify-write or it silently wipes the others.
    /// </summary>
    [Fact]
    public async Task Saving_one_program_leaves_the_others_run_times_intact()
    {
        var (schedule, _, _) = await BuildAsync(stations: 4);

        var programB = await schedule.GetProgramAsync(1);
        await schedule.SaveProgramAsync(programB with
        {
            StationRunTimes = new Dictionary<int, int> { [1] = 33, [2] = 33, [3] = 33, [4] = 33 },
        });

        var programA = await schedule.GetProgramAsync(0);
        await schedule.SaveProgramAsync(programA with
        {
            StationRunTimes = new Dictionary<int, int> { [1] = 11, [2] = 11, [3] = 11, [4] = 11 },
        });

        var reloadedB = await schedule.GetProgramAsync(1);
        Assert.Equal(33, reloadedB.StationRunTimes[1]);
        Assert.Equal(33, reloadedB.StationRunTimes[4]);

        var reloadedA = await schedule.GetProgramAsync(0);
        Assert.Equal(11, reloadedA.StationRunTimes[1]);
    }

    [Fact]
    public async Task Reads_every_program_the_model_supports()
    {
        var (schedule, _, _) = await BuildAsync();

        var programs = await schedule.GetAllProgramsAsync();

        // ESP-ME3 supports four programs.
        Assert.Equal(4, programs.Count);
        Assert.Equal([0, 1, 2, 3], programs.Select(p => p.ProgramNumber));
    }

    [Fact]
    public async Task Asking_for_a_program_the_model_does_not_have_is_rejected_clearly()
    {
        var (schedule, _, _) = await BuildAsync();

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => schedule.GetProgramAsync(9));
        Assert.Contains("ESP-ME3 supports programs 0-3", ex.Message);
    }

    [Fact]
    public async Task Disabling_a_station_zeroes_its_run_time_in_every_program()
    {
        var (schedule, _, _) = await BuildAsync(stations: 4);

        await schedule.SetStationEnabledAsync(station: 2, enabled: false);

        foreach (var program in await schedule.GetAllProgramsAsync())
        {
            Assert.Equal(0, program.StationRunTimes[2]);
            // Its pair partner must be untouched.
            Assert.NotEqual(0, program.StationRunTimes[1]);
        }
    }

    [Fact]
    public async Task Re_enabling_a_station_restores_a_run_time()
    {
        var (schedule, _, _) = await BuildAsync(stations: 4);

        await schedule.SetStationEnabledAsync(2, enabled: false);
        await schedule.SetStationEnabledAsync(2, enabled: true, restoreMinutes: 15);

        var program = await schedule.GetProgramAsync(0);
        Assert.Equal(15, program.StationRunTimes[2]);
    }

    [Fact]
    public async Task Global_schedule_settings_round_trip()
    {
        var (schedule, controller, _) = await BuildAsync();

        await schedule.SaveGlobalInfoAsync(
            new SchedulePages.GlobalInfoPage(InterStationDelaySeconds: 45, Snooze: 1, RainSensorBypassed: true));

        var global = await schedule.GetGlobalInfoAsync();

        Assert.Equal(45, global.InterStationDelaySeconds);
        Assert.True(global.RainSensorBypassed);
        Assert.Equal(45, controller.InterStationDelaySeconds);
    }

    [Fact]
    public async Task A_program_with_no_start_time_reports_itself_disabled()
    {
        var (schedule, _, _) = await BuildAsync(stations: 4);

        var program = await schedule.GetProgramAsync(0);
        Assert.True(program.Enabled);

        await schedule.SaveProgramAsync(program with
        {
            StartTimes = [SchedulePages.UnsetStartTime, SchedulePages.UnsetStartTime],
        });

        Assert.False((await schedule.GetProgramAsync(0)).Enabled);
    }
}
