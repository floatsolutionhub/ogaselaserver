using System.Text;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Ogasela.Api.Common;
using Ogasela.Application.Admin.InterestSignups;
using Ogasela.Domain.Marketing;

namespace Ogasela.Api.Controllers.Admin;

public sealed record UpdateInterestSignupRequest(InterestSignupStatus? Status, string? Notes);

/// <summary>
/// Waiting-list and tester signups from ogasela.com, for the Control Portal's Waitlist & Testers
/// page. Reading needs marketing.interest.view; changing, exporting or deleting needs
/// marketing.interest.manage. SuperAdmins pass every check.
/// </summary>
[ApiController]
[Authorize(Policy = "perm:marketing.interest.view")]
[Route("api/v1/admin/interest-signups")]
public sealed class AdminInterestSignupsController : ControllerBase
{
    private readonly ISender _sender;

    public AdminInterestSignupsController(ISender sender)
    {
        _sender = sender;
    }

    [HttpGet]
    public async Task<IActionResult> GetSignups(
        [FromQuery] InterestKind? kind,
        [FromQuery] InterestSignupStatus? status,
        [FromQuery] string? search,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        [FromQuery] InterestDevicePlatform? devicePlatform,
        CancellationToken cancellationToken) =>
        (await _sender.Send(
            new GetAdminInterestSignupsQuery(
                kind, status, search, page <= 0 ? 1 : page, pageSize <= 0 ? 25 : pageSize, devicePlatform),
            cancellationToken)).ToActionResult(this);

    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary(CancellationToken cancellationToken) =>
        (await _sender.Send(new GetInterestSignupSummaryQuery(), cancellationToken)).ToActionResult(this);

    [HttpGet("export")]
    [Authorize(Policy = "perm:marketing.interest.manage")]
    public async Task<IActionResult> Export(
        [FromQuery] InterestKind? kind,
        [FromQuery] InterestSignupStatus? status,
        [FromQuery] string? search,
        [FromQuery] InterestDevicePlatform? devicePlatform,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new ExportInterestSignupsQuery(kind, status, search, devicePlatform), cancellationToken);
        if (result.IsFailure)
        {
            return result.ToActionResult(this);
        }

        // UTF-8 BOM so Excel shows names with non-ASCII characters correctly.
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(result.Value)).ToArray();
        var fileName = $"ogasela-{(kind?.ToString().ToLowerInvariant() ?? "interest")}-signups-{DateTime.UtcNow:yyyyMMdd}.csv";
        return File(bytes, "text/csv; charset=utf-8", fileName);
    }

    [HttpPatch("{id:guid}")]
    [Authorize(Policy = "perm:marketing.interest.manage")]
    public async Task<IActionResult> Update(Guid id, UpdateInterestSignupRequest request, CancellationToken cancellationToken) =>
        (await _sender.Send(new UpdateInterestSignupCommand(id, request.Status, request.Notes), cancellationToken))
            .ToActionResult(this);

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "perm:marketing.interest.manage")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        (await _sender.Send(new DeleteInterestSignupCommand(id), cancellationToken)).ToActionResult(this);
}
