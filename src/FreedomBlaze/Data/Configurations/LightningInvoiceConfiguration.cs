using FreedomBlaze.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FreedomBlaze.Data.Configurations;

public class LightningInvoiceConfiguration : IEntityTypeConfiguration<LightningInvoice>
{
    public void Configure(EntityTypeBuilder<LightningInvoice> builder)
    {
        builder.ToTable("LightningInvoices");

        builder.HasKey(i => i.Id);

        // Null only while a reservation is being created, so the uniqueness applies to issued invoices.
        builder.Property(i => i.PaymentHash).HasMaxLength(64);
        builder.HasIndex(i => i.PaymentHash).IsUnique().HasFilter("[PaymentHash] IS NOT NULL");

        builder.Property(i => i.Bolt11).HasMaxLength(2048);
        builder.Property(i => i.ExternalId).HasMaxLength(100);
        builder.Property(i => i.Description).IsRequired().HasMaxLength(128);
        builder.Property(i => i.Currency).IsRequired().HasMaxLength(8);
        builder.Property(i => i.Amount).HasPrecision(28, 8);
        builder.Property(i => i.BitcoinPrice).HasPrecision(28, 8);
        builder.Property(i => i.BitcoinPriceUsd).HasPrecision(28, 8);
        builder.Property(i => i.WebhookUrl).HasMaxLength(2048);
        builder.Property(i => i.RequestHash).HasMaxLength(64);

        // One invoice per idempotency key per merchant: the database, not the code, settles a race
        // between two retries of the same request.
        builder.Property(i => i.IdempotencyKey).HasMaxLength(100);
        builder.HasIndex(i => new { i.MerchantId, i.IdempotencyKey })
            .IsUnique()
            .HasFilter("[IdempotencyKey] IS NOT NULL");

        builder.HasIndex(i => new { i.MerchantId, i.ExternalId });

        // The background sweep's queue: only pending invoices carry a next check.
        builder.HasIndex(i => i.NextCheckAtUtc).HasFilter("[NextCheckAtUtc] IS NOT NULL");

        builder.HasOne<Merchant>().WithMany().HasForeignKey(i => i.MerchantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApiKey>().WithMany().HasForeignKey(i => i.ApiKeyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<LightningBackend>().WithMany().HasForeignKey(i => i.BackendId).OnDelete(DeleteBehavior.Restrict);
    }
}
