namespace FreedomBlaze.Client.Helpers;

public static class CurrencyConverterHelper
{
    public const decimal SatoshiPerBitcoin = 100_000_000m;

    public static decimal? ConvertToSats(decimal currencyAmount, decimal bitcoinPrice)
    {
        if (currencyAmount <= 0)
            return null;

        if (bitcoinPrice <= 0)
            throw new ArgumentOutOfRangeException(nameof(bitcoinPrice), "Bitcoin price must be positive.");

        var btcAmount = currencyAmount / bitcoinPrice;
        return Math.Round(btcAmount * SatoshiPerBitcoin, MidpointRounding.AwayFromZero);
    }

    public static decimal ConvertSatsToCurrency(decimal satsAmount, decimal bitcoinPrice)
    {
        if (satsAmount <= 0)
            throw new ArgumentOutOfRangeException(nameof(satsAmount), "Satoshis amount must be positive.");

        if (bitcoinPrice <= 0)
            throw new ArgumentOutOfRangeException(nameof(bitcoinPrice), "Bitcoin price must be positive.");

        var btcAmount = satsAmount / SatoshiPerBitcoin;
        return Math.Round(btcAmount * bitcoinPrice, 2);
    }
}
