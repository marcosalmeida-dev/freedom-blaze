using System.Globalization;
using System.Net;
using System.Text.Json;
using FreedomBlaze.Services.Webhooks;

namespace FreedomBlaze.Tests;

/// <summary>A merchant's webhook endpoint as the dispatcher's HTTP client reaches it: records each delivery and answers it.</summary>
internal sealed class FakeWebhookReceiver : HttpMessageHandler, IHttpClientFactory
{
    public List<ReceivedWebhook> Received { get; } = [];

    /// <summary>Answers each delivery; 200 by default. May throw, like an unreachable host.</summary>
    public Func<ReceivedWebhook, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string Header(string name) => request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : string.Empty;

        var received = new ReceivedWebhook(request.Method.Method, request.RequestUri!,
            Header(WebhookSignature.IdHeader), Header(WebhookSignature.TimestampHeader), Header(WebhookSignature.SignatureHeader),
            request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
        Received.Add(received);
        return Respond(received);
    }
}

internal sealed record ReceivedWebhook(string Method, Uri Url, string Id, string Timestamp, string Signature, string Body)
{
    public bool IsSignedWith(string secret) =>
        WebhookSignature.Verify(secret, Id, long.Parse(Timestamp, CultureInfo.InvariantCulture), Body, Signature);

    public JsonElement Json => JsonDocument.Parse(Body).RootElement;
}
