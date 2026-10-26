using DackHem.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DackHem.Api.Controllers;

[ApiController]
[Route("api/pricing")]
public sealed class PricingController : ControllerBase
{
    private readonly DistancePricingService _pricing;
    public PricingController(DistancePricingService pricing) => _pricing = pricing;

    [HttpPost("quote")]
    [EnableRateLimiting("booking-write")]
    public async Task<IActionResult> Quote([FromBody] QuoteRequest input)
    {
        try
        {
            var quote = await _pricing.QuoteAsync(input.Address ?? "", HttpContext.RequestAborted);
            // Do not disclose the origin address, coordinates or route.
            return Ok(new { price = quote.Price, enabled = quote.Enabled });
        }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        catch (InvalidOperationException ex) { return StatusCode(503, new { message = ex.Message }); }
    }

    public sealed class QuoteRequest { public string? Address { get; set; } }
}
