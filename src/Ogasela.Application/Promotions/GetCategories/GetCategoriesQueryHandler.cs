using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Promotions.Interfaces;
using Ogasela.Shared;

namespace Ogasela.Application.Promotions.GetCategories;

public sealed class GetCategoriesQueryHandler : IRequestHandler<GetCategoriesQuery, Result<IReadOnlyList<CategoryResponse>>>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICategoryImageStorage _imageStorage;

    public GetCategoriesQueryHandler(IApplicationDbContext dbContext, ICategoryImageStorage imageStorage)
    {
        _dbContext = dbContext;
        _imageStorage = imageStorage;
    }

    public async Task<Result<IReadOnlyList<CategoryResponse>>> Handle(
        GetCategoriesQuery request, CancellationToken cancellationToken)
    {
        var categories = await _dbContext.Categories
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken);

        var response = categories
            .Select(c => new CategoryResponse(
                c.Id,
                c.Name,
                c.ParentCategoryId,
                c.IsFreeEligible,
                c.AttributeSchemaVersion,
                c.ImageS3Key is null ? null : _imageStorage.GetImageUrl(c.ImageS3Key),
                c.IsOfficePickupEligible))
            .ToList();

        return Result.Success<IReadOnlyList<CategoryResponse>>(response);
    }
}
