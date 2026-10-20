using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Ogasela.Api.Common;
using Ogasela.Api.Contracts.Listings;
using Ogasela.Application.Listings.Checkout;
using Ogasela.Application.Listings.CreateDraftListing;
using Ogasela.Application.Listings.DeleteListing;
using Ogasela.Application.Listings.GetHotPicks;
using Ogasela.Application.Listings.GetListing;
using Ogasela.Application.Listings.GetSellerListings;
using Ogasela.Application.Listings.MarkSoldListing;
using Ogasela.Application.Listings.PauseListing;
using Ogasela.Application.Listings.PublishListing;
using Ogasela.Application.Listings.RepostListing;
using Ogasela.Application.Listings.ResubmitListing;
using Ogasela.Application.Listings.UpdateListing;
using Ogasela.Application.Listings.UploadListingImage;

namespace Ogasela.Api.Controllers.Listings;

/// <summary>Listing CRUD and lifecycle transitions (Draft -&gt; Active -&gt; Paused/Sold/Expired). Reading a listing is public; every write requires the caller to own it.</summary>
[ApiController]
public sealed class ListingsController : ControllerBase
{
    private readonly ISender _sender;

    public ListingsController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>Creates a new listing as a Draft. Nothing here is validated against the chosen PromotionPlan's limits (photo count, free-tier eligibility, etc.) until Publish is called.</summary>
    [HttpPost("api/v1/listings")]
    [Authorize]
    public async Task<IActionResult> CreateDraft(CreateListingRequest request, CancellationToken cancellationToken)
    {
        var command = new CreateDraftListingCommand(
            request.Title, request.Description, request.CategoryId, request.Price, request.Condition,
            request.MediaUrls ?? [], request.PromotionPlanId, request.Location, request.Latitude, request.Longitude,
            request.HasOriginalReceipt, request.TikTokUrl, request.FacebookUrl, request.InstagramUrl, request.YouTubeUrl);

        var result = await _sender.Send(command, cancellationToken);
        return result.ToActionResult(this);
    }

    /// <summary>
    /// Uploads a single photo (multipart/form-data, up to 10MB), compresses it and stamps an
    /// "Ogasela • {seller's business name}" watermark on it, then returns its permanent public
    /// URL - call this once per photo, then pass the returned URLs (at most 5) in
    /// CreateListingRequest/UpdateListingRequest.MediaUrls.
    /// </summary>
    [HttpPost("api/v1/listings/images")]
    [Authorize]
    [RequestSizeLimit(10_000_000)]
    public async Task<IActionResult> UploadImage([FromForm] UploadListingImageRequest request, CancellationToken cancellationToken)
    {
        await using var stream = request.Image.OpenReadStream();
        var command = new UploadListingImageCommand(stream, request.Image.FileName, request.Image.ContentType);
        var result = await _sender.Send(command, cancellationToken);
        return result.ToActionResult(this);
    }

    /// <summary>
    /// Pays for a Draft listing's plan and submits it for review. Every rule but payment is checked
    /// first. Free/Wallet settle now (Outcome Submitted). Card returns PaymentRequired with a
    /// RedirectUrl to the gateway; the listing stays a Draft until confirm (or the webhook) settles it.
    /// </summary>
    [HttpPost("api/v1/listings/{id:guid}/checkout")]
    [Authorize]
    public async Task<IActionResult> Checkout(Guid id, ListingCheckoutRequest request, CancellationToken cancellationToken) =>
        (await _sender.Send(new StartListingCheckoutCommand(id, request.PromotionPlanId, request.PaymentMethod, request.ReturnUrl), cancellationToken))
            .ToActionResult(this);

    /// <summary>Call on returning from the gateway: verifies the card payment and, once paid, submits the listing. Safe to repeat.</summary>
    [HttpPost("api/v1/listings/{id:guid}/checkout/{reference}/confirm")]
    [Authorize]
    public async Task<IActionResult> ConfirmCheckout(Guid id, string reference, CancellationToken cancellationToken) =>
        (await _sender.Send(new ConfirmListingPaymentCommand(id, reference), cancellationToken)).ToActionResult(this);

    /// <summary>The seller closed the payment page without paying - recorded as cancelled unless the gateway says it was paid or is still in progress.</summary>
    [HttpPost("api/v1/listings/{id:guid}/checkout/{reference}/cancel")]
    [Authorize]
    public async Task<IActionResult> CancelCheckoutPayment(Guid id, string reference, CancellationToken cancellationToken) =>
        (await _sender.Send(new CancelListingPaymentCommand(id, reference), cancellationToken)).ToActionResult(this);

    /// <summary>
    /// Moves a live ad (or one whose edit is in review or was sent back) onto a higher plan. The
    /// full price of the new plan is charged; once paid, its duration starts counting immediately
    /// and the old expiry date is dropped. Downgrades are refused. Card payments settle through the
    /// same confirm/cancel endpoints as checkout.
    /// </summary>
    [HttpPost("api/v1/listings/{id:guid}/upgrade")]
    [Authorize]
    public async Task<IActionResult> UpgradePlan(Guid id, PlanUpgradeRequest request, CancellationToken cancellationToken) =>
        (await _sender.Send(new StartPlanUpgradeCommand(id, request.PromotionPlanId, request.PaymentMethod, request.ReturnUrl), cancellationToken))
            .ToActionResult(this);

