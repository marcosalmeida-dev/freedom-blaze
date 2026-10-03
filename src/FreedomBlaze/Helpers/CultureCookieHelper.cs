using Microsoft.AspNetCore.Localization;

namespace FreedomBlaze.Helpers;

/// <summary>Persists the site language independently of saved converter preferences.</summary>
public static class CultureCookieHelper
{
    public static void Write(HttpContext context, RequestCulture culture)
    {
        context.Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(culture),
            new CookieOptions
            {
                MaxAge = TimeSpan.FromDays(30),
                Path = context.Request.PathBase.HasValue ? context.Request.PathBase.Value : "/",
                SameSite = SameSiteMode.Lax,
                Secure = context.Request.IsHttps,
                IsEssential = true
            });
    }
}
