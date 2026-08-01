namespace RainBird.Protocol;

/// <summary>
/// Reads and writes whole watering programs on top of the raw page protocol.
///
/// A program is spread across several pages — its settings, its start times, and a
/// slice of every run-time page — so this type exists to make "load program B" and
/// "save program B" single operations rather than a dozen coordinated page calls.
///
/// Run-time pages hold values for *all* programs, so writes are read-modify-write:
/// changing program B's run time must not clobber program A's.
/// </summary>
public sealed class ScheduleClient
{
    private readonly LnkClient _client;
    private readonly ControllerCapabilities _capabilities;

    public ScheduleClient(LnkClient client, ControllerCapabilities capabilities)
    {
        _client = client;
        _capabilities = capabilities;
    }

    private int MaxPrograms => Math.Max(1, _capabilities.Model.MaxPrograms);
    private int MaxStartTimes => Math.Max(1, _capabilities.Model.MaxStartTimes);
    private IReadOnlyList<int> Stations => _capabilities.Stations;

    /// <summary>Reads one program in full.</summary>
    public async Task<ProgramSchedule> GetProgramAsync(int programIndex, CancellationToken ct = default)
    {
        EnsureValidProgram(programIndex);

        var infoHex = await _client.GetSchedulePageAsync(SchedulePages.ProgramInfo(programIndex), ct)
            .ConfigureAwait(false);
        var info = SchedulePages.DecodeProgramInfo(SchedulePages.PayloadOf(infoHex));

        var startsHex = await _client.GetSchedulePageAsync(SchedulePages.StartTimes(programIndex), ct)
            .ConfigureAwait(false);
        var starts = SchedulePages.DecodeStartTimes(SchedulePages.PayloadOf(startsHex))
            .Take(MaxStartTimes)
            .ToList();

        var runTimes = new Dictionary<int, int>();
        foreach (var (page, stations) in RunTimePages())
        {
            var pageHex = await _client.GetSchedulePageAsync(page, ct).ConfigureAwait(false);
            var decoded = SchedulePages.DecodeRunTimes(SchedulePages.PayloadOf(pageHex), MaxPrograms);

            foreach (var station in stations)
            {
                var values = SchedulePages.IsFirstOfPair(station)
                    ? decoded.OddStationRunTimes
                    : decoded.EvenStationRunTimes;

                runTimes[station] = programIndex < values.Count ? values[programIndex] : 0;
            }
        }

        return new ProgramSchedule
        {
            ProgramNumber = programIndex,
            Frequency = info.Frequency,
            CustomDays = info.CustomDays,
            CyclicDays = info.CyclicDays,
            DaysRemaining = info.DaysRemaining,
            SeasonalAdjustPercent = info.SeasonalAdjustPercent,
            StartTimes = starts,
            StationRunTimes = runTimes,
        };
    }

    /// <summary>Reads every program the model supports.</summary>
    public async Task<IReadOnlyList<ProgramSchedule>> GetAllProgramsAsync(CancellationToken ct = default)
    {
        var programs = new List<ProgramSchedule>(MaxPrograms);
        for (var index = 0; index < MaxPrograms; index++)
            programs.Add(await GetProgramAsync(index, ct).ConfigureAwait(false));
        return programs;
    }

