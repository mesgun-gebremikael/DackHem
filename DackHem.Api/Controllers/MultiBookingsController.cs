using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;
using DackHem.Api.Data;
using DackHem.Api.Models;
using DackHem.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace DackHem.Api.Controllers;

// Additive endpoint: the existing POST /api/bookings is preserved for single-vehicle clients.
[ApiController]
[Route("api/bookings/multi")]
public sealed class MultiBookingsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly DistancePricingService _pricing;
    private readonly BookingEmailQueue _emailQueue;

    public MultiBookingsController(AppDbContext db, DistancePricingService pricing, BookingEmailQueue emailQueue)
    {
        _db = db;
        _pricing = pricing;
        _emailQueue = emailQueue;
    }

    public sealed class VehicleRequest
    {
        [Required, StringLength(20)]
        public string RegistrationNumber { get; set; } = "";
        [Required, StringLength(40)]
        public string CarBrand { get; set; } = "";
        [Required, StringLength(300)]
        public string Address { get; set; } = "";
        [Required]
        public DateTime BookingDateTime { get; set; }
        [StringLength(1000)]
        public string? Notes { get; set; }
        public decimal? QuotedPrice { get; set; }
    }

    public sealed class MultiBookingRequest
    {
        [Required, StringLength(100)]
        public string CustomerName { get; set; } = "";
        [Required, Phone, StringLength(30)]
        public string PhoneNumber { get; set; } = "";
        [Required, EmailAddress, StringLength(254)]
        public string Email { get; set; } = "";
        [Required]
        public List<VehicleRequest> Vehicles { get; set; } = new();
        public decimal? QuotedTotalPrice { get; set; }
    }

    [HttpPost]
    [EnableRateLimiting("booking-write")]
    public async Task<IActionResult> Create([FromBody] MultiBookingRequest request)
    {
        if (request.Vehicles is null || request.Vehicles.Count is < 1 or > 5)
            return BadRequest(new { message = "Välj mellan 1 och 5 personbilar." });

        var now = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm"));
        var plates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bookings = new List<Booking>(request.Vehicles.Count);
        var distances = new List<int?>(request.Vehicles.Count);
        var tokens = new List<string>(request.Vehicles.Count);

        // All price quotes must succeed and match what the customer accepted before writing anything.
        foreach (var vehicle in request.Vehicles)
        {
            if (vehicle is null || string.IsNullOrWhiteSpace(vehicle.RegistrationNumber) ||
                string.IsNullOrWhiteSpace(vehicle.CarBrand) || string.IsNullOrWhiteSpace(vehicle.Address))
                return BadRequest(new { message = "Fyll i registreringsnummer, bilmärke och adress för varje bil." });
            if (vehicle.RegistrationNumber.Length > 20 || vehicle.CarBrand.Length > 40 ||
                vehicle.Address.Length > 300 || (vehicle.Notes?.Length ?? 0) > 1000)
                return BadRequest(new { message = "Ett av fälten är för långt." });
            if (vehicle.BookingDateTime.Kind != DateTimeKind.Unspecified || vehicle.BookingDateTime <= now)
                return BadRequest(new { message = "Välj en framtida svensk lokal tid för varje bil." });

            var plate = vehicle.RegistrationNumber.Trim().ToUpperInvariant();
            if (plate.Any(char.IsControl) || !plates.Add(plate))
                return BadRequest(new { message = "Ange olika, giltiga registreringsnummer för varje bil." });
            var brand = vehicle.CarBrand.Trim();
            if (brand.Any(char.IsControl))
                return BadRequest(new { message = "Bilmärket innehåller ogiltiga tecken." });
            var notes = vehicle.Notes?.Trim();
            var combinedNotes = "Bilmärke: " + brand +
                (string.IsNullOrWhiteSpace(notes) ? "" : "\n" + notes);
            if (combinedNotes.Length > 1100)
                return BadRequest(new { message = "Informationen om bilen är för lång." });

            DistancePricingService.Quote quote;
            try { quote = await _pricing.QuoteAsync(vehicle.Address.Trim(), HttpContext.RequestAborted); }
            catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
            catch (InvalidOperationException ex) { return StatusCode(503, new { message = ex.Message }); }

            if (vehicle.QuotedPrice != quote.Price)
                return Conflict(new { message = "Priset har ändrats. Kontrollera priset för varje bil och försök igen." });

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var booking = new Booking
            {
                CustomerName = request.CustomerName.Trim(),
                PhoneNumber = request.PhoneNumber.Trim(),
                Email = request.Email.Trim(),
                RegistrationNumber = plate,
                Address = vehicle.Address.Trim(),
                BookingDateTime = vehicle.BookingDateTime,
                Notes = combinedNotes,
                Price = quote.Price,
                Status = "Confirmed",
                CancellationTokenHash = Convert.ToHexString(
                    SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)))
            };
            bookings.Add(booking);
            distances.Add(quote.DistanceMeters);
            tokens.Add(token);
        }

        var total = bookings.Sum(b => b.Price);
        if (request.QuotedTotalPrice != total)
            return Conflict(new { message = "Summan har ändrats. Kontrollera totalpriset innan du bokar." });

        var orderId = Guid.NewGuid().ToString("D").ToUpperInvariant();

        // Save all cars and one encrypted consolidated email job atomically.
        await using var transaction = await _db.Database.BeginTransactionAsync(HttpContext.RequestAborted);
        for (var i = 0; i < bookings.Count; i++)
        {
            var booking = bookings[i];
            _db.Bookings.Add(booking);
            await _db.SaveChangesAsync(HttpContext.RequestAborted);
            var bookingId = booking.Id.ToString().ToUpperInvariant();
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO BookingOrderVehicles (OrderId, BookingId, VehicleIndex)
                VALUES ({orderId}, {bookingId}, {i + 1})
                """, HttpContext.RequestAborted);
            if (distances[i].HasValue)
                await _db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO BookingRouteDistance (BookingId, DistanceMeters)
                    VALUES ({bookingId}, {distances[i]!.Value})
                    """, HttpContext.RequestAborted);
        }

        var tokenData = JsonSerializer.Serialize(bookings.Select((b, i) =>
            new GroupEmailToken(b.Id.ToString().ToUpperInvariant(), tokens[i])).ToList());
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO BookingOrderEmailOutbox
                (OrderId, ProtectedTokens, CreatedUtc, NextAttemptUtc)
            VALUES ({orderId}, {_emailQueue.ProtectGroupTokens(tokenData)},
                    {DateTime.UtcNow.ToString("O")}, {DateTime.UtcNow.ToString("O")})
            """, HttpContext.RequestAborted);

        await transaction.CommitAsync(HttpContext.RequestAborted);

        return Created($"/api/bookings/multi/{orderId}", new
        {
            orderId,
            totalPrice = total,
            vehicleCount = bookings.Count,
            emailQueued = true,
            vehicles = bookings.Select(b => new {
                id = b.Id,
                registrationNumber = b.RegistrationNumber,
                address = b.Address,
                bookingDateTime = b.BookingDateTime,
                price = b.Price
            })
        });
    }

    public sealed record GroupEmailToken(string BookingId, string Token);
}
