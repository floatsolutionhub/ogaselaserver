using Ogasela.Application.Listings.Interfaces;
using Ogasela.Infrastructure.Imaging;
using SkiaSharp;

namespace Ogasela.Infrastructure.Listings;

/// <summary>
/// Re-encodes every listing photo to roughly 60% of its original upload size (a ~40% cut) and
/// stamps a semi-transparent "Ogasela • {seller}" watermark in the bottom-right corner before the
/// photo reaches IListingImageStorage. A flat JPEG quality setting doesn't reliably hit a
/// percentage target - a busy photo and a near-solid one compress very differently at the same
/// quality - so this instead searches downward from a high starting quality until the encoded
/// size is at or under the target, or a quality floor is hit. The watermark font is bundled as an
/// embedded resource (Assets/Fonts/DejaVuSans-Bold.ttf) rather than resolved from the system,
/// since most minimal container base images ship with no fonts installed at all.
/// </summary>
public sealed class SkiaListingImageProcessor : IListingImageProcessor
{
    private const float TargetSizeRatio = 0.60f;
    private const int StartingQuality = 85;
    private const int QualityStep = 10;
    private const int MinQuality = 30;
    private const float WatermarkMargin = 16f;

    private static readonly SKTypeface WatermarkTypeface = LoadEmbeddedFont();

    public async Task<ProcessedListingImage> ProcessAsync(Stream content, string watermarkText, CancellationToken cancellationToken)
    {
        using var originalBuffer = new MemoryStream();
        await content.CopyToAsync(originalBuffer, cancellationToken);
        var originalSizeBytes = originalBuffer.Length;
        originalBuffer.Position = 0;

        using var image = SkiaImaging.DecodeAutoOriented(originalBuffer)
            ?? throw new UnsupportedListingImageException(
                new InvalidDataException("SkiaSharp could not decode the uploaded image."));

        DrawWatermark(image, watermarkText);

        var output = EncodeAtOrBelowTargetSize(image, originalSizeBytes);
        return new ProcessedListingImage(output, "image/jpeg", ".jpg");
    }

    private static void DrawWatermark(SKBitmap image, string watermarkText)
    {
        using var font = new SKFont(WatermarkTypeface, Math.Max(14f, image.Width / 28f));
        using var fill = new SKPaint { Color = SKColors.White.WithAlpha(217), IsAntialias = true };
        using var outline = new SKPaint
        {
            Color = SKColors.Black.WithAlpha(153),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f
        };

        // Right-aligned on the margin, with the text's descenders (not just its baseline) sitting
        // on the bottom margin.
        var x = image.Width - WatermarkMargin;
        var baseline = image.Height - WatermarkMargin - font.Metrics.Descent;

        using var canvas = new SKCanvas(image);
        canvas.DrawText(watermarkText, x, baseline, SKTextAlign.Right, font, fill);
        canvas.DrawText(watermarkText, x, baseline, SKTextAlign.Right, font, outline);
    }

    /// <summary>Tries StartingQuality first, then steps down by QualityStep until the encoded size is at or under TargetSizeRatio of the original, or MinQuality is reached (whichever comes first) - the last encode attempted is what's returned either way.</summary>
    private static MemoryStream EncodeAtOrBelowTargetSize(SKBitmap image, long originalSizeBytes)
    {
        var targetBytes = (long)(originalSizeBytes * TargetSizeRatio);

        var output = new MemoryStream();
        for (var quality = StartingQuality; ; quality -= QualityStep)
        {
            output.SetLength(0);
            SkiaImaging.EncodeJpeg(image, quality, output);

            if (output.Length <= targetBytes || quality <= MinQuality)
            {
                break;
            }
        }

        output.Position = 0;
        return output;
    }

    private static SKTypeface LoadEmbeddedFont()
    {
        var assembly = typeof(SkiaListingImageProcessor).Assembly;
        using var fontStream = assembly.GetManifestResourceStream("Ogasela.Infrastructure.Assets.Fonts.DejaVuSans-Bold.ttf")
            ?? throw new InvalidOperationException("Watermark font embedded resource was not found.");

        using var fontData = SKData.Create(fontStream);
        return SKTypeface.FromData(fontData)
            ?? throw new InvalidOperationException("Watermark font embedded resource could not be loaded.");
    }
}
