using FreedomBlaze.Client.Enums;
using FreedomBlaze.Client.Helpers;

namespace FreedomBlaze.Models;

/// <summary>Per-converter amounts and rates. The last edited amount remains the source on rate updates.</summary>
public sealed class SatsConversionState
{
    public const decimal MaximumAmount = 1_000_000_000_000_000_000m;
    private readonly List<Currency> _currencies;

    public SatsConversionState(string cultureName)
    {
        _currencies = CurrencyModel.GetCurrencyList(cultureName);
        SelectedCurrency = Currencies.FirstOrDefault(currency => currency.CultureName == cultureName)
            ?? Currencies.First(currency => currency.Value == "USD");
    }

    public IReadOnlyList<Currency> Currencies => _currencies;
    public Currency SelectedCurrency { get; private set; }
    public ConversionType Direction { get; private set; } = ConversionType.BitcoinToCurrency;
    public decimal? CurrencyAmount { get; private set; }
    public decimal? SatsAmount { get; private set; } = 100_000m;
    public bool HasRates => SelectedCurrency.BitcoinPrice > 0;
    public bool InputOutOfRange { get; private set; }
    public decimal? BitcoinAmount => Direction == ConversionType.BitcoinToCurrency
        ? SatsAmount / CurrencyConverterHelper.SatoshiPerBitcoin
        : HasRates && !InputOutOfRange ? CurrencyAmount / SelectedCurrency.BitcoinPrice : null;

    public void SelectCurrency(string? cultureName)
    {
        var currency = Currencies.FirstOrDefault(item => item.CultureName == cultureName);
        if (currency is null || ReferenceEquals(currency, SelectedCurrency))
            return;

        SelectedCurrency = currency;
        _currencies.Remove(currency);
        _currencies.Insert(0, currency);
        Recalculate();
    }

    public void SetCurrencyAmount(decimal? amount)
    {
        Direction = ConversionType.CurrencyToBitcoin;
        CurrencyAmount = Normalize(amount);
        Recalculate();
    }

    public void SetSatsAmount(decimal? amount)
    {
        Direction = ConversionType.BitcoinToCurrency;
        SatsAmount = amount.HasValue ? Math.Round(Normalize(amount)!.Value, MidpointRounding.AwayFromZero) : null;
        Recalculate();
    }

    public void UpdateRates(BitcoinExchangeRateModel? rates)
    {
        var fiatRates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["USD"] = 1m };
        if (rates?.CurrencyExchangeRate is { } exchange)
        {
            foreach (var rate in exchange.Rates)
            {
                if (!string.IsNullOrEmpty(rate.Currency) && rate.Rate > 0)
                    fiatRates[rate.Currency] = rate.Rate;
            }
        }

        foreach (var currency in Currencies)
        {
            currency.CurrencyRateInUSD = fiatRates.GetValueOrDefault(currency.Value);
            currency.BitcoinPrice = 0;
            if (rates?.BitcoinRateInUSD > 0 && currency.CurrencyRateInUSD > 0)
            {
                try
                {
                    currency.BitcoinPrice = rates.BitcoinRateInUSD * currency.CurrencyRateInUSD;
                }
                catch (OverflowException)
                {
                    // An invalid upstream rate must not prevent other currencies from converting.
                }
            }
        }

        Recalculate();
    }

    private static decimal? Normalize(decimal? amount) => amount.HasValue
        ? Math.Clamp(amount.Value, 0m, MaximumAmount)
        : null;

    private void Recalculate()
    {
        InputOutOfRange = false;
        foreach (var currency in Currencies)
            currency.CurrencyValueInCurrency = 0;

        var source = Direction == ConversionType.CurrencyToBitcoin ? CurrencyAmount : SatsAmount;
        if (!source.HasValue || source.Value == 0 || !HasRates)
        {
            var result = source == 0 ? 0m : (decimal?)null;
            if (Direction == ConversionType.CurrencyToBitcoin)
                SatsAmount = result;
            else
                CurrencyAmount = result;
            return;
        }

        try
        {
            var bitcoin = Direction == ConversionType.CurrencyToBitcoin
                ? source.Value / SelectedCurrency.BitcoinPrice
                : source.Value / CurrencyConverterHelper.SatoshiPerBitcoin;

            if (Direction == ConversionType.CurrencyToBitcoin)
                SatsAmount = Math.Round(bitcoin * CurrencyConverterHelper.SatoshiPerBitcoin, MidpointRounding.AwayFromZero);
            else
                CurrencyAmount = Math.Round(bitcoin * SelectedCurrency.BitcoinPrice, 2, MidpointRounding.AwayFromZero);

            foreach (var currency in Currencies)
                currency.CurrencyValueInCurrency = bitcoin * currency.BitcoinPrice;
        }
        catch (OverflowException)
        {
            InputOutOfRange = true;
            foreach (var currency in Currencies)
                currency.CurrencyValueInCurrency = 0;
            if (Direction == ConversionType.CurrencyToBitcoin)
                SatsAmount = null;
            else
                CurrencyAmount = null;
        }
    }
}
