using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Promotions.GetCategories;
using Ogasela.Application.Promotions.Interfaces;
using Ogasela.Domain.Promotions;
using Ogasela.Shared;

namespace Ogasela.Application.Promotions.UpdateCategory;

public sealed class UpdateCategoryCommandHandler : IRequestHandler<UpdateCategoryCommand, Result<CategoryResponse>>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICategoryImageStorage _imageStorage;
    private readonly ICurrentUserService _currentUser;
    private readonly IDateTime _dateTime;
    private readonly IAuditLogger _auditLogger;

    public UpdateCategoryCommandHandler(
        IApplicationDbContext dbContext,
        ICategoryImageStorage imageStorage,
        ICurrentUserService currentUser,
        IDateTime dateTime,
        IAuditLogger auditLogger)
    {
        _dbContext = dbContext;
        _imageStorage = imageStorage;
        _currentUser = currentUser;
        _dateTime = dateTime;
        _auditLogger = auditLogger;
    }

    public async Task<Result<CategoryResponse>> Handle(UpdateCategoryCommand request, CancellationToken cancellationToken)
    {
        var category = await _dbContext.Categories.FirstOrDefaultAsync(c => c.Id == request.Id, cancellationToken);
        if (category is null)
        {
            return Result.Failure<CategoryResponse>(PromotionErrors.CategoryNotFound);
        }

        if (request.ParentCategoryId is { } parentId)
        {
            if (parentId == request.Id)
            {
                return Result.Failure<CategoryResponse>(PromotionErrors.CategoryCannotBeOwnParent);
            }

            var parent = await _dbContext.Categories.FirstOrDefaultAsync(c => c.Id == parentId, cancellationToken);
            if (parent is null)
            {
                return Result.Failure<CategoryResponse>(PromotionErrors.ParentCategoryNotFound);
            }

            if (parent.ParentCategoryId is not null)
            {
                return Result.Failure<CategoryResponse>(PromotionErrors.ParentMustBeTopLevel);
            }

            var hasSubcategories = await _dbContext.Categories.AnyAsync(c => c.ParentCategoryId == request.Id, cancellationToken);
            if (hasSubcategories)
            {
                return Result.Failure<CategoryResponse>(PromotionErrors.CategoryHasSubcategories);
            }
        }

        var beforeJson = JsonSerializer.Serialize(category);
        var now = _dateTime.UtcNow;

        var oldImageKey = category.ImageS3Key;
        if (request.Image is not null)
        {
            var newImageKey = await _imageStorage.UploadAsync(
                request.ImageFileName!, request.ImageContentType!, request.Image, cancellationToken);
            category.SetImage(newImageKey, now);
        }

        category.Update(request.Name, request.ParentCategoryId, request.IsFreeEligible, request.AttributeSchemaVersion, now);
        if (request.IsOfficePickupEligible is { } officePickup)
        {
            category.SetOfficePickupEligible(officePickup, now);
        }

        // Ads are posted under a subcategory, and RULE-04 (ListingPublishService) reads the
        // subcategory's own flag - so a top-level category's Free-plan setting only takes effect
        // once its subcategories carry it too, the same way they inherit it at creation.
        if (category.ParentCategoryId is null)
        {
            var subcategories = await _dbContext.Categories
                .Where(c => c.ParentCategoryId == category.Id && c.IsFreeEligible != request.IsFreeEligible)
                .ToListAsync(cancellationToken);
            foreach (var subcategory in subcategories)
            {
                subcategory.Update(subcategory.Name, subcategory.ParentCategoryId, request.IsFreeEligible,
                    subcategory.AttributeSchemaVersion, now);
            }
        }

        var afterJson = JsonSerializer.Serialize(category);

        await _auditLogger.LogAsync(
            _currentUser.UserId!.Value, "Category.Updated", nameof(Category), category.Id, beforeJson, afterJson,
            cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);

        if (request.Image is not null && oldImageKey is not null)
        {
            await _imageStorage.DeleteAsync(oldImageKey, cancellationToken);
        }

        return Result.Success(new CategoryResponse(
            category.Id,
            category.Name,
            category.ParentCategoryId,
            category.IsFreeEligible,
            category.AttributeSchemaVersion,
            category.ImageS3Key is null ? null : _imageStorage.GetImageUrl(category.ImageS3Key),
            category.IsOfficePickupEligible));
    }
}
