using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Listings;
using Ogasela.Domain.Listings;
using Ogasela.Domain.Moderation;
using Ogasela.Shared;

namespace Ogasela.Application.Moderation.ReportListing;

/// <summary>
/// Files a TargetType.Listing report into the same moderation queue the publish-time fraud check
/// uses, so a Moderator's "Remove" decision pauses the listing (see ResolveReportCommandHandler).
/// Re-reporting a listing you already have an open report on returns that report rather than
/// stacking duplicates in the queue.
/// </summary>
public sealed class ReportListingCommandHandler : IRequestHandler<ReportListingCommand, Result<Guid>>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IDateTime _dateTime;

    public ReportListingCommandHandler(IApplicationDbContext dbContext, ICurrentUserService currentUser, IDateTime dateTime)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _dateTime = dateTime;
    }

    public async Task<Result<Guid>> Handle(ReportListingCommand request, CancellationToken cancellationToken)
    {
        var reporterId = _currentUser.UserId!.Value;

        var listing = await _dbContext.Listings
            .Where(l => l.Id == request.ListingId && l.Status != ListingStatus.Draft && l.Status != ListingStatus.PendingReview && l.Status != ListingStatus.Deleted)
            .Select(l => new { l.Id, l.SellerId })
            .FirstOrDefaultAsync(cancellationToken);

        if (listing is null)
        {
            return Result.Failure<Guid>(ListingErrors.ListingNotFound);
        }

        var isOwnListing = await _dbContext.SellerProfiles
            .AnyAsync(s => s.Id == listing.SellerId && s.UserId == reporterId, cancellationToken);
        if (isOwnListing)
        {
            return Result.Failure<Guid>(ModerationErrors.CannotReportOwnListing);
        }

        var existingOpenReportId = await _dbContext.Reports
            .Where(r => r.ReporterId == reporterId
                && r.TargetType == ReportTargetType.Listing
                && r.TargetId == listing.Id
                && r.Status == ReportStatus.Open)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existingOpenReportId is { } reportId)
        {
            return Result.Success(reportId);
        }

        var report = Report.Create(reporterId, ReportTargetType.Listing, listing.Id, request.Reason.Trim(), _dateTime.UtcNow);
        _dbContext.Reports.Add(report);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(report.Id);
    }
}
