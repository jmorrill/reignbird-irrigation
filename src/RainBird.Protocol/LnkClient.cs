using System.Text.Json.Nodes;

namespace RainBird.Protocol;

/// <summary>
/// Typed client for a Rain Bird controller's local LNK WiFi interface.
///
/// Every call goes out as a SIP command wrapped in a <c>tunnelSip</c> JSON-RPC
/// request. See <c>docs/rainbird-protocol.md</c> for the wire details.
/// </summary>
public sealed class LnkClient
{
    private readonly IRainBirdTransport _transport;

    public LnkClient(IRainBirdTransport transport) => _transport = transport;

    // ---------------------------------------------------------------- tunnel

    /// <summary>
    /// Sends a raw SIP payload and decodes the reply.
    /// </summary>
    /// <param name="repeatable">
    /// False for commands that must not be sent twice — anything that opens a valve.
    /// A lost reply is ambiguous, and the retry ladder would turn that ambiguity into
    /// a zone queued three times. Reads default to true because asking again is free.
    /// </param>
    public async Task<SipMessage> TunnelAsync(
        string commandHex, CancellationToken ct = default, bool repeatable = true)
    {
        var parameters = new JsonObject
        {
            ["length"] = commandHex.Length / 2,
            ["data"] = commandHex,
        };

        var result = repeatable
            ? await _transport.SendAsync("tunnelSip", parameters, ct).ConfigureAwait(false)
            : await _transport.SendWithoutRetryAsync("tunnelSip", parameters, ct).ConfigureAwait(false);

        var data = result["data"]?.GetValue<string>()
            ?? throw new RainBirdProtocolException("tunnelSip response contained no data field.");

        return SipCodec.Decode(data);
    }

    private Task<SipMessage> SendAsync(SipCommand command, CancellationToken ct, params byte[] args) =>
        TunnelAsync(SipCodec.Encode(command, args), ct);

    /// <summary>Calls an RPC method on the LNK module itself, outside the SIP tunnel.</summary>
    public Task<JsonObject> CallAsync(string method, JsonObject? parameters = null, CancellationToken ct = default) =>
        _transport.SendAsync(method, parameters ?? new JsonObject(), ct);

    // ------------------------------------------------------------- identity

    public async Task<ModelAndVersion> GetModelAndVersionAsync(CancellationToken ct = default)
    {
        var r = await SendAsync(SipCommand.ModelAndVersion, ct).ConfigureAwait(false);
        return new ModelAndVersion(
            $"{r["modelID"]:X4}",
            r.Int("protocolRevisionMajor"),
            r.Int("protocolRevisionMinor"));
    }

    public async Task<string> GetSerialNumberAsync(CancellationToken ct = default)
    {
        var r = await SendAsync(SipCommand.SerialNumber, ct).ConfigureAwait(false);
        // 8 bytes, carried as raw hex — not a number, so read the slice directly.
        return r.Hex.Substring(2, 16);
    }

    public async Task<FirmwareVersion> GetFirmwareVersionAsync(CancellationToken ct = default)
    {
        var r = await SendAsync(SipCommand.ControllerFirmwareVersion, ct).ConfigureAwait(false);

        // The response length differs between firmware generations. The 5-byte form
        // documented in the app's table ends with a 16-bit patch; protocol 2.12
        // hardware answers with 9 bytes whose tail is undocumented, so that is carried
        // through verbatim instead of being guessed at.
        if (r.Hex.Length == 10)
        {
            var patch = Convert.ToInt32(r.Hex.Substring(6, 4), 16);
            return new FirmwareVersion(r.Int("major"), r.Int("minor"), patch);
        }

        return new FirmwareVersion(
            r.Int("major"), r.Int("minor"), 0, r.Hex.Length > 6 ? r.Hex[6..] : null);
    }

    /// <summary>Asks the controller whether it implements a given command (SIP <c>04</c>).</summary>
    public async Task<bool> IsCommandSupportedAsync(SipCommand command, CancellationToken ct = default)
    {
        try
        {
            var r = await SendAsync(SipCommand.CommandSupport, ct, (byte)command).ConfigureAwait(false);
            return r.Int("support") != 0;
        }
        catch (RainBirdNakException)
        {
            // Older firmware NAKs the probe itself; treat that as "not supported".
            return false;
        }
    }

    // ---------------------------------------------------------------- state

    /// <summary>
    /// Null until we have found out whether this controller implements SIP <c>4C</c>.
    /// </summary>
    private bool? _supportsCombinedState;

