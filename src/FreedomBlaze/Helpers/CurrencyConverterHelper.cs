namespace FreedomBlaze.Helpers;

public static class CurrencyConverterHelper
{
    public const decimal SatoshiPerBitcoin = 100000000m; // 1 BTC = 100,000,000 Satoshis

    public static decimal ConvertToSats(decimal usdAmount, decimal bitcoinPrice)
    {
        if (usdAmount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(usdAmount), "USD amount must be positive.");
        }

        if (bitcoinPrice <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bitcoinPrice), "Bitcoin price must be positive.");
        }

        // Convert USD to BTC
        decimal btcAmount = usdAmount / bitcoinPrice;

        // Convert BTC to Satoshis and round to nearest long
        return Math.Round(btcAmount * SatoshiPerBitcoin, MidpointRounding.AwayFromZero);
    }
}
