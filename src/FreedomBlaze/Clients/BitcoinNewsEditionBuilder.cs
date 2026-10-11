namespace FreedomBlaze.Clients;

/// <summary>Retains usable discovery results and selects a complete, geographically balanced edition.</summary>
internal static class BitcoinNewsEditionBuilder
{
    public static BitcoinNewsEditionSelection Select(
        IEnumerable<BitcoinNewsCandidate> pool, DateOnly editionDate, int lookbackDays)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentOutOfRangeException.ThrowIfLessThan(lookbackDays, 1);

        var earliestDate = DateOnly.FromDayNumber(Math.Max(0, editionDate.DayNumber - (lookbackDays - 1)));
        var valid = new List<BitcoinNewsCandidate>();
        var seenUrls = new HashSet<string>(StringComparer.Ordinal);
        var rejectionReasons = new HashSet<string>(StringComparer.Ordinal);
        var rejectedCount = 0;

        foreach (var candidate in pool)
        {
            var issues = BitcoinNewsCoveragePolicy.FindCandidateIssues(candidate);
            var publishedDate = DateOnly.FromDateTime(candidate.Article.Date);
            if (publishedDate < earliestDate || publishedDate > editionDate)
            {
                issues.Add($"Use verified publication dates between {earliestDate:yyyy-MM-dd} and {editionDate:yyyy-MM-dd}; never substitute a missing date.");
            }

            if (issues.Count > 0)
            {
                rejectedCount++;
                rejectionReasons.UnionWith(issues);
                continue;
            }

            // Callers prepend the latest repair results. A valid correction wins over previous
            // metadata for the same article, even if it corrects the publication date backwards.
            // Invalid corrections are rejected above and therefore cannot erase valid old results.
            if (!seenUrls.Add(BitcoinNewsCoveragePolicy.ArticleIdentity(candidate.Article.ArticleLinkUrl)))
            {
                rejectedCount++;
                rejectionReasons.Add("Discarded repeated article links, including tracking and publisher-domain variants.");
                continue;
            }

            valid.Add(candidate);
        }

        var seenHeadlines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<BitcoinNewsCandidate>();
        foreach (var candidate in valid.OrderByDescending(candidate => candidate.Article.Date))
        {
            var headline = string.Join(" ", candidate.Article.Title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (!seenHeadlines.Add(headline))
            {
                rejectedCount++;
                rejectionReasons.Add("Discarded repeated headlines, keeping the most recently published version.");
                continue;
            }

            candidates.Add(candidate);
        }

        var selected = new HashSet<BitcoinNewsCandidate>();
        var brazil = candidates.FirstOrDefault(candidate => candidate.CountryCode == "BR"
            && candidate.Continent == "South America");
        if (brazil is not null) selected.Add(brazil);

        // Reserve coverage before considering extra stories. A large supply of recent US news
        // must never crowd an available African, Oceanian, Brazilian or other regional story out.
        foreach (var continent in BitcoinNewsCoveragePolicy.Continents)
        {
            if (selected.Any(candidate => candidate.Continent == continent)) continue;
            var regionalStory = candidates.FirstOrDefault(candidate => candidate.Continent == continent);
            if (regionalStory is not null) selected.Add(regionalStory);
        }

        var northAmericaCount = selected.Count(candidate => candidate.Continent == "North America");
        foreach (var candidate in candidates)
        {
            if (selected.Count == BitcoinNewsCoveragePolicy.ArticleCount) break;
            if (selected.Contains(candidate)) continue;
            if (candidate.Continent == "North America" && northAmericaCount >= 2) continue;

            selected.Add(candidate);
            if (candidate.Continent == "North America") northAmericaCount++;
        }

        // Keep newest-first display order, preserving discovery order when dates are equal.
        var edition = candidates.Where(selected.Contains).ToArray();
        return new BitcoinNewsEditionSelection(candidates.ToArray(), edition,
            BitcoinNewsCoveragePolicy.FindIssues(edition), rejectedCount, rejectionReasons.Order(StringComparer.Ordinal).ToArray());
    }
}

internal sealed record BitcoinNewsEditionSelection(
    IReadOnlyList<BitcoinNewsCandidate> Candidates,
    IReadOnlyList<BitcoinNewsCandidate> Edition,
    IReadOnlyList<string> Issues,
    int RejectedCount,
    IReadOnlyList<string> RejectionReasons);
