using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Accounts.Interfaces;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Domain.Accounts;
using Ogasela.Domain.Listings;
using Ogasela.Domain.Moderation;
using Ogasela.Shared;

namespace Ogasela.Application.Admin.Users;

public enum AdminUserStatusFilter
{
    All,
    Active,
    Suspended,

    /// <summary>Has an ID or address document waiting for staff review.</summary>
    DocumentsPending
}

/// <summary>Every app user (Buyers and Sellers - staff are managed on the Staff page), newest first, filterable.</summary>
public sealed record GetAdminUsersQuery(
    string? Search, UserRole? Role, AdminUserStatusFilter Status, int Page, int PageSize) : IRequest<Result<AdminUserPage>>;

public sealed record AdminUserListItem(
    Guid Id,
    string? Name,
    string? Email,
    string? Phone,
    UserRole Role,
    string? BusinessName,
    string? VerificationStatus,
    int LiveAdCount,
    bool IsSuspended,
    DateTime CreatedAt);

public sealed record AdminUserPage(IReadOnlyList<AdminUserListItem> Items, int Page, int PageSize, int TotalCount);

/// <summary>Everything the portal's user page shows about one app user.</summary>
public sealed record GetAdminUserDetailQuery(Guid UserId) : IRequest<Result<AdminUserDetail>>;

public sealed record AdminUserDetail(
    Guid Id,
    string? Name,
    string? Email,
    string? Phone,
    UserRole Role,
    string? ProfilePhotoUrl,
    DateTime CreatedAt,
    bool IsSuspended,
    DateTime? SuspendedAt,
    string? SuspensionReason,
    IReadOnlyList<string> SocialLogins,
    AdminSellerSummary? Seller,
    AdminUserStats Stats,
    IReadOnlyList<AdminUserDocument> Documents,
    IReadOnlyList<AdminUserListing> RecentListings);

public sealed record AdminSellerSummary(
    Guid SellerProfileId,
    string BusinessName,
    string? StoreAddress,
    string VerificationStatus,
    DateTime? VerifiedAt,
    DateTime? BusinessVerifiedAt);

public sealed record AdminUserStats(
    int TotalAds,
    int LiveAds,
    int PendingAds,
    int ReviewCount,
    decimal AverageRating,
    int OpenReportsAgainst,
    int Conversations,
    int ActiveSessions);

/// <summary>Unmasked IdNumber - staff need the full number to check it. FileUrl is short-lived.</summary>
public sealed record AdminUserDocument(
    Guid Id,
    UserDocumentType Type,
    IdDocumentType? IdType,
    string? IdNumber,
    string FileName,
    string ContentType,
    string FileUrl,
    UserDocumentStatus Status,
    string? ReviewNote,
    DateTime CreatedAt);

public sealed record AdminUserListing(Guid Id, string Title, decimal? Price, ListingStatus Status, string? ThumbnailUrl, DateTime CreatedAt);

