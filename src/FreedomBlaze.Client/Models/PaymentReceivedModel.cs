using System.Text.Json.Serialization;

namespace FreedomBlaze.Client.Models;

public class PaymentReceivedModel
{
    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("amountSat")]
    public int AmountSat { get; set; }

    [JsonPropertyName("paymentHash")]
    public string PaymentHash { get; set; }

    [JsonPropertyName("externalId")]
    public string ExternalId { get; set; }
}
