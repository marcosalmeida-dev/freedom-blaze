using FreedomBlaze.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FreedomBlaze.Data.Configurations;

public class WebhookEndpointConfiguration : IEntityTypeConfiguration<WebhookEndpoint>
{
    public void Configure(EntityTypeBuilder<WebhookEndpoint> builder)
    {
        builder.ToTable("WebhookEndpoints");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Url).IsRequired().HasMaxLength(2048);
        builder.Property(e => e.Description).HasMaxLength(128);
        builder.Property(e => e.ProtectedSecret).IsRequired().HasMaxLength(1024);

        builder.HasIndex(e => e.MerchantId);
        builder.HasOne<Merchant>().WithMany().HasForeignKey(e => e.MerchantId).OnDelete(DeleteBehavior.Restrict);
    }
}
