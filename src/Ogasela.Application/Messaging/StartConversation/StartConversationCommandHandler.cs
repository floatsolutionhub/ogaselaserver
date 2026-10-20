using MediatR;
using Ogasela.Application.Analytics;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Domain.Messaging;
using Ogasela.Shared;

namespace Ogasela.Application.Messaging.StartConversation;

public sealed class StartConversationCommandHandler : IRequestHandler<StartConversationCommand, Result<ConversationResponse>>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IDateTime _dateTime;
    private readonly IEngagementTracker _engagementTracker;

    public StartConversationCommandHandler(
        IApplicationDbContext dbContext, ICurrentUserService currentUser, IDateTime dateTime, IEngagementTracker engagementTracker)
    {
        _engagementTracker = engagementTracker;
        _dbContext = dbContext;
        _currentUser = currentUser;
        _dateTime = dateTime;
    }

    public async Task<Result<ConversationResponse>> Handle(StartConversationCommand request, CancellationToken cancellationToken)
    {
        var buyerId = _currentUser.UserId!.Value;

        var listing = await _dbContext.Listings.FirstOrDefaultAsync(l => l.Id == request.ListingId, cancellationToken);
        if (listing is null)
        {
            return Result.Failure<ConversationResponse>(MessagingErrors.ListingNotFound);
        }

        var sellerUserId = await _dbContext.SellerProfiles
            .Where(s => s.Id == listing.SellerId)
            .Select(s => s.UserId)
            .FirstOrDefaultAsync(cancellationToken);

        if (buyerId == sellerUserId)
        {
            return Result.Failure<ConversationResponse>(MessagingErrors.CannotMessageOwnListing);
        }

        var blocked = await _dbContext.BlockedUsers.AnyAsync(
            b => (b.BlockerId == sellerUserId && b.BlockedId == buyerId)
                 || (b.BlockerId == buyerId && b.BlockedId == sellerUserId),
            cancellationToken);

        if (blocked)
        {
            return Result.Failure<ConversationResponse>(MessagingErrors.Blocked);
        }

        var existing = await _dbContext.Conversations.FirstOrDefaultAsync(
            c => c.ListingId == request.ListingId && c.BuyerId == buyerId && c.SellerId == sellerUserId,
            cancellationToken);

        var sellerDeleted = await _dbContext.Users.AnyAsync(u => u.Id == sellerUserId && u.DeletedAt != null, cancellationToken);

        if (existing is not null)
        {
            // Reopening a deleted seller's chat still shows the history, closed.
            return Result.Success(ToResponse(existing, buyerId, sellerDeleted));
        }

        if (sellerDeleted)
        {
            return Result.Failure<ConversationResponse>(MessagingErrors.ParticipantDeleted);
        }

        var conversation = Conversation.Start(request.ListingId, buyerId, sellerUserId, _dateTime.UtcNow);
        _dbContext.Conversations.Add(conversation);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Only a brand-new conversation counts - reopening an existing chat isn't a new lead.
        await _engagementTracker.RecordAsync([(listing.Id, listing.SellerId)], ListingEngagement.MessageStart, cancellationToken);

        return Result.Success(ToResponse(conversation, buyerId, false));
    }

    private static ConversationResponse ToResponse(Conversation conversation, Guid currentUserId, bool sellerDeleted) => new(
        conversation.Id,
        conversation.ListingId,
        conversation.BuyerId,
        conversation.SellerId,
        conversation.GetOtherParticipant(currentUserId),
        conversation.CreatedAt,
        conversation.LastMessageAt,
        // A block either way stops the conversation from being started or reopened above.
        IsBlockedByMe: false,
        IsOtherParticipantDeleted: sellerDeleted);
}
