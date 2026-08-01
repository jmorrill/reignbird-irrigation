namespace RainBird.Protocol;

/// <summary>
/// A Rain Bird controller model, keyed by the <c>modelID</c> in the SIP <c>82</c>
/// response.
/// </summary>
/// <param name="ModelId">Four hex digits as returned by ModelAndVersion.</param>
/// <param name="Series">Marketing name, e.g. "ESP-ME3".</param>
/// <param name="IsProgramBased">
/// Program-based models expose programs A/B/C(/D) with shared start times. The
/// others (RZXe, ST8x) schedule per zone instead.
/// </param>
/// <param name="MaxPrograms">Programs the model supports.</param>
/// <param name="MaxStartTimes">Start times per program.</param>
/// <param name="MaxStationPages">Extra station pages beyond the first 32 stations.</param>
/// <param name="Transport">How the app reaches this model.</param>
public sealed record ControllerModel(
    string ModelId,
    string Series,
    bool IsProgramBased,
    int MaxPrograms,
    int MaxStartTimes,
    int MaxStationPages,
    ControllerTransport Transport = ControllerTransport.WiFi)
{
    /// <summary>Stations addressable, given the page count.</summary>
    public int MaxStations => 32 * (MaxStationPages + 1);
}

public enum ControllerTransport
{
    /// <summary>Reachable over HTTP at <c>/stick</c> — everything this app supports.</summary>
    WiFi,

    /// <summary>Bluetooth only. Out of scope: there is no HTTP endpoint.</summary>
    Bluetooth,
}

public static class ControllerModels
{
    public static readonly IReadOnlyList<ControllerModel> All =
    [
        new("0003", "ESP-RZXe",   false,  0, 6, 0),
        new("0005", "ESP-TM2",    true,   3, 4, 0),
        new("0006", "ST8x-WiFi",  false,  0, 6, 0),
        new("0007", "ESP-Me",     true,   4, 6, 0),
        new("0008", "ST8x-WiFi2", false,  8, 6, 0),
        new("0009", "ESP-ME3",    true,   4, 6, 0),
        new("000A", "ESP-TM2",    true,   3, 4, 0),
        new("000C", "LXME2",      true,  40, 10, 1),
        new("000D", "LX-IVM",     true,  10, 8, 1),
        new("000E", "LX-IVM Pro", true,  40, 8, 7),
        new("0010", "ESP-Me2",    true,   4, 6, 0),
        new("0011", "ESP-2WIRE",  true,   4, 6, 1),
        new("0014", "ESP-TM2",    true,   3, 4, 0),
        new("0015", "TRU",        true,   3, 4, 0),
        new("0099", "TBOS-BT",    true,   3, 8, 0, ControllerTransport.Bluetooth),
        new("0103", "ESP-RZXe2",  false,  8, 6, 0),
        new("010A", "ESP-TM2",    true,   3, 4, 0),
        new("0812", "RC2",        true,   3, 4, 0),
        new("0813", "ARC8",       true,   3, 4, 0),
    ];

    private static readonly Dictionary<string, ControllerModel> ByIdMap =
        All.GroupBy(m => m.ModelId).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Looks up a model. Unknown IDs get a conservative fallback rather than an
    /// exception — a controller we don't recognise should still be usable for the
    /// basics, with capability probing filling in the rest.
    /// </summary>
    public static ControllerModel Lookup(string modelId) =>
        ByIdMap.TryGetValue(modelId, out var model)
            ? model
            : new ControllerModel(modelId, $"Unknown ({modelId})", true, 3, 4, 0);
}
