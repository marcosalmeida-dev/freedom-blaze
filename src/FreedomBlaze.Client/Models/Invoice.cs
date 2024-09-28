namespace FreedomBlaze.Client.Models;

public class InvoiceModel
{
    public long AmountSat { get; set; }
    public string PaymentHash { get; set; }
    public string Serialized { get; set; }
}
