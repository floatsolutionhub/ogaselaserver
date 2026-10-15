using Microsoft.AspNetCore.Http;
using Ogasela.Domain.Listings;

namespace Ogasela.Api.Contracts.Listings;

/// <summary>MediaUrls: at most Listing.MaxPhotoCount (5) URLs, each returned by POST api/v1/listings/images.
/// HasOriginalReceipt only counts for Used items. The social links are optional and must point at that platform.</summary>
public sealed record CreateListingRequest(
    string Title,
    string Description,
    Guid CategoryId,
    decimal? Price,
    ListingCondition Condition,
    IReadOnlyList<string>? MediaUrls,
    Guid? PromotionPlanId,
    string? Location = null,
    decimal? Latitude = null,
    decimal? Longitude = null,
    bool? HasOriginalReceipt = null,
    string? TikTokUrl = null,
    string? FacebookUrl = null,
    string? InstagramUrl = null,
    string? YouTubeUrl = null);

/// <summary>MediaUrls: at most Listing.MaxPhotoCount (5) URLs, each returned by POST api/v1/listings/images.
/// HasOriginalReceipt only counts for Used items. The social links are optional and must point at that platform.</summary>
public sealed record UpdateListingRequest(
    string Title,
    string Description,
    Guid CategoryId,
    decimal? Price,
    ListingCondition Condition,
    IReadOnlyList<string>? MediaUrls,
    Guid? PromotionPlanId,
    string? Location = null,
    decimal? Latitude = null,
    decimal? Longitude = null,
    bool? HasOriginalReceipt = null,
    string? TikTokUrl = null,
    string? FacebookUrl = null,
    string? InstagramUrl = null,
    string? YouTubeUrl = null);

public sealed record UploadListingImageRequest(IFormFile Image);

public sealed record ListingCheckoutRequest(Guid PromotionPlanId, Ogasela.Application.Listings.Checkout.ListingPaymentMethod PaymentMethod);
