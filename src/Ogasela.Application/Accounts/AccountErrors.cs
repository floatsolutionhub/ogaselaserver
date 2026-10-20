using Ogasela.Shared;

namespace Ogasela.Application.Accounts;

public static class AccountErrors
{
    public static readonly Error UserAlreadyExists = new(
        "User.AlreadyExists", "A user with this phone number or email already exists.");

    public static readonly Error InvalidCredentials = new(
        "User.InvalidCredentials", "The phone/email or password is incorrect.");

    public static readonly Error UserNotFound = new(
        "User.NotFound", "The user could not be found.");

    public static readonly Error StaffCannotSelfDelete = new(
        "User.StaffCannotSelfDelete", "Staff accounts can't be deleted from the app. Ask a SuperAdmin to offboard you.");

    public static readonly Error OtpInvalid = new(
        "Otp.InvalidCode", "The verification code is incorrect.");

    public static readonly Error OtpExpired = new(
        "Otp.Expired", "The verification code has expired. Request a new one.");

    public static readonly Error OtpRateLimited = new(
        "Otp.RateLimited", "Too many verification attempts. Try again later.");

    public static readonly Error MfaRequired = new(
        "User.MfaRequired", "This account requires email + OTP sign-in. Use /api/v1/auth/admin/login instead.");

    public static readonly Error MfaPhoneRequired = new(
        "User.MfaPhoneRequired", "This admin account has no phone number on file, so a login OTP cannot be sent. Contact a Super Admin to add one.");

    public static readonly Error RefreshTokenInvalid = new(
        "RefreshToken.Invalid", "The refresh token is invalid.");

    public static readonly Error RefreshTokenExpired = new(
        "RefreshToken.Expired", "The refresh token has expired. Please log in again.");

    public static readonly Error RefreshTokenReused = new(
        "RefreshToken.Reused", "This refresh token has already been used. All sessions for this account have been revoked.");

    public static readonly Error CurrentPasswordIncorrect = new(
        "User.CurrentPasswordIncorrect", "The current password is incorrect.");

    public static readonly Error SocialProviderNotConfigured = new(
        "SocialLogin.ProviderNotConfigured", "This sign-in option isn't available yet.");

    public static readonly Error SocialTokenInvalid = new(
        "SocialLogin.InvalidToken", "We couldn't confirm your sign-in with that provider. Please try again.");

    public static readonly Error SocialLoginNotAllowed = new(
        "SocialLogin.NotAllowed", "This account can't sign in with a social provider.");

    public static readonly Error SocialEmailBelongsToAnotherAccount = new(
        "SocialLogin.EmailInUse", "An account with this email already exists. Log in with your password instead.");

    public static Error ProfilePhotoRejected(string? reason) => new(
        "User.ProfilePhotoRejected",
        $"That photo can't be used{(string.IsNullOrWhiteSpace(reason) ? "" : $" ({reason.ToLowerInvariant()})")}. Face the camera in good light and try again.");

    public static readonly Error DocumentAlreadyPending = new(
        "UserDocument.AlreadyPending",
        "You already have this document waiting for review. You can upload another once it's approved or rejected.");

    public static readonly Error VerifiedIdentityLocked = new(
        "User.VerifiedIdentityLocked", "Your account is verified, so your selfie, ID card and utility bill can no longer be changed.");

    public static readonly Error VerifiedBusinessLocked = new(
        "User.VerifiedBusinessLocked", "Your business is verified, so its registration and proof of address can no longer be changed.");

    public static readonly Error AccountSuspended = new(
        "User.Suspended", "This account has been suspended. Contact support@ogasela.com if you think this is a mistake.");
}
