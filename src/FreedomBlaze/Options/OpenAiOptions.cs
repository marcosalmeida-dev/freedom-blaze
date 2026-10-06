namespace FreedomBlaze.Options;

/// <summary>
/// Configuration for OpenAI-backed Bitcoin news retrieval, bound from the "OpenAI" section.
/// The news is fetched in real time via the Responses API web-search tool, so the model must be one
/// that supports that tool (the gpt-5 family, gpt-4.1/gpt-4.1-mini, or gpt-4o).
/// </summary>
public class OpenAiOptions
{
    public const string Section = "OpenAI";

    /// <summary>OpenAI API key. Falls back to the legacy "ChatGptApiKey" setting when empty.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Model used for the web-search news call. Defaults to gpt-5.6-terra, the current-generation
    /// model that balances capability against cost and supports both web search and structured
    /// outputs. Check the model's own docs page before changing this: a model without web-search
    /// support makes every generation fail.
    /// </summary>
    public string Model { get; set; } = "gpt-5.6-terra";

    /// <summary>
    /// Reasoning effort for the gpt-5 family. "low" keeps a headline sweep fast and cheap.
    /// Empty leaves the model default in place; "none"/"minimal" are lifted to "low" for search
    /// compatibility and quality across models.
    /// </summary>
    public string ReasoningEffort { get; set; } = "low";

    /// <summary>Daily edition size. Must be 9 to preserve the global coverage requirements.</summary>
    public int NewsArticleCount { get; set; } = 9;

    /// <summary>
    /// How long a generated news set is served from the in-memory cache before falling back to
    /// the persistent store. Defaults to 24 hours (news is keyed per day).
    /// </summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How long to stop attempting generation after a failure. Billing and configuration failures
    /// pause all dates; other failures pause the requested day. Defaults to 10 minutes.
    /// </summary>
    public TimeSpan FailureCooldown { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Upper bound on a single generation, independent of any inbound request's lifetime.
    /// Defaults to 4 minutes.
    /// </summary>
    public TimeSpan GenerationTimeout { get; set; } = TimeSpan.FromMinutes(4);

    /// <summary>
    /// How far back a caller may trigger a brand-new (paid) generation. Older days are still served
    /// from storage, but never generated on demand, so the endpoint cannot be walked backwards
    /// through history to run up a bill. Defaults to 30 days, matching the date picker.
    /// </summary>
    public int MaxGenerationHistoryDays { get; set; } = 30;
}
