using System.Globalization;
using Microsoft.AspNetCore.Localization;
using FreedomBlaze.Models;

namespace FreedomBlaze.Services;

public class CultureService
{
    public CultureInfo CurrentCulture { get; private set; } = CultureInfo.GetCultureInfo("en-US");

    public CultureService(IHttpContextAccessor contextAccessor)
    {
        var localizationValue = contextAccessor?.HttpContext?.Features.Get<IRequestCultureFeature>()?.RequestCulture?.Culture?.Name;
        if (!string.IsNullOrEmpty(localizationValue))
        {
            LanguageCultureName = localizationValue;
        }
    }

    private string _languageCultureName = "en-US";
    public string LanguageCultureName
    {
        get => _languageCultureName;
        set
        {
            var currency = CurrencyModel.CurrencyListStatic.FirstOrDefault(currency =>
                string.Equals(currency.CultureName, value, StringComparison.OrdinalIgnoreCase));
            if (currency is not null && _languageCultureName != currency.CultureName)
            {
                _languageCultureName = currency.CultureName;
                CurrentCulture = CultureInfo.GetCultureInfo(currency.CultureName);
                OnLanguageChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public event EventHandler? OnLanguageChanged;
}
