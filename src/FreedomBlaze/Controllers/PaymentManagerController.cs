using Microsoft.AspNetCore.Mvc;
using Phoenixd.NET.Models;
using Phoenixd.NET.Services;

namespace FreedomBlaze.Controllers;

[ApiController]
[Route("api/payment-manager")]
public class PaymentManagerController : ControllerBase
{
    private readonly PhoenixdManagerService _phoenixdManagerService;

    public PaymentManagerController(PhoenixdManagerService phoenixdManagerService)
    {
        _phoenixdManagerService = phoenixdManagerService;
    }

    [HttpPost("close-channel")]
    public async Task<IActionResult> CloseChannel([FromQuery] string channelId, [FromQuery] string address, [FromQuery] int feerateSatByte)
    {
        var response = await _phoenixdManagerService.NodeService.CloseChannel(channelId, address, feerateSatByte);
        return Ok(response);
    }

    [HttpGet("balance")]
    public async Task<IActionResult> GetBalance()
    {
        var balance = await _phoenixdManagerService.NodeService.GetBalance();
        return Ok(balance);
    }

    [HttpGet("node-info")]
    public async Task<IActionResult> GetNodeInfo()
    {
        var nodeInfo = await _phoenixdManagerService.NodeService.GetNodeInfo();
        return Ok(nodeInfo);
    }

    [HttpGet("channels")]
    public async Task<IActionResult> ListChannels()
    {
        var channels = await _phoenixdManagerService.NodeService.ListChannels();
        return Ok(channels);
    }

    [HttpGet("incoming-payment")]
    public async Task<IActionResult> GetIncomingPayment([FromQuery] string paymentHash)
    {
        var paymentInfo = await _phoenixdManagerService.PaymentService.GetIncomingPayment(paymentHash);
        return Ok(paymentInfo);
    }

    [HttpGet("outgoing-payment")]
    public async Task<IActionResult> GetOutgoingPayment([FromQuery] string paymentId)
    {
        var paymentInfo = await _phoenixdManagerService.PaymentService.GetOutgoingPayment(paymentId);
        return Ok(paymentInfo);
    }

    [HttpGet("incoming-payments")]
    public async Task<IActionResult> ListIncomingPayments([FromQuery] string externalId)
    {
        var payments = await _phoenixdManagerService.PaymentService.ListIncomingPayments(externalId);
        return Ok(payments);
    }

    [HttpPost("receive-payment")]
    public async Task<IActionResult> ReceiveLightningPayment([FromBody] ReceiveLightningPaymentRequest receiveLightningPaymentRequest)
    {
        var invoice = await _phoenixdManagerService.PaymentService.ReceiveLightningPaymentAsync(
            receiveLightningPaymentRequest.Description,
            receiveLightningPaymentRequest.AmountSat,
            receiveLightningPaymentRequest.ExternalId);
        return Ok(invoice);
    }

    [HttpPost("send-invoice")]
    public async Task<IActionResult> SendLightningInvoice([FromQuery] long amountSat, [FromQuery] string invoice)
    {
        var response = await _phoenixdManagerService.PaymentService.SendLightningInvoice(amountSat, invoice);
        return Ok(response);
    }

    [HttpPost("send-onchain-payment")]
    public async Task<IActionResult> SendOnchainPayment([FromQuery] long amountSat, [FromQuery] string address, [FromQuery] int feerateSatByte)
    {
        var result = await _phoenixdManagerService.PaymentService.SendOnchainPayment(amountSat, address, feerateSatByte);
        return Ok(result);
    }
}
