using MediatR;
using Ogasela.Application.Listings.GetListing;
using Ogasela.Domain.Listings;
using Ogasela.Shared;

namespace Ogasela.Application.Listings.CreateDraftListing;

public sealed record CreateDraftListingCommand(
    string Title,
    string Description,
    Guid CategoryId,
    decimal? Price,
    ListingCondition Condition,
    IReadOnlyList<string> MediaUrls,
    Guid? PromotionPlanId,
    string? Location = null,
    decimal? Latitude = null,
    decimal? Longitude = null,
    bool? HasOriginalReceipt = null,
    string? TikTokUrl = null,
    string? FacebookUrl = null,
    string? InstagramUrl = null,
    string? YouTubeUrl = null) : IRequest<Result<ListingResponse>>;
