using FreedomBlaze.Options;
using FreedomBlaze.Services.Webhooks;
using Microsoft.Extensions.Options;

namespace FreedomBlaze.Services.Lightning;

/// <summary>
/// Every few seconds, confirms due pending invoices with their nodes and sends due webhooks. Safe to run on more
/// than one replica: each invoice check and each delivery is claimed in the database before it is worked on.
/// </summary>
public sealed class LightningGatewayWorker(
    LightningInvoiceSweeper sweeper,
    WebhookDispatcher dispatcher,
    IOptions<LightningWorkerOptions> options,
    TimeProvider timeProvider,
    IHostApplicationLifetime applicationLifetime,
    ILogger<LightningGatewayWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("The Lightning gateway worker is disabled (LightningWorker:Enabled).");
            return;
        }

        try
        {
            await WaitForApplicationStartedAsync(stoppingToken);
            logger.LogInformation("Lightning gateway worker started; a round every {Interval} s.", settings.IntervalSeconds);

            var interval = TimeSpan.FromSeconds(Math.Max(1, settings.IntervalSeconds));
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunOnceAsync(stoppingToken);
                await Task.Delay(interval, timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    /// <summary>One round: invoice checks first, so the webhooks they queue go out in the same round.</summary>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await sweeper.SweepAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "The background invoice check failed; it runs again next round.");
        }

        try
        {
            await dispatcher.DispatchDueAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Sending webhooks failed; they are retried next round.");
        }
    }

    private async Task WaitForApplicationStartedAsync(CancellationToken cancellationToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = applicationLifetime.ApplicationStarted.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(), started);
        await started.Task.WaitAsync(cancellationToken);
    }
}
