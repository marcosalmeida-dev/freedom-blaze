using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc;

namespace FreedomBlaze.Controllers;

/// <summary>
/// Serves the crawler-facing <c>robots.txt</c> and <c>sitemap.xml</c>, built from the live request
/// host so the advertised URLs are always correct regardless of environment (no hard-coded domain).
/// </summary>
public class SeoController : ControllerBase
{
    // Indexable routes and their crawl hints. Keep in sync with the app's @page routes.
    private static readonly (string Path, string Priority, string ChangeFreq)[] Pages =
    [
        ("/", "1.0", "daily"),
        ("/satsconverter", "1.0", "daily"),
        ("/bitcoinnews", "0.8", "daily"),
        ("/about", "0.5", "monthly"),
    ];

    [HttpGet("robots.txt")]
    public ContentResult Robots()
    {
        var sb = new StringBuilder();
        sb.AppendLine("User-agent: *");
        sb.AppendLine("Allow: /");
        sb.AppendLine("Disallow: /api/");
        sb.AppendLine("Disallow: /Error");
        sb.AppendLine();
        sb.AppendLine($"Sitemap: {BaseUrl}/sitemap.xml");

        return Content(sb.ToString(), "text/plain", Encoding.UTF8);
    }

    [HttpGet("sitemap.xml")]
    public ContentResult Sitemap()
    {
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        var lastModified = DateTime.UtcNow.ToString("yyyy-MM-dd");

        var urls = Pages.Select(p => new XElement(ns + "url",
            new XElement(ns + "loc", $"{BaseUrl}{p.Path}"),
            new XElement(ns + "lastmod", lastModified),
            new XElement(ns + "changefreq", p.ChangeFreq),
            new XElement(ns + "priority", p.Priority)));

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(ns + "urlset", urls));

        return Content($"{document.Declaration}{Environment.NewLine}{document}", "application/xml", Encoding.UTF8);
    }

    private string BaseUrl => $"{Request.Scheme}://{Request.Host}";
}
