using Ogasela.Domain.Listings;
using Ogasela.Domain.Accounts;

namespace Ogasela.Application.Listings.GetListing;

/// <summary>
/// GET /listings/{id}: every <see cref="ListingResponse"/> field (same names, so existing callers
/// keep working) plus what the ad detail screen shows alongside it - category and promotion plan
/// names, and a summary of the seller.
/// </summary>
public sealed record ListingDetailResponse(
    Guid Id,
    Guid SellerId,
    Guid CategoryId,
    Guid? PromotionPlanId,
    string Title,
    string Description,
    decimal? Price,
    ListingCondition Condition,
    ListingStatus Status,
    IReadOnlyList<string> MediaUrls,
    string? Location,
    decimal? Latitude,
    decimal? Longitude,
    DateTime? PublishedAt,
    DateTime? ExpiresAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? CategoryName,
    Guid? ParentCategoryId,
    string? PromotionPlanName,
    ListingSellerSummary Seller,
    string? ReviewNote = null,
    bool? HasOriginalReceipt = null,
    string? TikTokUrl = null,
    string? FacebookUrl = null,
    string? InstagramUrl = null,
    string? YouTubeUrl = null,
    bool OfficePickupAvailable = false);

/// <summary>
/// Phone is only filled in for a signed-in caller, so seller numbers can't be scraped
/// anonymously off public listing pages; the app asks visitors to sign in before calling.
/// </summary>
public sealed record ListingSellerSummary(
    Guid SellerProfileId,
    Guid UserId,
    string BusinessName,
    string? Name,
    string? Phone,
    string? StoreAddress,
    VerificationStatus VerificationStatus,
    DateTime MemberSince,
    bool IsBusinessVerified = false,
    bool IsBlockedByMe = false);
