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
    private readonly ListingPublishService _publishService;

    public UpdateListingCommandHandler(
        IApplicationDbContext dbContext, ICurrentUserService currentUser, IDateTime dateTime, ListingPublishService publishService)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _dateTime = dateTime;
        _publishService = publishService;
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

        // Drafts (including rejected ads), live ads, and expired ads being freshened up before renewal.
        if (listing.Status is not (ListingStatus.Draft or ListingStatus.Active or ListingStatus.ExpiringSoon or ListingStatus.Expired))
        {
            return Result.Failure<ListingResponse>(ListingErrors.InvalidStatusForUpdate);
        }

        var now = _dateTime.UtcNow;
        var isLive = listing.Status is ListingStatus.Active or ListingStatus.ExpiringSoon;
        // A live ad - or a rejected edit of one whose plan is still running - keeps its plan: changing
        // it is a paid upgrade (StartPlanUpgradeCommand), never a free edit.
        var planLocked = isLive || listing.HasRunningPlanPeriod(now);
        if (planLocked && request.PromotionPlanId is { } requestedPlan && requestedPlan != listing.PromotionPlanId)
        {
            return Result.Failure<ListingResponse>(ListingErrors.PlanChangeNotAllowedInEdit);
        }

        var categoryExists = await _dbContext.Categories.AnyAsync(c => c.Id == request.CategoryId, cancellationToken);
        if (!categoryExists)
        {
            return Result.Failure<ListingResponse>(ListingErrors.CategoryNotFound);
        }

        if ((planLocked ? listing.PromotionPlanId : request.PromotionPlanId) is { } promotionPlanId)
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

        // A null plan keeps the one already chosen (e.g. at checkout) - an edit never silently drops it.
        listing.UpdateDetails(
            request.CategoryId, request.PromotionPlanId ?? listing.PromotionPlanId, request.Title, request.Description, request.Price,
            request.Condition, request.MediaUrls, now);

        listing.SetLocation(request.Location, request.Latitude, request.Longitude, now);
        listing.SetReceiptAndLinks(
            request.HasOriginalReceipt, request.TikTokUrl, request.FacebookUrl, request.InstagramUrl, request.YouTubeUrl, now);

        // Changes to a live ad go back to a moderator before buyers see them; its plan period keeps running.
        if (isLive && await _publishService.RequiresApprovalAsync(cancellationToken))
        {
            listing.SubmitEditForReview(now);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(ListingResponse.From(listing));
    }
}
