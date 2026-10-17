using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ogasela.Application.Accounts.Interfaces;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Notifications;
using Ogasela.Application.Notifications.Interfaces;
using Ogasela.Domain.Accounts;
using Ogasela.Shared;

namespace Ogasela.Application.Accounts.ProfileChanges;

/// <summary>Step 1 of changing phone/email: sends a code to the NEW phone (SMS) or email, proving the user owns it.</summary>
public sealed record RequestContactChangeCodeCommand(ProfileChangeType Type, string Value) : IRequest<Result>;

/// <summary>Step 2: checks the code and files the change for staff approval. It only takes effect once approved.</summary>
public sealed record SubmitContactChangeCommand(ProfileChangeType Type, string Value, string Code)
    : IRequest<Result<ProfileChangeResponse>>;

/// <summary>Sellers: files a new business name/store address for staff approval. An empty StoreAddress clears it.</summary>
public sealed record SubmitBusinessInfoChangeCommand(string BusinessName, string? StoreAddress)
    : IRequest<Result<ProfileChangeResponse>>;

/// <summary>The caller's change requests, newest first.</summary>
public sealed record GetMyProfileChangesQuery : IRequest<Result<IReadOnlyList<ProfileChangeResponse>>>;

/// <summary>Withdraws a change that's still awaiting review.</summary>
public sealed record CancelProfileChangeCommand(Guid Id) : IRequest<Result>;

public sealed record ProfileChangeResponse(
    Guid Id,
    ProfileChangeType Type,
    string? NewValue,
    string? BusinessName,
    string? StoreAddress,
    ProfileChangeStatus Status,
    string? ReviewNote,
    DateTime CreatedAt,
    DateTime? ReviewedAt)
{
    public static ProfileChangeResponse From(ProfileChangeRequest r) => new(
        r.Id, r.Type, r.NewValue, r.BusinessName, r.StoreAddress, r.Status, r.ReviewNote, r.CreatedAt, r.ReviewedAt);
}

public static class ProfileChangeErrors
{
    public static readonly Error SameAsCurrent = new(
        "ProfileChange.SameAsCurrent", "That's already the one on your account.");

    public static readonly Error InUse = new(
        "ProfileChange.InUse", "Another account already uses this. Try a different one.");

    public static readonly Error SellersOnly = new(
        "ProfileChange.SellersOnly", "Only seller accounts have business info.");

    public static readonly Error NoChange = new(
        "ProfileChange.NoChange", "Nothing has changed from your current business info.");

    public static readonly Error NotFound = new(
        "ProfileChange.NotFound", "This change request could not be found.");

    public static readonly Error NotPending = new(
        "ProfileChange.NotPending", "This change request has already been reviewed.");

    public static Error AlreadyPending(ProfileChangeType type) => new(
        "ProfileChange.AlreadyPending",
        $"You already have a {Describe(type)} change waiting for review. You can submit another once it's approved or rejected, or withdraw it first.");

    private static string Describe(ProfileChangeType type) => type switch
    {
        ProfileChangeType.Phone => "phone number",
        ProfileChangeType.Email => "email",
        _ => "business info"
    };
}

/// <summary>Contact-change codes get their own key namespace, so they can't be confused with login or reset codes.</summary>
internal static class ContactChangeOtpKey
{
    public static string For(Guid userId, ProfileChangeType type, string value) =>
        $"contact-change:{userId}:{type}:{value}";
}

internal static class ContactValue
{
    /// <summary>Emails are compared and stored lower-case; phones as typed (local 0XXXXXXXXXX format).</summary>
    public static string Normalize(ProfileChangeType type, string value) =>
        type == ProfileChangeType.Email ? value.Trim().ToLowerInvariant() : value.Trim();

    public static Task<bool> InUseByAnotherAsync(
        IApplicationDbContext dbContext, Guid userId, ProfileChangeType type, string value, CancellationToken cancellationToken) =>
        type == ProfileChangeType.Email
            ? dbContext.Users.AnyAsync(u => u.Id != userId && u.Email != null && u.Email.ToLower() == value, cancellationToken)
            : dbContext.Users.AnyAsync(u => u.Id != userId && u.Phone == value, cancellationToken);
}

public sealed class RequestContactChangeCodeCommandValidator : AbstractValidator<RequestContactChangeCodeCommand>
{
    public RequestContactChangeCodeCommandValidator()
    {
        RuleFor(x => x.Type).Must(t => t is ProfileChangeType.Phone or ProfileChangeType.Email)
            .WithMessage("Choose phone or email.");
        RuleFor(x => x.Value).NotEmpty();
        When(x => x.Type == ProfileChangeType.Phone, () =>
            RuleFor(x => x.Value).Must(NigerianPhoneNumber.IsValid)
                .WithMessage("Phone number must be a valid Nigerian mobile number."));
        When(x => x.Type == ProfileChangeType.Email, () =>
            RuleFor(x => x.Value).EmailAddress().WithMessage("Email address is not valid.").MaximumLength(256));
    }
}

