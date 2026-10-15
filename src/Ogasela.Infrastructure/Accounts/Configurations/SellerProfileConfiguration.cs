using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ogasela.Domain.Accounts;

namespace Ogasela.Infrastructure.Accounts.Configurations;

public sealed class SellerProfileConfiguration : IEntityTypeConfiguration<SellerProfile>
{
    public void Configure(EntityTypeBuilder<SellerProfile> builder)
    {
        builder.ToTable("SellerProfiles");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.BusinessName).HasMaxLength(200).IsRequired();
        builder.Property(s => s.RcNumber).HasMaxLength(50);
        builder.Property(s => s.Nin).HasMaxLength(20);
        builder.Property(s => s.StoreAddress).HasMaxLength(300);
        builder.Property(s => s.VerificationStatus).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(s => s.CreatedAt).IsRequired();
        builder.Ignore(s => s.IsBusinessVerified);

        builder.HasIndex(s => s.UserId).IsUnique();
    }
}
