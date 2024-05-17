using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;

namespace FreedomBlaze.Controllers;

[Route("[controller]/[action]")]
public class CultureController : Controller
{
    public IActionResult Set(string culture, string redirectUri)
    {
        if (culture != null)
        {
            var requestCulture = new RequestCulture(culture, culture);
            HttpContext.Response.Cookies.Append(
                CookieRequestCultureProvider.DefaultCookieName,
                CookieRequestCultureProvider.MakeCookieValue(requestCulture));
        }

        return LocalRedirect(redirectUri);
    }
}
