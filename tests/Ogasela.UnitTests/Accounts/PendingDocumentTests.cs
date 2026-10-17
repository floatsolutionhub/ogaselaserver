using FluentAssertions;
using Ogasela.Application.Accounts;
using Ogasela.Application.Accounts.Interfaces;
using Ogasela.Application.Accounts.UserDocuments;
using Ogasela.Domain.Accounts;
using Ogasela.UnitTests.TestSupport;

namespace Ogasela.UnitTests.Accounts;

/// <summary>A document type can only be submitted again once the pending one has been reviewed.</summary>
public class PendingDocumentTests
{
    private sealed class FakeStorage : IUserDocumentStorage
    {
        public int Uploads { get; private set; }

        public Task<string> UploadAsync(string fileName, string contentType, Stream content, CancellationToken cancellationToken)
        {
            Uploads++;
            return Task.FromResult($"docs/{Guid.NewGuid():N}");
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken) => Task.CompletedTask;

        public string GetImageUrl(string key) => $"https://files.test/{key}";
    }

    private static UploadUserDocumentCommand UtilityBill() =>
        new(UserDocumentType.UtilityBill, null, null, new MemoryStream([1, 2, 3]), "bill.pdf", "application/pdf");

    [Fact]
    public async Task ASecondUpload_WhileTheFirstIsPending_IsRefusedWithoutStoringTheFile()
    {
        using var db = TestApplicationDbContext.Create();
        var storage = new FakeStorage();
        var sut = new UserDocumentHandlers(db, new FakeCurrentUserService { UserId = Guid.NewGuid() }, storage, new FakeDateTime());

        (await sut.Handle(UtilityBill(), CancellationToken.None)).IsSuccess.Should().BeTrue();
        var second = await sut.Handle(UtilityBill(), CancellationToken.None);

        second.IsFailure.Should().BeTrue();
        second.Error.Should().Be(AccountErrors.DocumentAlreadyPending);
        storage.Uploads.Should().Be(1, "a refused upload mustn't leave an orphaned file in storage");
    }

    [Fact]
    public async Task OnceThePendingOneIsReviewed_AnotherCanBeSubmitted()
    {
        using var db = TestApplicationDbContext.Create();
        var sut = new UserDocumentHandlers(db, new FakeCurrentUserService { UserId = Guid.NewGuid() }, new FakeStorage(), new FakeDateTime());
        await sut.Handle(UtilityBill(), CancellationToken.None);

        db.UserDocuments.Single().Reject("Blurry photo", DateTime.UtcNow);
        await db.SaveChangesAsync(CancellationToken.None);

        (await sut.Handle(UtilityBill(), CancellationToken.None)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task APendingDocument_DoesNotBlockADifferentDocumentType()
    {
        using var db = TestApplicationDbContext.Create();
        var sut = new UserDocumentHandlers(db, new FakeCurrentUserService { UserId = Guid.NewGuid() }, new FakeStorage(), new FakeDateTime());
        await sut.Handle(UtilityBill(), CancellationToken.None);

        var idCard = new UploadUserDocumentCommand(
            UserDocumentType.IdCard, IdDocumentType.NationalIdCard, "A1234567", new MemoryStream([1]), "id.jpg", "image/jpeg");
        (await sut.Handle(idCard, CancellationToken.None)).IsSuccess.Should().BeTrue();
    }
}
