namespace RainBird.Protocol;

/// <summary>Result of SIP <c>4C</c> — the whole controller state in one round trip.</summary>
public sealed record CombinedState
{
    public required TimeOnly ControllerTime { get; init; }
    public required DateOnly ControllerDate { get; init; }

    /// <summary>Remaining rain-delay days. Zero means no delay.</summary>
    public required int RainDelayDays { get; init; }

    public required RainSensorState SensorState { get; init; }

    /// <summary>
    /// Whether automatic watering is enabled, from SIP <c>48</c>.
    ///
    /// This is <i>not</i> "water is flowing right now": a physical ESP-ME3 sitting
    /// idle answers 1. Only the active-station mask says what is actually running.
    /// </summary>
    public required bool ControllerEnabled { get; init; }

    /// <summary>Global seasonal adjust, as a percentage. 100 is unadjusted.</summary>
    public required int SeasonalAdjustPercent { get; init; }

    /// <summary>Seconds left on the running station, or zero.</summary>
    public required int RemainingRuntimeSeconds { get; init; }

    /// <summary>1-based station number currently watering, or zero when idle.</summary>
    public required int ActiveStation { get; init; }

    /// <summary>True when a station is actually open.</summary>
    public bool IsWatering => ActiveStation > 0;
}

public enum RainSensorState
{
    /// <summary>No rain detected — irrigation allowed.</summary>
    Dry = 0,

    /// <summary>Sensor tripped — the controller is suppressing irrigation.</summary>
    Wet = 1,
}

/// <summary>Result of SIP <c>02</c>.</summary>
public sealed record ModelAndVersion(string ModelId, int ProtocolMajor, int ProtocolMinor)
{
    public ControllerModel Model => ControllerModels.Lookup(ModelId);
}

/// <summary>
/// Result of SIP <c>0B</c>. The response length varies by firmware generation, so
/// only major and minor are decoded; anything after them is kept verbatim.
/// </summary>
public sealed record FirmwareVersion(int Major, int Minor, int Patch, string? Build = null)
{
    public override string ToString() =>
        Build is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor} ({Build})";
}

/// <summary>
/// Which stations physically exist, from SIP <c>03</c>. The controller answers with a
/// 32-bit mask per page.
/// </summary>
public sealed record AvailableStations(int PageNumber, uint Mask)
{
    /// <summary>1-based station numbers present on this page.</summary>
    public IEnumerable<int> Stations
    {
        get
        {
            for (var bit = 0; bit < 32; bit++)
                if ((Mask & (1u << bit)) != 0)
                    yield return PageNumber * 32 + bit + 1;
        }
    }

    public int Count => System.Numerics.BitOperations.PopCount(Mask);
}

/// <summary>Which stations are watering right now, from SIP <c>3F</c>.</summary>
public sealed record ActiveStations(int PageNumber, uint Mask)
{
    public IEnumerable<int> Stations
    {
        get
        {
            for (var bit = 0; bit < 32; bit++)
                if ((Mask & (1u << bit)) != 0)
                    yield return PageNumber * 32 + bit + 1;
        }
    }
}

/// <summary>Seasonal adjust for one program, from SIP <c>30</c>.</summary>
public sealed record WaterBudget(int ProgramCode, int SeasonalAdjustPercent);

/// <summary>Per-zone seasonal adjust factors for a program, from SIP <c>32</c>.</summary>
public sealed record ZoneSeasonalAdjust(int ProgramCode, IReadOnlyList<int> FactorsByStation);

/// <summary>
/// What a specific controller can actually do. Built at connect time from the model
/// table plus live probing with SIP <c>04</c>, so the UI can hide controls that would
/// only ever produce a NAK.
/// </summary>
public sealed record ControllerCapabilities
{
    public required ControllerModel Model { get; init; }
    public required string SerialNumber { get; init; }
    public required FirmwareVersion Firmware { get; init; }
    public required IReadOnlyList<int> Stations { get; init; }

    /// <summary>
    /// The controller exposes its schedule through the SIP page protocol
    /// (<c>20</c>/<c>21</c>). Newer firmware does not � a physical ESP-ME3 on protocol
    /// 2.12 rejects both � and uses the universal message transport instead.
    /// </summary>
    public required bool SupportsSchedulePages { get; init; }

    /// <summary>The one-shot state read (SIP <c>4C</c>) is available.</summary>
    public required bool SupportsCombinedState { get; init; }

    /// <summary>Automatic watering can be switched off over the wire (SIP <c>49</c>).</summary>
    public required bool SupportsControllerToggle { get; init; }

    /// <summary>The universal message transport (SIP <c>0C</c>) is available.</summary>
    public required bool SupportsUniversalTransport { get; init; }

    public required bool SupportsFlowMonitoring { get; init; }
    public required bool SupportsIrrigationStatistics { get; init; }
    public required bool SupportsZoneSeasonalAdjust { get; init; }
    public required bool SupportsStationErrors { get; init; }

    public int StationCount => Stations.Count;

    /// <summary>
    /// True when this app must own scheduling because the controller will not let us
    /// read or write its own.
    /// </summary>
    public bool RequiresSoftwareScheduling => !SupportsSchedulePages;
}

/// <summary>
/// How a station run was initiated. Ours, not the controller's — the protocol has no
/// field for it, so this is only known when the app issued the command itself.
/// </summary>
public enum RunTrigger
{
    Manual,
    Program,
    Test,

    /// <summary>
    /// The run was observed by polling but nothing in this app started it — so it was
    /// the controller's own schedule, or someone at the faceplate. Listed last so the
    /// stored values of the others do not shift.
    /// </summary>
    Unknown,
}

/// <summary>Watering frequency, from the frequency byte of a program info page.</summary>
public enum FrequencyType
{
    /// <summary>Specific days of the week, per <see cref="ProgramSchedule.CustomDays"/>.</summary>
    CustomDays = 0,

    /// <summary>Every N days.</summary>
    Cyclic = 1,

    OddDays = 2,
    EvenDays = 3,
}

/// <summary>A decoded watering program.</summary>
public sealed record ProgramSchedule
{
    public required int ProgramNumber { get; init; }
    public required FrequencyType Frequency { get; init; }

    /// <summary>Seven flags, Sunday first. Meaningful when <see cref="Frequency"/> is CustomDays.</summary>
    public required IReadOnlyList<bool> CustomDays { get; init; }

    /// <summary>Interval for <see cref="FrequencyType.Cyclic"/>.</summary>
    public required int CyclicDays { get; init; }

    public required int DaysRemaining { get; init; }
    public required int SeasonalAdjustPercent { get; init; }

    /// <summary>Start times as minutes past midnight. 0xFFFF (65535) means unset.</summary>
    public required IReadOnlyList<int> StartTimes { get; init; }

    /// <summary>Run time in minutes, keyed by 1-based station number.</summary>
    public required IReadOnlyDictionary<int, int> StationRunTimes { get; init; }

    public bool Enabled => StartTimes.Any(t => t is >= 0 and < 1440)
                           && StationRunTimes.Values.Any(m => m > 0);
}

/// <summary>One entry in the controller's run queue, from SIP <c>3B</c>.</summary>
public sealed record QueueEntry(int Station, int Minutes, RunTrigger Trigger);
