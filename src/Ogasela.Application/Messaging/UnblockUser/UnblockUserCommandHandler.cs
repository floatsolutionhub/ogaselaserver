using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Shared;

namespace Ogasela.Application.Messaging.UnblockUser;

/// <summary>Idempotent - unblocking someone who isn't blocked still succeeds.</summary>
public sealed class UnblockUserCommandHandler : IRequestHandler<UnblockUserCommand, Result>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;

    public UnblockUserCommandHandler(IApplicationDbContext dbContext, ICurrentUserService currentUser)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
    }

    public async Task<Result> Handle(UnblockUserCommand request, CancellationToken cancellationToken)
    {
        var blockerId = _currentUser.UserId!.Value;

        var block = await _dbContext.BlockedUsers.FirstOrDefaultAsync(
            b => b.BlockerId == blockerId && b.BlockedId == request.TargetUserId, cancellationToken);

        if (block is null)
        {
            return Result.Success();
        }

        _dbContext.BlockedUsers.Remove(block);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
