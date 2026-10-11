using FreedomBlaze.Client.Models;
using FreedomBlaze.Clients;

namespace FreedomBlaze.Tests;

public sealed class BitcoinNewsEditionBuilderTests
{
    private static readonly DateOnly EditionDate = new(2026, 10, 7);

    [Fact]
    public void RegionalCoverageIsReservedBeforeFillingWithTheNewestStories()
    {
        var northAmerican = Enumerable.Range(0, 5)
            .Select(index => Candidate($"us-{index}", "North America", "US", EditionDate));
        var regional = BalancedPool().Where(candidate => candidate.Continent != "North America")
            .Select(candidate => WithDate(candidate, EditionDate.AddDays(-2)));

        var result = BitcoinNewsEditionBuilder.Select(northAmerican.Concat(regional), EditionDate, 3);

        Assert.Equal(13, result.Candidates.Count);
        Assert.Equal(9, result.Edition.Count);
        Assert.Empty(result.Issues);
        Assert.Equal(0, result.RejectedCount);
        Assert.Equal(2, result.Edition.Count(candidate => candidate.Continent == "North America"));
        Assert.Contains(result.Edition, candidate => candidate.CountryCode == "BR");
        Assert.Equal(6, result.Edition.Select(candidate => candidate.Continent).Distinct().Count());
        Assert.Equal(new[] { "us-0", "us-1" }, result.Edition.Take(2).Select(candidate => candidate.Article.Title));
    }

    [Fact]
    public void PartialDiscoveriesRemainAvailableToCompleteTheNextSearch()
    {
        var pool = BalancedPool();
        var initial = BitcoinNewsEditionBuilder.Select(pool.Take(5), EditionDate, 3);

        Assert.Equal(5, initial.Candidates.Count);
        Assert.Contains(initial.Issues, issue => issue.Contains("received 5"));
        Assert.Contains(initial.Issues, issue => issue.Contains("Oceania"));

        var repaired = BitcoinNewsEditionBuilder.Select(pool.Skip(5).Concat(initial.Candidates), EditionDate, 3);

        Assert.Equal(9, repaired.Candidates.Count);
        Assert.Equal(9, repaired.Edition.Count);
        Assert.Empty(repaired.Issues);
        Assert.All(initial.Candidates, candidate => Assert.Contains(candidate, repaired.Edition));
    }

    [Theory]
    [InlineData("country")]
    [InlineData("continent")]
    [InlineData("date-flag")]
    [InlineData("old-date")]
    [InlineData("future-date")]
    [InlineData("http")]
    [InlineData("homepage")]
    [InlineData("publisher")]
    [InlineData("headline")]
    public void OneInvalidCandidateDoesNotDiscardACompleteValidEdition(string defect)
    {
        var invalid = Candidate("invalid", "Europe", "DE", EditionDate);
        invalid = defect switch
        {
            "country" => invalid with { CountryCode = "ZZ" },
            "continent" => invalid with { Continent = "Africa" },
            "date-flag" => invalid with { HasValidPublicationDate = false },
            "old-date" => WithDate(invalid, EditionDate.AddDays(-3)),
            "future-date" => WithDate(invalid, EditionDate.AddDays(1)),
            "http" => WithUrl(invalid, "http://coindesk.com/invalid"),
            "homepage" => WithUrl(invalid, "https://coindesk.com/"),
            "publisher" => WithUrl(invalid, "https://coindesk.com.untrusted.example/invalid"),
            "headline" => invalid with { Article = new NewsArticleModel { Title = " ", ArticleLinkUrl = invalid.Article.ArticleLinkUrl, Date = invalid.Article.Date } },
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };

        var result = BitcoinNewsEditionBuilder.Select(BalancedPool().Prepend(invalid), EditionDate, 3);

        Assert.Equal(1, result.RejectedCount);
        Assert.NotEmpty(result.RejectionReasons);
        Assert.Equal(9, result.Candidates.Count);
        Assert.Equal(9, result.Edition.Count);
        Assert.Empty(result.Issues);
        Assert.DoesNotContain(invalid, result.Candidates);
    }

    [Fact]
    public void AValidRepairReplacesOlderMetadataEvenWhenItsCorrectedPublicationDateIsEarlier()
    {
        var initial = Candidate("Brazilian adoption", "South America", "AR", EditionDate);
        var repair = WithDate(initial with { CountryCode = "BR" }, EditionDate.AddDays(-1));

        var result = BitcoinNewsEditionBuilder.Select([repair, initial], EditionDate, 3);

        Assert.Same(repair, Assert.Single(result.Candidates));
        Assert.Equal(1, result.RejectedCount);
        Assert.DoesNotContain(result.Issues, issue => issue.Contains("Brazil"));
    }

