
namespace DackHem.Api.Models
{
    public class Booking
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public string CustomerName { get; set; } = string.Empty;

        public string PhoneNumber { get; set; } = string.Empty;

        public string Email { get; set; } = string.Empty;

        public string RegistrationNumber { get; set; } = string.Empty;

        public string Address { get; set; } = string.Empty;

        public DateTime BookingDateTime { get; set; }

        public string? Notes { get; set; }

        public decimal Price { get; set; } = 350;

        public string Status { get; set; } = "Confirmed";
        public string CancellationTokenHash { get; set; } = string.Empty;

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
