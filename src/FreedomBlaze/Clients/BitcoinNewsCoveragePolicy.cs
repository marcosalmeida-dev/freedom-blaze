using System.Globalization;
using FreedomBlaze.Client.Models;

namespace FreedomBlaze.Clients;

/// <summary>Editorial requirements for a newly generated daily news edition.</summary>
internal static class BitcoinNewsCoveragePolicy
{
    public const int ArticleCount = 9;

    public static IReadOnlyList<string> Continents { get; } = Array.AsReadOnly(new[]
    {
        "North America", "South America", "Europe", "Africa", "Asia", "Oceania"
    });

    // Established specialist publications, including local reporting outside North America.
    // These are editorial preferences, not a ranking of publication traffic.
    public static IReadOnlyList<string> SourceDomains { get; } = Array.AsReadOnly(new[]
    {
        "coindesk.com", "cointelegraph.com", "bitcoinmagazine.com", "decrypt.co",
        "news.bitcoin.com", "blockworks.com", "blockworks.co",
        "portaldobitcoin.uol.com.br", "livecoins.com.br", "criptofacil.com",
        "bitcoinke.io", "cryptonews.com.au", "coinpost.jp"
    });

    // UN M49 country/area groups, retrieved 2026-10-05:
    // https://unstats.un.org/unsd/methodology/m49/overview/
    // Northern America, Central America and the Caribbean form North America here.
    // Include ISO 3166 TW in Asia; exclude AQ because Antarctica has no required news slot.
    // Eurasian border countries may describe events on either continent; likewise EG and ID.
    // Overseas territories use their own ISO codes instead of their administering country's code.
    private static readonly Dictionary<string, HashSet<string>> CountriesByContinent = new(StringComparer.Ordinal)
    {
        ["North America"] = CountryCodes("AG AI AW BB BL BM BQ BS BZ CA CR CU CW DM DO GD GL GP GT HN HT JM KN KY LC MF MQ MS MX NI PA PM PR SV SX TC TT US VC VG VI"),
        ["South America"] = CountryCodes("AR BO BR BV CL CO EC FK GF GS GY PE PY SR UY VE"),
        ["Europe"] = CountryCodes("AD AL AT AX BA BE BG BY CH CZ DE DK EE ES FI FO FR GB GG GI GR HR HU IE IM IS IT JE LI LT LU LV MC MD ME MK MT NL NO PL PT RO RS RU SE SI SJ SK SM UA VA AZ GE KZ TR"),
        ["Africa"] = CountryCodes("AO BF BI BJ BW CD CF CG CI CM CV DJ DZ EG EH ER ET GA GH GM GN GQ GW IO KE KM LR LS LY MA MG ML MR MU MW MZ NA NE NG RE RW SC SD SH SL SN SO SS ST SZ TD TF TG TN TZ UG YT ZA ZM ZW"),
        ["Asia"] = CountryCodes("AE AF AM AZ BD BH BN BT CN CY GE HK ID IL IN IQ IR JO JP KG KH KP KR KW KZ LA LB LK MM MN MO MV MY NP OM PH PK PS QA SA SG SY TH TJ TL TM TR TW UZ VN YE EG RU"),
        ["Oceania"] = CountryCodes("AS AU CC CK CX FJ FM GU HM KI MH MP NC NF NR NU NZ PF PG PN PW SB TK TO TV UM VU WF WS ID"),
    };

    private static HashSet<string> CountryCodes(string codes) => new(codes.Split(' '), StringComparer.Ordinal);

