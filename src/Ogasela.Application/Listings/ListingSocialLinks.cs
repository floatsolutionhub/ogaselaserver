using Ogasela.Domain.Listings;

namespace Ogasela.Application.Listings;

/// <summary>
/// Checks the optional social links on an ad: blank is fine, otherwise an http(s) link on that platform's
/// own domain (or a subdomain, e.g. vm.tiktok.com, m.facebook.com) - so an ad can't link buyers off to
/// an arbitrary site.
/// </summary>
public static class ListingSocialLinks
{
    public static bool IsTikTok(string? url) => IsOn(url, "tiktok.com");

    public static bool IsFacebook(string? url) => IsOn(url, "facebook.com", "fb.com", "fb.watch");

    public static bool IsInstagram(string? url) => IsOn(url, "instagram.com", "instagr.am");

    public static bool IsYouTube(string? url) => IsOn(url, "youtube.com", "youtu.be");

    public static string Message(string platform) => $"Enter a full {platform} link (starting with https://), or leave it blank.";

    private static bool IsOn(string? url, params string[] domains)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return true;
        }

        var trimmed = url.Trim();
        if (trimmed.Length > Listing.SocialLinkMaxLength
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        return domains.Any(d => host == d || host.EndsWith("." + d, StringComparison.Ordinal));
    }
}
