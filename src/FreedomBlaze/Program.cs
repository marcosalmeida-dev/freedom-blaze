using System.ClientModel;
using Azure.Identity;
using Azure.Storage;
using Azure.Storage.Blobs;
using FreedomBlaze;
using FreedomBlaze.Client.Services;
using FreedomBlaze.Components;
using FreedomBlaze.Configuration;
using FreedomBlaze.Data;
using FreedomBlaze.Data.Repositories;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;
using FreedomBlaze.Options;
using FreedomBlaze.ServiceDefaults;
using FreedomBlaze.Services;
using FreedomBlaze.WebClients;
using FreedomBlaze.WebClients.BitcoinExchanges;
using FreedomBlaze.WebClients.CurrencyExchanges;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using OpenAI;
using Phoenixd.NET;
using Phoenixd.NET.Hubs;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IConfiguration>(provider => builder.Configuration);

builder.Services.Configure<TelegramOptions>(builder.Configuration);
builder.Services.AddProblemDetails();

var baseUrl = builder.Configuration["BaseUrl"] ?? throw new InvalidOperationException("BaseUrl configuration is missing or empty.");
var baseUrlAddress = new Uri(baseUrl);

builder.Services.AddHttpClient<ContactService>(c =>
{
    c.BaseAddress = baseUrlAddress;
});


// Named HttpClients for each Bitcoin exchange provider (pooled handlers, DNS refresh)
builder.Services.AddHttpClient("BlockchainInfo", c => c.BaseAddress = new Uri("https://blockchain.info"))
    .AddStandardResilienceHandler();
builder.Services.AddHttpClient("Bitstamp", c => c.BaseAddress = new Uri("https://www.bitstamp.net"))
    .AddStandardResilienceHandler();
builder.Services.AddHttpClient("CoinGecko", c =>
{
    c.BaseAddress = new Uri("https://api.coingecko.com");
    c.DefaultRequestHeaders.UserAgent.ParseAdd("FreedomBlaze/1.0");
}).AddStandardResilienceHandler();
builder.Services.AddHttpClient("Coinbase", c => c.BaseAddress = new Uri("https://api.coinbase.com"))
    .AddStandardResilienceHandler();
builder.Services.AddHttpClient("Gemini", c => c.BaseAddress = new Uri("https://api.gemini.com"))
    .AddStandardResilienceHandler();
builder.Services.AddHttpClient("Coingate", c => c.BaseAddress = new Uri("https://api.coingate.com"))
    .AddStandardResilienceHandler();
builder.Services.AddHttpClient("ExchangeRateApi", c => c.BaseAddress = new Uri("http://api.exchangeratesapi.io"))
    .AddStandardResilienceHandler();
builder.Services.AddHttpClient("ExchangeRateApiCom", c => c.BaseAddress = new Uri("https://v6.exchangerate-api.com"))
    .AddStandardResilienceHandler();
builder.Services.AddHttpClient("Telegram", c => c.BaseAddress = new Uri("https://api.telegram.org"))
    .AddStandardResilienceHandler();

builder.Services.AddSingleton<IBitcoinExchangeRateProvider, BlockchainInfoExchangeRateProvider>();
builder.Services.AddSingleton<IBitcoinExchangeRateProvider, BitstampExchangeRateProvider>();
builder.Services.AddSingleton<IBitcoinExchangeRateProvider, CoinGeckoExchangeRateProvider>();
builder.Services.AddSingleton<IBitcoinExchangeRateProvider, CoinbaseExchangeRateProvider>();
builder.Services.AddSingleton<IBitcoinExchangeRateProvider, GeminiExchangeRateProvider>();
builder.Services.AddSingleton<IBitcoinExchangeRateProvider, CoingateExchangeRateProvider>();
builder.Services.AddSingleton<IExchangeRateProvider, ExchangeRateProvider>();

// Currency exchange-rate providers — switch the active one via "CurrencyExchange:Provider".
builder.Services.Configure<CurrencyExchangeOptions>(builder.Configuration.GetSection(CurrencyExchangeOptions.Section));
builder.Services.AddKeyedSingleton<ICurrencyExchangeProvider, ExchangeRateApiProvider>(CurrencyExchangeProviderType.ExchangeRatesApiIo);
builder.Services.AddKeyedSingleton<ICurrencyExchangeProvider, ExchangeRateApiComProvider>(CurrencyExchangeProviderType.ExchangeRateApiCom);
builder.Services.AddSingleton<ICurrencyExchangeProvider, CurrencyExchangeRateProviders>();

builder.Services.AddScoped<CultureService>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AppState>();
builder.Services.AddSingleton<ThemeManager>();

builder.Services.AddMemoryCache();
builder.Services.AddResponseCompression(opts =>
{
    opts.EnableForHttps = true;
    opts.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    opts.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
});

// Real-time Bitcoin news via the OpenAI Responses API web-search tool.
builder.Services.Configure<OpenAiOptions>(builder.Configuration.GetSection(OpenAiOptions.Section));

