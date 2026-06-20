using FreedomBlaze.Client.Helpers;
using FreedomBlaze.Exceptions;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;
using FreedomBlaze.Models.Api;

namespace FreedomBlaze.Services;

/// <summary>
/// Converts between fiat currencies and Bitcoin units using the aggregated BTC/USD price and fiat
/// rates from <see cref="IExchangeRateService"/>. Everything is routed through USD as the pivot: a
/// "units per USD" factor is derived for each side, so any supported pair (fiat↔fiat, fiat↔BTC/SATS,
/// BTC↔SATS) works with the same two-step calculation.
/// </summary>
public sealed class CurrencyConversionService(IExchangeRateService exchangeRateService) : ICurrencyConversionService
{
    private const string Btc = "BTC";
    private const string Sats = "SATS";
    private const string Usd = "USD";

    // Fiat codes the rate provider returns, plus the two Bitcoin units. Built once; case-insensitive.
    private static readonly HashSet<string> Supported = BuildSupported();

    public IReadOnlyCollection<string> SupportedCurrencies { get; } = Supported.OrderBy(c => c).ToArray();

    public async Task<ConversionResponse> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken)
    {
        if (request.Amount <= 0)
            throw new ConversionException("Amount must be greater than zero.", isClientError: true);

        var from = Normalize(request.From);
        var to = Normalize(request.To);

        if (!Supported.Contains(from))
            throw new ConversionException($"Unsupported 'from' currency: '{request.From}'.", isClientError: true);
        if (!Supported.Contains(to))
            throw new ConversionException($"Unsupported 'to' currency: '{request.To}'.", isClientError: true);

        var rates = await exchangeRateService.GetExchangeRateAsync(cancellationToken)
            ?? throw new ConversionException("Live exchange rates are temporarily unavailable.", isClientError: false);

        if (rates.BitcoinRateInUSD <= 0 || rates.CurrencyExchangeRate is null)
            throw new ConversionException("Live exchange rates are temporarily unavailable.", isClientError: false);

        var fromPerUsd = UnitsPerUsd(from, rates);
        var toPerUsd = UnitsPerUsd(to, rates);

        var amountInUsd = request.Amount / fromPerUsd;
        var rawResult = amountInUsd * toPerUsd;

        // One unit of `from` expressed in `to`, surfaced so callers can audit the figure.
        var rate = toPerUsd / fromPerUsd;

        return new ConversionResponse
        {
            Amount = request.Amount,
            From = from,
            To = to,
            Result = Round(rawResult, to),
            Rate = Round(rate, to),
            BitcoinPriceUsd = rates.BitcoinRateInUSD,
            Timestamp = rates.CurrencyExchangeRate.Date is { } date
                ? new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc))
                : DateTimeOffset.UtcNow,
        };
    }

    /// <summary>How many units of <paramref name="code"/> equal one US dollar.</summary>
    private static decimal UnitsPerUsd(string code, BitcoinExchangeRateModel rates)
    {
        var btcUsd = rates.BitcoinRateInUSD;
        switch (code)
        {
            case Usd: return 1m;
            case Btc: return 1m / btcUsd;
            case Sats: return CurrencyConverterHelper.SatoshiPerBitcoin / btcUsd;
            default:
                // Supported, but the provider may not have returned this fiat in the current tick.
                var rate = rates.CurrencyExchangeRate!.Rates.FirstOrDefault(r => r.Currency == code)?.Rate ?? 0m;
                if (rate <= 0)
                    throw new ConversionException($"No live rate available for '{code}' right now.", isClientError: false);
                return rate;
        }
    }

    /// <summary>Rounds to the natural precision of the target unit.</summary>
    private static decimal Round(decimal value, string code) => code switch
    {
        Sats => Math.Round(value, 0, MidpointRounding.AwayFromZero),
        Btc => Math.Round(value, 8, MidpointRounding.AwayFromZero),
        _ => Math.Round(value, 2, MidpointRounding.AwayFromZero),
    };

    private static string Normalize(string code) => code.Trim().ToUpperInvariant();

    private static HashSet<string> BuildSupported()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Btc, Sats };
        foreach (var currency in CurrencyModel.CurrencyListStatic)
            set.Add(currency.Value);
        return set;
    }
}
