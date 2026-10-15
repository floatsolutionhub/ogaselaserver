using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ogasela.Domain.Listings;

namespace Ogasela.Infrastructure.Listings.Configurations;

public sealed class ListingConfiguration : IEntityTypeConfiguration<Listing>
{
    public void Configure(EntityTypeBuilder<Listing> builder)
    {
        builder.ToTable("Listings");

        builder.HasKey(l => l.Id);

        builder.Property(l => l.Title).HasMaxLength(150).IsRequired();
        builder.Property(l => l.Description).HasMaxLength(5000).IsRequired();
        builder.Property(l => l.Price).HasPrecision(18, 2);
        builder.Property(l => l.Condition).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(l => l.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(l => l.Location).HasMaxLength(200);
        builder.Property(l => l.ReviewNote).HasMaxLength(1000);
        builder.Property(l => l.Latitude).HasPrecision(9, 6);
        builder.Property(l => l.Longitude).HasPrecision(9, 6);
        builder.Property(l => l.CreatedAt).IsRequired();
        builder.Property(l => l.UpdatedAt).IsRequired();
        builder.Property(l => l.TikTokUrl).HasMaxLength(Listing.SocialLinkMaxLength);
        builder.Property(l => l.FacebookUrl).HasMaxLength(Listing.SocialLinkMaxLength);
        builder.Property(l => l.InstagramUrl).HasMaxLength(Listing.SocialLinkMaxLength);
        builder.Property(l => l.YouTubeUrl).HasMaxLength(Listing.SocialLinkMaxLength);

        builder.PrimitiveCollection(l => l.MediaUrls);

        // Note: the "SearchVector" tsvector column (maintained by a Postgres trigger, combining
        // Title/Description/Category name - see the AddListingSearch migration) is deliberately
        // NOT declared anywhere in the EF model. EF's model validator rejects NpgsqlTsVector on
        // any non-Npgsql provider (breaking InMemory-backed unit tests), and nothing here ever
        // needs to read/write it through EF anyway - PostgresListingSearchRepository queries it
        // with raw SQL. The migration adds the column directly via migrationBuilder.Sql.

        builder.HasIndex(l => l.SellerId);
        builder.HasIndex(l => l.CategoryId);
        builder.HasIndex(l => new { l.Status, l.ExpiresAt });
    }
}
