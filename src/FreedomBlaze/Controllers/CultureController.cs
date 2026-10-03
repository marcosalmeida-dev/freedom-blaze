using FreedomBlaze.Helpers;
using FreedomBlaze.Models;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;

namespace FreedomBlaze.Controllers;

[Route("[controller]/[action]")]
[ApiExplorerSettings(IgnoreApi = true)]
public class CultureController : Controller
{
    public IActionResult Set(string culture, string redirectUri)
    {
        var currency = CurrencyModel.CurrencyListStatic.FirstOrDefault(item =>
            string.Equals(item.CultureName, culture, StringComparison.OrdinalIgnoreCase));
        if (currency is null || !Url.IsLocalUrl(redirectUri))
            return BadRequest();

        CultureCookieHelper.Write(HttpContext, new RequestCulture(currency.CultureName));
        return LocalRedirect(redirectUri);
    }
}
