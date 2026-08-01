using System.Text;

namespace RainBird.Protocol.Universal;

/// <summary>
/// Controller Data Table entries.
///
/// Only the entries this project uses are named; the numbering is the controller's,
/// so any other entry can still be reached by casting.
/// </summary>
public enum CdtDataId
{
    IrrigationCycleTime = 10,
    IrrigationSoakTime = 11,
    InterStationDelay = 12,
    GlobalSensorBypass = 13,
    LocalSensorType = 14,

    ProgramCycleAdvancedDays = 15,
    ProgramCycleCustomDays = 16,
    ProgramCycleWaterCycleCount = 17,
    ProgramCycleWaterCycleDays = 18,
    ProgramCycleWaterCycleType = 19,

    RainDelay = 20,

    /// <summary>Per-program, per-station run time in seconds. Rank 2.</summary>
    RunTimes = 21,

    SeasonalAdjustByMonth = 22,
    SeasonalAdjustByMonthEnable = 23,

    /// <summary>Percentage per program. Rank 1.</summary>
    SeasonalAdjustByProgram = 24,

    SensorAssociation = 25,
    SensorType = 26,

    /// <summary>Per-program start times. Rank 3.</summary>
    StartTimes = 29,

    StationPriority = 30,
    StationSequence = 31,
    SolenoidsMax = 32,
    StationsMax = 33,

    WaterRestrictionStartTime = 34,
    WaterRestrictionEndTime = 35,

    FlowMonitorEnabled = 37,
    StationFlow = 61,
    StationsLearned = 62,
}

/// <summary>
/// A rectangular selection of table slots: a data ID plus an inclusive index range
/// per dimension.
///
/// The controller's own term is "rank" — the number of dimensions an entry has. Run
/// times are rank 2 (program × station); start times are rank 3.
/// </summary>
public sealed record CdtRange
{
    private readonly (int Start, int End)[] _bounds;

    private CdtRange(CdtDataId dataId, params (int Start, int End)[] bounds)
    {
        foreach (var (start, end) in bounds)
        {
            if (start < 0 || end < start)
                throw new ArgumentOutOfRangeException(nameof(bounds), $"Invalid index range {start}..{end}.");
        }

        DataId = dataId;
        _bounds = bounds;
    }

    public CdtDataId DataId { get; }

    public int Rank => _bounds.Length;

    /// <summary>How many values this selection covers.</summary>
    public int Count => _bounds.Aggregate(1, (total, b) => total * (b.End - b.Start + 1));

    public IReadOnlyList<(int Start, int End)> Bounds => _bounds;

    /// <summary>An entry with no indices — a single global value.</summary>
    public static CdtRange Scalar(CdtDataId dataId) => new(dataId);

    /// <summary>A one-dimensional slice, for example seasonal adjust across programs.</summary>
    public static CdtRange Of(CdtDataId dataId, int start, int end) => new(dataId, (start, end));

    /// <summary>A two-dimensional slice, for example run times across programs and stations.</summary>
    public static CdtRange Of(CdtDataId dataId, int start1, int end1, int start2, int end2) =>
        new(dataId, (start1, end1), (start2, end2));

    /// <summary>A three-dimensional slice, as start times use.</summary>
    public static CdtRange Of(
        CdtDataId dataId, int start1, int end1, int start2, int end2, int start3, int end3) =>
        new(dataId, (start1, end1), (start2, end2), (start3, end3));

    /// <summary>Every station's run time for one program.</summary>
    public static CdtRange RunTimesForProgram(int programIndex, int maxStationIndex) =>
        Of(CdtDataId.RunTimes, programIndex, programIndex, 0, maxStationIndex);

    /// <summary>Every start time for a span of programs.</summary>
    public static CdtRange StartTimes(int lastProgramIndex, int lastStartIndex) =>
        Of(CdtDataId.StartTimes, 0, lastProgramIndex, 0, 0, 0, lastStartIndex);

    internal string Encode()
    {
        var builder = new StringBuilder();
        builder.Append(UniversalProtocol.LittleEndian((int)DataId, 2));
        builder.Append($"{Rank:X2}");

        foreach (var (start, end) in _bounds)
        {
            builder.Append(UniversalProtocol.LittleEndian(start, 2));
            builder.Append(UniversalProtocol.LittleEndian(end, 2));
        }

        return builder.ToString();
    }

    public override string ToString() =>
        $"{DataId}[{string.Join(", ", _bounds.Select(b => $"{b.Start}..{b.End}"))}]";
}

/// <summary>Values read back for one table entry.</summary>
/// <param name="DataId">Raw data ID, so unnamed entries still round-trip.</param>
/// <param name="Bounds">The index ranges the controller echoed back.</param>
/// <param name="ValueWidth">Bytes per value, as the controller reported it.</param>
/// <param name="Values">Values in row-major order over <paramref name="Bounds"/>.</param>
public sealed record CdtValues(
    int DataId,
    IReadOnlyList<(int Start, int End)> Bounds,
    int ValueWidth,
    IReadOnlyList<int> Values)
{
    public CdtDataId Id => (CdtDataId)DataId;

    public override string ToString() =>
        $"{Id} width={ValueWidth} [{string.Join(", ", Values)}]";
}
