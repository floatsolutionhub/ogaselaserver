namespace Ogasela.Application.Accounts;

/// <summary>
/// IOtpService keys codes by a plain string. Phone resets keep using the bare phone number (the
/// same key otp/request and the login OTP use); email resets get their own namespace so an email
/// address can never collide with a phone key.
/// </summary>
public static class PasswordResetOtpKey
{
    public static string ForEmail(string email) => $"email:{email.Trim().ToLowerInvariant()}";

    public static string ForPhone(string phone) => phone.Trim();
}

/// <summary>
/// Login codes are keyed by the identifier the user signed in with: the bare phone number (shared
/// with otp/request, as before) or an "email:" key, so signing in by email works for accounts
/// whose phone isn't verified.
/// </summary>
public static class LoginOtpKey
{
    public static string For(string? phone, string? email) =>
        phone is not null ? PasswordResetOtpKey.ForPhone(phone) : PasswordResetOtpKey.ForEmail(email!);
}
