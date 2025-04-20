using System.Text.Json;
using System.Text.RegularExpressions;
using FreedomBlaze.Constants;
using FreedomBlaze.Models;
using FreedomBlaze.Services;
using HtmlAgilityPack;
using Microsoft.Extensions.Caching.Memory;
using OpenAI.Images;
using OpenAI.Responses;

public class ChatGptService
{
    private readonly HttpClient _httpClient;
    private readonly ImageService _imageService;
    private readonly BlobStorageService _blobStorageService;
    private readonly IMemoryCache _cache;
    private readonly string _apiKey;

    private readonly string _promptContainerName = "prompts";
    private readonly string _promptGetBitcoinNewsBlobName = "get-bitcoin-news.txt";
    private readonly string _newsContainerName = "bitcoin-news";
    private readonly string _newsBlobName = $"bitcoin-news-{DateTime.Now:dd-MM-yyyy}/result.json";

    private readonly JsonSerializerOptions _jsonSerializerOptions;

    public ChatGptService(HttpClient httpClient, BlobStorageService blobStorageService, ImageService imageService, IMemoryCache memoryCache, IConfiguration configuration)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _blobStorageService = blobStorageService ?? throw new ArgumentNullException(nameof(blobStorageService));
        _imageService = imageService ?? throw new ArgumentNullException(nameof(imageService));
        _cache = memoryCache;
        _apiKey = configuration["ChatGptApiKey"] ?? throw new ArgumentNullException("ChatGptApiKey");

        _jsonSerializerOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
    }

    public async Task SaveDefaultImageToBlobStorage()
    {
        // Get the image URL
        var imageUrl = _imageService.GetAbsoluteImageUri("img/articles/default-img.png");

        // Extract the file name from the image URL
        var fileName = Path.GetFileName(new Uri(imageUrl).LocalPath);

        // Define the blob name with a subfolder
        var blobName = $"images/{fileName}";

        // Download the image as a byte array
        using var response = await _httpClient.GetAsync(imageUrl);
        response.EnsureSuccessStatusCode();
        var imageBytes = await response.Content.ReadAsByteArrayAsync();

        // Upload the image to blob storage
        var blobUrl = await _blobStorageService.UploadImageAsync(_newsContainerName, blobName, imageBytes);

        Console.WriteLine($"Image uploaded to blob storage: {blobUrl}");
    }

    public async Task<List<NewsArticleModel>?> GetTodayBitcoinNewsAsync()
    {
        if (!_cache.TryGetValue(CacheKeys.TodayBitcoinNewsCacheKey, out List<NewsArticleModel>? cachedNews))
        {
            string? jsonContent = await _blobStorageService.DownloadTextAsync(_newsContainerName, _newsBlobName);

            if (string.IsNullOrEmpty(jsonContent))
            {
                cachedNews = new List<NewsArticleModel>(); 
            }
            else
            {
                cachedNews = JsonSerializer.Deserialize<List<NewsArticleModel>>(jsonContent, _jsonSerializerOptions) ?? new List<NewsArticleModel>();

                foreach (var newsArticle in cachedNews)
                {
                    newsArticle.NewsThumbImg = await SearchArticleImageAsync(newsArticle.ArticleLinkUrl);
                    if (string.IsNullOrEmpty(newsArticle.NewsThumbImg))
                    {
                        newsArticle.NewsThumbImg = _imageService.GetAbsoluteImageUri("img/articles/default-img.png"); //await GenerateChatGptImageByText(newsArticle.Title); // 
                    }
                }
            }

            _cache.Set(CacheKeys.TodayBitcoinNewsCacheKey, cachedNews, TimeSpan.FromHours(1));
        }

        return cachedNews;
    }

    public async Task SearchBitcoinChatGptNews(string model)
    {
        string promptBlob = await _blobStorageService.DownloadTextAsync(_promptContainerName, _promptGetBitcoinNewsBlobName) ?? string.Empty;

        if (string.IsNullOrEmpty(promptBlob))
        {
            throw new InvalidOperationException("Prompt content is empty or null.");
        }

        OpenAIResponseClient client = new(model: model, apiKey: _apiKey);

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
                if (!string.IsNullOrEmpty(messageContent))
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
            newsArticle.NewsThumbImg = await SearchArticleImageAsync(newsArticle.ArticleLinkUrl);
            if (string.IsNullOrEmpty(newsArticle.NewsThumbImg))
            {
                newsArticle.NewsThumbImg = _imageService.GetAbsoluteImageUri("img/articles/default-img.png"); //await GenerateChatGptImageByText(newsArticle.Title); // 
            }
        }

        //var tasks = newsResult.Where(w => string.IsNullOrEmpty(w.NewsThumbImg))
        //                      .Select(async newsArticle =>
        //                      {
        //                         var img = await GetMainArticleImageAsync(newsArticle.ArticleLinkUrl);
        //                          newsArticle.NewsThumbImg = !string.IsNullOrEmpty(img) ? img
        //                                                                                : await GetGeneratedChatGptImageByText(newsArticle.Title);
        //                                                                               //: _imageService.GetAbsoluteImageUri("img/articles/default-img.png");
        //                      });

        //await Task.WhenAll(tasks);

        string jsonContent = JsonSerializer.Serialize(newsResult, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        // Upload the JSON content to blob storage
        await _blobStorageService.UploadTextAsync(_newsContainerName, _newsBlobName, jsonContent);

        // Update the cache
        _cache.Set(CacheKeys.TodayBitcoinNewsCacheKey, newsResult, TimeSpan.FromHours(1));
    }

    public async Task<string> GenerateChatGptImageByText(string prompt)
    {
        ImageClient client = new(model: "dall-e-3", apiKey: _apiKey);

        ImageGenerationOptions options = new()
        {
            Quality = GeneratedImageQuality.Standard,
            Size = GeneratedImageSize.W1024xH1024,
            Style = GeneratedImageStyle.Natural,
            //ResponseFormat = GeneratedImageFormat.Uri
            ResponseFormat = GeneratedImageFormat.Bytes
        };

        GeneratedImage image = await client.GenerateImageAsync("Generate a image, with no text inside, for this title: " + prompt, options);

        //TODO: save it into blob storage and return generated url getting from image.Bytes
        //var blobUrl = _blobStorageService.UploadTextAsync()
        string blobUrl = string.Empty;

        return options.ResponseFormat == GeneratedImageFormat.Uri ? image.ImageUri.ToString() : blobUrl;

        //BinaryData bytes = image.ImageBytes;

        //using FileStream stream = File.OpenWrite($"{Guid.NewGuid()}.png");
        //bytes.ToStream().CopyTo(stream);
    }

    public async Task<string?> SearchArticleImageAsync(string articleLinkUrl)
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
