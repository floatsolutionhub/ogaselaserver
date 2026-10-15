using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Accounts.Interfaces;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Verification.Interfaces;
using Ogasela.Domain.Accounts;
using Ogasela.Shared;

namespace Ogasela.Application.Accounts.ProfilePhoto;

/// <summary>
/// Sets the caller's profile photo from the app's face capture. The photo must pass the same face
/// quality check identity verification uses (exactly one face, clear and well lit) - a photo that
/// fails is deleted and rejected with the reason, so profile photos are always a real, recognisable face.
/// </summary>
public sealed record UploadProfilePhotoCommand(Stream Image, string FileName, string ContentType) : IRequest<Result<ProfilePhotoResponse>>;

public sealed record ProfilePhotoResponse(string Url);

public sealed class UploadProfilePhotoCommandValidator : AbstractValidator<UploadProfilePhotoCommand>
{
    public UploadProfilePhotoCommandValidator()
    {
        RuleFor(x => x.ContentType)
            .Must(t => t is "image/jpeg" or "image/png" or "image/heic" or "image/webp")
            .WithMessage("The photo must be a JPEG, PNG, HEIC or WebP image.");
    }
}

public sealed class UploadProfilePhotoCommandHandler : IRequestHandler<UploadProfilePhotoCommand, Result<ProfilePhotoResponse>>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IProfilePhotoStorage _storage;
    private readonly IFaceVerificationProvider _faceProvider;

    public UploadProfilePhotoCommandHandler(
        IApplicationDbContext dbContext, ICurrentUserService currentUser, IProfilePhotoStorage storage,
        IFaceVerificationProvider faceProvider)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _storage = storage;
        _faceProvider = faceProvider;
    }

    public async Task<Result<ProfilePhotoResponse>> Handle(UploadProfilePhotoCommand request, CancellationToken cancellationToken)
    {
        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == _currentUser.UserId, cancellationToken);
        if (user is null)
        {
            return Result.Failure<ProfilePhotoResponse>(AccountErrors.UserNotFound);
        }

        // The selfie is part of what verification was decided on - frozen once it's granted.
        if (await _dbContext.SellerProfiles.AnyAsync(
                s => s.UserId == user.Id && s.VerificationStatus == VerificationStatus.Verified, cancellationToken))
        {
            return Result.Failure<ProfilePhotoResponse>(AccountErrors.VerifiedIdentityLocked);
        }

        var key = await _storage.UploadAsync(request.FileName, request.ContentType, request.Image, cancellationToken);

        var quality = await _faceProvider.CheckQualityAsync(key, cancellationToken);
        if (!quality.Passed)
        {
            await _storage.DeleteAsync(key, cancellationToken);
            return Result.Failure<ProfilePhotoResponse>(AccountErrors.ProfilePhotoRejected(quality.Reason));
        }

        var previousKey = user.ProfilePhotoS3Key;
        user.SetProfilePhoto(key);
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (previousKey is not null)
        {
            try
            {
                await _storage.DeleteAsync(previousKey, cancellationToken);
            }
            catch (Exception)
            {
                // The new photo is already saved; a leftover old file is harmless.
            }
        }

        return Result.Success(new ProfilePhotoResponse(_storage.GetImageUrl(key)));
    }
}
