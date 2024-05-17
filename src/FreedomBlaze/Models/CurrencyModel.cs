using System.Globalization;

namespace FreedomBlaze.Models;

public class CurrencyModel
{
    public CurrencyModel()
    {
    }

    public static Currency CurrentAppCurrency 
    { 
        get
        {
            return Currencies.Where(w => w.CultureName == CultureInfo.CurrentCulture.Name).FirstOrDefault();
        } 
    }
    public static List<Currency> Currencies { get; } = new List<Currency>
    {
        new Currency() { Name = "US Dollar", Value = "USD", CultureName = "en-US", Symbol = "$" },
        new Currency() { Name = "Euro", Value = "EUR", CultureName = "es-ES", Symbol = "€" },
        new Currency() { Name = "Pound Sterling", Value = "GBP", CultureName = "en-GB", Symbol = "£" },
        new Currency() { Name = "Australian Dollar", Value = "AUD", CultureName = "en-AU", Symbol = "AU$" },
        new Currency() { Name = "Japanese Yen", Value = "JPY", CultureName = "ja-JP", Symbol = "¥" },
        new Currency() { Name = "S. African Rand", Value = "ZAR", CultureName = "af-ZA", Symbol = "R" },
        new Currency() { Name = "Argentine Peso", Value = "ARS", CultureName = "es-AR", Symbol = "AR$" },
        new Currency() { Name = "Brazilian Real", Value = "BRL", CultureName = "pt-BR", Symbol = "R$" }
    };
}

public class Currency
{
    public string Name { get; set; }
    public string Value { get; set; }
    public string CultureName { get; set; }
    public string Symbol { get; set; }

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
