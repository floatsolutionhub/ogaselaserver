namespace Ogasela.Application.Promotions.GetCategories;

public sealed record CategoryResponse(
    Guid Id,
    string Name,
    Guid? ParentCategoryId,
    bool IsFreeEligible,
    int AttributeSchemaVersion,
    string? ImageUrl,
    bool IsOfficePickupEligible = false);
