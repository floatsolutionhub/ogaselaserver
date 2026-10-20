using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Listings.GetListing;
using Ogasela.Application.Payments;
using Ogasela.Domain.Listings;
using Ogasela.Domain.Payments;
using Ogasela.Shared;

namespace Ogasela.Application.Listings.Checkout;

public enum ListingPaymentMethod
{
    /// <summary>Debit the seller's wallet - settled immediately.</summary>
    Wallet,

    /// <summary>Pay through the active gateway (Paystack/Flutterwave) - settled when the gateway confirms.</summary>
    Card
}

public enum ListingCheckoutOutcome
{
    /// <summary>Paid (or free) and submitted - under review, or live if approval is switched off.</summary>
    Submitted,

    /// <summary>Card payment started: open RedirectUrl, then confirm with the Reference. The listing is still a Draft.</summary>
    PaymentRequired,

    /// <summary>The card payment hasn't been confirmed by the gateway yet - confirm again shortly.</summary>
    PaymentPending,

    /// <summary>The gateway reports the card payment failed. The listing is still a Draft.</summary>
    PaymentFailed,

    /// <summary>The seller left the payment page without paying. Nothing was charged; the listing is still a Draft.</summary>
    PaymentCancelled
}

/// <summary>
/// Choose a plan for a Draft listing and pay for it. Every rule except payment is checked first,
/// so money is only taken for a listing that can actually be submitted. Free and Wallet settle in
/// this call; Card returns a gateway checkout and the listing stays a Draft until
/// <see cref="ConfirmListingPaymentCommand"/> or the payment webhook confirms it.
/// <paramref name="ReturnUrl"/> (optional) is where the gateway sends a card payer back to - the web
/// client passes its own return page; it must be on <see cref="IPaymentReturnUrlPolicy"/>'s list.
/// </summary>
public sealed record StartListingCheckoutCommand(
    Guid ListingId, Guid PromotionPlanId, ListingPaymentMethod PaymentMethod, string? ReturnUrl = null)
    : IRequest<Result<ListingCheckoutResponse>>;

/// <summary>
/// Called by the app when the seller returns from the gateway's checkout page. Asks the gateway
/// directly (webhooks can be late, and never reach a local dev API), then settles exactly like the
/// webhook would. Safe to call repeatedly.
/// </summary>
public sealed record ConfirmListingPaymentCommand(Guid ListingId, string Reference) : IRequest<Result<ListingCheckoutResponse>>;

/// <summary>
/// Called when the seller closes the payment page without returning through the success page. Asks
/// the gateway first: a payment that went through is settled as normal, one still in progress at the
/// bank stays pending, and only one that was never paid is recorded as Cancelled.
/// </summary>
public sealed record CancelListingPaymentCommand(Guid ListingId, string Reference) : IRequest<Result<ListingCheckoutResponse>>;

/// <summary>
/// Move an ad whose plan period is running (live, or in review/back in draft after an edit) onto a
/// higher plan. The seller pays the new plan's full price; once paid, the new plan's duration
/// starts counting from that moment and the old expiry date is dropped. A plan can never be
/// downgraded, here or while editing.
/// </summary>
public sealed record StartPlanUpgradeCommand(
    Guid ListingId, Guid PromotionPlanId, ListingPaymentMethod PaymentMethod = ListingPaymentMethod.Card, string? ReturnUrl = null)
    : IRequest<Result<ListingCheckoutResponse>>;

public sealed record ListingCheckoutResponse(
    ListingCheckoutOutcome Outcome,
    ListingResponse Listing,
    string? PaymentReference = null,
    string? RedirectUrl = null);

public sealed class StartListingCheckoutCommandValidator : AbstractValidator<StartListingCheckoutCommand>
{
    public StartListingCheckoutCommandValidator()
    {
        RuleFor(x => x.ListingId).NotEmpty();
        RuleFor(x => x.PromotionPlanId).NotEmpty();
        RuleFor(x => x.PaymentMethod).IsInEnum();
    }
}

