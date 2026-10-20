using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Analytics;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Messaging;
using Ogasela.Application.Search;
using Ogasela.Domain.Listings;
using Ogasela.Shared;

namespace Ogasela.Application.Listings.GetHotPicks;

/// <summary>
/// How a listing becomes "hot":
/// 1. Score each live ad on the last 7 days of buyer engagement - opens, calls, chats and saves weigh
///    most (they're deliberate interest); impressions only a little. A small boost for paid plans and
///    for freshly published ads lets new listings surface before they've built up engagement.
/// 2. Diversify: take ads round-robin across categories/subcategories by score, so a single busy
///    category (say phones) can't fill the whole feed.
/// 3. Shuffle within blocks of ten using the request's seed - the hottest ads stay near the top, but
///    the exact mix changes from visit to visit while staying stable as one visitor pages through.
/// </summary>
public sealed class GetHotPicksQueryHandler : IRequestHandler<GetHotPicksQuery, Result<SearchResultPage>>
{
    public const int MaxItems = 50;
    private const int CandidatePool = 400;
    private const int ShuffleBlock = 10;
    private const int EngagementWindowDays = 7;

    private readonly IApplicationDbContext _dbContext;
    private readonly IDateTime _dateTime;
    private readonly IEngagementTracker _engagementTracker;
    private readonly ICurrentUserService _currentUser;

    public GetHotPicksQueryHandler(
        IApplicationDbContext dbContext, IDateTime dateTime, IEngagementTracker engagementTracker, ICurrentUserService currentUser)
    {
        _dbContext = dbContext;
        _dateTime = dateTime;
        _engagementTracker = engagementTracker;
        _currentUser = currentUser;
    }

    public async Task<Result<SearchResultPage>> Handle(GetHotPicksQuery request, CancellationToken cancellationToken)
    {
        var now = _dateTime.UtcNow;
        var listings = _dbContext.Listings
            .Where(l => l.Status == ListingStatus.Active || l.Status == ListingStatus.ExpiringSoon);

        if (_currentUser.UserId is { } viewerId)
        {
            var blockedSellerIds = _dbContext.ProfileIdsBlockedBy(viewerId);
            listings = listings.Where(l => !blockedSellerIds.Contains(l.SellerId));
        }

        if (request.CategoryId is { } categoryId)
        {
            var categoryIds = await _dbContext.Categories
                .Where(c => c.Id == categoryId || c.ParentCategoryId == categoryId)
                .Select(c => c.Id)
                .ToListAsync(cancellationToken);
            listings = listings.Where(l => categoryIds.Contains(l.CategoryId));
        }

        if (!string.IsNullOrWhiteSpace(request.Location))
        {
            // Case-insensitive "contains" - translates to SQL on Postgres and works in the in-memory test provider.
            var place = request.Location.Trim().ToLower();
            listings = listings.Where(l => l.Location != null && l.Location.ToLower().Contains(place));
        }

        var candidates = await (
            from l in listings
            join p in _dbContext.PromotionPlans on l.PromotionPlanId equals p.Id
            orderby l.PublishedAt descending
            select new { Listing = l, p.Name, p.BoostWeight })
            .Take(CandidatePool)
            .ToListAsync(cancellationToken);

        var ids = candidates.Select(c => c.Listing.Id).ToList();
        var since = DateOnly.FromDateTime(now).AddDays(-(EngagementWindowDays - 1));
        var engagement = await _dbContext.ListingDailyStats
            .Where(s => ids.Contains(s.ListingId) && s.Date >= since)
            .GroupBy(s => s.ListingId)
            .Select(g => new
            {
                ListingId = g.Key,
                Impressions = g.Sum(s => s.Impressions),
                Views = g.Sum(s => s.Views),
                Calls = g.Sum(s => s.CallClicks),
                Chats = g.Sum(s => s.MessageStarts),
                Saves = g.Sum(s => s.Saves)
            })
            .ToDictionaryAsync(e => e.ListingId, cancellationToken);

        var scored = candidates
            .Select(c =>
            {
                engagement.TryGetValue(c.Listing.Id, out var e);
                var interest = e is null ? 0m : e.Views * 3m + e.Calls * 5m + e.Chats * 5m + e.Saves * 2m + e.Impressions * 0.02m;
                var ageDays = (decimal)(now - (c.Listing.PublishedAt ?? c.Listing.CreatedAt)).TotalDays;
                var freshness = 20m / (1m + Math.Max(ageDays, 0m));
                return (c.Listing, c.Name, Score: interest + freshness + c.BoostWeight * 4m);
            })
            .ToList();

        // Round-robin across categories, hottest first within each.
        var queues = scored
            .GroupBy(s => s.Listing.CategoryId)
            .Select(g => new Queue<(Listing Listing, string Name, decimal Score)>(g.OrderByDescending(s => s.Score)))
            .OrderByDescending(q => q.Peek().Score)
            .ToList();

        var diversified = new List<(Listing Listing, string Name, decimal Score)>();
        while (diversified.Count < MaxItems && queues.Count > 0)
        {
            foreach (var queue in queues.ToList())
            {
                diversified.Add(queue.Dequeue());
                if (queue.Count == 0)
                {
                    queues.Remove(queue);
                }

                if (diversified.Count == MaxItems)
                {
                    break;
                }
            }
        }

        var random = new Random(request.Seed);
        var ordered = diversified
            .Chunk(ShuffleBlock)
            .SelectMany(block => block.OrderBy(_ => random.Next()))
            .ToList();

        var pageItems = ordered
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(x => new SearchResultItem(
                x.Listing.Id,
                x.Listing.SellerId,
                x.Listing.CategoryId,
                x.Listing.Title,
                x.Listing.Description,
                x.Listing.MediaUrls.FirstOrDefault(),
                x.Listing.Price,
                x.Listing.Condition,
                x.Listing.Location,
                x.Listing.Latitude,
                x.Listing.Longitude,
                x.Listing.PublishedAt,
                x.Listing.CreatedAt,
                x.Name,
                null,
                Math.Round(x.Score, 4)))
            .ToList();

        await _engagementTracker.RecordAsync(
            pageItems.Select(i => (i.Id, i.SellerId)).ToList(), ListingEngagement.Impression, cancellationToken);

        return Result.Success(new SearchResultPage(pageItems, request.Page, request.PageSize, ordered.Count));
    }
}