    /// <summary>
    /// The controller's current state.
    ///
    /// Prefers SIP <c>4C</c>, which returns everything in one round trip. Not every
    /// firmware has it — a physical ESP-ME3 on protocol 2.12 rejects it outright — so
    /// this falls back to reading the same values individually and remembers which
    /// path works.
    /// </summary>
    public async Task<CombinedState> GetCombinedStateAsync(CancellationToken ct = default)
    {
        if (_supportsCombinedState != false)
        {
            try
            {
                var state = await ReadCombinedAsync(ct).ConfigureAwait(false);
                _supportsCombinedState = true;
                return state;
            }
            catch (RainBirdNakException ex) when (ex.Reason == NakReason.CommandNotSupported)
            {
                _supportsCombinedState = false;
            }
            catch (RainBirdProtocolException) when (_supportsCombinedState is null)
            {
                // Some firmware answers 4C with something we cannot parse. Treat that
                // the same as not supporting it rather than failing every poll.
                _supportsCombinedState = false;
            }
        }

        return await ReadPiecewiseAsync(ct).ConfigureAwait(false);
    }

    private async Task<CombinedState> ReadCombinedAsync(CancellationToken ct)
    {
        var r = await SendAsync(SipCommand.CombinedControllerState, ct).ConfigureAwait(false);

        var activeStation = r.Int("activeStation");
        return new CombinedState
        {
            ControllerTime = new TimeOnly(r.Int("hour"), r.Int("minute"), r.Int("second")),
            ControllerDate = SafeDate(r.Int("year"), r.Int("month"), r.Int("day")),
            RainDelayDays = r.Int("delaySetting"),
            SensorState = (RainSensorState)r.Int("sensorState"),
            ControllerEnabled = r.Int("irrigationState") != 0,
            SeasonalAdjustPercent = r.Int("seasonalAdjust"),
            RemainingRuntimeSeconds = r.Int("remainingRuntime"),
            ActiveStation = activeStation,
        };
    }

    /// <summary>Slow-moving values, refreshed on a longer cycle than the poll.</summary>
    private sealed record SlowState(
        DateOnly Date, int RainDelayDays, RainSensorState Sensor, bool Enabled, int SeasonalAdjust);

    private SlowState? _slow;
    private DateTimeOffset _slowReadAt = DateTimeOffset.MinValue;

    /// <summary>How long the slow-moving values are reused for.</summary>
    private static readonly TimeSpan SlowStateLifetime = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The same picture assembled from individual commands, for firmware without
    /// <c>4C</c>.
    ///
    /// Doing all seven reads every poll is wasteful: only the clock and the active
    /// station change from one poll to the next, and over a high-latency link seven
    /// serialised round trips make the whole app feel slow. The rain delay, sensor,
    /// seasonal adjust and date are refreshed on a slower cycle instead, which takes
    /// the common case down to two round trips.
    /// </summary>
    private async Task<CombinedState> ReadPiecewiseAsync(CancellationToken ct)
    {
        var time = await GetControllerTimeAsync(ct).ConfigureAwait(false);
        var active = await GetActiveStationsAsync(0, ct).ConfigureAwait(false);

        if (_slow is null || DateTimeOffset.UtcNow - _slowReadAt > SlowStateLifetime)
        {
            _slow = new SlowState(
                await GetControllerDateAsync(ct).ConfigureAwait(false),
                await SafeAsync(() => GetRainDelayAsync(ct), 0).ConfigureAwait(false),
                await SafeAsync(() => GetRainSensorStateAsync(ct), RainSensorState.Dry).ConfigureAwait(false),
                await SafeAsync(async () => await GetIrrigationStateAsync(ct) != 0, true).ConfigureAwait(false),
                (await SafeAsync(() => GetWaterBudgetAsync(0, ct), new WaterBudget(0, 100))
                    .ConfigureAwait(false)).SeasonalAdjustPercent);

            _slowReadAt = DateTimeOffset.UtcNow;
        }

        return new CombinedState
        {
            ControllerTime = time,
            ControllerDate = _slow.Date,
            RainDelayDays = _slow.RainDelayDays,
            SensorState = _slow.Sensor,
            ControllerEnabled = _slow.Enabled,
            SeasonalAdjustPercent = _slow.SeasonalAdjust,
            // Nothing in this path reports a countdown; the caller's own record of
            // what it started is the only source for that.
            RemainingRuntimeSeconds = 0,
            ActiveStation = active.Stations.FirstOrDefault(),
        };
    }