public sealed class AdminUserQueryHandlers :
    IRequestHandler<GetAdminUsersQuery, Result<AdminUserPage>>,
    IRequestHandler<GetAdminUserDetailQuery, Result<AdminUserDetail>>
{
    private static readonly UserRole[] AppRoles = [UserRole.Buyer, UserRole.Seller];

    private readonly IApplicationDbContext _dbContext;
    private readonly IDateTime _dateTime;
    private readonly IProfilePhotoStorage _photoStorage;
    private readonly IUserDocumentStorage _documentStorage;

    public AdminUserQueryHandlers(
        IApplicationDbContext dbContext, IDateTime dateTime, IProfilePhotoStorage photoStorage, IUserDocumentStorage documentStorage)
    {
        _dbContext = dbContext;
        _dateTime = dateTime;
        _photoStorage = photoStorage;
        _documentStorage = documentStorage;
    }

    public async Task<Result<AdminUserPage>> Handle(GetAdminUsersQuery request, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);

        var users = _dbContext.Users.Where(u => AppRoles.Contains(u.Role));

        if (request.Role is { } role)
        {
            users = users.Where(u => u.Role == role);
        }

        users = request.Status switch
        {
            AdminUserStatusFilter.Active => users.Where(u => u.SuspendedAt == null),
            AdminUserStatusFilter.Suspended => users.Where(u => u.SuspendedAt != null),
            AdminUserStatusFilter.DocumentsPending => users.Where(u =>
                _dbContext.UserDocuments.Any(d => d.UserId == u.Id && d.Status == UserDocumentStatus.PendingReview)),
            _ => users
        };

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim().ToLower();
            var businessUserIds = _dbContext.SellerProfiles
                .Where(s => s.BusinessName.ToLower().Contains(term))
                .Select(s => s.UserId);
            users = users.Where(u =>
                (u.Name != null && u.Name.ToLower().Contains(term))
                || (u.Email != null && u.Email.ToLower().Contains(term))
                || (u.Phone != null && u.Phone.Contains(term))
                || businessUserIds.Contains(u.Id));
        }

        var total = await users.CountAsync(cancellationToken);

        var rows = await (
            from u in users
            join s in _dbContext.SellerProfiles on u.Id equals s.UserId into sellers
            from s in sellers.DefaultIfEmpty()
            orderby u.CreatedAt descending, u.Id descending
            select new
            {
                u.Id, u.Name, u.Email, u.Phone, u.Role, u.SuspendedAt, u.CreatedAt,
                SellerId = s == null ? (Guid?)null : s.Id,
                BusinessName = s == null ? null : s.BusinessName,
                Verification = s == null ? (Domain.Accounts.VerificationStatus?)null : s.VerificationStatus
            })
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var sellerIds = rows.Where(r => r.SellerId != null).Select(r => r.SellerId!.Value).ToList();
        var liveCounts = await _dbContext.Listings
            .Where(l => sellerIds.Contains(l.SellerId) && (l.Status == ListingStatus.Active || l.Status == ListingStatus.ExpiringSoon))
            .GroupBy(l => l.SellerId)
            .Select(g => new { SellerId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SellerId, x => x.Count, cancellationToken);

        var items = rows.Select(r => new AdminUserListItem(
                r.Id, r.Name, r.Email, r.Phone, r.Role, r.BusinessName, r.Verification?.ToString(),
                r.SellerId is { } sid ? liveCounts.GetValueOrDefault(sid) : 0,
                r.SuspendedAt != null, r.CreatedAt))
            .ToList();

        return Result.Success(new AdminUserPage(items, page, pageSize, total));
    }

    public async Task<Result<AdminUserDetail>> Handle(GetAdminUserDetailQuery request, CancellationToken cancellationToken)
    {
        var user = await _dbContext.Users.FirstOrDefaultAsync(
            u => u.Id == request.UserId && AppRoles.Contains(u.Role), cancellationToken);
        if (user is null)
        {
            return Result.Failure<AdminUserDetail>(AdminUserErrors.NotFound);
        }

        var seller = await _dbContext.SellerProfiles.FirstOrDefaultAsync(s => s.UserId == user.Id, cancellationToken);

        var listings = seller is null
            ? _dbContext.Listings.Where(_ => false)
            : _dbContext.Listings.Where(l => l.SellerId == seller.Id);
        var totalAds = await listings.CountAsync(cancellationToken);
        var liveAds = await listings.CountAsync(l => l.Status == ListingStatus.Active || l.Status == ListingStatus.ExpiringSoon, cancellationToken);
        var pendingAds = await listings.CountAsync(l => l.Status == ListingStatus.PendingReview, cancellationToken);
        var recent = await listings
            .OrderByDescending(l => l.CreatedAt)
            .Take(8)
            .ToListAsync(cancellationToken);

        var reviews = _dbContext.Reviews.Where(r => r.RevieweeId == user.Id);
        var reviewCount = await reviews.CountAsync(cancellationToken);
        var averageRating = reviewCount == 0 ? 0m : await reviews.AverageAsync(r => (decimal)r.Rating, cancellationToken);

        var listingIds = recent.Select(l => l.Id).ToList();
        var allListingIds = seller is null ? new List<Guid>() : await listings.Select(l => l.Id).ToListAsync(cancellationToken);
        var openReports = await _dbContext.Reports.CountAsync(
            r => r.Status == ReportStatus.Open
                 && ((r.TargetType == ReportTargetType.User && r.TargetId == user.Id)
                     || (r.TargetType == ReportTargetType.Listing && allListingIds.Contains(r.TargetId))),
            cancellationToken);

        var conversations = await _dbContext.Conversations.CountAsync(
            c => c.BuyerId == user.Id || c.SellerId == user.Id, cancellationToken);
        var now = _dateTime.UtcNow;
        var activeSessions = await _dbContext.RefreshTokens.CountAsync(
            t => t.UserId == user.Id && t.RevokedAt == null && t.ExpiresAt > now, cancellationToken);

        var socialLogins = await _dbContext.ExternalLogins
            .Where(e => e.UserId == user.Id)
            .Select(e => e.Provider.ToString())
            .ToListAsync(cancellationToken);

        var documents = await _dbContext.UserDocuments
            .Where(d => d.UserId == user.Id)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(cancellationToken);

        return Result.Success(new AdminUserDetail(
            user.Id,
            user.Name,
            user.Email,
            user.Phone,
            user.Role,
            user.ProfilePhotoS3Key is null ? null : _photoStorage.GetImageUrl(user.ProfilePhotoS3Key),
            user.CreatedAt,
            user.IsSuspended,
            user.SuspendedAt,
            user.SuspensionReason,
            socialLogins,
            seller is null
                ? null
                : new AdminSellerSummary(seller.Id, seller.BusinessName, seller.StoreAddress, seller.VerificationStatus.ToString(), seller.VerifiedAt, seller.BusinessVerifiedAt),
            new AdminUserStats(totalAds, liveAds, pendingAds, reviewCount, Math.Round(averageRating, 2), openReports, conversations, activeSessions),
            documents.Select(d => new AdminUserDocument(
                d.Id, d.Type, d.IdType, d.IdNumber, d.FileName, d.ContentType,
                _documentStorage.GetImageUrl(d.FileS3Key), d.Status, d.ReviewNote, d.CreatedAt)).ToList(),
            recent.Select(l => new AdminUserListing(l.Id, l.Title, l.Price, l.Status, l.MediaUrls.FirstOrDefault(), l.CreatedAt)).ToList()));
    }
}

