using System.Globalization;
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
//builder.Services.AddScoped<BitcoinNewsService>();
//builder.Services.AddScoped<ContactService>();

builder.Services.AddMudServices();
builder.Services.AddScoped<HubConnectionBuilder>();

await builder.Build().RunAsync();
