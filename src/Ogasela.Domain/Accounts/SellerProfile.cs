namespace Ogasela.Domain.Accounts;

public class SellerProfile
{
    private SellerProfile()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public string BusinessName { get; private set; } = string.Empty;

    public string? RcNumber { get; private set; }

    public string? Nin { get; private set; }

    /// <summary>Where buyers can find the seller's shop, shown on listing detail. Optional - sellers without a physical shop leave it empty.</summary>
    public string? StoreAddress { get; private set; }

    public VerificationStatus VerificationStatus { get; private set; }

    public DateTime? VerifiedAt { get; private set; }

    /// <summary>When staff verified the business (registration + proof of office address); null if it isn't.
    /// A trust badge on top of personal verification - selling only needs <see cref="VerificationStatus"/>.</summary>
    public DateTime? BusinessVerifiedAt { get; private set; }

    public bool IsBusinessVerified => BusinessVerifiedAt is not null;

    public DateTime CreatedAt { get; private set; }

    public static SellerProfile Create(Guid userId, string businessName, string? rcNumber, string? nin)
    {
        return new SellerProfile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BusinessName = businessName,
            RcNumber = rcNumber,
            Nin = nin,
            VerificationStatus = VerificationStatus.NotStarted,
            VerifiedAt = null,
            CreatedAt = DateTime.UtcNow
        };
    }

    public void UpdateBusinessName(string businessName)
    {
        BusinessName = businessName;
    }

    public void UpdateStoreAddress(string? storeAddress)
    {
        StoreAddress = string.IsNullOrWhiteSpace(storeAddress) ? null : storeAddress.Trim();
    }

    public void UpdateVerificationStatus(VerificationStatus status, DateTime? decidedAt)
    {
        VerificationStatus = status;

        // VerifiedAt only describes a current verification - cleared if it's revoked.
        VerifiedAt = status == VerificationStatus.Verified ? decidedAt : null;

        // A business is only vouched for while the person behind it is.
        if (status != VerificationStatus.Verified)
        {
            BusinessVerifiedAt = null;
        }
    }

    public void VerifyBusiness(DateTime now) => BusinessVerifiedAt = now;

    public void RevokeBusinessVerification() => BusinessVerifiedAt = null;

    /// <summary>NDPA data-subject deletion: clears identifying business/ID fields. Id/UserId/VerificationStatus/CreatedAt are kept, since Listings/Transactions/TrustScore still reference this SellerId for financial/audit history.</summary>
    public void Anonymize()
    {
        BusinessName = "Erased Seller";
        RcNumber = null;
        Nin = null;
        StoreAddress = null;
        BusinessVerifiedAt = null;
    }
}
