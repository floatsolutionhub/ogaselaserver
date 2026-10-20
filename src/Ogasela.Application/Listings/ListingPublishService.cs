using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ogasela.Application.Ai;
using Ogasela.Application.Ai.Interfaces;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Payments;
using Ogasela.Application.Verification.Interfaces;
using Ogasela.Domain.Listings;
using Ogasela.Domain.Moderation;
using Ogasela.Domain.Promotions;
using Ogasela.Domain.Verification;
using Ogasela.Shared;

namespace Ogasela.Application.Listings;

/// <summary>
/// The single place PRS Section 5's publish rules are enforced, in order. Both
/// <c>PublishListingCommandHandler</c> (first publish, from Draft) and
/// <c>RepostListingCommandHandler</c> (re-publish, from Expired/Sold/Paused) call this after
/// their own status precondition passes, so a repost can never skip a rule a fresh publish
/// would have to pass.
/// </summary>
public sealed class ListingPublishService
{
    private readonly IApplicationDbContext _dbContext;
    private readonly IVerificationGuard _verificationGuard;
    private readonly IPaymentAuthorizer _paymentAuthorizer;
    private readonly IFraudRiskScorer _fraudRiskScorer;
    private readonly IDateTime _dateTime;
    private readonly AiSettings _aiSettings;
    private readonly Settings.IPlatformSettings _platformSettings;
    private readonly ListingMediaCleanup _mediaCleanup;

    public ListingPublishService(
        IApplicationDbContext dbContext,
        IVerificationGuard verificationGuard,
        IPaymentAuthorizer paymentAuthorizer,
        IFraudRiskScorer fraudRiskScorer,
        IDateTime dateTime,
        IOptions<ListingSettings> settings,
        IOptions<AiSettings> aiSettings,
        ListingMediaCleanup mediaCleanup)
    {
        _mediaCleanup = mediaCleanup;
        _dbContext = dbContext;
        _verificationGuard = verificationGuard;
        _paymentAuthorizer = paymentAuthorizer;
        _fraudRiskScorer = fraudRiskScorer;
        _dateTime = dateTime;
        // SuperAdmin overrides from Control Portal → Settings, falling back to these appsettings values.
        _platformSettings = new Settings.PlatformSettingsService(dbContext, settings);
        _aiSettings = aiSettings.Value;
    }

    /// <summary>Publish/repost paid from the wallet: eligibility, then the wallet debit, then submission.</summary>
    public async Task<Result> PublishAsync(Listing listing, CancellationToken cancellationToken)
    {
        // RULE-02: a plan must be selected.
        if (listing.PromotionPlanId is not { } promotionPlanId)
        {
            return Result.Failure(ListingErrors.NoPlanSelected);
        }

        var plan = await _dbContext.PromotionPlans.FirstOrDefaultAsync(p => p.Id == promotionPlanId, cancellationToken);
        if (plan is null)
        {
            return Result.Failure(ListingErrors.PromotionPlanNotFound);
        }

        var eligibility = await CheckEligibilityAsync(listing, plan, cancellationToken);
        if (eligibility.IsFailure)
        {
            return eligibility;
        }

        if (!plan.IsFree)
        {
            // RULE-06: paid plans require authorized payment; never publish on failure.
            var paymentResult = await _paymentAuthorizer.AuthorizeAsync(listing.SellerId, plan.Id, listing.Id, cancellationToken);
            if (paymentResult.IsFailure)
            {
                return Result.Failure(ListingErrors.PaymentFailed);
            }
        }

        await SubmitAsync(listing, plan, cancellationToken);
        return Result.Success();
    }

