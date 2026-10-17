
using DackHem.Api.Data;
using DackHem.Api.Models;
using DackHem.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;

namespace DackHem.Api.Controllers
{
    [ApiController]
    [Route("api/bookings")]
    public class BookingsController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly BookingEmailService _email;

        public BookingsController(AppDbContext db, BookingEmailService email)
        {
            _db = db;
            _email = email;
        }

        public class CreateBookingRequest
        {
            [Required, StringLength(100)]
            public string CustomerName { get; set; } = "";

            [Required, Phone, StringLength(30)]
            public string PhoneNumber { get; set; } = "";

            [Required, EmailAddress, StringLength(254)]
            public string Email { get; set; } = "";

            [Required, StringLength(20)]
            public string RegistrationNumber { get; set; } = "";

            [Required, StringLength(300)]
            public string Address { get; set; } = "";

            [Required]
            public DateTime BookingDateTime { get; set; }

            [StringLength(1000)]
            public string? Notes { get; set; }
        }

        [HttpPost]
        [EnableRateLimiting("booking-write")]
        public async Task<IActionResult> Create(
            [FromBody] CreateBookingRequest request)
        {
            if (request.BookingDateTime.Kind != DateTimeKind.Unspecified)
            {
                return BadRequest(
                    "Ange svensk lokal tid utan tidszonsangivelse.");
            }

            if (request.BookingDateTime <=
                TimeZoneInfo.ConvertTimeFromUtc(
                    DateTime.UtcNow,
                    TimeZoneInfo.FindSystemTimeZoneById(
                        "Europe/Stockholm")))
            {
                return BadRequest(
                    "Du måste välja en framtida tid.");
            }

            var booking = new Booking
            {
                CustomerName = request.CustomerName.Trim(),
                PhoneNumber = request.PhoneNumber.Trim(),
                Email = request.Email.Trim(),
                RegistrationNumber =
                    request.RegistrationNumber.Trim().ToUpperInvariant(),
                Address = request.Address.Trim(),
                BookingDateTime = request.BookingDateTime,
                Notes = request.Notes,
                Price = 345,
                Status = "Confirmed"
            };

            // Hemlig avbokningsnyckel.
            var token = Convert.ToHexString(
                RandomNumberGenerator.GetBytes(32));

            booking.CancellationTokenHash =
                Convert.ToHexString(
                    SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(token)));

            _db.Bookings.Add(booking);
            await _db.SaveChangesAsync();
            // Send independent emails concurrently instead of making the customer wait twice.
            var customerEmailTask = _email.SendConfirmationAsync(booking, token);
            var ownerEmailTask = _email.SendOwnerNotificationAsync(booking, cancelled: false);
            await Task.WhenAll(customerEmailTask, ownerEmailTask);
            var emailSent = await customerEmailTask;

            return Created(
                $"/api/bookings/{booking.Id}",
                new
                {
                    booking.Id,
                    booking.BookingDateTime,
                    booking.Price,
                    booking.Status,
                    CancellationToken = token,
                    EmailSent = emailSent
                });
        }

        [HttpPost("cancel")]
        [EnableRateLimiting("booking-write")]
        public async Task<IActionResult> Cancel(
            [FromBody] CancelBookingRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Token))
                return BadRequest("Avbokningsnyckel saknas.");

            var hash = Convert.ToHexString(
                SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(request.Token)));

            var booking = await _db.Bookings
                .FirstOrDefaultAsync(b =>
                    b.CancellationTokenHash == hash);

            if (booking == null)
                return NotFound("Bokningen hittades inte.");

            if (booking.Status == "Cancelled")
                return BadRequest("Bokningen är redan avbokad.");

            var swedishNow =
                TimeZoneInfo.ConvertTimeFromUtc(
                    DateTime.UtcNow,
                    TimeZoneInfo.FindSystemTimeZoneById(
                        "Europe/Stockholm"));

            if (booking.BookingDateTime <= swedishNow.AddHours(3))
            {
                return BadRequest(
                    "Du kan avboka via hemsidan mer än 3 timmar före besöket. Ring 070-789 23 40 vid senare avbokning.");
            }

            await using var transaction = await _db.Database.BeginTransactionAsync();
            booking.Status = "Cancelled";
            await _db.SaveChangesAsync();
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO BookingCancellationEvents (BookingId, CancelledAtUtc)
                VALUES ({booking.Id.ToString().ToUpperInvariant()}, {DateTime.UtcNow.ToString("O")})
                """);
            await transaction.CommitAsync();
            await _email.SendOwnerNotificationAsync(booking, cancelled: true);

            return Ok(new
            {
                Message = "Din bokning är avbokad."
            });
        }

        public class CancelBookingRequest
        {
            [Required]
            public string Token { get; set; } = "";
        }
    }
}

