using MediatR;
using Ogasela.Application.Analytics;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Shared;

namespace Ogasela.Application.Search;

public sealed class SearchListingsQueryHandler : IRequestHandler<SearchListingsQuery, Result<SearchResultPage>>
{
    private readonly IListingSearchRepository _searchRepository;
    private readonly IEngagementTracker _engagementTracker;
    private readonly ICurrentUserService _currentUser;

    public SearchListingsQueryHandler(
        IListingSearchRepository searchRepository, IEngagementTracker engagementTracker, ICurrentUserService currentUser)
    {
        _searchRepository = searchRepository;
        _engagementTracker = engagementTracker;
        _currentUser = currentUser;
    }

    public async Task<Result<SearchResultPage>> Handle(SearchListingsQuery request, CancellationToken cancellationToken)
    {
        // Signed-in viewers never see ads from sellers they've blocked.
        var criteria = request.Criteria with { ViewerUserId = _currentUser.UserId };
        var page = await _searchRepository.SearchAsync(criteria, cancellationToken);

        // Every listing shown in a result page (search, category pages, home-screen rails) is an impression.
        await _engagementTracker.RecordAsync(
            page.Items.Select(i => (i.Id, i.SellerId)).ToList(), ListingEngagement.Impression, cancellationToken);

        return Result.Success(page);
    }
}
