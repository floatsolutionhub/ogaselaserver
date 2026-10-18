using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Ogasela.Application.Listings.Interfaces;
using Ogasela.Infrastructure.Verification;

namespace Ogasela.Infrastructure.Listings;

/// <summary>
/// Writes listing photos to the same S3 bucket Verification/Category images use, under their own
/// "listings" prefix, with a public-read ACL - listing photos are public marketplace content, not
/// a compliance-sensitive document, so (unlike biometric/category images) there's no presigned-URL
/// indirection here. Requires the bucket to permit public ACLs on this prefix (Block Public
/// Access must be off, or scoped to allow it) - if that's not viable in the real AWS account, the
/// alternative is switching MediaUrls to store keys and re-presigning at every read, the way
/// Category images already do.
/// </summary>
public sealed class S3ListingImageStorage : IListingImageStorage
{
    private const string KeyPrefix = "listings";

    private readonly IAmazonS3 _s3Client;
    private readonly AwsSettings _settings;

    public S3ListingImageStorage(IAmazonS3 s3Client, IOptions<AwsSettings> settings)
    {
        _s3Client = s3Client;
        _settings = settings.Value;
    }

    public async Task<string> UploadAsync(string fileName, string contentType, Stream content, CancellationToken cancellationToken)
    {
        var key = $"{KeyPrefix}/{Guid.NewGuid():N}-{Path.GetFileName(fileName)}";

        await _s3Client.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = _settings.S3BucketName,
                Key = key,
                InputStream = content,
                ContentType = contentType,
                CannedACL = S3CannedACL.PublicRead,
                AutoCloseStream = false
            },
            cancellationToken);

        return $"{PublicBaseUrl}{key}";
    }

    public async Task<bool> DeleteAsync(string url, CancellationToken cancellationToken)
    {
        var ownPrefix = $"{PublicBaseUrl}{KeyPrefix}/";
        if (!url.StartsWith(ownPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var key = url[PublicBaseUrl.Length..];
        if (key.Contains("..", StringComparison.Ordinal) || key.Contains('?') || key.Contains('#'))
        {
            return false;
        }

        await _s3Client.DeleteObjectAsync(
            new DeleteObjectRequest { BucketName = _settings.S3BucketName, Key = key }, cancellationToken);
        return true;
    }

    private string PublicBaseUrl => $"https://{_settings.S3BucketName}.s3.{_settings.Region}.amazonaws.com/";
}
