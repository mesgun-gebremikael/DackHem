using System.Net;
using System.Net.Mail;
using DackHem.Api.Models;

namespace DackHem.Api.Services;

public sealed class BookingEmailService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<BookingEmailService> _logger;

    public BookingEmailService(IConfiguration configuration, ILogger<BookingEmailService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<bool> SendConfirmationAsync(Booking booking, string cancellationToken)
    {
        var host = _configuration["Email:SmtpHost"];
        var username = _configuration["Email:Username"];
        var password = _configuration["Email:Password"];
        var sender = _configuration["Email:From"];
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(sender))
            return false;

        var port = _configuration.GetValue<int?>("Email:SmtpPort") ?? 587;
        var baseUrl = _configuration["Email:PublicBaseUrl"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl)) return false;

        var cancelUrl = $"{baseUrl}/cancel.html#token={Uri.EscapeDataString(cancellationToken)}";
        var body = $"""
            Hej {booking.CustomerName}!

            Din bokning hos DäckHem är registrerad.

            Bokningsnummer: {booking.Id}
            Datum och tid: {booking.BookingDateTime:yyyy-MM-dd HH:mm} (svensk tid)
            Adress: {booking.Address}
            Registreringsnummer: {booking.RegistrationNumber}
            Pris för däckbyte: {booking.Price} kr

            Du kan avboka mer än 7 timmar före besöket via länken:
            {cancelUrl}

            Länken är personlig. Dela den inte med andra.

            Hälsningar,
            DäckHem
            """;

        try
        {
            using var message = new MailMessage(new MailAddress(sender, "DäckHem"), new MailAddress(booking.Email))
            {
                Subject = "DäckHem – din bokningsbekräftelse",
                Body = body,
                IsBodyHtml = false
            };
            using var client = new SmtpClient(host, port)
            {
                EnableSsl = true,
                Credentials = new NetworkCredential(username, password),
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = 15000
            };
            await client.SendMailAsync(message);
            return true;
        }
        catch (Exception ex)
        {
            // Never log message body, cancellation token, credentials, or customer details.
            _logger.LogWarning("Booking confirmation email failed ({ErrorType}).", ex.GetType().Name);
            return false;
        }
    }

    public async Task<bool> SendRescheduleAsync(Booking booking, DateTime previousTime)
    {
        var host = _configuration["Email:SmtpHost"];
        var username = _configuration["Email:Username"];
        var password = _configuration["Email:Password"];
        var sender = _configuration["Email:From"];
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(sender))
            return false;

        var body = $"""
            Hej {booking.CustomerName}!

            Din bokning hos DäckHem har fått en ny tid efter överenskommelse.

            Tidigare tid: {previousTime:yyyy-MM-dd HH:mm} (svensk tid)
            Ny tid: {booking.BookingDateTime:yyyy-MM-dd HH:mm} (svensk tid)
            Bokningsnummer: {booking.Id}
            Adress: {booking.Address}
            Registreringsnummer: {booking.RegistrationNumber}
            Pris: {booking.Price} kr

            Din tidigare avbokningslänk gäller fortfarande. Avbokning måste ske mer än 7 timmar före besöket.

            Har du frågor eller stämmer inte tiden? Kontakta DäckHem.

            Hälsningar,
            DäckHem
            """;

        try
        {
            using var message = new MailMessage(new MailAddress(sender, "DäckHem"), new MailAddress(booking.Email))
            {
                Subject = "DäckHem – din bokning har fått en ny tid",
                Body = body,
                IsBodyHtml = false
            };
            using var client = new SmtpClient(host, _configuration.GetValue<int?>("Email:SmtpPort") ?? 587)
            {
                EnableSsl = true,
                Credentials = new NetworkCredential(username, password),
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = 15000
            };
            await client.SendMailAsync(message);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Booking reschedule email failed ({ErrorType}).", ex.GetType().Name);
            return false;
        }
    }
}