    /// <summary>
    /// Drops the cached slow-moving values, so the next read reflects a change this
    /// client just made rather than waiting out the refresh cycle.
    /// </summary>
    public void InvalidateCachedState() => _slowReadAt = DateTimeOffset.MinValue;

    /// <summary>
    /// A freshly reset controller can report an impossible date (month 0, day 0).
    /// Clamp rather than throw — the app is still perfectly usable, and the settings
    /// screen offers to sync the clock.
    /// </summary>
    private static DateOnly SafeDate(int year, int month, int day)
    {
        year = Math.Clamp(year, 1, 9999);
        month = Math.Clamp(month, 1, 12);
        day = Math.Clamp(day, 1, DateTime.DaysInMonth(year, month));
        return new DateOnly(year, month, day);
    }

    /// <summary>
    /// SIP <c>48</c>. Despite the name this reports whether automatic watering is
    /// <i>enabled</i>, not whether a zone is open � an idle ESP-ME3 answers 1.
    /// </summary>
    public async Task<int> GetIrrigationStateAsync(CancellationToken ct = default)
    {
        var r = await SendAsync(SipCommand.CurrentIrrigationState, ct).ConfigureAwait(false);
        return r.Int("irrigationState");
    }

    public async Task<RainSensorState> GetRainSensorStateAsync(CancellationToken ct = default)
    {
        var r = await SendAsync(SipCommand.CurrentRainSensorState, ct).ConfigureAwait(false);
        return (RainSensorState)r.Int("sensorState");
    }

    public async Task<AvailableStations> GetAvailableStationsAsync(int page = 0, CancellationToken ct = default)
    {
        var r = await SendAsync(SipCommand.AvailableStations, ct, (byte)page).ConfigureAwait(false);
        return new AvailableStations(r.Int("pageNumber"), SipCodec.ReadStationMask(r.Hex, 4));
    }

    public async Task<ActiveStations> GetActiveStationsAsync(int page = 0, CancellationToken ct = default)
    {
        var r = await SendAsync(SipCommand.CurrentStationsActive, ct, (byte)page).ConfigureAwait(false);
        return new ActiveStations(r.Int("pageNumber"), SipCodec.ReadStationMask(r.Hex, 4));
    }

    /// <summary>Stations the controller has flagged as faulted (SIP <c>3D</c>).</summary>
    public async Task<IReadOnlyList<int>> GetStationErrorsAsync(int page = 0, CancellationToken ct = default)
    {
        var r = await SendAsync(SipCommand.CurrentStationError, ct, (byte)page).ConfigureAwait(false);
        return new ActiveStations(r.Int("pageNumber"), SipCodec.ReadStationMask(r.Hex, 4)).Stations.ToList();
    }

    // ----------------------------------------------------------- manual runs

    /// <summary>
    /// The longest run any single command can express. The duration is one byte on
    /// the wire, so this is the format's limit rather than a policy choice.
    /// </summary>
    public const int MaxRunMinutes = 255;

