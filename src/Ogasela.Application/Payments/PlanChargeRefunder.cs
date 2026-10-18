using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Domain.Payments;
using Ogasela.Shared;

namespace Ogasela.Application.Payments;

/// <summary>
/// Gives an ad-plan charge (a successful PlanPurchase) back the way it was paid. Used when an ad is
/// rejected in review, when a confirmed card payment can't submit its ad any more, and by the
/// Control Portal's refund action.
/// <list type="bullet">
/// <item>Card/transfer/USSD (has a GatewayReference): refunded in full through the payment gateway
/// to the payer's original method. The wallet is switched off in the app, so it's never used for
/// these. The charge is claimed first - marked Refunded and saved under its xmin token - so two
/// callers can't refund it twice; if the gateway refuses, it's put back to Success and the caller
/// gets <see cref="PaymentErrors.RefundFailed"/>. This path saves the context, so call it before
/// making any other changes you don't want saved yet.</item>
/// <item>Wallet (no GatewayReference - only charges from before the wallet was switched off):
/// credited back to the wallet the money came from. Nothing is saved; the caller's save does it.</item>
/// </list>
/// Either way a Refund transaction is added for the caller's next save.
/// </summary>
public sealed class PlanChargeRefunder
{
    private readonly IApplicationDbContext _dbContext;
    private readonly IPaymentGatewayResolver _gatewayResolver;
    private readonly IDateTime _dateTime;
    private readonly ILogger<PlanChargeRefunder> _logger;

    public PlanChargeRefunder(
        IApplicationDbContext dbContext, IPaymentGatewayResolver gatewayResolver, IDateTime dateTime,
        ILogger<PlanChargeRefunder> logger)
    {
        _dbContext = dbContext;
        _gatewayResolver = gatewayResolver;
        _dateTime = dateTime;
        _logger = logger;
    }

    public async Task<Result> RefundAsync(Transaction charge, CancellationToken cancellationToken)
    {
        if (charge.Type != TransactionType.PlanPurchase || charge.Status != TransactionStatus.Success)
        {
            return Result.Failure(PaymentErrors.TransactionNotRefundable);
        }

        var now = _dateTime.UtcNow;
        if (charge.GatewayReference is not { } reference)
        {
            var wallet = await _dbContext.GetOrCreateWalletAsync(charge.SellerId, _dateTime, cancellationToken);
            wallet.Credit(charge.AmountKobo, now);
            charge.MarkRefunded();
            AddRefundRecord(charge, now);
            return Result.Success();
        }

        charge.MarkRefunded();
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure(PaymentErrors.RefundAlreadyInProgress);
        }

        var refund = await _gatewayResolver.GetActive().ProcessRefundAsync(reference, cancellationToken);
        if (refund.IsFailure)
        {
            _logger.LogError("Gateway refused the refund for plan payment {Reference}; it needs refunding by hand", reference);
            charge.MarkSuccess();
            await _dbContext.SaveChangesAsync(cancellationToken);
            return Result.Failure(PaymentErrors.RefundFailed);
        }

        AddRefundRecord(charge, now);
        return Result.Success();
    }

    private void AddRefundRecord(Transaction charge, DateTime now) =>
        _dbContext.Transactions.Add(Transaction.CreateSucceeded(
            charge.SellerId, TransactionType.Refund, charge.AmountKobo, null, charge.RelatedListingId, now));
}