    [Fact]
    public void AnInvalidRepairCannotReplaceAPreviouslyValidArticle()
    {
        var initial = Candidate("Brazilian adoption", "South America", "BR", EditionDate);
        var repair = WithDate(initial, EditionDate.AddDays(1));

        var result = BitcoinNewsEditionBuilder.Select([repair, initial], EditionDate, 3);

        Assert.Same(initial, Assert.Single(result.Candidates));
        Assert.Equal(1, result.RejectedCount);
        Assert.Contains(result.RejectionReasons, issue => issue.Contains("publication dates between"));
    }

    [Fact]
    public void TrackingVariantsAreOneArticleAndTheFirstValidVersionWins()
    {
        var initial = Candidate("Initial metadata", "Europe", "DE", EditionDate);
        var repair = WithUrl(Candidate("Corrected metadata", "Europe", "DE", EditionDate.AddDays(-1)),
            initial.Article.ArticleLinkUrl.Replace("https://", "https://www.") + "?utm_source=repair#article");

        var result = BitcoinNewsEditionBuilder.Select([repair, initial], EditionDate, 3);

        Assert.Same(repair, Assert.Single(result.Candidates));
        Assert.Equal(1, result.RejectedCount);
        Assert.Contains(result.RejectionReasons, reason => reason.Contains("repeated article links"));
    }

    [Fact]
    public void RepeatedHeadlinesKeepTheNewestStoryIgnoringCaseAndWhitespace()
    {
        var older = Candidate("Bitcoin adoption", "Asia", "JP", EditionDate.AddDays(-1));
        var newer = Candidate("\tBITCOIN   adoption ", "Asia", "JP", EditionDate);

        var result = BitcoinNewsEditionBuilder.Select([older, newer], EditionDate, 3);

        Assert.Same(newer, Assert.Single(result.Candidates));
        Assert.Equal(1, result.RejectedCount);
        Assert.Contains(result.RejectionReasons, reason => reason.Contains("repeated headlines"));
    }

    [Fact]
    public void PublicationWindowIncludesBothBoundaryDates()
    {
        var oldestEligible = Candidate("Older regional story", "Africa", "KE", EditionDate.AddDays(-2));
        var newestEligible = Candidate("Today's story", "Europe", "DE", EditionDate);

        var result = BitcoinNewsEditionBuilder.Select([oldestEligible, newestEligible], EditionDate, 3);

        Assert.Equal(new[] { newestEligible, oldestEligible }, result.Candidates);
        Assert.Equal(0, result.RejectedCount);
        Assert.Empty(result.RejectionReasons);
    }

    [Fact]
    public void ExtraNorthAmericanStoriesCannotFillAnOtherwiseIncompleteEdition()
    {
        var pool = BalancedPool().Take(6).Concat(Enumerable.Range(0, 5)
            .Select(index => Candidate($"extra-us-{index}", "North America", "US", EditionDate)));

        var result = BitcoinNewsEditionBuilder.Select(pool, EditionDate, 3);

        Assert.Equal(11, result.Candidates.Count);
        Assert.Equal(7, result.Edition.Count);
        Assert.Equal(2, result.Edition.Count(candidate => candidate.Continent == "North America"));
        Assert.Contains(result.Issues, issue => issue.Contains("received 7"));
        Assert.Equal(0, result.RejectedCount);
    }

    private static List<BitcoinNewsCandidate> BalancedPool() =>
    [
        Candidate("Brazil", "South America", "BR", EditionDate),
        Candidate("United States", "North America", "US", EditionDate),
        Candidate("Germany", "Europe", "DE", EditionDate),
        Candidate("Kenya", "Africa", "KE", EditionDate),
        Candidate("Japan", "Asia", "JP", EditionDate),
        Candidate("Australia", "Oceania", "AU", EditionDate),
        Candidate("France", "Europe", "FR", EditionDate),
        Candidate("Chile", "South America", "CL", EditionDate),
        Candidate("Singapore", "Asia", "SG", EditionDate),
    ];

    private static BitcoinNewsCandidate Candidate(string title, string continent, string countryCode, DateOnly date) =>
        new(new NewsArticleModel
        {
            Title = title,
            Text = "Verified Bitcoin reporting.",
            ArticleLinkUrl = $"https://coindesk.com/news/{Uri.EscapeDataString(title)}",
            Date = date.ToDateTime(TimeOnly.MinValue),
        }, continent, countryCode, true);

    private static BitcoinNewsCandidate WithDate(BitcoinNewsCandidate candidate, DateOnly date) =>
        candidate with
        {
            Article = new NewsArticleModel
            {
                Title = candidate.Article.Title,
                Text = candidate.Article.Text,
                ArticleLinkUrl = candidate.Article.ArticleLinkUrl,
                Date = date.ToDateTime(TimeOnly.MinValue),
            }
        };

    private static BitcoinNewsCandidate WithUrl(BitcoinNewsCandidate candidate, string url) =>
        candidate with
        {
            Article = new NewsArticleModel
            {
                Title = candidate.Article.Title,
                Text = candidate.Article.Text,
                ArticleLinkUrl = url,
                Date = candidate.Article.Date,
            }
        };
}
