using Ogasela.Domain.Marketing;

namespace Ogasela.Api.Contracts.Marketing;

public sealed record SubmitContactFormRequest(
    string Name, string Email, string Subject, string Message, string TurnstileToken);

/// <summary>Body for both /marketing/testers and /marketing/waitlist - the route decides the kind.</summary>
public sealed record SubmitInterestSignupRequest(
    string Name,
    string Email,
    string? Phone,
    string? State,
    InterestIntent? Intent,
    InterestDevicePlatform? DevicePlatform,
    bool Consent,
    string TurnstileToken);

public sealed record RequestAccountDeletionRequest(
    string? Email,
    string? Phone,
    AccountDeletionRequestType RequestType,
    string? Details,
    string TurnstileToken);
