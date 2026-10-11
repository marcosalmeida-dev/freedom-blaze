using FreedomBlaze.Client.Models;

namespace FreedomBlaze.Services;

/// <summary>Refreshes today's news after startup and every day at 08:00 UTC-03:00.</summary>
public sealed class BitcoinNewsBackgroundService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IHostApplicationLifetime applicationLifetime,
    ILogger<BitcoinNewsBackgroundService> logger,
    BitcoinNewsRetryQueue? retryQueue = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await WaitForApplicationStartedAsync(stoppingToken);

            var now = timeProvider.GetUtcNow();
            var nextRun = BitcoinNewsSchedule.GetNextRunUtc(now);
            await GenerateAsync(BitcoinNewsSchedule.Today(now), "startup refresh", stoppingToken);

            // A startup search that spans 08:00 also serves that scheduled run. Do not immediately
            // pay for another search just because startup completed after its captured deadline.
            now = timeProvider.GetUtcNow();
            if (now >= nextRun)
            {
                nextRun = BitcoinNewsSchedule.GetNextRunUtc(now);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                await WaitForWorkAsync(nextRun, stoppingToken);

                var scheduled = timeProvider.GetUtcNow() >= nextRun;
                if (!scheduled && retryQueue?.HasPending != true) continue;

                // Re-read the clock after waking: a suspended process or a clock correction must
                // generate the current edition, not an obsolete date captured before the delay.
                await GenerateAsync(BitcoinNewsSchedule.Today(timeProvider.GetUtcNow()),
                    scheduled ? "daily refresh" : "reader retry", stoppingToken);
                if (timeProvider.GetUtcNow() >= nextRun)
                    nextRun = BitcoinNewsSchedule.GetNextRunUtc(timeProvider.GetUtcNow());
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown cancels both waiting and any active generation.
        }
        finally
        {
            retryQueue?.CancelPending(stoppingToken);
        }
    }

    private async Task WaitForWorkAsync(DateTimeOffset nextRun, CancellationToken stoppingToken)
    {
        if (retryQueue is null)
        {
            await WaitUntilAsync(nextRun, stoppingToken);
            return;
        }
        using var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var scheduled = WaitUntilAsync(nextRun, wake.Token);
        var requested = retryQueue.WaitForRequestAsync(wake.Token);
        await Task.WhenAny(scheduled, requested);
        wake.Cancel();
        try { await Task.WhenAll(scheduled, requested); }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }
        stoppingToken.ThrowIfCancellationRequested();
    }

    private async Task WaitForApplicationStartedAsync(CancellationToken cancellationToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = applicationLifetime.ApplicationStarted.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(), started);
        await started.Task.WaitAsync(cancellationToken);
    }

    private async Task WaitUntilAsync(DateTimeOffset scheduledTime, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var delay = scheduledTime - timeProvider.GetUtcNow();
            if (delay <= TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(delay, timeProvider, cancellationToken);
        }
    }

    private async Task GenerateAsync(DateOnly date, string operation, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<BitcoinNewsService>();
            var result = await service.RefreshNewsAsync(date, cancellationToken);
            retryQueue?.Complete(result);

            if (result.Status == NewsStatus.Unavailable)
            {
                logger.LogWarning("Bitcoin news {Operation} for {Date} was unavailable: {Message}", operation, date, result.Message);
            }
            else
            {
                logger.LogInformation("Bitcoin news {Operation} for {Date} completed with {Count} articles.", operation, date, result.Articles.Count);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            retryQueue?.Complete(NewsResult.Unavailable("Bitcoin news is temporarily unavailable. Please try again later."));
            logger.LogError(ex, "Bitcoin news {Operation} for {Date} failed; the next daily refresh remains scheduled.", operation, date);
        }
    }
}
