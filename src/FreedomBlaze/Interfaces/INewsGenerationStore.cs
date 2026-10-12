namespace FreedomBlaze.Interfaces;

/// <summary>Coordinates paid news generation across processes and retains its attempt history.</summary>
public interface INewsGenerationStore
{
    /// <summary>
    /// Acquires an exclusive lease for the edition, or returns null immediately when another
    /// process owns it. Keep the lease alive throughout generation and outcome persistence.
    /// </summary>
    Task<IAsyncDisposable?> TryAcquireLeaseAsync(DateOnly date, CancellationToken cancellationToken = default);

    Task<NewsGenerationState> GetStateAsync(DateOnly date, CancellationToken cancellationToken = default);

    /// <summary>Records a new paid attempt. The caller must own the edition's lease.</summary>
    Task RecordAttemptAsync(DateOnly date, CancellationToken cancellationToken = default);

    /// <summary>Completes the current attempt. A null failure denotes success; requires the lease.</summary>
    Task RecordOutcomeAsync(DateOnly date, string? failure, CancellationToken cancellationToken = default);
}

/// <summary>
/// Durable generation state. IsRunning can survive a crashed process; an acquired lease is the
/// authority for whether another process still owns generation.
/// </summary>
public sealed record NewsGenerationState(
    int AttemptCount, DateTimeOffset? LastAttemptUtc, bool IsRunning, string? LastFailure,
    DateTimeOffset? LastCompletedUtc = null);
