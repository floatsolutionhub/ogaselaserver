using System.Security.Cryptography;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ogasela.Application.Accounts.Interfaces;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Notifications;
using Ogasela.Application.Notifications.Interfaces;
using Ogasela.Domain.Accounts;
using Ogasela.Shared;

namespace Ogasela.Application.Accounts.SocialLogin;

/// <summary>
/// Resolves the provider identity to an account, in this order:
/// 1. an existing ExternalLogin link for that provider + subject id;
/// 2. an existing account with the same email, but only when the provider says that email is
///    verified - it's then linked, so the user can use either sign-in from now on;
/// 3. otherwise a brand-new Buyer/Seller account (no phone, an unusable random password - they can
///    set one later via password reset).
/// Internal roles never sign in this way; the admin portal requires email + password + OTP.
/// Tokens are issued directly: the provider has already authenticated the user, so there's no
/// separate OTP step as with password sign-in.
/// </summary>
public sealed class SocialLoginCommandHandler : IRequestHandler<SocialLoginCommand, Result<SocialLoginResponse>>
{
    private static readonly HashSet<UserRole> ConsumerRoles = [UserRole.Buyer, UserRole.Seller];

    private readonly IApplicationDbContext _dbContext;
    private readonly ISocialTokenVerifier _verifier;
    private readonly IPasswordHasher _passwordHasher;
    private readonly AuthTokenIssuer _tokenIssuer;
    private readonly IDateTime _dateTime;
    private readonly IEmailSender _emailSender;
    private readonly ILogger<SocialLoginCommandHandler> _logger;

    public SocialLoginCommandHandler(
        IApplicationDbContext dbContext,
        ISocialTokenVerifier verifier,
        IPasswordHasher passwordHasher,
        AuthTokenIssuer tokenIssuer,
        IDateTime dateTime,
        IEmailSender emailSender,
        ILogger<SocialLoginCommandHandler> logger)
    {
        _dbContext = dbContext;
        _verifier = verifier;
        _passwordHasher = passwordHasher;
        _tokenIssuer = tokenIssuer;
        _dateTime = dateTime;
        _emailSender = emailSender;
        _logger = logger;
    }

    public async Task<Result<SocialLoginResponse>> Handle(SocialLoginCommand request, CancellationToken cancellationToken)
    {
        var verified = await _verifier.VerifyAsync(request.Provider, request.Token, cancellationToken);
        if (verified.IsFailure)
        {
            return Result.Failure<SocialLoginResponse>(verified.Error);
        }

        var identity = verified.Value;
        var now = _dateTime.UtcNow;

        var linkedUser = await (
            from link in _dbContext.ExternalLogins
            join user in _dbContext.Users on link.UserId equals user.Id
            where link.Provider == request.Provider && link.ProviderUserId == identity.ProviderUserId
            select user)
            .FirstOrDefaultAsync(cancellationToken);

        if (linkedUser is not null)
        {
            return await SignInAsync(linkedUser, isNewUser: false, cancellationToken, identity);
        }

        var email = string.IsNullOrWhiteSpace(identity.Email) ? null : identity.Email.Trim().ToLowerInvariant();

        if (email is not null)
        {
            var existingUser = await _dbContext.Users
                .FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == email, cancellationToken);

            if (existingUser is not null)
            {
                if (!identity.EmailVerified)
                {
                    return Result.Failure<SocialLoginResponse>(AccountErrors.SocialEmailBelongsToAnotherAccount);
                }

                if (!ConsumerRoles.Contains(existingUser.Role))
                {
                    return Result.Failure<SocialLoginResponse>(AccountErrors.SocialLoginNotAllowed);
                }

                var alreadyLinkedToProvider = await _dbContext.ExternalLogins
                    .AnyAsync(l => l.UserId == existingUser.Id && l.Provider == request.Provider, cancellationToken);
                if (alreadyLinkedToProvider)
                {
                    // Same email, but a different account at this provider is already linked.
                    return Result.Failure<SocialLoginResponse>(AccountErrors.SocialEmailBelongsToAnotherAccount);
                }

                _dbContext.ExternalLogins.Add(ExternalLogin.Create(existingUser.Id, request.Provider, identity.ProviderUserId, now));
                await _dbContext.SaveChangesAsync(cancellationToken);
                return await SignInAsync(existingUser, isNewUser: false, cancellationToken, identity);
            }
        }

        var name = FirstNonBlank(request.Name, identity.Name);
        var unusablePassword = _passwordHasher.Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var newUser = User.Create(phone: null, email, unusablePassword, request.AccountType, name);
        _dbContext.Users.Add(newUser);

        if (request.AccountType == UserRole.Seller)
        {
            // Sellers need a business name; start with their own name - it can be changed in their profile.
            _dbContext.SellerProfiles.Add(SellerProfile.Create(newUser.Id, name ?? "My Shop", rcNumber: null, nin: null));
        }

        _dbContext.ExternalLogins.Add(ExternalLogin.Create(newUser.Id, request.Provider, identity.ProviderUserId, now));
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (email is not null)
        {
            await SendWelcomeEmailAsync(email, name, request.AccountType, cancellationToken);
        }

        return await SignInAsync(newUser, isNewUser: true, cancellationToken, identity);
    }

    private async Task<Result<SocialLoginResponse>> SignInAsync(
        User user, bool isNewUser, CancellationToken cancellationToken, SocialIdentity identity)
    {
        // The provider vouches the account's email is theirs - saved by IssueAsync below.
        if (identity.EmailVerified && user.Email is not null && identity.Email is not null
            && string.Equals(user.Email.Trim(), identity.Email.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            user.MarkEmailVerified(_dateTime.UtcNow);
        }

        if (!ConsumerRoles.Contains(user.Role))
        {
            return Result.Failure<SocialLoginResponse>(AccountErrors.SocialLoginNotAllowed);
        }

        if (user.IsSuspended)
        {
            return Result.Failure<SocialLoginResponse>(AccountErrors.AccountSuspended);
        }

        var tokens = await _tokenIssuer.IssueAsync(user, tokenToRotate: null, cancellationToken);
        return Result.Success(new SocialLoginResponse(
            tokens.AccessToken, tokens.AccessTokenExpiresAt, tokens.RefreshToken, tokens.RefreshTokenExpiresAt, isNewUser));
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.Select(v => v?.Trim()).FirstOrDefault(v => !string.IsNullOrEmpty(v));

    private async Task SendWelcomeEmailAsync(string email, string? name, UserRole accountType, CancellationToken cancellationToken)
    {
        try
        {
            var roleNoun = accountType == UserRole.Seller ? "seller" : "buyer";
            var html = BrandedEmailTemplate.Render(
                previewText: "Welcome to Ogasela!",
                heading: "Welcome to Ogasela",
                bodyParagraphs:
                [
                    $"Your {roleNoun} account has been created. You're all set to start {(accountType == UserRole.Seller ? "listing on" : "shopping on")} Ogasela.",
                    "If you didn't create this account, please contact our support team right away."
                ],
                eyebrow: "Welcome");

            await _emailSender.SendAsync(
                new EmailMessage(email, ToName: name, Subject: "Welcome to Ogasela", HtmlBody: html),
                cancellationToken);
        }
        catch (Exception ex)
        {
            // The account already exists; a failed welcome email shouldn't undo the sign-up.
            _logger.LogError(ex, "Failed to send welcome email to a new social sign-up");
        }
    }
}
