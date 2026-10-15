using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Admin.Users;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Domain.Accounts;
using Ogasela.UnitTests.TestSupport;

namespace Ogasela.UnitTests.Admin;

public class SetSellerVerificationTests
{
    private sealed class NoOpAuditLogger : IAuditLogger
    {
        public Task LogAsync(Guid actorId, string action, string targetType, Guid targetId,
            string? beforeJson, string? afterJson, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static async Task<(AdminUserCommandHandlers Handler, TestApplicationDbContext Db, SellerProfile Seller)> SetupAsync()
    {
        var db = TestApplicationDbContext.Create();
        var user = User.Create("08031234567", "ada@example.com", "hash", UserRole.Seller, "Ada");
        user.SetProfilePhoto("profile-photos/ada.jpg");
        db.Users.Add(user);
        var seller = SellerProfile.Create(user.Id, "Ada's Gadgets", null, null);
        db.SellerProfiles.Add(seller);
        AddApprovedPersonalDocuments(db, user.Id);
        await db.SaveChangesAsync(CancellationToken.None);
        return (NewHandler(db), db, seller);
    }

    private static AdminUserCommandHandlers NewHandler(TestApplicationDbContext db) => new(
        db, new FakeCurrentUserService { UserId = Guid.NewGuid() }, new NoOpAuditLogger(), new FakeDateTime(),
        new FakePublisher());

    private static UserDocument AddApproved(TestApplicationDbContext db, Guid userId, UserDocumentType type)
    {
        var document = UserDocument.Create(userId, type,
            type == UserDocumentType.IdCard ? IdDocumentType.NationalIdCard : null,
            type == UserDocumentType.IdCard ? "A1234567" : null,
            $"user-documents/{type}.jpg", $"{type}.jpg", "image/jpeg", DateTime.UtcNow);
        document.Approve(DateTime.UtcNow);
        db.UserDocuments.Add(document);
        return document;
    }

    private static void AddApprovedPersonalDocuments(TestApplicationDbContext db, Guid userId)
    {
        AddApproved(db, userId, UserDocumentType.IdCard);
        AddApproved(db, userId, UserDocumentType.UtilityBill);
    }

    [Fact]
    public async Task Verify_MarksTheSellerVerified()
    {
        var (handler, db, seller) = await SetupAsync();

        var result = await handler.Handle(new SetSellerVerificationCommand(seller.UserId, true, null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var reloaded = await db.SellerProfiles.SingleAsync(s => s.Id == seller.Id);
        reloaded.VerificationStatus.Should().Be(VerificationStatus.Verified);
        reloaded.VerifiedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Verify_TwiceIsRefused()
    {
        var (handler, _, seller) = await SetupAsync();
        await handler.Handle(new SetSellerVerificationCommand(seller.UserId, true, null), CancellationToken.None);

        var again = await handler.Handle(new SetSellerVerificationCommand(seller.UserId, true, null), CancellationToken.None);

        again.Error.Should().Be(AdminUserErrors.AlreadyVerified);
    }

    [Fact]
    public async Task Revoke_ResetsTheStatus_ClearsVerifiedAt_AndTellsTheSellerWhy()
    {
        var (handler, db, seller) = await SetupAsync();
        await handler.Handle(new SetSellerVerificationCommand(seller.UserId, true, null), CancellationToken.None);

        var result = await handler.Handle(
            new SetSellerVerificationCommand(seller.UserId, false, "The ID photo doesn't match."), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var reloaded = await db.SellerProfiles.SingleAsync(s => s.Id == seller.Id);
        reloaded.VerificationStatus.Should().Be(VerificationStatus.NotStarted);
        reloaded.VerifiedAt.Should().BeNull();
        (await db.InboxNotifications.SingleAsync(n => n.UserId == seller.UserId)).Body.Should().Contain("The ID photo doesn't match.");
    }

    [Fact]
    public async Task Revoke_OfAnUnverifiedSellerIsRefused()
    {
        var (handler, _, seller) = await SetupAsync();

        var result = await handler.Handle(new SetSellerVerificationCommand(seller.UserId, false, "x"), CancellationToken.None);

        result.Error.Should().Be(AdminUserErrors.NotVerified);
    }

    [Fact]
    public async Task Verify_OfAnUnknownUserIsRefused()
    {
        var (handler, _, _) = await SetupAsync();

        var result = await handler.Handle(new SetSellerVerificationCommand(Guid.NewGuid(), true, null), CancellationToken.None);

        result.Error.Should().Be(AdminUserErrors.NotASeller);
    }

    [Fact]
    public async Task Verify_WithoutAnApprovedIdCard_IsRefused()
    {
        var (handler, db, seller) = await SetupAsync();
        var idCard = await db.UserDocuments.SingleAsync(d => d.UserId == seller.UserId && d.Type == UserDocumentType.IdCard);
        db.UserDocuments.Remove(idCard);
        await db.SaveChangesAsync(CancellationToken.None);

        var result = await handler.Handle(new SetSellerVerificationCommand(seller.UserId, true, null), CancellationToken.None);

        result.Error.Should().Be(AdminUserErrors.NotReadyToVerify);
    }

    [Fact]
    public async Task Verify_WithoutAnApprovedUtilityBill_IsRefused()
    {
        var (handler, db, seller) = await SetupAsync();
        var bill = await db.UserDocuments.SingleAsync(d => d.UserId == seller.UserId && d.Type == UserDocumentType.UtilityBill);
        db.UserDocuments.Remove(bill);
        await db.SaveChangesAsync(CancellationToken.None);

        var result = await handler.Handle(new SetSellerVerificationCommand(seller.UserId, true, null), CancellationToken.None);

        result.Error.Should().Be(AdminUserErrors.NotReadyToVerify);
    }

    [Fact]
    public async Task Verify_OfABuyer_MakesThemAVerifiedSellerWithAShop()
    {
        var db = TestApplicationDbContext.Create();
        var buyer = User.Create("08039876543", "bola@example.com", "hash", UserRole.Buyer, "Bola");
        buyer.SetProfilePhoto("profile-photos/bola.jpg");
        db.Users.Add(buyer);
        AddApprovedPersonalDocuments(db, buyer.Id);
        await db.SaveChangesAsync(CancellationToken.None);

        var result = await NewHandler(db).Handle(new SetSellerVerificationCommand(buyer.Id, true, null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await db.Users.SingleAsync(u => u.Id == buyer.Id)).Role.Should().Be(UserRole.Seller);
        var seller = await db.SellerProfiles.SingleAsync(s => s.UserId == buyer.Id);
        seller.BusinessName.Should().Be("Bola");
        seller.VerificationStatus.Should().Be(VerificationStatus.Verified);
    }

    [Fact]
    public async Task VerifyBusiness_BeforeThePersonIsVerified_IsRefused()
    {
        var (handler, db, seller) = await SetupAsync();
        AddApproved(db, seller.UserId, UserDocumentType.BusinessRegistration);
        AddApproved(db, seller.UserId, UserDocumentType.OfficeUtilityBill);
        await db.SaveChangesAsync(CancellationToken.None);

        var result = await handler.Handle(new SetBusinessVerificationCommand(seller.UserId, true, null), CancellationToken.None);

        result.Error.Should().Be(AdminUserErrors.PersonNotVerified);
    }

    [Fact]
    public async Task VerifyBusiness_WithoutApprovedBusinessDocuments_IsRefused()
    {
        var (handler, db, seller) = await SetupAsync();
        await handler.Handle(new SetSellerVerificationCommand(seller.UserId, true, null), CancellationToken.None);
        AddApproved(db, seller.UserId, UserDocumentType.BusinessRegistration);
        await db.SaveChangesAsync(CancellationToken.None);

        var result = await handler.Handle(new SetBusinessVerificationCommand(seller.UserId, true, null), CancellationToken.None);

        result.Error.Should().Be(AdminUserErrors.BusinessNotReadyToVerify);
    }

    [Fact]
    public async Task VerifyBusiness_MarksItVerified_AndRevokingThePersonClearsIt()
    {
        var (handler, db, seller) = await SetupAsync();
        await handler.Handle(new SetSellerVerificationCommand(seller.UserId, true, null), CancellationToken.None);
        AddApproved(db, seller.UserId, UserDocumentType.BusinessRegistration);
        AddApproved(db, seller.UserId, UserDocumentType.OfficeUtilityBill);
        await db.SaveChangesAsync(CancellationToken.None);

        var verified = await handler.Handle(new SetBusinessVerificationCommand(seller.UserId, true, null), CancellationToken.None);

        verified.IsSuccess.Should().BeTrue();
        (await db.SellerProfiles.SingleAsync(s => s.Id == seller.Id)).IsBusinessVerified.Should().BeTrue();

        await handler.Handle(new SetSellerVerificationCommand(seller.UserId, false, "ID mismatch."), CancellationToken.None);

        (await db.SellerProfiles.SingleAsync(s => s.Id == seller.Id)).IsBusinessVerified.Should().BeFalse();
    }
}
