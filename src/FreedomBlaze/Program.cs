using System.ClientModel;
using FreedomBlaze;
using FreedomBlaze.Authentication;
using FreedomBlaze.Client.Interfaces;
using FreedomBlaze.Client.Services;
using FreedomBlaze.Clients;
using FreedomBlaze.Clients.BitcoinExchanges;
using FreedomBlaze.Clients.BitcoinTracking;
using FreedomBlaze.Clients.CurrencyExchanges;
using FreedomBlaze.Components;
using FreedomBlaze.Data;
using FreedomBlaze.Data.Repositories;
using FreedomBlaze.Helpers;
using FreedomBlaze.Extensions;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;
using FreedomBlaze.OpenApi;
using FreedomBlaze.Options;
using FreedomBlaze.ServiceDefaults;
using FreedomBlaze.Services;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using MudBlazor.Services;
using OpenAI;
using Phoenixd.NET;
using Phoenixd.NET.Hubs;
using Scalar.AspNetCore;

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

builder.Services.AddSingleton<IBitcoinExchangeRateClient, BlockchainInfoExchangeRateClient>();
builder.Services.AddSingleton<IBitcoinExchangeRateClient, BitstampExchangeRateClient>();
builder.Services.AddSingleton<IBitcoinExchangeRateClient, CoinGeckoExchangeRateClient>();
builder.Services.AddSingleton<IBitcoinExchangeRateClient, CoinbaseExchangeRateClient>();
builder.Services.AddSingleton<IBitcoinExchangeRateClient, GeminiExchangeRateClient>();
builder.Services.AddSingleton<IBitcoinExchangeRateClient, CoingateExchangeRateClient>();
builder.Services.AddSingleton<IExchangeRateService, ExchangeRateService>();

// Currency exchange-rate clients — switch the active one via "CurrencyExchange:Provider".
builder.Services.Configure<CurrencyExchangeOptions>(builder.Configuration.GetSection(CurrencyExchangeOptions.Section));
builder.Services.AddKeyedSingleton<ICurrencyExchangeRateClient, ExchangeRatesApiIoClient>(CurrencyExchangeClientType.ExchangeRatesApiIo);
builder.Services.AddKeyedSingleton<ICurrencyExchangeRateClient, ExchangeRateApiComClient>(CurrencyExchangeClientType.ExchangeRateApiCom);
builder.Services.AddSingleton<ICurrencyExchangeRateClient, CurrencyExchangeRateService>();

builder.Services.AddScoped<CultureService>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AppState>();
builder.Services.AddScoped<ThemeManager>();

builder.Services.AddMemoryCache();

// Public on-chain lookup: all calls are read-only and share a bounded cache/provider budget.
builder.Services.AddOptions<BitcoinTrackingOptions>()
    .Bind(builder.Configuration.GetSection(BitcoinTrackingOptions.Section))
    .Validate(options => Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
        && options.BaseUrl.EndsWith('/'), "BitcoinTracking:BaseUrl must be an HTTPS API URL ending in '/'.")
    .Validate(options => options.RequestTimeout > TimeSpan.Zero && options.RequestTimeout <= TimeSpan.FromMinutes(1),
        "BitcoinTracking:RequestTimeout must be positive and no longer than one minute.")
    .Validate(options => options.CacheDuration > TimeSpan.Zero && options.CacheDuration <= TimeSpan.FromSeconds(30),
        "BitcoinTracking:CacheDuration must be positive and no longer than 30 seconds.")
    .Validate(options => options.FailureCooldown > TimeSpan.Zero && options.FailureCooldown <= TimeSpan.FromMinutes(1),
        "BitcoinTracking:FailureCooldown must be positive and no longer than one minute.")
    .Validate(options => options.MaxCacheEntries > 0 && options.MaxProviderRequestsPerMinute > 0
        && options.MaxConcurrentRequests >= 2, "BitcoinTracking cache/request budgets must be positive; at least two requests may run concurrently.")
    .ValidateOnStart();
#pragma warning disable EXTEXP0001 // Required to remove Aspire's inherited retry handlers for this privacy-sensitive, budgeted client.
builder.Services.AddHttpClient(EsploraClient.HttpClientName, (provider, client) =>
{
    var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<BitcoinTrackingOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl);
    client.Timeout = options.RequestTimeout;
    client.DefaultRequestHeaders.UserAgent.ParseAdd("FreedomBlaze/1.0");
})
    // Addresses and transaction IDs must not appear in the default request-URI logs. No automatic
    // retries: every actual HTTP attempt is counted by the singleton provider budget.
    .RemoveAllLoggers()
    .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
