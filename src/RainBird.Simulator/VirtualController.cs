using System.Globalization;
using RainBird.Protocol;

namespace RainBird.Simulator;

/// <summary>
/// An in-memory Rain Bird controller that speaks the real SIP protocol.
///
/// This exists so the entire stack — client, server, UI — can be built and verified
/// without a sprinkler controller on the desk. It implements the wire format exactly
/// as documented in <c>docs/rainbird-protocol.md</c>, including the quirks: nibble
/// field packing, the 12-bit year, NAK codes for unsupported commands, and the
/// station bitmask paging.
/// </summary>
public sealed class VirtualController
{
    private readonly Lock _lock = new();
    private readonly TimeProvider _time;

    private readonly Queue<PendingRun> _queue = new();
    private RunningStation? _running;

    public VirtualController(ControllerModel? model = null, int stationCount = 8, TimeProvider? timeProvider = null)
    {
        Model = model ?? ControllerModels.Lookup("0009"); // ESP-ME3 by default
        StationCount = stationCount;
        _time = timeProvider ?? TimeProvider.System;

        SerialNumber = "0102030405060708";
        Firmware = new FirmwareVersion(2, 4, 0);

        Programs = Enumerable.Range(0, Math.Max(1, Model.MaxPrograms))
            .Select(CreateDefaultProgram)
            .ToList();

        ZoneSeasonalAdjustFactors = Enumerable.Repeat(100, 16).ToList();
    }

    public ControllerModel Model { get; }
    public int StationCount { get; }
    public string SerialNumber { get; set; }
    public FirmwareVersion Firmware { get; set; }
    public int RainDelayDays { get; set; }
    public RainSensorState SensorState { get; set; } = RainSensorState.Dry;
    public int SeasonalAdjustPercent { get; set; } = 100;
    public bool Enabled { get; set; } = true;

    /// <summary>Delay between stations, from the global schedule page.</summary>
    public int InterStationDelaySeconds { get; set; }
    public int Snooze { get; set; }
    public bool RainSensorBypassed { get; set; }

    /// <summary>
    /// Firmware differences the simulator can reproduce. A physical ESP-ME3 on
    /// protocol 2.12 supports none of these, so tests can model that firmware rather
    /// than only the generation the app's resource table describes.
    /// </summary>
    public bool SupportsSchedulePages { get; set; } = true;
    public bool SupportsCombinedState { get; set; } = true;
    public bool SupportsControllerToggle { get; set; } = true;
    public List<int> ZoneSeasonalAdjustFactors { get; }
    public List<SimulatedProgram> Programs { get; }

    /// <summary>Schedule pages the controller has been told to store, keyed by page number.</summary>
    public Dictionary<int, string> StoredSchedulePages { get; } = new();

    /// <summary>Every command the simulator has served — useful in tests.</summary>
    public List<string> CommandLog { get; } = [];

    /// <summary>Commands the simulator should refuse, so tests can exercise NAK handling.</summary>
    public HashSet<SipCommand> UnsupportedCommands { get; } = [];

    private SimulatedProgram CreateDefaultProgram(int index) => new()
    {
        Number = index,
        Frequency = FrequencyType.CustomDays,
        CustomDays = [false, true, false, true, false, true, false],
        CyclicDays = 2,
        DaysRemaining = 0,
        SeasonalAdjustPercent = 100,
        StartTimes = Enumerable.Range(0, Model.MaxStartTimes)
            .Select(i => i == 0 ? 5 * 60 + 15 : 0xFFFF)
            .ToList(),
        StationRunTimes = Enumerable.Range(1, StationCount)
            .ToDictionary(s => s, _ => 10),
    };

    // ------------------------------------------------------------- run state

    private sealed record RunningStation(int Station, DateTimeOffset EndsAt, RunTrigger Trigger);
    private sealed record PendingRun(int Station, int Minutes, RunTrigger Trigger);

    /// <summary>Advances the internal clock model. Called before serving any state query.</summary>
    private void Tick()
    {
        var now = _time.GetUtcNow();

        if (_running is { } running && now >= running.EndsAt)
            _running = null;

        if (_running is null && _queue.Count > 0)
        {
            var next = _queue.Dequeue();
            _running = new RunningStation(next.Station, now.AddMinutes(next.Minutes), next.Trigger);
        }
    }

    private void StartRun(int station, int minutes, RunTrigger trigger)
    {
        var now = _time.GetUtcNow();
        _running = new RunningStation(station, now.AddMinutes(minutes), trigger);
    }

