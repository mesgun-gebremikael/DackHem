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

            Bokningsnummer: DH-{booking.Id.ToString("N")[..8].ToUpperInvariant()}
            Datum och tid: {booking.BookingDateTime:yyyy-MM-dd HH:mm} (svensk tid)
            Adress: {booking.Address}
            Registreringsnummer: {booking.RegistrationNumber}
            Pris för däckbyte: {booking.Price} kr

            Du kan avboka mer än 3 timmar före besöket via länken:
            {cancelUrl}

            Behöver du avboka senare än 3 timmar före besöket eller har du frågor? Ring oss på 070-789 23 40.\n\n            Länken är personlig. Dela den inte med andra.

            Hälsningar,
            DäckHem
            Telefon: 070-789 23 40
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


    public async Task<bool> SendOwnerNotificationAsync(Booking booking, bool cancelled)
    {
        var host = _configuration["Email:SmtpHost"];
        var username = _configuration["Email:Username"];
        var password = _configuration["Email:Password"];
        var sender = _configuration["Email:From"];
        var recipient = _configuration["Email:OwnerNotificationTo"] ?? username;
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(sender) ||
            string.IsNullOrWhiteSpace(recipient)) return false;

        var body = $"""
            {(cancelled ? "KUNDEN HAR AVBOKAT" : "NY BOKNING")} – DäckHem

            Kund: {booking.CustomerName}
            Telefonnummer: {booking.PhoneNumber}
            E-post: {booking.Email}
            Tid: {booking.BookingDateTime:yyyy-MM-dd HH:mm}
            Adress: {booking.Address}
            Registreringsnummer: {booking.RegistrationNumber}
            Bokningsnummer: DH-{booking.Id.ToString("N")[..8].ToUpperInvariant()}
            """;
        try
        {
            using var message = new MailMessage(new MailAddress(sender, "DäckHem"), new MailAddress(recipient))
            {
                Subject = cancelled ? "DäckHem – kundavbokning" : "DäckHem – ny bokning",
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
            _logger.LogWarning("Owner notification failed ({ErrorType}).", ex.GetType().Name);
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
            Bokningsnummer: DH-{booking.Id.ToString("N")[..8].ToUpperInvariant()}
            Adress: {booking.Address}
            Registreringsnummer: {booking.RegistrationNumber}
            Pris: {booking.Price} kr

            Din tidigare avbokningslänk gäller fortfarande. Du kan avboka via länken mer än 3 timmar före besöket.\n            Behöver du avboka senare eller har frågor? Ring oss på 070-789 23 40.

            Hälsningar,
            DäckHem
            Telefon: 070-789 23 40
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
