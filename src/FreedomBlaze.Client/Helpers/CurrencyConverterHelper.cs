namespace FreedomBlaze.Client.Helpers;

public static class CurrencyConverterHelper
{
    public const double SatoshiPerBitcoin = 100000000d; // 1 BTC = 100,000,000 Satoshis

    public static double? ConvertToSats(double currencyAmount, double bitcoinPrice)
    {
        if (currencyAmount <= 0)
        {
            return null;
        }

        if (bitcoinPrice <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bitcoinPrice), "Bitcoin price must be positive.");
        }

        // Convert USD to BTC and then to Satoshis
        double btcAmount = currencyAmount / bitcoinPrice;
        return Math.Round(btcAmount * SatoshiPerBitcoin, MidpointRounding.AwayFromZero);
    }

    public static double ConvertSatsToCurrency(double satsAmount, double bitcoinPrice)
    {
        if (satsAmount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(satsAmount), "Satoshis amount must be positive.");
        }

        if (bitcoinPrice <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bitcoinPrice), "Bitcoin price must be positive.");
        }

        // Convert Satoshis to BTC
        double btcAmount = satsAmount / SatoshiPerBitcoin;

        // Convert BTC to currency (USD)
        return Math.Round(btcAmount * bitcoinPrice, 2); // rounding to 2 decimal places for currency value
    }
}
