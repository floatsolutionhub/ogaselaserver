using FluentAssertions;
using Ogasela.Application.Listings.Interfaces;
using Ogasela.Infrastructure.Listings;

namespace Ogasela.UnitTests.Listings;

public class ImageSharpListingImageProcessorTests
{
    [Fact]
    public async Task ProcessAsync_WithAFormatItCantRead_ThrowsUnsupportedListingImageException()
    {
        // e.g. an iPhone HEIC photo - which used to surface as an unhandled 500.
        var notAnImage = new MemoryStream("ftypheic-not-a-decodable-image"u8.ToArray());
        var sut = new ImageSharpListingImageProcessor();

        var act = () => sut.ProcessAsync(notAnImage, "Ogasela • Test", CancellationToken.None);

        await act.Should().ThrowAsync<UnsupportedListingImageException>();
    }
}
