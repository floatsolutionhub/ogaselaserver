using SkiaSharp;

namespace Ogasela.Infrastructure.Imaging;

/// <summary>
/// Shared SkiaSharp plumbing for the listing-photo processor and the AI image enhancer: decoding
/// an upload the right way up, and re-encoding it to JPEG. Every bitmap this hands back is opaque
/// RGBA8888 - any transparency (e.g. a PNG) is flattened onto white, since JPEG has no alpha - so
/// callers can read its pixel bytes as R,G,B,A without checking the platform colour type.
/// </summary>
internal static class SkiaImaging
{
    /// <summary>Decodes <paramref name="content"/> (which must be seekable) and applies its EXIF orientation, or returns null if it isn't an image format Skia can read.</summary>
    public static SKBitmap? DecodeAutoOriented(Stream content)
    {
        using var codec = SKCodec.Create(content);
        if (codec is null)
        {
            return null;
        }

        using var decoded = SKBitmap.Decode(codec);
        if (decoded is null)
        {
            return null;
        }

        var origin = codec.EncodedOrigin;
        var swapsAxes = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var width = swapsAxes ? decoded.Height : decoded.Width;
        var height = swapsAxes ? decoded.Width : decoded.Height;

        var oriented = CreateOpaque(width, height);
        using var canvas = new SKCanvas(oriented);
        canvas.Clear(SKColors.White);
        canvas.SetMatrix(OrientationMatrix(origin, width, height));
        canvas.DrawBitmap(decoded, 0, 0, SKSamplingOptions.Default, null);
        return oriented;
    }

    /// <summary>A blank opaque RGBA8888 bitmap - the layout every other method here assumes.</summary>
    public static SKBitmap CreateOpaque(int width, int height) =>
        new(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));

    public static void EncodeJpeg(SKBitmap bitmap, int quality, Stream output)
    {
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, quality)
            ?? throw new InvalidOperationException("SkiaSharp failed to encode the image as JPEG.");
        data.SaveTo(output);
    }

    /// <summary>Maps the stored pixel grid onto the upright one for each EXIF orientation (width/height are the upright dimensions).</summary>
    private static SKMatrix OrientationMatrix(SKEncodedOrigin origin, int width, int height) => origin switch
    {
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, width, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, width, 0, -1, height, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, height, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, width, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, width, -1, 0, height, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, height, 0, 0, 1),
        _ => SKMatrix.Identity
    };
}
