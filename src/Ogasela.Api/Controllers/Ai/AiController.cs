using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Ogasela.Api.Common;
using Ogasela.Api.Contracts.Ai;
using Ogasela.Application.Ai.EnhanceImage;
using Ogasela.Application.Ai.GenerateDescription;
using Ogasela.Application.Ai.SuggestPrice;

namespace Ogasela.Api.Controllers.Ai;

/// <summary>Seller-facing AI tools, each gated by the given PromotionPlanId's AiToolTier (see AiAccessGuard) - a plan that doesn't include a given tool returns Ai.NotIncludedInPlan.</summary>
[ApiController]
[Authorize]
public sealed class AiController : ControllerBase
{
    private readonly ISender _sender;

    public AiController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>Generates a listing title/description from keywords and/or a photo, via the configured provider (Ai:Provider - Gemini by default). At least one of Keywords/ImageRef should be supplied.</summary>
    [HttpPost("api/v1/ai/generate-description")]
    public async Task<IActionResult> GenerateDescription(GenerateDescriptionRequest request, CancellationToken cancellationToken)
    {
        var command = new GenerateDescriptionCommand(request.PromotionPlanId, request.Keywords, request.ImageRef);
        var result = await _sender.Send(command, cancellationToken);
        return result.ToActionResult(this);
    }

    /// <summary>Suggests a price based on recently-sold comparable listings in the same category/condition. Returns Ai.NotEnoughComparableData if too few comparables exist.</summary>
    [HttpPost("api/v1/ai/suggest-price")]
    public async Task<IActionResult> SuggestPrice(SuggestPriceRequest request, CancellationToken cancellationToken)
    {
        var command = new SuggestPriceCommand(request.PromotionPlanId, request.CategoryId, request.Title, request.Condition);
        var result = await _sender.Send(command, cancellationToken);
        return result.ToActionResult(this);
    }

    /// <summary>Runs a real (non-AI) image enhancement pass (SkiaSharp: auto-orient, crop to subject, brightness/contrast) on an uploaded photo and returns the processed image's storage reference. multipart/form-data, up to 20MB.</summary>
    [HttpPost("api/v1/ai/enhance-image")]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> EnhanceImage([FromForm] EnhanceImageRequest request, CancellationToken cancellationToken)
    {
        await using var imageStream = request.Image.OpenReadStream();

        var command = new EnhanceImageCommand(
            request.PromotionPlanId, imageStream, request.Image.FileName, request.Image.ContentType);

        var result = await _sender.Send(command, cancellationToken);
        return result.ToActionResult(this);
    }
}
