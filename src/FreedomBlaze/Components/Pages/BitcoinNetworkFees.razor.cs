using System.Globalization;
using FreedomBlaze.Models;
using FreedomBlaze.Models.BitcoinTracking;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace FreedomBlaze.Components.Pages;

public partial class BitcoinNetworkFees
{
    [Inject] private IStringLocalizer<Resources.Localization> Localizer { get; set; } = default!;
    [Parameter] public BitcoinFeeRecommendations? Recommendations { get; set; }
    [Parameter] public Currency SelectedCurrency { get; set; } = CurrencyModel.CurrencyListStatic[0];
    [Parameter] public decimal? FiatPricePerBitcoin { get; set; }
    [Parameter] public bool LoadingFees { get; set; }
    [Parameter] public bool RefreshDisabled { get; set; }
    [Parameter] public bool FeesUnavailable { get; set; }
    [Parameter] public bool FeesStale { get; set; }
    [Parameter] public bool FiatRatesStale { get; set; }
    [Parameter] public EventCallback OnRefresh { get; set; }
    [Parameter] public int InitialVirtualSize { get; set; } = 140;

    private string _virtualSizeText = "140";
    private static CultureInfo DisplayCulture => CultureInfo.CurrentCulture;
    private int? VirtualSize => int.TryParse(_virtualSizeText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var size)
        && size is >= 1 and <= 100_000 ? size : null;
    private string SizeDescription => VirtualSize is null ? "tracker-fee-basis tracker-fee-size-error" : "tracker-fee-basis";
    private (string Id, string Label, decimal? Rate)[] Priorities =>
    [
        ("none", "Fees.NoPriority", Recommendations?.NoPrioritySatPerVByte),
        ("low", "Fees.LowPriority", Recommendations?.LowPrioritySatPerVByte),
        ("medium", "Fees.MediumPriority", Recommendations?.MediumPrioritySatPerVByte),
        ("high", "Fees.HighPriority", Recommendations?.HighPrioritySatPerVByte),
    ];

    protected override void OnInitialized() => _virtualSizeText = InitialVirtualSize.ToString(CultureInfo.InvariantCulture);

    private long? EstimateSats(decimal? rate)
    {
        if (rate is not > 0 || VirtualSize is not { } size)
            return null;
        try { return checked((long)decimal.Ceiling(rate.Value * size)); }
        catch (OverflowException) { return null; }
    }

    private string FormatFiat(long? sats)
    {
        if (sats is not { } amount || FiatPricePerBitcoin is not > 0)
            return "—";
        try { return "≈ " + (amount / 100_000_000m * FiatPricePerBitcoin.Value).ToString("C", SelectedCurrency.CultureInfo); }
        catch (OverflowException) { return "—"; }
    }
}
