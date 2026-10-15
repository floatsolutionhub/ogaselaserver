using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Ogasela.Api.Common;
using Ogasela.Application.Settings;

namespace Ogasela.Api.Controllers.Admin;

/// <summary>Control Portal → Settings: ad approvals, the free-ad limit, platform-wide notification switches and the chat safety notice. SuperAdmin only. Plans live on PromotionPlansController.</summary>
[ApiController]
[Authorize(Policy = "SuperAdmin")]
[Route("api/v1/admin/settings")]
public sealed class AdminSettingsController : ControllerBase
{
    private readonly ISender _sender;

    public AdminSettingsController(ISender sender)
    {
        _sender = sender;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        (await _sender.Send(new GetPlatformSettingsQuery(), cancellationToken)).ToActionResult(this);

    /// <summary>Only the fields sent are changed.</summary>
    [HttpPatch]
    public async Task<IActionResult> Update(UpdatePlatformSettingsCommand command, CancellationToken cancellationToken) =>
        (await _sender.Send(command, cancellationToken)).ToActionResult(this);
}
