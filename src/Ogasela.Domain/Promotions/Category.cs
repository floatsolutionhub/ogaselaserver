namespace Ogasela.Domain.Promotions;

/// <summary>
/// A listing category. Subcategories are just <see cref="Category"/> rows with
/// <see cref="ParentCategoryId"/> set - there is no separate subcategory entity - so the same
/// image field represents whichever "category image" or "subcategory image" a client uploaded
/// when creating that row.
/// </summary>
public class Category
{
    private Category()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public Guid? ParentCategoryId { get; private set; }

    public bool IsFreeEligible { get; private set; }

    public int AttributeSchemaVersion { get; private set; }

    /// <summary>
    /// Whether buyers can request an office pickup for ads in this category. Set from the Control
    /// Portal; an ad qualifies when its own category or that category's parent has it on.
    /// </summary>
    public bool IsOfficePickupEligible { get; private set; }

    /// <summary>Storage key for the image representing this category/subcategory, set at creation.</summary>
    public string? ImageS3Key { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime UpdatedAt { get; private set; }

    public static Category Create(
        Guid id,
        string name,
        Guid? parentCategoryId,
        bool isFreeEligible,
        int attributeSchemaVersion,
        string? imageS3Key,
        DateTime now)
    {
        return new Category
        {
            Id = id,
            Name = name,
            ParentCategoryId = parentCategoryId,
            IsFreeEligible = isFreeEligible,
            AttributeSchemaVersion = attributeSchemaVersion,
            ImageS3Key = imageS3Key,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public void Update(
        string name, Guid? parentCategoryId, bool isFreeEligible, int attributeSchemaVersion, DateTime now)
    {
        Name = name;
        ParentCategoryId = parentCategoryId;
        IsFreeEligible = isFreeEligible;
        AttributeSchemaVersion = attributeSchemaVersion;
        UpdatedAt = now;
    }

    public void SetOfficePickupEligible(bool eligible, DateTime now)
    {
        IsOfficePickupEligible = eligible;
        UpdatedAt = now;
    }

    public void SetImage(string? imageS3Key, DateTime now)
    {
        ImageS3Key = imageS3Key;
        UpdatedAt = now;
    }
}
