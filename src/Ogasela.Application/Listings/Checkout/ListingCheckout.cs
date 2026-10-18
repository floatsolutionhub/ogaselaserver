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
/// </summary>
public sealed record StartListingCheckoutCommand(Guid ListingId, Guid PromotionPlanId, ListingPaymentMethod PaymentMethod)
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

    public ListingCheckoutHandlers(
        IApplicationDbContext dbContext,
        ICurrentUserService currentUser,
        ListingPublishService publishService,
        IPaymentAuthorizer paymentAuthorizer,
        IPaymentGatewayResolver gatewayResolver,
        ListingPaymentSettlement settlement,
        IDateTime dateTime)
    {
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
        var owned = await LoadOwnedListingAsync(request.ListingId, cancellationToken);
        if (owned.IsFailure)
        {
            return Result.Failure<ListingCheckoutResponse>(owned.Error);
        }

        var listing = owned.Value;
        if (listing.Status != ListingStatus.Draft)
        {
            return Result.Failure<ListingCheckoutResponse>(ListingErrors.InvalidStatusForPublish);
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
            listing.SellerId, plan.Price, $"Ogasela {plan.Name} plan for \"{listing.Title}\"", cancellationToken);
        if (init.IsFailure)
        {
            return Result.Failure<ListingCheckoutResponse>(PaymentErrors.GatewayError);
        }

        _dbContext.Transactions.Add(Transaction.CreatePending(
            listing.SellerId, TransactionType.PlanPurchase, plan.Price, init.Value.Reference, _dateTime.UtcNow, listing.Id));
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
            TransactionStatus.Success when listing.Status != ListingStatus.Draft => ListingCheckoutOutcome.Submitted,
            TransactionStatus.Pending => ListingCheckoutOutcome.PaymentPending,
            TransactionStatus.Cancelled => ListingCheckoutOutcome.PaymentCancelled,
            // Paid but couldn't be submitted (credited to the wallet instead), or failed/abandoned.
            _ => ListingCheckoutOutcome.PaymentFailed
        };

        return Result.Success(new ListingCheckoutResponse(outcome, ListingResponse.From(listing), reference));
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

    public ListingPaymentSettlement(
        IApplicationDbContext dbContext, ListingPublishService publishService, PlanChargeRefunder refunder,
        ILogger<ListingPaymentSettlement> logger)
    {
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
        if (listing is not { Status: ListingStatus.Draft } || listing.PromotionPlanId is not { } planId)
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

        await _publishService.SubmitAsync(listing, plan, cancellationToken);
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
