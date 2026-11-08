using System.Text.Json;
using DackHem.Api.Controllers;
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
    public string ProtectGroupTokens(string tokensJson) => _protector.Protect(tokensJson);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try { await ProcessPendingAsync(stoppingToken); await ProcessMultiPendingAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError("Email outbox processing error ({Type}).", ex.GetType().Name); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }


    private async Task ProcessMultiPendingAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var email = scope.ServiceProvider.GetRequiredService<BookingEmailService>();
        var jobs = new List<(string OrderId, string Tokens, bool CustomerSent, bool OwnerSent)>();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT OrderId, ProtectedTokens, CustomerSent, OwnerSent
                FROM BookingOrderEmailOutbox
                WHERE (CustomerSent = 0 OR OwnerSent = 0)
                  AND NextAttemptUtc <= $now
                ORDER BY CreatedUtc LIMIT 10
                """;
            var now = cmd.CreateParameter();
            now.ParameterName = "$now";
            now.Value = DateTime.UtcNow.ToString("O");
            cmd.Parameters.Add(now);
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
                var tokens = JsonSerializer.Deserialize<List<MultiBookingsController.GroupEmailToken>>(
                    _protector.Unprotect(job.Tokens)) ?? new();
                if (tokens.Count is < 1 or > 5) throw new InvalidOperationException("Invalid multi order token count");
                var vehicles = new List<(DackHem.Api.Models.Booking Booking, string Token)>();
                foreach (var item in tokens)
                {
                    var booking = await db.Bookings.FirstOrDefaultAsync(
                        b => b.Id == Guid.Parse(item.BookingId), ct);
                    if (booking is null) throw new InvalidOperationException("Booking missing from multi order");
                    vehicles.Add((booking, item.Token));
                }

                var customerSent = job.CustomerSent ||
                    await email.SendMultiBookingEmailAsync(vehicles, owner: false);
                var ownerSent = job.OwnerSent ||
                    await email.SendMultiBookingEmailAsync(vehicles, owner: true);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE BookingOrderEmailOutbox SET
                        CustomerSent = {(customerSent ? 1 : 0)},
                        OwnerSent = {(ownerSent ? 1 : 0)},
                        Attempts = Attempts + 1,
                        NextAttemptUtc = {DateTime.UtcNow.AddMinutes(2).ToString("O")}
                    WHERE OrderId = {job.OrderId}
                    """, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Multi-order email delivery failed ({Type}).", ex.GetType().Name);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE BookingOrderEmailOutbox SET
                        Attempts = Attempts + 1,
                        NextAttemptUtc = {DateTime.UtcNow.AddMinutes(2).ToString("O")}
                    WHERE OrderId = {job.OrderId}
                    """, ct);
            }
        }
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
