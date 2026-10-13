using FreedomBlaze.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Phoenixd.NET.Exceptions;
using Phoenixd.NET.Interfaces;
using Phoenixd.NET.Models;

namespace FreedomBlaze.Controllers;

/// <summary>
/// REST surface over the phoenixd node/payment services for external integrators. The Blazor donate
/// UI talks to the services in-process and does not depend on this controller.
/// <para>
/// These endpoints can move funds (send on-chain/Lightning payments, close channels) and expose
/// sensitive node state (balance, channels), so the whole controller is gated behind a <b>master</b>
/// API key sent in the <c>X-Api-Key</c> header — never expose it anonymously.
/// </para>
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
[Authorize(AuthenticationSchemes = ApiKeyDefaults.Scheme, Policy = ApiKeyDefaults.MasterPolicy)]
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

    /// <summary>Returns complete channel states, including Taproot commitments and funding data.</summary>
    [HttpGet("channel-states")]
    public Task<IActionResult> ListChannelStates(CancellationToken ct) =>
        ExecuteAsync(() => node!.ListChannelStatesAsync(ct));

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
        ExecuteAsync(async () =>
        {
            var result = await node!.CloseChannel(channelId, address, feerateSatByte, ct);
            if (result.Status != "ok")
            {
                throw new PhoenixdApiException("phoenixd could not close the channel.", responseBody: result.Message);
            }

            return result;
        });

    /// <summary>Bumps an unconfirmed transaction's fee and returns the resulting transaction ID.</summary>
    [HttpPost("bump-fee")]
    public Task<IActionResult> BumpFee([FromQuery] int feerateSatByte, CancellationToken ct) =>
        ExecuteAsync(() => node!.BumpFee(feerateSatByte, ct));

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

    /// <summary>Creates a BOLT11 invoice; omit amountSat to let the payer choose the amount.</summary>
    [HttpPost("create-invoice")]
    public Task<IActionResult> CreateInvoice([FromBody] CreateInvoiceRequest request, CancellationToken ct) =>
        ExecuteAsync(() => payments!.CreateInvoiceAsync(request, ct));

    /// <summary>Returns the on-chain swap-in address. Requires the daemon's Electrum blockchain client.</summary>
    [HttpGet("swap-in-address")]
    public Task<IActionResult> GetSwapInAddress(CancellationToken ct) =>
        ExecuteAsync(() => payments!.GetSwapInAddressAsync(ct));

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

    /// <summary>Creates a reusable BOLT12 offer, optionally with a fixed amount and description.</summary>
    [HttpPost("create-offer")]
    public Task<IActionResult> CreateOffer([FromQuery] long? amountSat, [FromQuery] string? description, CancellationToken ct) =>
        ExecuteAsync(() => payments!.CreateOfferAsync(amountSat, description, ct));

    // --- Sending -----------------------------------------------------------------------------

    /// <summary>Pays a BOLT11 invoice. Use sendAll to spend the available balance, or specify amountSat.</summary>
    [HttpPost("send-invoice")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> SendLightningInvoice([FromQuery] long amountSat, [FromQuery] string invoice, CancellationToken ct, [FromQuery] bool sendAll = false) =>
        ExecuteAsync(() => payments!.SendLightningInvoice(new PayInvoiceRequest
        {
            Invoice = invoice,
            AmountSat = amountSat == 0 ? null : amountSat,
            SendAll = sendAll
        }, ct));

    /// <summary>Pays a BOLT12 offer, with an optional amount, payer message, or sendAll.</summary>
    [HttpPost("pay-offer")]
    public Task<IActionResult> PayOffer([FromBody] PayOfferRequest request, CancellationToken ct) =>
        ExecuteAsync(() => payments!.PayOfferAsync(request, ct));

    /// <summary>Pays a Lightning address with a specified amount or sendAll.</summary>
    [HttpPost("pay-lightning-address")]
    public Task<IActionResult> PayLightningAddress([FromBody] PayLnAddressRequest request, CancellationToken ct) =>
        ExecuteAsync(() => payments!.PayLnAddressAsync(request, ct));

    /// <summary>Pays an LNURL-pay request with an optional amount, comment, or sendAll.</summary>
    [HttpPost("lnurl-pay")]
    public Task<IActionResult> PayLnurl([FromBody] LnurlPayRequest request, CancellationToken ct) =>
        ExecuteAsync(() => payments!.LnurlPayAsync(request, ct));

    /// <summary>Receives a payment through an LNURL-withdraw request.</summary>
    [HttpPost("lnurl-withdraw")]
    public Task<IActionResult> WithdrawLnurl([FromQuery] string lnurl, CancellationToken ct) =>
        ExecuteAsync(() => payments!.WithdrawLnurlAsync(lnurl, ct));

    /// <summary>Authenticates an LNURL-auth request using the node's key.</summary>
    [HttpPost("lnurl-auth")]
    public Task<IActionResult> AuthenticateLnurl([FromQuery] string lnurl, CancellationToken ct) =>
        ExecuteAsync(() => payments!.AuthenticateLnurlAsync(lnurl, ct));

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

    /// <summary>Decodes a BOLT12 offer and returns its parsed fields.</summary>
    [HttpPost("decode-offer")]
    public Task<IActionResult> DecodeOffer([FromQuery] string offer, CancellationToken ct) =>
        ExecuteAsync(() => payments!.DecodeOfferAsync(offer, ct));

    /// <summary>Looks up a specific incoming payment by its payment hash.</summary>
    [HttpGet("incoming-payment")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> GetIncomingPayment([FromQuery] string paymentHash, CancellationToken ct) =>
        ExecuteAsync(() => payments!.FindIncomingPaymentAsync(paymentHash, ct));

    /// <summary>Looks up a specific outgoing payment by its payment ID.</summary>
    [HttpGet("outgoing-payment")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> GetOutgoingPayment([FromQuery] string paymentId, CancellationToken ct) =>
        ExecuteAsync(() => payments!.FindOutgoingPaymentAsync(paymentId, ct));

    /// <summary>Looks up the preferred outgoing Lightning payment by its payment hash.</summary>
    [HttpGet("outgoing-payment-by-hash")]
    public Task<IActionResult> GetOutgoingPaymentByHash([FromQuery] string paymentHash, CancellationToken ct) =>
        ExecuteAsync(() => payments!.FindOutgoingPaymentByHashAsync(paymentHash, ct));

    /// <summary>Lists incoming payments with date, status, external ID, and pagination filters.</summary>
    [HttpGet("incoming-payments")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> ListIncomingPayments([FromQuery] PaymentsQuery query, CancellationToken ct) =>
        ExecuteAsync(() => payments!.ListIncomingPayments(query, ct));

    /// <summary>Lists Lightning and on-chain outgoing payments with date and pagination filters.</summary>
    [HttpGet("outgoing-payments")]
    public Task<IActionResult> ListOutgoingPayments([FromQuery] PaymentsQuery query, CancellationToken ct) =>
        ExecuteAsync(() => payments!.ListOutgoingPayments(query, ct));

    /// <summary>Exports successful payment history to CSV on the daemon and returns its file location.</summary>
    [HttpPost("export-payments")]
    public Task<IActionResult> ExportPayments([FromQuery] long? from, [FromQuery] long? to, CancellationToken ct) =>
        ExecuteAsync(() => payments!.ExportPaymentsAsync(from, to, ct));

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
            var result = await action();
            return result is null ? NotFound() : Ok(result);
        }
        catch (ArgumentException ex)
        {
            return Problem(
                title: "Invalid payment request",
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
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
