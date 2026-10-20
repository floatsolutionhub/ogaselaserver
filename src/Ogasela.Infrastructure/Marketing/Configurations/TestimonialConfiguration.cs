using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ogasela.Domain.Marketing;

namespace Ogasela.Infrastructure.Marketing.Configurations;

public sealed class TestimonialConfiguration : IEntityTypeConfiguration<Testimonial>
{
    public void Configure(EntityTypeBuilder<Testimonial> builder)
    {
        builder.ToTable("Testimonials", t => t.HasCheckConstraint("CK_Testimonials_Rating", "\"Rating\" BETWEEN 1 AND 5"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Quote).HasMaxLength(Testimonial.QuoteMaxLength).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(Testimonial.NameMaxLength).IsRequired();
        builder.Property(x => x.Role).HasMaxLength(Testimonial.RoleMaxLength).IsRequired();
        builder.Property(x => x.Rating).IsRequired();
        builder.Property(x => x.ConsentConfirmedAt).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.HasIndex(x => new { x.IsPublished, x.CreatedAt });
    }
}
