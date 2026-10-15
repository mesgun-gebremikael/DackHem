
using DackHem.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace DackHem.Api.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options)
            : base(options)
        {
        }

        public DbSet<Booking> Bookings => Set<Booking>();
    }
}

