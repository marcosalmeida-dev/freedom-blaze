using System.Text.Json;
using System.Text.RegularExpressions;
using FreedomBlaze.Models;
using FreedomBlaze.Services;
using HtmlAgilityPack;
using OpenAI.Responses;

public class ChatGptService
{
    private readonly HttpClient _httpClient;
    private readonly ImageService _imageService;
    private readonly BlobStorageService _blobStorageService;
    private readonly string _apiKey;

    private readonly JsonSerializerOptions _jsonSerializerOptions;

    public ChatGptService(HttpClient httpClient, BlobStorageService blobStorageService, ImageService imageService, string apiKey)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _blobStorageService = blobStorageService ?? throw new ArgumentNullException(nameof(blobStorageService));
        _imageService = imageService ?? throw new ArgumentNullException(nameof(imageService));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));

        _jsonSerializerOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
    }

    public async Task<List<NewsArticleModel>> GetTodayBitcoinNewsAsync()
    {
        string? jsonContent = await _blobStorageService.DownloadTextAsync("bitcoin-news", $"bitcoin-news-{DateTime.Now:dd-MM-yyyy}.json");

        if (string.IsNullOrEmpty(jsonContent))
        {
            return new List<NewsArticleModel>(); // Return an empty list if jsonContent is null or empty
        }

        var newsResult = JsonSerializer.Deserialize<List<NewsArticleModel>>(jsonContent, _jsonSerializerOptions) ?? new List<NewsArticleModel>();

        foreach (var newsArticle in newsResult.Where(w => w.NewsThumbImg == null))
        {
            newsArticle.NewsThumbImg = _imageService.GetAbsoluteImageUri("img/articles/default-img.png");
        }

        return newsResult;
    }

    public async Task GetBitcoinChatGptNews()
    {
        string promptBlob = await _blobStorageService.DownloadTextAsync("prompts", "get-bitcoin-news.txt") ?? string.Empty;

        if (string.IsNullOrEmpty(promptBlob))
        {
            throw new InvalidOperationException("Prompt content is empty or null.");
        }

        OpenAIResponseClient client = new(model: "gpt-4o", apiKey: _apiKey);

        OpenAIResponse response = await client.CreateResponseAsync(userInputText: promptBlob,
                                                                   new ResponseCreationOptions()
                                                                   {
                                                                       Tools = { ResponseTool.CreateWebSearchTool() },
                                                                       TruncationMode = ResponseTruncationMode.Auto,
                                                                       MaxOutputTokenCount = 8000
                                                                   });
        string textResult = string.Empty;
        foreach (ResponseItem item in response.OutputItems)
        {
            if (item is WebSearchCallResponseItem webSearchCall)
            {
                Console.WriteLine($"[Web search invoked]({webSearchCall.Status}) {webSearchCall.Id}");
            }
            else if (item is MessageResponseItem message)
            {
                var messageContent = message.Content?.FirstOrDefault()?.Text;
                Console.WriteLine($"[{message.Role}] {messageContent}");
                if(!string.IsNullOrEmpty(messageContent))
                {
                    textResult = ExtractJsonArray(messageContent) ?? string.Empty;
                }
            }
        }

        if (textResult == string.Empty)
        {
            throw new InvalidOperationException("No valid JSON content found in the response.");
        }

        List<NewsArticleModel> newsResult = JsonSerializer.Deserialize<List<NewsArticleModel>>(textResult, _jsonSerializerOptions) ?? new List<NewsArticleModel>();

        foreach (var newsArticle in newsResult.Where(w => string.IsNullOrEmpty(w.NewsThumbImg)))
        {
            newsArticle.NewsThumbImg = await GetMainArticleImageAsync(newsArticle.ArticleLinkUrl);
        }

        // Serialize the list to JSON
        string jsonContent = JsonSerializer.Serialize(newsResult, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        // Upload the JSON content to blob storage
        string blobName = $"bitcoin-news-{DateTime.Now:dd-MM-yyyy}.json";
        await _blobStorageService.UploadTextAsync("bitcoin-news", blobName, jsonContent);
    }

    public async Task<string?> GetMainArticleImageAsync(string articleLinkUrl)
    {
        _httpClient.DefaultRequestHeaders.Clear(); // Clear default headers
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.9");
        _httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");

        if (string.IsNullOrEmpty(articleLinkUrl))
        {
            throw new ArgumentException("Article link URL cannot be null or empty.", nameof(articleLinkUrl));
        }

        try
        {
            var response = await _httpClient.GetAsync(articleLinkUrl);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync();
            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(html);

            // Implement your logic to extract the main article image URL.
            // This is just a basic example and might need adjustments based on the website's structure.
            // Common approaches include:
            // 1. Using Open Graph tags (<meta property="og:image" ...>)
            // 2. Looking for specific image tags within the article content.
            // 3. Using CSS selectors to target the main image element.

            // Example using Open Graph tags (if available):
            var ogImageNode = htmlDoc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");
            if (ogImageNode != null)
            {
                var imageUrl = ogImageNode.GetAttributeValue("content", null);
                if (!string.IsNullOrEmpty(imageUrl))
                {
                    return imageUrl;
                }
            }

            // Example: look for the first <img> tag within an article tag.
            var articleNode = htmlDoc.DocumentNode.SelectSingleNode("//article"); // adjust selector as needed.
            if (articleNode != null)
            {
                var imageNode = articleNode.SelectSingleNode(".//img"); // find first img inside article.
                if (imageNode != null)
                {
                    var imageUrl = imageNode.GetAttributeValue("src", null);
                    if (!string.IsNullOrEmpty(imageUrl) && (imageUrl.StartsWith("http://") || imageUrl.StartsWith("https://")))
                    {
                        return imageUrl;
                    }
                }
            }

            // Example using a class selector (adjust selector as needed):
            var imageElement = htmlDoc.DocumentNode.SelectSingleNode("//img[@class='main-article-image']");

            if (imageElement != null)
            {
                var imageSrc = imageElement.GetAttributeValue("src", null);
                if (!string.IsNullOrEmpty(imageSrc))
                {
                    return imageSrc;
                }
            }

            // If no image is found using the above methods, return null or an appropriate default.
            return null;
        }
        catch (HttpRequestException ex)
        {
            // Handle HTTP request errors (e.g., 404, 500).
            Console.WriteLine($"Error fetching article: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            // Handle other exceptions (e.g., parsing errors).
            Console.WriteLine($"An error occurred: {ex.Message}");
            return null;
        }
    }

    private static string? ExtractJsonArray(string input)
    {
        var match = Regex.Match(input, @"\[\s*{.*?}\s*\]", RegexOptions.Singleline);
        return match.Success ? match.Value : null;
    }
}
