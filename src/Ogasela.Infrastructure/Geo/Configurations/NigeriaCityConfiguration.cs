using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ogasela.Domain.Geo;

namespace Ogasela.Infrastructure.Geo.Configurations;

public sealed class NigeriaCityConfiguration : IEntityTypeConfiguration<NigeriaCity>
{
    public void Configure(EntityTypeBuilder<NigeriaCity> builder)
    {
        builder.ToTable("NigeriaCities");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Name).HasMaxLength(150).IsRequired();
        builder.Property(c => c.Latitude).HasPrecision(9, 6);
        builder.Property(c => c.Longitude).HasPrecision(9, 6);
        builder.Property(c => c.CreatedAt).IsRequired();

        builder.HasIndex(c => c.StateId);
        builder.HasIndex(c => new { c.StateId, c.Name }).IsUnique();
    }
}
