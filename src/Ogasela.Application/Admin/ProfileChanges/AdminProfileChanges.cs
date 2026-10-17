using System.Text.Json;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Accounts.ProfileChanges;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Notifications.Inbox;
using Ogasela.Domain.Accounts;
using Ogasela.Domain.Notifications;
using Ogasela.Shared;

namespace Ogasela.Application.Admin.ProfileChanges;

/// <summary>Phone/email/business-info changes awaiting (or past) staff review, newest first. Status null = all.</summary>
public sealed record GetAdminProfileChangesQuery(ProfileChangeStatus? Status, int Page, int PageSize)
    : IRequest<Result<AdminProfileChangePage>>;

/// <summary>Approve (applies the change) or reject with a reason the user sees. Either way the user gets an inbox notification.</summary>
public sealed record ReviewProfileChangeCommand(Guid Id, bool Approve, string? Reason) : IRequest<Result>;

/// <summary>Current* fields are the user's details today, so staff can compare old and new side by side.</summary>
public sealed record AdminProfileChangeItem(
    Guid Id,
    Guid UserId,
    string? UserName,
    UserRole UserRole,
    ProfileChangeType Type,
    string? CurrentPhone,
    string? CurrentEmail,
    string? CurrentBusinessName,
    string? CurrentStoreAddress,
    string? NewValue,
    string? BusinessName,
    string? StoreAddress,
    ProfileChangeStatus Status,
    string? ReviewNote,
    DateTime CreatedAt,
    DateTime? ReviewedAt);

public sealed record AdminProfileChangePage(IReadOnlyList<AdminProfileChangeItem> Items, int Page, int PageSize, int TotalCount);

public sealed class ReviewProfileChangeCommandValidator : AbstractValidator<ReviewProfileChangeCommand>
{
    public ReviewProfileChangeCommandValidator()
    {
        When(x => !x.Approve, () =>
            RuleFor(x => x.Reason).NotEmpty().WithMessage("Tell the user why the change was rejected.").MaximumLength(500));
    }
}

public static class AdminProfileChangeErrors
{
    public static readonly Error NowInUse = new(
        "ProfileChange.InUse", "Another account now uses this phone number or email, so it can't be approved. Reject it instead.");
}

public sealed class AdminProfileChangeHandlers :
    IRequestHandler<GetAdminProfileChangesQuery, Result<AdminProfileChangePage>>,
    IRequestHandler<ReviewProfileChangeCommand, Result>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditLogger _auditLogger;
    private readonly IDateTime _dateTime;

    public AdminProfileChangeHandlers(
        IApplicationDbContext dbContext, ICurrentUserService currentUser, IAuditLogger auditLogger, IDateTime dateTime)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _auditLogger = auditLogger;
        _dateTime = dateTime;
    }

    public async Task<Result<AdminProfileChangePage>> Handle(GetAdminProfileChangesQuery request, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);

        var changes = _dbContext.ProfileChangeRequests.AsQueryable();
        if (request.Status is { } status)
        {
            changes = changes.Where(r => r.Status == status);
        }

        var total = await changes.CountAsync(cancellationToken);
        var items = await (
            from r in changes
            join u in _dbContext.Users on r.UserId equals u.Id
            join s in _dbContext.SellerProfiles on u.Id equals s.UserId into sellers
            from s in sellers.DefaultIfEmpty()
            orderby r.CreatedAt descending, r.Id descending
            select new AdminProfileChangeItem(
                r.Id, u.Id, u.Name, u.Role, r.Type,
                u.Phone, u.Email,
                s == null ? null : s.BusinessName,
                s == null ? null : s.StoreAddress,
                r.NewValue, r.BusinessName, r.StoreAddress, r.Status, r.ReviewNote, r.CreatedAt, r.ReviewedAt))
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return Result.Success(new AdminProfileChangePage(items, page, pageSize, total));
    }

    public async Task<Result> Handle(ReviewProfileChangeCommand request, CancellationToken cancellationToken)
    {
        var change = await _dbContext.ProfileChangeRequests.FirstOrDefaultAsync(r => r.Id == request.Id, cancellationToken);
        if (change is null)
        {
            return Result.Failure(ProfileChangeErrors.NotFound);
        }

        if (change.Status != ProfileChangeStatus.PendingReview)
        {
            return Result.Failure(ProfileChangeErrors.NotPending);
        }

        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == change.UserId, cancellationToken);
        if (user is null)
        {
            return Result.Failure(ProfileChangeErrors.NotFound);
        }

        var reviewerId = _currentUser.UserId!.Value;
        var now = _dateTime.UtcNow;
        var label = change.Type switch
        {
            ProfileChangeType.Phone => "phone number",
            ProfileChangeType.Email => "email address",
            _ => "business info"
        };
        var before = JsonSerializer.Serialize(new { user.Phone, user.Email });

        if (request.Approve)
        {
            if (change.Type is ProfileChangeType.Phone or ProfileChangeType.Email)
            {
                // Checked again here: another account may have taken it while this waited for review.
                if (await ContactValue.InUseByAnotherAsync(_dbContext, user.Id, change.Type, change.NewValue!, cancellationToken))
                {
                    return Result.Failure(AdminProfileChangeErrors.NowInUse);
                }

                // The user proved they own the new value with a code before filing the change.
                user.UpdateContactDetails(
                    change.Type == ProfileChangeType.Phone ? change.NewValue : null,
                    change.Type == ProfileChangeType.Email ? change.NewValue : null,
                    verifiedAt: _dateTime.UtcNow);
            }
            else
            {
                var seller = await _dbContext.SellerProfiles.FirstOrDefaultAsync(s => s.UserId == user.Id, cancellationToken);
                if (seller is null)
                {
                    return Result.Failure(ProfileChangeErrors.SellersOnly);
                }

                before = JsonSerializer.Serialize(new { seller.BusinessName, seller.StoreAddress });
                seller.UpdateBusinessName(change.BusinessName!);
                seller.UpdateStoreAddress(change.StoreAddress);
            }

            change.Approve(reviewerId, now);
            _dbContext.InboxNotifications.Add(InboxNotification.Create(
                user.Id, InboxNotificationTypes.ProfileChangeApproved, $"Your new {label} was approved",
                $"Your {label} has been updated.", null, null, now));
        }
        else
        {
            var reason = request.Reason!.Trim();
            change.Reject(reviewerId, reason, now);
            _dbContext.InboxNotifications.Add(InboxNotification.Create(
                user.Id, InboxNotificationTypes.ProfileChangeRejected, $"Your {label} change wasn't approved",
                $"{reason} You can submit it again from Settings.", null, null, now));
        }

        await _auditLogger.LogAsync(
            reviewerId, request.Approve ? "ProfileChange.Approved" : "ProfileChange.Rejected",
            nameof(ProfileChangeRequest), change.Id, before,
            JsonSerializer.Serialize(new
            {
                change.UserId, change.Type, change.NewValue, change.BusinessName, change.StoreAddress, Reason = request.Reason
            }),
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
