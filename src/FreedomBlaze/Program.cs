using Azure.Identity;
using Azure.Storage;
using FreedomBlaze;
using FreedomBlaze.Client.Services;
using FreedomBlaze.Components;
using FreedomBlaze.Configuration;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;
using FreedomBlaze.ServiceDefaults;
using FreedomBlaze.Services;
using FreedomBlaze.WebClients;
using FreedomBlaze.WebClients.CurrencyExchanges;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Azure;
using MudBlazor.Services;
using Phoenixd.NET.Hubs;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IConfiguration>(provider => builder.Configuration);

var baseUrl = builder.Configuration["BaseUrl"];
if (string.IsNullOrEmpty(baseUrl))
{
    throw new InvalidOperationException("BaseUrl configuration is missing or empty.");
}
var baseUrlAddress = new Uri(baseUrl);

builder.Services.AddHttpClient<BitcoinNewsService>(c =>
{
    c.BaseAddress = baseUrlAddress;
});
builder.Services.AddHttpClient<ContactService>(c =>
{
    c.BaseAddress = baseUrlAddress;
});

var blobStorageOptions = builder.Configuration.GetSection("BlobStorage").Get<BlobStorageOptions>();
builder.Services.AddAzureClients(clientBuilder =>
{
    var blobUri = new Uri($"https://{blobStorageOptions?.AccountName}.blob.core.windows.net");
    if (!string.IsNullOrEmpty(blobStorageOptions?.AccessKey))
    {
        var credential = new StorageSharedKeyCredential(blobStorageOptions.AccountName, blobStorageOptions.AccessKey);
        clientBuilder.AddBlobServiceClient(blobUri, credential);
    }
    else
    {
        clientBuilder.AddBlobServiceClient(blobUri);
        clientBuilder.UseCredential(new DefaultAzureCredential());
    }

    var useKeyVault = builder.Configuration.GetValue<bool?>("UseKeyVault");
    if (useKeyVault.HasValue && useKeyVault == true)
    {
        clientBuilder.AddSecretClient(new Uri($"https://{builder.Configuration["KeyVaultName"]}.vault.azure.net/"));
    }
});

builder.Services.AddScoped<IExchangeRateProvider, ExchangeRateProvider>();
builder.Services.AddScoped<ICurrencyExchangeProvider, CurrencyExchangeRateProviders>();

builder.Services.AddSingleton<BlobStorageService>();

builder.Services.AddScoped<CultureService>();
builder.Services.AddScoped<ImageService>();
builder.Services.AddScoped<ChatGptService>();

builder.Services.AddScoped<AppState>();

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
}

//app.UseCors(builder =>
//    builder.AllowAnyOrigin()
//           .AllowAnyHeader()
//           .AllowAnyMethod());


app.UseStaticFiles();

var localizationOptions = new RequestLocalizationOptions()
    .SetDefaultCulture(supportedCultures[0]) 
    .AddSupportedCultures(supportedCultures)
    .AddSupportedUICultures(supportedCultures);

app.UseRequestLocalization(localizationOptions);

app.UseExceptionHandler(options => { });

app.UseRouting();

app.UseAntiforgery();

app.UseHttpsRedirection();

app.MapControllers();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(FreedomBlaze.Client._Imports).Assembly);

app.MapHub<PaymentHub>("/paymentHub");

app.Run();
