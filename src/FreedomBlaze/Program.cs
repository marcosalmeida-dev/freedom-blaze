using Azure.Identity;
using Azure.Storage.Blobs;
using FreedomBlaze.Client.Services;
using FreedomBlaze.Components;
using FreedomBlaze.Configuration;
using FreedomBlaze.Infrastructure;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;
using FreedomBlaze.ServiceDefaults;
using FreedomBlaze.Services;
using FreedomBlaze.WebClients;
using FreedomBlaze.WebClients.CurrencyExchanges;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
using Phoenixd.NET.Hubs;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IConfiguration>(provider => builder.Configuration);

var baseUrlAddress = new Uri(builder.Configuration["BaseUrl"]);
builder.Services.AddHttpClient<BitcoinNewsService>(c =>
{
    c.BaseAddress = baseUrlAddress;
});
builder.Services.AddHttpClient<ContactService>(c =>
{
    c.BaseAddress = baseUrlAddress;
});

builder.Services.Configure<BlobStorageOptions>(
    builder.Configuration.GetSection("BlobStorage"));
// Register BlobServiceClient using DefaultAzureCredential
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<BlobStorageOptions>>().Value;
    var blobUri = new Uri($"https://{options.AccountName}.blob.core.windows.net");
    return new BlobServiceClient(blobUri, new DefaultAzureCredential());
});
builder.Services.AddSingleton<BlobStorageService>();

builder.Services.AddScoped(provider =>
{
    var httpClient = provider.GetRequiredService<HttpClient>();
    var blobStorageService = provider.GetRequiredService<BlobStorageService>();
    var imageService = provider.GetRequiredService<ImageService>();
    var apiKey = builder.Configuration["ChatGptApiKey"];
    return new ChatGptService(httpClient, blobStorageService, imageService, apiKey);
});

builder.Services.AddScoped<IExchangeRateProvider, ExchangeRateProvider>();
builder.Services.AddScoped<ICurrencyExchangeProvider, CurrencyExchangeRateProviders>();

builder.Services.AddScoped<CultureService>();
builder.Services.AddScoped<ImageService>();

builder.Services.AddExceptionHandler<CustomExceptionHandler>();

builder.Services.AddMemoryCache();

builder.Services.AddControllers();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

builder.Services.AddMudServices();

builder.Services.AddLocalization(options => options.ResourcesPath = "");

var supportedCultures = CurrencyModel.GetCurrencyList().Select(s => s.CultureName).ToArray();
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.SetDefaultCulture(supportedCultures[0])
           .AddSupportedCultures(supportedCultures)
           .AddSupportedUICultures(supportedCultures);
});

builder.Services.AddHttpContextAccessor();

builder.Services.AddApplicationInsightsTelemetry();

builder.AddServiceDefaults();

//builder.Services.ConfigurePhoenixdServices(builder.Configuration);

//Logger.InitializeDefaults(Path.Combine(AppContext.BaseDirectory, "Logs", "Logs.txt"));
//Logger.LogSoftwareStarted("Freedom Blaze App");

var app = builder.Build();

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    //app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();

    var useKeyVault = builder.Configuration.GetValue<bool?>("UseKeyVault");
    if (useKeyVault.HasValue && useKeyVault == true)
    {
        builder.Configuration.AddAzureKeyVault(
            new Uri($"https://{builder.Configuration["KeyVaultName"]}.vault.azure.net/"),
            new DefaultAzureCredential());
    }
}

app.UseCors(builder =>
    builder.AllowAnyOrigin()
           .AllowAnyHeader()
           .AllowAnyMethod());

app.UseHttpsRedirection();

app.UseStaticFiles();

var localizationOptions = new RequestLocalizationOptions()
    .SetDefaultCulture(supportedCultures[0]) 
    .AddSupportedCultures(supportedCultures)
    .AddSupportedUICultures(supportedCultures);

app.UseRequestLocalization(localizationOptions);

app.UseExceptionHandler(options => { });

app.UseRouting();
app.UseAntiforgery();

app.MapControllers();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(FreedomBlaze.Client._Imports).Assembly);

app.MapHub<PaymentHub>("/paymentHub");

app.Run();
