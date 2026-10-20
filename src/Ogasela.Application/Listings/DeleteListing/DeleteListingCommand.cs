using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Shared;

namespace Ogasela.Application.Listings.DeleteListing;

/// <summary>
/// The seller removes a draft, rejected, pending or expired ad. It's soft-deleted (status Deleted):
/// chats, disputes, office pickups and payments still reference it. A plan already paid for an ad
/// awaiting review isn't refunded - the seller chose to delete it, and the apps warn them first.
/// </summary>
public sealed record DeleteListingCommand(Guid ListingId) : IRequest<Result>;

public sealed class DeleteListingCommandHandler : IRequestHandler<DeleteListingCommand, Result>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IDateTime _dateTime;

    public DeleteListingCommandHandler(IApplicationDbContext dbContext, ICurrentUserService currentUser, IDateTime dateTime)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _dateTime = dateTime;
    }

    public async Task<Result> Handle(DeleteListingCommand request, CancellationToken cancellationToken)
    {
        var listing = await _dbContext.Listings.FirstOrDefaultAsync(l => l.Id == request.ListingId, cancellationToken);
        if (listing is null || listing.Status == Domain.Listings.ListingStatus.Deleted)
        {
            return Result.Failure(ListingErrors.ListingNotFound);
        }

        var isOwner = await _dbContext.SellerProfiles
            .AnyAsync(s => s.Id == listing.SellerId && s.UserId == _currentUser.UserId!.Value, cancellationToken);
        if (!isOwner)
        {
            return Result.Failure(ListingErrors.NotOwner);
        }

        if (!listing.CanBeDeleted)
        {
            return Result.Failure(ListingErrors.InvalidStatusForDelete);
        }

        listing.Delete(_dateTime.UtcNow);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
