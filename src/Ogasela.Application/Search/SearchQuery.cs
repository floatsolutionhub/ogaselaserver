using Ogasela.Domain.Listings;

namespace Ogasela.Application.Search;

/// <summary>The search/filter/sort criteria for one page of listing search results.</summary>
public sealed record SearchQuery(
    string? Keyword,
    Guid? CategoryId,
    string? Location,
    decimal? Latitude,
    decimal? Longitude,
    decimal? RadiusKm,
    decimal? MinPrice,
    decimal? MaxPrice,
    ListingCondition? Condition,
    bool VerifiedSellerOnly,
    SearchSortOption SortBy,
    int Page,
    int PageSize,
    Guid? ViewerUserId = null)
{
    public bool HasCoordinates => Latitude is not null && Longitude is not null;
}
