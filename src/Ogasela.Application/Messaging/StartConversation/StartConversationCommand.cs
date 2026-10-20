using MediatR;
using Ogasela.Shared;

namespace Ogasela.Application.Messaging.StartConversation;

public sealed record StartConversationCommand(Guid ListingId) : IRequest<Result<ConversationResponse>>;

/// <summary>
/// IsBlockedByMe: the caller has blocked the other participant - the chat stays readable but closed until they unblock.
/// IsOtherParticipantDeleted: the other person deleted their account - the chat stays readable for reference, but closed for good.
/// </summary>
public sealed record ConversationResponse(
    Guid Id, Guid ListingId, Guid BuyerId, Guid SellerId, Guid OtherParticipantId, DateTime CreatedAt, DateTime LastMessageAt,
    bool IsBlockedByMe, bool IsOtherParticipantDeleted = false);
