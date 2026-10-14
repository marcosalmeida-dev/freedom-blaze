using System.Globalization;
using System.Net;
using System.Text.Json;
using Phoenixd.NET.Json;
using Phoenixd.NET.Models;

namespace FreedomBlaze.Services.Lightning;

/// <summary>
/// A phoenixd node, reached only through the calls invoicing needs: issue an invoice, look one up and
/// describe the node. None of them can move funds, whatever password the node was given. The client's
/// base address ends in <c>/</c> and every path here is relative, so a node behind a path prefix works.
/// </summary>
internal sealed class PhoenixdBackend(HttpClient http) : ILightningBackend
{
    private const int MaxErrorBodyLength = 200;

    public async Task<BackendInvoice> CreateInvoiceAsync(BackendInvoiceRequest request, CancellationToken cancellationToken)
    {
        List<KeyValuePair<string, string>> fields =
        [
            new("amountSat", request.AmountSat.ToString(CultureInfo.InvariantCulture)),
            new("description", request.Description),
            new("externalId", request.ExternalId),
            new("expirySeconds", request.ExpirySeconds.ToString(CultureInfo.InvariantCulture)),
        ];
        if (request.WebhookUrl is { } webhookUrl)
            fields.Add(new("webhookUrl", webhookUrl));

        using var content = new FormUrlEncodedContent(fields);
        using var response = await SendAsync(token => http.PostAsync("createinvoice", content, token), "createinvoice", cancellationToken);
        var invoice = await ReadAsync<Invoice>(response, "createinvoice", cancellationToken);

        return string.IsNullOrEmpty(invoice.PaymentHash) || string.IsNullOrEmpty(invoice.Serialized)
            ? throw new LightningBackendException("The node answered createinvoice without an invoice.")
            : new BackendInvoice(invoice.PaymentHash.ToLowerInvariant(), invoice.Serialized);
    }

    public async Task<BackendPayment?> FindIncomingPaymentAsync(string paymentHash, CancellationToken cancellationToken)
    {
        var path = "payments/incoming/" + Uri.EscapeDataString(paymentHash);
        using var response = await SendAsync(token => http.GetAsync(path, token), "payments/incoming", cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
            return null;

        var payment = await ReadAsync<PaymentInfo>(response, "payments/incoming", cancellationToken);
        return new BackendPayment(
            payment.IsPaid,
            payment.IsExpired,
            payment.RequestedSat,
            payment.ReceivedSat,
            payment.Fees, // phoenixd reports incoming fees in millisatoshis
            payment.Preimage,
            payment.CompletedAt is { } completedAt ? DateTimeOffset.FromUnixTimeMilliseconds(completedAt) : null);
    }

    public async Task<BackendNodeInfo> GetInfoAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(token => http.GetAsync("getinfo", token), "getinfo", cancellationToken);
        var info = await ReadAsync<NodeInfo>(response, "getinfo", cancellationToken);

        return string.IsNullOrEmpty(info.NodeId)
            ? throw new LightningBackendException("The node did not report its id. Is this a phoenixd?")
            : new BackendNodeInfo(info.NodeId.ToLowerInvariant(), info.Chain, info.Version, info.Channels.Count);
    }

    /// <summary>
    /// Asks <c>lnurlauth</c>, a full-access-only call, without an lnurl. The full-access password gets past
    /// authentication and is refused for the missing parameter (400); the limited-access one is refused at
    /// the door (401). Nothing is signed or sent either way.
    /// </summary>
    public async Task<bool> CanSpendAsync(CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent([]);
        using var response = await SendAsync(token => http.PostAsync("lnurlauth", content, token), "lnurlauth", cancellationToken);

        return response.StatusCode switch
        {
            HttpStatusCode.BadRequest => true,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound => false,
            _ => throw await FailureAsync(response, "lnurlauth", cancellationToken),
        };
    }

    private static async Task<HttpResponseMessage> SendAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send, string operation, CancellationToken cancellationToken)
    {
        try
        {
            return await send(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new LightningBackendException($"The node could not be reached ({operation}): {ex.Message}", innerException: ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LightningBackendException($"The node did not answer {operation} in time.", innerException: ex);
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw await FailureAsync(response, operation, cancellationToken);

        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<T>(body, PhoenixdJson.Default, cancellationToken)
                ?? throw new LightningBackendException($"The node answered {operation} with nothing.");
        }
        catch (JsonException ex)
        {
            throw new LightningBackendException($"The node's answer to {operation} is not phoenixd's.", innerException: ex);
        }
    }

    private static async Task<LightningBackendException> FailureAsync(
        HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        var body = (await response.Content.ReadAsStringAsync(cancellationToken)).ReplaceLineEndings(" ").Trim();
        if (body.Length > MaxErrorBodyLength)
            body = body[..MaxErrorBodyLength] + "…";

        var status = (int)response.StatusCode;
        return new LightningBackendException(
            body.Length > 0 ? $"The node refused {operation}: HTTP {status} {body}" : $"The node refused {operation}: HTTP {status}",
            response.StatusCode);
    }
}
