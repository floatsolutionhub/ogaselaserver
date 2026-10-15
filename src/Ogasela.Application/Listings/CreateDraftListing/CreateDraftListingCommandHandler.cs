using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Listings.GetListing;
using Ogasela.Domain.Listings;
using Ogasela.Shared;

namespace Ogasela.Application.Listings.CreateDraftListing;

public sealed class CreateDraftListingCommandHandler : IRequestHandler<CreateDraftListingCommand, Result<ListingResponse>>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IDateTime _dateTime;

    public CreateDraftListingCommandHandler(IApplicationDbContext dbContext, ICurrentUserService currentUser, IDateTime dateTime)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _dateTime = dateTime;
    }

    public async Task<Result<ListingResponse>> Handle(CreateDraftListingCommand request, CancellationToken cancellationToken)
    {
        var sellerId = await _dbContext.SellerProfiles
            .Where(s => s.UserId == _currentUser.UserId!.Value)
            .Select(s => s.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (sellerId == Guid.Empty)
        {
            return Result.Failure<ListingResponse>(ListingErrors.SellerProfileNotFound);
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
        var listing = Listing.CreateDraft(
            Guid.NewGuid(), sellerId, request.CategoryId, request.PromotionPlanId, request.Title, request.Description,
            request.Price, request.Condition, request.MediaUrls, now);

        listing.SetLocation(request.Location, request.Latitude, request.Longitude, now);
        listing.SetReceiptAndLinks(
            request.HasOriginalReceipt, request.TikTokUrl, request.FacebookUrl, request.InstagramUrl, request.YouTubeUrl, now);

        _dbContext.Listings.Add(listing);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(ListingResponse.From(listing));
    }
}
