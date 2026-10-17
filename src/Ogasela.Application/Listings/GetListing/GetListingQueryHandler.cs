using MediatR;
using Ogasela.Application.Analytics;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.OfficePickups;
using Ogasela.Domain.Listings;
using Ogasela.Domain.Promotions;
using Ogasela.Shared;

namespace Ogasela.Application.Listings.GetListing;

public sealed class GetListingQueryHandler : IRequestHandler<GetListingQuery, Result<ListingDetailResponse>>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IEngagementTracker _engagementTracker;

    public GetListingQueryHandler(IApplicationDbContext dbContext, ICurrentUserService currentUser, IEngagementTracker engagementTracker)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _engagementTracker = engagementTracker;
    }

    public async Task<Result<ListingDetailResponse>> Handle(GetListingQuery request, CancellationToken cancellationToken)
    {
        var listing = await _dbContext.Listings.FirstOrDefaultAsync(l => l.Id == request.ListingId, cancellationToken);
        if (listing is null)
        {
            return Result.Failure<ListingDetailResponse>(ListingErrors.ListingNotFound);
        }

        if (listing.Status is ListingStatus.Draft or ListingStatus.PendingReview
            && !await IsOwnerAsync(listing, cancellationToken))
        {
            // Drafts and ads awaiting approval are only visible to their own seller - hide their
            // existence from anyone else.
            return Result.Failure<ListingDetailResponse>(ListingErrors.ListingNotFound);
        }

        var seller = await (
            from sellerProfile in _dbContext.SellerProfiles
            join user in _dbContext.Users on sellerProfile.UserId equals user.Id
            where sellerProfile.Id == listing.SellerId
            select new { sellerProfile, user })
            .FirstOrDefaultAsync(cancellationToken);

        if (seller is null)
        {
            return Result.Failure<ListingDetailResponse>(ListingErrors.ListingNotFound);
        }

        var category = await _dbContext.Categories
            .Where(c => c.Id == listing.CategoryId)
            .Select(c => new { c.Name, c.ParentCategoryId })
            .FirstOrDefaultAsync(cancellationToken);

        var planName = listing.PromotionPlanId is { } planId
            ? await _dbContext.PromotionPlans
                .Where(p => p.Id == planId)
                .Select(p => p.Name)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        var sellerSummary = new ListingSellerSummary(
            seller.sellerProfile.Id,
            seller.user.Id,
            seller.sellerProfile.BusinessName,
            seller.user.Name,
            _currentUser.UserId is null ? null : seller.user.Phone,
            seller.sellerProfile.StoreAddress,
            seller.sellerProfile.VerificationStatus,
            seller.sellerProfile.CreatedAt,
            seller.sellerProfile.IsBusinessVerified);

        // A seller opening their own ad isn't a view.
        if (_currentUser.UserId != seller.user.Id)
        {
            await _engagementTracker.RecordAsync([(listing.Id, listing.SellerId)], ListingEngagement.View, cancellationToken);
        }

        return Result.Success(new ListingDetailResponse(
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
            category?.Name,
            category?.ParentCategoryId,
            planName?.ToString(),
            sellerSummary,
            listing.ReviewNote,
            listing.HasOriginalReceipt,
            listing.TikTokUrl,
            listing.FacebookUrl,
            listing.InstagramUrl,
            listing.YouTubeUrl,
            await OfficePickupRules.IsAvailableAsync(_dbContext, listing, cancellationToken)));
    }

    private async Task<bool> IsOwnerAsync(Listing listing, CancellationToken cancellationToken)
    {
        if (_currentUser.UserId is not { } userId)
        {
            return false;
        }

        return await _dbContext.SellerProfiles
            .AnyAsync(s => s.Id == listing.SellerId && s.UserId == userId, cancellationToken);
    }
}
