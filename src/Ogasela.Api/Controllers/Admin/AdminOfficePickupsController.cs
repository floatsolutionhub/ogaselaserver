using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Ogasela.Api.Common;
using Ogasela.Application.Admin.OfficePickups;
using Ogasela.Domain.OfficePickups;

namespace Ogasela.Api.Controllers.Admin;

public sealed record UpdateOfficePickupStatusRequest(OfficePickupStatus Status, string? Note, string? DeliveryAddress);

public sealed record RefundOfficePickupRequest(string Reason);

public sealed record SaveOfficeBranchRequest(
    string Name, string Address, string State, string? City, string? Phone, string? OpeningHours,
    decimal Latitude, decimal Longitude, bool IsActive);

public sealed record SetOfficePickupCategoryRequest(bool Eligible);

/// <summary>
/// The Control Portal's Office Pickup section. Viewing needs officepickup.view; moving a request
/// along needs officepickup.manage; refunds need officepickup.refund; branches and eligible
/// categories need officepickup.setup.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/admin/office-pickups")]
public sealed class AdminOfficePickupsController : ControllerBase
{
    private readonly ISender _sender;

    public AdminOfficePickupsController(ISender sender)
    {
        _sender = sender;
    }

    [HttpGet]
    [Authorize(Policy = "perm:officepickup.view")]
    public async Task<IActionResult> GetRequests(
        [FromQuery] OfficePickupStatus? status, [FromQuery] Guid? branchId, [FromQuery] bool cancellationRequested,
        [FromQuery] bool includeUnpaid, [FromQuery] string? search, [FromQuery] int page, [FromQuery] int pageSize,
        CancellationToken cancellationToken) =>
        (await _sender.Send(
            new GetAdminOfficePickupsQuery(status, branchId, cancellationRequested, includeUnpaid, search,
                page <= 0 ? 1 : page, pageSize <= 0 ? 25 : pageSize),
            cancellationToken)).ToActionResult(this);

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "perm:officepickup.view")]
    public async Task<IActionResult> GetRequest(Guid id, CancellationToken cancellationToken) =>
        (await _sender.Send(new GetAdminOfficePickupQuery(id), cancellationToken)).ToActionResult(this);

    [HttpPost("{id:guid}/status")]
    [Authorize(Policy = "perm:officepickup.manage")]
    public async Task<IActionResult> UpdateStatus(Guid id, UpdateOfficePickupStatusRequest request, CancellationToken cancellationToken) =>
        (await _sender.Send(new UpdateOfficePickupStatusCommand(id, request.Status, request.Note, request.DeliveryAddress), cancellationToken))
        .ToActionResult(this);

    [HttpPost("{id:guid}/refund")]
    [Authorize(Policy = "perm:officepickup.refund")]
    public async Task<IActionResult> Refund(Guid id, RefundOfficePickupRequest request, CancellationToken cancellationToken) =>
        (await _sender.Send(new RefundOfficePickupCommand(id, request.Reason), cancellationToken)).ToActionResult(this);

    [HttpGet("branches")]
    [Authorize(Policy = "perm:officepickup.view")]
    public async Task<IActionResult> GetBranches(CancellationToken cancellationToken) =>
        (await _sender.Send(new GetAdminOfficeBranchesQuery(), cancellationToken)).ToActionResult(this);

    [HttpPost("branches")]
    [Authorize(Policy = "perm:officepickup.setup")]
    public async Task<IActionResult> CreateBranch(SaveOfficeBranchRequest request, CancellationToken cancellationToken) =>
        (await _sender.Send(ToCommand(null, request), cancellationToken)).ToActionResult(this);

    [HttpPut("branches/{id:guid}")]
    [Authorize(Policy = "perm:officepickup.setup")]
    public async Task<IActionResult> UpdateBranch(Guid id, SaveOfficeBranchRequest request, CancellationToken cancellationToken) =>
        (await _sender.Send(ToCommand(id, request), cancellationToken)).ToActionResult(this);

    [HttpGet("categories")]
    [Authorize(Policy = "perm:officepickup.view")]
    public async Task<IActionResult> GetCategories(CancellationToken cancellationToken) =>
        (await _sender.Send(new GetOfficePickupCategoriesQuery(), cancellationToken)).ToActionResult(this);

    [HttpPut("categories/{id:guid}")]
    [Authorize(Policy = "perm:officepickup.setup")]
    public async Task<IActionResult> SetCategory(Guid id, SetOfficePickupCategoryRequest request, CancellationToken cancellationToken) =>
        (await _sender.Send(new SetOfficePickupCategoryCommand(id, request.Eligible), cancellationToken)).ToActionResult(this);

    private static SaveOfficeBranchCommand ToCommand(Guid? id, SaveOfficeBranchRequest r) =>
        new(id, r.Name, r.Address, r.State, r.City, r.Phone, r.OpeningHours, r.Latitude, r.Longitude, r.IsActive);
}
