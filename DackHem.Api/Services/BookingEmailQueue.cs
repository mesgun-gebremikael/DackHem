using System.Threading.Channels;
using DackHem.Api.Models;

namespace DackHem.Api.Services;

// In-memory email queue: fast confirmation, but pending emails can be lost on restart.
// Replace with a durable outbox before production launch.
public sealed class BookingEmailQueue : BackgroundService
{
    private readonly Channel<(Booking Booking, string Token)> _queue =
        Channel.CreateBounded<(Booking, string)>(new BoundedChannelOptions(500)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<BookingEmailQueue> _logger;

    public BookingEmailQueue(IServiceScopeFactory scopes, ILogger<BookingEmailQueue> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public bool TryEnqueue(Booking booking, string token) => _queue.Writer.TryWrite((booking, token));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var sender = scope.ServiceProvider.GetRequiredService<BookingEmailService>();
                await Task.WhenAll(
                    sender.SendConfirmationAsync(item.Booking, item.Token),
                    sender.SendOwnerNotificationAsync(item.Booking, cancelled: false));
            }
            catch (Exception ex)
            {
                _logger.LogError("Queued booking email processing failed ({ErrorType}).", ex.GetType().Name);
            }
        }
    }
}
