using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Messaging.Interfaces;
using Ogasela.Domain.Messaging;
using Ogasela.Shared;

namespace Ogasela.Application.Messaging.SendMessage;

public sealed class SendMessageCommandHandler : IRequestHandler<SendMessageCommand, Result<MessageResponse>>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IDateTime _dateTime;
    private readonly IMessagePushNotifier _pushNotifier;
    private readonly ILogger<SendMessageCommandHandler> _logger;
    private readonly IPublisher _publisher;

    public SendMessageCommandHandler(
        IApplicationDbContext dbContext, ICurrentUserService currentUser, IDateTime dateTime,
        IMessagePushNotifier pushNotifier, ILogger<SendMessageCommandHandler> logger, IPublisher publisher)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _dateTime = dateTime;
        _pushNotifier = pushNotifier;
        _logger = logger;
        _publisher = publisher;
    }

    public async Task<Result<MessageResponse>> Handle(SendMessageCommand request, CancellationToken cancellationToken)
    {
        var senderId = _currentUser.UserId!.Value;

        var conversation = await _dbContext.Conversations
            .FirstOrDefaultAsync(c => c.Id == request.ConversationId, cancellationToken);

        if (conversation is null)
        {
            return Result.Failure<MessageResponse>(MessagingErrors.ConversationNotFound);
        }

        if (!conversation.HasParticipant(senderId))
        {
            return Result.Failure<MessageResponse>(MessagingErrors.NotParticipant);
        }

        var recipientId = conversation.GetOtherParticipant(senderId);

        // A deleted account's chats are kept for reference only - nobody is left to read new messages.
        if (await _dbContext.Users.AnyAsync(u => u.Id == recipientId && u.DeletedAt != null, cancellationToken))
        {
            return Result.Failure<MessageResponse>(MessagingErrors.ParticipantDeleted);
        }

        var blockedByRecipient = await _dbContext.BlockedUsers.AnyAsync(
            b => b.BlockerId == recipientId && b.BlockedId == senderId, cancellationToken);

        if (blockedByRecipient)
        {
            return Result.Failure<MessageResponse>(MessagingErrors.Blocked);
        }

        var senderBlockedRecipient = await _dbContext.BlockedUsers.AnyAsync(
            b => b.BlockerId == senderId && b.BlockedId == recipientId, cancellationToken);

        if (senderBlockedRecipient)
        {
            return Result.Failure<MessageResponse>(MessagingErrors.YouBlockedThisUser);
        }

        var now = _dateTime.UtcNow;
        var message = Message.Send(conversation.Id, senderId, request.Content, request.ImageUrl, now);
        _dbContext.Messages.Add(message);

        conversation.TouchLastMessageAt(now);

        await _dbContext.SaveChangesAsync(cancellationToken);

        var response = ToResponse(message);

        try
        {
            // Store-and-forward: the message is already persisted above regardless of whether
            // the recipient is currently connected, or this push fails for any reason.
            await _pushNotifier.PushNewMessageAsync(recipientId, response, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push new message {MessageId} to recipient {RecipientId}", message.Id, recipientId);
        }

        await _publisher.Publish(
            new NewMessageEvent(
                conversation.Id, conversation.ListingId, recipientId, senderId,
                string.IsNullOrWhiteSpace(request.Content) ? "Photo" : request.Content.Trim()),
            cancellationToken);

        return Result.Success(response);
    }

    private static MessageResponse ToResponse(Message message) => new(
        message.Id, message.ConversationId, message.SenderId, message.Content, message.ImageUrl, message.SentAt, message.ReadAt);
}
