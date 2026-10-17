using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ogasela.Application.Accounts.Interfaces;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Notifications;
using Ogasela.Application.Notifications.Interfaces;
using Ogasela.Domain.Accounts;
using Ogasela.Shared;

namespace Ogasela.Application.Accounts.Login;

/// <summary>
/// Verifies phone-or-email + password, then sends a 6-digit OTP (best-effort). The code goes to the
/// identifier the user signed in with, plus their other contact if that one is verified. When the
/// identifier itself isn't verified yet it goes there alone, worded as a verification, and
/// VerifyLoginOtpCommand marks it verified - so every sign-in is through a verified email or phone.
/// Internal roles are turned away (they use AdminLoginCommand). No tokens are issued here.
/// </summary>
public sealed class LoginCommandHandler : IRequestHandler<LoginCommand, Result<LoginOtpSentResponse>>
{
    /// <summary>These roles must go through AdminLoginCommand's email+password -> OTP flow instead - never issued a token straight off a password check, however this endpoint is called.</summary>
    private static readonly HashSet<UserRole> MfaRequiredRoles = [UserRole.Moderator, UserRole.FinanceAdmin, UserRole.SuperAdmin, UserRole.Staff];

    private readonly IApplicationDbContext _dbContext;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IOtpService _otpService;
    private readonly ISmsSender _smsSender;
    private readonly IEmailSender _emailSender;
    private readonly ILogger<LoginCommandHandler> _logger;

    public LoginCommandHandler(
        IApplicationDbContext dbContext,
        IPasswordHasher passwordHasher,
        IOtpService otpService,
        ISmsSender smsSender,
        IEmailSender emailSender,
        ILogger<LoginCommandHandler> logger)
    {
        _dbContext = dbContext;
        _passwordHasher = passwordHasher;
        _otpService = otpService;
        _smsSender = smsSender;
        _emailSender = emailSender;
        _logger = logger;
    }

    public async Task<Result<LoginOtpSentResponse>> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        var user = await _dbContext.Users.FirstOrDefaultAsync(
            u => (request.Phone != null && u.Phone == request.Phone) ||
                 (request.Email != null && u.Email == request.Email),
            cancellationToken);

        if (user is null || !_passwordHasher.Verify(request.Password, user.PasswordHash))
        {
            return Result.Failure<LoginOtpSentResponse>(AccountErrors.InvalidCredentials);
        }

        if (MfaRequiredRoles.Contains(user.Role))
        {
            return Result.Failure<LoginOtpSentResponse>(AccountErrors.MfaRequired);
        }

        // Only after the password checked out, so this can't be used to probe which accounts are suspended.
        if (user.IsSuspended)
        {
            return Result.Failure<LoginOtpSentResponse>(AccountErrors.AccountSuspended);
        }

        // The code always goes to the identifier they typed - entering it is what proves (or
        // re-proves) they own it. Their other contact only gets it once that one is verified too, so
        // nobody can complete a sign-in through an address they never proved they own.
        var byPhone = request.Phone is not null;
        var identifierVerified = byPhone ? user.IsPhoneVerified : user.IsEmailVerified;
        var sendSms = byPhone || user.IsPhoneVerified;
        var sendEmail = !byPhone || user.IsEmailVerified;
        if (!identifierVerified)
        {
            sendSms = byPhone;
            sendEmail = !byPhone;
        }

        var code = await _otpService.GenerateAsync(LoginOtpKey.For(byPhone ? user.Phone : null, byPhone ? null : user.Email), cancellationToken);
        var purpose = identifierVerified ? "login" : "verification";

        if (sendSms && !string.IsNullOrWhiteSpace(user.Phone))
        {
            try
            {
                await _smsSender.SendAsync(user.Phone, $"Your Ogasela {purpose} code is {code}. It expires in 5 minutes. Do not share this code with anyone.", cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send {Purpose} OTP SMS to {Phone}", purpose, user.Phone);
            }
        }

        if (sendEmail && !string.IsNullOrWhiteSpace(user.Email))
        {
            try
            {
                var html = identifierVerified
                    ? BrandedEmailTemplate.Render(
                        previewText: "Your Ogasela login code",
                        heading: "Your login code",
                        bodyParagraphs:
                        [
                            $"Your Ogasela login code is {code}. It expires in 5 minutes and can be used once.",
                            "If you didn't try to sign in, you can ignore this email - your account is still safe."
                        ],
                        eyebrow: "Security")
                    : BrandedEmailTemplate.Render(
                        previewText: "Verify your email to sign in",
                        heading: "Verify your email address",
                        bodyParagraphs:
                        [
                            $"Enter {code} in the Ogasela app to verify this email address and finish signing in. It expires in 5 minutes.",
                            "If you didn't try to sign in to Ogasela, you can ignore this email."
                        ],
                        eyebrow: "Verification");

                await _emailSender.SendAsync(
                    new EmailMessage(user.Email, user.Name,
                        identifierVerified ? "Your Ogasela login code" : "Verify your email for Ogasela", html),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send {Purpose} OTP email to {Email}", purpose, user.Email);
            }
        }

        return Result.Success(new LoginOtpSentResponse(
            sendEmail ? MaskEmail(user.Email) : string.Empty,
            sendSms ? MaskPhone(user.Phone) : string.Empty,
            VerificationRequired: !identifierVerified,
            Channel: byPhone ? "Phone" : "Email"));
    }

    private static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return string.Empty;
        }

        var at = email.IndexOf('@');
        return at <= 1 ? email : $"{email[0]}***{email[at..]}";
    }

    private static string MaskPhone(string? phone) =>
        string.IsNullOrWhiteSpace(phone) ? string.Empty : phone.Length <= 6 ? phone : $"{phone[..3]}***{phone[^3..]}";
}
