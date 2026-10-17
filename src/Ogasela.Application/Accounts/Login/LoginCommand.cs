using MediatR;
using Ogasela.Shared;

namespace Ogasela.Application.Accounts.Login;

/// <summary>Step 1 of every consumer login: phone-or-email + password. On success an OTP has been sent to the identifier they signed in with (and to their other contact too, if that one is verified) - complete sign-in with VerifyLoginOtpCommand. An unverified identifier gets the code alone, so every sign-in is proven through a verified email or phone. Internal roles (Moderator/FinanceAdmin/SuperAdmin/Staff) must use AdminLoginCommand instead - this always fails with User.MfaRequired for them.</summary>
public sealed record LoginCommand(string? Phone, string? Email, string Password) : IRequest<Result<LoginOtpSentResponse>>;

/// <summary>
/// MaskedEmail/MaskedPhone are where the code actually went (empty when it wasn't sent there).
/// VerificationRequired: the email/phone they signed in with isn't verified yet, so the code went
/// only there - entering it verifies it and completes sign-in. Channel is "Email" or "Phone".
/// </summary>
public sealed record LoginOtpSentResponse(
    string MaskedEmail, string MaskedPhone, bool VerificationRequired = false, string Channel = "Phone");
