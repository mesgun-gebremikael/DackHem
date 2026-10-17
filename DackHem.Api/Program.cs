using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using DackHem.Api.Data;
using DackHem.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.DataProtection;

namespace DackHem.Api
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddControllers();
            builder.Services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.AddPolicy("booking-write", context =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 10,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                            AutoReplenishment = true
                        }));
                options.AddPolicy("admin-read-write", context =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 20,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                            AutoReplenishment = true
                        }));
            });
            builder.Services.AddScoped<BookingEmailService>();
            builder.Services.AddSingleton<BookingEmailQueue>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<BookingEmailQueue>());
            builder.Services.AddOpenApi();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();

            // In production set Database:Path to a persistent storage volume.
            // Keep the existing local database location as the default.
            var databasePath = builder.Configuration["Database:Path"];
            if (string.IsNullOrWhiteSpace(databasePath)) databasePath = "dackhem.db";
            var databaseDirectory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
            if (!string.IsNullOrEmpty(databaseDirectory)) Directory.CreateDirectory(databaseDirectory);
            var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
            // Encryption keys must survive deployment alongside the SQLite file.
            var keyDirectory = Path.Combine(databaseDirectory!, ".dackhem-keys");
            Directory.CreateDirectory(keyDirectory);
            builder.Services.AddDataProtection().SetApplicationName("DackHem")
                .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
            builder.Services.AddDbContext<AppDbContext>(options =>
                options.UseSqlite(connectionString));

            var app = builder.Build();

            if (app.Environment.IsDevelopment())
            {
                app.MapOpenApi();
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.UseHttpsRedirection();
            app.UseDefaultFiles();
            app.UseStaticFiles();
            app.UseRouting();
            app.UseRateLimiter();
            app.UseAuthorization();
            app.MapControllers();

            // Separate work-tracking table: does not change existing booking schema.
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Database.ExecuteSqlRaw("""
                    CREATE TABLE IF NOT EXISTS BookingWork (
                        BookingId TEXT PRIMARY KEY NOT NULL,
                        WorkStatus TEXT NOT NULL DEFAULT 'Waiting',
                        AssignedTo TEXT NOT NULL DEFAULT '',
                        InternalNote TEXT NOT NULL DEFAULT ''
                    )
                    """);
            }

            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Database.ExecuteSqlRaw("""
                    CREATE TABLE IF NOT EXISTS BookingCancellationEvents (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        BookingId TEXT NOT NULL UNIQUE,
                        CancelledAtUtc TEXT NOT NULL
                    )
                    """);
            }

            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Database.ExecuteSqlRaw("""
                    CREATE TABLE IF NOT EXISTS BookingEmailOutbox (
                        BookingId TEXT PRIMARY KEY NOT NULL,
                        ProtectedToken TEXT NOT NULL,
                        CustomerSent INTEGER NOT NULL DEFAULT 0,
                        OwnerSent INTEGER NOT NULL DEFAULT 0,
                        Attempts INTEGER NOT NULL DEFAULT 0,
                        CreatedUtc TEXT NOT NULL,
                        NextAttemptUtc TEXT NOT NULL
                    )
                    """);
            }

            app.Run();
        }
    }
}
