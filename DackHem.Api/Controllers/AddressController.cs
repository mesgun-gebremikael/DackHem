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
        var key = _config["GoogleMaps:ApiKey"];
        if (string.IsNullOrWhiteSpace(key)) return StatusCode(503, "Adressökning är inte konfigurerad.");

        // Limit repeated requests per client. No key is sent to the browser.
        var client = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var now = DateTimeOffset.UtcNow;
        if (LastRequests.TryGetValue(client, out var last) && (now - last).TotalMilliseconds < 400)
            return StatusCode(429, "Vänta en stund.");
        LastRequests[client] = now;
        if (LastRequests.Count > 10000)
            foreach (var entry in LastRequests.Where(x => now - x.Value > TimeSpan.FromMinutes(5)))
                LastRequests.TryRemove(entry.Key, out _);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://places.googleapis.com/v1/places:autocomplete");
        request.Headers.Add("X-Goog-Api-Key", key);
        request.Content = JsonContent.Create(new {
            input = q,
            includedRegionCodes = new[] { "se" },
            languageCode = "sv",
            sessionToken = Guid.TryParse(session, out _) ? session : null
        });
        try
        {
            using var response = await Http.SendAsync(request, HttpContext.RequestAborted);
            if (!response.IsSuccessStatusCode) return StatusCode(502, "Google kunde inte hämta adresser.");
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(), cancellationToken: HttpContext.RequestAborted);
            if (!json.RootElement.TryGetProperty("suggestions", out var suggestions))
                return Ok(Array.Empty<object>());
            var result = new List<object>();
            foreach (var suggestion in suggestions.EnumerateArray())
            {
                if (!suggestion.TryGetProperty("placePrediction", out var prediction)) continue;
                if (!prediction.TryGetProperty("text", out var text) || !text.TryGetProperty("text", out var label)) continue;
                var value = label.GetString();
                if (!string.IsNullOrWhiteSpace(value)) result.Add(new { label = value });
                if (result.Count == 5) break;
            }
            return Ok(result);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { return new EmptyResult(); }
        catch (HttpRequestException) { return StatusCode(502, "Adresstjänsten är inte tillgänglig."); }
        catch (TaskCanceledException) { return StatusCode(504, "Adresstjänsten svarade inte i tid."); }
    }
}
