using Microsoft.AspNetCore.Mvc;
using Phoenixd.NET.Interfaces;
using Phoenixd.NET.Models;
using Phoenixd.NET.Services;
using Phoenixd.NET.WebService.Client;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FreedomBlaze.Controllers;

[Route("api/payment-manager")]
public class PaymentManagerController : Controller
{
    private readonly PhoenixdManagerService _phoenixdManagerService;

    public PaymentManagerController(PhoenixdManagerService phoenixdManagerService)
    {
        _phoenixdManagerService = phoenixdManagerService;
    }

    // Implement INodeService methods
    [HttpPost("close-channel")]
    public async Task<IActionResult> CloseChannel(string channelId, string address, int feerateSatByte)
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

    // Implement IPaymentService methods
    [HttpGet("incoming-payment")]
    public async Task<IActionResult> GetIncomingPayment(string paymentHash)
    {
        var paymentInfo = await _phoenixdManagerService.PaymentService.GetIncomingPayment(paymentHash);
        return Ok(paymentInfo);
    }

    [HttpGet("outgoing-payment")]
    public async Task<IActionResult> GetOutgoingPayment(string paymentId)
    {
        var paymentInfo = await _phoenixdManagerService.PaymentService.GetOutgoingPayment(paymentId);
        return Ok(paymentInfo);
    }

    [HttpGet("incoming-payments")]
    public async Task<IActionResult> ListIncomingPayments(string externalId)
    {
        var payments = await _phoenixdManagerService.PaymentService.ListIncomingPayments(externalId);
        return Ok(payments);
    }

    [HttpPost("receive-payment")]
    public async Task<IActionResult> ReceiveLightningPayment([FromBody] ReceiveLightningPaymentRequest receiveLightningPaymentRequest)
    {
        var invoice = await _phoenixdManagerService.PaymentService.ReceiveLightningPaymentAsync(receiveLightningPaymentRequest.Description, receiveLightningPaymentRequest.AmountSat, receiveLightningPaymentRequest.ExternalId);
        return Ok(invoice);
    }

    [HttpPost("send-invoice")]
    public async Task<IActionResult> SendLightningInvoice(long amountSat, string invoice)
    {
        var response = await _phoenixdManagerService.PaymentService.SendLightningInvoice(amountSat, invoice);
        return Ok(response);
    }

    [HttpPost("send-onchain-payment")]
    public async Task<IActionResult> SendOnchainPayment(long amountSat, string address, int feerateSatByte)
    {
        var result = await _phoenixdManagerService.PaymentService.SendOnchainPayment(amountSat, address, feerateSatByte);
        return Ok(result);
    }
}