public sealed class SubmitContactChangeCommandValidator : AbstractValidator<SubmitContactChangeCommand>
{
    public SubmitContactChangeCommandValidator()
    {
        RuleFor(x => x.Type).Must(t => t is ProfileChangeType.Phone or ProfileChangeType.Email)
            .WithMessage("Choose phone or email.");
        RuleFor(x => x.Value).NotEmpty();
        RuleFor(x => x.Code).NotEmpty().WithMessage("Enter the code we sent you.");
        When(x => x.Type == ProfileChangeType.Phone, () =>
            RuleFor(x => x.Value).Must(NigerianPhoneNumber.IsValid)
                .WithMessage("Phone number must be a valid Nigerian mobile number."));
        When(x => x.Type == ProfileChangeType.Email, () =>
            RuleFor(x => x.Value).EmailAddress().WithMessage("Email address is not valid.").MaximumLength(256));
    }
}

public sealed class SubmitBusinessInfoChangeCommandValidator : AbstractValidator<SubmitBusinessInfoChangeCommand>
{
    public SubmitBusinessInfoChangeCommandValidator()
    {
        RuleFor(x => x.BusinessName).NotEmpty().WithMessage("Enter your business name.").MaximumLength(200);
        RuleFor(x => x.StoreAddress).MaximumLength(300);
    }
}

