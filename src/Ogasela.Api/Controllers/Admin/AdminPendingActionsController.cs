using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Ogasela.Api.Common;
using Ogasela.Application.Admin.PendingActions;

namespace Ogasela.Api.Controllers.Admin;

/// <summary>
/// The Control Portal's notification bell. Any portal account may ask; the counts it gets back are
/// only for queues its own role or permissions let it act on - the same claims the action
/// endpoints check (see PermissionAuthorizationHandler).
/// </summary>
[ApiController]
[Authorize(Roles = "Moderator,FinanceAdmin,SuperAdmin,Staff")]
[Route("api/v1/admin/pending-actions")]
public sealed class AdminPendingActionsController : ControllerBase
{
    private readonly ISender _sender;

    public AdminPendingActionsController(ISender sender)
    {
        _sender = sender;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var access = new AdminAccess(
            IsSuperAdmin: User.HasClaim(ClaimTypes.Role, "SuperAdmin") || User.HasClaim("role_type", "SuperAdmin"),
            IsModerator: User.HasClaim(ClaimTypes.Role, "Moderator"),
            Permissions: User.FindAll("permission").Select(c => c.Value).ToHashSet());
        return (await _sender.Send(new GetPendingActionsQuery(access), cancellationToken)).ToActionResult(this);
    }
}