    /// <summary>
    /// Every rule except payment (RULE-01, completeness, the plan's photo limit, RULE-04/05 for the
    /// Free plan). Checkout runs this before taking any money, so a seller is never charged for an
    /// ad that can't be submitted.
    /// </summary>
    public async Task<Result> CheckEligibilityAsync(Listing listing, PromotionPlan plan, CancellationToken cancellationToken)
    {
        // RULE-01: the seller must be fully verified.
        try
        {
            await _verificationGuard.RequireVerifiedSellerAsync(listing.SellerId, cancellationToken);
        }
        catch (SellerNotVerifiedException)
        {
            return Result.Failure(ListingErrors.NotVerified);
        }

        // Drafts may be partial; what goes to review must be complete.
        if (string.IsNullOrWhiteSpace(listing.Title)
            || string.IsNullOrWhiteSpace(listing.Description)
            || listing.MediaUrls.Count == 0)
        {
            return Result.Failure(ListingErrors.Incomplete);
        }

        // More photos than the plan allows isn't a failure: SubmitAsync keeps the first
        // PhotoLimit of them, so a seller can pick a smaller plan without re-editing the ad.

        if (plan.IsFree)
        {
            // RULE-04: the category must allow the Free plan.
            var category = await _dbContext.Categories.FirstOrDefaultAsync(c => c.Id == listing.CategoryId, cancellationToken);
            if (category is null)
            {
                return Result.Failure(ListingErrors.CategoryNotFound);
            }

            if (!category.IsFreeEligible)
            {
                return Result.Failure(ListingErrors.CategoryNotFreeEligible);
            }

            // RULE-05: cap on concurrently Active Free-plan listings.
            var activeFreeListings = await _dbContext.Listings.CountAsync(
                l => l.SellerId == listing.SellerId
                     && l.Id != listing.Id
                     && l.PromotionPlanId == plan.Id
                     && (l.Status == ListingStatus.Active
                         || l.Status == ListingStatus.ExpiringSoon
                         || l.Status == ListingStatus.PendingReview),
                cancellationToken);

            if (activeFreeListings >= await _platformSettings.FreePlanCapAsync(cancellationToken))
            {
                return Result.Failure(ListingErrors.FreePlanCapReached);
            }
        }

        return Result.Success();
    }

    /// <summary>
    /// Moves a listing whose plan is settled (free, or already paid) on: to moderator review when
    /// approval is required (the default), otherwise straight live. Drops any photos beyond the
    /// plan's PhotoLimit (keeping the first ones, so the cover stays) - done here rather than at
    /// checkout so an abandoned card payment never costs the seller photos. Runs the fraud-risk
    /// check. The caller saves, then calls <see cref="FlushDroppedMediaAsync"/> to delete the dropped
    /// photo files.
    /// </summary>
    public async Task SubmitAsync(Listing listing, PromotionPlan plan, CancellationToken cancellationToken)
    {
        var now = _dateTime.UtcNow;
        _mediaCleanup.Queue(listing.TrimMediaTo(plan.PhotoLimit));
        if (await _platformSettings.RequireListingApprovalAsync(cancellationToken))
        {
            // Goes live (and the plan's duration starts) only when a moderator approves it -
            // see ApproveListingCommandHandler. Payment is already taken; a rejection refunds it.
            listing.SubmitForReview(now);
        }
        else
        {
            listing.Publish(now, now.AddDays(plan.DurationDays));
        }

        // Fraud-risk scoring is a platform safety net, not a seller-facing tool - it runs for
        // every publish regardless of plan tier and never blocks the listing. Above the
        // threshold, it opens a Report for the moderation queue, and the score is shown next to
        // the listing in the pending-approval queue too.
        var assessment = await _fraudRiskScorer.ScoreAsync(listing, cancellationToken);
        if (assessment.RiskScore >= _aiSettings.FraudRiskThreshold)
        {
            _dbContext.Reports.Add(Report.Create(
                reporterId: null, ReportTargetType.Listing, listing.Id, assessment.ReasonSummary, now, assessment.RiskScore));
        }
    }

    /// <summary>
    /// A paid upgrade of an ad whose plan period is running: the new plan starts now (its full
    /// duration from today - the old expiry date no longer counts) and the ad keeps its status, so
    /// a live ad stays live. Photos beyond the new plan's limit are dropped like on submit; the
    /// caller saves, then calls <see cref="FlushDroppedMediaAsync"/>.
    /// </summary>
    public void ApplyPlanUpgrade(Listing listing, PromotionPlan plan)
    {
        _mediaCleanup.Queue(listing.TrimMediaTo(plan.PhotoLimit));
        listing.ChangePlanNow(plan.Id, plan.DurationDays, _dateTime.UtcNow);
    }

    /// <summary>Whether ads (and edits of live ads) go to a moderator before buyers see them - a platform setting.</summary>
    public Task<bool> RequiresApprovalAsync(CancellationToken cancellationToken) =>
        _platformSettings.RequireListingApprovalAsync(cancellationToken);

    /// <summary>Call after the save that persisted a <see cref="SubmitAsync"/>: deletes photo files it dropped.</summary>
    public Task FlushDroppedMediaAsync(CancellationToken cancellationToken) => _mediaCleanup.FlushAsync(cancellationToken);

    /// <summary>Call instead when that save didn't happen, so nothing still in use is deleted.</summary>
    public void DiscardDroppedMedia() => _mediaCleanup.Discard();
}
