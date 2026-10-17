using MediatR;
using Ogasela.Application.Promotions.GetCategories;
using Ogasela.Shared;

namespace Ogasela.Application.Promotions.UpdateCategory;

public sealed record UpdateCategoryCommand(
    Guid Id,
    string Name,
    Guid? ParentCategoryId,
    bool IsFreeEligible,
    int AttributeSchemaVersion,
    Stream? Image,
    string? ImageFileName,
    string? ImageContentType,
    bool? IsOfficePickupEligible = null) : IRequest<Result<CategoryResponse>>;
