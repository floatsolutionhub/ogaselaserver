using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Domain.Accounts;
using Ogasela.Shared;

namespace Ogasela.Application.Accounts.VerificationOverview;

/// <summary>
/// Where the caller stands on personal verification (selfie, ID card, utility bill) and - for sellers -
/// business verification (registration document, proof of office address): one checklist per track, so
/// the app can say exactly what's still missing. Business is null for buyers; they get a business once
/// staff verify the person and the account becomes a seller.
/// </summary>
public sealed record GetVerificationOverviewQuery : IRequest<Result<VerificationOverviewResponse>>;

public sealed record VerificationOverviewResponse(VerificationTrack Personal, VerificationTrack? Business);

/// <summary>Status: NotStarted (nothing in), InProgress (some items missing or rejected), PendingReview (everything
/// in, waiting on staff) or Verified.</summary>
public sealed record VerificationTrack(string Status, DateTime? VerifiedAt, IReadOnlyList<VerificationItem> Items);

/// <summary>Key: Selfie | IdCard | UtilityBill | BusinessRegistration | OfficeUtilityBill.
/// State: Missing | Submitted (awaiting review) | Approved | Rejected. Note is staff's reason for a rejection.</summary>
public sealed record VerificationItem(string Key, string State, string? Note = null);

public sealed class GetVerificationOverviewQueryHandler
    : IRequestHandler<GetVerificationOverviewQuery, Result<VerificationOverviewResponse>>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;

    public GetVerificationOverviewQueryHandler(IApplicationDbContext dbContext, ICurrentUserService currentUser)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
    }

    public async Task<Result<VerificationOverviewResponse>> Handle(
        GetVerificationOverviewQuery request, CancellationToken cancellationToken)
    {
        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == _currentUser.UserId, cancellationToken);
        if (user is null)
        {
            return Result.Failure<VerificationOverviewResponse>(AccountErrors.UserNotFound);
        }

        var seller = await _dbContext.SellerProfiles.FirstOrDefaultAsync(s => s.UserId == user.Id, cancellationToken);

        // Newest first: the first of each type is the one that counts.
        var documents = await _dbContext.UserDocuments
            .Where(d => d.UserId == user.Id)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(cancellationToken);

        VerificationItem DocumentItem(UserDocumentType type)
        {
            var document = documents.FirstOrDefault(d => d.Type == type);
            return document is null
                ? new VerificationItem(type.ToString(), "Missing")
                : document.Status switch
                {
                    UserDocumentStatus.Approved => new VerificationItem(type.ToString(), "Approved"),
                    UserDocumentStatus.Rejected => new VerificationItem(type.ToString(), "Rejected", document.ReviewNote),
                    _ => new VerificationItem(type.ToString(), "Submitted"),
                };
        }

        var personalVerified = seller?.VerificationStatus == VerificationStatus.Verified;
        var personal = Track(
            personalVerified,
            seller?.VerifiedAt,
            [
                new VerificationItem("Selfie", user.ProfilePhotoS3Key is null ? "Missing" : "Submitted"),
                DocumentItem(UserDocumentType.IdCard),
                DocumentItem(UserDocumentType.UtilityBill),
            ]);

        var business = seller is null
            ? null
            : Track(
                seller.IsBusinessVerified,
                seller.BusinessVerifiedAt,
                [
                    DocumentItem(UserDocumentType.BusinessRegistration),
                    DocumentItem(UserDocumentType.OfficeUtilityBill),
                ]);

        return Result.Success(new VerificationOverviewResponse(personal, business));
    }

    private static VerificationTrack Track(bool verified, DateTime? verifiedAt, IReadOnlyList<VerificationItem> items)
    {
        if (verified)
        {
            return new VerificationTrack("Verified", verifiedAt, items);
        }

        // A rejected item has to be uploaded again, so it holds the track back like a missing one.
        var outstanding = items.Count(i => i.State is "Missing" or "Rejected");
        var status = outstanding == 0
            ? "PendingReview"
            : items.All(i => i.State == "Missing") ? "NotStarted" : "InProgress";
        return new VerificationTrack(status, null, items);
    }
}
