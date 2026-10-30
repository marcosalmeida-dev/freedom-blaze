using System.Buffers;
using System.Security.Cryptography;
using FreedomBlaze.Data;
using FreedomBlaze.Data.Entities;
using FreedomBlaze.Models.Api;
using FreedomBlaze.Services.Webhooks;
using Microsoft.EntityFrameworkCore;

namespace FreedomBlaze.Services.Lightning;

public enum InvoiceRefreshOutcome
{
    /// <summary>The node still reports it pending.</summary>
    Unchanged,

    /// <summary>Paid or expired, and recorded with its webhooks, by this call or a concurrent one.</summary>
    Settled,

    /// <summary>The node no longer knows the invoice; the record is kept as it was.</summary>
    UnknownToNode,

    /// <summary>The invoice's backend cannot be used on this server.</summary>
    BackendUnavailable,

    /// <summary>The node refused or could not be reached.</summary>
    BackendFailure,

    /// <summary>The node reported a payment without the preimage that proves it; nothing was recorded.</summary>
    Unproven,
}

public sealed record InvoiceRefreshResult(InvoiceRefreshOutcome Outcome, LightningInvoice Invoice, string? Message = null);

/// <summary>
/// Asks the node that issued a pending invoice about it and records what it says. A settlement and the webhooks
/// announcing it are written in one transaction, and only once: the update applies only to a row that is still
/// pending, so when the sweep and a client's read race, one records it and the other finds it recorded.
/// </summary>
public sealed class LightningInvoiceRefresher(
    IDbContextFactory<FreedomBlazeDbContext> contexts,
    ILightningBackendFactory backendFactory,
    IWebhookOutbox outbox,
    TimeProvider timeProvider,
    ILogger<LightningInvoiceRefresher> logger)
{
    public async Task<InvoiceRefreshResult> RefreshAsync(LightningInvoice invoice, LightningBackend backend, CancellationToken cancellationToken)
    {
        ILightningBackend node;
        try
        {
            node = backendFactory.Create(backend);
        }
        catch (LightningBackendUnavailableException ex)
        {
            logger.LogError(ex, "Backend {BackendId} of merchant {MerchantId} cannot be used.", backend.Id, backend.MerchantId);
            return new InvoiceRefreshResult(InvoiceRefreshOutcome.BackendUnavailable, invoice, ex.Message);
        }

        BackendPayment? payment;
        try
        {
            payment = await node.FindIncomingPaymentAsync(invoice.PaymentHash!, cancellationToken);
        }
        catch (LightningBackendException ex)
        {
            logger.LogWarning(ex, "Backend {BackendId} could not look up invoice {PaymentHash}.", backend.Id, invoice.PaymentHash);
            return new InvoiceRefreshResult(InvoiceRefreshOutcome.BackendFailure, invoice, ex.Message);
        }

        if (payment is null)
        {
            logger.LogError("Backend {BackendId} no longer knows invoice {PaymentHash} issued for merchant {MerchantId}.",
                backend.Id, invoice.PaymentHash, invoice.MerchantId);
            return new InvoiceRefreshResult(InvoiceRefreshOutcome.UnknownToNode, invoice);
        }

        if (payment.IsPaid && !ProvesPayment(payment.Preimage, invoice.PaymentHash!))
        {
            logger.LogError("Backend {BackendId} reported invoice {PaymentHash} paid without its preimage; not recorded.",
                backend.Id, invoice.PaymentHash);
            return new InvoiceRefreshResult(InvoiceRefreshOutcome.Unproven, invoice, "The node reported a payment it could not prove.");
        }

        if (!payment.IsPaid && !payment.IsExpired)
            return new InvoiceRefreshResult(InvoiceRefreshOutcome.Unchanged, invoice);

        return new InvoiceRefreshResult(InvoiceRefreshOutcome.Settled, await SettleAsync(invoice, payment, cancellationToken));
    }

    /// <summary>True when <paramref name="preimage"/> hashes to <paramref name="paymentHash"/>, which only a real payment reveals.</summary>
    internal static bool ProvesPayment(string? preimage, string paymentHash)
    {
        Span<byte> bytes = stackalloc byte[32];
        if (preimage is not { Length: 64 }
            || Convert.FromHexString(preimage, bytes, out _, out var written) != OperationStatus.Done || written != 32)
            return false;

        return string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), paymentHash, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<LightningInvoice> SettleAsync(LightningInvoice invoice, BackendPayment payment, CancellationToken cancellationToken)
    {
        if (payment.IsPaid && payment.RequestedSat is { } requested && requested != invoice.AmountSat)
            logger.LogError("Invoice {PaymentHash}: the node requested {RequestedSat} sats, the gateway recorded {AmountSat}.",
                invoice.PaymentHash, requested, invoice.AmountSat);

        var status = payment.IsPaid ? LightningInvoiceStatus.Paid : LightningInvoiceStatus.Expired;
        DateTimeOffset? paidAt = payment.IsPaid ? payment.CompletedAt ?? timeProvider.GetUtcNow() : null;
        long? receivedSat = payment.IsPaid ? payment.ReceivedSat : null;
        long? feeSat = payment.IsPaid ? (long)Math.Ceiling(payment.FeesMsat / 1000m) : null;
        var eventType = payment.IsPaid ? WebhookEventTypes.InvoicePaid : WebhookEventTypes.InvoiceExpired;

        invoice.Status = status;
        invoice.PaidAtUtc = paidAt;
        invoice.ReceivedSat = receivedSat;
        invoice.FeeSat = feeSat;
        invoice.NextCheckAtUtc = null;

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var webhooks = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var updated = await db.LightningInvoices
                .Where(i => i.Id == invoice.Id && i.Status == LightningInvoiceStatus.Pending)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(i => i.Status, status)
                    .SetProperty(i => i.PaidAtUtc, paidAt)
                    .SetProperty(i => i.ReceivedSat, receivedSat)
                    .SetProperty(i => i.FeeSat, feeSat)
                    .SetProperty(i => i.NextCheckAtUtc, (DateTimeOffset?)null), cancellationToken);
            if (updated == 0)
                return (int?)null; // Recorded already, by whoever got here first.

            var queued = await outbox.EnqueueAsync(db, invoice.MerchantId, eventType,
                LightningInvoiceStatusResponse.From(invoice), invoice.Id, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return queued;
        });

        if (webhooks is null)
        {
            db.ChangeTracker.Clear();
            return await db.LightningInvoices.AsNoTracking().SingleAsync(i => i.Id == invoice.Id, cancellationToken);
        }

        logger.LogInformation("Invoice {PaymentHash} of merchant {MerchantId} is {Status}; {Webhooks} webhook(s) queued.",
            invoice.PaymentHash, invoice.MerchantId, status, webhooks);
        return invoice;
    }
}
