using RainBird.Protocol;
using RainBird.Server.Data;
using RainBird.Server.Services;

namespace RainBird.Server.Api;

// Requests --------------------------------------------------------------------

public sealed record AddControllerRequest(
    string Host,
    string Password,
    string? Name,
    double? Latitude,
    double? Longitude,
    string? TimeZoneId);

public sealed record UpdateControllerRequest(
    string? Name,
    string? Host,
    string? Password,
    double? Latitude,
    double? Longitude,
    string? TimeZoneId);

public sealed record RunZoneRequest(int Minutes);

public sealed record RunAllRequest(int Minutes);

public sealed record RainDelayRequest(int Days);

public sealed record SeasonalAdjustRequest(int Program, int Percent);

public sealed record ControllerEnabledRequest(bool Enabled);

public sealed record SetClockRequest(bool UseServerTime, DateTimeOffset? Value);

public sealed record UpdateZoneRequest(
    string? Name,
    PlantType? PlantType,
    SoilType? SoilType,
    SunExposure? SunExposure,
    SlopeGrade? Slope,
    SprinklerType? SprinklerType,
    double? NozzleFlowGpm,
    bool? Enabled,
    int? SortOrder);

public sealed record SaveProgramRequest(
    FrequencyType Frequency,
    IReadOnlyList<bool> CustomDays,
    int CyclicDays,
    int SeasonalAdjustPercent,
    IReadOnlyList<int> StartTimes,
    IReadOnlyDictionary<int, int> StationRunTimes);

// Responses -------------------------------------------------------------------

public sealed record ControllerSummary(
    int Id,
    string Name,
    string Host,
    string ModelId,
    string ModelSeries,
    string SerialNumber,
    string FirmwareVersion,
    bool Online,
    string? LastError,
    DateTimeOffset? LastSeenUtc,
    double? Latitude,
    double? Longitude,
    string TimeZoneId,
    ControllerStateDto? State);

public sealed record ControllerStateDto(
    string ControllerTime,
    string ControllerDate,
    int RainDelayDays,
    string SensorState,
    bool ControllerEnabled,
    bool IsWatering,
    int ActiveStation,
    int RemainingRuntimeSeconds,
    int SeasonalAdjustPercent)
{
    public static ControllerStateDto From(CombinedState state) => new(
        state.ControllerTime.ToString("HH:mm:ss"),
        state.ControllerDate.ToString("yyyy-MM-dd"),
        state.RainDelayDays,
        state.SensorState.ToString(),
        state.ControllerEnabled,
        state.IsWatering,
        state.ActiveStation,
        state.RemainingRuntimeSeconds,
        state.SeasonalAdjustPercent);
}

public sealed record CapabilitiesDto(
    string ModelId,
    string Series,
    bool IsProgramBased,
    int MaxPrograms,
    int MaxStartTimes,
    IReadOnlyList<int> Stations,
    bool SupportsSchedulePages,
    bool SupportsCombinedState,
    bool SupportsControllerToggle,
    bool SupportsUniversalTransport,
    bool RequiresSoftwareScheduling,
    bool SupportsFlowMonitoring,
    bool SupportsIrrigationStatistics,
    bool SupportsZoneSeasonalAdjust,
    bool SupportsStationErrors)
{
    public static CapabilitiesDto From(ControllerCapabilities capabilities) => new(
        capabilities.Model.ModelId,
        capabilities.Model.Series,
        capabilities.Model.IsProgramBased,
        capabilities.Model.MaxPrograms,
        capabilities.Model.MaxStartTimes,
        capabilities.Stations,
        capabilities.SupportsSchedulePages,
        capabilities.SupportsCombinedState,
        capabilities.SupportsControllerToggle,
        capabilities.SupportsUniversalTransport,
        capabilities.RequiresSoftwareScheduling,
        capabilities.SupportsFlowMonitoring,
        capabilities.SupportsIrrigationStatistics,
        capabilities.SupportsZoneSeasonalAdjust,
        capabilities.SupportsStationErrors);
}

