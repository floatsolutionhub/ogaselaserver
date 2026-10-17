using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ogasela.Domain.Promotions;
using Ogasela.Infrastructure.Promotions.Seed;

namespace Ogasela.Infrastructure.Promotions.Configurations;

public sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("Categories");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Name).HasMaxLength(100).IsRequired();
        builder.Property(c => c.ImageS3Key).HasMaxLength(500);
        builder.Property(c => c.CreatedAt).IsRequired();
        builder.Property(c => c.UpdatedAt).IsRequired();

        builder.HasOne<Category>().WithMany()
            .HasForeignKey(c => c.ParentCategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(c => c.ParentCategoryId);

        builder.HasData(
            Seed(CategorySeedData.ElectronicsId, "Electronics", isFreeEligible: true),
            Seed(CategorySeedData.FashionId, "Fashion", isFreeEligible: true),
            Seed(CategorySeedData.VehiclesId, "Vehicles", isFreeEligible: false),
            Seed(CategorySeedData.RealEstateId, "Real Estate", isFreeEligible: false),
            Seed(CategorySeedData.HomeAndFurnitureId, "Home & Furniture", isFreeEligible: true),
            Seed(CategorySeedData.JobsId, "Jobs", isFreeEligible: false),
            Seed(CategorySeedData.ServicesId, "Services", isFreeEligible: true));

        // Subcategories inherit their parent's IsFreeEligible.
        var parentFreeEligible = new Dictionary<Guid, bool>
        {
            [CategorySeedData.ElectronicsId] = true,
            [CategorySeedData.FashionId] = true,
            [CategorySeedData.VehiclesId] = false,
            [CategorySeedData.RealEstateId] = false,
            [CategorySeedData.HomeAndFurnitureId] = true,
            [CategorySeedData.JobsId] = false,
            [CategorySeedData.ServicesId] = true,
        };

        builder.HasData(CategorySeedData.Subcategories
            .Select(sub => Seed(sub.Id, sub.Name, parentFreeEligible[sub.ParentId], sub.ParentId))
            .ToArray());
    }

    private static object Seed(Guid id, string name, bool isFreeEligible, Guid? parentCategoryId = null) => new
    {
        Id = id,
        Name = name,
        ParentCategoryId = parentCategoryId,
        IsFreeEligible = isFreeEligible,
        IsOfficePickupEligible = false,
        AttributeSchemaVersion = 1,
        ImageS3Key = (string?)null,
        CreatedAt = CategorySeedData.SeedTimestamp,
        UpdatedAt = CategorySeedData.SeedTimestamp
    };
}
