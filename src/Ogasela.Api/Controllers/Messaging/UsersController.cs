using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Ogasela.Api.Common;
using Ogasela.Application.Messaging.BlockUser;
using Ogasela.Application.Messaging.GetBlockedUsers;
using Ogasela.Application.Messaging.UnblockUser;

namespace Ogasela.Api.Controllers.Messaging;

[ApiController]
[Authorize]
public sealed class UsersController : ControllerBase
{
    private readonly ISender _sender;

    public UsersController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>The caller's blocked users, most recently blocked first.</summary>
    [HttpGet("api/v1/users/blocked")]
    public async Task<IActionResult> GetBlocked(CancellationToken cancellationToken) =>
        (await _sender.Send(new GetBlockedUsersQuery(), cancellationToken)).ToActionResult(this);

    /// <summary>Blocks another user: neither side can start a new conversation, and messages either way in an existing one are rejected until the caller unblocks them.</summary>
    [HttpPost("api/v1/users/{id:guid}/block")]
    public async Task<IActionResult> Block(Guid id, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new BlockUserCommand(id), cancellationToken);
        return result.ToActionResult(this);
    }

    /// <summary>Lifts the caller's block on another user. Succeeds even if they weren't blocked.</summary>
    [HttpDelete("api/v1/users/{id:guid}/block")]
    public async Task<IActionResult> Unblock(Guid id, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new UnblockUserCommand(id), cancellationToken);
        return result.ToActionResult(this);
    }
}
