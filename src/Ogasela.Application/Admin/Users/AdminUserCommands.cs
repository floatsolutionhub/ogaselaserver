using System.Text.Json;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Notifications.Inbox;
using Ogasela.Domain.Accounts;
using Ogasela.Domain.Listings;
using Ogasela.Domain.Notifications;
using Ogasela.Shared;

namespace Ogasela.Application.Admin.Users;

/// <summary>Blocks every sign-in route, ends the user's sessions and pauses their live ads (they repost once reactivated).</summary>
public sealed record SuspendUserCommand(Guid UserId, string Reason) : IRequest<Result>;

public sealed record ReactivateUserCommand(Guid UserId) : IRequest<Result>;

/// <summary>Revokes every refresh token: the user is signed out on all devices once their short-lived access token expires.</summary>
public sealed record RevokeUserSessionsCommand(Guid UserId) : IRequest<Result>;

/// <summary>Approve, or reject with a reason the user sees. Either way the user gets an inbox notification.</summary>
public sealed record ReviewUserDocumentCommand(Guid UserId, Guid DocumentId, bool Approve, string? Reason) : IRequest<Result>;

/// <summary>
/// Staff verify a person by hand after checking their face photo, ID card and utility bill (the interim
/// route until in-app biometric verification ships), or revoke it. Verified unlocks publishing ads - a
/// buyer who's verified becomes a seller. Revoking needs a reason the seller sees; their live ads stay up,
/// and their business verification goes with it.
/// </summary>
public sealed record SetSellerVerificationCommand(Guid UserId, bool Verified, string? Reason) : IRequest<Result>;

public sealed class SetSellerVerificationCommandValidator : AbstractValidator<SetSellerVerificationCommand>
{
    public SetSellerVerificationCommandValidator()
    {
        When(x => !x.Verified, () =>
            RuleFor(x => x.Reason).NotEmpty().WithMessage("Tell the seller why their verification was revoked.").MaximumLength(500));
    }
}

/// <summary>
/// Staff verify a seller's business once its registration document and proof of office address are
/// approved and the person is verified - a trust badge on their shop and ads - or revoke it with a reason.
/// </summary>
public sealed record SetBusinessVerificationCommand(Guid UserId, bool Verified, string? Reason) : IRequest<Result>;

public sealed class SetBusinessVerificationCommandValidator : AbstractValidator<SetBusinessVerificationCommand>
{
    public SetBusinessVerificationCommandValidator()
    {
        When(x => !x.Verified, () =>
            RuleFor(x => x.Reason).NotEmpty().WithMessage("Tell the seller why their business verification was revoked.").MaximumLength(500));
    }
}

public sealed class SuspendUserCommandValidator : AbstractValidator<SuspendUserCommand>
{
    public SuspendUserCommandValidator() =>
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Give a reason for the suspension.").MaximumLength(500);
}

public sealed class ReviewUserDocumentCommandValidator : AbstractValidator<ReviewUserDocumentCommand>
{
    public ReviewUserDocumentCommandValidator()
    {
        When(x => !x.Approve, () =>
            RuleFor(x => x.Reason).NotEmpty().WithMessage("Tell the user why the document was rejected.").MaximumLength(500));
    }
}

