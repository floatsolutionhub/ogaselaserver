using FluentAssertions;
using Microsoft.Extensions.Options;
using Ogasela.Application.Listings;
using Ogasela.Application.Messaging.GetSafetyNotice;
using Ogasela.Application.Settings;
using Ogasela.Domain.Settings;
using Ogasela.UnitTests.TestSupport;

namespace Ogasela.UnitTests.Settings;

public class MessagingSafetyNoticeTests
{
    private static PlatformSettingsService Settings(TestApplicationDbContext db) =>
        new(db, Options.Create(new ListingSettings()));

    private static async Task<string> NoticeAsync(TestApplicationDbContext db) =>
        (await new GetSafetyNoticeQueryHandler(Settings(db)).Handle(new GetSafetyNoticeQuery(), CancellationToken.None)).Value.Text;

    [Fact]
    public async Task NeverChanged_ShowsTheDefault()
    {
        var db = TestApplicationDbContext.Create();

        (await NoticeAsync(db)).Should().Be("Do not send money for an item you have not seen and verified");
    }

    [Fact]
    public async Task ChangedByAnAdmin_ShowsTheirText()
    {
        var db = TestApplicationDbContext.Create();
        db.PlatformSettings.Add(PlatformSetting.Create(
            PlatformSettingKeys.MessagingSafetyNotice, "Meet in a public place.", Guid.NewGuid(), DateTime.UtcNow));
        await db.SaveChangesAsync(CancellationToken.None);

        (await NoticeAsync(db)).Should().Be("Meet in a public place.");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("Meet in a public place.", true)]
    [InlineData("   ", false)]
    public void Validation_RejectsABlankNotice(string? notice, bool valid) =>
        new UpdatePlatformSettingsCommandValidator()
            .Validate(new UpdatePlatformSettingsCommand(null, null, null, notice))
            .IsValid.Should().Be(valid);

    [Fact]
    public void Validation_RejectsAnOverlongNotice() =>
        new UpdatePlatformSettingsCommandValidator()
            .Validate(new UpdatePlatformSettingsCommand(null, null, null, new string('a', 301)))
            .IsValid.Should().BeFalse();
}
