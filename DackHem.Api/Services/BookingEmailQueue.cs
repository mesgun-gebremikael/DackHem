using DackHem.Api.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace DackHem.Api.Services;

// Durable SQLite outbox. Cancellation tokens are encrypted at rest.
public sealed class BookingEmailQueue : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<BookingEmailQueue> _logger;
    private readonly IDataProtector _protector;

    public BookingEmailQueue(IServiceScopeFactory scopes, ILogger<BookingEmailQueue> logger,
        IDataProtectionProvider protection)
    {
        _scopes = scopes;
        _logger = logger;
        _protector = protection.CreateProtector("DackHem.BookingEmailOutbox.v1");
    }

    public string ProtectToken(string token) => _protector.Protect(token);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try { await ProcessPendingAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError("Email outbox processing error ({Type}).", ex.GetType().Name); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ProcessPendingAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var email = scope.ServiceProvider.GetRequiredService<BookingEmailService>();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        var jobs = new List<(string Id, string Token, bool CustomerSent, bool OwnerSent)>();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT BookingId, ProtectedToken, CustomerSent, OwnerSent
                FROM BookingEmailOutbox
                WHERE (CustomerSent = 0 OR OwnerSent = 0)
                  AND NextAttemptUtc <= $now
                ORDER BY CreatedUtc LIMIT 20
                """;
            var now = cmd.CreateParameter(); now.ParameterName = "$now";
            now.Value = DateTime.UtcNow.ToString("O"); cmd.Parameters.Add(now);
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                jobs.Add((reader.GetString(0), reader.GetString(1),
                    reader.GetInt32(2) != 0, reader.GetInt32(3) != 0));
        }
        finally { await connection.CloseAsync(); }

        foreach (var job in jobs)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var booking = await db.Bookings.FirstOrDefaultAsync(b => b.Id == Guid.Parse(job.Id), ct);
                if (booking == null) continue;
                var customerSent = job.CustomerSent;
                var ownerSent = job.OwnerSent;
                if (!customerSent)
                    customerSent = await email.SendConfirmationAsync(booking, _protector.Unprotect(job.Token));
                if (!ownerSent)
                    ownerSent = await email.SendOwnerNotificationAsync(booking, cancelled: false);
                // Retry failures with a delay. Each recipient is tracked separately.
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE BookingEmailOutbox SET
                        CustomerSent = {(customerSent ? 1 : 0)},
                        OwnerSent = {(ownerSent ? 1 : 0)},
                        Attempts = Attempts + 1,
                        NextAttemptUtc = {DateTime.UtcNow.AddMinutes(2).ToString("O")}
                    WHERE BookingId = {job.Id}
                    """, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Email outbox delivery attempt failed ({Type}).", ex.GetType().Name);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE BookingEmailOutbox SET
                        Attempts = Attempts + 1,
                        NextAttemptUtc = {DateTime.UtcNow.AddMinutes(2).ToString("O")}
                    WHERE BookingId = {job.Id}
                    """, ct);
            }
        }
    }
}
