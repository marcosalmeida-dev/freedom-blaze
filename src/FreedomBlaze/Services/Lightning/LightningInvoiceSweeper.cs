using FreedomBlaze.Data;
using FreedomBlaze.Data.Entities;
using FreedomBlaze.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FreedomBlaze.Services.Lightning;

/// <summary>
/// Confirms pending invoices with their nodes in the background, so a payment is recorded (and its webhooks
/// sent) even when nobody asks about the invoice: the payer's browser closed, the merchant stopped polling, or
/// the node's own fire-once webhook was lost.
/// </summary>
public sealed class LightningInvoiceSweeper(
    IDbContextFactory<FreedomBlazeDbContext> contexts,
    LightningInvoiceRefresher refresher,
    IOptions<LightningWorkerOptions> options,
    TimeProvider timeProvider,
    ILogger<LightningInvoiceSweeper> logger)
{
    /// <summary>Checks the invoices that are due. Returns how many were checked.</summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);

        var due = await db.LightningInvoices.AsNoTracking()
            .Where(i => i.Status == LightningInvoiceStatus.Pending && i.NextCheckAtUtc != null && i.NextCheckAtUtc <= now)
            .OrderBy(i => i.NextCheckAtUtc)
            .Take(Math.Max(1, options.Value.BatchSize))
            .ToListAsync(cancellationToken);
        if (due.Count == 0)
            return 0;

        var backendIds = due.Select(i => i.BackendId).Distinct().ToList();
        var backends = await db.LightningBackends.AsNoTracking()
            .Where(b => backendIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, cancellationToken);

        var checkedCount = 0;
        foreach (var invoice in due)
        {
            // Claim the check by moving it forward first, so another instance skips it and a failure below
            // is simply retried at the next scheduled time.
            var next = LightningInvoiceSchedule.NextCheck(invoice.CreatedAtUtc, invoice.ExpiresAtUtc, now);
            var claimed = await db.LightningInvoices
                .Where(i => i.Id == invoice.Id && i.Status == LightningInvoiceStatus.Pending && i.NextCheckAtUtc == invoice.NextCheckAtUtc)
                .ExecuteUpdateAsync(set => set.SetProperty(i => i.NextCheckAtUtc, next), cancellationToken);
            if (claimed == 0)
                continue;

            invoice.NextCheckAtUtc = next;
            if (next is null)
                logger.LogError("Giving up on invoice {PaymentHash} of merchant {MerchantId}: still unconfirmed a week after it expired.",
                    invoice.PaymentHash, invoice.MerchantId);

            var result = await refresher.RefreshAsync(invoice, backends[invoice.BackendId], cancellationToken);
            checkedCount++;
            if (result.Outcome is not (InvoiceRefreshOutcome.Unchanged or InvoiceRefreshOutcome.Settled))
                logger.LogWarning("Background check of invoice {PaymentHash}: {Outcome}. Next check {NextCheck}.",
                    invoice.PaymentHash, result.Outcome, next);
        }

        return checkedCount;
    }
}
