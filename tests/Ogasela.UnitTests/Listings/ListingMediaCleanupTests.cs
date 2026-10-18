using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Ogasela.Application.Listings;
using Ogasela.Application.Listings.Interfaces;
using Ogasela.Domain.Listings;
using Ogasela.UnitTests.TestSupport;

namespace Ogasela.UnitTests.Listings;

public class ListingMediaCleanupTests
{
    private sealed class RecordingStorage : IListingImageStorage
    {
        public bool Throw { get; init; }
        public List<string> Deleted { get; } = [];

        public Task<string> UploadAsync(string fileName, string contentType, Stream content, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(string url, CancellationToken cancellationToken)
        {
            if (Throw)
            {
                throw new IOException("storage is down");
            }

            Deleted.Add(url);
            return Task.FromResult(true);
        }
    }

    private static ListingMediaCleanup NewCleanup(TestApplicationDbContext db, RecordingStorage storage) =>
        new(db, storage, NullLogger<ListingMediaCleanup>.Instance);

    private static Listing Draft(params string[] mediaUrls) => Listing.CreateDraft(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "Laptop", "Barely used laptop",
        250_000m, ListingCondition.Used, mediaUrls, DateTime.UtcNow);

    [Fact]
    public void TrimMediaTo_KeepsTheFirstPhotosAndReturnsTheRest()
    {
        var listing = Draft("a", "b", "c", "d");

        listing.TrimMediaTo(2).Should().Equal("c", "d");
        listing.MediaUrls.Should().Equal("a", "b");
        listing.TrimMediaTo(5).Should().BeEmpty();
    }

    [Fact]
    public async Task Flush_DeletesDroppedPhotosNoListingUses_AndKeepsOnesAnotherAdStillShows()
    {
        var db = TestApplicationDbContext.Create();
        db.Listings.Add(Draft("https://cdn/shared.jpg"));
        await db.SaveChangesAsync(CancellationToken.None);
        var storage = new RecordingStorage();
        var cleanup = NewCleanup(db, storage);

        cleanup.Queue(["https://cdn/dropped.jpg", "https://cdn/shared.jpg"]);
        await cleanup.FlushAsync(CancellationToken.None);

        storage.Deleted.Should().Equal("https://cdn/dropped.jpg");
    }

    [Fact]
    public async Task Discard_DeletesNothing()
    {
        var storage = new RecordingStorage();
        var cleanup = NewCleanup(TestApplicationDbContext.Create(), storage);

        cleanup.Queue(["https://cdn/dropped.jpg"]);
        cleanup.Discard();
        await cleanup.FlushAsync(CancellationToken.None);

        storage.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task Flush_WhenStorageFails_DoesNotThrow()
    {
        var cleanup = NewCleanup(TestApplicationDbContext.Create(), new RecordingStorage { Throw = true });
        cleanup.Queue(["https://cdn/dropped.jpg"]);

        await cleanup.Invoking(c => c.FlushAsync(CancellationToken.None)).Should().NotThrowAsync();
    }
}
