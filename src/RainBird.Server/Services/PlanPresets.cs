using RainBird.Server.Data;

namespace RainBird.Server.Services;

/// <summary>A ready-made plan the user can drop in and adjust.</summary>
/// <param name="Key">Stable identifier used by the API.</param>
/// <param name="Name">What it will be called once created.</param>
/// <param name="Summary">One line explaining when to reach for it.</param>
/// <param name="Rationale">Why it is shaped this way — the horticultural reasoning.</param>
public sealed record PlanPreset(
    string Key,
    string Name,
    string Summary,
    string Rationale,
    Func<IReadOnlyList<ZoneRecord>, WateringPlan> Build);

/// <summary>
/// Starting points for common watering situations.
///
/// These exist because the interesting cases are the ones a Rain Bird controller
/// cannot express. Seed germination wants four short passes a day; a hardware
/// program runs the same durations at every start time and caps how many it has.
/// Once the schedule lives here, those arrangements are just data.
/// </summary>
public static class PlanPresets
{
    public static readonly IReadOnlyList<PlanPreset> All =
    [
        new(
            "standard",
            "Standard watering",
            "One pass in the early morning, three days a week.",
            "Early morning loses the least to evaporation and leaves foliage dry by "
            + "nightfall, which is what keeps fungal disease down. Deep and infrequent "
            + "beats little and often for established plants: it draws roots downward.",
            zones => new WateringPlan
            {
                Name = "Standard watering",
                Description = "Mon, Wed, Fri at 6:00am",
                Frequency = PlanFrequency.DaysOfWeek,
                DaysOfWeek = "0101010",
                StartTimes = "360",
                CycleSoakEnabled = true,
                Cycles = 2,
                SoakMinutes = 20,
                Zones = DefaultZones(zones),
            }),

        new(
            "grass-seed",
            "New grass seed",
            "Four short passes a day, every day.",
            "Germinating seed must not dry out, and the seed bed is only wet in its top "
            + "centimetre — so this waters little and often rather than deeply. Expect to "
            + "run it for two to three weeks, then move to the standard plan once the "
            + "grass is established, because frequent shallow watering keeps roots at the "
            + "surface.",
            zones => new WateringPlan
            {
                Name = "New grass seed",
                Description = "10 minutes, four times a day, every day",
                Frequency = PlanFrequency.EveryDay,
                StartTimes = "360,660,900,1140",   // 6am, 11am, 3pm, 7pm
                CycleSoakEnabled = false,
                WeatherSkipEnabled = false,        // a dry seed bed is a dead seed bed
                Zones = zones
                    .Where(zone => zone.Enabled)
                    .Select((zone, index) => new PlanZone
                    {
                        StationNumber = zone.StationNumber,
                        Minutes = 10,
                        SortOrder = index,
                    })
                    .ToList(),
            }),

        new(
            "new-sod",
            "New sod",
            "Three passes a day, tapering off as roots take.",
            "Fresh sod has no root contact with the soil beneath it, so it dries out from "
            + "the bottom. Keep it damp for the first fortnight, then reduce — pull back "
            + "the run times here as the sod knits down.",
            zones => new WateringPlan
            {
                Name = "New sod",
                Description = "15 minutes, three times a day",
                Frequency = PlanFrequency.EveryDay,
                StartTimes = "360,720,1020",       // 6am, noon, 5pm
                CycleSoakEnabled = false,
                WeatherSkipEnabled = false,
                Zones = zones
                    .Where(zone => zone.Enabled)
                    .Select((zone, index) => new PlanZone
                    {
                        StationNumber = zone.StationNumber,
                        Minutes = 15,
                        SortOrder = index,
                    })
                    .ToList(),
            }),

        new(
            "deep-roots",
            "Deep and infrequent",
            "One long pass twice a week, split to avoid runoff.",
            "The most drought-resilient way to water an established lawn. Long runs on "
            + "clay or a slope run off before they soak in, so this splits each zone into "
            + "three passes with a rest between — the water goes in instead of down the "
            + "drive.",
            zones => new WateringPlan
            {
                Name = "Deep and infrequent",
                Description = "Sunday and Wednesday at 5:00am",
                Frequency = PlanFrequency.DaysOfWeek,
                DaysOfWeek = "1001000",
                StartTimes = "300",
                CycleSoakEnabled = true,
                Cycles = 3,
                SoakMinutes = 30,
                Zones = zones
                    .Where(zone => zone.Enabled)
                    .Select((zone, index) => new PlanZone
                    {
                        StationNumber = zone.StationNumber,
                        Minutes = DefaultMinutes(zone) * 2,
                        SortOrder = index,
                    })
                    .ToList(),
            }),

        new(
            "odd-days",
            "Odd-day restriction",
            "One pass on odd-numbered days.",
            "For water districts that allow watering on odd or even days only. Change the "
            + "frequency to even days if that is the half you are allotted.",
            zones => new WateringPlan
            {
                Name = "Odd-day watering",
                Description = "Odd days of the month at 5:30am",
                Frequency = PlanFrequency.OddDays,
                StartTimes = "330",
                CycleSoakEnabled = true,
                Cycles = 2,
                SoakMinutes = 20,
                Zones = DefaultZones(zones),
            }),
    ];

    public static PlanPreset? Find(string key) =>
        All.FirstOrDefault(preset => preset.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    private static List<PlanZone> DefaultZones(IReadOnlyList<ZoneRecord> zones) =>
        zones
            .Where(zone => zone.Enabled)
            .Select((zone, index) => new PlanZone
            {
                StationNumber = zone.StationNumber,
                Minutes = DefaultMinutes(zone),
                SortOrder = index,
            })
            .ToList();

    /// <summary>
    /// A sensible starting run time for a zone, from its head type.
    ///
    /// Rotors throw far less water per minute than sprays, and drip less again, so a
    /// single default across every zone would badly over- or under-water most of them.
    /// These are starting points to be tuned, not recommendations.
    /// </summary>
    internal static int DefaultMinutes(ZoneRecord zone) => zone.SprinklerType switch
    {
        SprinklerType.Rotor => 25,
        SprinklerType.RotaryNozzle => 20,
        SprinklerType.FixedSpray => 12,
        SprinklerType.Bubbler => 15,
        SprinklerType.Drip => 30,
        SprinklerType.Emitter => 30,
        _ => 15,
    };
}
