using FluentAssertions;
using Ogasela.Application.Messaging.GetUnreadMessageCount;
using Ogasela.Domain.Messaging;
using Ogasela.UnitTests.TestSupport;

namespace Ogasela.UnitTests.Messaging;

public class UnreadMessageCountTests
{
    [Fact]
    public async Task CountsOnlyUnreadMessagesFromOthers_InTheCallersConversations()
    {
        var db = TestApplicationDbContext.Create();
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var mine = db.Conversations.Add(Conversation.Start(Guid.NewGuid(), me, other, now)).Entity;
        var someoneElses = db.Conversations.Add(Conversation.Start(Guid.NewGuid(), Guid.NewGuid(), other, now)).Entity;

        db.Messages.Add(Message.Send(mine.Id, other, "Is it available?", null, now));
        db.Messages.Add(Message.Send(mine.Id, other, "Hello?", null, now));
        var read = db.Messages.Add(Message.Send(mine.Id, other, "Old one", null, now)).Entity;
        read.MarkRead(now);
        db.Messages.Add(Message.Send(mine.Id, me, "My own reply", null, now));
        db.Messages.Add(Message.Send(someoneElses.Id, other, "Not for me", null, now));
        await db.SaveChangesAsync(CancellationToken.None);

        var result = await new GetUnreadMessageCountQueryHandler(db, new FakeCurrentUserService { UserId = me })
            .Handle(new GetUnreadMessageCountQuery(), CancellationToken.None);

        result.Value.Should().Be(2);
    }
}
