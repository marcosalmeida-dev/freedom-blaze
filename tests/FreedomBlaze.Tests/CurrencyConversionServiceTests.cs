using FreedomBlaze.Exceptions;
using FreedomBlaze.Interfaces;
using FreedomBlaze.Models;
using FreedomBlaze.Models.Api;
using FreedomBlaze.Services;

namespace FreedomBlaze.Tests;

/// <summary>
/// The rates used throughout: 80,000 USD per bitcoin and 5 BRL per dollar, i.e. R$ 400,000 per
/// bitcoin, so R$ 1 is exactly 250 sats.
/// </summary>
public sealed class CurrencyConversionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 15, 0, 0, TimeSpan.Zero);
    private static readonly SatsQuoteRules Rules = new(MinimumSources: 2, MaxFiatRateAge: TimeSpan.FromHours(26));

    [Fact]
    public async Task QuotePricesFiatAtTheAggregatedRate()
    {
        var service = Service(Rates());

        var quote = await service.QuoteSatsAsync(40m, "brl", Rules, CancellationToken.None);

        Assert.Equal(10_000, quote.AmountSat);
        Assert.Equal("BRL", quote.Currency);
        Assert.Equal(40m, quote.Amount);
        Assert.Equal(400_000m, quote.BitcoinPrice);
        Assert.Equal(80_000m, quote.BitcoinPriceUsd);
        Assert.Equal(3, quote.RateSources);
        Assert.Equal(Now.AddHours(-2), quote.FiatRateTimestamp);
        Assert.Equal(Now, quote.QuotedAt);
    }

    [Fact]
    public async Task QuoteRoundsUpSoThePayeeNeverReceivesLess()
    {
        var service = Service(Rates());

        // 10.0001 BRL is 2,500.025 sats: a payment must ask for 2,501, never 2,500.
        var quote = await service.QuoteSatsAsync(10.0001m, "BRL", Rules, CancellationToken.None);
        var conversion = await service.ConvertAsync(
            new ConversionRequest { Amount = 10.0001m, From = "BRL", To = "SATS" }, CancellationToken.None);

        Assert.Equal(2_501, quote.AmountSat);
        Assert.Equal(2_500m, conversion.Result); // display conversions keep rounding to the nearest sat
    }

    [Fact]
    public async Task QuoteNeverAsksForLessThanOneSatoshi()
    {
        var quote = await Service(Rates()).QuoteSatsAsync(0.0001m, "BRL", Rules, CancellationToken.None);

        Assert.Equal(1, quote.AmountSat);
    }

    [Fact]
    public async Task QuoteRefusesAThinlySourcedPrice()
    {
        var service = Service(Rates(sourceCount: 1));

        var error = await Assert.ThrowsAsync<ConversionException>(
            () => service.QuoteSatsAsync(40m, "BRL", Rules, CancellationToken.None));

        Assert.False(error.IsClientError);
        Assert.Contains("source", error.Message);
    }

    [Fact]
    public async Task QuoteRefusesAStaleFiatRate()
    {
        var service = Service(Rates(fiatDate: Now.AddHours(-27).UtcDateTime));

        var error = await Assert.ThrowsAsync<ConversionException>(
            () => service.QuoteSatsAsync(40m, "BRL", Rules, CancellationToken.None));

        Assert.False(error.IsClientError);
        Assert.Contains("stale", error.Message);
    }

    [Fact]
    public async Task QuoteRefusesAFiatRateWithNoPublicationTime()
    {
        var rates = Rates();
        rates.CurrencyExchangeRate!.Date = null;

        var error = await Assert.ThrowsAsync<ConversionException>(
            () => Service(rates).QuoteSatsAsync(40m, "BRL", Rules, CancellationToken.None));

        Assert.False(error.IsClientError);
    }

    [Fact]
    public async Task QuoteRefusesDevelopmentPlaceholderRates()
    {
        // Placeholders are stamped "now", so freshness alone would let them through.
        var rates = Rates();
        rates.CurrencyExchangeRate!.IsSimulated = true;
        var service = Service(rates);

        var error = await Assert.ThrowsAsync<ConversionException>(
            () => service.QuoteSatsAsync(40m, "BRL", Rules, CancellationToken.None));
        var usd = await service.QuoteSatsAsync(8m, "USD", Rules, CancellationToken.None);

        Assert.False(error.IsClientError);
        Assert.Contains("placeholder", error.Message);
        Assert.Equal(10_000, usd.AmountSat); // BTC/USD comes from the exchanges, not the placeholders
    }

    [Fact]
    public async Task DateOnlyFiatTimestampsAreReadAsUtc()
    {
        // exchangeratesapi.io reports a calendar date, parsed with an unspecified kind.
        var service = Service(Rates(fiatDate: new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Unspecified)));

        var quote = await service.QuoteSatsAsync(40m, "BRL", Rules, CancellationToken.None);

        Assert.Equal(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero), quote.FiatRateTimestamp);
    }

    [Fact]
    public async Task UsdQuoteDoesNotDependOnTheFiatRateAge()
    {
        var service = Service(Rates(fiatDate: Now.AddDays(-30).UtcDateTime));

        var quote = await service.QuoteSatsAsync(8m, "USD", Rules, CancellationToken.None);

        Assert.Equal(10_000, quote.AmountSat);
        Assert.Null(quote.FiatRateTimestamp);
        Assert.Equal(80_000m, quote.BitcoinPrice);
    }

    [Fact]
    public async Task QuoteRefusesACurrencyTheProviderDidNotReturn()
    {
        var error = await Assert.ThrowsAsync<ConversionException>(
            () => Service(Rates()).QuoteSatsAsync(40m, "EUR", Rules, CancellationToken.None));

        Assert.False(error.IsClientError);
    }

    [Fact]
    public async Task QuoteIsUnavailableWithoutRates()
    {
        var error = await Assert.ThrowsAsync<ConversionException>(
            () => Service(null).QuoteSatsAsync(40m, "BRL", Rules, CancellationToken.None));

        Assert.False(error.IsClientError);
    }

    [Fact]
    public async Task BitcoinUnitQuotesNeedNoMarketData()
    {
        var rates = new StubRates(null);
        var service = new CurrencyConversionService(rates, new FixedClock(Now));

        var sats = await service.QuoteSatsAsync(1_000.2m, "sats", Rules, CancellationToken.None);
        var btc = await service.QuoteSatsAsync(0.00001m, "BTC", Rules, CancellationToken.None);

        Assert.Equal(1_001, sats.AmountSat);
        Assert.Equal(1_000, btc.AmountSat);
        Assert.Null(sats.BitcoinPriceUsd);
        Assert.Equal(0, rates.Calls);
    }

    [Theory]
    [InlineData(0, "BRL")]
    [InlineData(-5, "BRL")]
    [InlineData(10, "XYZ")]
    public async Task BadInputIsTheCallersError(double amount, string currency)
    {
        var error = await Assert.ThrowsAsync<ConversionException>(
            () => Service(Rates()).QuoteSatsAsync((decimal)amount, currency, Rules, CancellationToken.None));

        Assert.True(error.IsClientError);
    }

    [Fact]
    public async Task ConvertPricesFiatInSats()
    {
        var conversion = await Service(Rates()).ConvertAsync(
            new ConversionRequest { Amount = 40m, From = "BRL", To = "SATS" }, CancellationToken.None);

        Assert.Equal(10_000m, conversion.Result);
        Assert.Equal(250m, conversion.Rate);
        Assert.Equal(80_000m, conversion.BitcoinPriceUsd);
    }

    private static CurrencyConversionService Service(BitcoinExchangeRateModel? rates) =>
        new(new StubRates(rates), new FixedClock(Now));

    private static BitcoinExchangeRateModel Rates(int sourceCount = 3, DateTime? fiatDate = null) => new()
    {
        BitcoinRateInUSD = 80_000m,
        SourceCount = sourceCount,
        CurrencyExchangeRate = new CurrencyExchangeRateModel
        {
            Date = fiatDate ?? Now.AddHours(-2).UtcDateTime,
            Rates = [new() { Currency = "USD", Rate = 1m }, new() { Currency = "BRL", Rate = 5m }]
        }
    };

    private sealed class StubRates(BitcoinExchangeRateModel? rates) : IExchangeRateService
    {
        public int Calls { get; private set; }
        public List<BitcoinExchangeStatusModel> BitcoinExchangeStatusList { get; } = [];

        public Task<BitcoinExchangeRateModel?> GetExchangeRateAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(rates);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
