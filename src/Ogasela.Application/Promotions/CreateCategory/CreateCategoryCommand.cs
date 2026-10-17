using MediatR;
using Ogasela.Application.Promotions.GetCategories;
using Ogasela.Shared;

namespace Ogasela.Application.Promotions.CreateCategory;

public sealed record CreateCategoryCommand(
    string Name,
    Guid? ParentCategoryId,
    bool IsFreeEligible,
    int AttributeSchemaVersion,
    Stream? Image,
    string? ImageFileName,
    string? ImageContentType,
    IReadOnlyList<string> SubcategoryNames,
    bool IsOfficePickupEligible = false) : IRequest<Result<CategoryResponse>>;
