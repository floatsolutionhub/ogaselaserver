using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Ogasela.Api.Common;
using Ogasela.Application.Admin.Users;
using Ogasela.Domain.Accounts;

namespace Ogasela.Api.Controllers.Admin;

public sealed record SuspendUserRequest(string Reason);

public sealed record ReviewDocumentRequest(bool Approve, string? Reason);

/// <summary>
/// App users (Buyers/Sellers) for the Control Portal's Users page. Viewing needs users.view;
/// suspending/reactivating/signing out needs users.manage; document review needs
/// users.documents.review. SuperAdmins pass every check.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/admin/users")]
public sealed class AdminUsersController : ControllerBase
{
    private readonly ISender _sender;

    public AdminUsersController(ISender sender)
    {
        _sender = sender;
    }

    [HttpGet]
    [Authorize(Policy = "perm:users.view")]
    public async Task<IActionResult> GetUsers(
        [FromQuery] string? search, [FromQuery] UserRole? role, [FromQuery] AdminUserStatusFilter status,
        [FromQuery] int page, [FromQuery] int pageSize, CancellationToken cancellationToken) =>
        (await _sender.Send(
            new GetAdminUsersQuery(search, role, status, page <= 0 ? 1 : page, pageSize <= 0 ? 25 : pageSize),
            cancellationToken)).ToActionResult(this);

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "perm:users.view")]
    public async Task<IActionResult> GetUser(Guid id, CancellationToken cancellationToken) =>
        (await _sender.Send(new GetAdminUserDetailQuery(id), cancellationToken)).ToActionResult(this);

    [HttpPost("{id:guid}/suspend")]
    [Authorize(Policy = "perm:users.manage")]
    public async Task<IActionResult> Suspend(Guid id, SuspendUserRequest request, CancellationToken cancellationToken) =>
        (await _sender.Send(new SuspendUserCommand(id, request.Reason), cancellationToken)).ToActionResult(this);

    [HttpPost("{id:guid}/reactivate")]
    [Authorize(Policy = "perm:users.manage")]
    public async Task<IActionResult> Reactivate(Guid id, CancellationToken cancellationToken) =>
        (await _sender.Send(new ReactivateUserCommand(id), cancellationToken)).ToActionResult(this);

    [HttpPost("{id:guid}/revoke-sessions")]
    [Authorize(Policy = "perm:users.manage")]
    public async Task<IActionResult> RevokeSessions(Guid id, CancellationToken cancellationToken) =>
        (await _sender.Send(new RevokeUserSessionsCommand(id), cancellationToken)).ToActionResult(this);

    /// <summary>Verify a person by hand (or revoke it, with a reason they see). Verified sellers can publish ads; a verified buyer becomes a seller.</summary>
    [HttpPost("{id:guid}/verification")]
    [Authorize(Policy = "perm:users.verify")]
    public async Task<IActionResult> SetVerification(Guid id, SetSellerVerificationRequest request, CancellationToken cancellationToken) =>
        (await _sender.Send(new SetSellerVerificationCommand(id, request.Verified, request.Reason), cancellationToken))
            .ToActionResult(this);

    /// <summary>Verify a seller's business (approved registration + proof of office address, person already verified),
    /// or revoke it with a reason they see. A trust badge on their shop and ads.</summary>
    [HttpPost("{id:guid}/business-verification")]
    [Authorize(Policy = "perm:users.verify")]
    public async Task<IActionResult> SetBusinessVerification(
        Guid id, SetSellerVerificationRequest request, CancellationToken cancellationToken) =>
        (await _sender.Send(new SetBusinessVerificationCommand(id, request.Verified, request.Reason), cancellationToken))
            .ToActionResult(this);

    [HttpPost("{id:guid}/documents/{documentId:guid}/review")]
    [Authorize(Policy = "perm:users.documents.review")]
    public async Task<IActionResult> ReviewDocument(
        Guid id, Guid documentId, ReviewDocumentRequest request, CancellationToken cancellationToken) =>
        (await _sender.Send(new ReviewUserDocumentCommand(id, documentId, request.Approve, request.Reason), cancellationToken))
            .ToActionResult(this);
}

public sealed record SetSellerVerificationRequest(bool Verified, string? Reason);
