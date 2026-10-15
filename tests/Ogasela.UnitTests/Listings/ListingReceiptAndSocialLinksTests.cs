using FluentAssertions;
using Ogasela.Application.Listings;
using Ogasela.Application.Listings.CreateDraftListing;
using Ogasela.Domain.Listings;

namespace Ogasela.UnitTests.Listings;

public class ListingReceiptAndSocialLinksTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", true)]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", true)]
    [InlineData("https://m.youtube.com/shorts/abc123", true)]
    [InlineData("https://evil-youtube.com/watch?v=x", false)]
    [InlineData("https://youtube.com.evil.com/watch?v=x", false)]
    [InlineData("youtube.com/watch?v=x", false)]
    [InlineData("javascript:alert(1)", false)]
    public void YouTubeLinks(string? url, bool valid) => ListingSocialLinks.IsYouTube(url).Should().Be(valid);

    [Theory]
    [InlineData("https://vm.tiktok.com/ZMabc/", true)]
    [InlineData("https://www.facebook.com/marketplace/item/1", false)]
    public void TikTokLinks(string url, bool valid) => ListingSocialLinks.IsTikTok(url).Should().Be(valid);

    [Theory]
    [InlineData("https://fb.watch/abc/", true)]
    [InlineData("https://m.facebook.com/story.php?id=1", true)]
    [InlineData("https://instagram.com/p/abc", false)]
    public void FacebookLinks(string url, bool valid) => ListingSocialLinks.IsFacebook(url).Should().Be(valid);

    [Fact]
    public void ALinkOnTheWrongPlatform_FailsValidation()
    {
        var command = new CreateDraftListingCommand(
            "iPhone 13", "", Guid.NewGuid(), 100, ListingCondition.Used, [], null,
            InstagramUrl: "https://www.tiktok.com/@shop/video/1");

        var result = new CreateDraftListingCommandValidator().Validate(command);

        result.Errors.Should().ContainSingle(e => e.PropertyName == "InstagramUrl");
    }

    [Theory]
    [InlineData(ListingCondition.Used, true, true)]
    [InlineData(ListingCondition.Used, false, false)]
    [InlineData(ListingCondition.New, true, null)]
    public void TheReceiptFlag_OnlyStaysOnUsedItems(ListingCondition condition, bool sent, bool? kept)
    {
        var listing = Listing.CreateDraft(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "iPhone 13", "", 100, condition, [], DateTime.UtcNow);

        listing.SetReceiptAndLinks(sent, "  https://vm.tiktok.com/abc ", "  ", null, null, DateTime.UtcNow);

        listing.HasOriginalReceipt.Should().Be(kept);
        listing.TikTokUrl.Should().Be("https://vm.tiktok.com/abc");
        listing.FacebookUrl.Should().BeNull();
    }
}