public sealed record ZoneDto(
    int Id,
    int StationNumber,
    string Name,
    string? PhotoUrl,
    PlantType PlantType,
    SoilType SoilType,
    SunExposure SunExposure,
    SlopeGrade Slope,
    SprinklerType SprinklerType,
    double NozzleFlowGpm,
    bool Enabled,
    int SortOrder,
    DateTimeOffset? LastRunUtc,
    int? LastRunSeconds,
    bool IsWatering,
    int RemainingSeconds)
{
    public static ZoneDto From(
        ZoneRecord zone,
        RunRecord? lastRun,
        CombinedState? state) => new(
        zone.Id,
        zone.StationNumber,
        zone.Name,
        zone.PhotoPath is null ? null : $"/media/{zone.PhotoPath}",
        zone.PlantType,
        zone.SoilType,
        zone.SunExposure,
        zone.Slope,
        zone.SprinklerType,
        zone.NozzleFlowGpm,
        zone.Enabled,
        zone.SortOrder,
        lastRun?.StartedUtc,
        lastRun?.DurationSeconds,
        state?.IsWatering == true && state.ActiveStation == zone.StationNumber,
        state?.ActiveStation == zone.StationNumber ? state.RemainingRuntimeSeconds : 0);
}

public sealed record ProgramDto(
    int ProgramNumber,
    string Label,
    FrequencyType Frequency,
    IReadOnlyList<bool> CustomDays,
    int CyclicDays,
    int SeasonalAdjustPercent,
    IReadOnlyList<int> StartTimes,
    IReadOnlyDictionary<int, int> StationRunTimes,
    bool Enabled,
    int TotalMinutes)
{
    public static ProgramDto From(ProgramSchedule program) => new(
        program.ProgramNumber,
        LabelFor(program.ProgramNumber),
        program.Frequency,
        program.CustomDays,
        program.CyclicDays,
        program.SeasonalAdjustPercent,
        program.StartTimes,
        program.StationRunTimes,
        program.Enabled,
        program.StationRunTimes.Values.Sum());

    /// <summary>Rain Bird labels programs A, B, C, D on the faceplate.</summary>
    public static string LabelFor(int index) =>
        index < 26 ? ((char)('A' + index)).ToString() : $"P{index + 1}";
}

public sealed record RunDto(
    long Id,
    int StationNumber,
    string ZoneName,
    DateTimeOffset StartedUtc,
    DateTimeOffset? EndedUtc,
    int DurationSeconds,
    RunTrigger Trigger,
    double EstimatedGallons);

public sealed record WeatherDayDto(
    string Date,
    string Condition,
    int ConditionCode,
    double TempHighC,
    double TempLowC,
    double PrecipitationMm,
    int PrecipitationProbability,
    double WindKph,
    bool HasScheduledRun,
    string? SkipReason)
{
    public static WeatherDayDto From(
        WeatherDayRecord day, bool hasScheduledRun, SkipEventRecord? skip) => new(
        day.Date.ToString("yyyy-MM-dd"),
        WeatherService.ConditionOf(day.ConditionCode),
        day.ConditionCode,
        day.TempHighC,
        day.TempLowC,
        day.PrecipitationMm,
        day.PrecipitationProbability,
        day.WindKph,
        hasScheduledRun,
        skip?.Reason.ToString());
}

public sealed record UsageDto(
    string Month,
    double GallonsUsed,
    int TotalMinutes,
    int RunCount,
    IReadOnlyList<ZoneUsageDto> ByZone);

public sealed record ZoneUsageDto(int StationNumber, string ZoneName, double Gallons, int Minutes);

public sealed record SipExchangeDto(
    DateTimeOffset At, string Method, string Request, string? Response, string? Error);

public sealed record CalendarDayDto(
    string Date,
    int RunCount,
    int TotalMinutes,
    double Gallons,
    string? SkipReason,
    bool Scheduled);