    /// <summary>
    /// Runs one station for a number of minutes (SIP <c>39</c>).
    ///
    /// The station is a <b>16-bit</b> value and the duration 8-bit, giving a 4-byte
    /// command — which is what the app's own table says ("length: 4") and what
    /// <c>SIPCommandTwoParam</c> encodes as <c>%04X</c> then <c>%02X</c>. Sending the
    /// station as a single byte makes real hardware reject the command outright with
    /// a bad-length NAK, even though a 3-byte form looks perfectly plausible.
    /// </summary>
    public Task RunStationAsync(int station, int minutes, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(station, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(minutes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minutes, MaxRunMinutes);

        return TunnelAsync(
            SipCodec.EncodeRaw(SipCommand.ManuallyRunStation, $"{station:X4}{minutes:X2}"),
            ct, repeatable: false);
    }

    /// <summary>Queues a station run behind whatever is already going (SIP <c>4B</c>).</summary>
    public Task StackStationAsync(int station, int minutes, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(station, 1);

        // Checked rather than masked. The duration is one byte on the wire, and
        // `minutes & 0xFF` turned -1 into 255 — a request for negative watering
        // opening a valve for four and a quarter hours.
        ArgumentOutOfRangeException.ThrowIfLessThan(minutes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minutes, MaxRunMinutes);

        var hex = $"{(station >> 8) & 0xFF:X2}{station & 0xFF:X2}{minutes:X2}";
        // The one that stacks rather than replaces, so a repeat is strictly additive:
        // three attempts would water this zone three times over.
        return TunnelAsync(SipCodec.EncodeRaw(SipCommand.StackManuallyRunStation, hex), ct, repeatable: false);
    }

    /// <summary>Runs a whole program now (SIP <c>38</c>).</summary>
    public Task RunProgramAsync(int program, CancellationToken ct = default) =>
        TunnelAsync(SipCodec.Encode(SipCommand.ManuallyRunProgram, (byte)program), ct, repeatable: false);

    /// <summary>Runs every station in sequence for N minutes each (SIP <c>3A</c>).</summary>
    public Task TestAllStationsAsync(int minutes, CancellationToken ct = default) =>
        TunnelAsync(SipCodec.Encode(SipCommand.TestStations, (byte)minutes), ct, repeatable: false);

    public Task StopIrrigationAsync(CancellationToken ct = default) =>
        SendAsync(SipCommand.StopIrrigation, ct);

    /// <summary>Skips to the next station in the running sequence (SIP <c>42</c>).</summary>
    public Task AdvanceStationAsync(int station = 0, CancellationToken ct = default) =>
        SendAsync(SipCommand.AdvanceStation, ct, (byte)station);

    /// <summary>Turns the controller's automatic watering on or off (SIP <c>49</c>).</summary>
    public async Task SetControllerEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        await SendAsync(SipCommand.SetControllerState, ct, (byte)(enabled ? 1 : 0)).ConfigureAwait(false);
        InvalidateCachedState();
    }

    // ----------------------------------------------------------- rain delay

    public async Task<int> GetRainDelayAsync(CancellationToken ct = default)
    {
        var r = await SendAsync(SipCommand.GetRainDelay, ct).ConfigureAwait(false);
        return r.Int("delaySetting");
    }

    /// <summary>Sets the rain delay in days (SIP <c>37</c>). Zero clears it.</summary>
    public async Task SetRainDelayAsync(int days, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(days);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(days, 14);

        await TunnelAsync(SipCodec.EncodeUInt16(SipCommand.SetRainDelay, (ushort)days), ct).ConfigureAwait(false);
        InvalidateCachedState();
    }

    // ------------------------------------------------------ seasonal adjust

    public async Task<WaterBudget> GetWaterBudgetAsync(int program = 0, CancellationToken ct = default)
    {
        var r = await SendAsync(SipCommand.GetWaterBudget, ct, (byte)program).ConfigureAwait(false);
        return new WaterBudget(r.Int("programCode"), r.Int("seasonalAdjust"));
    }

    /// <summary>Sets a program's seasonal adjust percentage (SIP <c>31</c>).</summary>
    public async Task SetWaterBudgetAsync(int program, int percent, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(percent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 300);

        var hex = $"{program:X2}{percent:X4}";
        await TunnelAsync(SipCodec.EncodeRaw(SipCommand.SetWaterBudget, hex), ct).ConfigureAwait(false);
        InvalidateCachedState();
    }

    public async Task<ZoneSeasonalAdjust> GetZoneSeasonalAdjustAsync(int program = 0, CancellationToken ct = default)
    {
        var r = await SendAsync(SipCommand.GetZonesSeasonalAdjust, ct, (byte)program).ConfigureAwait(false);

        // 32 nibbles = 16 bytes = one adjust byte per station.
        var raw = r.Hex.Substring(4, 32);
        var factors = new List<int>(16);
        for (var i = 0; i < raw.Length; i += 2)
            factors.Add(Convert.ToInt32(raw.Substring(i, 2), 16));

        return new ZoneSeasonalAdjust(r.Int("programCode"), factors);
    }

    // ------------------------------------------------------------ the clock

    public async Task<TimeOnly> GetControllerTimeAsync(CancellationToken ct = default)
    {
        var r = await SendAsync(SipCommand.CurrentTime, ct).ConfigureAwait(false);
        return new TimeOnly(r.Int("hour"), r.Int("minute"), r.Int("second"));
    }

    public async Task<DateOnly> GetControllerDateAsync(CancellationToken ct = default)
    {
        var r = await SendAsync(SipCommand.CurrentDate, ct).ConfigureAwait(false);
        return SafeDate(r.Int("year"), r.Int("month"), r.Int("day"));
    }

