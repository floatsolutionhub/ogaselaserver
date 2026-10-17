namespace Ogasela.Domain.Payments;

public class Transaction
{
    private Transaction()
    {
    }

    public Guid Id { get; private set; }

    public Guid SellerId { get; private set; }

    public TransactionType Type { get; private set; }

    public decimal AmountKobo { get; private set; }

    /// <summary>
    /// The gateway's reference for this transaction. Null for a <see cref="TransactionType.PlanPurchase"/>,
    /// which is an internal wallet debit that never calls a gateway.
    /// </summary>
    public string? GatewayReference { get; private set; }

    public TransactionStatus Status { get; private set; }

    public Guid? RelatedListingId { get; private set; }

    public DateTime CreatedAt { get; private set; }

    /// <param name="relatedListingId">Set for a card payment of a listing's plan (a PlanPurchase through the gateway).</param>
    public static Transaction CreatePending(
        Guid sellerId, TransactionType type, decimal amountKobo, string gatewayReference, DateTime now,
        Guid? relatedListingId = null)
    {
        return new Transaction
        {
            RelatedListingId = relatedListingId,
            Id = Guid.NewGuid(),
            SellerId = sellerId,
            Type = type,
            AmountKobo = amountKobo,
            GatewayReference = gatewayReference,
            Status = TransactionStatus.Pending,
            CreatedAt = now
        };
    }

    public static Transaction CreateSucceeded(
        Guid sellerId, TransactionType type, decimal amountKobo, string? gatewayReference, Guid? relatedListingId, DateTime now)
    {
        return new Transaction
        {
            Id = Guid.NewGuid(),
            SellerId = sellerId,
            Type = type,
            AmountKobo = amountKobo,
            GatewayReference = gatewayReference,
            Status = TransactionStatus.Success,
            RelatedListingId = relatedListingId,
            CreatedAt = now
        };
    }

    public void MarkSuccess() => Status = TransactionStatus.Success;

    public void MarkFailed() => Status = TransactionStatus.Failed;

    public void MarkCancelled() => Status = TransactionStatus.Cancelled;

    /// <summary>Not yet settled - Pending, or Cancelled by the payer (a late success still settles it).</summary>
    public bool IsAwaitingSettlement => Status is TransactionStatus.Pending or TransactionStatus.Cancelled;

    public void MarkRefunded() => Status = TransactionStatus.Refunded;
}
