namespace FreedomBlaze.Options;

/// <summary>
/// Configuration for OpenAI-backed Bitcoin news retrieval, bound from the "OpenAI" section.
/// The news is fetched in real time via the Responses API web-search tool, so the model must be one
/// that supports web search and structured outputs.
/// </summary>
public class OpenAiOptions
{
    public const string Section = "OpenAI";

    /// <summary>OpenAI API key. Falls back to the legacy "ChatGptApiKey" setting when empty.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Model used for the web-search news call. Defaults to gpt-6.1-sol, a current-generation
    /// model that balances capability against cost and supports both web search and structured
    /// outputs. Check the model's own docs page before changing this: a model without web-search
    /// support makes every generation fail.
    /// </summary>
    public string Model { get; set; } = "gpt-6.1-sol";

    /// <summary>
    /// Reasoning effort for GPT-5/GPT-6 reasoning models. "medium" supports regional research.
    /// Empty leaves the model default in place; "none"/"minimal" are lifted to "low" for search
    /// compatibility and quality across models.
    /// </summary>
    public string ReasoningEffort { get; set; } = "medium";

    /// <summary>Daily edition size. Must be 9 to preserve the global coverage requirements.</summary>
    public int NewsArticleCount { get; set; } = 9;

    /// <summary>
    /// Number of calendar dates eligible for an edition, ending on its date. Prefer the freshest
    /// stories; allow recent regional reporting when no same-day story exists. Defaults to 3.
    /// </summary>
    public int NewsLookbackDays { get; set; } = 3;

    /// <summary>Per-response budget including reasoning and JSON output. Defaults to 24,000 tokens.</summary>
    public int MaxOutputTokenCount { get; set; } = 24_000;

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
    /// Upper bound on search including its coverage repair attempt and optional thumbnails.
    /// Persistence uses the host token and database timeout after successful search.
    /// Host shutdown also cancels generation. Defaults to 10 minutes.
    /// </summary>
    public TimeSpan GenerationTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How far back EnsureNewsAsync may generate a missing edition. Public readers never generate news.
    /// Older days are still served from storage. Defaults to 30 days, matching the date picker.
    /// </summary>
    public int MaxGenerationHistoryDays { get; set; } = 30;
}