    public Task SetControllerTimeAsync(TimeOnly time, CancellationToken ct = default) =>
        SendAsync(SipCommand.SetTime, ct, (byte)time.Hour, (byte)time.Minute, (byte)time.Second);

    /// <summary>
    /// Sets the date (SIP <c>13</c>). Note the packing: day is a byte, but month is a
    /// single nibble and year takes the remaining three.
    /// </summary>
    public Task SetControllerDateAsync(DateOnly date, CancellationToken ct = default)
    {
        var hex = $"{date.Day:X2}{date.Month:X1}{date.Year:X3}";
        return TunnelAsync(SipCodec.EncodeRaw(SipCommand.SetDate, hex), ct);
    }

    // ------------------------------------------------------------ schedules

    /// <summary>Reads one schedule page (SIP <c>20</c>). See §6 of the protocol doc.</summary>
    public async Task<string> GetSchedulePageAsync(int page, CancellationToken ct = default)
    {
        var r = await TunnelAsync(SipCodec.EncodeRaw(SipCommand.RetrieveSchedule, $"00{page:X2}"), ct)
            .ConfigureAwait(false);
        return r.Hex;
    }

    /// <summary>Writes one schedule page (SIP <c>21</c>).</summary>
    public Task SetSchedulePageAsync(int page, string payloadHex, CancellationToken ct = default) =>
        TunnelAsync(SipCodec.EncodeRaw(SipCommand.SetSchedule, $"00{page:X2}{payloadHex}"), ct);

    // --------------------------------------------------------- capabilities

    /// <summary>
    /// Builds the capability profile for this controller: model lookup, station
    /// discovery, and live probing of the optional command groups. Called once at
    /// connect time so the UI never offers a control the hardware will reject.
    /// </summary>
    public async Task<ControllerCapabilities> ProbeCapabilitiesAsync(CancellationToken ct = default)
    {
        var modelVersion = await GetModelAndVersionAsync(ct).ConfigureAwait(false);
        var model = modelVersion.Model;

        var serial = await SafeAsync(() => GetSerialNumberAsync(ct), "").ConfigureAwait(false);
        var firmware = await SafeAsync(() => GetFirmwareVersionAsync(ct), new FirmwareVersion(0, 0, 0))
            .ConfigureAwait(false);

        var stations = new List<int>();
        for (var page = 0; page <= model.MaxStationPages; page++)
        {
            var available = await GetAvailableStationsAsync(page, ct).ConfigureAwait(false);
            stations.AddRange(available.Stations);
        }

        // A controller that reports no stations is almost certainly a model whose
        // station mask we're reading from the wrong page. Fall back to a sane default
        // rather than presenting an empty app.
        if (stations.Count == 0)
            stations.AddRange(Enumerable.Range(1, 8));

        return new ControllerCapabilities
        {
            Model = model,
            SerialNumber = serial,
            Firmware = firmware,
            Stations = stations,
            // Ask the controller rather than inferring from the model table: firmware
            // within one model varies, and a wrong guess means offering a control that
            // can only ever be rejected.
            SupportsSchedulePages = await IsCommandSupportedAsync(SipCommand.RetrieveSchedule, ct)
                .ConfigureAwait(false),
            SupportsCombinedState = await IsCommandSupportedAsync(SipCommand.CombinedControllerState, ct)
                .ConfigureAwait(false),
            SupportsControllerToggle = await IsCommandSupportedAsync(SipCommand.SetControllerState, ct)
                .ConfigureAwait(false),
            SupportsUniversalTransport = await IsCommandSupportedAsync(SipCommand.UniversalMessage, ct)
                .ConfigureAwait(false),
            SupportsFlowMonitoring = await IsCommandSupportedAsync(SipCommand.FlowMonitorStatus, ct)
                .ConfigureAwait(false),
            SupportsIrrigationStatistics = await IsCommandSupportedAsync(SipCommand.IrrigationStatistics, ct)
                .ConfigureAwait(false),
            SupportsZoneSeasonalAdjust = await IsCommandSupportedAsync(SipCommand.GetZonesSeasonalAdjust, ct)
                .ConfigureAwait(false),
            SupportsStationErrors = await IsCommandSupportedAsync(SipCommand.CurrentStationError, ct)
                .ConfigureAwait(false),
        };
    }

    private static async Task<T> SafeAsync<T>(Func<Task<T>> action, T fallback)
    {
        try { return await action().ConfigureAwait(false); }
        catch (RainBirdNakException) { return fallback; }
        catch (RainBirdProtocolException) { return fallback; }
    }
}
