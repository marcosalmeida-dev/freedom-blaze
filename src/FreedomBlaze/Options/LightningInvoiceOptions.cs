namespace FreedomBlaze.Options;

/// <summary>
/// Policy for the fiat-priced Lightning invoices issued through <c>/api/v1/lightning/invoices</c>,
/// bound from the "LightningInvoices" section.
/// </summary>
public sealed class LightningInvoiceOptions
{
    public const string Section = "LightningInvoices";

    /// <summary>Shortest invoice lifetime a caller may ask for.</summary>
    public const int MinExpirySeconds = 60;

    /// <summary>Longest invoice lifetime a caller may ask for — the quoted price holds for this long.</summary>
    public const int MaxExpirySeconds = 3600;

    /// <summary>Invoice lifetime (and how long its price holds) when the caller does not ask for one.</summary>
    public int DefaultExpirySeconds { get; set; } = 600;

    /// <summary>Largest invoice accepted, in satoshis. Guards against a mistyped amount.</summary>
    public long MaxAmountSat { get; set; } = 5_000_000;

    /// <summary>Independent BTC/USD sources the price must come from before a payment is quoted.</summary>
    public int MinimumRateSources { get; set; } = 2;

    /// <summary>
    /// Oldest acceptable fiat rate. exchangerate-api.com's free plan publishes once a day, so the
    /// default only refuses a provider that has stopped updating; with an hourly plan a few hours fits.
    /// </summary>
    public TimeSpan MaxFiatRateAge { get; set; } = TimeSpan.FromHours(26);

    /// <summary>
    /// When non-empty, the only hosts an invoice's webhook URL may point at. HTTPS is always required;
    /// leave empty to allow any HTTPS host.
    /// </summary>
    public string[] AllowedWebhookHosts { get; set; } = [];
}
