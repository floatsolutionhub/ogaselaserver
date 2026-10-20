namespace Ogasela.Application.Payments;

/// <summary>
/// Decides which client-supplied payment return URLs a checkout may send the payer back to. A
/// web client passes its own return page so its payers land back on it; anything not on the
/// configured allow-list (<c>Payments:AllowedReturnUrls</c>) is refused, so a checkout can never
/// be turned into an open redirect to someone else's site.
/// </summary>
public interface IPaymentReturnUrlPolicy
{
    bool IsAllowed(string returnUrl);
}
