namespace Ogasela.Application.Listings.Interfaces;

/// <summary>
/// Stores a seller-uploaded listing photo. Unlike category/verification images, listing photos
/// are public marketplace content shown to every buyer indefinitely for as long as the listing
/// runs (which can be weeks) - a time-limited presigned URL would go stale mid-listing, so this
/// returns a stable, non-expiring public URL instead, and the Listing entity persists that URL
/// directly (same "MediaUrls is a list of already-hosted URLs" contract ListingsController
/// already exposes for create/update). Requires the storage prefix this writes to be public-read.
/// </summary>
public interface IListingImageStorage
{
    /// <summary>Uploads the image and returns its permanent public URL.</summary>
    Task<string> UploadAsync(string fileName, string contentType, Stream content, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a photo this storage uploaded. Returns false, without touching anything, for a URL it
    /// didn't produce (MediaUrls can hold any https URL, so a seller-supplied one must never map to a
    /// deletable key). Callers must check no listing still uses the URL first.
    /// </summary>
    Task<bool> DeleteAsync(string url, CancellationToken cancellationToken);
}
