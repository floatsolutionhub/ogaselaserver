using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Promotions.Interfaces;
using Ogasela.Application.Promotions.UpdateCategory;
using Ogasela.Domain.Promotions;
using Ogasela.UnitTests.TestSupport;

namespace Ogasela.UnitTests.Promotions;

public class UpdateCategoryCommandTests
{
    private sealed class NoOpCategoryImageStorage : ICategoryImageStorage
    {
        public Task<string> UploadAsync(string fileName, string contentType, Stream content, CancellationToken cancellationToken) =>
            Task.FromResult($"categories/{fileName}");

        public Task DeleteAsync(string key, CancellationToken cancellationToken) => Task.CompletedTask;

        public string GetImageUrl(string key) => $"https://images.test/{key}";
    }

    private sealed class NoOpAuditLogger : IAuditLogger
    {
        public Task LogAsync(Guid actorId, string action, string targetType, Guid targetId, string? beforeJson,
            string? afterJson, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static UpdateCategoryCommand Command(Guid id, string name, Guid? parentId, bool isFreeEligible) =>
        new(id, name, parentId, isFreeEligible, AttributeSchemaVersion: 1, Image: null, ImageFileName: null,
            ImageContentType: null);

    private static async Task<(UpdateCategoryCommandHandler Handler, TestApplicationDbContext DbContext, Category Top, Category Sub)> CreateSut()
    {
        var dbContext = TestApplicationDbContext.Create();
        var now = new FakeDateTime().UtcNow;
        var top = Category.Create(Guid.NewGuid(), "Pets", null, true, 1, null, now);
        var sub = Category.Create(Guid.NewGuid(), "Dogs", top.Id, true, 1, null, now);
        dbContext.Categories.AddRange(top, sub);
        await dbContext.SaveChangesAsync(CancellationToken.None);

        var handler = new UpdateCategoryCommandHandler(dbContext, new NoOpCategoryImageStorage(),
            new FakeCurrentUserService { UserId = Guid.NewGuid() }, new FakeDateTime(), new NoOpAuditLogger());
        return (handler, dbContext, top, sub);
    }

    [Fact]
    public async Task Handle_TopLevelFreePlanChange_AppliesToItsSubcategories()
    {
        var (handler, dbContext, top, sub) = await CreateSut();

        var result = await handler.Handle(Command(top.Id, "Pets & Animals", null, isFreeEligible: false), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("Pets & Animals");
        var savedSub = await dbContext.Categories.SingleAsync(c => c.Id == sub.Id);
        savedSub.IsFreeEligible.Should().BeFalse();
        savedSub.Name.Should().Be("Dogs");
    }

    [Fact]
    public async Task Handle_RenameSubcategory_KeepsItUnderItsParent()
    {
        var (handler, dbContext, top, sub) = await CreateSut();

        var result = await handler.Handle(Command(sub.Id, "Puppies", top.Id, isFreeEligible: true), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var savedSub = await dbContext.Categories.SingleAsync(c => c.Id == sub.Id);
        savedSub.Name.Should().Be("Puppies");
        savedSub.ParentCategoryId.Should().Be(top.Id);
    }
}
