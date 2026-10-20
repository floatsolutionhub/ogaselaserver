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
/// <see cref="IPaymentGateway"/> backed by Paystack's REST API
/// (https://paystack.com/docs/api/transaction, /docs/api/refund, /docs/payments/webhooks).
/// </summary>
public sealed class PaystackGateway : IPaymentGateway
{
    public const string Name = "Paystack";

    private readonly HttpClient _httpClient;
    private readonly IApplicationDbContext _dbContext;
    private readonly PaystackSettings _settings;
    private readonly ILogger<PaystackGateway> _logger;

    public PaystackGateway(
        HttpClient httpClient, IApplicationDbContext dbContext, IOptions<PaystackSettings> settings,
        ILogger<PaystackGateway> logger)
    {
        _httpClient = httpClient;
        _dbContext = dbContext;
        _settings = settings.Value;
        _logger = logger;

        _httpClient.BaseAddress = new Uri(_settings.BaseUrl);
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

        var payload = new InitializeRequest(email, (long)amountKobo, reference, callbackUrl ?? _settings.CallbackUrl);

        using var response = await _httpClient.PostAsJsonAsync("transaction/initialize", payload, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Never log the raw response body here or below - Paystack's payload shape isn't
            // guaranteed error-only on a non-2xx, and can carry customer email/card metadata.
            _logger.LogError("Paystack initialize failed with status {StatusCode}", (int)response.StatusCode);
            return Result.Failure<PaymentInitialization>(PaymentErrors.GatewayError);
        }

        var parsed = JsonSerializer.Deserialize<InitializeResponse>(body, JsonOptions);
        if (parsed is not { Status: true, Data: not null })
        {
            _logger.LogError("Paystack initialize returned an unsuccessful payload");
            return Result.Failure<PaymentInitialization>(PaymentErrors.GatewayError);
        }

        return Result.Success(new PaymentInitialization(parsed.Data.Reference, parsed.Data.AuthorizationUrl));
    }

    public async Task<Result<PaymentVerificationResult>> VerifyTransactionAsync(
        string reference, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync($"transaction/verify/{Uri.EscapeDataString(reference)}", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Paystack verify failed with status {StatusCode}", (int)response.StatusCode);
            return Result.Failure<PaymentVerificationResult>(PaymentErrors.GatewayError);
        }

        var parsed = JsonSerializer.Deserialize<VerifyResponse>(body, JsonOptions);
        if (parsed is not { Status: true, Data: not null })
        {
            return Result.Failure<PaymentVerificationResult>(PaymentErrors.GatewayError);
        }

        // https://paystack.com/docs/api/transaction/#verify - "abandoned" is also what a checkout
        // nobody has paid on yet reports; ongoing/pending/processing/queued are still in flight.
        var state = parsed.Data.Status.ToLowerInvariant() switch
        {
            "success" => PaymentAttemptState.Succeeded,
            "failed" or "reversed" => PaymentAttemptState.Failed,
            "abandoned" => PaymentAttemptState.Abandoned,
            _ => PaymentAttemptState.Pending
        };
        return Result.Success(new PaymentVerificationResult(
            state == PaymentAttemptState.Succeeded, parsed.Data.Reference, parsed.Data.Amount, state));
    }

    public Task<Result> ProcessRefundAsync(string transactionReference, CancellationToken cancellationToken) =>
        RefundAsync(new RefundRequest(transactionReference, null), cancellationToken);

    public Task<Result> ProcessPartialRefundAsync(string transactionReference, decimal amountKobo, CancellationToken cancellationToken) =>
        RefundAsync(new RefundRequest(transactionReference, (long)amountKobo), cancellationToken);

    private async Task<Result> RefundAsync(RefundRequest request, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync("refund", request, RefundJsonOptions, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Paystack refund failed with status {StatusCode}", (int)response.StatusCode);
            return Result.Failure(PaymentErrors.GatewayError);
        }

        return Result.Success();
    }

    public bool VerifyWebhookSignature(string rawBody, string? signatureHeaderValue)
    {
        if (string.IsNullOrEmpty(signatureHeaderValue))
        {
            return false;
        }

        var computedHash = Convert.ToHexStringLower(
            HMACSHA512.HashData(Encoding.UTF8.GetBytes(_settings.SecretKey), Encoding.UTF8.GetBytes(rawBody)));

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computedHash), Encoding.UTF8.GetBytes(signatureHeaderValue.ToLowerInvariant()));
    }

    public string? ExtractReferenceFromWebhookPayload(string rawBody)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<WebhookPayload>(rawBody, JsonOptions);
            return payload?.Data?.Reference;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string> ResolveSellerEmailAsync(Guid sellerId, CancellationToken cancellationToken)
    {
        var email = await _dbContext.SellerProfiles
            .Where(s => s.Id == sellerId)
            .Join(_dbContext.Users, s => s.UserId, u => u.Id, (s, u) => u.Email)
            .FirstOrDefaultAsync(cancellationToken);

        // Paystack requires a well-formed email even for phone-only accounts.
        return email ?? $"seller+{sellerId:N}@ogasela.com";
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record InitializeRequest(
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("amount")] long Amount,
        [property: JsonPropertyName("reference")] string Reference,
        [property: JsonPropertyName("callback_url")] string CallbackUrl);

    private sealed record InitializeResponse(
        [property: JsonPropertyName("status")] bool Status,
        [property: JsonPropertyName("data")] InitializeData? Data);

    private sealed record InitializeData(
        [property: JsonPropertyName("authorization_url")] string AuthorizationUrl,
        [property: JsonPropertyName("reference")] string Reference);

    private sealed record VerifyResponse(
        [property: JsonPropertyName("status")] bool Status,
        [property: JsonPropertyName("data")] VerifyData? Data);

    private sealed record VerifyData(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("reference")] string Reference,
        [property: JsonPropertyName("amount")] decimal Amount);

    // Omits a null amount so Paystack refunds the full charge.
    private static readonly JsonSerializerOptions RefundJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <param name="Amount">In kobo; null refunds the full charge.</param>
    private sealed record RefundRequest(
        [property: JsonPropertyName("transaction")] string Transaction,
        [property: JsonPropertyName("amount")] long? Amount);

    private sealed record WebhookPayload([property: JsonPropertyName("data")] WebhookData? Data);

    private sealed record WebhookData([property: JsonPropertyName("reference")] string? Reference);
}
