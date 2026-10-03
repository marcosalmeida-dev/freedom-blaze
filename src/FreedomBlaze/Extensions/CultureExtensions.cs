using System.Globalization;
using Microsoft.AspNetCore.Localization;

namespace FreedomBlaze.Extensions;

public static class CultureExtensions
{
    /// <summary>The language selector controls translated text and regional number formatting.</summary>
    public static IApplicationBuilder UseLanguageCulture(this IApplicationBuilder app) =>
        app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            var culture = CultureInfo.CurrentCulture;
            if (!CultureInfo.CurrentUICulture.Equals(culture))
            {
                CultureInfo.CurrentUICulture = culture;
                var provider = context.Features.Get<IRequestCultureFeature>()?.Provider;
                context.Features.Set<IRequestCultureFeature>(
                    new RequestCultureFeature(new RequestCulture(culture, culture), provider));
            }

            await next(context);
        });
}
