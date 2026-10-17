using System.Text.RegularExpressions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Accounts.Interfaces;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Domain.Accounts;
using Ogasela.Shared;

namespace Ogasela.Application.Accounts.UserDocuments;

/// <summary>Uploads an ID card (with its type and number), a utility bill or - sellers only - a business registration
/// document or office utility bill: an image or a PDF. Each goes to staff review.</summary>
public sealed record UploadUserDocumentCommand(
    UserDocumentType Type,
    IdDocumentType? IdType,
    string? IdNumber,
    Stream File,
    string FileName,
    string ContentType) : IRequest<Result<UserDocumentResponse>>;

/// <summary>The caller's documents, newest first.</summary>
public sealed record GetMyDocumentsQuery : IRequest<Result<IReadOnlyList<UserDocumentResponse>>>;

/// <summary>IdNumber is masked to its last 4 characters; FileUrl is a short-lived link to view the file.</summary>
public sealed record UserDocumentResponse(
    Guid Id,
    UserDocumentType Type,
    IdDocumentType? IdType,
    string? IdNumber,
    string FileName,
    string ContentType,
    UserDocumentStatus Status,
    string FileUrl,
    DateTime CreatedAt,
    string? ReviewNote = null);

public sealed partial class UploadUserDocumentCommandValidator : AbstractValidator<UploadUserDocumentCommand>
{
    private static readonly string[] AllowedContentTypes =
        ["image/jpeg", "image/png", "image/heic", "image/webp", "application/pdf"];

    public UploadUserDocumentCommandValidator()
    {
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.ContentType)
            .Must(t => AllowedContentTypes.Contains(t))
            .WithMessage("Upload a photo (JPEG, PNG, HEIC, WebP) or a PDF.");

        When(x => x.Type == UserDocumentType.IdCard, () =>
        {
            RuleFor(x => x.IdType).NotNull().WithMessage("Choose the type of ID.").IsInEnum();
            RuleFor(x => x.IdNumber)
                .NotEmpty().WithMessage("Enter the ID number.")
                .Must(n => n is not null && IdNumberPattern().IsMatch(n.Trim()))
                .WithMessage("The ID number should be 5-20 letters or digits.");
        });

        When(x => x.Type != UserDocumentType.IdCard, () =>
        {
            RuleFor(x => x.IdType).Null();
            RuleFor(x => x.IdNumber).Empty();
        });
    }

    [GeneratedRegex("^[A-Za-z0-9]{5,20}$")]
    private static partial Regex IdNumberPattern();
}

public sealed class UserDocumentHandlers :
    IRequestHandler<UploadUserDocumentCommand, Result<UserDocumentResponse>>,
    IRequestHandler<GetMyDocumentsQuery, Result<IReadOnlyList<UserDocumentResponse>>>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IUserDocumentStorage _storage;
    private readonly IDateTime _dateTime;

    public UserDocumentHandlers(
        IApplicationDbContext dbContext, ICurrentUserService currentUser, IUserDocumentStorage storage, IDateTime dateTime)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _storage = storage;
        _dateTime = dateTime;
    }

    public async Task<Result<UserDocumentResponse>> Handle(UploadUserDocumentCommand request, CancellationToken cancellationToken)
    {
        if (request.Type is UserDocumentType.BusinessRegistration or UserDocumentType.OfficeUtilityBill
            && !await _dbContext.SellerProfiles.AnyAsync(s => s.UserId == _currentUser.UserId, cancellationToken))
        {
            return Result.Failure<UserDocumentResponse>(
                "UserDocument.SellersOnly", "Only seller accounts can upload business documents.");
        }

        // The ID card and utility bill are what verification was decided on - frozen once it's granted.
        if (request.Type is UserDocumentType.IdCard or UserDocumentType.UtilityBill
            && await _dbContext.SellerProfiles.AnyAsync(
                s => s.UserId == _currentUser.UserId && s.VerificationStatus == VerificationStatus.Verified, cancellationToken))
        {
            return Result.Failure<UserDocumentResponse>(AccountErrors.VerifiedIdentityLocked);
        }

        // Likewise the business documents once the business is verified.
        if (request.Type is UserDocumentType.BusinessRegistration or UserDocumentType.OfficeUtilityBill
            && await _dbContext.SellerProfiles.AnyAsync(
                s => s.UserId == _currentUser.UserId && s.BusinessVerifiedAt != null, cancellationToken))
        {
            return Result.Failure<UserDocumentResponse>(AccountErrors.VerifiedBusinessLocked);
        }

        // One submission per document type at a time: wait for the pending one to be reviewed.
        if (await _dbContext.UserDocuments.AnyAsync(
                d => d.UserId == _currentUser.UserId && d.Type == request.Type && d.Status == UserDocumentStatus.PendingReview,
                cancellationToken))
        {
            return Result.Failure<UserDocumentResponse>(AccountErrors.DocumentAlreadyPending);
        }

        var key = await _storage.UploadAsync(request.FileName, request.ContentType, request.File, cancellationToken);
        var document = UserDocument.Create(
            _currentUser.UserId!.Value,
            request.Type,
            request.Type == UserDocumentType.IdCard ? request.IdType : null,
            request.Type == UserDocumentType.IdCard ? request.IdNumber!.Trim().ToUpperInvariant() : null,
            key,
            Path.GetFileName(request.FileName),
            request.ContentType,
            _dateTime.UtcNow);

        _dbContext.UserDocuments.Add(document);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success(ToResponse(document));
    }

    public async Task<Result<IReadOnlyList<UserDocumentResponse>>> Handle(GetMyDocumentsQuery request, CancellationToken cancellationToken)
    {
        var documents = await _dbContext.UserDocuments
            .Where(d => d.UserId == _currentUser.UserId)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(cancellationToken);

        return Result.Success<IReadOnlyList<UserDocumentResponse>>(documents.Select(ToResponse).ToList());
    }

    private UserDocumentResponse ToResponse(UserDocument d) => new(
        d.Id,
        d.Type,
        d.IdType,
        d.IdNumber is null ? null : Mask(d.IdNumber),
        d.FileName,
        d.ContentType,
        d.Status,
        _storage.GetImageUrl(d.FileS3Key),
        d.CreatedAt,
        d.ReviewNote);

    private static string Mask(string idNumber) =>
        idNumber.Length <= 4 ? idNumber : new string('•', idNumber.Length - 4) + idNumber[^4..];
}