    private void StopAll()
    {
        _running = null;
        _queue.Clear();
    }

    // ------------------------------------------------------- SIP dispatching

    /// <summary>
    /// Processes one SIP command and returns the response hex, exactly as a real
    /// controller would.
    /// </summary>
    public string Execute(string commandHex)
    {
        lock (_lock)
        {
            CommandLog.Add(commandHex);
            Tick();

            if (commandHex.Length < 2)
                return Nak(0x00, NakReason.BadLength);

            var command = (SipCommand)byte.Parse(
                commandHex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

            if (UnsupportedCommands.Contains(command))
                return Nak((byte)command, NakReason.CommandNotSupported);

            var args = ParseArgs(commandHex);

            return command switch
            {
                SipCommand.ModelAndVersion => $"82{Model.ModelId}0201",
                SipCommand.SerialNumber => $"85{SerialNumber}",
                SipCommand.ControllerFirmwareVersion =>
                    $"8B{Firmware.Major:X2}{Firmware.Minor:X2}{Firmware.Patch:X4}",
                SipCommand.CommandSupport => CommandSupport(args),
                SipCommand.AvailableStations => AvailableStations(args),
                SipCommand.CurrentStationsActive => ActiveStations(args),
                SipCommand.CurrentTime => CurrentTime(),
                SipCommand.CurrentDate => CurrentDate(),
                SipCommand.SetTime => Ack(command),
                SipCommand.SetDate => Ack(command),
                SipCommand.CombinedControllerState => SupportsCombinedState
                    ? CombinedState()
                    : Nak((byte)command, NakReason.CommandNotSupported),
                SipCommand.CurrentIrrigationState => $"C8{(Enabled ? 1 : 0):X2}",
                SipCommand.CurrentRainSensorState => $"BE{(int)SensorState:X2}",
                SipCommand.GetRainDelay => $"B6{RainDelayDays:X4}",
                SipCommand.SetRainDelay => SetRainDelay(args, command),
                SipCommand.GetWaterBudget => GetWaterBudget(args),
                SipCommand.SetWaterBudget => SetWaterBudget(args, command),
                SipCommand.GetZonesSeasonalAdjust => GetZoneSeasonalAdjust(args),
                SipCommand.ManuallyRunStation => RunStation(commandHex, command),
                SipCommand.StackManuallyRunStation => StackStation(commandHex, command),
                SipCommand.ManuallyRunProgram => RunProgram(args, command),
                SipCommand.TestStations => TestStations(args, command),
                SipCommand.StopIrrigation => StopIrrigation(command),
                SipCommand.AdvanceStation => Advance(command),
                SipCommand.SetControllerState => SupportsControllerToggle
                    ? SetControllerState(args, command)
                    : Nak((byte)command, NakReason.CommandNotSupported),
                SipCommand.RetrieveSchedule => RetrieveSchedule(commandHex),
                SipCommand.SetSchedule => SetSchedule(commandHex, command),
                _ => Nak((byte)command, NakReason.CommandNotSupported),
            };
        }
    }

    private static byte[] ParseArgs(string hex)
    {
        var argChars = hex.Length - 2;
        var args = new byte[argChars / 2];
        for (var i = 0; i < args.Length; i++)
            args[i] = byte.Parse(hex.AsSpan(2 + i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return args;
    }

    private static string Ack(SipCommand command) => $"01{(byte)command:X2}";
    private static string Nak(byte command, NakReason reason) => $"00{command:X2}{(byte)reason:X2}";

    // ------------------------------------------------------- handlers

    private string CommandSupport(byte[] args)
    {
        if (args.Length < 1) return Nak((byte)SipCommand.CommandSupport, NakReason.BadLength);
        var probed = (SipCommand)args[0];
        var supported = !UnsupportedCommands.Contains(probed) && IsImplemented(probed);
        return $"84{args[0]:X2}{(supported ? 1 : 0):X2}";
    }

    private bool IsImplemented(SipCommand command) => command switch
    {
        SipCommand.FlowMonitorStatus or SipCommand.FlowMonitorRate or SipCommand.StartLearnFlowSequence =>
            Model.Series.StartsWith("LX", StringComparison.Ordinal),
        SipCommand.IrrigationStatistics => true,
        SipCommand.GetZonesSeasonalAdjust => true,
        SipCommand.CurrentStationError => true,
        SipCommand.RetrieveSchedule or SipCommand.SetSchedule => Model.IsProgramBased && SupportsSchedulePages,
        SipCommand.CombinedControllerState => SupportsCombinedState,
        SipCommand.SetControllerState => SupportsControllerToggle,
        _ => true,
    };

    private string AvailableStations(byte[] args)
    {
        var page = args.Length > 0 ? args[0] : 0;
        uint mask = 0;
        for (var station = page * 32 + 1; station <= Math.Min(StationCount, (page + 1) * 32); station++)
            mask |= 1u << (station - 1 - page * 32);
        return $"83{page:X2}{LittleEndianMask(mask)}";
    }

    /// <summary>
    /// Station masks go out least-significant byte first: byte 0 carries stations 1-8.
    /// Confirmed against a physical ESP-ME3, which reports ten stations as FF030000.
    /// </summary>
    private static string LittleEndianMask(uint mask) =>
        $"{(byte)mask:X2}{(byte)(mask >> 8):X2}{(byte)(mask >> 16):X2}{(byte)(mask >> 24):X2}";

    private string ActiveStations(byte[] args)
    {
        var page = args.Length > 0 ? args[0] : 0;
        uint mask = 0;
        if (_running is { } running)
        {
            var index = running.Station - 1 - page * 32;
            if (index is >= 0 and < 32) mask = 1u << index;
        }
        return $"BF{page:X2}{LittleEndianMask(mask)}";
    }

    private string CurrentTime()
    {
        var now = _time.GetLocalNow();
        return $"90{now.Hour:X2}{now.Minute:X2}{now.Second:X2}";
    }

    private string CurrentDate()
    {
        var now = _time.GetLocalNow();
        return $"92{now.Day:X2}{now.Month:X1}{now.Year:X3}";
    }

    private string CombinedState()
    {
        var now = _time.GetLocalNow();
        var remaining = _running is { } r
            ? Math.Max(0, (int)(r.EndsAt - _time.GetUtcNow()).TotalSeconds)
            : 0;
        var station = _running?.Station ?? 0;
        var state = Enabled ? 1 : 0;

        return "CC"
             + $"{now.Hour:X2}{now.Minute:X2}{now.Second:X2}"
             + $"{now.Day:X2}{now.Month:X1}{now.Year:X3}"
             + $"{RainDelayDays:X4}"
             + $"{(int)SensorState:X2}"
             + $"{state:X2}"
             + $"{SeasonalAdjustPercent:X4}"
             + $"{remaining:X4}"
             + $"{station:X2}";
    }

    private string SetRainDelay(byte[] args, SipCommand command)
    {
        if (args.Length < 2) return Nak((byte)command, NakReason.BadLength);
        // The high bit of the first byte is a "user set" flag; the value is the low bits.
        RainDelayDays = ((args[0] & 0x7F) << 8) | args[1];
        return Ack(command);
    }

    private string GetWaterBudget(byte[] args)
    {
        var program = args.Length > 0 ? args[0] : 0;
        var percent = program < Programs.Count ? Programs[program].SeasonalAdjustPercent : SeasonalAdjustPercent;
        return $"B0{program:X2}{percent:X4}";
    }

    private string SetWaterBudget(byte[] args, SipCommand command)
    {
        if (args.Length < 3) return Nak((byte)command, NakReason.BadLength);
        var program = args[0];
        var percent = (args[1] << 8) | args[2];
        if (program < Programs.Count) Programs[program].SeasonalAdjustPercent = percent;
        SeasonalAdjustPercent = percent;
        return Ack(command);
    }

    private string GetZoneSeasonalAdjust(byte[] args)
    {
        var program = args.Length > 0 ? args[0] : 0;
        var factors = string.Concat(ZoneSeasonalAdjustFactors.Take(16).Select(f => $"{f:X2}"));
        return $"B2{program:X2}{factors.PadRight(32, '0')}";
    }

    /// <summary>
    /// SIP 39: a 16-bit station then an 8-bit duration, four bytes in total.
    ///
    /// The length is enforced exactly as real hardware does. A three-byte form � one
    /// byte per parameter � looks entirely reasonable and is what a casual reading of
    /// the field table invites, but the controller rejects it with a bad-length NAK.
    /// </summary>
    private string RunStation(string hex, SipCommand command)
    {
        if (hex.Length != 8) return Nak((byte)command, NakReason.BadLength);

        var station = Convert.ToInt32(hex.Substring(2, 4), 16);
        var minutes = Convert.ToInt32(hex.Substring(6, 2), 16);

        if (station < 1 || station > StationCount) return Nak((byte)command, NakReason.IncompatibleData);

        StartRun(station, minutes, RunTrigger.Manual);
        return Ack(command);
    }

    private string StackStation(string hex, SipCommand command)
    {
        // 4B <station-hi> <station-lo> <minutes>
        if (hex.Length < 8) return Nak((byte)command, NakReason.BadLength);
        var station = Convert.ToInt32(hex.Substring(2, 4), 16);
        var minutes = Convert.ToInt32(hex.Substring(6, 2), 16);
        if (station < 1 || station > StationCount) return Nak((byte)command, NakReason.IncompatibleData);

        if (_running is null) StartRun(station, minutes, RunTrigger.Manual);
        else _queue.Enqueue(new PendingRun(station, minutes, RunTrigger.Manual));
        return Ack(command);
    }

    private string RunProgram(byte[] args, SipCommand command)
    {
        if (args.Length < 1) return Nak((byte)command, NakReason.BadLength);
        var programNumber = args[0];
        if (programNumber >= Programs.Count) return Nak((byte)command, NakReason.IncompatibleData);

        var program = Programs[programNumber];
        StopAll();
        foreach (var (station, minutes) in program.StationRunTimes.OrderBy(kv => kv.Key))
            if (minutes > 0)
                _queue.Enqueue(new PendingRun(station, minutes, RunTrigger.Program));

        Tick();
        return Ack(command);
    }

    private string TestStations(byte[] args, SipCommand command)
    {
        if (args.Length < 1) return Nak((byte)command, NakReason.BadLength);
        var minutes = Math.Max(1, (int)args[0]);
        StopAll();
        for (var station = 1; station <= StationCount; station++)
            _queue.Enqueue(new PendingRun(station, minutes, RunTrigger.Test));

        Tick();
        return Ack(command);
    }

    private string StopIrrigation(SipCommand command)
    {
        StopAll();
        return Ack(command);
    }

    private string Advance(SipCommand command)
    {
        _running = null;
        Tick();
        return Ack(command);
    }

    private string SetControllerState(byte[] args, SipCommand command)
    {
        if (args.Length < 1) return Nak((byte)command, NakReason.BadLength);
        Enabled = args[0] != 0;
        if (!Enabled) StopAll();
        return Ack(command);
    }

    // ------------------------------------------------------ schedule pages

    private string RetrieveSchedule(string hex)
    {
        if (!Model.IsProgramBased)
            return Nak((byte)SipCommand.RetrieveSchedule, NakReason.CommandNotSupported);
        if (hex.Length < 6)
            return Nak((byte)SipCommand.RetrieveSchedule, NakReason.BadLength);

        var page = Convert.ToInt32(hex.Substring(4, 2), 16);

        if (StoredSchedulePages.TryGetValue(page, out var stored))
            return $"A000{page:X2}{stored}";

        return $"A000{page:X2}{BuildSchedulePage(page)}";
    }

    private string SetSchedule(string hex, SipCommand command)
    {
        if (!Model.IsProgramBased) return Nak((byte)command, NakReason.CommandNotSupported);
        if (hex.Length < 6) return Nak((byte)command, NakReason.BadLength);

        var page = Convert.ToInt32(hex.Substring(4, 2), 16);
        var payload = hex[6..];
        StoredSchedulePages[page] = payload;
        ApplySchedulePage(page, payload);
        return Ack(command);
    }

    /// <summary>
    /// Renders a schedule page from the simulator's program state, using the real
    /// page layout: page 0 is global, 15+n is program info, 95+n is start times, and
    /// 128+⌊(station−1)/2⌋ holds a station pair's run times.
    /// </summary>
    private string BuildSchedulePage(int page)
    {
        if (page == SchedulePages.GlobalInfo)
            return SchedulePages.EncodeGlobalInfo(
                new SchedulePages.GlobalInfoPage(InterStationDelaySeconds, Snooze, RainSensorBypassed));

        if (page >= SchedulePages.ProgramInfoBase && page < SchedulePages.ProgramInfoBase + 16)
        {
            var index = page - SchedulePages.ProgramInfoBase;
            if (index >= Programs.Count) return new string('0', 12);
            var p = Programs[index];

            return SchedulePages.EncodeProgramInfo(new SchedulePages.ProgramInfoPage(
                p.CustomDays, p.CyclicDays, p.DaysRemaining, 0, p.SeasonalAdjustPercent, p.Frequency));
        }

        if (page >= SchedulePages.StartTimesBase && page < SchedulePages.StartTimesBase + 16)
        {
            var index = page - SchedulePages.StartTimesBase;
            if (index >= Programs.Count)
                return string.Concat(Enumerable.Repeat($"{SchedulePages.UnsetStartTime:X4}", Model.MaxStartTimes));

            var starts = Programs[index].StartTimes
                .Concat(Enumerable.Repeat(SchedulePages.UnsetStartTime, Model.MaxStartTimes))
                .Take(Model.MaxStartTimes);

            return SchedulePages.EncodeStartTimes(starts);
        }

        if (page >= SchedulePages.RunTimesBase)
        {
            var (odd, even) = PairForPage(page);
            return SchedulePages.EncodeRunTimes(
                new SchedulePages.RunTimePage(RunTimesFor(odd), RunTimesFor(even)),
                MaxPrograms);
        }

        return new string('0', 8);
    }

    /// <summary>Run times for one station across every program, in program order.</summary>
    private int[] RunTimesFor(int station) =>
        Programs
            .Select(p => p.StationRunTimes.TryGetValue(station, out var minutes) ? minutes : 0)
            .Concat(Enumerable.Repeat(0, MaxPrograms))
            .Take(MaxPrograms)
            .ToArray();

    private static (int Odd, int Even) PairForPage(int page)
    {
        var odd = (page - SchedulePages.RunTimesBase) * 2 + 1;
        return (odd, odd + 1);
    }

    private int MaxPrograms => Math.Max(1, Model.MaxPrograms);

    private void ApplySchedulePage(int page, string payload)
    {
        try
        {
            if (page == SchedulePages.GlobalInfo)
            {
                var global = SchedulePages.DecodeGlobalInfo(payload);
                InterStationDelaySeconds = global.InterStationDelaySeconds;
                Snooze = global.Snooze;
                RainSensorBypassed = global.RainSensorBypassed;
            }
            else if (page >= SchedulePages.ProgramInfoBase && page < SchedulePages.ProgramInfoBase + 16)
            {
                var index = page - SchedulePages.ProgramInfoBase;
                if (index >= Programs.Count) return;

                var info = SchedulePages.DecodeProgramInfo(payload);
                var p = Programs[index];
                p.CustomDays = info.CustomDays;
                p.CyclicDays = info.CyclicDays;
                p.DaysRemaining = info.DaysRemaining;
                p.SeasonalAdjustPercent = info.SeasonalAdjustPercent;
                p.Frequency = info.Frequency;
            }
            else if (page >= SchedulePages.StartTimesBase && page < SchedulePages.StartTimesBase + 16)
            {
                var index = page - SchedulePages.StartTimesBase;
                if (index >= Programs.Count) return;
                Programs[index].StartTimes = SchedulePages.DecodeStartTimes(payload);
            }
            else if (page >= SchedulePages.RunTimesBase)
            {
                var (odd, even) = PairForPage(page);
                var decoded = SchedulePages.DecodeRunTimes(payload, MaxPrograms);

                ApplyStationRunTimes(odd, decoded.OddStationRunTimes);
                ApplyStationRunTimes(even, decoded.EvenStationRunTimes);
            }
        }
        catch (Exception ex) when (ex is FormatException or RainBirdProtocolException)
        {
            // A malformed page would be NAKed by real hardware, but the flow has
            // already ACKed by this point. Leave the stored state untouched.
        }
    }

    private void ApplyStationRunTimes(int station, IReadOnlyList<int> runTimesByProgram)
    {
        if (station < 1 || station > StationCount) return;

        for (var index = 0; index < Programs.Count && index < runTimesByProgram.Count; index++)
        {
            var runTimes = Programs[index].StationRunTimes.ToDictionary(kv => kv.Key, kv => kv.Value);
            runTimes[station] = runTimesByProgram[index];
            Programs[index].StationRunTimes = runTimes;
        }
    }
}

/// <summary>A program held by the simulator.</summary>
public sealed class SimulatedProgram
{
    public required int Number { get; init; }
    public required FrequencyType Frequency { get; set; }
    public required IReadOnlyList<bool> CustomDays { get; set; }
    public required int CyclicDays { get; set; }
    public required int DaysRemaining { get; set; }
    public required int SeasonalAdjustPercent { get; set; }
    public required IReadOnlyList<int> StartTimes { get; set; }
    public required IReadOnlyDictionary<int, int> StationRunTimes { get; set; }
}