    public static List<string> FindIssues(IReadOnlyList<BitcoinNewsCandidate> candidates)
    {
        var issues = new List<string>();
        if (candidates.Count != ArticleCount)
        {
            issues.Add($"Expected exactly {ArticleCount} usable, distinct articles; received {candidates.Count}.");
        }

        var missing = Continents.Where(continent => !candidates.Any(c => c.Continent == continent)).ToList();
        if (missing.Count > 0)
        {
            issues.Add($"Missing story coverage from: {string.Join(", ", missing)}.");
        }

        if (!candidates.Any(c => c.CountryCode == "BR" && c.Continent == "South America"))
        {
            issues.Add("Include at least one story about Bitcoin in Brazil (BR, South America).");
        }

        if (candidates.Count(c => c.Continent == "North America") > 2)
        {
            issues.Add("Include no more than two North American stories; replace excess stories with other regions.");
        }

        if (candidates.Any(c => !CountriesByContinent.TryGetValue(c.Continent, out var countries)
            || !countries.Contains(c.CountryCode)))
        {
            issues.Add("Every story needs a valid continent and country code for the event, not the publisher's headquarters.");
        }

        if (candidates.Any(c => !c.HasValidPublicationDate))
        {
            issues.Add("Every story needs a valid ISO publication date within the requested news window.");
        }

        if (candidates.Any(c => !IsApprovedArticleUrl(c.Article.ArticleLinkUrl)))
        {
            issues.Add("Use direct HTTPS article links from the preferred Bitcoin news publications only.");
        }

        if (candidates.Select(c => c.Article.Title).Distinct(StringComparer.OrdinalIgnoreCase).Count() != candidates.Count
            || candidates.Select(c => ArticleIdentity(c.Article.ArticleLinkUrl)).Distinct(StringComparer.Ordinal).Count() != candidates.Count)
        {
            issues.Add("Replace repeated headlines or article links with distinct Bitcoin stories.");
        }

        return issues;
    }

    /// <summary>Uses the validated event country for the card's label, never model-supplied geography.</summary>
    public static string CountryDisplayName(string countryCode)
    {
        if (!CountriesByContinent.Values.Any(countries => countries.Contains(countryCode)))
        {
            return string.Empty;
        }

        try
        {
            return new RegionInfo(countryCode).EnglishName;
        }
        catch (ArgumentException)
        {
            // A runtime's globalization data may omit a territory present in the ISO mapping.
            return countryCode;
        }
    }

    /// <summary>Deduplicates tracking variants while preserving meaningful article query parameters.</summary>
    public static string ArticleIdentity(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var parameters = QueryParameters(uri)
            .Where(parameter => !IsTrackingParameter(ParameterName(parameter)))
            .Order(StringComparer.Ordinal)
            .ToList();

        var canonicalHost = uri.IdnHost.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? uri.IdnHost[4..]
            : uri.IdnHost;
        if (canonicalHost.Equals("blockworks.co", StringComparison.OrdinalIgnoreCase))
        {
            canonicalHost = "blockworks.com";
        }

        var canonicalUri = new UriBuilder(uri) { Host = canonicalHost }.Uri;
        return canonicalUri.GetLeftPart(UriPartial.Path)
            + (parameters.Count > 0 ? "?" + string.Join("&", parameters) : string.Empty);
    }

    private static bool IsApprovedArticleUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo)
        && (uri.AbsolutePath != "/" || HasQueryArticleId(uri))
        && SourceDomains.Any(domain => uri.IdnHost.Equals(domain, StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));

    private static bool HasQueryArticleId(Uri uri) =>
        (IsHost(uri, "coinpost.jp") || IsHost(uri, "bitcoinke.io"))
        && QueryParameters(uri).Any(parameter =>
        {
            var separator = parameter.IndexOf('=');
            return separator > 0
                && ParameterName(parameter) == "p"
                && long.TryParse(Uri.UnescapeDataString(parameter[(separator + 1)..]),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                && id > 0;
        });

    private static bool IsHost(Uri uri, string host) =>
        uri.IdnHost.Equals(host, StringComparison.OrdinalIgnoreCase)
        || uri.IdnHost.EndsWith("." + host, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> QueryParameters(Uri uri) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);

    private static string ParameterName(string parameter)
    {
        var separator = parameter.IndexOf('=');
        return Uri.UnescapeDataString((separator < 0 ? parameter : parameter[..separator]).Replace('+', ' '));
    }

    private static bool IsTrackingParameter(string name) =>
        name.StartsWith("utm_", StringComparison.OrdinalIgnoreCase)
        || name.Equals("fbclid", StringComparison.OrdinalIgnoreCase)
        || name.Equals("gclid", StringComparison.OrdinalIgnoreCase)
        || name.Equals("mc_cid", StringComparison.OrdinalIgnoreCase)
        || name.Equals("mc_eid", StringComparison.OrdinalIgnoreCase);
}

internal sealed record BitcoinNewsCandidate(
    NewsArticleModel Article, string Continent, string CountryCode, bool HasValidPublicationDate);