public static class AdminUserErrors
{
    public static readonly Error NotFound = new("AdminUsers.NotFound", "This user could not be found.");

    public static readonly Error AlreadySuspended = new("AdminUsers.AlreadySuspended", "This account is already suspended.");

    public static readonly Error NotSuspended = new("AdminUsers.NotSuspended", "This account isn't suspended.");

    public static readonly Error DocumentNotFound = new("AdminUsers.DocumentNotFound", "This document could not be found.");

    public static readonly Error DocumentAlreadyReviewed = new("AdminUsers.DocumentAlreadyReviewed", "This document has already been reviewed.");

    public static readonly Error NotASeller = new("AdminUsers.NotASeller", "Only buyer and seller accounts can be verified.");

    public static readonly Error NoSellerProfile = new(
        "AdminUsers.NoSellerProfile", "This account has no business to verify yet - verify the person first.");

    public static readonly Error PersonNotVerified = new(
        "AdminUsers.PersonNotVerified", "Verify the person before their business.");

    public static readonly Error BusinessAlreadyVerified = new("AdminUsers.BusinessAlreadyVerified", "This business is already verified.");

    public static readonly Error BusinessNotVerified = new("AdminUsers.BusinessNotVerified", "This business isn't verified.");

    public static readonly Error BusinessNotReadyToVerify = new(
        "AdminUsers.BusinessNotReadyToVerify",
        "A business needs an approved registration document and an approved proof of office address before it can be verified.");

    public static readonly Error AlreadyVerified = new("AdminUsers.AlreadyVerified", "This seller is already verified.");

    public static readonly Error NotVerified = new("AdminUsers.NotVerified", "This seller isn't verified.");

    public static readonly Error NotReadyToVerify = new(
        "AdminUsers.NotReadyToVerify", "Someone needs a face photo, an approved ID card and an approved utility bill before they can be verified.");
}
