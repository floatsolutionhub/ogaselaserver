using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Ogasela.Api.Common;
using Ogasela.Api.Contracts.Messaging;
using Ogasela.Api.RateLimiting;
using Ogasela.Application.Messaging.GetConversations;
using Ogasela.Application.Messaging.GetMessages;
using Ogasela.Application.Messaging.GetSafetyNotice;
using Ogasela.Application.Messaging.GetUnreadMessageCount;
using Ogasela.Application.Messaging.SendMessage;
using Ogasela.Application.Messaging.StartConversation;

namespace Ogasela.Api.Controllers.Messaging;

[ApiController]
[Authorize]
public sealed class ConversationsController : ControllerBase
{
    private readonly ISender _sender;

    public ConversationsController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>Starts (or returns the existing) conversation between the caller and a listing's seller. A seller can't start a conversation on their own listing.</summary>
    [HttpPost("api/v1/conversations")]
    public async Task<IActionResult> Start(StartConversationRequest request, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new StartConversationCommand(request.ListingId), cancellationToken);
        return result.ToActionResult(this);
    }

    /// <summary>The caller's own conversation list, most recently active first.</summary>
    [HttpGet("api/v1/conversations")]
    public async Task<IActionResult> GetConversations(
        [FromQuery] int page, [FromQuery] int pageSize, CancellationToken cancellationToken)
    {
        var query = new GetConversationsQuery(page <= 0 ? 1 : page, pageSize <= 0 ? 20 : pageSize);
        var result = await _sender.Send(query, cancellationToken);
        return result.ToActionResult(this);
    }

    /// <summary>The safety caption shown above the message box in every chat (SuperAdmin-editable).</summary>
    [HttpGet("api/v1/conversations/safety-notice")]
    public async Task<IActionResult> GetSafetyNotice(CancellationToken cancellationToken) =>
        (await _sender.Send(new GetSafetyNoticeQuery(), cancellationToken)).ToActionResult(this);

    /// <summary>Unread messages sent to the caller across all their conversations, for the Messages tab badge.</summary>
    [HttpGet("api/v1/conversations/unread-count")]
    public async Task<IActionResult> GetUnreadCount(CancellationToken cancellationToken) =>
        (await _sender.Send(new GetUnreadMessageCountQuery(), cancellationToken)).ToActionResult(this);

    /// <summary>Message history for a conversation the caller is a participant in.</summary>
    [HttpGet("api/v1/conversations/{id:guid}/messages")]
    public async Task<IActionResult> GetMessages(
        Guid id, [FromQuery] int page, [FromQuery] int pageSize, CancellationToken cancellationToken)
    {
        var query = new GetMessagesQuery(id, page <= 0 ? 1 : page, pageSize <= 0 ? 30 : pageSize);
        var result = await _sender.Send(query, cancellationToken);
        return result.ToActionResult(this);
    }

    /// <summary>Sends a message. Rejected only if the recipient has blocked the caller - the caller having blocked the recipient does not stop the caller's own outgoing messages (see UsersController.Block).</summary>
    [HttpPost("api/v1/conversations/{id:guid}/messages")]
    [EnableRateLimiting(RateLimitingExtensions.MessagingPolicy)]
    public async Task<IActionResult> SendMessage(Guid id, SendMessageRequest request, CancellationToken cancellationToken)
    {
        var command = new SendMessageCommand(id, request.Content, request.ImageUrl);
        var result = await _sender.Send(command, cancellationToken);
        return result.ToActionResult(this);
    }
}
