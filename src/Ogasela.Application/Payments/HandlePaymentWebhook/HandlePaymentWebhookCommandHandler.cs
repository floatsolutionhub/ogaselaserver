using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Listings.Checkout;
using Ogasela.Application.OfficePickups;
using Ogasela.Domain.Payments;
using Ogasela.Shared;

namespace Ogasela.Application.Payments.HandlePaymentWebhook;

/// <summary>
/// Webhooks are not guaranteed exactly-once, so every step here is written to be safely
/// re-run: an already-processed Transaction (Status != Pending) is a no-op, and the actual
/// credit happens inside <see cref="IApplicationDbContext.ExecuteInTransactionAsync"/> guarded
/// by the Transaction/Wallet rows' optimistic concurrency tokens, so two concurrent deliveries
/// can never both credit the wallet.
/// </summary>
public sealed class HandlePaymentWebhookCommandHandler : IRequestHandler<HandlePaymentWebhookCommand, Result>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly IDateTime _dateTime;
    private readonly IPaymentGatewayResolver _gatewayResolver;
    private readonly ILogger<HandlePaymentWebhookCommandHandler> _logger;
    private readonly ListingPaymentSettlement _listingSettlement;
    private readonly OfficePickupPaymentSettlement _officePickupSettlement;

    public HandlePaymentWebhookCommandHandler(
        IApplicationDbContext dbContext, IDateTime dateTime, IPaymentGatewayResolver gatewayResolver,
        ILogger<HandlePaymentWebhookCommandHandler> logger, ListingPaymentSettlement listingSettlement,
        OfficePickupPaymentSettlement officePickupSettlement)
    {
        _officePickupSettlement = officePickupSettlement;
        _listingSettlement = listingSettlement;
        _dbContext = dbContext;
        _dateTime = dateTime;
        _gatewayResolver = gatewayResolver;
        _logger = logger;
    }

    public async Task<Result> Handle(HandlePaymentWebhookCommand request, CancellationToken cancellationToken)
    {
        var gateway = _gatewayResolver.GetByProviderName(request.Provider);
        if (gateway is null)
        {
            _logger.LogWarning("Received a webhook for unknown payment provider {Provider}", request.Provider);
            return Result.Failure(PaymentErrors.UnknownProvider);
        }

        if (!gateway.VerifyWebhookSignature(request.RawBody, request.SignatureHeaderValue))
        {
            _logger.LogWarning("Rejected a {Provider} webhook with an invalid signature", request.Provider);
            return Result.Failure(PaymentErrors.InvalidWebhookSignature);
        }

        var reference = gateway.ExtractReferenceFromWebhookPayload(request.RawBody);
        if (string.IsNullOrWhiteSpace(reference))
        {
            _logger.LogWarning("A {Provider} webhook payload had no transaction reference", request.Provider);
            return Result.Failure(PaymentErrors.WebhookReferenceMissing);
        }

        var transaction = await _dbContext.Transactions
            .FirstOrDefaultAsync(t => t.GatewayReference == reference, cancellationToken);

        if (transaction is null)
        {
            // A buyer's office pickup payment isn't a wallet Transaction - it's tracked on the request itself.
            var pickup = await _dbContext.OfficePickupRequests.FirstOrDefaultAsync(r => r.PaymentReference == reference, cancellationToken);
            if (pickup is not null)
            {
                var pickupVerification = await gateway.VerifyTransactionAsync(reference, cancellationToken);
                if (pickupVerification.IsFailure)
                {
                    return Result.Failure(PaymentErrors.GatewayError);
                }

                await _officePickupSettlement.SettleAsync(pickup, pickupVerification.Value, cancellationToken);
                return Result.Success();
            }

            _logger.LogWarning(
                "A {Provider} webhook referenced an unknown transaction {Reference}", request.Provider, reference);
            return Result.Failure(PaymentErrors.TransactionNotFound);
        }

        if (!transaction.IsAwaitingSettlement)
        {
            // Already processed by an earlier delivery of this same webhook - a safe no-op.
            return Result.Success();
        }

        var verifyResult = await gateway.VerifyTransactionAsync(reference, cancellationToken);
        if (verifyResult.IsFailure)
        {
            return Result.Failure(PaymentErrors.GatewayError);
        }

        // A card payment for a listing's plan: settle it (submits the listing) instead of crediting the wallet.
        if (transaction.Type == TransactionType.PlanPurchase && transaction.RelatedListingId is not null)
        {
            await _listingSettlement.SettleAsync(transaction, verifyResult.Value, cancellationToken);
            return Result.Success();
        }

        if (!verifyResult.Value.Successful)
        {
            await _dbContext.ExecuteInTransactionAsync(async () =>
            {
                if (transaction.Status == TransactionStatus.Pending)
                {
                    transaction.MarkFailed();
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }
            }, cancellationToken);

            return Result.Success();
        }

        await _dbContext.ExecuteInTransactionAsync(async () =>
        {
            if (!transaction.IsAwaitingSettlement)
            {
                return;
            }

            var wallet = await _dbContext.GetOrCreateWalletAsync(transaction.SellerId, _dateTime, cancellationToken);
            wallet.Credit(transaction.AmountKobo, _dateTime.UtcNow);
            transaction.MarkSuccess();

            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another concurrent delivery of the same webhook already credited this
                // transaction between our read and our write - nothing left to do.
                _logger.LogInformation(
                    "Concurrent webhook delivery detected for transaction {Reference}; skipping duplicate credit", reference);
            }
        }, cancellationToken);

        return Result.Success();
    }
}
