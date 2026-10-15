using FluentAssertions;
using Ogasela.Application.Listings.Interfaces;
using Ogasela.Infrastructure.Listings;
using SkiaSharp;

namespace Ogasela.UnitTests.Listings;

public class SkiaListingImageProcessorTests
{
    private readonly SkiaListingImageProcessor _sut = new();

    [Fact]
    public async Task ProcessAsync_WithAFormatItCantRead_ThrowsUnsupportedListingImageException()
    {
        // e.g. an iPhone HEIC photo - which used to surface as an unhandled 500.
        var notAnImage = new MemoryStream("ftypheic-not-a-decodable-image"u8.ToArray());

        var act = () => _sut.ProcessAsync(notAnImage, "Ogasela • Test", CancellationToken.None);

        await act.Should().ThrowAsync<UnsupportedListingImageException>();
    }

    [Fact]
    public async Task ProcessAsync_ReturnsAJpegOfTheSameSize_WithTheWatermarkInTheBottomRightCorner()
    {
        var upload = new MemoryStream(SolidJpeg(600, 400, new SKColor(40, 90, 160)));

        var result = await _sut.ProcessAsync(upload, "Ogasela • Test Seller", CancellationToken.None);

        result.ContentType.Should().Be("image/jpeg");
        result.FileExtension.Should().Be(".jpg");
        using var output = SKBitmap.Decode(result.Content);
        output.Width.Should().Be(600);
        output.Height.Should().Be(400);

        // The near-white watermark fill lands in the bottom-right; the top-left stays the photo.
        CountNearWhitePixels(output, new SKRectI(300, 340, 600, 400)).Should().BeGreaterThan(50);
        CountNearWhitePixels(output, new SKRectI(0, 0, 300, 200)).Should().Be(0);
    }

    [Fact]
    public async Task ProcessAsync_AppliesTheExifOrientation()
    {
        // Phone cameras store portrait photos sideways plus an EXIF "rotate 90°" flag (6).
        var sideways = WithExifOrientation(SolidJpeg(300, 200, SKColors.Gray), orientation: 6);

        var result = await _sut.ProcessAsync(new MemoryStream(sideways), "Ogasela • Test", CancellationToken.None);

        using var output = SKBitmap.Decode(result.Content);
        output.Width.Should().Be(200);
        output.Height.Should().Be(300);
    }

    internal static byte[] SolidJpeg(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95);
        return data.ToArray();
    }

    private static int CountNearWhitePixels(SKBitmap bitmap, SKRectI area)
    {
        var count = 0;
        for (var y = area.Top; y < area.Bottom; y++)
        {
            for (var x = area.Left; x < area.Right; x++)
            {
                var p = bitmap.GetPixel(x, y);
                if (p.Red > 200 && p.Green > 200 && p.Blue > 200)
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>Splices a minimal EXIF APP1 segment holding just the Orientation tag in right after the JPEG's SOI marker.</summary>
    private static byte[] WithExifOrientation(byte[] jpeg, ushort orientation)
    {
        byte[] exif =
        [
            0xFF, 0xE1, 0x00, 0x22,                         // APP1, segment length 34
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'M', (byte)'M', 0x00, 0x2A,               // big-endian TIFF header
            0x00, 0x00, 0x00, 0x08,                         // first IFD at offset 8
            0x00, 0x01,                                     // 1 entry
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, // Orientation, SHORT, count 1
            (byte)(orientation >> 8), (byte)orientation, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00                          // no next IFD
        ];

        return [.. jpeg[..2], .. exif, .. jpeg[2..]];
    }
}
