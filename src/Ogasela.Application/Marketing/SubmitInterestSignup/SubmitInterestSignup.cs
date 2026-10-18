using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ogasela.Application.Accounts;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Marketing.Interfaces;
using Ogasela.Application.Notifications;
using Ogasela.Application.Notifications.Interfaces;
using Ogasela.Domain.Marketing;
using Ogasela.Shared;

namespace Ogasela.Application.Marketing.SubmitInterestSignup;

/// <summary>
/// "Join our testers" or "Join waiting list" from ogasela.com. Consent must be true - it's the
/// lawful basis for keeping the details (NDPA s.25), so the form can't be submitted without it.
/// </summary>
public sealed record SubmitInterestSignupCommand(
    InterestKind Kind,
    string Name,
    string Email,
    string? Phone,
    string? State,
    InterestIntent? Intent,
    InterestDevicePlatform? DevicePlatform,
    bool Consent,
    string TurnstileToken) : IRequest<Result>;

public sealed class SubmitInterestSignupCommandValidator : AbstractValidator<SubmitInterestSignupCommand>
{
    public SubmitInterestSignupCommandValidator()
    {
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Email).NotEmpty().MaximumLength(320).EmailAddress().WithMessage("Email address is not valid.");
        RuleFor(x => x.Phone)
            .Must(NigerianPhoneNumber.IsValid).WithMessage("Enter a valid Nigerian phone number, e.g. 08031234567.")
            .When(x => !string.IsNullOrWhiteSpace(x.Phone));
        RuleFor(x => x.State).MaximumLength(100);
        RuleFor(x => x.Intent).IsInEnum();
        RuleFor(x => x.DevicePlatform).IsInEnum();
        RuleFor(x => x.DevicePlatform).NotNull().WithMessage("Tell us whether your phone is Android or iPhone (iOS).");
        RuleFor(x => x.Consent).Equal(true).WithMessage("Please agree to be contacted so we can keep your details.");
        RuleFor(x => x.TurnstileToken).NotEmpty().WithMessage("Verification token is required.");
    }
}

public sealed class SubmitInterestSignupCommandHandler : IRequestHandler<SubmitInterestSignupCommand, Result>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ITurnstileVerifier _turnstileVerifier;
    private readonly IEmailSender _emailSender;
    private readonly IDateTime _dateTime;
    private readonly ILogger<SubmitInterestSignupCommandHandler> _logger;

    public SubmitInterestSignupCommandHandler(
        IApplicationDbContext dbContext,
        ITurnstileVerifier turnstileVerifier,
        IEmailSender emailSender,
        IDateTime dateTime,
        ILogger<SubmitInterestSignupCommandHandler> logger)
    {
        _dbContext = dbContext;
        _turnstileVerifier = turnstileVerifier;
        _emailSender = emailSender;
        _dateTime = dateTime;
        _logger = logger;
    }

    public async Task<Result> Handle(SubmitInterestSignupCommand request, CancellationToken cancellationToken)
    {
        var verified = await _turnstileVerifier.VerifyAsync(request.TurnstileToken, cancellationToken);
        if (!verified)
        {
            return Result.Failure(MarketingErrors.TurnstileVerificationFailed);
        }

        var email = request.Email.Trim().ToLowerInvariant();

        // Signing up twice is harmless for a list like this - treat it as success rather than
        // surfacing a confusing "already exists" error (which would also reveal who's on the list).
        var alreadySignedUp = await _dbContext.InterestSignups
            .AnyAsync(s => s.Kind == request.Kind && s.Email == email, cancellationToken);
        if (alreadySignedUp)
        {
            return Result.Success();
        }

        var name = request.Name.Trim();
        _dbContext.InterestSignups.Add(InterestSignup.Create(
            request.Kind,
            name,
            email,
            string.IsNullOrWhiteSpace(request.Phone) ? null : NigerianPhoneNumber.ToLocalFormat(request.Phone.Trim()),
            string.IsNullOrWhiteSpace(request.State) ? null : request.State.Trim(),
            request.Intent,
            request.DevicePlatform,
            _dateTime.UtcNow));
        await _dbContext.SaveChangesAsync(cancellationToken);

        await SendConfirmationAsync(request.Kind, name, email, cancellationToken);
        return Result.Success();
    }

    // Best effort, like the welcome email: the signup is saved either way.
    private async Task SendConfirmationAsync(InterestKind kind, string name, string email, CancellationToken cancellationToken)
    {
        try
        {
            var isTester = kind == InterestKind.Tester;
            var html = BrandedEmailTemplate.Render(
                previewText: isTester ? "You're on the Ogasela testers list." : "You're on the Ogasela waiting list.",
                heading: isTester ? "Thanks for volunteering to test Ogasela" : "You're on the waiting list",
                bodyParagraphs:
                [
                    $"Hi {name},",
                    isTester
                        ? "Thanks for offering to test Ogasela before launch. When a test build is ready for your phone, we'll email you instructions to install it and tell you how to send us feedback."
                        : "Thanks for your interest in Ogasela. We'll email you as soon as the app is available on the App Store and Google Play.",
                    "We'll only use these details to contact you about this. To be removed from the list at any time, reply to this email or write to privacy@ogasela.com and we'll delete your details."
                ],
                ctaText: "Visit ogasela.com",
                ctaUrl: "https://ogasela.com",
                eyebrow: isTester ? "Early testers" : "Waiting list");

            await _emailSender.SendAsync(
                new EmailMessage(
                    email,
                    ToName: name,
                    Subject: isTester ? "You're on the Ogasela testers list" : "You're on the Ogasela waiting list",
                    HtmlBody: html),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send the {Kind} signup confirmation email", kind);
        }
    }
}
