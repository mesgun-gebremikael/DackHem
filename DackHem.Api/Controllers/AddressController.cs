using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace DackHem.Api.Controllers;

[ApiController]
[Route("api/address")]
public class AddressController : ControllerBase
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly ConcurrentDictionary<string, DateTimeOffset> LastRequests = new();
    private readonly IConfiguration _config;
    public AddressController(IConfiguration config) => _config = config;

    [HttpGet("suggest")]
    public async Task<IActionResult> Suggest([FromQuery] string q, [FromQuery] string? session)
    {
        q = (q ?? "").Trim();
        if (q.Length < 4 || q.Length > 120) return BadRequest("Skriv en giltig adress.");
        // Interpret glued street and house numbers, e.g. "Holmvägen21" as "Holmvägen 21".
        // This is used only for suggestions, not to change the customer's saved address.
        q = System.Text.RegularExpressions.Regex.Replace(q, @"(?<=[\p{L}])(?=\d)", " ");
        // Use Google Places if configured; fall back to Photon/OpenStreetMap if unavailable.
        // The free public Photon service is best-effort and may have rate limits.
        var key = _config["GoogleMaps:ApiKey"];
        var client = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var now = DateTimeOffset.UtcNow;
        if (LastRequests.TryGetValue(client, out var last) && (now - last).TotalMilliseconds < 400)
            return StatusCode(429, "Vänta en stund.");
        LastRequests[client] = now;
        if (LastRequests.Count > 10000)
            foreach (var entry in LastRequests.Where(x => now - x.Value > TimeSpan.FromMinutes(5)))
                LastRequests.TryRemove(entry.Key, out _);

        if (!string.IsNullOrWhiteSpace(key))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://places.googleapis.com/v1/places:autocomplete");
                request.Headers.Add("X-Goog-Api-Key", key);
                request.Content = JsonContent.Create(new
                {
                    input = q,
                    includedRegionCodes = new[] { "se" },
                    languageCode = "sv",
                    sessionToken = Guid.TryParse(session, out _) ? session : null
                });
                using var response = await Http.SendAsync(request, HttpContext.RequestAborted);
                if (response.IsSuccessStatusCode)
                {
                    using var json = await JsonDocument.ParseAsync(
                        await response.Content.ReadAsStreamAsync(HttpContext.RequestAborted),
                        cancellationToken: HttpContext.RequestAborted);
                    var results = new List<object>();
                    if (json.RootElement.TryGetProperty("suggestions", out var suggestions))
                        foreach (var suggestion in suggestions.EnumerateArray())
                        {
                            if (!suggestion.TryGetProperty("placePrediction", out var prediction) ||
                                !prediction.TryGetProperty("text", out var text) ||
                                !text.TryGetProperty("text", out var label)) continue;
                            var value = label.GetString();
                            if (!string.IsNullOrWhiteSpace(value)) results.Add(new { label = value });
                            if (results.Count >= 5) break;
                        }
                    if (results.Count > 0) return Ok(results);
                }
                else
                {
                    // Never log API keys or raw customer address strings.
                    HttpContext.RequestServices.GetRequiredService<ILogger<AddressController>>()
                        .LogWarning("Google Places autocomplete unavailable (HTTP {StatusCode}); trying fallback.", (int)response.StatusCode);
                }
            }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { return new EmptyResult(); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                HttpContext.RequestServices.GetRequiredService<ILogger<AddressController>>()
                    .LogWarning("Google Places autocomplete error ({ErrorType}); trying fallback.", ex.GetType().Name);
            }
        }

        try
        {
            var uri = "https://photon.komoot.io/api/?" +
                "q=" + Uri.EscapeDataString(q) + "&limit=6&lat=59.50&lon=17.90";
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("DackHem/1.0 (address autocomplete)");
            using var response = await Http.SendAsync(request, HttpContext.RequestAborted);
            if (!response.IsSuccessStatusCode)
                return StatusCode(502, "Adressökningen är tillfälligt otillgänglig. Skriv adressen manuellt.");
            using var json = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(HttpContext.RequestAborted),
                cancellationToken: HttpContext.RequestAborted);
            var results = new List<object>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (json.RootElement.TryGetProperty("features", out var features))
            foreach (var feature in features.EnumerateArray())
            {
                if (!feature.TryGetProperty("properties", out var properties)) continue;
                static string Get(JsonElement obj, string property) =>
                    obj.TryGetProperty(property, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "";
                var country = Get(properties, "countrycode");
                if (!string.IsNullOrEmpty(country) && !country.Equals("SE", StringComparison.OrdinalIgnoreCase)) continue;
                var street = Get(properties, "street");
                var house = Get(properties, "housenumber");
                var name = Get(properties, "name");
                var city = Get(properties, "city");
                if (string.IsNullOrWhiteSpace(city)) city = Get(properties, "district");
                if (string.IsNullOrWhiteSpace(city)) city = Get(properties, "county");
                var postal = Get(properties, "postcode");
                var line = string.IsNullOrWhiteSpace(street) ? name : street + (string.IsNullOrWhiteSpace(house) ? "" : " " + house);
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = new[] { line, postal, city }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
                var label = string.Join(", ", parts) + ", Sverige";
                if (seen.Add(label)) results.Add(new { label });
                if (results.Count >= 5) break;
            }
            return Ok(results);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { return new EmptyResult(); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            HttpContext.RequestServices.GetRequiredService<ILogger<AddressController>>()
                .LogWarning("OpenStreetMap address fallback unavailable ({ErrorType}).", ex.GetType().Name);
            return StatusCode(502, "Adressökningen är tillfälligt otillgänglig. Skriv adressen manuellt.");
        }
    }
}
