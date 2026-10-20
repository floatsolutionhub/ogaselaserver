using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Domain.Messaging;
using Ogasela.Shared;

namespace Ogasela.Application.Messaging.BlockUser;

public sealed class BlockUserCommandHandler : IRequestHandler<BlockUserCommand, Result>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IDateTime _dateTime;

    public BlockUserCommandHandler(IApplicationDbContext dbContext, ICurrentUserService currentUser, IDateTime dateTime)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _dateTime = dateTime;
    }

    public async Task<Result> Handle(BlockUserCommand request, CancellationToken cancellationToken)
    {
        var blockerId = _currentUser.UserId!.Value;

        if (blockerId == request.TargetUserId)
        {
            return Result.Failure(MessagingErrors.CannotBlockSelf);
        }

        if (!await _dbContext.Users.AnyAsync(u => u.Id == request.TargetUserId, cancellationToken))
        {
            return Result.Failure(MessagingErrors.UserNotFound);
        }

        var alreadyBlocked = await _dbContext.BlockedUsers.AnyAsync(
            b => b.BlockerId == blockerId && b.BlockedId == request.TargetUserId, cancellationToken);

        if (alreadyBlocked)
        {
            return Result.Success();
        }

        _dbContext.BlockedUsers.Add(BlockedUser.Create(blockerId, request.TargetUserId, _dateTime.UtcNow));
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