    /// <summary>
    /// Writes one program. Run-time pages are read back first so the other programs'
    /// values on the same page survive.
    /// </summary>
    public async Task SaveProgramAsync(ProgramSchedule program, CancellationToken ct = default)
    {
        EnsureValidProgram(program.ProgramNumber);

        var info = new SchedulePages.ProgramInfoPage(
            program.CustomDays,
            program.CyclicDays,
            program.DaysRemaining,
            PermanentDaysOff: 0,
            program.SeasonalAdjustPercent,
            program.Frequency);

        await _client.SetSchedulePageAsync(
            SchedulePages.ProgramInfo(program.ProgramNumber),
            SchedulePages.EncodeProgramInfo(info),
            ct).ConfigureAwait(false);

        var starts = program.StartTimes
            .Take(MaxStartTimes)
            .Concat(Enumerable.Repeat(SchedulePages.UnsetStartTime, MaxStartTimes))
            .Take(MaxStartTimes);

        await _client.SetSchedulePageAsync(
            SchedulePages.StartTimes(program.ProgramNumber),
            SchedulePages.EncodeStartTimes(starts),
            ct).ConfigureAwait(false);

        foreach (var (page, stations) in RunTimePages())
        {
            var existingHex = await _client.GetSchedulePageAsync(page, ct).ConfigureAwait(false);
            var existing = SchedulePages.DecodeRunTimes(SchedulePages.PayloadOf(existingHex), MaxPrograms);

            var odd = existing.OddStationRunTimes.ToArray();
            var even = existing.EvenStationRunTimes.ToArray();

            foreach (var station in stations)
            {
                if (!program.StationRunTimes.TryGetValue(station, out var minutes)) continue;

                var target = SchedulePages.IsFirstOfPair(station) ? odd : even;
                if (program.ProgramNumber < target.Length)
                    target[program.ProgramNumber] = minutes;
            }

            await _client.SetSchedulePageAsync(
                page,
                SchedulePages.EncodeRunTimes(new SchedulePages.RunTimePage(odd, even), MaxPrograms),
                ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Disables a station by zeroing its run time in every program. There is no
    /// separate disable flag on these controllers, so this is the only mechanism.
    /// </summary>
    public async Task SetStationEnabledAsync(int station, bool enabled, int restoreMinutes = 10,
        CancellationToken ct = default)
    {
        var page = SchedulePages.RunTimes(station);

        var existingHex = await _client.GetSchedulePageAsync(page, ct).ConfigureAwait(false);
        var existing = SchedulePages.DecodeRunTimes(SchedulePages.PayloadOf(existingHex), MaxPrograms);

        var odd = existing.OddStationRunTimes.ToArray();
        var even = existing.EvenStationRunTimes.ToArray();
        var target = SchedulePages.IsFirstOfPair(station) ? odd : even;

        for (var program = 0; program < target.Length; program++)
            target[program] = enabled ? Math.Max(target[program], restoreMinutes) : 0;

        await _client.SetSchedulePageAsync(
            page,
            SchedulePages.EncodeRunTimes(new SchedulePages.RunTimePage(odd, even), MaxPrograms),
            ct).ConfigureAwait(false);
    }

    /// <summary>Reads the controller-wide schedule settings.</summary>
    public async Task<SchedulePages.GlobalInfoPage> GetGlobalInfoAsync(CancellationToken ct = default)
    {
        var hex = await _client.GetSchedulePageAsync(SchedulePages.GlobalInfo, ct).ConfigureAwait(false);
        return SchedulePages.DecodeGlobalInfo(SchedulePages.PayloadOf(hex));
    }

    public Task SaveGlobalInfoAsync(SchedulePages.GlobalInfoPage page, CancellationToken ct = default) =>
        _client.SetSchedulePageAsync(SchedulePages.GlobalInfo, SchedulePages.EncodeGlobalInfo(page), ct);

    /// <summary>The run-time pages this controller's stations occupy, and which stations each covers.</summary>
    private IEnumerable<(int Page, List<int> Stations)> RunTimePages() =>
        Stations
            .GroupBy(SchedulePages.RunTimes)
            .OrderBy(group => group.Key)
            .Select(group => (group.Key, group.ToList()));

    private void EnsureValidProgram(int programIndex)
    {
        if (programIndex < 0 || programIndex >= MaxPrograms)
            throw new ArgumentOutOfRangeException(
                nameof(programIndex),
                $"{_capabilities.Model.Series} supports programs 0-{MaxPrograms - 1}.");
    }
}
