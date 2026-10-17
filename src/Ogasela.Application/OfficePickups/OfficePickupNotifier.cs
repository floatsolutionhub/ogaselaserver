using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Notifications.Inbox;
using Ogasela.Application.Notifications.Interfaces;
using Ogasela.Domain.Notifications;
using Ogasela.Domain.OfficePickups;

namespace Ogasela.Application.OfficePickups;

/// <summary>
/// Tells a buyer or seller about a step in their office pickup: an entry in their in-app inbox
/// (tapping it opens the request) plus a push notification with the same wording. These are
/// transactional - someone's money and item - so they aren't subject to the per-category
/// notification toggles. Never throws: a failed notification mustn't undo the step behind it.
/// </summary>
public sealed class OfficePickupNotifier
{
    private readonly IApplicationDbContext _dbContext;
    private readonly IEnumerable<INotificationChannel> _channels;
    private readonly IDateTime _dateTime;
    private readonly ILogger<OfficePickupNotifier> _logger;

    public OfficePickupNotifier(
        IApplicationDbContext dbContext, IEnumerable<INotificationChannel> channels, IDateTime dateTime,
        ILogger<OfficePickupNotifier> logger)
    {
        _dbContext = dbContext;
        _channels = channels;
        _dateTime = dateTime;
        _logger = logger;
    }

    public async Task NotifyAsync(Guid userId, OfficePickupRequest request, string title, string body, CancellationToken cancellationToken)
    {
        try
        {
            _dbContext.InboxNotifications.Add(InboxNotification.Create(
                userId, InboxNotificationTypes.OfficePickupUpdate, title, body, request.ListingId, null, _dateTime.UtcNow,
                officePickupId: request.Id));
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write an office pickup inbox notification for request {RequestId}", request.Id);
        }

        var push = _channels.FirstOrDefault(c => c.Channel == NotificationChannel.Push);
        if (push is null)
        {
            return;
        }

        try
        {
            var payload = JsonSerializer.Serialize(new { title, body, officePickupId = request.Id, request.ListingId });
            var record = Notification.CreatePending(userId, NotificationChannel.Push, InboxNotificationTypes.OfficePickupUpdate, payload, _dateTime.UtcNow);
            _dbContext.Notifications.Add(record);
            if (await push.SendAsync(userId, InboxNotificationTypes.OfficePickupUpdate, payload, cancellationToken))
            {
                record.MarkSent();
            }
            else
            {
                record.MarkFailed();
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push an office pickup notification for request {RequestId}", request.Id);
        }
    }
}
