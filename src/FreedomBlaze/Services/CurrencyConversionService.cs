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
public sealed class CurrencyConversionService(
    IExchangeRateService exchangeRateService,
    TimeProvider? timeProvider = null) : ICurrencyConversionService
{
    private const string Btc = "BTC";
    private const string Sats = "SATS";
    private const string Usd = "USD";

    // Fiat codes the rate provider returns, plus the two Bitcoin units. Built once; case-insensitive.
    private static readonly HashSet<string> Supported = BuildSupported();

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public IReadOnlyCollection<string> SupportedCurrencies { get; } = Supported.OrderBy(c => c).ToArray();

    public async Task<SatsQuote> QuoteSatsAsync(
        decimal amount, string currency, SatsQuoteRules rules, CancellationToken cancellationToken)
    {
        if (amount <= 0)
            throw new ConversionException("Amount must be greater than zero.", isClientError: true);

        var code = Normalize(currency);
        if (!Supported.Contains(code))
            throw new ConversionException($"Unsupported currency: '{currency}'.", isClientError: true);

        var now = _timeProvider.GetUtcNow();

        // Bitcoin units are priced without market data, so an exchange outage cannot block them.
        if (code is Sats or Btc)
        {
            var bitcoinPrice = code == Sats ? CurrencyConverterHelper.SatoshiPerBitcoin : 1m;
            return new SatsQuote
            {
                Amount = amount,
                Currency = code,
                AmountSat = CeilingSats(amount * CurrencyConverterHelper.SatoshiPerBitcoin / bitcoinPrice),
                BitcoinPrice = bitcoinPrice,
                QuotedAt = now,
            };
        }

        var rates = await exchangeRateService.GetExchangeRateAsync(cancellationToken)
            ?? throw new ConversionException("Live exchange rates are temporarily unavailable.", isClientError: false);

        if (rates.BitcoinRateInUSD <= 0 || rates.CurrencyExchangeRate is null)
            throw new ConversionException("Live exchange rates are temporarily unavailable.", isClientError: false);

        if (rates.SourceCount < rules.MinimumSources)
            throw new ConversionException(
                $"Only {rates.SourceCount} Bitcoin price source(s) answered; {rules.MinimumSources} are required to price a payment.",
                isClientError: false);

        DateTimeOffset? fiatRateTimestamp = null;
        if (code != Usd)
        {
            if (rates.CurrencyExchangeRate.IsSimulated)
                throw new ConversionException(
                    $"The {code} exchange rate is a development placeholder (no exchange-rate API key is configured); refusing to price a payment with it.",
                    isClientError: false);

            // A provider that stopped updating keeps serving its last rate; refuse it rather than
            // charge yesterday's (or last week's) exchange rate.
            fiatRateTimestamp = FiatRateTimestamp(rates.CurrencyExchangeRate);
            if (fiatRateTimestamp is null || now - fiatRateTimestamp.Value > rules.MaxFiatRateAge)
                throw new ConversionException(
                    $"The {code} exchange rate is stale (published {fiatRateTimestamp?.ToString("u") ?? "at an unknown time"}); refusing to price a payment with it.",
                    isClientError: false);
        }

        var priceInCurrency = rates.BitcoinRateInUSD * UnitsPerUsd(code, rates);

        return new SatsQuote
        {
            Amount = amount,
            Currency = code,
            AmountSat = CeilingSats(amount * CurrencyConverterHelper.SatoshiPerBitcoin / priceInCurrency),
            BitcoinPrice = Math.Round(priceInCurrency, 2, MidpointRounding.AwayFromZero),
            BitcoinPriceUsd = rates.BitcoinRateInUSD,
            RateSources = rates.SourceCount,
            FiatRateTimestamp = fiatRateTimestamp,
            QuotedAt = now,
        };
    }

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

    /// <summary>
    /// Rounds up to whole satoshis — a payment priced in fiat must never fall short of it — and never
    /// below one satoshi.
    /// </summary>
    private static long CeilingSats(decimal sats)
    {
        if (sats > long.MaxValue)
            throw new ConversionException("Amount is too large to price in satoshis.", isClientError: true);

        return Math.Max(1L, (long)Math.Ceiling(sats));
    }

    /// <summary>When the provider published the fiat rates, read as UTC; <c>null</c> when it did not say.</summary>
    private static DateTimeOffset? FiatRateTimestamp(CurrencyExchangeRateModel rates) => rates.Date switch
    {
        null => null,
        { Kind: DateTimeKind.Local } local => new DateTimeOffset(local.ToUniversalTime()),
        { } date => new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)),
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
