using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Listings.GetListing;
using Ogasela.Domain.Listings;
using Ogasela.Shared;

namespace Ogasela.Application.Listings.ResubmitListing;

/// <summary>
/// An edit of a live ad was rejected and the seller has fixed it: back to a moderator, free, on the
/// plan period that's still running. (A rejected first submission or renewal goes through checkout
/// instead - its charge was refunded on rejection.)
/// </summary>
public sealed record ResubmitListingCommand(Guid ListingId) : IRequest<Result<ListingResponse>>;

public sealed class ResubmitListingCommandHandler : IRequestHandler<ResubmitListingCommand, Result<ListingResponse>>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IDateTime _dateTime;
    private readonly ListingPublishService _publishService;

    public ResubmitListingCommandHandler(
        IApplicationDbContext dbContext, ICurrentUserService currentUser, IDateTime dateTime, ListingPublishService publishService)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _dateTime = dateTime;
        _publishService = publishService;
    }

    public async Task<Result<ListingResponse>> Handle(ResubmitListingCommand request, CancellationToken cancellationToken)
    {
        var listing = await _dbContext.Listings.FirstOrDefaultAsync(l => l.Id == request.ListingId, cancellationToken);
        if (listing is null || listing.Status == ListingStatus.Deleted)
        {
            return Result.Failure<ListingResponse>(ListingErrors.ListingNotFound);
        }

        var isOwner = await _dbContext.SellerProfiles
            .AnyAsync(s => s.Id == listing.SellerId && s.UserId == _currentUser.UserId!.Value, cancellationToken);
        if (!isOwner)
        {
            return Result.Failure<ListingResponse>(ListingErrors.NotOwner);
        }

        var now = _dateTime.UtcNow;
        if (listing.Status != ListingStatus.Draft || !listing.HasRunningPlanPeriod(now))
        {
            return Result.Failure<ListingResponse>(ListingErrors.NothingToResubmit);
        }

        if (string.IsNullOrWhiteSpace(listing.Title) || string.IsNullOrWhiteSpace(listing.Description) || listing.MediaUrls.Count == 0)
        {
            return Result.Failure<ListingResponse>(ListingErrors.Incomplete);
        }

        if (await _publishService.RequiresApprovalAsync(cancellationToken))
        {
            listing.SubmitEditForReview(now);
        }
        else
        {
            listing.ApproveReview(now, planDurationDays: 0);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success(ListingResponse.From(listing));
    }
}
