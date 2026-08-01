namespace RainBird.Protocol.Universal;

/// <summary>
/// Reads and writes the Controller Data Table on firmware that supports the
/// universal message transport.
///
/// This is the only way to configure a controller whose firmware has dropped the
/// legacy schedule pages, and in particular the only way to zero its run times so
/// that it stops watering on its own.
/// </summary>
public sealed class UniversalClient
{
    private readonly LnkClient _client;

    public UniversalClient(LnkClient client) => _client = client;

    /// <summary>Reads one or more table entries in a single round trip.</summary>
    public async Task<IReadOnlyList<CdtValues>> GetAsync(
        IReadOnlyList<CdtRange> ranges, CancellationToken ct = default)
    {
        var response = await _client.TunnelAsync(UniversalProtocol.EncodeGet(ranges), ct).ConfigureAwait(false);
        return UniversalProtocol.DecodeGet(response.Hex);
    }

    /// <summary>Reads a single table entry.</summary>
    public async Task<CdtValues> GetAsync(CdtRange range, CancellationToken ct = default)
    {
        var blocks = await GetAsync([range], ct).ConfigureAwait(false);
        return blocks.FirstOrDefault()
            ?? throw new RainBirdProtocolException($"Controller returned no data for {range}.");
    }

    /// <summary>
    /// Writes a table entry, and throws unless the controller acknowledges it.
    /// </summary>
    public async Task SetAsync(
        CdtRange range, int valueWidth, IReadOnlyList<int> values, CancellationToken ct = default)
    {
        var request = UniversalProtocol.EncodeSet(range, valueWidth, values);
        var response = await _client.TunnelAsync(request, ct).ConfigureAwait(false);
        UniversalProtocol.EnsureSetAccepted(response.Hex);
    }

    // ------------------------------------------------------------- run times

    /// <summary>
    /// Run times for one program, in seconds, indexed by 1-based station number.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, int>> GetRunTimesAsync(
        int programIndex, int stationCount, CancellationToken ct = default)
    {
        var range = CdtRange.RunTimesForProgram(programIndex, stationCount - 1);
        var block = await GetAsync(range, ct).ConfigureAwait(false);

        var runTimes = new Dictionary<int, int>(block.Values.Count);
        for (var i = 0; i < block.Values.Count; i++)
            runTimes[i + 1] = block.Values[i];

        return runTimes;
    }

    /// <summary>
    /// Writes run times for one program, in seconds, indexed by 1-based station.
    /// Stations not present are written as zero.
    /// </summary>
    public Task SetRunTimesAsync(
        int programIndex,
        int stationCount,
        IReadOnlyDictionary<int, int> runTimesByStation,
        CancellationToken ct = default)
    {
        var values = new int[stationCount];
        for (var station = 1; station <= stationCount; station++)
            values[station - 1] = runTimesByStation.TryGetValue(station, out var seconds) ? Math.Max(0, seconds) : 0;

        return SetAsync(CdtRange.RunTimesForProgram(programIndex, stationCount - 1), RunTimeWidth, values, ct);
    }

    /// <summary>Run times are stored as 32-bit second counts.</summary>
    private const int RunTimeWidth = 4;

    /// <summary>
    /// Clears every program's run times so the controller will not water on its own.
    ///
    /// This is what makes software-driven scheduling safe: with no run times the
    /// controller's own programs do nothing, while manual station commands — which is
    /// how the scheduler opens valves — keep working.
    /// </summary>
    public async Task<int> ClearAllRunTimesAsync(
        int programCount, int stationCount, CancellationToken ct = default)
    {
        var cleared = 0;
        var empty = new Dictionary<int, int>();

        for (var program = 0; program < programCount; program++)
        {
            var existing = await GetRunTimesAsync(program, stationCount, ct).ConfigureAwait(false);
            if (existing.Values.All(seconds => seconds == 0)) continue;

            await SetRunTimesAsync(program, stationCount, empty, ct).ConfigureAwait(false);
            cleared++;
        }

        return cleared;
    }

    /// <summary>True when no program has a run time, so nothing waters automatically.</summary>
    public async Task<bool> IsFullyDisarmedAsync(
        int programCount, int stationCount, CancellationToken ct = default)
    {
        for (var program = 0; program < programCount; program++)
        {
            var runTimes = await GetRunTimesAsync(program, stationCount, ct).ConfigureAwait(false);
            if (runTimes.Values.Any(seconds => seconds > 0)) return false;
        }

        return true;
    }

    // ---------------------------------------------------------- other entries

    /// <summary>Seasonal adjust percentage per program.</summary>
    public async Task<IReadOnlyList<int>> GetSeasonalAdjustAsync(
        int programCount, CancellationToken ct = default)
    {
        var block = await GetAsync(
            CdtRange.Of(CdtDataId.SeasonalAdjustByProgram, 0, programCount - 1), ct).ConfigureAwait(false);
        return block.Values;
    }

    public Task SetSeasonalAdjustAsync(int programIndex, int percent, CancellationToken ct = default) =>
        SetAsync(
            CdtRange.Of(CdtDataId.SeasonalAdjustByProgram, programIndex, programIndex),
            2,
            [Math.Clamp(percent, 0, 300)],
            ct);

    /// <summary>Start times per program, in minutes from midnight.</summary>
    public async Task<IReadOnlyList<int>> GetStartTimesAsync(
        int programCount, int startTimeCount, CancellationToken ct = default)
    {
        var block = await GetAsync(
            CdtRange.StartTimes(programCount - 1, startTimeCount - 1), ct).ConfigureAwait(false);
        return block.Values;
    }

    /// <summary>Cycle and soak times per station, in seconds.</summary>
    public async Task<(IReadOnlyList<int> Cycle, IReadOnlyList<int> Soak)> GetCycleSoakAsync(
        int stationCount, CancellationToken ct = default)
    {
        var blocks = await GetAsync(
        [
            CdtRange.Of(CdtDataId.IrrigationCycleTime, 0, stationCount - 1),
            CdtRange.Of(CdtDataId.IrrigationSoakTime, 0, stationCount - 1),
        ], ct).ConfigureAwait(false);

        var cycle = blocks.FirstOrDefault(b => b.Id == CdtDataId.IrrigationCycleTime)?.Values ?? [];
        var soak = blocks.FirstOrDefault(b => b.Id == CdtDataId.IrrigationSoakTime)?.Values ?? [];
        return (cycle, soak);
    }

    /// <summary>Reads a single global value, such as the inter-station delay.</summary>
    public async Task<int> GetScalarAsync(CdtDataId dataId, CancellationToken ct = default)
    {
        var block = await GetAsync(CdtRange.Of(dataId, 0, 0), ct).ConfigureAwait(false);
        return block.Values.Count > 0 ? block.Values[0] : 0;
    }
}
