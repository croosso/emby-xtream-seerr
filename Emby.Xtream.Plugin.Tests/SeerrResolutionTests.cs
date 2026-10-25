using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Emby.Xtream.Plugin.Client;
using Emby.Xtream.Plugin.Service;
using Emby.Xtream.Plugin.Tests.Fakes;
using MediaBrowser.Model.Logging;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Tests for the Seerr resolution view (ADR-C002 follow-up): the rows a user sees when a
    /// wanted title has no automatic match, the candidates proposed for it, and the cases
    /// that must NOT appear (unreleased titles, gate-matched titles, already-reviewed
    /// candidates).
    /// </summary>
    public class SeerrResolutionTests : SyncTestBase
    {
        private PluginConfiguration ResolutionConfig()
        {
            var config = DefaultConfig();
            config.EnableSeerrMonitor = true;
            config.SeerrUrl = "http://fake-seerr";
            config.SeerrApiKey = "test-key";
            config.SelectedVodCategoryIds = new[] { 199 };
            config.SelectedSeriesCategoryIds = new[] { 214, 215 };
            return config;
        }

        private SeerrResolutionService MakeResolutionService()
        {
            // One fake handler for everything: Seerr, the provider catalogue, categories.
            var client = new SeerrClient(new NullLogger(), Handler);
            var monitor = new SeerrMonitor(new NullLogger(), client);
            return new SeerrResolutionService(new NullLogger(), monitor, client, HttpClient);
        }

        private static string MovieRequestsJson(params int[] tmdbIds) =>
            "{\"pageInfo\":{\"pages\":1,\"results\":" + tmdbIds.Length + "},\"results\":[" +
            string.Join(",", tmdbIds.Select(id =>
                "{\"id\":" + id + ",\"status\":2,\"type\":\"movie\",\"media\":{\"mediaType\":\"movie\",\"tmdbId\":" + id + "}}")) + "]}";

        private static string SeriesRequestsJson(params int[] tmdbIds) =>
            "{\"pageInfo\":{\"pages\":1,\"results\":" + tmdbIds.Length + "},\"results\":[" +
            string.Join(",", tmdbIds.Select(id =>
                "{\"id\":" + id + ",\"status\":2,\"type\":\"tv\",\"media\":{\"mediaType\":\"tv\",\"tmdbId\":" + id + "}}")) + "]}";

        private void RegisterSeerrShell(string requestsJson)
        {
            // Registration order is load-bearing: the watchlist URL contains "/api/v1/user".
            Handler.RespondWith("/api/v1/user/1/watchlist",
                "{\"page\":1,\"totalPages\":1,\"totalResults\":0,\"results\":[]}");
            Handler.RespondWith("/api/v1/request", requestsJson);
            Handler.RespondWith("/api/v1/user",
                "{\"pageInfo\":{\"pages\":1,\"results\":1},\"results\":[{\"id\":1}]}");
        }

        private void RegisterCatalogues(
            string vodStreamsJson = null,
            string seriesJson = null,
            string vodCategoriesJson = "[{\"category_id\":199,\"category_name\":\"[EN] DRAMA/ROMANCE\"},{\"category_id\":328,\"category_name\":\"NETFLIX MOVIES\"}]",
            string seriesCategoriesJson = "[{\"category_id\":214,\"category_name\":\"TV\"},{\"category_id\":215,\"category_name\":\"TV FR\"}]")
        {
            Handler.RespondWith("action=get_vod_streams", vodStreamsJson ?? "[]");
            Handler.RespondWith("action=get_vod_categories", vodCategoriesJson);
            // The categories rule first: "action=get_series" is a substring of the categories
            // URL, so registration order is load-bearing here.
            Handler.RespondWith("action=get_series_categories", seriesCategoriesJson);
            Handler.RespondWith("action=get_series", seriesJson ?? "[]");
        }

        private static Func<string, string> Normalizer =>
            name => StrmSyncService.SanitizeFileName(name);

        private static List<SeerrResolutionRow> Movies(SeerrResolutionService service,
            PluginConfiguration config, HashSet<int> reviewed = null) =>
            service.GetResolutionsAsync(config, "movie", Normalizer, reviewed ?? new HashSet<int>(), None)
                .GetAwaiter().GetResult();

        private static List<SeerrResolutionRow> Series(SeerrResolutionService service,
            PluginConfiguration config, HashSet<int> reviewed = null) =>
            service.GetResolutionsAsync(config, "series", Normalizer, reviewed ?? new HashSet<int>(), None)
                .GetAwaiter().GetResult();

        // -----------------------------------------------------------------
        // Movies
        // -----------------------------------------------------------------

        [Fact]
        public void Movie_WithFuzzyAndUnselectedExactCandidates_ListsBothWithReasons()
        {
            // The Step Up shape: the selected category carries a no-tmdb_id variant, and the
            // matching ID exists only in a category the sync does not fetch.
            var config = ResolutionConfig();
            RegisterSeerrShell(MovieRequestsJson(9762));
            Handler.RespondWith("/api/v1/movie/9762",
                "{\"title\":\"Step Up\",\"release_date\":\"2006-08-11\"}");
            RegisterCatalogues(
                "[{\"stream_id\":13684,\"name\":\"EN| Step Up\",\"tmdb_id\":\"\",\"category_id\":199}," +
                "{\"stream_id\":102935,\"name\":\"Step Up (2006)\",\"tmdb_id\":\"9762\",\"category_id\":328}," +
                "{\"stream_id\":5,\"name\":\"Baby Gorilla Cam\",\"tmdb_id\":\"\",\"category_id\":199}]");

            var rows = Movies(MakeResolutionService(), config);

            var row = Assert.Single(rows);
            Assert.Equal("Step Up", row.Title);
            Assert.Equal(2, row.Candidates.Length);

            var unselected = row.Candidates.Single(c => c.ExactTmdb);
            Assert.False(unselected.InSelectedCategories);
            Assert.Equal(102935, unselected.ProviderId);
            Assert.Contains("NETFLIX MOVIES", unselected.CategoryNames);

            var fuzzy = row.Candidates.Single(c => !c.ExactTmdb);
            Assert.True(fuzzy.InSelectedCategories);
            Assert.Equal(13684, fuzzy.ProviderId);
        }

        [Fact]
        public void Movie_WithExactIdInASelectedCategory_IsAlreadyMatchedAndNotListed()
        {
            var config = ResolutionConfig();
            RegisterSeerrShell(MovieRequestsJson(9762));
            Handler.RespondWith("/api/v1/movie/9762",
                "{\"title\":\"Step Up\",\"release_date\":\"2006-08-11\"}");
            RegisterCatalogues(
                "[{\"stream_id\":1,\"name\":\"Step Up (2006)\",\"tmdb_id\":\"9762\",\"category_id\":199}]");

            var rows = Movies(MakeResolutionService(), config);

            Assert.Empty(rows);
        }

        [Fact]
        public void Movie_NotReleasedYet_IsNotListed()
        {
            // A title with a future release date cannot be in a provider catalogue, so
            // proposing candidates for it is noise. The gate picks it up when it lands.
            var config = ResolutionConfig();
            RegisterSeerrShell(MovieRequestsJson(42));
            Handler.RespondWith("/api/v1/movie/42",
                "{\"title\":\"Future Blockbuster\",\"release_date\":\"2099-06-01\"}");
            RegisterCatalogues(
                "[{\"stream_id\":1,\"name\":\"Future Blockbuster\",\"tmdb_id\":\"\",\"category_id\":199}]");

            var rows = Movies(MakeResolutionService(), config);

            Assert.Empty(rows);
        }

        [Fact]
        public void Movie_AlreadyReviewedCandidate_IsNotProposedAgain()
        {
            var config = ResolutionConfig();
            RegisterSeerrShell(MovieRequestsJson(9762));
            Handler.RespondWith("/api/v1/movie/9762",
                "{\"title\":\"Step Up\",\"release_date\":\"2006-08-11\"}");
            RegisterCatalogues(
                "[{\"stream_id\":1,\"name\":\"EN| Step Up\",\"tmdb_id\":\"\",\"category_id\":199}]");

            var rows = Movies(MakeResolutionService(), config, new HashSet<int> { 1 });

            Assert.Empty(rows);
        }

        [Fact]
        public void Movie_WithNoCandidatesAtAll_IsNotListed()
        {
            var config = ResolutionConfig();
            RegisterSeerrShell(MovieRequestsJson(9762));
            Handler.RespondWith("/api/v1/movie/9762",
                "{\"title\":\"Step Up\",\"release_date\":\"2006-08-11\"}");
            RegisterCatalogues(
                "[{\"stream_id\":5,\"name\":\"Baby Gorilla Cam\",\"tmdb_id\":\"\",\"category_id\":199}]");

            var rows = Movies(MakeResolutionService(), config);

            Assert.Empty(rows);
        }

        // -----------------------------------------------------------------
        // Series
        // -----------------------------------------------------------------

        [Fact]
        public void Series_FuzzyCandidate_CarriesEveryCopyOfTheNameGroup()
        {
            // Series collapse to one representative at sync time, so a proposal that carried
            // only one copy's id could review the wrong copy and leave the title held.
            var config = ResolutionConfig();
            RegisterSeerrShell(SeriesRequestsJson(1402));
            Handler.RespondWith("/api/v1/tv/1402",
                "{\"name\":\"Wanted Show\",\"firstAirDate\":\"2010-01-01\"}");
            RegisterCatalogues(null,
                "[{\"series_id\":7,\"name\":\"Wanted Shows\",\"category_id\":214}," +
                "{\"series_id\":8,\"name\":\"Wanted Shows\",\"category_id\":215}," +
                "{\"series_id\":9,\"name\":\"Baby Gorilla Cam\",\"category_id\":214}]");

            var rows = Series(MakeResolutionService(), config);

            var row = Assert.Single(rows);
            var candidate = Assert.Single(row.Candidates);
            Assert.Contains(7, candidate.ProviderIds);
            Assert.Contains(8, candidate.ProviderIds);
        }

        [Fact]
        public void Series_GateEquivalentMatch_IsAlreadyMatchedAndNotListed()
        {
            var config = ResolutionConfig();
            RegisterSeerrShell(SeriesRequestsJson(1402));
            Handler.RespondWith("/api/v1/tv/1402",
                "{\"name\":\"Wanted Show\",\"firstAirDate\":\"2010-01-01\"}");
            RegisterCatalogues(null,
                "[{\"series_id\":7,\"name\":\"Wanted Show\",\"category_id\":214}]");

            var rows = Series(MakeResolutionService(), config);

            Assert.Empty(rows);
        }

        [Fact]
        public void Series_SameNameDifferentYear_IsProposedWithADemotedScore()
        {
            // A same-named remake: not a gate match (the years disagree), so the user should
            // see it, ranked below what the year says it is not.
            var config = ResolutionConfig();
            RegisterSeerrShell(SeriesRequestsJson(1402));
            Handler.RespondWith("/api/v1/tv/1402",
                "{\"name\":\"Wanted Show\",\"firstAirDate\":\"2010-01-01\"}");
            RegisterCatalogues(null,
                "[{\"series_id\":7,\"name\":\"Wanted Show (1988)\",\"category_id\":214}]");

            var rows = Series(MakeResolutionService(), config);

            var row = Assert.Single(rows);
            var candidate = Assert.Single(row.Candidates);
            Assert.True(candidate.Score < 100, "the year mismatch must be visible in the score");
        }

        [Fact]
        public void UnconfiguredMonitor_ReturnsNothing()
        {
            var config = DefaultConfig();
            var rows = Movies(MakeResolutionService(), config);

            Assert.Empty(rows);
        }
    }
}
