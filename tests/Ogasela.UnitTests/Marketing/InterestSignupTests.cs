using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Ogasela.Application.Admin.InterestSignups;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Marketing.Interfaces;
using Ogasela.Application.Marketing.SubmitInterestSignup;
using Ogasela.Application.Notifications;
using Ogasela.Application.Notifications.Interfaces;
using Ogasela.Domain.Marketing;
using Ogasela.UnitTests.TestSupport;

namespace Ogasela.UnitTests.Marketing;

public class InterestSignupTests
{
    private sealed class PassingTurnstile : ITurnstileVerifier
    {
        public Task<bool> VerifyAsync(string token, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class RecordingEmailSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpAuditLogger : IAuditLogger
    {
        public Task LogAsync(Guid actorId, string action, string targetType, Guid targetId,
            string? beforeJson, string? afterJson, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static SubmitInterestSignupCommand Command(
        InterestKind kind = InterestKind.Waitlist, string email = "Ada@Example.com ", string? phone = "+2348031234567") =>
        new(kind, " Ada ", email, phone, "Lagos", InterestIntent.Both, InterestDevicePlatform.IOS, true, "token");

    private static SubmitInterestSignupCommandHandler NewSubmitHandler(TestApplicationDbContext db, RecordingEmailSender email) =>
        new(db, new PassingTurnstile(), email, new FakeDateTime(), NullLogger<SubmitInterestSignupCommandHandler>.Instance);

    [Fact]
    public async Task Submit_StoresANormalisedSignupAndSendsAConfirmation()
    {
        var db = TestApplicationDbContext.Create();
        var email = new RecordingEmailSender();

        var result = await NewSubmitHandler(db, email).Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var saved = await db.InterestSignups.SingleAsync();
        saved.Kind.Should().Be(InterestKind.Waitlist);
        saved.Name.Should().Be("Ada");
        saved.Email.Should().Be("ada@example.com");
        saved.Phone.Should().Be("08031234567");
        saved.Status.Should().Be(InterestSignupStatus.New);
        saved.DevicePlatform.Should().Be(InterestDevicePlatform.IOS);
        email.Sent.Should().ContainSingle().Which.ToEmail.Should().Be("ada@example.com");
    }

    [Fact]
    public async Task Submit_SameEmailTwice_IsSuccessWithOneRowAndOneEmail()
    {
        var db = TestApplicationDbContext.Create();
        var email = new RecordingEmailSender();
        var handler = NewSubmitHandler(db, email);

        await handler.Handle(Command(), CancellationToken.None);
        var second = await handler.Handle(Command(email: "ADA@example.com"), CancellationToken.None);

        second.IsSuccess.Should().BeTrue();
        (await db.InterestSignups.CountAsync()).Should().Be(1);
        email.Sent.Should().HaveCount(1);
    }

    [Fact]
    public async Task Submit_SameEmailOnBothLists_KeepsOneRowPerList()
    {
        var db = TestApplicationDbContext.Create();
        var handler = NewSubmitHandler(db, new RecordingEmailSender());

        await handler.Handle(Command(InterestKind.Waitlist), CancellationToken.None);
        await handler.Handle(Command(InterestKind.Tester), CancellationToken.None);

        (await db.InterestSignups.Select(s => s.Kind).ToListAsync())
            .Should().BeEquivalentTo([InterestKind.Waitlist, InterestKind.Tester]);
    }

    [Fact]
    public void Validator_RequiresConsentAndADeviceOnBothLists()
    {
        var validator = new SubmitInterestSignupCommandValidator();

        validator.Validate(Command() with { Consent = false }).IsValid.Should().BeFalse();
        validator.Validate(Command(InterestKind.Tester) with { DevicePlatform = null }).IsValid.Should().BeFalse();
        validator.Validate(Command(InterestKind.Waitlist) with { DevicePlatform = null }).IsValid.Should().BeFalse();
        validator.Validate(Command(phone: "12345")).IsValid.Should().BeFalse();
        validator.Validate(Command(phone: null)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Admin_FiltersUpdatesAndDeletes()
    {
        var db = TestApplicationDbContext.Create();
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var ada = InterestSignup.Create(InterestKind.Waitlist, "Ada", "ada@example.com", null, null, null, null, now);
        var bayo = InterestSignup.Create(InterestKind.Tester, "Bayo", "bayo@example.com", null, null, null, InterestDevicePlatform.IOS, now.AddHours(1));
        db.InterestSignups.AddRange(ada, bayo);
        await db.SaveChangesAsync(CancellationToken.None);
        var handler = new AdminInterestSignupHandlers(
            db, new FakeCurrentUserService { UserId = Guid.NewGuid() }, new NoOpAuditLogger(), new FakeDateTime());

        var waitlist = await handler.Handle(
            new GetAdminInterestSignupsQuery(InterestKind.Waitlist, null, "ADA", 1, 25), CancellationToken.None);
        waitlist.Value.Items.Should().ContainSingle().Which.Id.Should().Be(ada.Id);
        var iPhones = await handler.Handle(
            new GetAdminInterestSignupsQuery(null, null, null, 1, 25, InterestDevicePlatform.IOS), CancellationToken.None);
        iPhones.Value.Items.Should().ContainSingle().Which.Id.Should().Be(bayo.Id);

        (await handler.Handle(new UpdateInterestSignupCommand(ada.Id, InterestSignupStatus.Invited, " Sent launch email "), CancellationToken.None))
            .IsSuccess.Should().BeTrue();
        var updated = await db.InterestSignups.SingleAsync(s => s.Id == ada.Id);
        updated.Status.Should().Be(InterestSignupStatus.Invited);
        updated.Notes.Should().Be("Sent launch email");

        var summary = await handler.Handle(new GetInterestSignupSummaryQuery(), CancellationToken.None);
        summary.Value.Should().Be(new InterestSignupSummary(1, 0, 1, 1, 2));

        (await handler.Handle(new DeleteInterestSignupCommand(ada.Id), CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await db.InterestSignups.AnyAsync(s => s.Id == ada.Id)).Should().BeFalse();
        (await handler.Handle(new DeleteInterestSignupCommand(ada.Id), CancellationToken.None))
            .Error.Should().Be(InterestSignupErrors.NotFound);
    }

    [Fact]
    public async Task Export_DefusesSpreadsheetFormulas()
    {
        var db = TestApplicationDbContext.Create();
        db.InterestSignups.Add(InterestSignup.Create(
            InterestKind.Waitlist, "=HYPERLINK(\"x\")", "a@b.com", null, null, null, null, DateTime.UtcNow));
        await db.SaveChangesAsync(CancellationToken.None);
        var handler = new AdminInterestSignupHandlers(
            db, new FakeCurrentUserService { UserId = Guid.NewGuid() }, new NoOpAuditLogger(), new FakeDateTime());

        var csv = (await handler.Handle(new ExportInterestSignupsQuery(null, null, null), CancellationToken.None)).Value;

        csv.Should().Contain("\"'=HYPERLINK(\"\"x\"\")\"");
    }
}
