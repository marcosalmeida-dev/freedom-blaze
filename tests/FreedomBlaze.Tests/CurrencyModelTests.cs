using FreedomBlaze.Models;

namespace FreedomBlaze.Tests;

public sealed class CurrencyModelTests
{
    [Fact]
    public void CurrencyValuesAreIsolatedBetweenCallersAndTheCatalog()
    {
        var first = CurrencyModel.GetCurrencyList("pt-BR");
        var second = CurrencyModel.GetCurrencyList("pt-BR");

        Assert.Equal("BRL", first[0].Value);
        first[0].BitcoinPrice = 500_000m;
        first[0].CurrencyValueInCurrency = 123m;
        first[0].Name = "Changed by one user";

        Assert.Equal(0m, second[0].BitcoinPrice);
        Assert.Equal(0m, second[0].CurrencyValueInCurrency);
        Assert.Equal("Brazilian Real", second[0].Name);
        Assert.Equal(0m, CurrencyModel.CurrencyListStatic.Single(c => c.Value == "BRL").BitcoinPrice);
        Assert.True(first[0].CultureInfo.IsReadOnly);
    }
}
