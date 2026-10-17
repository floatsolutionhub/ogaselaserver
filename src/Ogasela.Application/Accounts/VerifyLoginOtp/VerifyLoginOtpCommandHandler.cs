using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Accounts.Interfaces;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Shared;

namespace Ogasela.Application.Accounts.VerifyLoginOtp;

public sealed class VerifyLoginOtpCommandHandler : IRequestHandler<VerifyLoginOtpCommand, Result<AuthTokenResponse>>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly IOtpService _otpService;
    private readonly AuthTokenIssuer _tokenIssuer;
    private readonly IDateTime _dateTime;

    public VerifyLoginOtpCommandHandler(
        IApplicationDbContext dbContext, IOtpService otpService, AuthTokenIssuer tokenIssuer, IDateTime dateTime)
    {
        _dbContext = dbContext;
        _otpService = otpService;
        _tokenIssuer = tokenIssuer;
        _dateTime = dateTime;
    }

    public async Task<Result<AuthTokenResponse>> Handle(VerifyLoginOtpCommand request, CancellationToken cancellationToken)
    {
        var user = await _dbContext.Users.FirstOrDefaultAsync(
            u => (request.Phone != null && u.Phone == request.Phone) ||
                 (request.Email != null && u.Email == request.Email),
            cancellationToken);

        var byPhone = request.Phone is not null;
        if (user is null || (byPhone ? string.IsNullOrWhiteSpace(user.Phone) : string.IsNullOrWhiteSpace(user.Email)))
        {
            return Result.Failure<AuthTokenResponse>(AccountErrors.InvalidCredentials);
        }

        var verifyResult = await _otpService.VerifyAsync(
            LoginOtpKey.For(byPhone ? user.Phone : null, byPhone ? null : user.Email), request.Code, cancellationToken);

        var errorResult = verifyResult switch
        {
            OtpVerificationResult.Success => (Error?)null,
            OtpVerificationResult.InvalidCode => AccountErrors.OtpInvalid,
            OtpVerificationResult.Expired => AccountErrors.OtpExpired,
            OtpVerificationResult.RateLimited => AccountErrors.OtpRateLimited,
            _ => AccountErrors.OtpInvalid
        };

        if (errorResult is not null)
        {
            return Result.Failure<AuthTokenResponse>(errorResult);
        }

        if (user.IsSuspended)
        {
            return Result.Failure<AuthTokenResponse>(AccountErrors.AccountSuspended);
        }

        // The code was always sent to the identifier they signed in with, so entering it proves
        // they own it. Saved by IssueAsync below along with the refresh token.
        if (byPhone)
        {
            user.MarkPhoneVerified(_dateTime.UtcNow);
        }
        else
        {
            user.MarkEmailVerified(_dateTime.UtcNow);
        }

        var response = await _tokenIssuer.IssueAsync(user, tokenToRotate: null, cancellationToken);
        return Result.Success(response);
    }
}
