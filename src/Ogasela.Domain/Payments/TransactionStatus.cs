namespace Ogasela.Domain.Payments;

public enum TransactionStatus
{
    Pending,
    Success,
    Failed,
    Refunded,

    /// <summary>The payer left the gateway checkout without paying. Settled after all if the gateway later reports it paid.</summary>
    Cancelled
}
