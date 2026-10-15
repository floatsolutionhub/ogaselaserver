using Ogasela.Application.Listings.Interfaces;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Ogasela.Infrastructure.Listings;

/// <summary>
/// Re-encodes every listing photo to roughly 60% of its original upload size (a ~40% cut) and
/// stamps a semi-transparent "Ogasela • {seller}" watermark in the bottom-right corner before the
/// photo reaches IListingImageStorage. A flat JPEG quality setting doesn't reliably hit a
/// percentage target - a busy photo and a near-solid one compress very differently at the same
/// quality - so this instead searches downward from a high starting quality until the encoded
/// size is at or under the target, or a quality floor is hit. The watermark font is bundled as an
/// embedded resource (Assets/Fonts/DejaVuSans-Bold.ttf) rather than resolved via SystemFonts,
/// since most minimal container base images ship with no fonts installed at all.
/// </summary>
public sealed class ImageSharpListingImageProcessor : IListingImageProcessor
{
    private const float TargetSizeRatio = 0.60f;
    private const int StartingQuality = 85;
    private const int QualityStep = 10;
    private const int MinQuality = 30;

    private static readonly FontFamily WatermarkFontFamily = LoadEmbeddedFont();

    public async Task<ProcessedListingImage> ProcessAsync(Stream content, string watermarkText, CancellationToken cancellationToken)
    {
        using var originalBuffer = new MemoryStream();
        await content.CopyToAsync(originalBuffer, cancellationToken);
        var originalSizeBytes = originalBuffer.Length;
        originalBuffer.Position = 0;

        Image image;
        try
        {
            image = await Image.LoadAsync(originalBuffer, cancellationToken);
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException)
        {
            throw new UnsupportedListingImageException(ex);
        }

        using var _ = image;

        var fontSize = Math.Max(14f, image.Width / 28f);
        var font = WatermarkFontFamily.CreateFont(fontSize, FontStyle.Bold);

        var textOptions = new RichTextOptions(font)
        {
            Origin = new PointF(image.Width - 16, image.Height - 16),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom
        };

        image.Mutate(ctx => ctx
            .AutoOrient()
            .Paint(canvas => canvas.DrawText(
                textOptions,
                watermarkText,
                Brushes.Solid(Color.White.WithAlpha(0.85f)),
                Pens.Solid(Color.Black.WithAlpha(0.6f), 1.5f))));

        var output = await EncodeAtOrBelowTargetSizeAsync(image, originalSizeBytes, cancellationToken);
        return new ProcessedListingImage(output, "image/jpeg", ".jpg");
    }

    /// <summary>Tries StartingQuality first, then steps down by QualityStep until the encoded size is at or under TargetSizeRatio of the original, or MinQuality is reached (whichever comes first) - the last encode attempted is what's returned either way.</summary>
    private static async Task<MemoryStream> EncodeAtOrBelowTargetSizeAsync(Image image, long originalSizeBytes, CancellationToken cancellationToken)
    {
        var targetBytes = (long)(originalSizeBytes * TargetSizeRatio);

        var output = new MemoryStream();
        for (var quality = StartingQuality; ; quality -= QualityStep)
        {
            output.SetLength(0);
            await image.SaveAsync(output, new JpegEncoder { Quality = quality }, cancellationToken);

            if (output.Length <= targetBytes || quality <= MinQuality)
            {
                break;
            }
        }

        output.Position = 0;
        return output;
    }

    private static FontFamily LoadEmbeddedFont()
    {
        var assembly = typeof(ImageSharpListingImageProcessor).Assembly;
        using var fontStream = assembly.GetManifestResourceStream("Ogasela.Infrastructure.Assets.Fonts.DejaVuSans-Bold.ttf")
            ?? throw new InvalidOperationException("Watermark font embedded resource was not found.");

        var collection = new FontCollection();
        return collection.Add(fontStream);
    }
}
