using System.Threading.Channels;
using FreedomBlaze.Client.Models;
using FreedomBlaze.Options;
using Microsoft.Extensions.Options;

namespace FreedomBlaze.Services;

/// <summary>Coalesces explicit reader retries for the background worker.</summary>
public sealed class BitcoinNewsRetryQueue(TimeProvider timeProvider, IOptions<OpenAiOptions>? options = null)
{
    private readonly object _sync = new();
    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(1);
    private TaskCompletionSource<NewsResult>? _pending;
    private NewsRetryOperation? _current;
    private readonly Dictionary<Guid, NewsRetryOperation> _operations = new();
    private readonly Queue<Guid> _operationOrder = new();
    private DateTimeOffset? _lastCompleted;
    private NewsResult? _lastResult;
    private static readonly TimeSpan SuccessRetryInterval = TimeSpan.FromMinutes(1);
    private readonly TimeSpan _failureRetryInterval = options?.Value.FailureCooldown ?? new OpenAiOptions().FailureCooldown;

    public bool HasPending { get { lock (_sync) return _pending is not null; } }

    public Task<NewsResult> RequestAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var operation = Start();
            // Canceling navigation only cancels this waiter, never the shared paid search.
            return operation.IsCompleted ? Task.FromResult(operation.Result!) : _pending!.Task.WaitAsync(cancellationToken);
        }
    }

    public NewsRetryOperation Start()
    {
        lock (_sync)
        {
            if (_pending is not null) return _current!;
            var retryInterval = _lastResult?.Status == NewsStatus.Unavailable ? _failureRetryInterval : SuccessRetryInterval;
            if (_lastCompleted is { } completed && timeProvider.GetUtcNow() - completed < retryInterval)
            {
                var result = _lastResult!;
                var remainingMinutes = Math.Max(1, (int)Math.Ceiling(
                    (completed + retryInterval - timeProvider.GetUtcNow()).TotalMinutes));
                var minuteLabel = remainingMinutes == 1 ? "minute" : "minutes";
                return new(Guid.Empty, true, result.Status == NewsStatus.Unavailable
                    ? NewsResult.Unavailable($"{result.Message} A refresh was recently attempted; please wait {remainingMinutes} {minuteLabel} before retrying.")
                    : result);
            }
            _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            // The timestamp lets other replicas recognize this queued request before its worker
            // acquires the shared generation lease, without starting an additional paid search.
            _current = new(Guid.CreateVersion7(timeProvider.GetUtcNow()), false, null);
            if (_operations.Count >= 16) _operations.Remove(_operationOrder.Dequeue());
            _operations[_current.Id] = _current;
            _operationOrder.Enqueue(_current.Id);
            _requests.Writer.TryWrite(true);
            return _current;
        }
    }

    public NewsRetryOperation? GetStatus(Guid id)
    {
        lock (_sync) return _operations.GetValueOrDefault(id);
    }

    public async Task WaitForRequestAsync(CancellationToken cancellationToken) =>
        _ = await _requests.Reader.ReadAsync(cancellationToken);

    public void Complete(NewsResult result)
    {
        lock (_sync)
        {
            _lastCompleted = timeProvider.GetUtcNow();
            _lastResult = result;
            if (_pending is not null && _current is not null)
                _operations[_current.Id] = _current with { IsCompleted = true, Result = result };
            _pending?.TrySetResult(result);
            _pending = null;
        }
    }

    public void CancelPending(CancellationToken stoppingToken)
    {
        lock (_sync)
        {
            _pending?.TrySetCanceled(stoppingToken);
            if (_pending is not null && _current is not null)
                _operations[_current.Id] = _current with
                {
                    IsCompleted = true,
                    Result = NewsResult.Unavailable("The news service restarted. Please retry in a moment.")
                };
            _pending = null;
        }
    }
}