public sealed class StartPlanUpgradeCommandValidator : AbstractValidator<StartPlanUpgradeCommand>
{
    public StartPlanUpgradeCommandValidator()
    {
        RuleFor(x => x.ListingId).NotEmpty();
        RuleFor(x => x.PromotionPlanId).NotEmpty();
        RuleFor(x => x.PaymentMethod).IsInEnum();
    }
}

public sealed class ConfirmListingPaymentCommandValidator : AbstractValidator<ConfirmListingPaymentCommand>
{
    public ConfirmListingPaymentCommandValidator()
    {
        RuleFor(x => x.ListingId).NotEmpty();
        RuleFor(x => x.Reference).NotEmpty().MaximumLength(200);
    }
}

public sealed class ListingCheckoutHandlers :
    IRequestHandler<StartListingCheckoutCommand, Result<ListingCheckoutResponse>>,
    IRequestHandler<StartPlanUpgradeCommand, Result<ListingCheckoutResponse>>,
    IRequestHandler<ConfirmListingPaymentCommand, Result<ListingCheckoutResponse>>,
    IRequestHandler<CancelListingPaymentCommand, Result<ListingCheckoutResponse>>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;
    private readonly ListingPublishService _publishService;
    private readonly IPaymentAuthorizer _paymentAuthorizer;
    private readonly IPaymentGatewayResolver _gatewayResolver;
    private readonly ListingPaymentSettlement _settlement;
    private readonly IDateTime _dateTime;
    private readonly IPaymentReturnUrlPolicy _returnUrlPolicy;

    public ListingCheckoutHandlers(
        IApplicationDbContext dbContext,
        ICurrentUserService currentUser,
        ListingPublishService publishService,
        IPaymentAuthorizer paymentAuthorizer,
        IPaymentGatewayResolver gatewayResolver,
        ListingPaymentSettlement settlement,
        IDateTime dateTime,
        IPaymentReturnUrlPolicy returnUrlPolicy)
    {
        _returnUrlPolicy = returnUrlPolicy;
        _dbContext = dbContext;
        _currentUser = currentUser;
        _publishService = publishService;
        _paymentAuthorizer = paymentAuthorizer;
        _gatewayResolver = gatewayResolver;
        _settlement = settlement;
        _dateTime = dateTime;
    }

    public async Task<Result<ListingCheckoutResponse>> Handle(StartListingCheckoutCommand request, CancellationToken cancellationToken)
    {
        if (request.ReturnUrl is not null && !_returnUrlPolicy.IsAllowed(request.ReturnUrl))
        {
            return Result.Failure<ListingCheckoutResponse>(PaymentErrors.ReturnUrlNotAllowed);
        }

        var owned = await LoadOwnedListingAsync(request.ListingId, cancellationToken);
        if (owned.IsFailure)
        {
            return Result.Failure<ListingCheckoutResponse>(owned.Error);
        }

        var listing = owned.Value;
        // A new ad (Draft) or a renewal of an expired one (Expired) - each buys a fresh plan period.
        if (listing.Status is not (ListingStatus.Draft or ListingStatus.Expired))
        {
            return Result.Failure<ListingCheckoutResponse>(ListingErrors.InvalidStatusForPublish);
        }

        // A rejected edit of a live ad is back in Draft with its plan still running: it's fixed and
        // resubmitted for free, never paid for again.
        if (listing.Status == ListingStatus.Draft && listing.HasRunningPlanPeriod(_dateTime.UtcNow))
        {
            return Result.Failure<ListingCheckoutResponse>(ListingErrors.ResubmitInstead);
        }

        var plan = await _dbContext.PromotionPlans.FirstOrDefaultAsync(p => p.Id == request.PromotionPlanId, cancellationToken);
        if (plan is null)
        {
            return Result.Failure<ListingCheckoutResponse>(ListingErrors.PromotionPlanNotFound);
        }

        if (!plan.IsActive)
        {
            return Result.Failure<ListingCheckoutResponse>(ListingErrors.PlanNotOnSale);
        }

        var eligibility = await _publishService.CheckEligibilityAsync(listing, plan, cancellationToken);
        if (eligibility.IsFailure)
        {
            return Result.Failure<ListingCheckoutResponse>(eligibility.Error);
        }

        listing.ChoosePlan(plan.Id, _dateTime.UtcNow);

        if (plan.IsFree || request.PaymentMethod == ListingPaymentMethod.Wallet)
        {
            if (!plan.IsFree)
            {
                var payment = await _paymentAuthorizer.AuthorizeAsync(listing.SellerId, plan.Id, listing.Id, cancellationToken);
                if (payment.IsFailure)
                {
                    return Result.Failure<ListingCheckoutResponse>(payment.Error);
                }
            }

            // The wallet debit, its PlanPurchase record and the status change land in one save.
            await _publishService.SubmitAsync(listing, plan, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await _publishService.FlushDroppedMediaAsync(cancellationToken);
            return Result.Success(new ListingCheckoutResponse(ListingCheckoutOutcome.Submitted, ListingResponse.From(listing)));
        }

        var gateway = _gatewayResolver.GetActive();
        var init = await gateway.InitializeTransactionAsync(
            listing.SellerId, plan.Price, $"Ogasela {plan.Name} plan for \"{listing.Title}\"", request.ReturnUrl, cancellationToken);
        if (init.IsFailure)
        {
            return Result.Failure<ListingCheckoutResponse>(PaymentErrors.GatewayError);
        }

        _dbContext.Transactions.Add(Transaction.CreatePending(
            listing.SellerId, TransactionType.PlanPurchase, plan.Price, init.Value.Reference, _dateTime.UtcNow, listing.Id, plan.Id));
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(new ListingCheckoutResponse(
            ListingCheckoutOutcome.PaymentRequired, ListingResponse.From(listing), init.Value.Reference, init.Value.RedirectUrl));
    }

    public async Task<Result<ListingCheckoutResponse>> Handle(StartPlanUpgradeCommand request, CancellationToken cancellationToken)
    {
        if (request.ReturnUrl is not null && !_returnUrlPolicy.IsAllowed(request.ReturnUrl))
        {
            return Result.Failure<ListingCheckoutResponse>(PaymentErrors.ReturnUrlNotAllowed);
        }

        var owned = await LoadOwnedListingAsync(request.ListingId, cancellationToken);
        if (owned.IsFailure)
        {
            return Result.Failure<ListingCheckoutResponse>(owned.Error);
        }

        var listing = owned.Value;
        var now = _dateTime.UtcNow;
        if (!ListingPaymentSettlement.IsUpgradable(listing, now) || listing.PromotionPlanId is not { } currentPlanId)
        {
            return Result.Failure<ListingCheckoutResponse>(ListingErrors.NotLive);
        }

        var plan = await _dbContext.PromotionPlans.FirstOrDefaultAsync(p => p.Id == request.PromotionPlanId, cancellationToken);
        if (plan is null)
        {
            return Result.Failure<ListingCheckoutResponse>(ListingErrors.PromotionPlanNotFound);
        }

        if (!plan.IsActive)
        {
            return Result.Failure<ListingCheckoutResponse>(ListingErrors.PlanNotOnSale);
        }

        if (plan.Id == currentPlanId)
        {
            return Result.Failure<ListingCheckoutResponse>(ListingErrors.SamePlan);
        }

        var current = await _dbContext.PromotionPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == currentPlanId, cancellationToken);
        if (plan.IsFree || (current is not null && plan.Price < current.Price))
        {
            return Result.Failure<ListingCheckoutResponse>(ListingErrors.PlanDowngradeNotAllowed);
        }

        if (request.PaymentMethod == ListingPaymentMethod.Wallet)
        {
            var payment = await _paymentAuthorizer.AuthorizeAsync(listing.SellerId, plan.Id, listing.Id, cancellationToken);
            if (payment.IsFailure)
            {
                return Result.Failure<ListingCheckoutResponse>(payment.Error);
            }

            // The wallet debit and the plan change land in one save.
            _publishService.ApplyPlanUpgrade(listing, plan);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await _publishService.FlushDroppedMediaAsync(cancellationToken);
            return Result.Success(new ListingCheckoutResponse(ListingCheckoutOutcome.Submitted, ListingResponse.From(listing)));
        }

        var gateway = _gatewayResolver.GetActive();
        var init = await gateway.InitializeTransactionAsync(
            listing.SellerId, plan.Price, $"Ogasela upgrade to the {plan.Name} plan for \"{listing.Title}\"", request.ReturnUrl, cancellationToken);
        if (init.IsFailure)
        {
            return Result.Failure<ListingCheckoutResponse>(PaymentErrors.GatewayError);
        }

        // The target plan rides on the payment: the ad's own plan only changes once it's paid.
        _dbContext.Transactions.Add(Transaction.CreatePending(
            listing.SellerId, TransactionType.PlanPurchase, plan.Price, init.Value.Reference, now, listing.Id, plan.Id));
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(new ListingCheckoutResponse(
            ListingCheckoutOutcome.PaymentRequired, ListingResponse.From(listing), init.Value.Reference, init.Value.RedirectUrl));
    }

    public Task<Result<ListingCheckoutResponse>> Handle(ConfirmListingPaymentCommand request, CancellationToken cancellationToken) =>
        VerifyAndRespondAsync(request.ListingId, request.Reference, cancelIfUnpaid: false, cancellationToken);

    public Task<Result<ListingCheckoutResponse>> Handle(CancelListingPaymentCommand request, CancellationToken cancellationToken) =>
        VerifyAndRespondAsync(request.ListingId, request.Reference, cancelIfUnpaid: true, cancellationToken);

    private async Task<Result<ListingCheckoutResponse>> VerifyAndRespondAsync(
        Guid listingId, string reference, bool cancelIfUnpaid, CancellationToken cancellationToken)
    {
        var owned = await LoadOwnedListingAsync(listingId, cancellationToken);
        if (owned.IsFailure)
        {
            return Result.Failure<ListingCheckoutResponse>(owned.Error);
        }

        var transaction = await _dbContext.Transactions.FirstOrDefaultAsync(
            t => t.GatewayReference == reference
                 && t.RelatedListingId == listingId
                 && t.Type == TransactionType.PlanPurchase,
            cancellationToken);
        if (transaction is null)
        {
            return Result.Failure<ListingCheckoutResponse>(ListingErrors.PaymentNotFound);
        }

        if (transaction.Status == TransactionStatus.Pending)
        {
            var verify = await _gatewayResolver.GetActive().VerifyTransactionAsync(reference, cancellationToken);
            if (verify.IsFailure)
            {
                return Result.Failure<ListingCheckoutResponse>(PaymentErrors.GatewayError);
            }

            if (cancelIfUnpaid && verify.Value.State == PaymentAttemptState.Abandoned)
            {
                transaction.MarkCancelled();
                try
                {
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateConcurrencyException)
                {
                    // The webhook settled it at the same moment - report whatever it decided.
                }
            }
            else if (verify.Value.State != PaymentAttemptState.Pending)
            {
                await _settlement.SettleAsync(transaction, verify.Value, cancellationToken);
            }
        }

        var listing = await _dbContext.Listings.AsNoTracking().FirstAsync(l => l.Id == listingId, cancellationToken);
        var outcome = transaction.Status switch
        {
            // Submitted (or, for an upgrade, moved onto the paid plan). A refunded payment leaves the
            // ad where it was - Draft/Expired, or on its old plan.
            TransactionStatus.Success when PaidFor(transaction, listing) => ListingCheckoutOutcome.Submitted,
            TransactionStatus.Pending => ListingCheckoutOutcome.PaymentPending,
            TransactionStatus.Cancelled => ListingCheckoutOutcome.PaymentCancelled,
            // Paid but couldn't be submitted (credited to the wallet instead), or failed/abandoned.
            _ => ListingCheckoutOutcome.PaymentFailed
        };

        return Result.Success(new ListingCheckoutResponse(outcome, ListingResponse.From(listing), reference));
    }

    private bool PaidFor(Transaction transaction, Listing listing)
    {
        // The ad must be on the plan this payment bought (older payments don't record it).
        if (transaction.PromotionPlanId is { } planId && listing.PromotionPlanId != planId)
        {
            return false;
        }

        // Submitted or live - or, for an upgrade of an ad whose edit was sent back, a Draft whose
        // plan period is running (a fresh Draft never has one).
        return listing.Status is ListingStatus.PendingReview or ListingStatus.Active or ListingStatus.ExpiringSoon
               || (listing.Status == ListingStatus.Draft && listing.HasRunningPlanPeriod(_dateTime.UtcNow));
    }

    private async Task<Result<Listing>> LoadOwnedListingAsync(Guid listingId, CancellationToken cancellationToken)
    {
        var listing = await _dbContext.Listings.FirstOrDefaultAsync(l => l.Id == listingId, cancellationToken);
        if (listing is null)
        {
            return Result.Failure<Listing>(ListingErrors.ListingNotFound);
        }

        var isOwner = await _dbContext.SellerProfiles
            .AnyAsync(s => s.Id == listing.SellerId && s.UserId == _currentUser.UserId!.Value, cancellationToken);
        return isOwner ? Result.Success(listing) : Result.Failure<Listing>(ListingErrors.NotOwner);
    }
}

