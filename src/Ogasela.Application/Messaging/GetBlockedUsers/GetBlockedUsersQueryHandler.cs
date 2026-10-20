using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Shared;

namespace Ogasela.Application.Messaging.GetBlockedUsers;

/// <summary>The caller's blocked users, most recently blocked first.</summary>
public sealed class GetBlockedUsersQueryHandler : IRequestHandler<GetBlockedUsersQuery, Result<IReadOnlyList<BlockedUserResponse>>>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;

    public GetBlockedUsersQueryHandler(IApplicationDbContext dbContext, ICurrentUserService currentUser)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
    }

    public async Task<Result<IReadOnlyList<BlockedUserResponse>>> Handle(GetBlockedUsersQuery request, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId!.Value;

        var items = await _dbContext.BlockedUsers
            .Where(b => b.BlockerId == userId)
            .OrderByDescending(b => b.CreatedAt)
            .Select(b => new BlockedUserResponse(
                b.BlockedId,
                _dbContext.SellerProfiles.Where(s => s.UserId == b.BlockedId).Select(s => s.BusinessName).FirstOrDefault()
                    ?? _dbContext.Users.Where(u => u.Id == b.BlockedId).Select(u => u.Name).FirstOrDefault(),
                b.CreatedAt))
            .ToListAsync(cancellationToken);

        return Result.Success<IReadOnlyList<BlockedUserResponse>>(items);
    }
}
