using System.Security.Cryptography;
using System.Text;
using System.ComponentModel.DataAnnotations;
using DackHem.Api.Services;
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
    private readonly BookingEmailService _email;

    public AdminBookingsController(AppDbContext db, IConfiguration configuration, BookingEmailService email)
    {
        _db = db;
        _configuration = configuration;
        _email = email;
    }

    [HttpGet]
    public async Task<IActionResult> GetBookings([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var authError = AuthorizeAdmin();
        if (authError != null) return authError;

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

        var work = new Dictionary<string, (string Status, string Assignee, string Note)>();
        var connection = _db.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT BookingId, WorkStatus, AssignedTo, InternalNote FROM BookingWork";
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                work[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2), reader.GetString(3));
        }
        finally { await connection.CloseAsync(); }

        var results = bookings.Select(b =>
        {
            work.TryGetValue(b.Id.ToString().ToUpperInvariant(), out var state);
            return new {
                b.Id, b.CustomerName, b.PhoneNumber, b.Email, b.RegistrationNumber,
                b.Address, b.BookingDateTime, b.Notes, b.Price, b.Status, b.CreatedAtUtc,
                WorkStatus = b.Status == "Cancelled" ? "Cancelled" : state.Status ?? "Waiting",
                AssignedTo = state.Assignee ?? "",
                InternalNote = state.Note ?? ""
            };
        }).ToList();
        return Ok(new { total, page, pageSize, bookings = results });
    }
    public sealed class RescheduleRequest
    {
        [Required]
        public DateTime BookingDateTime { get; set; }
    }

    [HttpPut("{id:guid}/reschedule")]
    public async Task<IActionResult> Reschedule(Guid id, [FromBody] RescheduleRequest request)
    {
        Response.Headers.CacheControl = "no-store";
        var authError = AuthorizeAdmin();
        if (authError != null) return authError;

        if (request.BookingDateTime.Kind != DateTimeKind.Unspecified)
            return BadRequest(new { message = "Ange svensk lokal tid utan tidszon." });

        var stockholmNow = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm"));
        if (request.BookingDateTime <= stockholmNow)
            return BadRequest(new { message = "Välj en framtida tid." });

        var booking = await _db.Bookings.FirstOrDefaultAsync(b => b.Id == id);
        if (booking == null) return NotFound(new { message = "Bokningen hittades inte." });
        if (booking.Status == "Cancelled")
            return BadRequest(new { message = "En avbokad bokning kan inte flyttas." });
        if (booking.BookingDateTime == request.BookingDateTime)
            return BadRequest(new { message = "Välj en annan tid än den nuvarande." });

        var oldTime = booking.BookingDateTime;
        booking.BookingDateTime = request.BookingDateTime;
        await _db.SaveChangesAsync();
        var emailSent = await _email.SendRescheduleAsync(booking, oldTime);
        return Ok(new { message = emailSent
            ? "Tiden har ändrats och kunden har fått mejl."
            : "Tiden har ändrats men mejlet kunde inte skickas. Kontakta kunden själv.",
            booking.Id, booking.BookingDateTime, emailSent });
    }


    public sealed class WorkUpdateRequest
    {
        [Required]
        public string WorkStatus { get; set; } = "Waiting";
        public string AssignedTo { get; set; } = "";
        public string InternalNote { get; set; } = "";
    }

    [HttpPut("{id:guid}/work")]
    public async Task<IActionResult> UpdateWork(Guid id, [FromBody] WorkUpdateRequest request)
    {
        var authError = AuthorizeAdmin();
        if (authError != null) return authError;
        if (request.WorkStatus is not ("Waiting" or "InProgress" or "Done"))
            return BadRequest(new { message = "Ogiltig arbetsstatus." });
        if (request.AssignedTo is not ("" or "Owner" or "Colleague"))
            return BadRequest(new { message = "Välj dig själv, kollegan eller ingen." });
        if ((request.InternalNote ?? "").Length > 1000)
            return BadRequest(new { message = "Kommentaren får vara högst 1000 tecken." });

        var booking = await _db.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id);
        if (booking == null) return NotFound(new { message = "Bokningen hittades inte." });
        if (booking.Status == "Cancelled")
            return BadRequest(new { message = "Avbokade bokningar kan inte ändras." });

        var bookingId = id.ToString().ToUpperInvariant();
        var note = (request.InternalNote ?? "").Trim();
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO BookingWork (BookingId, WorkStatus, AssignedTo, InternalNote)
            VALUES ({bookingId}, {request.WorkStatus}, {request.AssignedTo}, {note})
            ON CONFLICT(BookingId) DO UPDATE SET
                WorkStatus = excluded.WorkStatus,
                AssignedTo = excluded.AssignedTo,
                InternalNote = excluded.InternalNote
            """);
        return Ok(new { message = "Arbetsstatus sparad." });
    }

    private IActionResult? AuthorizeAdmin()
    {
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
        return null;
    }
}
