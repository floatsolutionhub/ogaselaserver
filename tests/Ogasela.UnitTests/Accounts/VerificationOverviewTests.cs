using FluentAssertions;
using Ogasela.Application.Accounts;
using Ogasela.Application.Accounts.Interfaces;
using Ogasela.Application.Accounts.UserDocuments;
using Ogasela.Application.Accounts.VerificationOverview;
using Ogasela.Domain.Accounts;
using Ogasela.UnitTests.TestSupport;

namespace Ogasela.UnitTests.Accounts;

public class VerificationOverviewTests
{
    private sealed class FakeDocumentStorage : IUserDocumentStorage
    {
        public Task<string> UploadAsync(string fileName, string contentType, Stream content, CancellationToken cancellationToken) =>
            Task.FromResult($"user-documents/{fileName}");

        public Task DeleteAsync(string key, CancellationToken cancellationToken) => Task.CompletedTask;

        public string GetImageUrl(string key) => $"https://files.test/{key}";
    }

    private static UserDocument AddDocument(TestApplicationDbContext db, Guid userId, UserDocumentType type) =>
        db.UserDocuments.Add(UserDocument.Create(userId, type,
            type == UserDocumentType.IdCard ? IdDocumentType.NationalIdCard : null,
            type == UserDocumentType.IdCard ? "A1234567" : null,
            $"user-documents/{type}.jpg", $"{type}.jpg", "image/jpeg", DateTime.UtcNow)).Entity;

    private static async Task<VerificationOverviewResponse> OverviewAsync(TestApplicationDbContext db, Guid userId)
    {
        var result = await new GetVerificationOverviewQueryHandler(db, new FakeCurrentUserService { UserId = userId })
            .Handle(new GetVerificationOverviewQuery(), CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        return result.Value;
    }

    [Fact]
    public async Task ABuyerWithNothingIn_IsNotStarted_AndHasNoBusinessTrack()
    {
        var db = TestApplicationDbContext.Create();
        var buyer = db.Users.Add(User.Create("08031234567", null, "hash", UserRole.Buyer, "Bola")).Entity;
        await db.SaveChangesAsync(CancellationToken.None);

        var overview = await OverviewAsync(db, buyer.Id);

        overview.Personal.Status.Should().Be("NotStarted");
        overview.Personal.Items.Select(i => i.Key).Should().Equal("Selfie", "IdCard", "UtilityBill");
        overview.Personal.Items.Should().OnlyContain(i => i.State == "Missing");
        overview.Business.Should().BeNull();
    }

    [Fact]
    public async Task SomeItemsIn_IsInProgress_AndARejectedOneCountsAsOutstanding()
    {
        var db = TestApplicationDbContext.Create();
        var user = db.Users.Add(User.Create("08031234567", null, "hash", UserRole.Seller, "Ada")).Entity;
        user.SetProfilePhoto("profile-photos/ada.jpg");
        db.SellerProfiles.Add(SellerProfile.Create(user.Id, "Ada's Gadgets", null, null));
        AddDocument(db, user.Id, UserDocumentType.IdCard);
        AddDocument(db, user.Id, UserDocumentType.UtilityBill).Reject("Too blurry.", DateTime.UtcNow);
        await db.SaveChangesAsync(CancellationToken.None);

        var overview = await OverviewAsync(db, user.Id);

        overview.Personal.Status.Should().Be("InProgress");
        overview.Personal.Items.Single(i => i.Key == "UtilityBill").Should().Be(new VerificationItem("UtilityBill", "Rejected", "Too blurry."));
        overview.Business!.Status.Should().Be("NotStarted");
    }

    [Fact]
    public async Task EverythingIn_IsPendingReview_ThenVerifiedOnceStaffDecide()
    {
        var db = TestApplicationDbContext.Create();
        var user = db.Users.Add(User.Create("08031234567", null, "hash", UserRole.Seller, "Ada")).Entity;
        user.SetProfilePhoto("profile-photos/ada.jpg");
        var seller = db.SellerProfiles.Add(SellerProfile.Create(user.Id, "Ada's Gadgets", null, null)).Entity;
        foreach (var type in Enum.GetValues<UserDocumentType>())
        {
            AddDocument(db, user.Id, type);
        }

        await db.SaveChangesAsync(CancellationToken.None);

        var pending = await OverviewAsync(db, user.Id);
        pending.Personal.Status.Should().Be("PendingReview");
        pending.Business!.Status.Should().Be("PendingReview");

        seller.UpdateVerificationStatus(VerificationStatus.Verified, DateTime.UtcNow);
        seller.VerifyBusiness(DateTime.UtcNow);
        await db.SaveChangesAsync(CancellationToken.None);

        var verified = await OverviewAsync(db, user.Id);
        verified.Personal.Status.Should().Be("Verified");
        verified.Business!.Status.Should().Be("Verified");
    }

    [Theory]
    [InlineData(UserDocumentType.IdCard, false, "User.VerifiedIdentityLocked")]
    [InlineData(UserDocumentType.UtilityBill, false, "User.VerifiedIdentityLocked")]
    [InlineData(UserDocumentType.BusinessRegistration, true, "User.VerifiedBusinessLocked")]
    [InlineData(UserDocumentType.OfficeUtilityBill, true, "User.VerifiedBusinessLocked")]
    public async Task Uploads_AreRefusedOnceTheirTrackIsVerified(UserDocumentType type, bool businessTrack, string errorCode)
    {
        var db = TestApplicationDbContext.Create();
        var user = db.Users.Add(User.Create("08031234567", null, "hash", UserRole.Seller, "Ada")).Entity;
        var seller = db.SellerProfiles.Add(SellerProfile.Create(user.Id, "Ada's Gadgets", null, null)).Entity;
        seller.UpdateVerificationStatus(VerificationStatus.Verified, DateTime.UtcNow);
        if (businessTrack)
        {
            seller.VerifyBusiness(DateTime.UtcNow);
        }

        await db.SaveChangesAsync(CancellationToken.None);
        var handler = new UserDocumentHandlers(
            db, new FakeCurrentUserService { UserId = user.Id }, new FakeDocumentStorage(), new FakeDateTime());

        var result = await handler.Handle(
            new UploadUserDocumentCommand(type,
                type == UserDocumentType.IdCard ? IdDocumentType.NationalIdCard : null,
                type == UserDocumentType.IdCard ? "A1234567" : null,
                Stream.Null, "doc.pdf", "application/pdf"),
            CancellationToken.None);

        result.Error.Code.Should().Be(errorCode);
    }

    [Fact]
    public async Task BusinessUploads_StayOpen_WhileOnlyThePersonIsVerified()
    {
        var db = TestApplicationDbContext.Create();
        var user = db.Users.Add(User.Create("08031234567", null, "hash", UserRole.Seller, "Ada")).Entity;
        var seller = db.SellerProfiles.Add(SellerProfile.Create(user.Id, "Ada's Gadgets", null, null)).Entity;
        seller.UpdateVerificationStatus(VerificationStatus.Verified, DateTime.UtcNow);
        await db.SaveChangesAsync(CancellationToken.None);
        var handler = new UserDocumentHandlers(
            db, new FakeCurrentUserService { UserId = user.Id }, new FakeDocumentStorage(), new FakeDateTime());

        var result = await handler.Handle(
            new UploadUserDocumentCommand(UserDocumentType.BusinessRegistration, null, null, Stream.Null, "cac.pdf", "application/pdf"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }
}
