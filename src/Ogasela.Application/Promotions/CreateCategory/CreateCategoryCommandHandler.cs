using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Promotions.GetCategories;
using Ogasela.Application.Promotions.Interfaces;
using Ogasela.Domain.Promotions;
using Ogasela.Shared;

namespace Ogasela.Application.Promotions.CreateCategory;

public sealed class CreateCategoryCommandHandler : IRequestHandler<CreateCategoryCommand, Result<CategoryResponse>>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICategoryImageStorage _imageStorage;
    private readonly IDateTime _dateTime;

    public CreateCategoryCommandHandler(
        IApplicationDbContext dbContext, ICategoryImageStorage imageStorage, IDateTime dateTime)
    {
        _dbContext = dbContext;
        _imageStorage = imageStorage;
        _dateTime = dateTime;
    }

    public async Task<Result<CategoryResponse>> Handle(CreateCategoryCommand request, CancellationToken cancellationToken)
    {
        if (request.ParentCategoryId is { } parentId)
        {
            var parent = await _dbContext.Categories.FirstOrDefaultAsync(c => c.Id == parentId, cancellationToken);
            if (parent is null)
            {
                return Result.Failure<CategoryResponse>(PromotionErrors.ParentCategoryNotFound);
            }

            if (parent.ParentCategoryId is not null)
            {
                return Result.Failure<CategoryResponse>(PromotionErrors.ParentMustBeTopLevel);
            }
        }

        string? imageKey = null;
        if (request.Image is not null)
        {
            imageKey = await _imageStorage.UploadAsync(
                request.ImageFileName!, request.ImageContentType!, request.Image, cancellationToken);
        }

        var now = _dateTime.UtcNow;
        var category = Category.Create(
            Guid.NewGuid(), request.Name, request.ParentCategoryId, request.IsFreeEligible,
            request.AttributeSchemaVersion, imageKey, now);
        // Subcategories don't need their own flag: a parent's office pickup setting covers them.
        category.SetOfficePickupEligible(request.IsOfficePickupEligible, now);

        _dbContext.Categories.Add(category);

        // Subcategories inherit the parent's free-eligibility and schema version; each can be
        // given its own image afterwards via UpdateCategory. Saved in the same SaveChanges so a
        // category never exists without its subcategories.
        foreach (var subcategoryName in request.SubcategoryNames)
        {
            _dbContext.Categories.Add(Category.Create(
                Guid.NewGuid(), subcategoryName.Trim(), category.Id, request.IsFreeEligible,
                request.AttributeSchemaVersion, imageS3Key: null, now));
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(new CategoryResponse(
            category.Id,
            category.Name,
            category.ParentCategoryId,
            category.IsFreeEligible,
            category.AttributeSchemaVersion,
            imageKey is null ? null : _imageStorage.GetImageUrl(imageKey),
            category.IsOfficePickupEligible));
    }
}