public sealed class ProfileChangeHandlers :
    IRequestHandler<RequestContactChangeCodeCommand, Result>,
    IRequestHandler<SubmitContactChangeCommand, Result<ProfileChangeResponse>>,
    IRequestHandler<SubmitBusinessInfoChangeCommand, Result<ProfileChangeResponse>>,
    IRequestHandler<GetMyProfileChangesQuery, Result<IReadOnlyList<ProfileChangeResponse>>>,
    IRequestHandler<CancelProfileChangeCommand, Result>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IOtpService _otpService;
    private readonly ISmsSender _smsSender;
    private readonly IEmailSender _emailSender;
    private readonly IDateTime _dateTime;
    private readonly ILogger<ProfileChangeHandlers> _logger;

    public ProfileChangeHandlers(
        IApplicationDbContext dbContext,
        ICurrentUserService currentUser,
        IOtpService otpService,
        ISmsSender smsSender,
        IEmailSender emailSender,
        IDateTime dateTime,
        ILogger<ProfileChangeHandlers> logger)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _otpService = otpService;
        _smsSender = smsSender;
        _emailSender = emailSender;
        _dateTime = dateTime;
        _logger = logger;
    }

    public async Task<Result> Handle(RequestContactChangeCodeCommand request, CancellationToken cancellationToken)
    {
        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == _currentUser.UserId, cancellationToken);
        if (user is null)
        {
            return Result.Failure(AccountErrors.UserNotFound);
        }

        if (await HasPendingAsync(user.Id, request.Type, cancellationToken))
        {
            return Result.Failure(ProfileChangeErrors.AlreadyPending(request.Type));
        }

        var value = ContactValue.Normalize(request.Type, request.Value);
        var check = await CheckNewValueAsync(user, request.Type, value, cancellationToken);
        if (check.IsFailure)
        {
            return check;
        }

        var code = await _otpService.GenerateAsync(ContactChangeOtpKey.For(user.Id, request.Type, value), cancellationToken);
        try
        {
            if (request.Type == ProfileChangeType.Phone)
            {
                await _smsSender.SendAsync(
                    value,
                    $"Your Ogasela code to confirm this phone number is {code}. It expires in 5 minutes. Do not share this code with anyone.",
                    cancellationToken);
            }
            else
            {
                var html = BrandedEmailTemplate.Render(
                    previewText: "Confirm your new Ogasela email",
                    heading: "Confirm your new email",
                    bodyParagraphs:
                    [
                        $"Your code to confirm this email address is {code}. It expires in 5 minutes and can be used once.",
                        "If you didn't ask to change the email on your Ogasela account, you can ignore this email."
                    ],
                    eyebrow: "Security");
                await _emailSender.SendAsync(
                    new EmailMessage(value, user.Name, "Confirm your new Ogasela email", html), cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // The code is stored regardless; the user can ask for it again.
            _logger.LogError(ex, "Failed to send contact-change code ({Type}) for user {UserId}", request.Type, user.Id);
        }

        return Result.Success();
    }

    public async Task<Result<ProfileChangeResponse>> Handle(SubmitContactChangeCommand request, CancellationToken cancellationToken)
    {
        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == _currentUser.UserId, cancellationToken);
        if (user is null)
        {
            return Result.Failure<ProfileChangeResponse>(AccountErrors.UserNotFound);
        }

        if (await HasPendingAsync(user.Id, request.Type, cancellationToken))
        {
            return Result.Failure<ProfileChangeResponse>(ProfileChangeErrors.AlreadyPending(request.Type));
        }

        var value = ContactValue.Normalize(request.Type, request.Value);
        var check = await CheckNewValueAsync(user, request.Type, value, cancellationToken);
        if (check.IsFailure)
        {
            return Result.Failure<ProfileChangeResponse>(check.Error);
        }

        var verification = await _otpService.VerifyAsync(
            ContactChangeOtpKey.For(user.Id, request.Type, value), request.Code.Trim(), cancellationToken);
        switch (verification)
        {
            case OtpVerificationResult.InvalidCode:
                return Result.Failure<ProfileChangeResponse>(AccountErrors.OtpInvalid);
            case OtpVerificationResult.Expired:
                return Result.Failure<ProfileChangeResponse>(AccountErrors.OtpExpired);
            case OtpVerificationResult.RateLimited:
                return Result.Failure<ProfileChangeResponse>(AccountErrors.OtpRateLimited);
        }

        var change = await AddAsync(ProfileChangeRequest.ForContact(user.Id, request.Type, value, _dateTime.UtcNow), cancellationToken);
        return Result.Success(ProfileChangeResponse.From(change));
    }

    public async Task<Result<ProfileChangeResponse>> Handle(SubmitBusinessInfoChangeCommand request, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId!.Value;
        var seller = await _dbContext.SellerProfiles.FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);
        if (seller is null)
        {
            return Result.Failure<ProfileChangeResponse>(ProfileChangeErrors.SellersOnly);
        }

        if (await HasPendingAsync(userId, ProfileChangeType.BusinessInfo, cancellationToken))
        {
            return Result.Failure<ProfileChangeResponse>(ProfileChangeErrors.AlreadyPending(ProfileChangeType.BusinessInfo));
        }

        var businessName = request.BusinessName.Trim();
        var storeAddress = request.StoreAddress?.Trim() ?? string.Empty;
        if (businessName == seller.BusinessName && storeAddress == (seller.StoreAddress ?? string.Empty))
        {
            return Result.Failure<ProfileChangeResponse>(ProfileChangeErrors.NoChange);
        }

        var change = await AddAsync(
            ProfileChangeRequest.ForBusinessInfo(userId, businessName, storeAddress, _dateTime.UtcNow), cancellationToken);
        return Result.Success(ProfileChangeResponse.From(change));
    }

    public async Task<Result<IReadOnlyList<ProfileChangeResponse>>> Handle(GetMyProfileChangesQuery request, CancellationToken cancellationToken)
    {
        var changes = await _dbContext.ProfileChangeRequests
            .Where(r => r.UserId == _currentUser.UserId)
            .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .ToListAsync(cancellationToken);

        return Result.Success<IReadOnlyList<ProfileChangeResponse>>(changes.Select(ProfileChangeResponse.From).ToList());
    }

    public async Task<Result> Handle(CancelProfileChangeCommand request, CancellationToken cancellationToken)
    {
        var change = await _dbContext.ProfileChangeRequests.FirstOrDefaultAsync(
            r => r.Id == request.Id && r.UserId == _currentUser.UserId, cancellationToken);
        if (change is null)
        {
            return Result.Failure(ProfileChangeErrors.NotFound);
        }

        if (change.Status != ProfileChangeStatus.PendingReview)
        {
            return Result.Failure(ProfileChangeErrors.NotPending);
        }

        change.Cancel();
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private async Task<Result> CheckNewValueAsync(User user, ProfileChangeType type, string value, CancellationToken cancellationToken)
    {
        var current = type == ProfileChangeType.Email ? user.Email?.ToLowerInvariant() : user.Phone;
        if (current == value)
        {
            return Result.Failure(ProfileChangeErrors.SameAsCurrent);
        }

        return await ContactValue.InUseByAnotherAsync(_dbContext, user.Id, type, value, cancellationToken)
            ? Result.Failure(ProfileChangeErrors.InUse)
            : Result.Success();
    }

    /// <summary>One pending request per type: a new one can only be filed once the last is reviewed (or withdrawn).</summary>
    private Task<bool> HasPendingAsync(Guid userId, ProfileChangeType type, CancellationToken cancellationToken) =>
        _dbContext.ProfileChangeRequests.AnyAsync(
            r => r.UserId == userId && r.Type == type && r.Status == ProfileChangeStatus.PendingReview, cancellationToken);

    private async Task<ProfileChangeRequest> AddAsync(ProfileChangeRequest change, CancellationToken cancellationToken)
    {
        _dbContext.ProfileChangeRequests.Add(change);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return change;
    }
}
