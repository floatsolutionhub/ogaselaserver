using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Accounts.Interfaces;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Shared;

namespace Ogasela.Application.Accounts.VerifyOtp;

public sealed class VerifyOtpCommandHandler : IRequestHandler<VerifyOtpCommand, Result>
{
    private readonly IOtpService _otpService;
    private readonly IApplicationDbContext _dbContext;
    private readonly IDateTime _dateTime;

    public VerifyOtpCommandHandler(IOtpService otpService, IApplicationDbContext dbContext, IDateTime dateTime)
    {
        _otpService = otpService;
        _dbContext = dbContext;
        _dateTime = dateTime;
    }

    public async Task<Result> Handle(VerifyOtpCommand request, CancellationToken cancellationToken)
    {
        var result = await _otpService.VerifyAsync(request.Phone, request.Code, cancellationToken);

        if (result == OtpVerificationResult.Success)
        {
            var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Phone == request.Phone, cancellationToken);
            if (user is not null)
            {
                user.MarkPhoneVerified(_dateTime.UtcNow);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        return result switch
        {
            OtpVerificationResult.Success => Result.Success(),
            OtpVerificationResult.InvalidCode => Result.Failure(AccountErrors.OtpInvalid),
            OtpVerificationResult.Expired => Result.Failure(AccountErrors.OtpExpired),
            OtpVerificationResult.RateLimited => Result.Failure(AccountErrors.OtpRateLimited),
            _ => Result.Failure(AccountErrors.OtpInvalid)
        };
    }
}
