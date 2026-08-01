using System.Globalization;
using System.Net;
using System.Text.Json;

namespace RainBird.Server.Services;

/// <summary>A place someone might mean, with everything the app needs to use it.</summary>
/// <param name="Name">"Denver".</param>
/// <param name="Region">State or province, where there is one — what tells two Denvers apart.</param>
/// <param name="Country">Two-letter code.</param>
/// <param name="TimeZoneId">
/// IANA name for the place. Worth having: watering runs in local time, and a
/// controller in a different zone from the server would otherwise water at the
/// server's idea of 6am.
/// </param>
public sealed record GeocodedPlace(
    string Name,
    string? Region,
    string? Country,
    double Latitude,
    double Longitude,
    string? TimeZoneId);

/// <summary>
/// Turns something a person can type into coordinates.
///
/// Nobody knows their latitude. They know their postcode, or the name of their town,
/// and asking for decimal degrees to get a weather forecast is the kind of thing that
/// makes a feature go unused.
///
/// Open-Meteo's geocoder handles both — a US ZIP resolves as readily as a place name —
/// and it comes back with the IANA time zone as well, which the app needs anyway and
/// would otherwise have to be picked from a list of four hundred.
/// </summary>
public sealed class GeocodingService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GeocodingService> _logger;

    public GeocodingService(IHttpClientFactory httpClientFactory, ILogger<GeocodingService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Finds places matching a postcode or name. Returns empty rather than throwing
    /// when the lookup fails: not finding somewhere is an ordinary outcome of typing,
    /// and the search box should say "no matches", not "something went wrong".
    /// </summary>
    public async Task<IReadOnlyList<GeocodedPlace>> SearchAsync(
        string query, int limit = 6, CancellationToken ct = default)
    {
        var trimmed = query.Trim();
        if (trimmed.Length < 2) return [];

        var url =
            "https://geocoding-api.open-meteo.com/v1/search"
            + $"?name={WebUtility.UrlEncode(trimmed)}"
            + $"&count={limit}&language=en&format=json";

        try
        {
            var http = _httpClientFactory.CreateClient("weather");
            using var response = await http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!document.RootElement.TryGetProperty("results", out var results))
                return [];

            return results.EnumerateArray().Select(Parse).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Place lookup failed for {Query}", trimmed);
            return [];
        }
    }

    private static GeocodedPlace Parse(JsonElement element) => new(
        Name: element.GetProperty("name").GetString() ?? "",
        Region: Text(element, "admin1"),
        Country: Text(element, "country_code"),
        Latitude: Round(element.GetProperty("latitude").GetDouble()),
        Longitude: Round(element.GetProperty("longitude").GetDouble()),
        TimeZoneId: Text(element, "timezone"));

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Four decimal places is about eleven metres, which is far beyond what a weather
    /// forecast resolves. The rest is noise that only makes the stored value look
    /// more precise than it is.
    /// </summary>
    private static double Round(double value) =>
        Math.Round(value, 4, MidpointRounding.AwayFromZero);

    /// <summary>Formats a place the way it should read in a list: "Denver, Colorado, US".</summary>
    public static string Describe(GeocodedPlace place) =>
        string.Join(", ", new[] { place.Name, place.Region, place.Country }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>Renders coordinates for display, without locale-dependent decimal separators.</summary>
    public static string DescribeCoordinates(double latitude, double longitude) =>
        latitude.ToString("0.####", CultureInfo.InvariantCulture)
        + ", "
        + longitude.ToString("0.####", CultureInfo.InvariantCulture);
}
