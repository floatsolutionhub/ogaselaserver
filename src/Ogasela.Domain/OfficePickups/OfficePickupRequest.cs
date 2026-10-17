namespace Ogasela.Domain.OfficePickups;

public enum OfficePickupStatus
{
    /// <summary>Created; the buyer is on the payment page.</summary>
    AwaitingPayment,

    /// <summary>The gateway reports the payment failed or was abandoned. Nothing was taken.</summary>
    PaymentFailed,

    /// <summary>The buyer left the payment page without paying. Nothing was taken.</summary>
    PaymentCancelled,

    /// <summary>Paid. The seller has been asked to drop the item at the branch.</summary>
    PaymentConfirmed,

    /// <summary>The seller dropped the item at the branch; it's waiting for inspection.</summary>
    ItemReceived,

    InspectionPassed,

    /// <summary>The item didn't pass inspection - the buyer is refunded from here.</summary>
    InspectionFailed,

    /// <summary>Inspected and ready: the buyer can collect it at the branch or have it delivered.</summary>
    ReadyForCollection,

    OutForDelivery,

    /// <summary>Collected at the branch or delivered to the buyer.</summary>
    Completed,

    /// <summary>The buyer was refunded (less the cancellation fee). Closed.</summary>
    Refunded
}

public enum OfficePickupFulfilment
{
    BranchCollection,
    HomeDelivery
}

/// <summary>
/// A buyer's request to buy one ad's item through an Ogasela office: the buyer pays Ogasela the
/// item price plus the office pickup fee, the seller drops the item at the chosen branch, staff
/// inspect it, and the buyer collects it there (or has it delivered). Amounts are in kobo and
/// are fixed when the request is created, so later price edits on the ad don't change them.
/// Reference is the short code buyers, sellers and staff quote (e.g. OPK-7K3Q9M).
/// </summary>
public class OfficePickupRequest
{
    private OfficePickupRequest()
    {
    }

    public Guid Id { get; private set; }

    public string Reference { get; private set; } = string.Empty;

    public Guid ListingId { get; private set; }

    /// <summary>The ad's title when the request was made - the ad can be edited or deleted later.</summary>
    public string ListingTitle { get; private set; } = string.Empty;

    public Guid BuyerUserId { get; private set; }

    public Guid SellerUserId { get; private set; }

    public Guid BranchId { get; private set; }

    public decimal ItemPriceKobo { get; private set; }

    public decimal ServiceFeeKobo { get; private set; }

    public decimal TotalKobo { get; private set; }

    public string? PaymentReference { get; private set; }

    public OfficePickupStatus Status { get; private set; }

    public OfficePickupFulfilment? Fulfilment { get; private set; }

    public string? DeliveryAddress { get; private set; }

    public DateTime? PaidAt { get; private set; }

    public DateTime? CancellationRequestedAt { get; private set; }

    public string? CancellationReason { get; private set; }

    public decimal? CancellationFeeKobo { get; private set; }

    public decimal? RefundedKobo { get; private set; }

    public DateTime? RefundedAt { get; private set; }

    public DateTime? CompletedAt { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime UpdatedAt { get; private set; }

    public static OfficePickupRequest Create(
        string reference, Guid listingId, string listingTitle, Guid buyerUserId, Guid sellerUserId, Guid branchId,
        decimal itemPriceKobo, decimal serviceFeeKobo, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        Reference = reference,
        ListingId = listingId,
        ListingTitle = listingTitle,
        BuyerUserId = buyerUserId,
        SellerUserId = sellerUserId,
        BranchId = branchId,
        ItemPriceKobo = itemPriceKobo,
        ServiceFeeKobo = serviceFeeKobo,
        TotalKobo = itemPriceKobo + serviceFeeKobo,
        Status = OfficePickupStatus.AwaitingPayment,
        CreatedAt = now,
        UpdatedAt = now
    };

    public bool IsParty(Guid userId) => userId == BuyerUserId || userId == SellerUserId;

    /// <summary>Paid, and not yet completed or refunded - the stage where a refund is possible.</summary>
    public bool IsInProgress => Status is OfficePickupStatus.PaymentConfirmed or OfficePickupStatus.ItemReceived
        or OfficePickupStatus.InspectionPassed or OfficePickupStatus.InspectionFailed
        or OfficePickupStatus.ReadyForCollection or OfficePickupStatus.OutForDelivery;

    public void AttachPayment(string paymentReference, DateTime now)
    {
        PaymentReference = paymentReference;
        UpdatedAt = now;
    }

    public void MarkPaid(DateTime now)
    {
        Status = OfficePickupStatus.PaymentConfirmed;
        PaidAt = now;
        UpdatedAt = now;
    }

    public void MarkPaymentFailed(DateTime now)
    {
        Status = OfficePickupStatus.PaymentFailed;
        UpdatedAt = now;
    }

    public void MarkPaymentCancelled(DateTime now)
    {
        Status = OfficePickupStatus.PaymentCancelled;
        UpdatedAt = now;
    }

    /// <summary>Not yet paid - awaiting payment, or cancelled by the buyer (a late success from the gateway still settles it).</summary>
    public bool IsAwaitingSettlement => Status is OfficePickupStatus.AwaitingPayment or OfficePickupStatus.PaymentCancelled;

    /// <summary>Staff moving the request along its pipeline (item received, inspection result, ready, out for delivery, completed).</summary>
    public void MoveTo(OfficePickupStatus status, DateTime now)
    {
        Status = status;
        if (status == OfficePickupStatus.Completed)
        {
            CompletedAt = now;
        }

        UpdatedAt = now;
    }

    public void SetFulfilment(OfficePickupFulfilment fulfilment, string? deliveryAddress, DateTime now)
    {
        Fulfilment = fulfilment;
        DeliveryAddress = fulfilment == OfficePickupFulfilment.HomeDelivery ? deliveryAddress : null;
        UpdatedAt = now;
    }

    public void RequestCancellation(string reason, DateTime now)
    {
        CancellationRequestedAt = now;
        CancellationReason = reason;
        UpdatedAt = now;
    }

    public void MarkRefunded(decimal cancellationFeeKobo, decimal refundedKobo, DateTime now)
    {
        Status = OfficePickupStatus.Refunded;
        CancellationFeeKobo = cancellationFeeKobo;
        RefundedKobo = refundedKobo;
        RefundedAt = now;
        UpdatedAt = now;
    }
}

/// <summary>One entry on a request's activity timeline - what the buyer and seller see as its progress.</summary>
public class OfficePickupActivity
{
    private OfficePickupActivity()
    {
    }

    public Guid Id { get; private set; }

    public Guid RequestId { get; private set; }

    public OfficePickupStatus Status { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string? Note { get; private set; }

    /// <summary>The staff member who recorded it; null for system entries (e.g. a confirmed payment).</summary>
    public Guid? ActorUserId { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public static OfficePickupActivity Create(
        Guid requestId, OfficePickupStatus status, string title, string? note, Guid? actorUserId, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        RequestId = requestId,
        Status = status,
        Title = title,
        Note = note,
        ActorUserId = actorUserId,
        CreatedAt = now
    };
}
