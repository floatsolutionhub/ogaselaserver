using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Ogasela.Application.Ai.Interfaces;
using Ogasela.Infrastructure.Ai;
using SkiaSharp;

namespace Ogasela.UnitTests.Ai;

public class SkiaImageEnhancerTests
{
    private readonly InMemoryAiImageStorage _storage = new();
    private readonly SkiaImageEnhancer _sut;

    public SkiaImageEnhancerTests()
    {
        _sut = new SkiaImageEnhancer(_storage, NullLogger<SkiaImageEnhancer>.Instance);
    }

    [Fact]
    public async Task EnhanceAsync_CropsToTheSubject()
    {
        // A dark 20x20 product on a plain white 200x200 background.
        using var bitmap = new SKBitmap(200, 200);
        bitmap.Erase(SKColors.White);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.DrawRect(new SKRect(90, 90, 110, 110), new SKPaint { Color = SKColors.Black });
        }
        _storage.Files["product.jpg"] = Encode(bitmap);

        var result = await _sut.EnhanceAsync("product.jpg", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        using var enhanced = SKBitmap.Decode(_storage.Files[result.Value]);
        enhanced.Width.Should().BeInRange(20, 30);
        enhanced.Height.Should().BeInRange(20, 30);
    }

    [Fact]
    public async Task EnhanceAsync_BrightensAFlatImage_WithoutCroppingIt()
    {
        using var bitmap = new SKBitmap(64, 64);
        bitmap.Erase(new SKColor(100, 100, 100));
        _storage.Files["flat.jpg"] = Encode(bitmap);

        var result = await _sut.EnhanceAsync("flat.jpg", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        using var enhanced = SKBitmap.Decode(_storage.Files[result.Value]);
        enhanced.Width.Should().Be(64);
        enhanced.Height.Should().Be(64);

        // 100 * 1.08 brightness, then 1.05 contrast about mid-grey: ~107 (allowing for JPEG).
        enhanced.GetPixel(32, 32).Red.Should().BeInRange(104, 110);
    }

    [Fact]
    public async Task EnhanceAsync_WithSomethingThatIsntAnImage_Fails()
    {
        _storage.Files["broken.jpg"] = "not an image"u8.ToArray();

        var result = await _sut.EnhanceAsync("broken.jpg", CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
    }

    private static byte[] Encode(SKBitmap bitmap)
    {
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private sealed class InMemoryAiImageStorage : IAiImageStorage
    {
        public Dictionary<string, byte[]> Files { get; } = new();

        public async Task<string> UploadAsync(string fileName, string contentType, Stream content, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            Files[fileName] = buffer.ToArray();
            return fileName;
        }

        public Task<Stream> DownloadAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream(Files[key]));

        public string GetImageUrl(string key) => $"https://example.test/{key}";
    }
}
