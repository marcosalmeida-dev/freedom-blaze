using FreedomBlaze.Client.Models;

namespace FreedomBlaze.Client.Interfaces;

/// <summary>
/// Source of Bitcoin news for the UI. The component depends on this abstraction so it can run in
/// both render modes: on the server it is backed by a direct service call (no loopback HTTP),
/// and in WebAssembly by an HTTP call to the server API.
/// </summary>
public interface IBitcoinNewsApiService
{
    /// <summary>
    /// Gets the news for <paramref name="date"/>.
    /// </summary>
    /// <param name="date">The calendar day to read.</param>
    /// <param name="allowGeneration">
    /// Legacy argument retained for source compatibility. Ignored: both implementations only read
    /// saved editions. Generation is handled by the server's background worker.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<NewsResult> GetNewsAsync(DateOnly date, bool allowGeneration = false, CancellationToken cancellationToken = default);

    /// <summary>Retries a missing current edition through the server's background worker.</summary>
    Task<NewsResult> RetryNewsAsync(DateOnly date, CancellationToken cancellationToken = default);

    /// <summary>The dates that already have saved news (drives the date filter), most recent first.</summary>
    Task<List<DateOnly>> GetAvailableDatesAsync(CancellationToken cancellationToken = default);
}
