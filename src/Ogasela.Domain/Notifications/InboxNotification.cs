namespace Ogasela.Domain.Notifications;

/// <summary>
/// An entry in a user's in-app notification inbox (the bell). Separate from <see cref="Notification"/>,
/// which records per-channel deliveries (push/SMS/email): this is what the user reads in the app,
/// with read/unread state. ListingId/ConversationId tell the app where tapping it should go.
/// Message notifications are grouped per conversation: while one is unread, further messages bump
/// it (Count, latest preview) instead of adding a new entry.
/// </summary>
public class InboxNotification
{
    private InboxNotification()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public string Type { get; private set; } = string.Empty;

    public string Title { get; private set; } = string.Empty;

    public string Body { get; private set; } = string.Empty;

    public Guid? ListingId { get; private set; }

    public Guid? ConversationId { get; private set; }

    /// <summary>Set for dispute notifications - tapping them opens that dispute.</summary>
    public Guid? DisputeId { get; private set; }

    /// <summary>Set for office pickup notifications - tapping them opens that request.</summary>
    public Guid? OfficePickupId { get; private set; }

    /// <summary>How many events this entry stands for (above 1 only for grouped chat messages).</summary>
    public int Count { get; private set; }

    public DateTime? ReadAt { get; private set; }

    public DateTime CreatedAt { get; private set; }

    /// <summary>When the entry last changed - the inbox is ordered by this, newest first.</summary>
    public DateTime UpdatedAt { get; private set; }

    public bool IsRead => ReadAt is not null;

    public static InboxNotification Create(
        Guid userId, string type, string title, string body, Guid? listingId, Guid? conversationId, DateTime now,
        Guid? disputeId = null, Guid? officePickupId = null) => new()
    {
        Id = Guid.NewGuid(),
        DisputeId = disputeId,
        OfficePickupId = officePickupId,
        UserId = userId,
        Type = type,
        Title = title,
        Body = body,
        ListingId = listingId,
        ConversationId = conversationId,
        Count = 1,
        CreatedAt = now,
        UpdatedAt = now
    };

    public void Bump(string title, string body, DateTime now)
    {
        Count++;
        Title = title;
        Body = body;
        ReadAt = null;
        UpdatedAt = now;
    }

    public void MarkRead(DateTime now)
    {
        ReadAt ??= now;
    }
}
