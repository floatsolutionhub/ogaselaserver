using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Listings;
using Ogasela.Application.Listings.Events;
using Ogasela.Application.Payments;
using Ogasela.Domain.Listings;
using Ogasela.Domain.Payments;
using Ogasela.Shared;

namespace Ogasela.Application.Moderation.ListingReview;

/// <summary>Approve: the listing goes Active now, and its plan's duration starts from this moment (not from submission).</summary>
public sealed class ApproveListingCommandHandler : IRequestHandler<ApproveListingCommand, Result>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditLogger _auditLogger;
    private readonly IPublisher _publisher;
    private readonly IDateTime _dateTime;

    public ApproveListingCommandHandler(
        IApplicationDbContext dbContext, ICurrentUserService currentUser, IAuditLogger auditLogger,
        IPublisher publisher, IDateTime dateTime)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _auditLogger = auditLogger;
        _publisher = publisher;
        _dateTime = dateTime;
    }

    public async Task<Result> Handle(ApproveListingCommand request, CancellationToken cancellationToken)
    {
        var listing = await _dbContext.Listings.FirstOrDefaultAsync(l => l.Id == request.ListingId, cancellationToken);
        if (listing is null)
        {
            return Result.Failure(ListingErrors.ListingNotFound);
        }

        if (listing.Status != ListingStatus.PendingReview)
        {
            return Result.Failure(ModerationErrors.ListingNotPendingReview);
        }

        var durationDays = await _dbContext.PromotionPlans
            .Where(p => p.Id == listing.PromotionPlanId)
            .Select(p => (int?)p.DurationDays)
            .FirstOrDefaultAsync(cancellationToken);
        if (durationDays is null)
        {
            return Result.Failure(ListingErrors.PromotionPlanNotFound);
        }

        var now = _dateTime.UtcNow;
        listing.Publish(now, now.AddDays(durationDays.Value));

        await _auditLogger.LogAsync(
            _currentUser.UserId!.Value, "Listing.Approved", nameof(Listing), listing.Id,
            JsonSerializer.Serialize(new { Status = ListingStatus.PendingReview.ToString() }),
            JsonSerializer.Serialize(new { Status = listing.Status.ToString(), listing.ExpiresAt }),
            cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
        await _publisher.Publish(new ListingReviewedEvent(listing.Id, listing.SellerId, listing.Title, true, null), cancellationToken);

        return Result.Success();
    }
}

/// <summary>
/// Reject: back to Draft with the moderator's reason so the seller can fix and resubmit. A paid
/// plan was charged on submission, so that charge is refunded the way it was paid (see
/// <see cref="PlanChargeRefunder"/>) - to the seller's card/account for card payments. The refund
/// happens first: if the gateway refuses it, the ad isn't rejected and the moderator can retry.
/// </summary>
public sealed class RejectListingCommandHandler : IRequestHandler<RejectListingCommand, Result>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditLogger _auditLogger;
    private readonly IPublisher _publisher;
    private readonly IDateTime _dateTime;
    private readonly PlanChargeRefunder _refunder;

    public RejectListingCommandHandler(
        IApplicationDbContext dbContext, ICurrentUserService currentUser, IAuditLogger auditLogger,
        IPublisher publisher, IDateTime dateTime, PlanChargeRefunder refunder)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _auditLogger = auditLogger;
        _publisher = publisher;
        _dateTime = dateTime;
        _refunder = refunder;
    }

    public async Task<Result> Handle(RejectListingCommand request, CancellationToken cancellationToken)
    {
        var listing = await _dbContext.Listings.FirstOrDefaultAsync(l => l.Id == request.ListingId, cancellationToken);
        if (listing is null)
        {
            return Result.Failure(ListingErrors.ListingNotFound);
        }

        if (listing.Status != ListingStatus.PendingReview)
        {
            return Result.Failure(ModerationErrors.ListingNotPendingReview);
        }

        var now = _dateTime.UtcNow;
        var reason = request.Reason.Trim();

        // The most recent successful plan charge for this listing is the one this submission made.
        var charge = await _dbContext.Transactions
            .Where(t => t.RelatedListingId == listing.Id
                && t.Type == TransactionType.PlanPurchase
                && t.Status == TransactionStatus.Success
                && t.AmountKobo > 0)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        // Refund before touching the listing: a card refund saves as it claims the charge, and if
        // the gateway refuses, the ad must still be awaiting review so the moderator can retry.
        if (charge is not null)
        {
            var refund = await _refunder.RefundAsync(charge, cancellationToken);
            if (refund.IsFailure)
            {
                return refund;
            }
        }

        listing.RejectReview(reason, now);

        await _auditLogger.LogAsync(
            _currentUser.UserId!.Value, "Listing.Rejected", nameof(Listing), listing.Id,
            JsonSerializer.Serialize(new { Status = ListingStatus.PendingReview.ToString() }),
            JsonSerializer.Serialize(new { Status = listing.Status.ToString(), Reason = reason, RefundedKobo = charge?.AmountKobo }),
            cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
        await _publisher.Publish(new ListingReviewedEvent(listing.Id, listing.SellerId, listing.Title, false, reason), cancellationToken);

        return Result.Success();
    }
}
