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
public class PaymentManagerController(
    ILogger<PaymentManagerController> logger,
    IPaymentService? payments = null,
    INodeService? node = null) : ControllerBase
{
    private bool Enabled => payments is not null && node is not null;

    [HttpGet("status")]
    public IActionResult GetStatus() => Ok(new { enabled = Enabled });

    // --- Node / channels ---------------------------------------------------------------------

    [HttpGet("node-info")]
    public Task<IActionResult> GetNodeInfo(CancellationToken ct) =>
        ExecuteAsync(() => node!.GetNodeInfo(ct));

    [HttpGet("balance")]
    public Task<IActionResult> GetBalance(CancellationToken ct) =>
        ExecuteAsync(() => node!.GetBalance(ct));

    [HttpGet("channels")]
    public Task<IActionResult> ListChannels(CancellationToken ct) =>
        ExecuteAsync(() => node!.ListChannels(ct));

    [HttpGet("estimate-liquidity-fees")]
    public Task<IActionResult> EstimateLiquidityFees([FromQuery] long amountSat, CancellationToken ct) =>
        ExecuteAsync(() => node!.EstimateLiquidityFees(amountSat, ct));

    [HttpPost("close-channel")]
    public Task<IActionResult> CloseChannel([FromQuery] string channelId, [FromQuery] string address, [FromQuery] int feerateSatByte, CancellationToken ct) =>
        ExecuteAsync(() => node!.CloseChannel(channelId, address, feerateSatByte, ct));

    // --- Receiving ---------------------------------------------------------------------------

    [HttpPost("receive-payment")]
    public Task<IActionResult> ReceiveLightningPayment([FromBody] ReceiveLightningPaymentRequest request, CancellationToken ct) =>
        ExecuteAsync(() => payments!.ReceiveLightningPaymentAsync(
            request.Description, request.AmountSat, request.ExternalId,
            request.DescriptionHash, request.ExpirySeconds, request.WebhookUrl, ct));

    [HttpGet("lightning-address")]
    public Task<IActionResult> GetLightningAddress(CancellationToken ct) =>
        ExecuteAsync(() => payments!.GetLnAddressAsync(ct));

    [HttpGet("offer")]
    public Task<IActionResult> GetOffer(CancellationToken ct) =>
        ExecuteAsync(() => payments!.GetOfferAsync(ct));

    // --- Sending -----------------------------------------------------------------------------

    [HttpPost("send-invoice")]
    public Task<IActionResult> SendLightningInvoice([FromQuery] long amountSat, [FromQuery] string invoice, CancellationToken ct) =>
        ExecuteAsync(() => payments!.SendLightningInvoice(amountSat, invoice, ct));

    [HttpPost("send-onchain-payment")]
    public Task<IActionResult> SendOnchainPayment([FromQuery] long amountSat, [FromQuery] string address, [FromQuery] int feerateSatByte, CancellationToken ct) =>
        ExecuteAsync(() => payments!.SendOnchainPayment(amountSat, address, feerateSatByte, ct));

    // --- Decoding / history ------------------------------------------------------------------

    [HttpPost("decode-invoice")]
    public Task<IActionResult> DecodeInvoice([FromQuery] string invoice, CancellationToken ct) =>
        ExecuteAsync(() => payments!.DecodeInvoiceAsync(invoice, ct));

    [HttpGet("incoming-payment")]
    public Task<IActionResult> GetIncomingPayment([FromQuery] string paymentHash, CancellationToken ct) =>
        ExecuteAsync(() => payments!.GetIncomingPayment(paymentHash, ct));

    [HttpGet("outgoing-payment")]
    public Task<IActionResult> GetOutgoingPayment([FromQuery] string paymentId, CancellationToken ct) =>
        ExecuteAsync(() => payments!.GetOutgoingPayment(paymentId, ct));

    [HttpGet("incoming-payments")]
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
