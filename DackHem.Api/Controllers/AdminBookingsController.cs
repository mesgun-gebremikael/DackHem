using System.Security.Cryptography;
using System.Text;
using DackHem.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DackHem.Api.Controllers;

[ApiController]
[Route("api/admin/bookings")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class AdminBookingsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _configuration;

    public AdminBookingsController(AppDbContext db, IConfiguration configuration)
    {
        _db = db;
        _configuration = configuration;
    }

    [HttpGet]
    public async Task<IActionResult> GetBookings([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        Response.Headers.CacheControl = "no-store";
        var configuredKey = _configuration["Admin:ApiKey"];
        var providedKey = Request.Headers["X-Admin-Key"].ToString();

        // Fail closed until the owner configures a strong secret outside source control.
        if (string.IsNullOrWhiteSpace(configuredKey) || configuredKey.Length < 24)
            return StatusCode(503, new { message = "Adminpanelen är inte konfigurerad. Ange Admin:ApiKey i User Secrets." });

        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(configuredKey));
        var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(providedKey));
        if (string.IsNullOrEmpty(providedKey) ||
            !CryptographicOperations.FixedTimeEquals(expected, supplied))
            return Unauthorized(new { message = "Fel adminnyckel." });

        page = Math.Clamp(page, 1, 100000);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _db.Bookings.AsNoTracking();
        var total = await query.CountAsync();
        var bookings = await query
            .OrderByDescending(b => b.BookingDateTime)
            .ThenBy(b => b.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(b => new
            {
                b.Id,
                b.CustomerName,
                b.PhoneNumber,
                b.Email,
                b.RegistrationNumber,
                b.Address,
                b.BookingDateTime,
                b.Notes,
                b.Price,
                b.Status,
                b.CreatedAtUtc
            })
            .ToListAsync();

        return Ok(new { total, page, pageSize, bookings });
    }
}
