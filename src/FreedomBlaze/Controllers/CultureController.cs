using System.Globalization;
using FreedomBlaze.Models;
using FreedomBlaze.Services;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;

namespace FreedomBlaze.Controllers;

[Route("[controller]/[action]")]
public class CultureController : Controller
{
    private CultureService _currencyService;

    public CultureController(CultureService currencyService)
    {
        _currencyService = currencyService;
    }

    public IActionResult Set(string culture, string redirectUri)
    {
        if (culture != null)
        {
            var requestCulture = new RequestCulture(culture, culture);
            HttpContext.Response.Cookies.Append(
                CookieRequestCultureProvider.DefaultCookieName,
                CookieRequestCultureProvider.MakeCookieValue(requestCulture));

            // Manually update the RequestCulture in the current HttpContext
            HttpContext.Features.Set<IRequestCultureFeature>(new RequestCultureFeature(requestCulture, new CookieRequestCultureProvider()));

            _currencyService.CurrencyCultureName = new CultureInfo(culture).Name;
        }

        return LocalRedirect(redirectUri);
    }
}
