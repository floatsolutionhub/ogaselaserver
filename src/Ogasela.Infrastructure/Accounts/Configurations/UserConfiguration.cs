using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ogasela.Domain.Accounts;

namespace Ogasela.Infrastructure.Accounts.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");

        builder.HasKey(u => u.Id);

        builder.Property(u => u.Name).HasMaxLength(200);
        builder.Property(u => u.Phone).HasMaxLength(20);
        builder.Property(u => u.Email).HasMaxLength(256);
        builder.Property(u => u.PasswordHash).IsRequired();
        builder.Property(u => u.Role).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(u => u.PushToken).HasMaxLength(500);
        builder.Property(u => u.ProfilePhotoS3Key).HasMaxLength(500);
        builder.Property(u => u.SuspensionReason).HasMaxLength(500);
        builder.Ignore(u => u.IsSuspended);
        builder.Ignore(u => u.IsDeleted);
        builder.Property(u => u.CreatedAt).IsRequired();

        builder.HasIndex(u => u.Phone).IsUnique().HasFilter("\"Phone\" IS NOT NULL");
        builder.HasIndex(u => u.Email).IsUnique().HasFilter("\"Email\" IS NOT NULL");
    }
}
