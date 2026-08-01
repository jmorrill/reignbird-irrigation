namespace RainBird.Protocol;

/// <summary>
/// The complete SIP command set. Values are the command byte that leads the
/// <c>tunnelSip</c> payload.
/// </summary>
public enum SipCommand : byte
{
    // --- Identity and capability ---
    ModelAndVersion = 0x02,
    AvailableStations = 0x03,
    CommandSupport = 0x04,
    SerialNumber = 0x05,
    AlternateBitRateSupport = 0x09,
    ControllerFirmwareVersion = 0x0B,
    UniversalMessage = 0x0C,

    // --- Clock ---
    CurrentTime = 0x10,
    SetTime = 0x11,
    CurrentDate = 0x12,
    SetDate = 0x13,
    SetTimeZone = 0x2B,
    GetTimeZone = 0xFC,

    // --- Schedule (page protocol, see RetrieveSchedule/SetSchedule) ---
    RetrieveSchedule = 0x20,
    SetSchedule = 0x21,

    // --- Seasonal adjust ---
    GetWaterBudget = 0x30,
    SetWaterBudget = 0x31,
    GetZonesSeasonalAdjust = 0x32,
    SetZonesSeasonalAdjust = 0x33,

    // --- Rain delay ---
    GetRainDelay = 0x36,
    SetRainDelay = 0x37,

    // --- Manual operation ---
    ManuallyRunProgram = 0x38,
    ManuallyRunStation = 0x39,
    TestStations = 0x3A,
    CurrentQueue = 0x3B,
    CurrentStationError = 0x3D,
    CurrentRainSensorState = 0x3E,
    CurrentStationsActive = 0x3F,
    StopIrrigation = 0x40,
    AdvanceStation = 0x42,

    // --- State ---
    CurrentIrrigationState = 0x48,
    SetControllerState = 0x49,
    ControllerEventTimestamp = 0x4A,
    StackManuallyRunStation = 0x4B,
    CombinedControllerState = 0x4C,
    IrrigationStatistics = 0x4D,

    // --- Flow (model dependent) ---
    StartLearnFlowSequence = 0x60,
    CancelLearnFlowSequence = 0x61,
    LearnFlowSequenceStatus = 0x62,
    FlowMonitorStatus = 0x63,
    SetFlowMonitorStatus = 0x64,
    FlowMonitorRate = 0x65,

    // --- Log ---
    LogEntries = 0x70,
}

/// <summary>Response codes the controller can send.</summary>
public enum SipResponse : byte
{
    Nak = 0x00,
    Ack = 0x01,
    ModelAndVersion = 0x82,
    AvailableStations = 0x83,
    CommandSupport = 0x84,
    SerialNumber = 0x85,
    ControllerFirmwareVersion = 0x8B,
    UniversalMessage = 0x8C,
    CurrentTime = 0x90,
    CurrentDate = 0x92,
    Schedule = 0xA0,
    WaterBudget = 0xB0,
    ZonesSeasonalAdjust = 0xB2,
    RainDelaySetting = 0xB6,
    CurrentQueue = 0xBB,
    CurrentRainSensorState = 0xBE,
    CurrentStationsActive = 0xBF,
    CurrentStationError = 0xBD,
    CurrentIrrigationState = 0xC8,
    ControllerEventTimestamp = 0xCA,
    CombinedControllerState = 0xCC,
    IrrigationStatistics = 0xCD,
    LearnFlowSequence = 0xE0,
    LearnFlowSequenceStatus = 0xE2,
    FlowMonitorStatus = 0xE3,
    FlowMonitorRate = 0xE5,
}

/// <summary>
/// A field within a SIP response.
///
/// Offsets and lengths are in <b>nibbles</b> (hex characters) of the response's hex
/// string, exactly as the app's <c>sipcommands.json</c> expresses them. This type
/// exists so that unit is stated once, here, and never re-derived at a call site.
/// </summary>
/// <param name="NibblePosition">Offset into the hex string.</param>
/// <param name="NibbleLength">Length in hex characters.</param>
public readonly record struct SipField(int NibblePosition, int NibbleLength);

/// <summary>The layout of one response type.</summary>
/// <param name="Code">Leading response byte.</param>
/// <param name="Name">Human-readable name, matching the app's naming.</param>
/// <param name="LengthBytes">Total response length in bytes, or null when variable.</param>
/// <param name="Fields">Named fields, keyed by the app's field names.</param>
public sealed record SipResponseSpec(
    byte Code,
    string Name,
    int? LengthBytes,
    IReadOnlyDictionary<string, SipField> Fields);

