namespace FreedomBlaze.Options;

/// <summary>
/// Configuration for OpenAI-backed Bitcoin news retrieval, bound from the "OpenAI" section.
/// The news is fetched in real time via the Responses API web-search tool, so the model
/// must be one that supports that tool (e.g. gpt-4o, gpt-4.1, gpt-5 family).
/// </summary>
public class OpenAiOptions
{
    public const string Section = "OpenAI";

    /// <summary>OpenAI API key. Falls back to the legacy "ChatGptApiKey" setting when empty.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Model used for the web-search news call. Defaults to gpt-4o.</summary>
    public string Model { get; set; } = "gpt-4o";

    /// <summary>How many news articles to retrieve per day. Defaults to 9.</summary>
    public int NewsArticleCount { get; set; } = 9;

    /// <summary>
    /// How long a generated news set is served from cache before a new (paid) web-search
    /// call is made. Defaults to 6 hours.
    /// </summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromHours(6);
}
