using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using NBitcoin.Secp256k1;

namespace FreedomBlaze.Tests;

/// <summary>
/// An in-memory phoenixd: issues invoices really signed with <see cref="NodeKey"/>, remembers their
/// preimages, and answers the calls the gateway makes, checking the password like the real one (only the
/// full-access password gets past <c>lnurlauth</c>'s door). Every request is recorded.
/// </summary>
internal sealed class FakePhoenixd(string password, bool fullAccess = false)
{
    private readonly Dictionary<string, Payment> _payments = new(StringComparer.OrdinalIgnoreCase);

    public ECPrivKey NodeKey { get; set; } = Bolt11TestInvoices.NewKey();

    public string NodeId => Bolt11TestInvoices.NodeId(NodeKey);

    public string Chain { get; set; } = "mainnet";

    /// <summary>The timestamp written into issued invoices.</summary>
    public DateTimeOffset Now { get; set; } = new(2026, 10, 12, 15, 0, 0, TimeSpan.Zero);

    public List<(string Method, string Path, string Body)> Requests { get; } = [];

    /// <summary>Answers a request path instead of the node when it returns a response; may also throw.</summary>
    public Func<string, HttpResponseMessage?>? Override { get; set; }

    public Payment this[string paymentHash] => _payments[paymentHash];

    public sealed class Payment
    {
        public required string Hash { get; init; }
        public required string Preimage { get; init; }
        public required long AmountSat { get; init; }
        public required string Bolt11 { get; init; }
        public string? ExternalId { get; init; }
        public string? Description { get; init; }
        public bool IsPaid { get; set; }
        public bool IsExpired { get; set; }
        public long ReceivedSat { get; set; }
        public long FeesMsat { get; set; }
        public long? CompletedAt { get; set; }
    }

    public void Pay(string paymentHash, long feesMsat = 21_000_000, long completedAt = 1791853300000)
    {
        var payment = _payments[paymentHash];
        payment.IsPaid = true;
        payment.ReceivedSat = payment.AmountSat;
        payment.FeesMsat = feesMsat;
        payment.CompletedAt = completedAt;
    }

    public void Expire(string paymentHash) => _payments[paymentHash].IsExpired = true;

    public async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method.Method, path, body));

        if (Override?.Invoke(path) is { } overridden)
            return overridden;

        if (!Authenticated(request))
            return Text(HttpStatusCode.Unauthorized, "Invalid authentication (use basic auth with the http password set in phoenix.conf)");

        if (path.EndsWith("/getinfo", StringComparison.Ordinal))
            return Json(new { nodeId = NodeId, channels = Array.Empty<object>(), chain = Chain, blockHeight = 971166, version = "0.9.2" });

        if (path.EndsWith("/createinvoice", StringComparison.Ordinal))
            return Json(Issue(HttpUtility.ParseQueryString(body)));

        if (path.EndsWith("/lnurlauth", StringComparison.Ordinal))
            return fullAccess
                ? Text(HttpStatusCode.BadRequest, "Missing parameter lnurl")
                : Text(HttpStatusCode.Unauthorized, "Invalid authentication (use basic auth with the http password set in phoenix.conf)");

        const string incoming = "/payments/incoming/";
        var at = path.IndexOf(incoming, StringComparison.Ordinal);
        if (at >= 0)
        {
            return _payments.TryGetValue(path[(at + incoming.Length)..], out var payment)
                ? Json(new
                {
                    type = "incoming_payment", subType = "lightning", paymentHash = payment.Hash, preimage = payment.Preimage,
                    externalId = payment.ExternalId, description = payment.Description, invoice = payment.Bolt11,
                    isPaid = payment.IsPaid, isExpired = payment.IsExpired, requestedSat = payment.AmountSat,
                    receivedSat = payment.ReceivedSat, fees = payment.FeesMsat, completedAt = payment.CompletedAt,
                    createdAt = Now.ToUnixTimeMilliseconds(),
                })
                : Text(HttpStatusCode.NotFound, "Not found");
        }

        return Text(HttpStatusCode.NotFound, "Unknown endpoint (check api doc)");
    }

    private object Issue(System.Collections.Specialized.NameValueCollection form)
    {
        var amountSat = long.Parse(form["amountSat"]!);
        var preimage = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(preimage));
        var bolt11 = Bolt11TestInvoices.Create(NodeKey, hash, amountSat * 1000, form["description"] ?? string.Empty,
            long.Parse(form["expirySeconds"] ?? "3600"), Now);

        _payments[hash] = new Payment
        {
            Hash = hash,
            Preimage = Convert.ToHexStringLower(preimage),
            AmountSat = amountSat,
            Bolt11 = bolt11,
            ExternalId = form["externalId"],
            Description = form["description"],
        };
        return new { amountSat, paymentHash = hash, serialized = bolt11 };
    }

    private bool Authenticated(HttpRequestMessage request)
    {
        if (request.Headers.Authorization is not { Scheme: "Basic", Parameter: { } parameter })
            return false;

        var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(parameter));
        return credentials[(credentials.IndexOf(':') + 1)..] == password;
    }

    public static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
    };

    public static HttpResponseMessage Text(HttpStatusCode status, string text) => new(status)
    {
        Content = new StringContent(text, Encoding.UTF8, "text/plain")
    };
}

/// <summary>
/// Routes each request to the fake node registered for its host, as the gateway's HTTP client would reach
/// it. A host with no node behaves like an unreachable one.
/// </summary>
internal sealed class FakeLightningNetwork : HttpMessageHandler, IHttpClientFactory
{
    private readonly Dictionary<string, FakePhoenixd> _nodes = new(StringComparer.OrdinalIgnoreCase);

    public FakePhoenixd Add(string url, FakePhoenixd node)
    {
        _nodes[new Uri(url).Authority] = node;
        return node;
    }

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        _nodes.TryGetValue(request.RequestUri!.Authority, out var node)
            ? node.HandleAsync(request, cancellationToken)
            : throw new HttpRequestException($"No such host is known. ({request.RequestUri.Host}:{request.RequestUri.Port})");
}
