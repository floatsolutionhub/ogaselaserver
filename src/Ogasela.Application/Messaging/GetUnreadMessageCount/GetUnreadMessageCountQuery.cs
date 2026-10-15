using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Shared;

namespace Ogasela.Application.Messaging.GetUnreadMessageCount;

/// <summary>How many messages other people have sent the caller that they haven't read yet, across all
/// their conversations - for the app's Messages tab badge. Opening a thread marks its messages read.</summary>
public sealed record GetUnreadMessageCountQuery : IRequest<Result<int>>;

public sealed class GetUnreadMessageCountQueryHandler : IRequestHandler<GetUnreadMessageCountQuery, Result<int>>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;

    public GetUnreadMessageCountQueryHandler(IApplicationDbContext dbContext, ICurrentUserService currentUser)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
    }

    public async Task<Result<int>> Handle(GetUnreadMessageCountQuery request, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId!.Value;
        var count = await (
            from m in _dbContext.Messages
            join c in _dbContext.Conversations on m.ConversationId equals c.Id
            where (c.BuyerId == userId || c.SellerId == userId) && m.SenderId != userId && m.ReadAt == null
            select m.Id)
            .CountAsync(cancellationToken);
        return Result.Success(count);
    }
}
