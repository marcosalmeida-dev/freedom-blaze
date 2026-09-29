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
    /// When <c>false</c>, only already-stored news is returned and no (paid) generation is started.
    /// Used during prerender so a crawler still gets real content without triggering an expensive
    /// call. The WebAssembly implementation always runs interactively, so it ignores the flag.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<NewsResult> GetNewsAsync(DateOnly date, bool allowGeneration = true, CancellationToken cancellationToken = default);

    /// <summary>The dates that already have saved news (drives the date filter), most recent first.</summary>
    Task<List<DateOnly>> GetAvailableDatesAsync(CancellationToken cancellationToken = default);
}
