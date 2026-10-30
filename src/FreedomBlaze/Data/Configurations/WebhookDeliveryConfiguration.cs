using FreedomBlaze.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FreedomBlaze.Data.Configurations;

public class WebhookDeliveryConfiguration : IEntityTypeConfiguration<WebhookDelivery>
{
    public void Configure(EntityTypeBuilder<WebhookDelivery> builder)
    {
        builder.ToTable("WebhookDeliveries");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.EventId).IsRequired().HasMaxLength(64);
        builder.Property(d => d.EventType).IsRequired().HasMaxLength(64);
        builder.Property(d => d.Payload).IsRequired();
        builder.Property(d => d.LastError).HasMaxLength(512);

        // The dispatcher's queue: only pending deliveries carry a next attempt.
        builder.HasIndex(d => d.NextAttemptAtUtc).HasFilter("[NextAttemptAtUtc] IS NOT NULL");

        // The operator's log, newest first per merchant.
        builder.HasIndex(d => new { d.MerchantId, d.Id });

        builder.HasIndex(d => d.EventId);

        builder.HasOne<Merchant>().WithMany().HasForeignKey(d => d.MerchantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<WebhookEndpoint>().WithMany().HasForeignKey(d => d.EndpointId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<LightningInvoice>().WithMany().HasForeignKey(d => d.InvoiceId).OnDelete(DeleteBehavior.Restrict);
    }
}
