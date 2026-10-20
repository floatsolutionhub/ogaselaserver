using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Messaging.StartConversation;
using Ogasela.Shared;

namespace Ogasela.Application.Messaging.GetConversation;

public sealed class GetConversationQueryHandler : IRequestHandler<GetConversationQuery, Result<ConversationResponse>>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;

    public GetConversationQueryHandler(IApplicationDbContext dbContext, ICurrentUserService currentUser)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
    }

    public async Task<Result<ConversationResponse>> Handle(GetConversationQuery request, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId!.Value;

        var conversation = await _dbContext.Conversations
            .FirstOrDefaultAsync(c => c.Id == request.ConversationId, cancellationToken);

        if (conversation is null)
        {
            return Result.Failure<ConversationResponse>(MessagingErrors.ConversationNotFound);
        }

        if (!conversation.HasParticipant(userId))
        {
            return Result.Failure<ConversationResponse>(MessagingErrors.NotParticipant);
        }

        var otherId = conversation.GetOtherParticipant(userId);
        var isBlockedByMe = await _dbContext.BlockedUsers.AnyAsync(
            b => b.BlockerId == userId && b.BlockedId == otherId, cancellationToken);
        var isOtherDeleted = await _dbContext.Users.AnyAsync(u => u.Id == otherId && u.DeletedAt != null, cancellationToken);

        return Result.Success(new ConversationResponse(
            conversation.Id, conversation.ListingId, conversation.BuyerId, conversation.SellerId, otherId,
            conversation.CreatedAt, conversation.LastMessageAt, isBlockedByMe, isOtherDeleted));
    }
}
