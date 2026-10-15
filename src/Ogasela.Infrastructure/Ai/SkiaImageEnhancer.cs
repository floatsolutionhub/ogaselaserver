using Microsoft.Extensions.Logging;
using Ogasela.Application.Ai;
using Ogasela.Application.Ai.Interfaces;
using Ogasela.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Ogasela.Infrastructure.Ai;

/// <summary>
/// First-pass IImageEnhancer: real pixel-level cleanup (auto-orient, crop to the visually
/// "interesting" region, brightness/contrast correction) via ImageSharp - no external API,
/// no credentials, always available. True background removal needs image segmentation, which
/// this doesn't attempt; a service like remove.bg or a cloud vision "salient object" API would
/// plug in here behind the same IImageEnhancer interface without any caller changing.
/// </summary>
public sealed class ImageSharpEnhancer : IImageEnhancer
{
    private readonly IAiImageStorage _imageStorage;
    private readonly ILogger<ImageSharpEnhancer> _logger;

    public ImageSharpEnhancer(IAiImageStorage imageStorage, ILogger<ImageSharpEnhancer> logger)
    {
        _imageStorage = imageStorage;
        _logger = logger;
    }

    public async Task<Result<string>> EnhanceAsync(string imageRef, CancellationToken cancellationToken)
    {
        try
        {
            await using var original = await _imageStorage.DownloadAsync(imageRef, cancellationToken);
            using var image = await Image.LoadAsync(original, cancellationToken);

            image.Mutate(ctx => ctx
                .AutoOrient()
                .EntropyCrop()
                .Brightness(1.08f)
                .Contrast(1.05f));

            using var output = new MemoryStream();
            await image.SaveAsync(output, new JpegEncoder { Quality = 90 }, cancellationToken);
            output.Position = 0;

            var enhancedKey = await _imageStorage.UploadAsync(
                $"enhanced-{Path.GetFileNameWithoutExtension(imageRef)}.jpg", "image/jpeg", output, cancellationToken);

            return Result.Success(enhancedKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enhance image {ImageRef}", imageRef);
            return Result.Failure<string>(AiErrors.EnhancementFailed);
        }
    }
}
