using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Phoenixd.NET;
using Phoenixd.NET.Interfaces;

namespace FreedomBlaze.Tests;

/// <summary>
/// The real phoenixd.NET clients over a stubbed HTTP handler that records every request the node would
/// have received. <see cref="Respond"/> answers by request path and can be swapped mid-test.
/// </summary>
internal sealed class PhoenixdStub : IDisposable
{
    private readonly ServiceProvider _provider;

    public PhoenixdStub(Func<string, HttpResponseMessage>? respond = null)
    {
        Respond = respond ?? (_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        services.ConfigureHttpClientDefaults(client => client.ConfigurePrimaryHttpMessageHandler(
            () => new RecordingHandler(this)));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PhoenixConfig:Host"] = "https://phoenixd.example",
            ["PhoenixConfig:Token"] = "test-password"
        }).Build();
        services.ConfigurePhoenixdServices(configuration);

        _provider = services.BuildServiceProvider();
        Payments = _provider.GetRequiredService<IPaymentService>();
        Node = _provider.GetRequiredService<INodeService>();
    }

    public List<(string Path, string Body)> Requests { get; } = [];

    public Func<string, HttpResponseMessage> Respond { get; set; }

    public IPaymentService Payments { get; }

    public INodeService Node { get; }

    public static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    public void Dispose() => _provider.Dispose();

    private sealed class RecordingHandler(PhoenixdStub stub) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            stub.Requests.Add((path, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return stub.Respond(path);
        }
    }
}
