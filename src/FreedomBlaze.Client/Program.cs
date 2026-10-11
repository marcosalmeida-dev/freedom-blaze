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

// News requests read stored editions; paid generation runs in the server's background worker.
builder.Services.AddScoped<IBitcoinNewsApiService>(sp => new BitcoinNewsApiService(new HttpClient
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
    // Retry starts background work and polls its status; each HTTP request stays short.
    Timeout = TimeSpan.FromSeconds(30),
}));

builder.Services.AddMudServices();
builder.Services.AddScoped<HubConnectionBuilder>();

await builder.Build().RunAsync();
