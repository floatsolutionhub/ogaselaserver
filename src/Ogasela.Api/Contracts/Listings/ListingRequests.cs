using Microsoft.AspNetCore.Http;
using Ogasela.Domain.Listings;

namespace Ogasela.Api.Contracts.Listings;

/// <summary>MediaUrls: at most Listing.MaxPhotoCount (30) URLs; on submission only the first PromotionPlan.PhotoLimit are kept, each returned by POST api/v1/listings/images.
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

/// <summary>MediaUrls: at most Listing.MaxPhotoCount (30) URLs; on submission only the first PromotionPlan.PhotoLimit are kept, each returned by POST api/v1/listings/images.
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

/// <summary>ReturnUrl: optional page the payment gateway sends the payer back to - must be on Payments:AllowedReturnUrls.</summary>
public sealed record ListingCheckoutRequest(
    Guid PromotionPlanId, Ogasela.Application.Listings.Checkout.ListingPaymentMethod PaymentMethod, string? ReturnUrl = null);

/// <summary>Moves a running ad onto a higher plan for its full price. ReturnUrl: as for checkout.</summary>
public sealed record PlanUpgradeRequest(
    Guid PromotionPlanId,
    Ogasela.Application.Listings.Checkout.ListingPaymentMethod PaymentMethod = Ogasela.Application.Listings.Checkout.ListingPaymentMethod.Card,
    string? ReturnUrl = null);
