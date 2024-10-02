using System.Net.Http.Headers;
using System.Text;
using Azure.Identity;
using FreedomBlaze.Components;
using FreedomBlaze.Infrastructure;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Logging;
using FreedomBlaze.Models;
using FreedomBlaze.Services;
using FreedomBlaze.WebClients.BitcoinExchanges;
using FreedomBlaze.WebClients.CurrencyExchanges;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.DataProtection;
using MudBlazor.Services;
using Phoenixd.NET;
using Phoenixd.NET.Hubs;
using Phoenixd.NET.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
// For Blazor Client: Use AddScoped to inject the HttpClient with a dynamically set base address.
builder.Services.AddScoped(sp =>
{
    //var navigationManager = sp.GetRequiredService<NavigationManager>();
    return new HttpClient
    {
        BaseAddress = new Uri(builder.Configuration["BaseUrl"]) 
    };
});

builder.Services.AddSingleton<IConfiguration>(provider => builder.Configuration);
builder.Services.AddMemoryCache();

builder.Services.AddSingleton<IExchangeRateProvider, ExchangeRateProvider>();
builder.Services.AddSingleton<ICurrencyExchangeProvider, CurrencyExchangeRateProviders>();

builder.Services.AddScoped<CultureService>();

builder.Services.AddExceptionHandler<CustomExceptionHandler>();

builder.Services.AddControllers();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

builder.Services.AddMudServices();

builder.Services.AddLocalization(options => options.ResourcesPath = "");

var supportedCultures = CurrencyModel.BuildCurrencyList().Select(s => s.CultureName).ToArray();
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.SetDefaultCulture(supportedCultures[0])
           .AddSupportedCultures(supportedCultures)
           .AddSupportedUICultures(supportedCultures);
});

builder.Services.AddHttpContextAccessor();

builder.Services.AddApplicationInsightsTelemetry();

Logger.InitializeDefaults(Path.Combine(AppContext.BaseDirectory, "Logs", "Logs.txt"));
Logger.LogSoftwareStarted("Freedom Blaze App");

builder.Services.ConfigurePhoenixdServices(builder.Configuration);

var app = builder.Build();

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
