using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Domain.Listings;
using Ogasela.Shared;

namespace Ogasela.Application.Analytics.GetSellerProfile;

/// <summary>A seller's public storefront header. Viewing it counts as a profile visit unless it's the seller's own.</summary>
public sealed record GetSellerProfileQuery(Guid SellerProfileId) : IRequest<Result<SellerProfileResponse>>;

public sealed record SellerProfileResponse(
    Guid SellerProfileId,
    string BusinessName,
    string? Name,
    string? StoreAddress,
    string VerificationStatus,
    DateTime MemberSince,
    int ActiveAdCount,
    decimal AverageRating,
    int ReviewCount,
    bool IsBusinessVerified = false,
    Guid UserId = default,
    bool IsBlockedByMe = false);

public sealed class GetSellerProfileQueryHandler : IRequestHandler<GetSellerProfileQuery, Result<SellerProfileResponse>>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IEngagementTracker _engagementTracker;

    public GetSellerProfileQueryHandler(
        IApplicationDbContext dbContext, ICurrentUserService currentUser, IEngagementTracker engagementTracker)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _engagementTracker = engagementTracker;
    }

    public async Task<Result<SellerProfileResponse>> Handle(GetSellerProfileQuery request, CancellationToken cancellationToken)
    {
        var seller = await (
            from s in _dbContext.SellerProfiles
            join u in _dbContext.Users on s.UserId equals u.Id
            where s.Id == request.SellerProfileId
            select new { s, u.Name })
            .FirstOrDefaultAsync(cancellationToken);

        if (seller is null)
        {
            return Result.Failure<SellerProfileResponse>(AnalyticsErrors.SellerNotFound);
        }

        var activeAdCount = await _dbContext.Listings.CountAsync(
            l => l.SellerId == seller.s.Id && (l.Status == ListingStatus.Active || l.Status == ListingStatus.ExpiringSoon),
            cancellationToken);

        var reviews = _dbContext.Reviews.Where(r => r.RevieweeId == seller.s.UserId);
        var reviewCount = await reviews.CountAsync(cancellationToken);
        var averageRating = reviewCount == 0 ? 0m : await reviews.AverageAsync(r => (decimal)r.Rating, cancellationToken);

        if (_currentUser.UserId != seller.s.UserId)
        {
            await _engagementTracker.RecordProfileVisitAsync(seller.s.Id, cancellationToken);
        }

        return Result.Success(new SellerProfileResponse(
            seller.s.Id,
            seller.s.BusinessName,
            seller.Name,
            seller.s.StoreAddress,
            seller.s.VerificationStatus.ToString(),
            seller.s.CreatedAt,
            activeAdCount,
            Math.Round(averageRating, 2),
            reviewCount,
            seller.s.IsBusinessVerified,
            seller.s.UserId,
            await _dbContext.BlockedUsers.AnyAsync(
                b => b.BlockerId == _currentUser.UserId && b.BlockedId == seller.s.UserId, cancellationToken)));
    }
}
