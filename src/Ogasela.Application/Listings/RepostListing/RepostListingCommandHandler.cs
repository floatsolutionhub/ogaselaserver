using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Listings.GetListing;
using Ogasela.Domain.Listings;
using Ogasela.Shared;

namespace Ogasela.Application.Listings.RepostListing;

/// <summary>
/// Re-runs the exact same <see cref="ListingPublishService"/> pipeline PublishListingCommand
/// uses - a listing coming back from Expired/Sold/Paused must clear every RULE-01..06 check
/// again, not just flip its status back to Active.
/// </summary>
public sealed class RepostListingCommandHandler : IRequestHandler<RepostListingCommand, Result<ListingResponse>>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;
    private readonly ListingPublishService _publishService;

    public RepostListingCommandHandler(
        IApplicationDbContext dbContext, ICurrentUserService currentUser, ListingPublishService publishService)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _publishService = publishService;
    }

    public async Task<Result<ListingResponse>> Handle(RepostListingCommand request, CancellationToken cancellationToken)
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

        if (listing.Status is not (ListingStatus.Expired or ListingStatus.Sold or ListingStatus.Paused))
        {
            return Result.Failure<ListingResponse>(ListingErrors.InvalidStatusForRepost);
        }

        var publishResult = await _publishService.PublishAsync(listing, cancellationToken);
        if (publishResult.IsFailure)
        {
            return Result.Failure<ListingResponse>(publishResult.Error);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await _publishService.FlushDroppedMediaAsync(cancellationToken);

        return Result.Success(ListingResponse.From(listing));
    }
}
