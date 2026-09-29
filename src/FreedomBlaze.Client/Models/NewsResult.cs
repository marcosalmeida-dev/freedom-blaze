namespace FreedomBlaze.Client.Models;

/// <summary>Outcome of a Bitcoin news lookup.</summary>
public enum NewsStatus
{
    /// <summary>Articles were returned.</summary>
    Ok,

    /// <summary>The lookup succeeded, but no news exists for that day.</summary>
    Empty,

    /// <summary>The news could not be produced (upstream error, quota, misconfiguration).</summary>
    Unavailable,
}

/// <summary>
/// The result of a news lookup. Carrying the outcome instead of throwing lets the UI tell
/// "there is no news for this day" apart from "we could not fetch the news", which are very
/// different messages for the reader — and keeps both render modes (direct server call and
/// WebAssembly HTTP call) on exactly the same contract.
/// </summary>
public sealed record NewsResult
{
    /// <summary>Successful lookup that produced nothing.</summary>
    public static readonly NewsResult NoNews = new();

    public IReadOnlyList<NewsArticleModel> Articles { get; init; } = [];

    public NewsStatus Status { get; init; } = NewsStatus.Empty;

    /// <summary>
    /// Reader-facing explanation, set only when <see cref="Status"/> is
    /// <see cref="NewsStatus.Unavailable"/>. Never contains raw upstream error text.
    /// </summary>
    public string? Message { get; init; }

    public bool HasArticles => Articles.Count > 0;

    public static NewsResult Ok(IReadOnlyList<NewsArticleModel> articles) =>
        articles.Count == 0 ? NoNews : new NewsResult { Articles = articles, Status = NewsStatus.Ok };

    public static NewsResult Unavailable(string message) =>
        new() { Status = NewsStatus.Unavailable, Message = message };
}
