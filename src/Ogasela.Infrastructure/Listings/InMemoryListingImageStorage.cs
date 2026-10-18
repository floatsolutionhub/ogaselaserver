using System.Collections.Concurrent;
using Ogasela.Application.Listings.Interfaces;

namespace Ogasela.Infrastructure.Listings;

/// <summary>Used in place of <see cref="S3ListingImageStorage"/> for local/dev and tests (same "Verification:Provider" != "Aws" switch as the other storage interfaces) - no real AWS credentials needed.</summary>
public sealed class InMemoryListingImageStorage : IListingImageStorage
{
    private const string BaseUrl = "https://mock-storage.local/";

    private readonly ConcurrentDictionary<string, byte[]> _store = new();

    public async Task<string> UploadAsync(string fileName, string contentType, Stream content, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);

        var key = $"mock/listings/{Guid.NewGuid():N}-{Path.GetFileName(fileName)}";
        _store[key] = buffer.ToArray();

        return $"{BaseUrl}{key}";
    }

    public Task<bool> DeleteAsync(string url, CancellationToken cancellationToken)
    {
        if (!url.StartsWith($"{BaseUrl}mock/listings/", StringComparison.Ordinal))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(_store.TryRemove(url[BaseUrl.Length..], out _));
    }

    /// <summary>For tests: whether a photo is still stored.</summary>
    public bool Exists(string url) => url.StartsWith(BaseUrl, StringComparison.Ordinal) && _store.ContainsKey(url[BaseUrl.Length..]);
}
