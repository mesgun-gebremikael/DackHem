using DackHem.Api.Data;
using DackHem.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace DackHem.Api
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddControllers();
            builder.Services.AddScoped<BookingEmailService>();
            builder.Services.AddOpenApi();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();

            builder.Services.AddDbContext<AppDbContext>(options =>
                options.UseSqlite("Data Source=dackhem.db"));

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

            app.Run();
        }
    }
}
