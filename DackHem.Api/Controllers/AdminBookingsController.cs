using System.Security.Cryptography;
using System.Text;
using System.ComponentModel.DataAnnotations;
using DackHem.Api.Services;
using DackHem.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.RateLimiting;

namespace DackHem.Api.Controllers;

[ApiController]
[Route("api/admin/bookings")]
[EnableRateLimiting("admin-read-write")]
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
    public async Task<IActionResult> GetBookings(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 30,
        [FromQuery] string? search = null, [FromQuery] string status = "all",
        [FromQuery] string workStatus = "all", [FromQuery] bool todayOnly = false,
        [FromQuery] string sort = "upcoming", [FromQuery] DateTime? date = null)
    {
        var authError = AuthorizeAdmin();
        if (authError != null) return authError;
        page = Math.Clamp(page, 1, 100000);
        pageSize = Math.Clamp(pageSize, 1, 100);
        search = (search ?? "").Trim();
        if (search.Length > 150) return BadRequest(new { message = "Sökningen är för lång." });
        if (status is not ("all" or "Confirmed" or "Cancelled") ||
            workStatus is not ("all" or "Waiting" or "InProgress" or "Done") ||
            sort is not ("upcoming" or "newest"))
            return BadRequest(new { message = "Ogiltigt filter." });

        var stockholmNow = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm"));
        var today = stockholmNow.Date;
        var tomorrow = today.AddDays(1);

        // Apply search and date filters before paging, across the entire database.
        var query = _db.Bookings.AsNoTracking();
        if (search.Length > 0)
            query = query.Where(b => EF.Functions.Like(b.CustomerName, "%" + search + "%") ||
                EF.Functions.Like(b.PhoneNumber, "%" + search + "%") ||
                EF.Functions.Like(b.RegistrationNumber, "%" + search + "%"));
        if (status != "all") query = query.Where(b => b.Status == status);
        if (date.HasValue)
        {
            if (date.Value.Kind != DateTimeKind.Unspecified)
                return BadRequest(new { message = "Ange datum utan tidszon." });
            var selected = date.Value.Date;
            var nextDay = selected.AddDays(1);
            query = query.Where(b => b.BookingDateTime >= selected && b.BookingDateTime < nextDay);
        }
        if (todayOnly) query = query.Where(b => b.Status != "Cancelled" &&
            b.BookingDateTime >= today && b.BookingDateTime < tomorrow);

        // Work states are stored in the existing separate SQLite table.
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

        // Overview follows the selected calendar date, otherwise today's date.
        var overviewDate = date?.Date ?? today;
        var overviewNextDay = overviewDate.AddDays(1);
        var todaysBookings = await _db.Bookings.AsNoTracking()
            .Where(b => b.Status != "Cancelled" && b.BookingDateTime >= overviewDate && b.BookingDateTime < overviewNextDay)
            .Select(b => b.Id).ToListAsync();
        var waiting = 0; var inProgress = 0; var done = 0;
        foreach (var id in todaysBookings)
        {
            work.TryGetValue(id.ToString().ToUpperInvariant(), out var w);
            switch (w.Status ?? "Waiting")
            {
                case "InProgress": inProgress++; break;
                case "Done": done++; break;
                default: waiting++; break;
            }
        }
        var overview = new { date = overviewDate.ToString("yyyy-MM-dd"), todayCount = todaysBookings.Count, waitingCount = waiting,
            progressCount = inProgress, doneCount = done };

        // Work filtering must happen before paging too.
        // This materializes only the booking rows matching the text/date filters.
        var matching = await query.ToListAsync();
        var filtered = matching.Where(b =>
        {
            if (workStatus == "all") return true;
            if (b.Status == "Cancelled") return false;
            work.TryGetValue(b.Id.ToString().ToUpperInvariant(), out var w);
            return (w.Status ?? "Waiting") == workStatus;
        });
        var ordered = sort == "newest"
            ? filtered.OrderByDescending(b => b.BookingDateTime).ThenBy(b => b.Id)
            : filtered.OrderBy(b => b.BookingDateTime >= stockholmNow ? 0 : 1)
                .ThenBy(b => b.BookingDateTime >= stockholmNow ? b.BookingDateTime : DateTime.MaxValue)
                .ThenByDescending(b => b.BookingDateTime < stockholmNow ? b.BookingDateTime : DateTime.MinValue)
                .ThenBy(b => b.Id);
        var resultsAll = ordered.ToList();
        var total = resultsAll.Count;
        var customerCancelledIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cancelConnection = _db.Database.GetDbConnection();
        await cancelConnection.OpenAsync();
        try
        {
            using var command = cancelConnection.CreateCommand();
            command.CommandText = "SELECT BookingId FROM BookingCancellationEvents";
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) customerCancelledIds.Add(reader.GetString(0));
        }
        finally { await cancelConnection.CloseAsync(); }

        var bookings = resultsAll.Skip((page - 1) * pageSize).Take(pageSize).Select(b =>
        {
            work.TryGetValue(b.Id.ToString().ToUpperInvariant(), out var w);
            return new
            {
                b.Id, b.CustomerName, b.PhoneNumber, b.Email, b.RegistrationNumber,
                b.Address, b.BookingDateTime, b.Notes, b.Price, b.Status, b.CreatedAtUtc,
                CancelledByCustomer = customerCancelledIds.Contains(b.Id.ToString()),
                WorkStatus = b.Status == "Cancelled" ? "Cancelled" : w.Status ?? "Waiting",
                AssignedTo = w.Assignee ?? "",
                InternalNote = w.Note ?? ""
            };
        }).ToList();
        return Ok(new { total, page, pageSize, overview, bookings });
    }

    [HttpGet("customer-cancellations")]
    public async Task<IActionResult> CustomerCancellations([FromQuery] long after = 0)
    {
        var authError = AuthorizeAdmin();
        if (authError != null) return authError;
        after = Math.Max(0, after);
        var events = new List<object>();
        long latest = after;
        var connection = _db.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            using var maxCommand = connection.CreateCommand();
            maxCommand.CommandText = "SELECT COALESCE(MAX(Id), 0) FROM BookingCancellationEvents";
            latest = Math.Max(after, Convert.ToInt64(await maxCommand.ExecuteScalarAsync()));
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT e.Id, b.CustomerName, b.BookingDateTime, b.RegistrationNumber
                FROM BookingCancellationEvents e
                JOIN Bookings b ON UPPER(b.Id) = e.BookingId
                WHERE e.Id > $after
                ORDER BY e.Id
                LIMIT 50
                """;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$after";
            parameter.Value = after;
            command.Parameters.Add(parameter);
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                events.Add(new { id = reader.GetInt64(0), customerName = reader.GetString(1),
                    bookingDateTime = reader.GetString(2), registrationNumber = reader.GetString(3) });
        }
        finally { await connection.CloseAsync(); }
        return Ok(new { latest, events });
    }

    [HttpGet("calendar")]
    public async Task<IActionResult> GetCalendar([FromQuery] int year, [FromQuery] int month)
    {
        var authError = AuthorizeAdmin();
        if (authError != null) return authError;
        if (year < 2000 || year > 2100 || month < 1 || month > 12)
            return BadRequest(new { message = "Ogiltig månad." });

        var first = new DateTime(year, month, 1);
        var next = first.AddMonths(1);
        var days = await _db.Bookings.AsNoTracking()
            .Where(b => b.Status != "Cancelled" &&
                b.BookingDateTime >= first && b.BookingDateTime < next)
            .GroupBy(b => b.BookingDateTime.Date)
            .Select(g => new { date = g.Key, count = g.Count() })
            .ToListAsync();
        return Ok(new { year, month, days });
    }


    public sealed class ClearAllBookingsRequest
    {
        [Required]
        public string Confirmation { get; set; } = "";
    }

    // Temporary administrator-only maintenance endpoint.
    // It creates a durable SQLite snapshot before deleting any booking data.
    [HttpPost("clear-all")]
    public async Task<IActionResult> ClearAllBookings([FromBody] ClearAllBookingsRequest request)
    {
        var authError = AuthorizeAdmin();
        if (authError != null) return authError;
        if (request.Confirmation != "RADERA ALLA BOKNINGAR")
            return BadRequest(new { message = "Bekräftelsen stämmer inte. Ingen bokning har raderats." });

        var configuredPath = _configuration["Database:Path"];
        var dbPath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(configuredPath) ? "dackhem.db" : configuredPath);
        if (!System.IO.File.Exists(dbPath))
            return StatusCode(503, new { message = "Databasen hittades inte. Ingen radering har utförts." });

        var backupDir = Path.Combine(Path.GetDirectoryName(dbPath)!, "dackhem-backups");
        Directory.CreateDirectory(backupDir);
        var backupPath = Path.Combine(backupDir, "before-clear-" +
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".db");
        var tempPath = backupPath + ".tmp";

        // Do not delete anything if a consistent backup cannot be created and verified.
        try
        {
            var sourceString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString();
            var destString = new SqliteConnectionStringBuilder
            {
                DataSource = tempPath, Pooling = false
            }.ToString();
            using (var source = new SqliteConnection(sourceString))
            using (var dest = new SqliteConnection(destString))
            {
                source.Open();
                dest.Open();
                source.BackupDatabase(dest);
                using var check = dest.CreateCommand();
                check.CommandText = "PRAGMA integrity_check";
                if (!string.Equals(check.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("SQLite backup integrity check failed");
            }
            System.IO.File.Move(tempPath, backupPath);
        }
        catch (Exception)
        {
            if (System.IO.File.Exists(tempPath))
            {
                try { System.IO.File.Delete(tempPath); } catch (IOException) { }
            }
            return StatusCode(503, new { message = "Säkerhetskopian kunde inte skapas. Inga bokningar har raderats." });
        }

        // All four tables are cleared atomically. Do not delete the volume,
        // protection keys, or the database schema.
        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();
            var removed = await _db.Bookings.CountAsync();
            await _db.Database.ExecuteSqlRawAsync("DELETE FROM BookingEmailOutbox");
            await _db.Database.ExecuteSqlRawAsync("DELETE FROM BookingCancellationEvents");
            await _db.Database.ExecuteSqlRawAsync("DELETE FROM BookingWork");
            await _db.Database.ExecuteSqlRawAsync("DELETE FROM Bookings");
            await transaction.CommitAsync();
            return Ok(new
            {
                message = "Alla bokningar och tillhörande testuppgifter har raderats.",
                removed,
                backupCreated = true
            });
        }
        catch (Exception)
        {
            return StatusCode(500, new
            {
                message = "Rensningen kunde inte slutföras. Ändringarna har rullats tillbaka. En säkerhetskopia har skapats."
            });
        }
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
