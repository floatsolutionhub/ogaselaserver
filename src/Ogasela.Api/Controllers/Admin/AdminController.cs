using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Ogasela.Api.Common;
using Ogasela.Api.Contracts.Admin;
using Ogasela.Application.Admin.EraseUserData;
using Ogasela.Application.Admin.GetAuditLog;
using Ogasela.Application.Admin.GetPlatformDashboard;
using Ogasela.Application.Moderation.GetModerationQueue;
using Ogasela.Application.Moderation.ListingReview;
using Ogasela.Application.Moderation.ResolveReport;
using Ogasela.Application.Verification.GetManualReviewQueue;

namespace Ogasela.Api.Controllers.Admin;

/// <summary>
/// Every route here is gated to an internal role - SuperAdmin is additionally allowed wherever a
/// narrower role (Moderator, FinanceAdmin) is named, on the assumption a super admin can do
/// anything a more specialized internal role can.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/admin")]
public sealed class AdminController : ControllerBase
{
    private readonly ISender _sender;

    public AdminController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>The unified, priority-ranked queue: open Reports (including system-generated fraud-risk ones) and pending biometric manual-review items in one list, highest priority first.</summary>
    [HttpGet("moderation/queue")]
    [Authorize(Roles = "Moderator,SuperAdmin")]
    public async Task<IActionResult> GetModerationQueue(CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new GetModerationQueueQuery(), cancellationToken);
        return result.ToActionResult(this);
    }

    /// <summary>Resolves an Open report. Approve/Escalate only close out the report itself; Remove additionally pauses the reported Listing (a reported User isn't automatically actioned - see ResolveReportCommandHandler).</summary>
    [HttpPost("moderation/{reportId:guid}/decision")]
    [Authorize(Roles = "Moderator,SuperAdmin")]
    public async Task<IActionResult> ResolveReport(Guid reportId, ResolveReportRequest request, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new ResolveReportCommand(reportId, request.Decision, request.Notes), cancellationToken);
        return result.ToActionResult(this);
    }

    /// <summary>Ads waiting for approval before they go live, oldest first, with any open fraud-risk score.</summary>
    [HttpGet("listings/pending")]
    [Authorize(Roles = "Moderator,SuperAdmin")]
    public async Task<IActionResult> GetPendingListings(CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new GetPendingListingsQuery(), cancellationToken);
        return result.ToActionResult(this);
    }

    /// <summary>Approves a pending ad: it goes live now, and its plan duration starts now.</summary>
    [HttpPost("listings/{id:guid}/approve")]
    [Authorize(Roles = "Moderator,SuperAdmin")]
    public async Task<IActionResult> ApproveListing(Guid id, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new ApproveListingCommand(id), cancellationToken);
        return result.ToActionResult(this);
    }

    /// <summary>Rejects a pending ad back to Draft with a reason the seller sees; a paid plan charge is refunded to the card or account it was paid with (older wallet payments go back to the wallet). If the payment provider refuses the refund, the ad stays in review and the call fails with 502 so it can be retried.</summary>
    [HttpPost("listings/{id:guid}/reject")]
    [Authorize(Roles = "Moderator,SuperAdmin")]
    public async Task<IActionResult> RejectListing(Guid id, RejectListingRequest request, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new RejectListingCommand(id, request.Reason), cancellationToken);
        return result.ToActionResult(this);
    }

    /// <summary>Just the biometric-verification manual-review backlog on its own (oldest first) - see moderation/queue for the combined, priority-ranked view.</summary>
    [HttpGet("verification/manual-review-queue")]
    [Authorize(Roles = "Moderator,SuperAdmin")]
    public async Task<IActionResult> GetManualReviewQueue(CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new GetManualReviewQueueQuery(), cancellationToken);
        return result.ToActionResult(this);
    }

    /// <summary>Platform-wide KPIs: active-user proxy, listings created, plan-purchase revenue by tier, the verification funnel, and ad-boost adoption rate. All figures are computed on demand, not cached.</summary>
    [HttpGet("dashboard")]
    [Authorize(Roles = "FinanceAdmin,SuperAdmin")]
    public async Task<IActionResult> GetDashboard(CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new GetPlatformDashboardQuery(), cancellationToken);
        return result.ToActionResult(this);
    }

    /// <summary>
    /// NDPA data-subject deletion: erases biometric images, anonymizes User/SellerProfile PII,
    /// revokes ad-platform connections, and revokes refresh tokens - Transactions/AuditLogs/
    /// Reviews are kept (anonymized, not deleted) for financial/audit history. Irreversible.
    /// </summary>
    [HttpPost("data-requests/{userId:guid}/erase")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> EraseUserData(Guid userId, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new EraseUserDataCommand(userId), cancellationToken);
        return result.ToActionResult(this);
    }

    /// <summary>Paginated, filterable view of every administrative action ever recorded (report decisions, plan edits, category edits, data erasures, ...). All filters are optional and combine with AND.</summary>
    [HttpGet("audit-log")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> GetAuditLog(
        [FromQuery] Guid? actorId, [FromQuery] string? action, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int page, [FromQuery] int pageSize, CancellationToken cancellationToken)
    {
        var query = new GetAuditLogQuery(actorId, action, from, to, page <= 0 ? 1 : page, pageSize <= 0 ? 20 : pageSize);
        var result = await _sender.Send(query, cancellationToken);
        return result.ToActionResult(this);
    }
}
