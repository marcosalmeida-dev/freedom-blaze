using FreedomBlaze;
using FreedomBlaze.Client.Services;
using FreedomBlaze.Components;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;
using FreedomBlaze.Options;
using FreedomBlaze.ServiceDefaults;
using FreedomBlaze.Services;
using FreedomBlaze.WebClients;
using FreedomBlaze.WebClients.BitcoinExchanges;
using FreedomBlaze.WebClients.CurrencyExchanges;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;
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

builder.Services.AddControllers();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

builder.Services.AddMudServices();

builder.Services.AddLocalization(options => options.ResourcesPath = "");

var supportedCultures = CurrencyModel.GetCurrencyList().Select(s => s.CultureName).ToArray();

builder.Services.AddHttpContextAccessor();

builder.AddServiceDefaults();

var app = builder.Build();

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

app.MapHub<PaymentHub>("/paymentHub");

app.Run();
