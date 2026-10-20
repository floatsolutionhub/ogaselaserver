using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Payments;
using Ogasela.Shared;

namespace Ogasela.Infrastructure.Payments;

/// <summary>
/// <see cref="IPaymentGateway"/> backed by Flutterwave's v3 REST API
/// (https://developer.flutterwave.com/docs/making-payments, /docs/direct-refund,
/// /docs/integration-guides/webhooks). Unlike Paystack, Flutterwave's API works in whole NGN,
/// not kobo, so every amount is divided/multiplied by 100 at the boundary.
/// </summary>
public sealed class FlutterwaveGateway : IPaymentGateway
{
    public const string Name = "Flutterwave";

    private readonly HttpClient _httpClient;
    private readonly IApplicationDbContext _dbContext;
    private readonly FlutterwaveSettings _settings;
    private readonly ILogger<FlutterwaveGateway> _logger;

    public FlutterwaveGateway(
        HttpClient httpClient, IApplicationDbContext dbContext, IOptions<FlutterwaveSettings> settings,
        ILogger<FlutterwaveGateway> logger)
    {
        _httpClient = httpClient;
        _dbContext = dbContext;
        _settings = settings.Value;
        _logger = logger;

        _httpClient.BaseAddress = new Uri(_settings.BaseUrl.TrimEnd('/') + "/");
        _httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.SecretKey);
    }

    public string ProviderName => Name;

    public async Task<Result<PaymentInitialization>> InitializeTransactionAsync(
        Guid sellerId, decimal amountKobo, string purpose, string? callbackUrl, CancellationToken cancellationToken) =>
        await InitializeCustomerTransactionAsync(
            await ResolveSellerEmailAsync(sellerId, cancellationToken), amountKobo, purpose, callbackUrl, cancellationToken);

    public async Task<Result<PaymentInitialization>> InitializeCustomerTransactionAsync(
        string email, decimal amountKobo, string purpose, string? callbackUrl, CancellationToken cancellationToken)
    {
        var reference = $"ogasela-{Guid.NewGuid():N}";

        var payload = new InitializeRequest(
            reference, ToNaira(amountKobo), "NGN", callbackUrl ?? _settings.RedirectUrl, new Customer(email));

        using var response = await _httpClient.PostAsJsonAsync("payments", payload, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Never log the raw response body here or below - Flutterwave's payload can carry
            // customer email/card metadata even off the happy path.
            _logger.LogError("Flutterwave initialize failed with status {StatusCode}", (int)response.StatusCode);
            return Result.Failure<PaymentInitialization>(PaymentErrors.GatewayError);
        }

        var parsed = JsonSerializer.Deserialize<InitializeResponse>(body, JsonOptions);
        if (parsed is not { Status: "success", Data: not null })
        {
            _logger.LogError("Flutterwave initialize returned an unsuccessful payload");
            return Result.Failure<PaymentInitialization>(PaymentErrors.GatewayError);
        }

        return Result.Success(new PaymentInitialization(reference, parsed.Data.Link));
    }

    public async Task<Result<PaymentVerificationResult>> VerifyTransactionAsync(
        string reference, CancellationToken cancellationToken)
    {
        var verification = await VerifyByReferenceAsync(reference, cancellationToken);
        if (verification is null)
        {
            return Result.Failure<PaymentVerificationResult>(PaymentErrors.GatewayError);
        }

        var state = verification.Status.ToLowerInvariant() switch
        {
            "successful" => PaymentAttemptState.Succeeded,
            "failed" => PaymentAttemptState.Failed,
            "cancelled" => PaymentAttemptState.Abandoned,
            _ => PaymentAttemptState.Pending
        };
        return Result.Success(new PaymentVerificationResult(
            state == PaymentAttemptState.Succeeded, verification.TxRef, ToKobo(verification.Amount), state));
    }

    public Task<Result> ProcessRefundAsync(string transactionReference, CancellationToken cancellationToken) =>
        RefundAsync(transactionReference, null, cancellationToken);

    public Task<Result> ProcessPartialRefundAsync(string transactionReference, decimal amountKobo, CancellationToken cancellationToken) =>
        RefundAsync(transactionReference, amountKobo, cancellationToken);

    private async Task<Result> RefundAsync(string transactionReference, decimal? amountKobo, CancellationToken cancellationToken)
    {
        // Flutterwave's refund endpoint needs the numeric transaction id, not the tx_ref, so it
        // must be resolved first.
        var verification = await VerifyByReferenceAsync(transactionReference, cancellationToken);
        if (verification is null)
        {
            return Result.Failure(PaymentErrors.GatewayError);
        }

        using var response = await _httpClient.PostAsJsonAsync(
            $"transactions/{verification.Id}/refund", new RefundRequest(amountKobo is { } kobo ? ToNaira(kobo) : verification.Amount), cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Flutterwave refund failed with status {StatusCode}", (int)response.StatusCode);
            return Result.Failure(PaymentErrors.GatewayError);
        }

        return Result.Success();
    }

    public bool VerifyWebhookSignature(string rawBody, string? signatureHeaderValue)
    {
        if (string.IsNullOrEmpty(signatureHeaderValue) || string.IsNullOrEmpty(_settings.SecretHash))
        {
            return false;
        }

        // Flutterwave webhooks carry a pre-shared hash (set once in the dashboard), not an
        // HMAC of the body - this is a direct comparison, not a signature recomputation.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(signatureHeaderValue), Encoding.UTF8.GetBytes(_settings.SecretHash));
    }

    public string? ExtractReferenceFromWebhookPayload(string rawBody)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<WebhookPayload>(rawBody, JsonOptions);
            return payload?.Data?.TxRef;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<VerifyData?> VerifyByReferenceAsync(string reference, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            $"transactions/verify_by_reference?tx_ref={Uri.EscapeDataString(reference)}", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Flutterwave verify failed with status {StatusCode}", (int)response.StatusCode);
            return null;
        }

        var parsed = JsonSerializer.Deserialize<VerifyResponse>(body, JsonOptions);
        return parsed is { Status: "success", Data: not null } ? parsed.Data : null;
    }

    private async Task<string> ResolveSellerEmailAsync(Guid sellerId, CancellationToken cancellationToken)
    {
        var email = await _dbContext.SellerProfiles
            .Where(s => s.Id == sellerId)
            .Join(_dbContext.Users, s => s.UserId, u => u.Id, (s, u) => u.Email)
            .FirstOrDefaultAsync(cancellationToken);

        return email ?? $"seller+{sellerId:N}@ogasela.com";
    }

    private static decimal ToNaira(decimal amountKobo) => amountKobo / 100m;

    private static decimal ToKobo(decimal amountNaira) => amountNaira * 100m;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record InitializeRequest(
        [property: JsonPropertyName("tx_ref")] string TxRef,
        [property: JsonPropertyName("amount")] decimal Amount,
        [property: JsonPropertyName("currency")] string Currency,
        [property: JsonPropertyName("redirect_url")] string RedirectUrl,
        [property: JsonPropertyName("customer")] Customer Customer);

    private sealed record Customer([property: JsonPropertyName("email")] string Email);

    private sealed record InitializeResponse(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("data")] InitializeData? Data);

    private sealed record InitializeData([property: JsonPropertyName("link")] string Link);

    private sealed record VerifyResponse(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("data")] VerifyData? Data);

    private sealed record VerifyData(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("tx_ref")] string TxRef,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("amount")] decimal Amount);

    private sealed record RefundRequest([property: JsonPropertyName("amount")] decimal Amount);

    private sealed record WebhookPayload([property: JsonPropertyName("data")] WebhookData? Data);

    private sealed record WebhookData([property: JsonPropertyName("tx_ref")] string? TxRef);
}
