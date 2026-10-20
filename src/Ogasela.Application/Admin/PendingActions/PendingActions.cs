using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Domain.Accounts;
using Ogasela.Domain.Listings;
using Ogasela.Domain.Marketing;
using Ogasela.Domain.Moderation;
using Ogasela.Domain.OfficePickups;
using Ogasela.Domain.Staff;
using Ogasela.Domain.Verification;
using Ogasela.Shared;

namespace Ogasela.Application.Admin.PendingActions;

/// <summary>
/// What the caller may act on, read from their token by the Api layer: SuperAdmins pass every
/// check, legacy Moderators reach the role-gated moderation routes, and everyone else needs the
/// permission the matching action endpoint requires.
/// </summary>
public sealed record AdminAccess(bool IsSuperAdmin, bool IsModerator, IReadOnlySet<string> Permissions)
{
    public bool Can(string permission) => IsSuperAdmin || Permissions.Contains(permission);

    /// <summary>AdminController's moderation and manual-review routes are [Authorize(Roles = "Moderator,SuperAdmin")].</summary>
    public bool CanModerate => IsSuperAdmin || IsModerator;
}

/// <summary>
/// The Control Portal's notification bell: how much work is waiting in each queue the caller can
/// act on. Only queues they can act on are returned (zero counts included), so the portal shows
/// nobody work they couldn't do.
/// </summary>
public sealed record GetPendingActionsQuery(AdminAccess Access) : IRequest<Result<PendingActionsResponse>>;

/// <summary>Key is stable (the portal maps it to an icon and a link); Count is how many items wait.</summary>
public sealed record PendingAction(string Key, int Count);

public sealed record PendingActionsResponse(IReadOnlyList<PendingAction> Items, int Total);

public static class PendingActionKeys
{
    public const string Reports = "reports";
    public const string AdApprovals = "adApprovals";
    public const string ManualReview = "manualReview";
    public const string ChangeRequests = "changeRequests";
    public const string UserDocuments = "userDocuments";
    public const string Disputes = "disputes";
    public const string OfficePickups = "officePickups";
    public const string PickupRefunds = "pickupRefunds";
    public const string StaffApprovals = "staffApprovals";
    public const string WaitlistSignups = "waitlistSignups";
}

public sealed class GetPendingActionsQueryHandler : IRequestHandler<GetPendingActionsQuery, Result<PendingActionsResponse>>
{
    private static readonly OfficePickupStatus[] PickupInProgress =
    [
        OfficePickupStatus.PaymentConfirmed, OfficePickupStatus.ItemReceived, OfficePickupStatus.InspectionPassed,
        OfficePickupStatus.InspectionFailed, OfficePickupStatus.ReadyForCollection, OfficePickupStatus.OutForDelivery
    ];

    private readonly IApplicationDbContext _dbContext;

    public GetPendingActionsQueryHandler(IApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Result<PendingActionsResponse>> Handle(GetPendingActionsQuery request, CancellationToken cancellationToken)
    {
        var access = request.Access;
        var items = new List<PendingAction>();

        async Task AddAsync(bool allowed, string key, Func<Task<int>> count)
        {
            if (allowed)
            {
                items.Add(new PendingAction(key, await count()));
            }
        }

        await AddAsync(access.CanModerate, PendingActionKeys.Reports, () =>
            _dbContext.Reports.CountAsync(r => r.Status == ReportStatus.Open, cancellationToken));
        await AddAsync(access.CanModerate, PendingActionKeys.AdApprovals, () =>
            _dbContext.Listings.CountAsync(l => l.Status == ListingStatus.PendingReview, cancellationToken));
        await AddAsync(access.CanModerate, PendingActionKeys.ManualReview, () =>
            _dbContext.BiometricVerifications.CountAsync(v => v.Decision == VerificationDecision.ManualReview, cancellationToken));
        await AddAsync(access.Can("users.changes.review"), PendingActionKeys.ChangeRequests, () =>
            _dbContext.ProfileChangeRequests.CountAsync(r => r.Status == ProfileChangeStatus.PendingReview, cancellationToken));
        // Per person, not per file - that's what the Users list filters to.
        await AddAsync(access.Can("users.documents.review"), PendingActionKeys.UserDocuments, () =>
            _dbContext.UserDocuments.Where(d => d.Status == UserDocumentStatus.PendingReview)
                .Select(d => d.UserId).Distinct().CountAsync(cancellationToken));
        await AddAsync(access.Can("disputes.manage"), PendingActionKeys.Disputes, () =>
            _dbContext.Disputes.CountAsync(d => d.Status != DisputeStatus.Resolved, cancellationToken));
        // The next pipeline step is staff's (a failed inspection's is a refund); ones the buyer wants
        // cancelled wait on a refund instead - the same split as the Office Pickup page's "wants to cancel" filter.
        await AddAsync(access.Can("officepickup.manage"), PendingActionKeys.OfficePickups, () =>
            _dbContext.OfficePickupRequests.CountAsync(
                p => PickupInProgress.Contains(p.Status) && p.CancellationRequestedAt == null, cancellationToken));
        await AddAsync(access.Can("officepickup.refund"), PendingActionKeys.PickupRefunds, () =>
            _dbContext.OfficePickupRequests.CountAsync(
                p => PickupInProgress.Contains(p.Status) && p.CancellationRequestedAt != null, cancellationToken));
        await AddAsync(access.Can("staff.kyc.approve"), PendingActionKeys.StaffApprovals, () =>
            _dbContext.StaffProfiles.CountAsync(s => s.Status == StaffOnboardingStatus.PendingApproval, cancellationToken));
        await AddAsync(access.Can("marketing.interest.manage"), PendingActionKeys.WaitlistSignups, () =>
            _dbContext.InterestSignups.CountAsync(s => s.Status == InterestSignupStatus.New, cancellationToken));

        return Result.Success(new PendingActionsResponse(items, items.Sum(i => i.Count)));
    }
}
