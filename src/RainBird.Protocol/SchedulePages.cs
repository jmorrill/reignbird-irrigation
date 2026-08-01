using System.Globalization;
using System.Text;

namespace RainBird.Protocol;

/// <summary>
/// The page layout of the controller's schedule (SIP <c>20</c>/<c>21</c>), for the
/// ESP-ME / ESP-ME3 family.
///
/// See §6 of <c>docs/rainbird-protocol.md</c> for the full page map.
/// </summary>
public static class SchedulePages
{
    public const int GlobalInfo = 0;
    public const int ProgramInfoBase = 15;
    public const int StartTimesBase = 95;
    public const int RunTimesBase = 128;

    /// <summary>A start time slot the controller treats as empty.</summary>
    public const int UnsetStartTime = 0xFFFF;

    public static int ProgramInfo(int programIndex) => ProgramInfoBase + programIndex;

    public static int StartTimes(int programIndex) => StartTimesBase + programIndex;

    /// <summary>
    /// The page holding a station's run times. Each page carries a pair — the
    /// odd-numbered station first, then the following even one.
    /// </summary>
    public static int RunTimes(int stationNumber)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stationNumber, 1);
        return RunTimesBase + (stationNumber - 1) / 2;
    }

    /// <summary>The two stations a run-time page covers, odd first.</summary>
    public static (int Odd, int Even) StationPair(int stationNumber)
    {
        var odd = stationNumber % 2 == 0 ? stationNumber - 1 : stationNumber;
        return (odd, odd + 1);
    }

    /// <summary>True when the station is the first of its page's pair.</summary>
    public static bool IsFirstOfPair(int stationNumber) => stationNumber % 2 != 0;

    // ------------------------------------------------------------ page 0

    public sealed record GlobalInfoPage(int InterStationDelaySeconds, int Snooze, bool RainSensorBypassed);

    public static string EncodeGlobalInfo(GlobalInfoPage page) =>
        $"{page.InterStationDelaySeconds:X4}{page.Snooze:X2}{(page.RainSensorBypassed ? 1 : 0):X2}";

    public static GlobalInfoPage DecodeGlobalInfo(string payload)
    {
        Require(payload, 8, "global info");
        return new GlobalInfoPage(
            Hex(payload, 0, 4),
            Hex(payload, 4, 2),
            Hex(payload, 6, 2) != 0);
    }

    // ---------------------------------------------------- page 15 + n

    public sealed record ProgramInfoPage(
        IReadOnlyList<bool> CustomDays,
        int CyclicDays,
        int DaysRemaining,
        int PermanentDaysOff,
        int SeasonalAdjustPercent,
        FrequencyType Frequency);

    public static string EncodeProgramInfo(ProgramInfoPage page)
    {
        var mask = 0;
        for (var i = 0; i < page.CustomDays.Count && i < 7; i++)
            if (page.CustomDays[i]) mask |= 1 << i;

        return $"{mask:X2}{page.CyclicDays:X2}{page.DaysRemaining:X2}"
             + $"{page.PermanentDaysOff:X2}{page.SeasonalAdjustPercent:X2}{(int)page.Frequency:X2}";
    }

    public static ProgramInfoPage DecodeProgramInfo(string payload)
    {
        Require(payload, 12, "program info");

        var mask = Hex(payload, 0, 2);
        var days = new bool[7];
        for (var i = 0; i < 7; i++) days[i] = (mask & (1 << i)) != 0;

        return new ProgramInfoPage(
            days,
            Hex(payload, 2, 2),
            Hex(payload, 4, 2),
            Hex(payload, 6, 2),
            Hex(payload, 8, 2),
            (FrequencyType)Hex(payload, 10, 2));
    }

    // ---------------------------------------------------- page 95 + n

    /// <summary>Start times as minutes from midnight, one 16-bit value each.</summary>
    public static string EncodeStartTimes(IEnumerable<int> minutesFromMidnight) =>
        string.Concat(minutesFromMidnight.Select(m => $"{(m < 0 ? UnsetStartTime : m):X4}"));

    public static IReadOnlyList<int> DecodeStartTimes(string payload)
    {
        var times = new List<int>(payload.Length / 4);
        for (var i = 0; i + 4 <= payload.Length; i += 4)
            times.Add(Hex(payload, i, 4));
        return times;
    }

    /// <summary>True when a slot holds a real time rather than the unset sentinel.</summary>
    public static bool IsStartTimeSet(int value) => value is >= 0 and < 1440;

    // --------------------------------------------------- page 128 + k

    /// <summary>
    /// A run-time page: two stations, each with one 16-bit run time per program.
    /// </summary>
    /// <param name="OddStationRunTimes">Run times for the odd station, indexed by program.</param>
    /// <param name="EvenStationRunTimes">Run times for the following even station.</param>
    public sealed record RunTimePage(
        IReadOnlyList<int> OddStationRunTimes,
        IReadOnlyList<int> EvenStationRunTimes);

    public static string EncodeRunTimes(RunTimePage page, int maxPrograms)
    {
        var builder = new StringBuilder(maxPrograms * 8);
        AppendStation(builder, page.OddStationRunTimes, maxPrograms);
        AppendStation(builder, page.EvenStationRunTimes, maxPrograms);
        return builder.ToString();

        static void AppendStation(StringBuilder builder, IReadOnlyList<int> runTimes, int maxPrograms)
        {
            for (var program = 0; program < maxPrograms; program++)
            {
                var minutes = program < runTimes.Count ? runTimes[program] : 0;
                builder.Append(CultureInfo.InvariantCulture, $"{Math.Clamp(minutes, 0, 0xFFFF):X4}");
            }
        }
    }

    public static RunTimePage DecodeRunTimes(string payload, int maxPrograms)
    {
        var perStation = maxPrograms * 4;
        Require(payload, perStation * 2, "run times");

        return new RunTimePage(
            ReadStation(payload, 0, maxPrograms),
            ReadStation(payload, perStation, maxPrograms));

        static int[] ReadStation(string payload, int offset, int maxPrograms)
        {
            var values = new int[maxPrograms];
            for (var i = 0; i < maxPrograms; i++)
                values[i] = Hex(payload, offset + i * 4, 4);
            return values;
        }
    }

    // -------------------------------------------------------------- helpers

    /// <summary>
    /// Strips the response header from a schedule read. A <c>20</c> request comes back
    /// as <c>A0 00 &lt;page&gt; &lt;payload&gt;</c>.
    /// </summary>
    public static string PayloadOf(string scheduleResponseHex)
    {
        if (scheduleResponseHex.Length < 6)
            throw new RainBirdProtocolException($"Schedule response too short: '{scheduleResponseHex}'.");
        return scheduleResponseHex[6..];
    }

    private static int Hex(string payload, int offset, int length) =>
        int.Parse(payload.AsSpan(offset, length), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static void Require(string payload, int nibbles, string what)
    {
        if (payload.Length < nibbles)
            throw new RainBirdProtocolException(
                $"Schedule {what} page needs {nibbles} hex characters but got {payload.Length}: '{payload}'.");
    }
}
