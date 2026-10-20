using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Messaging.StartConversation;
using Ogasela.Shared;

namespace Ogasela.Application.Messaging.GetConversations;

public sealed class GetConversationsQueryHandler : IRequestHandler<GetConversationsQuery, Result<PagedConversationsResponse>>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;

    public GetConversationsQueryHandler(IApplicationDbContext dbContext, ICurrentUserService currentUser)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
    }

    public async Task<Result<PagedConversationsResponse>> Handle(GetConversationsQuery request, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId!.Value;
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);

        var query = _dbContext.Conversations.Where(c => c.BuyerId == userId || c.SellerId == userId);

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(c => c.LastMessageAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new ConversationResponse(
                c.Id, c.ListingId, c.BuyerId, c.SellerId, c.BuyerId == userId ? c.SellerId : c.BuyerId,
                c.CreatedAt, c.LastMessageAt,
                _dbContext.BlockedUsers.Any(b =>
                    b.BlockerId == userId && b.BlockedId == (c.BuyerId == userId ? c.SellerId : c.BuyerId)),
                _dbContext.Users.Any(u => u.Id == (c.BuyerId == userId ? c.SellerId : c.BuyerId) && u.DeletedAt != null)))
            .ToListAsync(cancellationToken);

        return Result.Success(new PagedConversationsResponse(items, page, pageSize, totalCount));
    }
}
