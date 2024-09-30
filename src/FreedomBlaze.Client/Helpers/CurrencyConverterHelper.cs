namespace FreedomBlaze.Client.Helpers;

public static class CurrencyConverterHelper
{
    public const double SatoshiPerBitcoin = 100000000d; // 1 BTC = 100,000,000 Satoshis

    public static double ConvertToSats(double currencyAmount, double bitcoinPrice)
    {
        if (currencyAmount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currencyAmount), "Currency Amount must be positive.");
        }

        if (bitcoinPrice <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bitcoinPrice), "Bitcoin price must be positive.");
        }

        // Convert USD to BTC and then to Satoshis
        double btcAmount = currencyAmount / bitcoinPrice;
        return Math.Round(btcAmount * SatoshiPerBitcoin, MidpointRounding.AwayFromZero);
    }

    public static string ConvertToBtcFormat(double currencyAmount, double bitcoinPrice)
    {
        if (currencyAmount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currencyAmount), "Currency Amount must be positive.");
        }

        if (bitcoinPrice <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bitcoinPrice), "Bitcoin price must be positive.");
        }

        // Convert USD to BTC
        double btcAmount = currencyAmount / bitcoinPrice;

        // Format the result to 8 decimal places as Bitcoin format
        return btcAmount.ToString("F8");
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
