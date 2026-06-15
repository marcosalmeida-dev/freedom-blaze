using FreedomBlaze.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FreedomBlaze.Data.Configurations;

public class DonationConfiguration : IEntityTypeConfiguration<Donation>
{
    public void Configure(EntityTypeBuilder<Donation> builder)
    {
        builder.ToTable("Donations");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.PaymentHash).HasMaxLength(128);
        builder.Property(d => d.Message).HasMaxLength(512);
        builder.Property(d => d.Description).IsRequired().HasMaxLength(512);
        builder.Property(d => d.Bolt11).HasMaxLength(2048);
        builder.Property(d => d.FiatEstimate).HasMaxLength(64);
        builder.Property(d => d.ErrorMessage).HasMaxLength(2048);

        // Persist the status by name for readable, stable values in the database.
        builder.Property(d => d.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        // Look up a donation by its invoice, and list recent donations by status.
        builder.HasIndex(d => d.PaymentHash);
        builder.HasIndex(d => new { d.Status, d.CreatedAtUtc });
    }
}
