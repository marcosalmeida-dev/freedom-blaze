using Microsoft.AspNetCore.Mvc;
using Phoenixd.NET.Exceptions;
using Phoenixd.NET.Interfaces;
using Phoenixd.NET.Models;

namespace FreedomBlaze.Controllers;

/// <summary>
/// REST surface over the phoenixd node/payment services for external integrators. The Blazor donate
/// UI talks to the services in-process and does not depend on this controller.
/// <para>
/// The phoenixd services are only registered when a phoenixd host is configured, so the services are
/// resolved optionally here: when payments are not configured every endpoint returns
/// <c>503 Service Unavailable</c> instead of failing to construct the controller.
/// </para>
/// </summary>
[ApiController]
[Route("api/payment-manager")]
[Tags("Lightning Payments")]
[Produces("application/json")]
public class PaymentManagerController(
    ILogger<PaymentManagerController> logger,
    IPaymentService? payments = null,
    INodeService? node = null) : ControllerBase
{
    private bool Enabled => payments is not null && node is not null;

    /// <summary>Returns whether the phoenixd Lightning node is configured and available.</summary>
    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetStatus() => Ok(new { enabled = Enabled });

    // --- Node / channels ---------------------------------------------------------------------

    /// <summary>Returns information about the phoenixd node (pubkey, version, channels count).</summary>
    [HttpGet("node-info")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> GetNodeInfo(CancellationToken ct) =>
        ExecuteAsync(() => node!.GetNodeInfo(ct));

    /// <summary>Returns the node's current on-chain and Lightning balance in satoshis.</summary>
    [HttpGet("balance")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> GetBalance(CancellationToken ct) =>
        ExecuteAsync(() => node!.GetBalance(ct));

    /// <summary>Returns all open Lightning channels.</summary>
    [HttpGet("channels")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> ListChannels(CancellationToken ct) =>
        ExecuteAsync(() => node!.ListChannels(ct));

    /// <summary>Estimates the fees required to acquire inbound liquidity for the given amount.</summary>
    [HttpGet("estimate-liquidity-fees")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> EstimateLiquidityFees([FromQuery] long amountSat, CancellationToken ct) =>
        ExecuteAsync(() => node!.EstimateLiquidityFees(amountSat, ct));

    /// <summary>Initiates cooperative closure of a channel and sends the funds to the specified on-chain address.</summary>
    [HttpPost("close-channel")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> CloseChannel([FromQuery] string channelId, [FromQuery] string address, [FromQuery] int feerateSatByte, CancellationToken ct) =>
        ExecuteAsync(() => node!.CloseChannel(channelId, address, feerateSatByte, ct));

    // --- Receiving ---------------------------------------------------------------------------

    /// <summary>Creates a BOLT11 Lightning invoice for the specified amount.</summary>
    [HttpPost("receive-payment")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> ReceiveLightningPayment([FromBody] ReceiveLightningPaymentRequest request, CancellationToken ct) =>
        ExecuteAsync(() => payments!.ReceiveLightningPaymentAsync(
            request.Description, request.AmountSat, request.ExternalId,
            request.DescriptionHash, request.ExpirySeconds, request.WebhookUrl, ct));

    /// <summary>Returns the node's Lightning Address (user@domain style).</summary>
    [HttpGet("lightning-address")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> GetLightningAddress(CancellationToken ct) =>
        ExecuteAsync(() => payments!.GetLnAddressAsync(ct));

    /// <summary>Returns the node's BOLT12 offer string for reusable payment requests.</summary>
    [HttpGet("offer")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> GetOffer(CancellationToken ct) =>
        ExecuteAsync(() => payments!.GetOfferAsync(ct));

    // --- Sending -----------------------------------------------------------------------------

    /// <summary>Pays a BOLT11 invoice. Pass <c>amountSat</c> only for zero-amount invoices.</summary>
    [HttpPost("send-invoice")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> SendLightningInvoice([FromQuery] long amountSat, [FromQuery] string invoice, CancellationToken ct) =>
        ExecuteAsync(() => payments!.SendLightningInvoice(amountSat, invoice, ct));

    /// <summary>Sends bitcoin to an on-chain address at the specified feerate.</summary>
    [HttpPost("send-onchain-payment")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> SendOnchainPayment([FromQuery] long amountSat, [FromQuery] string address, [FromQuery] int feerateSatByte, CancellationToken ct) =>
        ExecuteAsync(() => payments!.SendOnchainPayment(amountSat, address, feerateSatByte, ct));

    // --- Decoding / history ------------------------------------------------------------------

    /// <summary>Decodes a BOLT11 invoice string and returns its parsed fields.</summary>
    [HttpPost("decode-invoice")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> DecodeInvoice([FromQuery] string invoice, CancellationToken ct) =>
        ExecuteAsync(() => payments!.DecodeInvoiceAsync(invoice, ct));

    /// <summary>Looks up a specific incoming payment by its payment hash.</summary>
    [HttpGet("incoming-payment")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> GetIncomingPayment([FromQuery] string paymentHash, CancellationToken ct) =>
        ExecuteAsync(() => payments!.GetIncomingPayment(paymentHash, ct));

    /// <summary>Looks up a specific outgoing payment by its payment ID.</summary>
    [HttpGet("outgoing-payment")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> GetOutgoingPayment([FromQuery] string paymentId, CancellationToken ct) =>
        ExecuteAsync(() => payments!.GetOutgoingPayment(paymentId, ct));

    /// <summary>Lists all incoming payments associated with the given external ID.</summary>
    [HttpGet("incoming-payments")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> ListIncomingPayments([FromQuery] string externalId, CancellationToken ct) =>
        ExecuteAsync(() => payments!.ListIncomingPayments(externalId, ct));

    /// <summary>
    /// Guards on configuration and maps phoenixd failures to a clean ProblemDetails response so
    /// callers get a meaningful status instead of a generic 500.
    /// </summary>
    private async Task<IActionResult> ExecuteAsync<T>(Func<Task<T>> action)
    {
        if (!Enabled)
        {
            return Problem(
                title: "Lightning payments unavailable",
                detail: "phoenixd is not configured on this server.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            return Ok(await action());
        }
        catch (PhoenixdApiException ex)
        {
            logger.LogWarning(ex, "phoenixd returned an error (status {StatusCode}).", ex.StatusCode);
            return Problem(
                title: "Lightning payment provider error",
                detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }
        catch (OperationCanceledException)
        {
            return new StatusCodeResult(StatusCodes.Status499ClientClosedRequest);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Could not reach phoenixd.");
            return Problem(
                title: "Lightning payment provider unreachable",
                detail: "Could not reach the phoenixd node.",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
