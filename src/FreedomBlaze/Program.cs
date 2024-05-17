using FreedomBlaze.Components;
using FreedomBlaze.Infrastructure;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Logging;
using FreedomBlaze.Models;
using FreedomBlaze.WebClients.BitcoinExchanges;
using FreedomBlaze.WebClients.CurrencyExchanges;
using FreedomBlaze.WebClients.CurrencyExchanges.ExchangeRateApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.JSInterop;
using MudBlazor.Services;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
builder.Services.AddScoped<HttpClient>();

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

Logger.InitializeDefaults(Path.Combine(AppContext.BaseDirectory, "Logs", "Logs.txt"));
Logger.LogSoftwareStarted("DCA Manager");

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
