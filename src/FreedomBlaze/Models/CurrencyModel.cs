using System.Globalization;

namespace FreedomBlaze.Models;

public class CurrencyModel
{
    private static List<Currency> _currentAppCurrencyList;
    public static Currency CurrentAppCurrency
    {
        get
        {
            if (_currentAppCurrencyList == null)
            {
                _currentAppCurrencyList = GetCurrencyList();
            }
            return _currentAppCurrencyList.FirstOrDefault(w => w.CultureName == CultureInfo.CurrentCulture.Name);
        }
    }

    public static List<Currency> CurrencyListStatic { get; } = new List<Currency>
    {
        new Currency() { Name = "US Dollar", Value = "USD", CultureName = "en-US", Symbol = "$", FlagSvgPath = "/img/country-flag/united-states-flag.svg", CultureInfo = new CultureInfo("en-US") },
        new Currency() { Name = "Euro", Value = "EUR", CultureName = "es-ES", Symbol = "€", FlagSvgPath = "/img/country-flag/european-union-europe-flag.svg", CultureInfo = new CultureInfo("es-ES") },
        new Currency() { Name = "Pound Sterling", Value = "GBP", CultureName = "en-GB", Symbol = "£", FlagSvgPath = "/img/country-flag/united-kingdom-uk-flag.svg", CultureInfo = new CultureInfo("en-GB") },
        new Currency() { Name = "Australian Dollar", Value = "AUD", CultureName = "en-AU", Symbol = "AU$", FlagSvgPath = "/img/country-flag/australia-flag.svg", CultureInfo = new CultureInfo("en-AU") },
        new Currency() { Name = "Japanese Yen", Value = "JPY", CultureName = "ja-JP", Symbol = "¥", FlagSvgPath = "/img/country-flag/japan-flag.svg", CultureInfo = new CultureInfo("ja-JP")  },
        new Currency() { Name = "S. African Rand", Value = "ZAR", CultureName = "af-ZA", Symbol = "R", FlagSvgPath = "/img/country-flag/south-africa-flag.svg", CultureInfo = new CultureInfo("af-ZA")  },
        new Currency() { Name = "Argentine Peso", Value = "ARS", CultureName = "es-AR", Symbol = "AR$", FlagSvgPath = "/img/country-flag/argentina-flag.svg", CultureInfo = new CultureInfo("es-AR") },
        new Currency() { Name = "Brazilian Real", Value = "BRL", CultureName = "pt-BR", Symbol = "R$", FlagSvgPath = "/img/country-flag/brazil-flag.svg", CultureInfo = new CultureInfo("pt-BR") }
    };

    public static List<Currency> GetCurrencyList(string currentCultureName = "en-US")
    {
        return CurrencyListStatic.OrderByDescending(ob => ob.CultureName == currentCultureName).ToList();
    }
}

public class Currency
{
    public string Name { get; set; }
    public string Value { get; set; }
    public string CultureName { get; set; }
    public string Symbol { get; set; }
    public string FlagSvgPath { get; set; }
    public CultureInfo CultureInfo { get; set; }

    public double CurrencyRateInUSD { get; set; }
    public double BitcoinPrice { get; set; }
    public double CurrencyValueInCurrency { get; set; }
    public double SatoshisInFiat { get; set; }

    // Note: this is important so the MudSelect can compare Currency
    public override bool Equals(object o)
    {
        var other = o as Currency;
        return other?.CultureName == CultureName;
    }

    // Note: this is important too!
    public override int GetHashCode() => CultureName?.GetHashCode() ?? 0;

    // Implement this for the Currency to display correctly in MudSelect
    public override string ToString() => CultureName;
}