// Register the official OpenAI SDK client. Per the openai-dotnet docs the OpenAIClient is the
// recommended entry point and is thread-safe, so it is registered as a singleton (one pooled
// HTTP connection set for the app). Feature clients (ResponsesClient, etc.) are derived from it.
// Only registered when a key exists; the news service degrades gracefully otherwise.
var openAiApiKey = builder.Configuration["OpenAI:ApiKey"];
if (string.IsNullOrWhiteSpace(openAiApiKey))
{
    openAiApiKey = builder.Configuration["ChatGptApiKey"]; // legacy fallback
}
if (!string.IsNullOrWhiteSpace(openAiApiKey))
{
    // A web-search news call on a reasoning model can take 60-90s, so raise the SDK's
    // network timeout well above the call duration to avoid spurious cancellations.
    builder.Services.AddSingleton(_ => new OpenAIClient(
        new ApiKeyCredential(openAiApiKey),
        new OpenAIClientOptions { NetworkTimeout = TimeSpan.FromMinutes(3) }));
}

// HttpClient dedicated to scraping article thumbnails (headers configured once, never mutated).
builder.Services.AddHttpClient(BitcoinNewsService.ImageScraperHttpClient, c =>
{
    c.Timeout = TimeSpan.FromSeconds(8);
    c.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
    c.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
    c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
});

// News persistence backend. Defaults to a local SQLite database; set "NewsStorage:Provider"
// (e.g. NewsStorage__Provider=LocalFile or =AzureBlob) to switch backends.
builder.Services.Configure<NewsStorageOptions>(builder.Configuration.GetSection(NewsStorageOptions.Section));
var newsStorageOptions = builder.Configuration.GetSection(NewsStorageOptions.Section).Get<NewsStorageOptions>()
    ?? new NewsStorageOptions();

switch (newsStorageOptions.Provider)
{
    case NewsStorageProvider.AzureBlob:
        var blobOptions = builder.Configuration.GetSection("BlobStorage").Get<BlobStorageOptions>();
        if (string.IsNullOrWhiteSpace(blobOptions?.AccountName))
        {
            throw new InvalidOperationException(
                "NewsStorage:Provider is 'AzureBlob' but 'BlobStorage:AccountName' is not configured.");
        }

        builder.Services.AddSingleton(_ =>
        {
            var endpoint = new Uri($"https://{blobOptions.AccountName}.blob.core.windows.net");
            return string.IsNullOrWhiteSpace(blobOptions.AccessKey)
                ? new BlobServiceClient(endpoint, new DefaultAzureCredential())
                : new BlobServiceClient(endpoint, new StorageSharedKeyCredential(blobOptions.AccountName, blobOptions.AccessKey));
        });
        builder.Services.AddSingleton<BlobStorageService>();
        builder.Services.AddSingleton<INewsStore, BlobNewsStore>();
        break;

    case NewsStorageProvider.LocalFile:
        builder.Services.AddSingleton<INewsStore, LocalFileNewsStore>();
        break;

    default: // Sqlite
        var dbPath = Path.IsPathRooted(newsStorageOptions.SqlitePath)
            ? newsStorageOptions.SqlitePath
            : Path.Combine(builder.Environment.ContentRootPath, newsStorageOptions.SqlitePath);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        builder.Services.AddDbContextFactory<FreedomBlazeDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));

        // Generic repository over any entity in the database (backed by the context factory, so it
        // is safe to use from any Blazor render mode).
        builder.Services.AddSingleton(typeof(IRepository<>), typeof(EfRepository<>));

        builder.Services.AddSingleton<INewsStore, SqliteNewsStore>();
        break;
}

builder.Services.AddSingleton<OpenAiNewsClient>();
builder.Services.AddScoped<BitcoinNewsService>();

// Server-side rendering (prerender / InteractiveServer) calls the service directly instead of
// making a loopback HTTP request to the app's own API.
builder.Services.AddScoped<IBitcoinNewsApi, ServerBitcoinNewsApi>();

builder.Services.AddControllers();

// Lightning payments via phoenixd. Only registered when a phoenixd host is configured, so the app
// (and its CI/dev runs) work fine without a payment backend. The donate UI is hidden when absent.
var phoenixdHost = builder.Configuration["PhoenixConfig:Host"];
var phoenixdEnabled = !string.IsNullOrWhiteSpace(phoenixdHost);
if (phoenixdEnabled)
{
    builder.Services.AddSignalR();
    builder.Services.ConfigurePhoenixdServices(builder.Configuration);
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

builder.Services.AddMudServices();

builder.Services.AddLocalization(options => options.ResourcesPath = "");

var supportedCultures = CurrencyModel.GetCurrencyList().Select(s => s.CultureName).ToArray();

builder.Services.AddHttpContextAccessor();

builder.AddServiceDefaults();

var app = builder.Build();

// Create the SQLite schema on first run. EnsureCreated suits a disposable local cache; switch to
// migrations if the news schema ever needs to evolve in place.
if (newsStorageOptions.Provider == NewsStorageProvider.Sqlite)
{
    await using var startupScope = app.Services.CreateAsyncScope();
    var dbFactory = startupScope.ServiceProvider.GetRequiredService<IDbContextFactory<FreedomBlazeDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
}

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseResponseCompression();

app.MapStaticAssets();

var localizationOptions = new RequestLocalizationOptions()
    .SetDefaultCulture(supportedCultures[0])
    .AddSupportedCultures(supportedCultures)
    .AddSupportedUICultures(supportedCultures);

app.UseRequestLocalization(localizationOptions);

app.UseRouting();

app.UseAntiforgery();

app.MapControllers();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(FreedomBlaze.Client._Imports).Assembly);

if (phoenixdEnabled)
{
    app.MapHub<PaymentHub>("/paymentHub");
}

app.Run();
