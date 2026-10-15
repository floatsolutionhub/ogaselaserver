using Ogasela.Domain.Listings;

namespace Ogasela.Application.Listings.GetListing;

public sealed record ListingResponse(
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
    string? ReviewNote = null,
    bool? HasOriginalReceipt = null,
    string? TikTokUrl = null,
    string? FacebookUrl = null,
    string? InstagramUrl = null,
    string? YouTubeUrl = null)
{
    public static ListingResponse From(Listing listing) => new(
        listing.Id,
        listing.SellerId,
        listing.CategoryId,
        listing.PromotionPlanId,
        listing.Title,
        listing.Description,
        listing.Price,
        listing.Condition,
        listing.Status,
        listing.MediaUrls,
        listing.Location,
        listing.Latitude,
        listing.Longitude,
        listing.PublishedAt,
        listing.ExpiresAt,
        listing.CreatedAt,
        listing.UpdatedAt,
        listing.ReviewNote,
        listing.HasOriginalReceipt,
        listing.TikTokUrl,
        listing.FacebookUrl,
        listing.InstagramUrl,
        listing.YouTubeUrl);
}
