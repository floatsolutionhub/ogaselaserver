using MediatR;
using Ogasela.Application.Messaging.StartConversation;
using Ogasela.Shared;

namespace Ogasela.Application.Messaging.GetConversation;

public sealed record GetConversationQuery(Guid ConversationId) : IRequest<Result<ConversationResponse>>;
