using System.Globalization;
using FreedomBlaze.Client.Interfaces;
using FreedomBlaze.Client.Services;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddLocalization(options => options.ResourcesPath = "");

var supportedCultures = new[] { "en-US", "es-ES", "en-GB", "en-AU", "ja-JP", "af-ZA", "es-AR", "pt-BR" };

// Set the default culture
var defaultCulture = supportedCultures[0];
CultureInfo.DefaultThreadCurrentCulture = new CultureInfo(defaultCulture);
CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo(defaultCulture);

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// In WebAssembly the news API is reached over HTTP (browser origin). The longer timeout covers a
// cold generation (web search on a reasoning model) that can take ~80s, just over the default 100s.
builder.Services.AddScoped<IBitcoinNewsApi>(sp => new BitcoinNewsApiClient(new HttpClient
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
    Timeout = TimeSpan.FromMinutes(3),
}));

builder.Services.AddMudServices();
builder.Services.AddScoped<HubConnectionBuilder>();

await builder.Build().RunAsync();