builder.Services.AddSingleton(provider => new EsploraClient(
    provider.GetRequiredService<IHttpClientFactory>().CreateClient(EsploraClient.HttpClientName),
    provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<BitcoinTrackingOptions>>(),
    provider.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IBitcoinTrackingService, BitcoinTrackingService>();

builder.Services.AddOptions<BitcoinHistoricalPriceOptions>()
    .Bind(builder.Configuration.GetSection(BitcoinHistoricalPriceOptions.Section))
    .Validate(options => new[] { options.CandleBaseUrl, options.FxBaseUrl }.All(value =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment) && value.EndsWith('/')),
        "BitcoinHistoricalPrice provider URLs must use HTTPS and end in '/'.")
    .Validate(options => options.RequestTimeout > TimeSpan.Zero && options.RequestTimeout <= TimeSpan.FromMinutes(2)
        && options.CacheDuration > TimeSpan.Zero && options.CacheDuration <= TimeSpan.FromDays(7)
        && options.FailureCooldown > TimeSpan.Zero && options.FailureCooldown <= TimeSpan.FromMinutes(5),
        "BitcoinHistoricalPrice timeouts and cache/cooldown durations must be within their supported ranges.")
    .Validate(options => options.MaxCacheEntries > 0 && options.MaxProviderRequestsPerMinute > 0
        && options.MaxConcurrentRequests > 0 && options.MaxQueuedRequests > 0,
        "BitcoinHistoricalPrice cache and request budgets must be positive.")
    .ValidateOnStart();
#pragma warning disable EXTEXP0001
foreach (var clientName in new[] { BitcoinHistoricalPriceClient.CandleHttpClientName, BitcoinHistoricalPriceClient.FxHttpClientName })
{
    builder.Services.AddHttpClient(clientName, (provider, client) =>
    {
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<BitcoinHistoricalPriceOptions>>().Value;
        client.BaseAddress = new Uri(clientName == BitcoinHistoricalPriceClient.CandleHttpClientName
            ? options.CandleBaseUrl : options.FxBaseUrl);
        client.Timeout = options.RequestTimeout;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FreedomBlaze/1.0");
    }).RemoveAllLoggers().RemoveAllResilienceHandlers();
}
#pragma warning restore EXTEXP0001
builder.Services.AddSingleton(provider => new BitcoinHistoricalPriceClient(
    provider.GetRequiredService<IHttpClientFactory>().CreateClient(BitcoinHistoricalPriceClient.CandleHttpClientName),
    provider.GetRequiredService<IHttpClientFactory>().CreateClient(BitcoinHistoricalPriceClient.FxHttpClientName),
    provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<BitcoinHistoricalPriceOptions>>(),
    provider.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IBitcoinHistoricalPriceService, BitcoinHistoricalPriceService>();

builder.Services.AddResponseCompression(opts =>
{
    opts.EnableForHttps = true;
    opts.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    opts.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
});

// Real-time Bitcoin news via the OpenAI Responses API web-search tool.
var openAiSection = builder.Configuration.GetSection(OpenAiOptions.Section);
builder.Services.AddOptions<OpenAiOptions>()
    .Bind(openAiSection)
    .Validate(options => options.GenerationTimeout > TimeSpan.Zero, "OpenAI:GenerationTimeout must be positive.")
    .Validate(options => options.CacheDuration > TimeSpan.Zero, "OpenAI:CacheDuration must be positive.")
    .Validate(options => options.FailureCooldown > TimeSpan.Zero, "OpenAI:FailureCooldown must be positive.")
    .ValidateOnStart();

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
    // The service cancellation token bounds the full generation, including any SDK retries.
    // Do not subtract a fixed margin: a short configured timeout could become zero or negative.
    var generationTimeout = openAiSection.GetValue("GenerationTimeout", TimeSpan.FromMinutes(4));

    builder.Services.AddSingleton(_ => new OpenAIClient(
        new ApiKeyCredential(openAiApiKey),
        new OpenAIClientOptions
        {
            NetworkTimeout = generationTimeout,
            RetryPolicy = new OpenAiNewsRetryPolicy(),
        }));
}

// HttpClient dedicated to scraping article thumbnails (headers configured once, never mutated).
builder.Services.AddHttpClient(FreedomBlaze.Helpers.ArticleThumbnailHelper.HttpClientName, c =>
{
    c.Timeout = TimeSpan.FromSeconds(8);
    c.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
    c.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
    c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
});

// News persistence: SQL Server (the only backend). Requires the "FreedomBlazeDb" connection string.
var connectionString = builder.Configuration.GetConnectionString("FreedomBlazeDb")
    ?? throw new InvalidOperationException("Connection string 'FreedomBlazeDb' is not configured.");

// Pooled context factory (safe for any Blazor render mode) over SQL Server. The retrying execution
// strategy transparently handles transient faults — the norm for Azure SQL and a best practice for
// SQL Server generally.
builder.Services.AddDbContextFactory<FreedomBlazeDbContext>(options =>
    options.UseSqlServer(connectionString, sql =>
    {
        sql.EnableRetryOnFailure(
            maxRetryCount: 5,
            maxRetryDelay: TimeSpan.FromSeconds(10),
            errorNumbersToAdd: null);
        sql.CommandTimeout(30);
    }));

// Generic repository over any entity in the database (backed by the context factory, so it is safe
// to use from any Blazor render mode).
builder.Services.AddSingleton(typeof(IRepository<>), typeof(EfRepository<>));

builder.Services.AddSingleton<INewsStore, NewsStore>();
builder.Services.AddSingleton<IArticleThumbnailHelper, ArticleThumbnailHelper>();

// Donations: persist every tip and announce success/failure on Telegram. Independent of phoenixd so
// the services always resolve (the donate UI itself is only shown when a node is configured).
builder.Services.AddSingleton<ITelegramNotifier, TelegramNotifier>();
builder.Services.AddSingleton<IDonationService, DonationService>();

builder.Services.AddSingleton<OpenAiNewsClient>();
builder.Services.AddScoped<BitcoinNewsService>();

// Server-side rendering (prerender / InteractiveServer) resolves the news service directly, so it
// runs in-process with no loopback HTTP request to the app's own API.
builder.Services.AddScoped<IBitcoinNewsApiService>(sp => sp.GetRequiredService<BitcoinNewsService>());

builder.Services.AddControllers();

builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info = new OpenApiInfo
        {
            Title = "Freedom Blaze API",
            Version = "v1",
            Description =
                "REST API for Bitcoin/fiat conversion, Bitcoin news, Lightning payments, and platform administration. " +
                "Most endpoints require an API key passed via the `X-Api-Key` header.",
            Contact = new OpenApiContact
            {
                Name = "Freedom Blaze",
                Url = new Uri(baseUrl),
            },
        };
        return Task.CompletedTask;
    });
    options.AddDocumentTransformer<ApiKeySecuritySchemeTransformer>();
    options.AddOperationTransformer<ApiKeySecurityOperationTransformer>();
});