public sealed class AdminUserCommandHandlers :
    IRequestHandler<SuspendUserCommand, Result>,
    IRequestHandler<ReactivateUserCommand, Result>,
    IRequestHandler<RevokeUserSessionsCommand, Result>,
    IRequestHandler<ReviewUserDocumentCommand, Result>,
    IRequestHandler<SetSellerVerificationCommand, Result>,
    IRequestHandler<SetBusinessVerificationCommand, Result>
{
    private static readonly UserRole[] AppRoles = [UserRole.Buyer, UserRole.Seller];

    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditLogger _auditLogger;
    private readonly IDateTime _dateTime;
    private readonly IPublisher _publisher;

    public AdminUserCommandHandlers(
        IApplicationDbContext dbContext, ICurrentUserService currentUser, IAuditLogger auditLogger, IDateTime dateTime,
        IPublisher publisher)
    {
        _publisher = publisher;
        _dbContext = dbContext;
        _currentUser = currentUser;
        _auditLogger = auditLogger;
        _dateTime = dateTime;
    }

    private Task<User?> FindAppUserAsync(Guid userId, CancellationToken cancellationToken) =>
        _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId && AppRoles.Contains(u.Role), cancellationToken);

    private async Task<int> RevokeSessionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var tokens = await _dbContext.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var token in tokens)
        {
            token.Revoke(_dateTime.UtcNow);
        }

        return tokens.Count;
    }

    public async Task<Result> Handle(SuspendUserCommand request, CancellationToken cancellationToken)
    {
        var user = await FindAppUserAsync(request.UserId, cancellationToken);
        if (user is null)
        {
            return Result.Failure(AdminUserErrors.NotFound);
        }

        if (user.IsSuspended)
        {
            return Result.Failure(AdminUserErrors.AlreadySuspended);
        }

        var now = _dateTime.UtcNow;
        var reason = request.Reason.Trim();
        user.Suspend(reason, now);
        var sessionsRevoked = await RevokeSessionsAsync(user.Id, cancellationToken);

        var sellerId = await _dbContext.SellerProfiles.Where(s => s.UserId == user.Id).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(cancellationToken);
        var paused = 0;
        if (sellerId is { } id)
        {
            var live = await _dbContext.Listings
                .Where(l => l.SellerId == id && (l.Status == ListingStatus.Active || l.Status == ListingStatus.ExpiringSoon))
                .ToListAsync(cancellationToken);
            foreach (var listing in live)
            {
                listing.Pause(now);
            }

            paused = live.Count;
        }

        await _auditLogger.LogAsync(
            _currentUser.UserId!.Value, "User.Suspended", nameof(User), user.Id, null,
            JsonSerializer.Serialize(new { Reason = reason, SessionsRevoked = sessionsRevoked, AdsPaused = paused }),
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> Handle(ReactivateUserCommand request, CancellationToken cancellationToken)
    {
        var user = await FindAppUserAsync(request.UserId, cancellationToken);
        if (user is null)
        {
            return Result.Failure(AdminUserErrors.NotFound);
        }

        if (!user.IsSuspended)
        {
            return Result.Failure(AdminUserErrors.NotSuspended);
        }

        var previousReason = user.SuspensionReason;
        user.Reactivate();
        await _auditLogger.LogAsync(
            _currentUser.UserId!.Value, "User.Reactivated", nameof(User), user.Id,
            JsonSerializer.Serialize(new { Reason = previousReason }), null, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> Handle(RevokeUserSessionsCommand request, CancellationToken cancellationToken)
    {
        var user = await FindAppUserAsync(request.UserId, cancellationToken);
        if (user is null)
        {
            return Result.Failure(AdminUserErrors.NotFound);
        }

        var revoked = await RevokeSessionsAsync(user.Id, cancellationToken);
        await _auditLogger.LogAsync(
            _currentUser.UserId!.Value, "User.SessionsRevoked", nameof(User), user.Id, null,
            JsonSerializer.Serialize(new { Revoked = revoked }), cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> Handle(ReviewUserDocumentCommand request, CancellationToken cancellationToken)
    {
        var document = await _dbContext.UserDocuments.FirstOrDefaultAsync(
            d => d.Id == request.DocumentId && d.UserId == request.UserId, cancellationToken);
        if (document is null)
        {
            return Result.Failure(AdminUserErrors.DocumentNotFound);
        }

        if (document.Status != UserDocumentStatus.PendingReview)
        {
            return Result.Failure(AdminUserErrors.DocumentAlreadyReviewed);
        }

        var now = _dateTime.UtcNow;
        var label = document.Type switch
        {
            UserDocumentType.IdCard => "ID card",
            UserDocumentType.BusinessRegistration => "business registration document",
            UserDocumentType.OfficeUtilityBill => "office utility bill",
            _ => "utility bill"
        };
        var uploadScreen = document.Type is UserDocumentType.BusinessRegistration or UserDocumentType.OfficeUtilityBill
            ? "Business Info"
            : "Personal Info";
        if (request.Approve)
        {
            document.Approve(now);
            _dbContext.InboxNotifications.Add(InboxNotification.Create(
                document.UserId, InboxNotificationTypes.DocumentApproved, $"Your {label} was approved",
                $"Thanks - your {label} has been checked and approved.", null, null, now));
        }
        else
        {
            var reason = request.Reason!.Trim();
            document.Reject(reason, now);
            _dbContext.InboxNotifications.Add(InboxNotification.Create(
                document.UserId, InboxNotificationTypes.DocumentRejected, $"Your {label} wasn't accepted",
                $"{reason} Please upload it again from {uploadScreen}.", null, null, now));
        }

        await _auditLogger.LogAsync(
            _currentUser.UserId!.Value, request.Approve ? "UserDocument.Approved" : "UserDocument.Rejected",
            nameof(UserDocument), document.Id, null,
            JsonSerializer.Serialize(new { document.UserId, document.Type, Reason = request.Reason }), cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private Task<bool> HasApprovedDocumentAsync(Guid userId, UserDocumentType type, CancellationToken cancellationToken) =>
        _dbContext.UserDocuments.AnyAsync(
            d => d.UserId == userId && d.Type == type && d.Status == UserDocumentStatus.Approved, cancellationToken);

    public async Task<Result> Handle(SetSellerVerificationCommand request, CancellationToken cancellationToken)
    {
        var user = await FindAppUserAsync(request.UserId, cancellationToken);
        if (user is null)
        {
            return Result.Failure(AdminUserErrors.NotASeller);
        }

        var seller = await _dbContext.SellerProfiles.FirstOrDefaultAsync(s => s.UserId == request.UserId, cancellationToken);
        var before = seller?.VerificationStatus ?? VerificationStatus.NotStarted;
        if (request.Verified && before == VerificationStatus.Verified)
        {
            return Result.Failure(AdminUserErrors.AlreadyVerified);
        }

        if (!request.Verified && before != VerificationStatus.Verified)
        {
            return Result.Failure(AdminUserErrors.NotVerified);
        }

        // Staff verify against evidence, never blind: a face photo to compare with an approved ID card,
        // plus an approved proof of address.
        if (request.Verified)
        {
            var ready = user.ProfilePhotoS3Key is not null
                        && await HasApprovedDocumentAsync(user.Id, UserDocumentType.IdCard, cancellationToken)
                        && await HasApprovedDocumentAsync(user.Id, UserDocumentType.UtilityBill, cancellationToken);
            if (!ready)
            {
                return Result.Failure(AdminUserErrors.NotReadyToVerify);
            }

            // A verified buyer becomes a seller: they get a shop they can rename from Business Info.
            if (seller is null)
            {
                var name = user.Name?.Trim();
                seller = SellerProfile.Create(user.Id, string.IsNullOrEmpty(name) ? "My Shop" : name, rcNumber: null, nin: null);
                _dbContext.SellerProfiles.Add(seller);
                user.BecomeSeller();
            }
        }

        var now = _dateTime.UtcNow;
        var reason = request.Reason?.Trim();
        if (request.Verified)
        {
            seller!.UpdateVerificationStatus(VerificationStatus.Verified, now);
        }
        else
        {
            seller!.UpdateVerificationStatus(VerificationStatus.NotStarted, null);
            _dbContext.InboxNotifications.Add(InboxNotification.Create(
                seller.UserId, InboxNotificationTypes.Verification, "Your seller verification was revoked",
                $"{reason} Contact support if you think this is a mistake.", null, null, now));
        }

        await _auditLogger.LogAsync(
            _currentUser.UserId!.Value, request.Verified ? "Seller.VerifiedByStaff" : "Seller.VerificationRevoked",
            nameof(SellerProfile), seller.Id,
            JsonSerializer.Serialize(new { Status = before.ToString() }),
            JsonSerializer.Serialize(new { Status = seller.VerificationStatus.ToString(), Reason = reason }),
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Same event as an automatic biometric pass: the seller gets the usual "you're verified"
        // inbox, push and email notifications.
        if (request.Verified)
        {
            await _publisher.Publish(
                new Verification.Events.VerificationDecisionEvent(seller.Id, Domain.Verification.VerificationDecision.Verified, now),
                cancellationToken);
        }

        return Result.Success();
    }

    public async Task<Result> Handle(SetBusinessVerificationCommand request, CancellationToken cancellationToken)
    {
        var seller = await _dbContext.SellerProfiles.FirstOrDefaultAsync(s => s.UserId == request.UserId, cancellationToken);
        if (seller is null)
        {
            return Result.Failure(AdminUserErrors.NoSellerProfile);
        }

        if (request.Verified)
        {
            if (seller.IsBusinessVerified)
            {
                return Result.Failure(AdminUserErrors.BusinessAlreadyVerified);
            }

            if (seller.VerificationStatus != VerificationStatus.Verified)
            {
                return Result.Failure(AdminUserErrors.PersonNotVerified);
            }

            var ready = await HasApprovedDocumentAsync(seller.UserId, UserDocumentType.BusinessRegistration, cancellationToken)
                        && await HasApprovedDocumentAsync(seller.UserId, UserDocumentType.OfficeUtilityBill, cancellationToken);
            if (!ready)
            {
                return Result.Failure(AdminUserErrors.BusinessNotReadyToVerify);
            }
        }
        else if (!seller.IsBusinessVerified)
        {
            return Result.Failure(AdminUserErrors.BusinessNotVerified);
        }

        var now = _dateTime.UtcNow;
        var reason = request.Reason?.Trim();
        if (request.Verified)
        {
            seller.VerifyBusiness(now);
            _dbContext.InboxNotifications.Add(InboxNotification.Create(
                seller.UserId, InboxNotificationTypes.Verification, "Your business is verified",
                $"{seller.BusinessName} now shows a verified business badge to buyers.", null, null, now));
        }
        else
        {
            seller.RevokeBusinessVerification();
            _dbContext.InboxNotifications.Add(InboxNotification.Create(
                seller.UserId, InboxNotificationTypes.Verification, "Your business verification was revoked",
                $"{reason} Contact support if you think this is a mistake.", null, null, now));
        }

        await _auditLogger.LogAsync(
            _currentUser.UserId!.Value, request.Verified ? "Seller.BusinessVerifiedByStaff" : "Seller.BusinessVerificationRevoked",
            nameof(SellerProfile), seller.Id,
            null,
            JsonSerializer.Serialize(new { BusinessVerified = seller.IsBusinessVerified, Reason = reason }),
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
