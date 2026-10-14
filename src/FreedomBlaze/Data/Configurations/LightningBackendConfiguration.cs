using FreedomBlaze.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FreedomBlaze.Data.Configurations;

public class LightningBackendConfiguration : IEntityTypeConfiguration<LightningBackend>
{
    public void Configure(EntityTypeBuilder<LightningBackend> builder)
    {
        builder.ToTable("LightningBackends");

        builder.HasKey(b => b.Id);

        builder.Property(b => b.Url).HasMaxLength(2048);
        builder.Property(b => b.ProtectedPassword).HasMaxLength(1024);
        builder.Property(b => b.NodeId).HasMaxLength(66);
        builder.Property(b => b.Chain).HasMaxLength(32);
        builder.Property(b => b.CheckError).HasMaxLength(512);

        // At most one current backend per merchant; retired ones stay to serve their invoices.
        builder.HasIndex(b => b.MerchantId).IsUnique().HasFilter("[IsActive] = 1");

        builder.HasOne<Merchant>().WithMany().HasForeignKey(b => b.MerchantId).OnDelete(DeleteBehavior.Restrict);
    }
}