/// <summary>
/// Settles a gateway-paid listing plan once the gateway's verify call has answered. Shared by the
/// payment webhook and <see cref="ConfirmListingPaymentCommand"/>, and idempotent: only a Pending
/// transaction is touched, inside a DB transaction guarded by the rows' concurrency tokens. Money
/// is never lost - if the payment succeeded but the listing can no longer be submitted (already
/// submitted by another payment, no longer eligible, underpaid), it's refunded to the payer's card
/// or account. If the gateway refuses that refund, the payment stays Success and an error is logged
/// so staff can refund it from the Control Portal.
/// </summary>
public sealed class ListingPaymentSettlement
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ListingPublishService _publishService;
    private readonly PlanChargeRefunder _refunder;
    private readonly ILogger<ListingPaymentSettlement> _logger;
    private readonly IDateTime _dateTime;

    public ListingPaymentSettlement(
        IApplicationDbContext dbContext, ListingPublishService publishService, PlanChargeRefunder refunder,
        ILogger<ListingPaymentSettlement> logger, IDateTime dateTime)
    {
        _dateTime = dateTime;
        _dbContext = dbContext;
        _publishService = publishService;
        _refunder = refunder;
        _logger = logger;
    }

    public async Task SettleAsync(Transaction transaction, PaymentVerificationResult verification, CancellationToken cancellationToken)
    {
        var saved = false;
        await _dbContext.ExecuteInTransactionAsync(async () =>
        {
            saved = false;
            if (!transaction.IsAwaitingSettlement)
            {
                return;
            }

            if (!verification.Successful)
            {
                // A payer-cancelled checkout stays Cancelled; only a live one is marked failed.
                if (transaction.Status == TransactionStatus.Cancelled)
                {
                    return;
                }

                transaction.MarkFailed();
                await SaveAsync(transaction, cancellationToken);
                return;
            }

            transaction.MarkSuccess();
            var submitted = await TrySubmitAsync(transaction, verification, cancellationToken);
            if (!submitted)
            {
                // Give the money back to the card/account it came from (the wallet is switched off).
                var refund = await _refunder.RefundAsync(transaction, cancellationToken);
                if (refund.IsFailure && refund.Error == PaymentErrors.RefundAlreadyInProgress)
                {
                    // A concurrent webhook/confirm settled the same payment first - nothing left to do.
                    return;
                }

                if (refund.IsFailure)
                {
                    _logger.LogError(
                        "Listing payment {Reference} couldn't submit its ad and the automatic refund failed; refund it from the Control Portal",
                        verification.Reference);
                }
            }

            saved = await SaveAsync(transaction, cancellationToken);
        }, cancellationToken);

        // Only once the trimmed ad is committed are the photos it dropped safe to delete.
        if (saved)
        {
            await _publishService.FlushDroppedMediaAsync(cancellationToken);
        }
        else
        {
            _publishService.DiscardDroppedMedia();
        }
    }

    private async Task<bool> TrySubmitAsync(Transaction transaction, PaymentVerificationResult verification, CancellationToken cancellationToken)
    {
        if (verification.AmountKobo < transaction.AmountKobo)
        {
            _logger.LogWarning("Listing payment {Reference} was underpaid; refunding it", verification.Reference);
            return false;
        }

        var listing = await _dbContext.Listings.FirstOrDefaultAsync(l => l.Id == transaction.RelatedListingId, cancellationToken);
        var now = _dateTime.UtcNow;

        // An upgrade: the payment names a plan other than the one the ad's running period is on.
        if (listing is not null
            && transaction.PromotionPlanId is { } targetPlanId
            && targetPlanId != listing.PromotionPlanId
            && IsUpgradable(listing, now))
        {
            return await TryUpgradeAsync(listing, targetPlanId, verification, cancellationToken);
        }

        // A new ad (Draft without a running period) or a renewal (Expired).
        var awaitingPayment = listing is not null
                              && (listing.Status == ListingStatus.Expired
                                  || (listing.Status == ListingStatus.Draft && !listing.HasRunningPlanPeriod(now)));
        if (!awaitingPayment || (transaction.PromotionPlanId ?? listing!.PromotionPlanId) is not { } planId)
        {
            _logger.LogInformation(
                "Listing payment {Reference} confirmed but the listing is no longer awaiting payment; refunding it",
                verification.Reference);
            return false;
        }

        var plan = await _dbContext.PromotionPlans.FirstOrDefaultAsync(p => p.Id == planId, cancellationToken);
        if (plan is null || (await _publishService.CheckEligibilityAsync(listing, plan, cancellationToken)).IsFailure)
        {
            _logger.LogWarning(
                "Listing payment {Reference} confirmed but the listing can't be submitted any more; refunding it",
                verification.Reference);
            return false;
        }

        // Submit on the plan this payment bought, even if the seller picked another one since.
        if (listing!.PromotionPlanId != plan.Id)
        {
            listing.ChoosePlan(plan.Id, now);
        }

        await _publishService.SubmitAsync(listing, plan, cancellationToken);
        return true;
    }

    /// <summary>
    /// Ads whose plan period is running and can move to a higher plan: live ones, and ones whose
    /// edit is in review or was sent back (their period keeps running meanwhile).
    /// </summary>
    public static bool IsUpgradable(Listing listing, DateTime now) =>
        listing.Status is ListingStatus.Active or ListingStatus.ExpiringSoon
        || (listing.Status is ListingStatus.PendingReview or ListingStatus.Draft && listing.HasRunningPlanPeriod(now));

    private async Task<bool> TryUpgradeAsync(
        Listing listing, Guid targetPlanId, PaymentVerificationResult verification, CancellationToken cancellationToken)
    {
        var plan = await _dbContext.PromotionPlans.FirstOrDefaultAsync(p => p.Id == targetPlanId, cancellationToken);
        var current = listing.PromotionPlanId is { } currentId
            ? await _dbContext.PromotionPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == currentId, cancellationToken)
            : null;
        if (plan is null || plan.IsFree || (current is not null && plan.Price < current.Price))
        {
            _logger.LogWarning(
                "Plan upgrade payment {Reference} confirmed but the upgrade is no longer valid; refunding it",
                verification.Reference);
            return false;
        }

        _publishService.ApplyPlanUpgrade(listing, plan);
        return true;
    }

    /// <summary>False when a concurrent webhook/confirm settled the same payment first.</summary>
    private async Task<bool> SaveAsync(Transaction transaction, CancellationToken cancellationToken)
    {
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            // A concurrent webhook/confirm settled the same payment first - nothing left to do.
            _logger.LogInformation("Concurrent settlement of listing payment {TransactionId}; skipping", transaction.Id);
            return false;
        }
    }
}
