using Microsoft.Extensions.Options;
using Ogasela.Application.Payments;

namespace Ogasela.Infrastructure.Payments;

public sealed class PaymentReturnUrlSettings
{
    public const string SectionName = "Payments";

    /// <summary>
    /// Return pages clients may ask the gateway to send payers back to, e.g.
    /// "https://market.ogasela.com/payments/return". A request matches an entry when its scheme,
    /// host and port are identical and its path is the entry's path or below it. Empty (the
    /// default) allows none - every checkout uses the gateway's configured callback.
    /// </summary>
    public string[] AllowedReturnUrls { get; init; } = [];
}

/// <inheritdoc cref="IPaymentReturnUrlPolicy"/>
public sealed class PaymentReturnUrlPolicy : IPaymentReturnUrlPolicy
{
    private readonly Uri[] _allowed;

    public PaymentReturnUrlPolicy(IOptions<PaymentReturnUrlSettings> settings)
    {
        _allowed = settings.Value.AllowedReturnUrls
            .Select(entry => Uri.TryCreate(entry.Trim(), UriKind.Absolute, out var uri) ? uri : null)
            .OfType<Uri>()
            .ToArray();
    }

    public bool IsAllowed(string returnUrl)
    {
        if (returnUrl.Length > 2048 || !Uri.TryCreate(returnUrl, UriKind.Absolute, out var candidate))
        {
            return false;
        }

        // No credentials-in-URL tricks, and only web schemes.
        if (!string.IsNullOrEmpty(candidate.UserInfo)
            || (candidate.Scheme != Uri.UriSchemeHttps && candidate.Scheme != Uri.UriSchemeHttp))
        {
            return false;
        }

        return _allowed.Any(allowed =>
            string.Equals(allowed.Scheme, candidate.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(allowed.Host, candidate.Host, StringComparison.OrdinalIgnoreCase)
            && allowed.Port == candidate.Port
            && IsSameOrBelow(candidate.AbsolutePath, allowed.AbsolutePath));
    }

    private static bool IsSameOrBelow(string path, string allowedPath)
    {
        var root = allowedPath.TrimEnd('/');
        return root.Length == 0
            || string.Equals(path.TrimEnd('/'), root, StringComparison.Ordinal)
            || path.StartsWith(root + "/", StringComparison.Ordinal);
    }
}
