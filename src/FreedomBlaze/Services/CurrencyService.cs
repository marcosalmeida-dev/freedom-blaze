using System.Globalization;
using Microsoft.AspNetCore.Localization;

namespace FreedomBlaze.Services;

public class CultureService
{
    public CultureInfo CurrentCulture;
    public CultureService(IHttpContextAccessor contextAccessor)
    {
        var localizationValue = contextAccessor?.HttpContext?.Features.Get<IRequestCultureFeature>()?.RequestCulture?.Culture?.Name;
        if (!string.IsNullOrEmpty(localizationValue))
        {
            CurrentCulture = new CultureInfo(localizationValue);
            CurrencyCultureName = CurrentCulture.Name;
        }
    }

    private string _currencyCultureName;
    public string CurrencyCultureName
    {
        get => _currencyCultureName;
        set
        {
            if (_currencyCultureName != value)
            {
                _currencyCultureName = value;
                // Optionally notify components that the currency culture has changed
                OnCurrencyCultureChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    // Event for notifying about the currency change
    public event EventHandler OnCurrencyCultureChanged;
}

