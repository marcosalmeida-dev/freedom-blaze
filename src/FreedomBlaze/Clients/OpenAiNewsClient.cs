using System.Text;
using System.Text.Json;
using FreedomBlaze.Models;
using FreedomBlaze.Options;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Responses;

namespace FreedomBlaze.Clients;

// The Responses API and its web-search tool are marked "evaluation only" in the OpenAI SDK.
#pragma warning disable OPENAI001

/// <summary>
/// Retrieves real-time Bitcoin news using the OpenAI <b>Responses API</b> together with the
/// built-in <c>web_search</c> tool.
/// <para>
/// The previous implementation issued a plain chat completion, which can only draw on the model's
/// (stale) training data and tends to invent article URLs. This client instead lets the model
/// perform a live web search and returns articles backed by real source citations, formatted as
/// strict structured JSON so no fragile regex parsing is needed.
/// </para>
/// </summary>
public class OpenAiNewsClient(
    IOptions<OpenAiOptions> options,
    TimeProvider timeProvider,
    ILogger<OpenAiNewsClient> logger,
    OpenAIClient? openAiClient = null)
{
    private readonly OpenAiOptions _options = options.Value;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<OpenAiNewsClient> _logger = logger;

    // The OpenAIClient is the SDK's recommended entry point; derive the per-feature ResponsesClient
    // from it. It is null only when no API key is configured, in which case calls throw a clear error
    // instead of a NullReferenceException.
    private readonly ResponsesClient? _responses = openAiClient?.GetResponsesClient();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Performs a live web search and returns the most relevant Bitcoin news articles published on
    /// <paramref name="date"/> from around the world.
    /// </summary>
    public async Task<List<NewsArticleModel>> GetBitcoinNewsAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        if (_responses is null)
        {
            throw new InvalidOperationException(
                "OpenAI API key is not configured. Set 'OpenAI:ApiKey' (or the legacy 'ChatGptApiKey').");
        }

        var today = DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);
        var count = Math.Clamp(_options.NewsArticleCount, 1, 20);

        // Phrase the time window relative to whether the requested day is today or in the past.
        var timeframe = date >= today
            ? $"published within the last 24 hours (today is {today:yyyy-MM-dd})"
            : $"published on {date:yyyy-MM-dd}";

        var requestOptions = new CreateResponseOptions
        {
            Model = _options.Model,
            Instructions =
                $"""
                You are a financial news editor specialising in Bitcoin.
                Use the web_search tool to find {count} distinct, high-quality Bitcoin news stories
                {timeframe}.
                Cover a diverse mix of regions around the world (e.g. North America, South America,
                Europe, Africa, Asia, Oceania) and prefer reputable outlets.
                Rules:
                - Exactly {count} articles, no duplicates, no opinion/sponsored pieces.
                - "articleUrl" must be a real URL you actually opened via web search.
                - "summary" is a neutral 2-3 sentence recap.
                - "publishedDate" is the article's publication date in ISO-8601 (yyyy-MM-dd).
                """,
            Tools = { ResponseTool.CreateWebSearchTool() },
            TextOptions = new ResponseTextOptions
            {
                TextFormat = ResponseTextFormat.CreateJsonSchemaFormat(
                    jsonSchemaFormatName: "bitcoin_news",
                    jsonSchema: BinaryData.FromString(NewsJsonSchema),
                    jsonSchemaFormatDescription: "A list of recent Bitcoin news articles from around the world.",
                    jsonSchemaIsStrict: true),
            },
        };

        requestOptions.InputItems.Add(ResponseItem.CreateUserMessageItem(
            $"Give me the top {count} Bitcoin news stories from around the world for {date:yyyy-MM-dd}."));

        _logger.LogInformation(
            "Requesting {Count} Bitcoin news articles for {Date} from OpenAI model {Model}.", count, date, _options.Model);

        var result = await _responses.CreateResponseAsync(requestOptions, cancellationToken);
        ResponseResult response = result.Value;

        var (json, citations) = ExtractOutput(response);

        if (string.IsNullOrWhiteSpace(json))
        {
            _logger.LogWarning("OpenAI returned an empty response for the Bitcoin news request.");
            return [];
        }

        var articles = ParseArticles(json, date.ToDateTime(TimeOnly.MinValue));
        BackfillFromCitations(articles, citations);

        _logger.LogInformation("Retrieved {Count} Bitcoin news articles.", articles.Count);
        return articles.Take(count).ToList();
    }

    /// <summary>
    /// Concatenates the assistant's output text and collects any URL citations produced by the
    /// web-search tool (used to recover real source links if the model omits them).
    /// </summary>
    private static (string Json, List<UriCitationMessageAnnotation> Citations) ExtractOutput(ResponseResult response)
    {
        var builder = new StringBuilder();
        var citations = new List<UriCitationMessageAnnotation>();

        foreach (var item in response.OutputItems)
        {
            if (item is not MessageResponseItem message)
            {
                continue;
            }

            foreach (var part in message.Content)
            {
                if (!string.IsNullOrEmpty(part.Text))
                {
                    builder.Append(part.Text);
                }

                foreach (var annotation in part.OutputTextAnnotations)
                {
                    if (annotation is UriCitationMessageAnnotation uriCitation)
                    {
                        citations.Add(uriCitation);
                    }
                }
            }
        }

        return (builder.ToString(), citations);
    }

    private List<NewsArticleModel> ParseArticles(string json, DateTime fallbackDate)
    {
        // Strict structured output yields a bare JSON object; the extra guard tolerates any stray
        // markdown fences should a non-strict model ever be configured.
        var payload = TryDeserialize(json) ?? TryDeserialize(ExtractJsonObject(json));

        if (payload?.Articles is null or { Count: 0 })
        {
            _logger.LogWarning("Could not parse any articles from the OpenAI response.");
            return [];
        }

        return payload.Articles
            .Where(a => !string.IsNullOrWhiteSpace(a.Title))
            .Select(a => new NewsArticleModel
            {
                Title = a.Title.Trim(),
                Text = a.Summary?.Trim() ?? string.Empty,
                Source = a.SourceName?.Trim() ?? string.Empty,
                SourceRegion = a.SourceRegion?.Trim() ?? string.Empty,
                SourceUrl = a.SourceUrl?.Trim() ?? string.Empty,
                ArticleLinkUrl = a.ArticleUrl?.Trim() ?? string.Empty,
                Date = ParseDate(a.PublishedDate, fallbackDate),
            })
            .ToList();
    }

    private NewsResponse? TryDeserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<NewsResponse>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize the OpenAI news payload.");
            return null;
        }
    }

    /// <summary>If the model omitted an article URL, fall back to a web-search citation.</summary>
    private static void BackfillFromCitations(List<NewsArticleModel> articles, List<UriCitationMessageAnnotation> citations)
    {
        if (citations.Count == 0)
        {
            return;
        }

        var citationUrls = citations
            .Where(c => c.Uri is not null)
            .Select(c => c.Uri.ToString())
            .Distinct()
            .ToList();

        var index = 0;
        foreach (var article in articles)
        {
            if (string.IsNullOrWhiteSpace(article.ArticleLinkUrl) && index < citationUrls.Count)
            {
                article.ArticleLinkUrl = citationUrls[index++];
            }
        }
    }

    private static DateTime ParseDate(string? value, DateTime fallback) =>
        DateTime.TryParse(value, out var parsed) ? parsed : fallback;

    private static string? ExtractJsonObject(string input)
    {
        var start = input.IndexOf('{');
        var end = input.LastIndexOf('}');
        return start >= 0 && end > start ? input[start..(end + 1)] : null;
    }

    private sealed record NewsResponse(List<NewsItem>? Articles);

    private sealed record NewsItem(
        string Title,
        string? Summary,
        string? SourceName,
        string? SourceRegion,
        string? SourceUrl,
        string? ArticleUrl,
        string? PublishedDate);

    private const string NewsJsonSchema =
        """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "articles": {
              "type": "array",
              "description": "The list of Bitcoin news articles.",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "title": { "type": "string", "description": "Headline of the article." },
                  "summary": { "type": "string", "description": "Neutral 2-3 sentence summary." },
                  "sourceName": { "type": "string", "description": "Name of the publication." },
                  "sourceRegion": { "type": "string", "description": "Country or world region of the source." },
                  "sourceUrl": { "type": "string", "description": "Home page URL of the publication." },
                  "articleUrl": { "type": "string", "description": "Direct URL to the article." },
                  "publishedDate": { "type": "string", "description": "Publication date in ISO-8601 (yyyy-MM-dd)." }
                },
                "required": ["title", "summary", "sourceName", "sourceRegion", "sourceUrl", "articleUrl", "publishedDate"]
              }
            }
          },
          "required": ["articles"]
        }
        """;
}