// Public, key-authenticated conversion API: API-key + conversion services, the "ApiKey"
// authentication scheme, an authorization policy, and a per-key rate limiter.
builder.Services.AddPublicApi(builder.Configuration);

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

// Apply any pending code-first migrations on startup so the schema is created/evolved in place.
// Fine for a single-instance deployment; for multi-instance rollouts, apply migrations as a
// separate deploy step instead to avoid concurrent migration races.
await using (var startupScope = app.Services.CreateAsyncScope())
{
    var dbFactory = startupScope.ServiceProvider.GetRequiredService<IDbContextFactory<FreedomBlazeDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.MigrateAsync();

    // Seed the first master key if none exists. The plaintext is shown exactly once, here, so capture
    // it from the logs on first run — it cannot be recovered later. Use it to mint further keys via
    // the /api/admin/api-keys endpoints.
    var apiKeyService = startupScope.ServiceProvider.GetRequiredService<IApiKeyService>();
    var masterKey = await apiKeyService.EnsureMasterKeyAsync(CancellationToken.None);
    if (masterKey is not null)
    {
        app.Logger.LogWarning(
            "Generated master API key (shown ONCE — store it now): {MasterKey}", masterKey.Key);
    }
}

app.MapDefaultEndpoints();

app.MapOpenApi();

app.MapScalarApiReference(options =>
{
    options.Title = "Freedom Blaze API";
    options.Theme = ScalarTheme.Default;
    options.AddPreferredSecuritySchemes(ApiKeyDefaults.Scheme);
    options.DefaultHttpClient = new(ScalarTarget.CSharp, ScalarClient.HttpClient);
});

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
app.UseLanguageCulture();

app.UseRouting();

// Authenticate/authorize before the rate limiter so the per-key partition is known when a request
// reaches the conversion API (the [Authorize] attribute drives authentication for the "ApiKey" scheme).
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

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
