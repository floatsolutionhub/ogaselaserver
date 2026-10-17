using Ogasela.Shared;

namespace Ogasela.Application.Payments;

/// <summary>
/// A card-payment gateway used to fund a seller's wallet. <c>PaystackGateway</c> and
/// <c>FlutterwaveGateway</c> (Infrastructure) implement this against their respective REST
/// APIs; which one is active is controlled by <c>Payments:Provider</c>.
/// </summary>
public interface IPaymentGateway
{
    /// <summary>The name this gateway is selected/addressed by (e.g. in <c>Payments:Provider</c> and webhook routes).</summary>
    string ProviderName { get; }

    /// <summary>Starts a checkout and returns a reference plus the URL to redirect the client to.</summary>
    Task<Result<PaymentInitialization>> InitializeTransactionAsync(
        Guid sellerId, decimal amountKobo, string purpose, CancellationToken cancellationToken);

    /// <summary>Like <see cref="InitializeTransactionAsync"/>, for a payer who isn't a seller (e.g. a buyer paying for an office pickup).</summary>
    Task<Result<PaymentInitialization>> InitializeCustomerTransactionAsync(
        string customerEmail, decimal amountKobo, string purpose, CancellationToken cancellationToken);

    /// <summary>Asks the gateway directly whether a transaction succeeded - the source of truth a webhook's claim is checked against.</summary>
    Task<Result<PaymentVerificationResult>> VerifyTransactionAsync(string reference, CancellationToken cancellationToken);

    Task<Result> ProcessRefundAsync(string transactionReference, CancellationToken cancellationToken);

    /// <summary>Refunds part of a successful charge back to the payer's card/account.</summary>
    Task<Result> ProcessPartialRefundAsync(string transactionReference, decimal amountKobo, CancellationToken cancellationToken);

    /// <summary>Verifies that a webhook payload genuinely came from this gateway.</summary>
    bool VerifyWebhookSignature(string rawBody, string? signatureHeaderValue);

    /// <summary>Pulls just the transaction reference out of a webhook payload, ahead of calling <see cref="VerifyTransactionAsync"/>.</summary>
    string? ExtractReferenceFromWebhookPayload(string rawBody);
}

public sealed record PaymentInitialization(string Reference, string RedirectUrl);

/// <param name="State">What the payer did: paid, was declined, never paid (closed the page), or is still mid-payment (e.g. a bank transfer).</param>
public sealed record PaymentVerificationResult(
    bool Successful, string Reference, decimal AmountKobo, PaymentAttemptState State = PaymentAttemptState.Pending);

public enum PaymentAttemptState
{
    Succeeded,

    /// <summary>The charge was attempted and declined.</summary>
    Failed,

    /// <summary>The payer left the checkout without paying.</summary>
    Abandoned,

    /// <summary>Still in progress at the gateway or bank - may yet succeed, so it must not be cancelled.</summary>
    Pending
}
