using Microsoft.Extensions.Logging;
using Ogasela.Application.Ai;
using Ogasela.Application.Ai.Interfaces;
using Ogasela.Infrastructure.Imaging;
using Ogasela.Shared;
using SkiaSharp;

namespace Ogasela.Infrastructure.Ai;

/// <summary>
/// First-pass IImageEnhancer: real pixel-level cleanup (auto-orient, crop to the visually
/// "interesting" region, brightness/contrast correction) via SkiaSharp - no external API,
/// no credentials, always available. True background removal needs image segmentation, which
/// this doesn't attempt; a service like remove.bg or a cloud vision "salient object" API would
/// plug in here behind the same IImageEnhancer interface without any caller changing.
/// </summary>
public sealed class SkiaImageEnhancer : IImageEnhancer
{
    private const float Brightness = 1.08f;
    private const float Contrast = 1.05f;

    /// <summary>Sobel edge strength (0-1) a pixel needs to count as part of the subject when cropping.</summary>
    private const float EdgeThreshold = 0.5f;

    private readonly IAiImageStorage _imageStorage;
    private readonly ILogger<SkiaImageEnhancer> _logger;

    public SkiaImageEnhancer(IAiImageStorage imageStorage, ILogger<SkiaImageEnhancer> logger)
    {
        _imageStorage = imageStorage;
        _logger = logger;
    }

    public async Task<Result<string>> EnhanceAsync(string imageRef, CancellationToken cancellationToken)
    {
        try
        {
            using var buffer = new MemoryStream();
            await using (var original = await _imageStorage.DownloadAsync(imageRef, cancellationToken))
            {
                await original.CopyToAsync(buffer, cancellationToken);
            }
            buffer.Position = 0;

            using var image = SkiaImaging.DecodeAutoOriented(buffer)
                ?? throw new InvalidDataException("SkiaSharp could not decode the image.");
            using var enhanced = Enhance(image);

            using var output = new MemoryStream();
            SkiaImaging.EncodeJpeg(enhanced, 90, output);
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

    /// <summary>Crops to the edge-detected subject, then brightens and adds a little contrast - in one draw.</summary>
    private static SKBitmap Enhance(SKBitmap image)
    {
        var crop = FindSubjectBounds(image);

        // Brightness multiplies each channel; contrast then scales it about mid-grey:
        // c' = (c * Brightness - 0.5) * Contrast + 0.5  =>  scale c by B*C, offset by 0.5*(1-C).
        // The span overload of CreateColorMatrix takes that offset normalised to 0-1 (the float[]
        // overload wants 0-255 instead).
        const float scale = Brightness * Contrast;
        const float offset = 0.5f * (1 - Contrast);
        using var paint = new SKPaint
        {
            ColorFilter = SKColorFilter.CreateColorMatrix(
            [
                scale, 0, 0, 0, offset,
                0, scale, 0, 0, offset,
                0, 0, scale, 0, offset,
                0, 0, 0, 1, 0
            ])
        };

        var result = SkiaImaging.CreateOpaque(crop.Width, crop.Height);
        using var canvas = new SKCanvas(result);
        canvas.DrawBitmap(image, crop, new SKRect(0, 0, crop.Width, crop.Height), SKSamplingOptions.Default, paint);
        return result;
    }

    /// <summary>
    /// The bounding box of every pixel whose Sobel edge strength reaches EdgeThreshold - the same
    /// idea as ImageSharp's EntropyCrop, which this replaced. Falls back to the whole image when
    /// nothing qualifies (e.g. a flat or very soft photo), so it never crops to nothing.
    /// </summary>
    private static SKRectI FindSubjectBounds(SKBitmap image)
    {
        int width = image.Width, height = image.Height;
        if (width < 3 || height < 3)
        {
            return new SKRectI(0, 0, width, height);
        }

        var pixels = image.GetPixelSpan();
        var luma = new float[width * height];
        for (var i = 0; i < luma.Length; i++)
        {
            var p = i * 4;
            luma[i] = (0.299f * pixels[p] + 0.587f * pixels[p + 1] + 0.114f * pixels[p + 2]) / 255f;
        }

        int left = width, top = height, right = -1, bottom = -1;
        for (var y = 1; y < height - 1; y++)
        {
            var row = y * width;
            for (var x = 1; x < width - 1; x++)
            {
                var i = row + x;
                var gx = luma[i - width + 1] + 2 * luma[i + 1] + luma[i + width + 1]
                    - luma[i - width - 1] - 2 * luma[i - 1] - luma[i + width - 1];
                var gy = luma[i + width - 1] + 2 * luma[i + width] + luma[i + width + 1]
                    - luma[i - width - 1] - 2 * luma[i - width] - luma[i - width + 1];

                if (gx * gx + gy * gy < EdgeThreshold * EdgeThreshold)
                {
                    continue;
                }

                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }
        }

        return right < 0
            ? new SKRectI(0, 0, width, height)
            : new SKRectI(left, top, right + 1, bottom + 1);
    }
}