    /// <summary>Sends a fixed, rejected edit of a live ad back for review - free, its plan period keeps running.</summary>
    [HttpPost("api/v1/listings/{id:guid}/resubmit")]
    [Authorize]
    public async Task<IActionResult> Resubmit(Guid id, CancellationToken cancellationToken) =>
        (await _sender.Send(new ResubmitListingCommand(id), cancellationToken)).ToActionResult(this);

    /// <summary>Deletes a Draft (including rejected), PendingReview or Expired ad. A paid plan isn't refunded.</summary>
    [HttpDelete("api/v1/listings/{id:guid}")]
    [Authorize]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        (await _sender.Send(new DeleteListingCommand(id), cancellationToken)).ToActionResult(this);

    /// <summary>
    /// Replaces a listing's editable fields - a Draft, or a live/expired ad. Editing an approved ad
    /// sends it back to review (when approval is on); its plan can't be changed here (see upgrade).
    /// </summary>
    [HttpPut("api/v1/listings/{id:guid}")]
    [Authorize]
    public async Task<IActionResult> Update(Guid id, UpdateListingRequest request, CancellationToken cancellationToken)
    {
        var command = new UpdateListingCommand(
            id, request.Title, request.Description, request.CategoryId, request.Price, request.Condition,
            request.MediaUrls ?? [], request.PromotionPlanId, request.Location, request.Latitude, request.Longitude,
            request.HasOriginalReceipt, request.TikTokUrl, request.FacebookUrl, request.InstagramUrl, request.YouTubeUrl);

        var result = await _sender.Send(command, cancellationToken);
        return result.ToActionResult(this);
    }

    /// <summary>
    /// Publishes a Draft listing: requires the seller to be biometrically Verified and a
    /// PromotionPlan to be selected; debits the wallet for a paid plan (fails cleanly, listing
    /// stays Draft, if the balance is insufficient). Also runs Phase 9's fraud-risk check, which
    /// can open a moderation Report but never blocks publishing.
    /// </summary>
    [HttpPost("api/v1/listings/{id:guid}/publish")]
    [Authorize]
    public async Task<IActionResult> Publish(Guid id, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new PublishListingCommand(id), cancellationToken);
        return result.ToActionResult(this);
    }

    /// <summary>
    /// Public - the home screen's Hot Picks: up to 50 live ads buyers are engaging with most, mixed
    /// across categories, paged. categoryId narrows it to one category (a top-level one includes its
    /// subcategories). location narrows it to ads from that place (e.g. the city the user picked).
    /// Pass the same seed on every page of one visit; a new seed reshuffles the mix.
    /// </summary>
    [HttpGet("api/v1/listings/hot")]
    public async Task<IActionResult> GetHotPicks(
        [FromQuery] Guid? categoryId, [FromQuery] int page, [FromQuery] int pageSize, [FromQuery] int seed,
        [FromQuery] string? location, CancellationToken cancellationToken)
    {
        var query = new GetHotPicksQuery(
            categoryId, page <= 0 ? 1 : page, pageSize <= 0 ? 10 : Math.Min(pageSize, 50), seed, location);
        var result = await _sender.Send(query, cancellationToken);
        return result.ToActionResult(this);
    }

    /// <summary>Public - no authentication required. Returns any listing regardless of status.</summary>
    [HttpGet("api/v1/listings/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new GetListingQuery(id), cancellationToken);
        return result.ToActionResult(this);
    }

    /// <summary>Public - a seller's own storefront/listing history. id is the SellerProfile id, not the User id.</summary>
    [HttpGet("api/v1/sellers/{id:guid}/listings")]
    public async Task<IActionResult> GetSellerListings(
        Guid id, [FromQuery] int page, [FromQuery] int pageSize, CancellationToken cancellationToken)
    {
        var query = new GetSellerListingsQuery(
            id, page <= 0 ? 1 : page, pageSize <= 0 ? 20 : pageSize);

        var result = await _sender.Send(query, cancellationToken);
        return result.ToActionResult(this);
    }

    /// <summary>Takes an Active or ExpiringSoon listing off public view. Use repost to bring it back.</summary>
    [HttpPost("api/v1/listings/{id:guid}/pause")]
    [Authorize]
    public async Task<IActionResult> Pause(Guid id, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new PauseListingCommand(id), cancellationToken);
        return result.ToActionResult(this);
    }

    /// <summary>Marks an Active, ExpiringSoon, or Paused listing as Sold. This is terminal - a sold listing can't be reposted.</summary>
    [HttpPost("api/v1/listings/{id:guid}/mark-sold")]
    [Authorize]
    public async Task<IActionResult> MarkSold(Guid id, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new MarkSoldCommand(id), cancellationToken);
        return result.ToActionResult(this);
    }

    /// <summary>Republishes an Expired, Sold, or Paused listing, re-running the same plan/payment/verification checks as an initial publish.</summary>
    [HttpPost("api/v1/listings/{id:guid}/repost")]
    [Authorize]
    public async Task<IActionResult> Repost(Guid id, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new RepostListingCommand(id), cancellationToken);
        return result.ToActionResult(this);
    }
}
