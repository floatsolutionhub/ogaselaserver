using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Listings.Interfaces;

namespace Ogasela.Application.Listings;

/// <summary>
/// Deletes listing photos that were dropped from an ad (see <see cref="ListingPublishService.SubmitAsync"/>,
/// which trims to the plan's PhotoLimit). Scoped per request: SubmitAsync queues the URLs, and the
/// caller calls <see cref="FlushAsync"/> only after its save has committed - deleting first would
/// leave the ad pointing at missing files if the save then failed. A queued URL is only deleted if
/// no listing still uses it (a seller can put the same URL on two ads), and only if our storage
/// uploaded it. Deletion is best effort: a failure is logged and never fails the submission.
/// </summary>
public sealed class ListingMediaCleanup
{
    private readonly IApplicationDbContext _dbContext;
    private readonly IListingImageStorage _storage;
    private readonly ILogger<ListingMediaCleanup> _logger;
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);

    public ListingMediaCleanup(IApplicationDbContext dbContext, IListingImageStorage storage, ILogger<ListingMediaCleanup> logger)
    {
        _dbContext = dbContext;
        _storage = storage;
        _logger = logger;
    }

    public void Queue(IEnumerable<string> urls) => _pending.UnionWith(urls);

    /// <summary>The save didn't happen (e.g. a concurrent settlement won) - forget what was queued.</summary>
    public void Discard() => _pending.Clear();

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        var urls = _pending.ToList();
        _pending.Clear();

        foreach (var url in urls)
        {
            try
            {
                if (await _dbContext.Listings.AnyAsync(l => l.MediaUrls.Contains(url), cancellationToken))
                {
                    continue;
                }

                await _storage.DeleteAsync(url, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Couldn't delete dropped listing photo {Url}; it's left in storage", url);
            }
        }
    }
}