/// <summary>
/// Response layout table: which bytes of a SIP response mean what, per command.
///
/// Covers more response types than the widely-circulated community command lists,
/// which omit several that real controllers emit — notably the station-error and
/// current-station forms.
/// </summary>
public static class SipResponseTable
{
    private static SipResponseSpec Spec(
        byte code, string name, int? lengthBytes, params (string Name, int Pos, int Len)[] fields) =>
        new(code, name, lengthBytes,
            fields.ToDictionary(f => f.Name, f => new SipField(f.Pos, f.Len)));

    public static readonly IReadOnlyDictionary<byte, SipResponseSpec> All =
        new SipResponseSpec[]
        {
            Spec(0x00, "NotAcknowledgeResponse", 3,
                ("commandEcho", 2, 2), ("NAKCode", 4, 2)),

            Spec(0x01, "AcknowledgeResponse", 2,
                ("commandEcho", 2, 2)),

            Spec(0x82, "ModelAndVersionResponse", 5,
                ("modelID", 2, 4), ("protocolRevisionMajor", 6, 2), ("protocolRevisionMinor", 8, 2)),

            Spec(0x83, "AvailableStationsResponse", 6,
                ("pageNumber", 2, 2), ("setStations", 4, 8)),

            Spec(0x84, "CommandSupportResponse", 3,
                ("commandEcho", 2, 2), ("support", 4, 2)),

            Spec(0x85, "SerialNumberResponse", 9,
                ("serialNumber", 2, 16)),

            // Length varies by firmware: an ESP-ME3 on protocol 2.12 answers with 9
            // bytes, not the 5 the app's own table implies. Only major and minor are
            // reliably positioned, so the rest is left to the caller.
            Spec(0x8B, "ControllerFirmwareVersionResponse", null,
                ("major", 2, 2), ("minor", 4, 2)),

            Spec(0x90, "CurrentTimeResponse", 4,
                ("hour", 2, 2), ("minute", 4, 2), ("second", 6, 2)),

            Spec(0x92, "CurrentDateResponse", 4,
                ("day", 2, 2), ("month", 4, 1), ("year", 5, 3)),

            Spec(0xB0, "WaterBudgetResponse", 4,
                ("programCode", 2, 2), ("seasonalAdjust", 4, 4)),

            Spec(0xB2, "ZonesSeasonalAdjustFactorResponse", 18,
                ("programCode", 2, 2), ("stationsSA", 4, 32)),

            Spec(0xB6, "RainDelaySettingResponse", 3,
                ("delaySetting", 2, 4)),

            Spec(0xBE, "CurrentRainSensorStateResponse", 2,
                ("sensorState", 2, 2)),

            Spec(0xBF, "CurrentStationsActiveResponse", 6,
                ("pageNumber", 2, 2), ("activeStations", 4, 8)),

            // Undocumented in the app's resource table, but real hardware answers
            // CurrentStationError (0x3D) with it: a station bitmask of faulted zones,
            // laid out exactly like the active-stations response.
            Spec(0xBD, "CurrentStationErrorResponse", 6,
                ("pageNumber", 2, 2), ("errorStations", 4, 8)),

            Spec(0xC8, "CurrentIrrigationStateResponse", 2,
                ("irrigationState", 2, 2)),

            Spec(0xCA, "ControllerEventTimestampResponse", 6,
                ("eventId", 2, 2), ("timestamp", 4, 8)),

            // The workhorse: one round trip yields the entire dashboard.
            Spec(0xCC, "CombinedControllerStateResponse", 16,
                ("hour", 2, 2), ("minute", 4, 2), ("second", 6, 2),
                ("day", 8, 2), ("month", 10, 1), ("year", 11, 3),
                ("delaySetting", 14, 4), ("sensorState", 18, 2),
                ("irrigationState", 20, 2), ("seasonalAdjust", 22, 4),
                ("remainingRuntime", 26, 4), ("activeStation", 30, 2)),

            // Variable-length responses: no field table, handled by dedicated decoders.
            Spec(0xA0, "RetrieveScheduleResponse", null),
            Spec(0xBB, "CurrentQueueResponse", null),
            Spec(0xCD, "IrrigationStatisticsResponse", null),
            Spec(0x8C, "UniversalMessageTransportResponse", null),
            Spec(0xE0, "LearnFlowSequenceResponse", null),
            Spec(0xE2, "LearnFlowSequenceStatusResponse", null),
            Spec(0xE3, "FlowMonitorStatusResponse", null),
            Spec(0xE5, "FlowMonitorRateResponse", null),
        }.ToDictionary(s => s.Code);

    public static SipResponseSpec? Find(byte code) =>
        All.TryGetValue(code, out var spec) ? spec : null;
}
