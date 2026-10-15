using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Listings.GetListing;
using Ogasela.Domain.Listings;
using Ogasela.Shared;

namespace Ogasela.Application.Listings.UpdateListing;

public sealed class UpdateListingCommandHandler : IRequestHandler<UpdateListingCommand, Result<ListingResponse>>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IDateTime _dateTime;

    public UpdateListingCommandHandler(IApplicationDbContext dbContext, ICurrentUserService currentUser, IDateTime dateTime)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _dateTime = dateTime;
    }

    public async Task<Result<ListingResponse>> Handle(UpdateListingCommand request, CancellationToken cancellationToken)
    {
        var listing = await _dbContext.Listings.FirstOrDefaultAsync(l => l.Id == request.ListingId, cancellationToken);
        if (listing is null)
        {
            return Result.Failure<ListingResponse>(ListingErrors.ListingNotFound);
        }

        var isOwner = await _dbContext.SellerProfiles
            .AnyAsync(s => s.Id == listing.SellerId && s.UserId == _currentUser.UserId!.Value, cancellationToken);
        if (!isOwner)
        {
            return Result.Failure<ListingResponse>(ListingErrors.NotOwner);
        }

        if (listing.Status is not (ListingStatus.Draft or ListingStatus.Active))
        {
            return Result.Failure<ListingResponse>(ListingErrors.InvalidStatusForUpdate);
        }

        var categoryExists = await _dbContext.Categories.AnyAsync(c => c.Id == request.CategoryId, cancellationToken);
        if (!categoryExists)
        {
            return Result.Failure<ListingResponse>(ListingErrors.CategoryNotFound);
        }

        if (request.PromotionPlanId is { } promotionPlanId)
        {
            var plan = await _dbContext.PromotionPlans.FirstOrDefaultAsync(p => p.Id == promotionPlanId, cancellationToken);
            if (plan is null)
            {
                return Result.Failure<ListingResponse>(ListingErrors.PromotionPlanNotFound);
            }

            if (request.MediaUrls.Count > plan.PhotoLimit)
            {
                return Result.Failure<ListingResponse>(ListingErrors.MediaLimitExceeded);
            }
        }

        var now = _dateTime.UtcNow;
        // A null plan keeps the one already chosen (e.g. at checkout) - an edit never silently drops it.
        listing.UpdateDetails(
            request.CategoryId, request.PromotionPlanId ?? listing.PromotionPlanId, request.Title, request.Description, request.Price,
            request.Condition, request.MediaUrls, now);

        listing.SetLocation(request.Location, request.Latitude, request.Longitude, now);
        listing.SetReceiptAndLinks(
            request.HasOriginalReceipt, request.TikTokUrl, request.FacebookUrl, request.InstagramUrl, request.YouTubeUrl, now);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(ListingResponse.From(listing));
    }
}
