using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
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
        if (string.IsNullOrWhiteSpace(sender) || (string.IsNullOrWhiteSpace(_configuration["Resend:ApiKey"]) &&
            (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))))
        {
            _logger.LogWarning("Booking confirmation email skipped: missing required email configuration.");
            return false;
        }

        var port = _configuration.GetValue<int?>("Email:SmtpPort") ?? 587;
        var baseUrl = _configuration["Email:PublicBaseUrl"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            _logger.LogWarning("Booking confirmation email skipped: Email:PublicBaseUrl is missing.");
            return false;
        }

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
            if (!string.IsNullOrWhiteSpace(_configuration["Resend:ApiKey"]))
                return await SendWithResendAsync(sender, booking.Email, "DäckHem – din bokningsbekräftelse", body);

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
            _logger.LogWarning("Booking confirmation email failed ({ErrorType}, {SmtpStatus}).", ex.GetType().Name, ex is SmtpException smtp ? smtp.StatusCode.ToString() : "NotSmtp");
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
        if (string.IsNullOrWhiteSpace(sender) || string.IsNullOrWhiteSpace(recipient) ||
            (string.IsNullOrWhiteSpace(_configuration["Resend:ApiKey"]) &&
            (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))))
        {
            _logger.LogWarning("Owner notification email skipped: missing required email configuration.");
            return false;
        }

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
            if (!string.IsNullOrWhiteSpace(_configuration["Resend:ApiKey"]))
                return await SendWithResendAsync(sender, recipient, cancelled ? "DäckHem – kundavbokning" : "DäckHem – ny bokning", body);

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
            _logger.LogWarning("Owner notification failed ({ErrorType}, {SmtpStatus}).", ex.GetType().Name, ex is SmtpException smtp ? smtp.StatusCode.ToString() : "NotSmtp");
            return false;
        }
    }

    public async Task<bool> SendRescheduleAsync(Booking booking, DateTime previousTime)
    {
        var host = _configuration["Email:SmtpHost"];
        var username = _configuration["Email:Username"];
        var password = _configuration["Email:Password"];
        var sender = _configuration["Email:From"];
        if (string.IsNullOrWhiteSpace(sender) || (string.IsNullOrWhiteSpace(_configuration["Resend:ApiKey"]) &&
            (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))))
        {
            _logger.LogWarning("Booking reschedule email skipped: missing required email configuration.");
            return false;
        }

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
            if (!string.IsNullOrWhiteSpace(_configuration["Resend:ApiKey"]))
                return await SendWithResendAsync(sender, booking.Email, "DäckHem – din bokning har fått en ny tid", body);

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
            _logger.LogWarning("Booking reschedule email failed ({ErrorType}, {SmtpStatus}).", ex.GetType().Name, ex is SmtpException smtp ? smtp.StatusCode.ToString() : "NotSmtp");
            return false;
        }
    }

    // HTTPS API avoids SMTP port restrictions on hosting platforms.
    // Do not log recipient addresses, message bodies, API keys or cancellation tokens.
    private static readonly HttpClient ResendClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    private async Task<bool> SendWithResendAsync(string sender, string recipient, string subject, string body)
    {
        var apiKey = _configuration["Resend:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey)) return false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                from = $"DäckHem <{sender}>",
                to = new[] { recipient },
                subject,
                text = body
            }), Encoding.UTF8, "application/json");

            using var response = await ResendClient.SendAsync(request);
            if (response.IsSuccessStatusCode) return true;
            _logger.LogWarning("Resend email delivery request failed (HTTP {StatusCode}).", (int)response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Resend email delivery request failed ({ErrorType}).", ex.GetType().Name);
            return false;
        }
    }
}
