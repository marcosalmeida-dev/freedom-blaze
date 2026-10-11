using System.Globalization;
using FreedomBlaze.Components.Pages;
using FreedomBlaze.Models;
using FreedomBlaze.Models.BitcoinTracking;
using HtmlAgilityPack;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FreedomBlaze.Tests;

public sealed class BitcoinNetworkFeesRenderingTests
{
    private static readonly DateTimeOffset QuoteTime = new(2026, 10, 10, 15, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task DefaultEstimateMapsAllFourPrioritiesAndRoundsFractionalFeesUpToWholeSats()
    {
        var document = await RenderAsync(Recommendations(), price: 100_000m);

        Assert.Equal(4, Cards(document).Count);
        AssertCard(document, "none", "No Priority", "1.03 sat/vB", "145 sats", "≈ $0.15");
        AssertCard(document, "low", "Low Priority", "4.01 sat/vB", "562 sats", "≈ $0.56");
        AssertCard(document, "medium", "Medium Priority", "8.05 sat/vB", "1,127 sats", "≈ $1.13");
        AssertCard(document, "high", "High Priority", "12.1 sat/vB", "1,694 sats", "≈ $1.69");
        Assert.Contains("140 vB", Text(Node(document, "//*[@id='tracker-fee-basis']")));
        var size = Node(document, "//*[@id='tracker-fee-size']");
        Assert.Equal("140", size.GetAttributeValue("value", ""));
        Assert.Equal("1", size.GetAttributeValue("min", ""));
        Assert.Equal("100000", size.GetAttributeValue("max", ""));
        Assert.Equal("1", size.GetAttributeValue("step", ""));
        Assert.Equal("false", size.GetAttributeValue("aria-invalid", ""));
        Assert.Equal("https://mempool.space/", Node(document, "//a").GetAttributeValue("href", ""));
        Assert.Equal(QuoteTime.ToString("O"), HtmlEntity.DeEntitize(Node(document, "//time").GetAttributeValue("datetime", "")));
        Assert.Contains("UTC", Text(Node(document, "//time")));
        Assert.Contains("current exchange rates", Text(Node(document, "//*[contains(@class,'fee-estimate-note')]")));
    }

    [Fact]
    public async Task ProviderRatesBelowOneSatPerVByteRemainDistinctInUsdEstimates()
    {
        var document = await RenderAsync(SubOneRecommendations(), price: 100_000m);

        AssertCard(document, "none", "No Priority", "0.2 sat/vB", "28 sats", "≈ $0.03");
        AssertCard(document, "low", "Low Priority", "0.3 sat/vB", "42 sats", "≈ $0.04");
        AssertCard(document, "medium", "Medium Priority", "0.6 sat/vB", "84 sats", "≈ $0.08");
        AssertCard(document, "high", "High Priority", "1 sat/vB", "140 sats", "≈ $0.14");
    }

    [Fact]
    public async Task PreciseProviderSnapshotCalculatesTotalsFromFullRatesRatherThanRoundedPortalLabels()
    {
        var recommendations = SubOneRecommendations() with
        {
            LowPrioritySatPerVByte = 0.284m, MediumPrioritySatPerVByte = 0.572m,
        };
        var document = await RenderAsync(recommendations, price: 100_000m);

        AssertCard(document, "none", "No Priority", "0.2 sat/vB", "28 sats", "≈ $0.03");
        AssertCard(document, "low", "Low Priority", "0.284 sat/vB", "40 sats", "≈ $0.04");
        AssertCard(document, "medium", "Medium Priority", "0.572 sat/vB", "81 sats", "≈ $0.08");
        AssertCard(document, "high", "High Priority", "1 sat/vB", "140 sats", "≈ $0.14");
    }

    [Fact]
    public async Task ProviderRatesBelowOneSatPerVByteUsePortugueseDecimalsAndCurrentBrlEstimates()
    {
        var document = await RenderAsync(SubOneRecommendations(), price: 600_000m, currency: "BRL", culture: "pt-BR");

        AssertCard(document, "none", "Sem prioridade", "0,2 sat/vB", "28 sats", "≈ R$ 0,17");
        AssertCard(document, "low", "Prioridade baixa", "0,3 sat/vB", "42 sats", "≈ R$ 0,25");
        AssertCard(document, "medium", "Prioridade média", "0,6 sat/vB", "84 sats", "≈ R$ 0,50");
        AssertCard(document, "high", "Prioridade alta", "1 sat/vB", "140 sats", "≈ R$ 0,84");
    }

    [Fact]
    public async Task SubOneProviderRatesRoundOnlyTheResultingTotalFeeWhenSizeChanges()
    {
        var document = await RenderAsync(SubOneRecommendations(), price: 100_000m, size: 141);

        AssertCard(document, "none", "No Priority", "0.2 sat/vB", "29 sats", "≈ $0.03");
        AssertCard(document, "low", "Low Priority", "0.3 sat/vB", "43 sats", "≈ $0.04");
        AssertCard(document, "medium", "Medium Priority", "0.6 sat/vB", "85 sats", "≈ $0.09");
        AssertCard(document, "high", "High Priority", "1 sat/vB", "141 sats", "≈ $0.14");
    }

    [Fact]
    public async Task CustomSizeChangesTotalFeesWhileRetainingProviderRates()
    {
        var document = await RenderAsync(Recommendations(), price: 100_000m, size: 141);

        AssertCard(document, "none", "No Priority", "1.03 sat/vB", "146 sats", "≈ $0.15");
        AssertCard(document, "low", "Low Priority", "4.01 sat/vB", "566 sats", "≈ $0.57");
        AssertCard(document, "medium", "Medium Priority", "8.05 sat/vB", "1,136 sats", "≈ $1.14");
        AssertCard(document, "high", "High Priority", "12.1 sat/vB", "1,707 sats", "≈ $1.71");
        Assert.Contains("141 vB", Text(Node(document, "//*[@id='tracker-fee-basis']")));
    }

    [Fact]
    public async Task PortugueseLabelsAndBrazilianCurrencyUseCurrentSelectedCurrencyConversion()
    {
        var document = await RenderAsync(Recommendations(), price: 600_000m, currency: "BRL", culture: "pt-BR");

        Assert.Contains("Taxas atuais de transação", Text(document.DocumentNode));
        AssertCard(document, "none", "Sem prioridade", "1,03 sat/vB", "145 sats", "≈ R$ 0,87");
        AssertCard(document, "low", "Prioridade baixa", "4,01 sat/vB", "562 sats", "≈ R$ 3,37");
        AssertCard(document, "medium", "Prioridade média", "8,05 sat/vB", "1.127 sats", "≈ R$ 6,76");
        AssertCard(document, "high", "Prioridade alta", "12,1 sat/vB", "1.694 sats", "≈ R$ 10,16");
        Assert.Contains("Atualizar taxas", Text(Node(document, "//button[contains(@class,'fee-refresh')]")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task MissingOrInvalidExchangeRateKeepsAllSatoshiEstimates(int? price)
    {
        var document = await RenderAsync(Recommendations(), price: price, fiatStale: true);

        Assert.Equal("145 sats", Value(Card(document, "none"), "fee-estimate-sats"));
        Assert.Equal("1,694 sats", Value(Card(document, "high"), "fee-estimate-sats"));
        Assert.All(Cards(document), card => Assert.Equal("—", Value(card, "fee-estimate-fiat")));
        var unavailableNotes = document.DocumentNode.SelectNodes("//*[contains(@class,'fee-fiat-unavailable')]");
        Assert.NotNull(unavailableNotes);
        Assert.Single(unavailableNotes);
        Assert.Null(document.DocumentNode.SelectSingleNode("//*[contains(@class,'fee-fiat-stale')]"));
        Assert.Contains("Currency estimate unavailable", Text(document.DocumentNode));
    }

    [Fact]
    public async Task InitialLoadingShowsFourPlaceholdersAndDisablesTheIndependentRefreshButton()
    {
        var document = await RenderAsync(null, price: 100_000m, loading: true);

        AssertPlaceholders(document);
        Assert.Contains("Loading current fee rates", Value(document.DocumentNode, "fee-state"));
        Assert.True(Node(document, "//button[contains(@class,'fee-refresh')]").Attributes.Contains("disabled"));
        Assert.Null(document.DocumentNode.SelectSingleNode("//*[contains(@class,'fee-fiat-unavailable')]"));
    }

    [Fact]
    public async Task InitialFailureKeepsAllPrioritiesVisibleWithoutInventingZeroFeesAndAllowsRetry()
    {
        var document = await RenderAsync(null, price: 100_000m, unavailable: true, fiatStale: true);

        AssertPlaceholders(document);
        Assert.Contains("Current fee rates are unavailable. Try again.", Value(document.DocumentNode, "fee-state"));
        Assert.False(Node(document, "//button[contains(@class,'fee-refresh')]").Attributes.Contains("disabled"));
        Assert.DoesNotContain("0 sats", Text(document.DocumentNode));
        Assert.DoesNotContain("$0.00", Text(document.DocumentNode));
        Assert.Null(document.DocumentNode.SelectSingleNode("//*[contains(@class,'fee-fiat-stale')]"));
    }

    [Fact]
    public async Task RefreshLoadingRetainsThePreviousSuccessfulQuote()
    {
        var document = await RenderAsync(Recommendations(), price: 100_000m, loading: true);

        Assert.Equal("145 sats", Value(Card(document, "none"), "fee-estimate-sats"));
        Assert.Equal("≈ $1.69", Value(Card(document, "high"), "fee-estimate-fiat"));
        Assert.Contains("Loading current fee rates", Value(document.DocumentNode, "fee-state"));
        Assert.True(Node(document, "//button[contains(@class,'fee-refresh')]").Attributes.Contains("disabled"));
    }

    [Fact]
    public async Task ATrackingRequestDisablesFeeRefreshWithoutHidingTheLastKnownRates()
    {
        var document = await RenderAsync(Recommendations(), price: 100_000m, refreshDisabled: true);

        Assert.True(Node(document, "//button[contains(@class,'fee-refresh')]").Attributes.Contains("disabled"));
        Assert.Equal("1,694 sats", Value(Card(document, "high"), "fee-estimate-sats"));
        Assert.DoesNotContain("Loading current fee rates", Value(document.DocumentNode, "fee-state"));
    }

    [Fact]
    public async Task FailedRefreshRetainsTheQuoteAndShowsItsLastUpdateTime()
    {
        var document = await RenderAsync(Recommendations(), price: 100_000m, unavailable: true, stale: true);

        Assert.Equal("145 sats", Value(Card(document, "none"), "fee-estimate-sats"));
        Assert.Equal("≈ $1.69", Value(Card(document, "high"), "fee-estimate-fiat"));
        Assert.Contains("Showing the last available rates.", Value(document.DocumentNode, "fee-state"));
        Assert.Contains("3:30", Value(document.DocumentNode, "fee-last-update"));
        Assert.Contains("UTC", Value(document.DocumentNode, "fee-last-update"));
        Assert.False(Node(document, "//button[contains(@class,'fee-refresh')]").Attributes.Contains("disabled"));
    }

    [Fact]
    public async Task StaleExchangeRatesKeepTheLastKnownCurrencyEstimateAndExplainItsAge()
    {
        var document = await RenderAsync(Recommendations(), price: 100_000m, fiatStale: true);

        Assert.Equal("145 sats", Value(Card(document, "none"), "fee-estimate-sats"));
        Assert.Equal("≈ $0.15", Value(Card(document, "none"), "fee-estimate-fiat"));
        Assert.Contains("last available rate", Value(document.DocumentNode, "fee-fiat-stale"));
        Assert.Null(document.DocumentNode.SelectSingleNode("//*[contains(@class,'fee-fiat-unavailable')]"));
        Assert.DoesNotContain("last available rates", Value(document.DocumentNode, "fee-state"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100001)]
    public async Task InvalidSizeKeepsRawRatesButDoesNotDisplayAFeeEstimate(int size)
    {
        var document = await RenderAsync(Recommendations(), price: 100_000m, size: size);

        Assert.Equal("true", Node(document, "//*[@id='tracker-fee-size']").GetAttributeValue("aria-invalid", ""));
        Assert.Contains("tracker-fee-size-error", Node(document, "//*[@id='tracker-fee-size']").GetAttributeValue("aria-describedby", ""));
        Assert.Equal("alert", Node(document, "//*[@id='tracker-fee-size-error']").GetAttributeValue("role", ""));
        Assert.Contains("whole transaction size", Text(Node(document, "//*[@id='tracker-fee-size-error']")));
        Assert.All(Cards(document), card =>
        {
            Assert.Equal("— sats", Value(card, "fee-estimate-sats"));
            Assert.Equal("—", Value(card, "fee-estimate-fiat"));
        });
        Assert.Equal("1.03 sat/vB", Value(Card(document, "none"), "fee-rate"));
    }

    [Theory]
    [InlineData(1, "2 sats", "13 sats")]
    [InlineData(100000, "103,000 sats", "1,210,000 sats")]
    public async Task SupportedSizeBoundariesRemainValid(int size, string noPriority, string highPriority)
    {
        var document = await RenderAsync(Recommendations(), price: 100_000m, size: size);

        Assert.Equal("false", Node(document, "//*[@id='tracker-fee-size']").GetAttributeValue("aria-invalid", ""));
        Assert.Null(document.DocumentNode.SelectSingleNode("//*[@id='tracker-fee-size-error']"));
        Assert.Equal(noPriority, Value(Card(document, "none"), "fee-estimate-sats"));
        Assert.Equal(highPriority, Value(Card(document, "high"), "fee-estimate-sats"));
    }

    [Fact]
    public async Task AnUnrepresentableEstimateRendersAnUnavailableValueRatherThanFailingThePage()
    {
        var recommendations = Recommendations() with { HighPrioritySatPerVByte = decimal.MaxValue };
        var document = await RenderAsync(recommendations, price: 100_000m);

        Assert.Equal("— sats", Value(Card(document, "high"), "fee-estimate-sats"));
        Assert.Equal("—", Value(Card(document, "high"), "fee-estimate-fiat"));
        Assert.Equal("145 sats", Value(Card(document, "none"), "fee-estimate-sats"));
    }

    private static BitcoinFeeRecommendations Recommendations() => new()
    {
        NoPrioritySatPerVByte = 1.03m, LowPrioritySatPerVByte = 4.01m,
        MediumPrioritySatPerVByte = 8.05m, HighPrioritySatPerVByte = 12.1m,
        MinimumSatPerVByte = 1m, UpdatedAt = QuoteTime,
    };

    private static BitcoinFeeRecommendations SubOneRecommendations() => Recommendations() with
    {
        NoPrioritySatPerVByte = 0.2m, LowPrioritySatPerVByte = 0.3m,
        MediumPrioritySatPerVByte = 0.6m, HighPrioritySatPerVByte = 1m,
        MinimumSatPerVByte = 0.1m,
    };

    private static async Task<HtmlDocument> RenderAsync(BitcoinFeeRecommendations? recommendations,
        decimal? price = null, string currency = "USD", string culture = "en-US", int size = 140,
        bool loading = false, bool unavailable = false, bool stale = false, bool fiatStale = false,
        bool refreshDisabled = false)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddLocalization();
            await using var provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var component = await renderer.RenderComponentAsync<BitcoinNetworkFees>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(BitcoinNetworkFees.Recommendations)] = recommendations,
                    [nameof(BitcoinNetworkFees.SelectedCurrency)] = CurrencyModel.CurrencyListStatic.Single(item => item.Value == currency),
                    [nameof(BitcoinNetworkFees.FiatPricePerBitcoin)] = price,
                    [nameof(BitcoinNetworkFees.InitialVirtualSize)] = size,
                    [nameof(BitcoinNetworkFees.LoadingFees)] = loading,
                    [nameof(BitcoinNetworkFees.FeesUnavailable)] = unavailable,
                    [nameof(BitcoinNetworkFees.FeesStale)] = stale,
                    [nameof(BitcoinNetworkFees.FiatRatesStale)] = fiatStale,
                    [nameof(BitcoinNetworkFees.RefreshDisabled)] = refreshDisabled,
                }));
                return component.ToHtmlString();
            });
            var document = new HtmlDocument();
            document.LoadHtml(html);
            return document;
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    private static void AssertPlaceholders(HtmlDocument document)
    {
        Assert.Equal(4, Cards(document).Count);
        Assert.All(Cards(document), card =>
        {
            Assert.Equal("—", Value(card, "fee-estimate-fiat"));
            Assert.Equal("— sats", Value(card, "fee-estimate-sats"));
            Assert.Equal("— sat/vB", Value(card, "fee-rate"));
        });
    }

    private static void AssertCard(HtmlDocument document, string priority, string label, string rate, string sats, string fiat)
    {
        var card = Card(document, priority);
        Assert.Equal(label, Text(Node(card, ".//h4")));
        Assert.Equal(rate, Value(card, "fee-rate"));
        Assert.Equal(sats, Value(card, "fee-estimate-sats"));
        Assert.Equal(fiat, Value(card, "fee-estimate-fiat"));
    }

    private static HtmlNode Card(HtmlDocument document, string priority) => Node(document, $"//article[@data-priority='{priority}']");
    private static HtmlNodeCollection Cards(HtmlDocument document) => document.DocumentNode.SelectNodes("//article[contains(@class,'fee-priority-card')]")
        ?? throw new Xunit.Sdk.XunitException("Expected fee priority cards.");
    private static HtmlNode Node(HtmlDocument document, string path) => Node(document.DocumentNode, path);
    private static HtmlNode Node(HtmlNode node, string path) => node.SelectSingleNode(path)
        ?? throw new Xunit.Sdk.XunitException($"Expected an element matching {path}.");
    private static string Value(HtmlNode node, string className) => Text(Node(node, $".//*[contains(@class,'{className}') ]"));
    private static string Text(HtmlNode node) => string.Join(' ', HtmlEntity.DeEntitize(node.InnerText).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
