using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ogasela.Domain.Marketing;

namespace Ogasela.Infrastructure.Marketing.Configurations;

public sealed class InterestSignupConfiguration : IEntityTypeConfiguration<InterestSignup>
{
    public void Configure(EntityTypeBuilder<InterestSignup> builder)
    {
        builder.ToTable("InterestSignups");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(320).IsRequired();
        builder.Property(x => x.Phone).HasMaxLength(20);
        builder.Property(x => x.State).HasMaxLength(100);
        builder.Property(x => x.Intent).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.DevicePlatform).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.Notes).HasMaxLength(2000);
        builder.Property(x => x.ConsentedAt).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.HasIndex(x => new { x.Kind, x.Email }).IsUnique();
        builder.HasIndex(x => x.CreatedAt);
    }
}
