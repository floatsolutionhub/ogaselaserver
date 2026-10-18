using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ogasela.Application.Payments;
using Ogasela.Shared;

namespace Ogasela.IntegrationTests.Payments;

/// <summary>
/// Stands in for both real gateways in tests: no network calls, but a faithful re-implementation
/// of Paystack's actual webhook signature scheme (HMAC-SHA512 of the raw body) against a fixed
/// test secret, so tests can build both validly-signed and deliberately-invalid webhook requests.
/// </summary>
public sealed class FakePaymentGateway : IPaymentGateway
{
    public const string TestWebhookSecret = "integration-test-webhook-secret";

    private readonly ConcurrentDictionary<string, PendingTransaction> _transactions = new();

    public string ProviderName => "Paystack";

    public Task<Result<PaymentInitialization>> InitializeTransactionAsync(
        Guid sellerId, decimal amountKobo, string purpose, CancellationToken cancellationToken)
    {
        var reference = $"fake-{Guid.NewGuid():N}";
        _transactions[reference] = new PendingTransaction(amountKobo, Successful: true);

        return Task.FromResult(Result.Success(
            new PaymentInitialization(reference, $"https://fake-checkout.test/{reference}")));
    }

    public Task<Result<PaymentInitialization>> InitializeCustomerTransactionAsync(
        string customerEmail, decimal amountKobo, string purpose, CancellationToken cancellationToken) =>
        InitializeTransactionAsync(Guid.Empty, amountKobo, purpose, cancellationToken);

    public Task<Result<PaymentVerificationResult>> VerifyTransactionAsync(string reference, CancellationToken cancellationToken)
    {
        if (!_transactions.TryGetValue(reference, out var transaction))
        {
            return Task.FromResult(Result.Failure<PaymentVerificationResult>(PaymentErrors.GatewayError));
        }

        return Task.FromResult(Result.Success(
            new PaymentVerificationResult(transaction.Successful, reference, transaction.AmountKobo, transaction.State)));
    }

    /// <summary>References fully refunded so far, for tests to assert on.</summary>
    public ConcurrentBag<string> FullRefunds { get; } = new();

    public Task<Result> ProcessRefundAsync(string transactionReference, CancellationToken cancellationToken)
    {
        FullRefunds.Add(transactionReference);
        return Task.FromResult(Result.Success());
    }

    /// <summary>Partial refunds requested so far, for tests to assert on (reference to amount in kobo).</summary>
    public ConcurrentDictionary<string, decimal> PartialRefunds { get; } = new();

    public Task<Result> ProcessPartialRefundAsync(string transactionReference, decimal amountKobo, CancellationToken cancellationToken)
    {
        PartialRefunds[transactionReference] = amountKobo;
        return Task.FromResult(Result.Success());
    }

    public bool VerifyWebhookSignature(string rawBody, string? signatureHeaderValue)
    {
        if (string.IsNullOrEmpty(signatureHeaderValue))
        {
            return false;
        }

        return string.Equals(ComputeSignature(rawBody), signatureHeaderValue, StringComparison.OrdinalIgnoreCase);
    }

    public string? ExtractReferenceFromWebhookPayload(string rawBody)
    {
        using var document = JsonDocument.Parse(rawBody);
        return document.RootElement.GetProperty("data").GetProperty("reference").GetString();
    }

    /// <summary>Test-only hook: makes the next verify for this reference report a failed charge.</summary>
    public void MarkVerificationAsFailed(string reference)
    {
        if (_transactions.TryGetValue(reference, out var transaction))
        {
            _transactions[reference] = transaction with { Successful = false, State = PaymentAttemptState.Failed };
        }
    }

    /// <summary>Test-only hook: the payment goes through (again), e.g. after it was abandoned.</summary>
    public void MarkSucceeded(string reference)
    {
        if (_transactions.TryGetValue(reference, out var transaction))
        {
            _transactions[reference] = transaction with { Successful = true, State = PaymentAttemptState.Succeeded };
        }
    }

    /// <summary>Test-only hook: the payer closed the checkout without paying.</summary>
    public void MarkAbandoned(string reference)
    {
        if (_transactions.TryGetValue(reference, out var transaction))
        {
            _transactions[reference] = transaction with { Successful = false, State = PaymentAttemptState.Abandoned };
        }
    }

    public static string ComputeSignature(string rawBody) => Convert.ToHexStringLower(
        HMACSHA512.HashData(Encoding.UTF8.GetBytes(TestWebhookSecret), Encoding.UTF8.GetBytes(rawBody)));

    public static string BuildWebhookBody(string reference) =>
        "{\"event\":\"charge.success\",\"data\":{\"reference\":\"" + reference + "\",\"status\":\"success\"}}";

    private sealed record PendingTransaction(
        decimal AmountKobo, bool Successful, PaymentAttemptState State = PaymentAttemptState.Succeeded);
}
