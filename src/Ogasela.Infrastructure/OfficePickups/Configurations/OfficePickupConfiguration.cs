using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ogasela.Domain.OfficePickups;

namespace Ogasela.Infrastructure.OfficePickups.Configurations;

public sealed class OfficeBranchConfiguration : IEntityTypeConfiguration<OfficeBranch>
{
    public void Configure(EntityTypeBuilder<OfficeBranch> builder)
    {
        builder.ToTable("OfficeBranches");
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Name).HasMaxLength(120).IsRequired();
        builder.Property(b => b.Address).HasMaxLength(500).IsRequired();
        builder.Property(b => b.State).HasMaxLength(100).IsRequired();
        builder.Property(b => b.City).HasMaxLength(100);
        builder.Property(b => b.Phone).HasMaxLength(20);
        builder.Property(b => b.OpeningHours).HasMaxLength(200);
        builder.Property(b => b.Latitude).HasPrecision(9, 6);
        builder.Property(b => b.Longitude).HasPrecision(9, 6);
    }
}

public sealed class OfficePickupRequestConfiguration : IEntityTypeConfiguration<OfficePickupRequest>
{
    public void Configure(EntityTypeBuilder<OfficePickupRequest> builder)
    {
        builder.ToTable("OfficePickupRequests");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Reference).HasMaxLength(20).IsRequired();
        builder.HasIndex(r => r.Reference).IsUnique();
        builder.Property(r => r.ListingTitle).HasMaxLength(200).IsRequired();
        builder.Property(r => r.ItemPriceKobo).HasPrecision(18, 2);
        builder.Property(r => r.ServiceFeeKobo).HasPrecision(18, 2);
        builder.Property(r => r.TotalKobo).HasPrecision(18, 2);
        builder.Property(r => r.CancellationFeeKobo).HasPrecision(18, 2);
        builder.Property(r => r.RefundedKobo).HasPrecision(18, 2);
        builder.Property(r => r.PaymentReference).HasMaxLength(200);
        builder.HasIndex(r => r.PaymentReference).IsUnique();
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(r => r.Fulfilment).HasConversion<string>().HasMaxLength(32);
        builder.Property(r => r.DeliveryAddress).HasMaxLength(500);
        builder.Property(r => r.CancellationReason).HasMaxLength(1000);
        builder.HasIndex(r => new { r.Status, r.CreatedAt });
        builder.HasIndex(r => r.BuyerUserId);
        builder.HasIndex(r => r.SellerUserId);

        builder.HasOne<OfficeBranch>().WithMany().HasForeignKey(r => r.BranchId).OnDelete(DeleteBehavior.Restrict);

        // The payment webhook and the app's confirm call can race to settle the same payment, and
        // two staff can act on the same request at once - "xmin" (see TransactionConfiguration)
        // makes the second writer fail instead of double-settling or double-refunding.
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}

public sealed class OfficePickupActivityConfiguration : IEntityTypeConfiguration<OfficePickupActivity>
{
    public void Configure(EntityTypeBuilder<OfficePickupActivity> builder)
    {
        builder.ToTable("OfficePickupActivities");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(a => a.Title).HasMaxLength(200).IsRequired();
        builder.Property(a => a.Note).HasMaxLength(1000);
        builder.HasIndex(a => new { a.RequestId, a.CreatedAt });
    }
}
