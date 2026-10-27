using System.Net.Http.Json;
using System.Text.Json;

namespace DackHem.Api.Services;

// Price is calculated on the server. Never reveal the private route origin to browsers.
public sealed class DistancePricingService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly IConfiguration _config;
    private readonly ILogger<DistancePricingService> _log;
    public DistancePricingService(IConfiguration config, ILogger<DistancePricingService> log)
    {
        _config = config;
        _log = log;
    }

    public bool Enabled => _config.GetValue<bool>("Pricing:Enabled");

    public sealed record Quote(decimal Price, int? DistanceMeters, bool Enabled);

    public async Task<Quote> QuoteAsync(string address, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Length > 300)
            throw new ArgumentException("Ange en giltig adress.");

        // Rollout safeguard: preserve existing 350 kr behavior until explicitly enabled.
        if (!Enabled) return new Quote(350m, null, false);

        var key = _config["Pricing:GoogleRoutesApiKey"];
        var origin = _config["Pricing:OriginAddress"];
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(origin))
            throw new InvalidOperationException("Prisberäkningen är ännu inte konfigurerad.");

        // Prevent a bare city or postcode from silently yielding a misleading quote.
        if (!System.Text.RegularExpressions.Regex.IsMatch(address, @"\b\d{1,4}[A-Za-z]?\b"))
            throw new ArgumentException("Ange gata, husnummer och ort för att se priset.");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://routes.googleapis.com/directions/v2:computeRoutes");
            request.Headers.TryAddWithoutValidation("X-Goog-Api-Key", key);
            request.Headers.TryAddWithoutValidation("X-Goog-FieldMask", "routes.distanceMeters");
            request.Content = JsonContent.Create(new
            {
                origin = new { address = origin },
                destination = new { address },
                travelMode = "DRIVE",
                routingPreference = "TRAFFIC_UNAWARE",
                computeAlternativeRoutes = false,
                languageCode = "sv-SE",
                units = "METRIC"
            });
            using var response = await Http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("Google Routes kunde inte beräkna priset (HTTP {StatusCode}).", (int)response.StatusCode);
                throw new InvalidOperationException("Priset kunde inte beräknas just nu. Försök igen senare.");
            }
            using var json = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            if (!json.RootElement.TryGetProperty("routes", out var routes) ||
                routes.GetArrayLength() < 1 ||
                !routes[0].TryGetProperty("distanceMeters", out var metersElement) ||
                !metersElement.TryGetInt32(out var meters) || meters < 0)
                throw new InvalidOperationException("Ingen körväg hittades för adressen.");

            // Ceil kilometres for clearly defined inclusive price zones; 15.01 km = 450 kr.
            var km = (int)Math.Ceiling(meters / 1000.0);
            decimal price = km <= 15 ? 350 : km <= 25 ? 450 : km <= 40 ? 550 :
                550 + 100 * (int)Math.Ceiling((km - 40) / 10.0);
            return new Quote(price, meters, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _log.LogWarning("Ruttjänsten kunde inte användas ({ErrorType}).", ex.GetType().Name);
            throw new InvalidOperationException("Priset kunde inte beräknas just nu. Försök igen senare.");
        }
    }
}
