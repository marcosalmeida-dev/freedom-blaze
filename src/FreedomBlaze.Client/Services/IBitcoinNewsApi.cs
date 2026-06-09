using FreedomBlaze.Models;

namespace FreedomBlaze.Client.Services;

/// <summary>
/// Source of Bitcoin news for the UI. The component depends on this abstraction so it can run in
/// both render modes: on the server it is backed by a direct service call (no loopback HTTP),
/// and in WebAssembly by an HTTP call to the server API.
/// </summary>
public interface IBitcoinNewsApi
{
    Task<List<NewsArticleModel>> GetNewsAsync(DateOnly date, CancellationToken cancellationToken = default);

    Task<List<NewsArticleModel>> RefreshNewsAsync(DateOnly date, CancellationToken cancellationToken = default);

    /// <summary>The dates that already have saved news (drives the date filter), most recent first.</summary>
    Task<List<DateOnly>> GetAvailableDatesAsync(CancellationToken cancellationToken = default);
}
