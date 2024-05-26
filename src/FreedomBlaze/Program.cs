using Azure.Identity;
using FreedomBlaze.Components;
using FreedomBlaze.Infrastructure;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Logging;
using FreedomBlaze.Models;
using FreedomBlaze.WebClients.BitcoinExchanges;
using FreedomBlaze.WebClients.CurrencyExchanges;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();

builder.Services.AddSingleton<IConfiguration>(provider => builder.Configuration);
builder.Services.AddMemoryCache();

builder.Services.AddSingleton<IExchangeRateProvider, ExchangeRateProvider>();
builder.Services.AddSingleton<ICurrencyExchangeProvider, CurrencyExchangeRateProviders>();

builder.Services.AddExceptionHandler<CustomExceptionHandler>();

builder.Services.AddControllers();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

builder.Services.AddMudServices();

var supportedCultures = CurrencyModel.Currencies.Select(s => s.CultureName).ToArray();
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.SetDefaultCulture(supportedCultures[0])
        .AddSupportedCultures(supportedCultures)
        .AddSupportedUICultures(supportedCultures);
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddLocalization();

builder.Services.AddApplicationInsightsTelemetry();

Logger.InitializeDefaults(Path.Combine(AppContext.BaseDirectory, "Logs", "Logs.txt"));
Logger.LogSoftwareStarted("Freedom Blaze App");

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
}
if (builder.Environment.IsProduction())
{
    builder.Configuration.AddAzureKeyVault(
        new Uri($"https://{builder.Configuration["KeyVaultName"]}.vault.azure.net/"),
        new DefaultAzureCredential());
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

var localizationOptions = new RequestLocalizationOptions()
    .SetDefaultCulture(supportedCultures[0]) 
    .AddSupportedCultures(supportedCultures)
    .AddSupportedUICultures(supportedCultures);

app.UseRequestLocalization(localizationOptions);

app.UseExceptionHandler(options => { });

app.MapControllers();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(FreedomBlaze.Client._Imports).Assembly);

app.Run();
