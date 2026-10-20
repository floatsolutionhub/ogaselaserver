using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Marketing.Testimonials;
using Ogasela.UnitTests.TestSupport;

namespace Ogasela.UnitTests.Marketing;

public class TestimonialTests
{
    private sealed class RecordingAuditLogger : IAuditLogger
    {
        public List<string> Actions { get; } = [];

        public Task LogAsync(Guid actorId, string action, string targetType, Guid targetId,
            string? beforeJson, string? afterJson, CancellationToken cancellationToken)
        {
            Actions.Add(action);
            return Task.CompletedTask;
        }
    }

    private static readonly Guid StaffId = Guid.NewGuid();

    private static TestimonialHandlers NewHandlers(TestApplicationDbContext db, FakeDateTime? clock = null, RecordingAuditLogger? audit = null) =>
        new(db, new FakeCurrentUserService { UserId = StaffId }, audit ?? new RecordingAuditLogger(), clock ?? new FakeDateTime());

    private static CreateTestimonialCommand Create(bool isPublished = true, bool consent = true) =>
        new(" Sold my laptop before lunch. ", " Chioma A. ", " Seller, Ikeja ", 5, isPublished, consent);

    [Fact]
    public async Task Create_StoresATrimmedTestimonialAndRecordsConsent()
    {
        var db = TestApplicationDbContext.Create();
        var audit = new RecordingAuditLogger();

        var result = await NewHandlers(db, audit: audit).Handle(Create(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var saved = await db.Testimonials.SingleAsync();
        saved.Quote.Should().Be("Sold my laptop before lunch.");
        saved.Name.Should().Be("Chioma A.");
        saved.Role.Should().Be("Seller, Ikeja");
        saved.IsPublished.Should().BeTrue();
        saved.CreatedByUserId.Should().Be(StaffId);
        saved.ConsentConfirmedAt.Should().Be(new FakeDateTime().UtcNow);
        audit.Actions.Should().Equal("Testimonial.Created");
    }

    [Theory]
    [InlineData(false, 5, "Quote", "ConsentConfirmed")]
    [InlineData(true, 0, "Quote", "Rating")]
    [InlineData(true, 6, "Quote", "Rating")]
    [InlineData(true, 5, "", "Quote")]
    public void Create_Validator_RejectsMissingConsentBadRatingOrEmptyText(bool consent, int rating, string quote, string property)
    {
        var command = new CreateTestimonialCommand(quote, "Name", "Buyer, Abuja", rating, true, consent);

        var result = new CreateTestimonialCommandValidator().Validate(command);

        result.Errors.Should().Contain(e => e.PropertyName == property);
    }

    [Fact]
    public async Task GetPublished_ReturnsOnlyPublished_NewestFirst_CappedAtSix()
    {
        var db = TestApplicationDbContext.Create();
        var clock = new FakeDateTime();
        var handlers = NewHandlers(db, clock);
        for (var i = 0; i < 8; i++)
        {
            clock.UtcNow = clock.UtcNow.AddMinutes(1);
            await handlers.Handle(Create(), CancellationToken.None);
        }

        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        await handlers.Handle(Create(isPublished: false), CancellationToken.None);

        var result = await handlers.Handle(new GetPublishedTestimonialsQuery(), CancellationToken.None);

        var newestPublished = await db.Testimonials
            .Where(t => t.IsPublished).OrderByDescending(t => t.CreatedAt).Take(6).Select(t => t.Id).ToListAsync();
        result.Value.Select(t => t.Id).Should().Equal(newestPublished);
    }

    [Fact]
    public async Task SetPublished_HidesItFromThePublicList()
    {
        var db = TestApplicationDbContext.Create();
        var handlers = NewHandlers(db);
        var created = (await handlers.Handle(Create(), CancellationToken.None)).Value;

        var result = await handlers.Handle(new SetTestimonialPublishedCommand(created.Id, false), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await handlers.Handle(new GetPublishedTestimonialsQuery(), CancellationToken.None)).Value.Should().BeEmpty();
        (await handlers.Handle(new GetAllTestimonialsQuery(), CancellationToken.None)).Value.Should().ContainSingle();
    }

    [Fact]
    public async Task Update_ChangesTheTextAndStampsUpdatedAt()
    {
        var db = TestApplicationDbContext.Create();
        var clock = new FakeDateTime();
        var handlers = NewHandlers(db, clock);
        var created = (await handlers.Handle(Create(), CancellationToken.None)).Value;
        clock.UtcNow = clock.UtcNow.AddHours(1);

        var result = await handlers.Handle(
            new UpdateTestimonialCommand(created.Id, "Smooth sale.", "Chioma", "Seller, Lekki", 4), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var saved = await db.Testimonials.SingleAsync();
        saved.Quote.Should().Be("Smooth sale.");
        saved.Role.Should().Be("Seller, Lekki");
        saved.Rating.Should().Be(4);
        saved.UpdatedAt.Should().Be(clock.UtcNow);
    }

    [Fact]
    public async Task Delete_RemovesTheRow_AndUnknownIdsAreNotFound()
    {
        var db = TestApplicationDbContext.Create();
        var handlers = NewHandlers(db);
        var created = (await handlers.Handle(Create(), CancellationToken.None)).Value;

        (await handlers.Handle(new DeleteTestimonialCommand(created.Id), CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await db.Testimonials.CountAsync()).Should().Be(0);

        var again = await handlers.Handle(new DeleteTestimonialCommand(created.Id), CancellationToken.None);
        again.Error.Should().Be(TestimonialErrors.NotFound);
    }
}
