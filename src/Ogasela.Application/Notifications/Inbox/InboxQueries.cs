using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Shared;

namespace Ogasela.Application.Notifications.Inbox;

public sealed record InboxNotificationResponse(
    Guid Id,
    string Type,
    string Title,
    string Body,
    Guid? ListingId,
    Guid? ConversationId,
    int Count,
    bool IsRead,
    DateTime UpdatedAt,
    Guid? DisputeId = null,
    Guid? OfficePickupId = null);

public sealed record InboxPageResponse(IReadOnlyList<InboxNotificationResponse> Items, int Page, int PageSize, int TotalCount, int UnreadCount);

/// <summary>The caller's inbox, newest activity first.</summary>
public sealed record GetInboxQuery(int Page, int PageSize) : IRequest<Result<InboxPageResponse>>;

/// <summary>Just the badge number for the bell.</summary>
public sealed record GetUnreadInboxCountQuery : IRequest<Result<int>>;

public sealed record MarkInboxNotificationReadCommand(Guid Id) : IRequest<Result>;

public sealed record MarkAllInboxNotificationsReadCommand : IRequest<Result>;

public sealed class InboxHandlers :
    IRequestHandler<GetInboxQuery, Result<InboxPageResponse>>,
    IRequestHandler<GetUnreadInboxCountQuery, Result<int>>,
    IRequestHandler<MarkInboxNotificationReadCommand, Result>,
    IRequestHandler<MarkAllInboxNotificationsReadCommand, Result>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IDateTime _dateTime;

    public InboxHandlers(IApplicationDbContext dbContext, ICurrentUserService currentUser, IDateTime dateTime)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _dateTime = dateTime;
    }

    private Guid UserId => _currentUser.UserId!.Value;

    public async Task<Result<InboxPageResponse>> Handle(GetInboxQuery request, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 50);
        var mine = _dbContext.InboxNotifications.Where(n => n.UserId == UserId);

        var total = await mine.CountAsync(cancellationToken);
        var unread = await mine.CountAsync(n => n.ReadAt == null, cancellationToken);
        var items = await mine
            .OrderByDescending(n => n.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(n => new InboxNotificationResponse(
                n.Id, n.Type, n.Title, n.Body, n.ListingId, n.ConversationId, n.Count, n.ReadAt != null, n.UpdatedAt, n.DisputeId, n.OfficePickupId))
            .ToListAsync(cancellationToken);

        return Result.Success(new InboxPageResponse(items, page, pageSize, total, unread));
    }

    public async Task<Result<int>> Handle(GetUnreadInboxCountQuery request, CancellationToken cancellationToken) =>
        Result.Success(await _dbContext.InboxNotifications.CountAsync(n => n.UserId == UserId && n.ReadAt == null, cancellationToken));

    public async Task<Result> Handle(MarkInboxNotificationReadCommand request, CancellationToken cancellationToken)
    {
        var notification = await _dbContext.InboxNotifications
            .FirstOrDefaultAsync(n => n.Id == request.Id && n.UserId == UserId, cancellationToken);
        if (notification is null)
        {
            return Result.Failure(InboxErrors.NotFound);
        }

        notification.MarkRead(_dateTime.UtcNow);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> Handle(MarkAllInboxNotificationsReadCommand request, CancellationToken cancellationToken)
    {
        var unread = await _dbContext.InboxNotifications
            .Where(n => n.UserId == UserId && n.ReadAt == null)
            .ToListAsync(cancellationToken);
        var now = _dateTime.UtcNow;
        foreach (var notification in unread)
        {
            notification.MarkRead(now);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

public static class InboxErrors
{
    public static readonly Error NotFound = new("Inbox.NotFound", "That notification could not be found.");
}
