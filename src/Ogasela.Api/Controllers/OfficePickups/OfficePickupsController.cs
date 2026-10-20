using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Ogasela.Api.Common;
using Ogasela.Application.OfficePickups;

namespace Ogasela.Api.Controllers.OfficePickups;

/// <summary>ReturnUrl: optional page the payment gateway sends the buyer back to - must be on Payments:AllowedReturnUrls.</summary>
public sealed record StartOfficePickupRequest(Guid ListingId, Guid BranchId, string? ReturnUrl = null);

public sealed record CancelOfficePickupRequest(string Reason);

/// <summary>
/// Office pickup from the app: the buyer pays Ogasela for an ad's item plus a 3% fee, the seller
/// drops it at a branch, and the buyer collects it after inspection. Only the buyer and seller
/// can see a request.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/office-pickups")]
public sealed class OfficePickupsController : ControllerBase
{
    private readonly ISender _sender;

    public OfficePickupsController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>Active branches; pass the buyer's latitude/longitude to get them nearest first, with distances.</summary>
    [HttpGet("branches")]
    public async Task<IActionResult> GetBranches([FromQuery] decimal? latitude, [FromQuery] decimal? longitude, CancellationToken cancellationToken) =>
        (await _sender.Send(new GetOfficeBranchesQuery(latitude, longitude), cancellationToken)).ToActionResult(this);

    /// <summary>The price breakdown (kobo) for office pickup of an ad, and whether it's available.</summary>
    [HttpGet("quote/{listingId:guid}")]
    public async Task<IActionResult> GetQuote(Guid listingId, CancellationToken cancellationToken) =>
        (await _sender.Send(new GetOfficePickupQuoteQuery(listingId), cancellationToken)).ToActionResult(this);

    /// <summary>Creates the request and returns the payment page URL. Confirm it when the page closes.</summary>
    [HttpPost]
    public async Task<IActionResult> Start(StartOfficePickupRequest request, CancellationToken cancellationToken) =>
        (await _sender.Send(new StartOfficePickupCommand(request.ListingId, request.BranchId, request.ReturnUrl), cancellationToken)).ToActionResult(this);

    [HttpPost("{id:guid}/confirm-payment")]
    public async Task<IActionResult> ConfirmPayment(Guid id, CancellationToken cancellationToken) =>
        (await _sender.Send(new ConfirmOfficePickupPaymentCommand(id), cancellationToken)).ToActionResult(this);

    /// <summary>The buyer closed the payment page without paying - recorded as cancelled unless the gateway says otherwise.</summary>
    [HttpPost("{id:guid}/cancel-payment")]
    public async Task<IActionResult> CancelPayment(Guid id, CancellationToken cancellationToken) =>
        (await _sender.Send(new CancelOfficePickupPaymentCommand(id), cancellationToken)).ToActionResult(this);

    /// <summary>Paid requests the caller is the buyer or seller on, most recent first.</summary>
    [HttpGet("mine")]
    public async Task<IActionResult> GetMine(CancellationToken cancellationToken) =>
        (await _sender.Send(new GetMyOfficePickupsQuery(), cancellationToken)).ToActionResult(this);

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await _sender.Send(new GetMyOfficePickupQuery(id), cancellationToken)).ToActionResult(this);

    /// <summary>The buyer asks to cancel; staff refund them less the 1% cancellation fee.</summary>
    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> RequestCancellation(Guid id, CancelOfficePickupRequest request, CancellationToken cancellationToken) =>
        (await _sender.Send(new RequestOfficePickupCancellationCommand(id, request.Reason), cancellationToken)).ToActionResult(this);
}
