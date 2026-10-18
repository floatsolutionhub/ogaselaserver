using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Payments.GetTransactions;
using Ogasela.Domain.Payments;
using Ogasela.Shared;

namespace Ogasela.Application.Payments.RefundTransaction;

/// <summary>
/// A PlanPurchase is refunded the way it was paid, by <see cref="PlanChargeRefunder"/>: card
/// payments back through the gateway, wallet payments back to the wallet. An AdSpendTopUp (money that came from the gateway) is
/// refunded through the gateway first, then the wallet credit it originally produced is
/// reversed. Either way, both the wallet balance and the transaction's Refunded status change
/// atomically together.
/// </summary>
public sealed class RefundTransactionCommandHandler : IRequestHandler<RefundTransactionCommand, Result<TransactionResponse>>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly IDateTime _dateTime;
    private readonly IPaymentGatewayResolver _gatewayResolver;
    private readonly PlanChargeRefunder _planRefunder;

    public RefundTransactionCommandHandler(
        IApplicationDbContext dbContext, IDateTime dateTime, IPaymentGatewayResolver gatewayResolver,
        PlanChargeRefunder planRefunder)
    {
        _dbContext = dbContext;
        _dateTime = dateTime;
        _gatewayResolver = gatewayResolver;
        _planRefunder = planRefunder;
    }

    public async Task<Result<TransactionResponse>> Handle(RefundTransactionCommand request, CancellationToken cancellationToken)
    {
        var transaction = await _dbContext.Transactions
            .FirstOrDefaultAsync(t => t.Id == request.TransactionId, cancellationToken);

        if (transaction is null)
        {
            return Result.Failure<TransactionResponse>(PaymentErrors.TransactionNotFound);
        }

        if (transaction.Status != TransactionStatus.Success || transaction.Type == TransactionType.Refund)
        {
            return Result.Failure<TransactionResponse>(PaymentErrors.TransactionNotRefundable);
        }

        if (transaction.Type == TransactionType.PlanPurchase)
        {
            var planRefund = await _planRefunder.RefundAsync(transaction, cancellationToken);
            if (planRefund.IsFailure)
            {
                return Result.Failure<TransactionResponse>(planRefund.Error);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            return Result.Success(new TransactionResponse(
                transaction.Id, transaction.Type, transaction.AmountKobo, transaction.GatewayReference,
                transaction.Status, transaction.RelatedListingId, transaction.CreatedAt));
        }

        if (transaction.Type == TransactionType.AdSpendTopUp)
        {
            var refundResult = await _gatewayResolver.GetActive()
                .ProcessRefundAsync(transaction.GatewayReference!, cancellationToken);

            if (refundResult.IsFailure)
            {
                return Result.Failure<TransactionResponse>(PaymentErrors.GatewayError);
            }
        }

        Error? failure = null;

        await _dbContext.ExecuteInTransactionAsync(async () =>
        {
            var wallet = await _dbContext.GetOrCreateWalletAsync(transaction.SellerId, _dateTime, cancellationToken);

            if (transaction.Type == TransactionType.AdSpendTopUp)
            {
                // Reversing money that came in - the wallet must still have it to give back.
                if (!wallet.HasSufficientBalance(transaction.AmountKobo))
                {
                    failure = PaymentErrors.RefundExceedsWalletBalance;
                    return;
                }

                wallet.Debit(transaction.AmountKobo, _dateTime.UtcNow);
            }
            else
            {
                // PlanPurchase: reverse the original internal debit.
                wallet.Credit(transaction.AmountKobo, _dateTime.UtcNow);
            }

            transaction.MarkRefunded();

            var refundEntry = Transaction.CreateSucceeded(
                transaction.SellerId, TransactionType.Refund, transaction.AmountKobo, null,
                transaction.RelatedListingId, _dateTime.UtcNow);
            _dbContext.Transactions.Add(refundEntry);

            await _dbContext.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

        if (failure is not null)
        {
            return Result.Failure<TransactionResponse>(failure);
        }

        return Result.Success(new TransactionResponse(
            transaction.Id, transaction.Type, transaction.AmountKobo, transaction.GatewayReference,
            transaction.Status, transaction.RelatedListingId, transaction.CreatedAt));
    }
}
