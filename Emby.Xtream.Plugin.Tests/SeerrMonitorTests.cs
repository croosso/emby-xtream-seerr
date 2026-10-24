using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Integration tests for the Seerr watchlist monitor's effect on the review gates
    /// (ADR-C002): a title the user asked for in Seerr syncs on the first run after the
    /// provider carries it, a deliberate un-review still wins over a watchlist entry, and
    /// a failed poll stands the monitor down without failing the sync.
    /// </summary>
    public class SeerrMonitorTests : SyncTestBase
    {
        private static readonly string EmptyRequests =
            "{\"pageInfo\":{\"pages\":0,\"results\":0},\"results\":[]}";
        private static readonly string EmptyWatchlist =
            "{\"page\":1,\"totalPages\":1,\"totalResults\":0,\"results\":[]}";

        private StrmSyncService MakeSeerrService()
        {
            var service = MakeService();
            // Same fake handler as the provider calls: the Seerr URLs are registered with
            // distinct substrings, and the registration order below respects the
            // first-match-wins walk (watchlist before the shorter /api/v1/user rule).
            service.SeerrHandler = Handler;
            return service;
        }

        private PluginConfiguration SeerrConfig()
        {
            var config = DefaultConfig();
            config.RequireReviewBeforeSync = true;
            config.EnableSeerrMonitor = true;
            config.SeerrUrl = "http://fake-seerr";
            config.SeerrApiKey = "test-key";
            config.ReviewedVodStreamIdsJson = "[]";
            config.ReviewedSeriesIdsJson = "[]";
            config.UnreviewedVodStreamIdsJson = "[]";
            config.UnreviewedSeriesIdsJson = "[]";
            return config;
        }

        private void RegisterSeerr(string requestsJson, string watchlistJson)
        {
            // Watchlist first: its URL contains "/api/v1/user", so a user rule registered
            // earlier would swallow the watchlist request (registration order is
            // load-bearing for substring matches). Every endpoint answers even when one
            // source is unused — one unmatched URL fails the whole poll.
            Handler.RespondWith("/api/v1/user/1/watchlist", watchlistJson ?? EmptyWatchlist);
            Handler.RespondWith("/api/v1/request", requestsJson ?? EmptyRequests);
            Handler.RespondWith("/api/v1/user",
                "{\"pageInfo\":{\"pages\":1,\"results\":1},\"results\":[{\"id\":1}]}");
        }

        private string MovieRequestsJson(params int[] tmdbIds) =>
            "{\"pageInfo\":{\"pages\":1,\"results\":" + tmdbIds.Length + "},\"results\":[" +
            string.Join(",", tmdbIds.Select(id =>
                "{\"id\":" + id + ",\"status\":2,\"title\":\"Requested " + id +
                "\",\"media\":{\"mediaType\":\"movie\",\"tmdbId\":" + id + "}}")) + "]}";

        private string WatchlistJson(params (int TmdbId, string Title, string MediaType)[] rows) =>
            "{\"page\":1,\"totalPages\":1,\"results\":[" +
            string.Join(",", rows.Select(r =>
                "{\"id\":" + r.TmdbId + ",\"subject\":\"" + r.Title +
                "\",\"media\":{\"mediaType\":\"" + r.MediaType + "\",\"tmdbId\":" + r.TmdbId + "}}")) + "]}";

        private static int[] ParseIds(string json)
            => JsonSerializer.Deserialize<int[]>(json ?? "[]");

        // -----------------------------------------------------------------
        // Movies
        // -----------------------------------------------------------------

        [Fact]
        public async Task Movie_WatchlistedInSeerr_SyncedAndMarkedReviewed()
        {
            var config = SeerrConfig();
            RegisterSeerr(MovieRequestsJson(603), null);
            Handler.RespondWith("get_vod_streams", VodStreamsJson(
                VodStream(streamId: 42, name: "Wanted Movie", added: 1000, tmdbId: "603"),
                VodStream(streamId: 2, name: "Random Movie", added: 1000)));

            var service = MakeSeerrService();
            await service.SyncMoviesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(MovieStrmPath("Wanted Movie")));
            Assert.False(File.Exists(MovieStrmPath("Random Movie")));
            // Only the matched title is folded into the checkpoint; the held one stays
            // waiting in the review queue.
            var reviewed = ParseIds(config.ReviewedVodStreamIdsJson);
            Assert.Contains(42, reviewed);
            Assert.DoesNotContain(2, reviewed);
        }

        [Fact]
        public async Task Movie_UnreviewTombstone_WinsOverSeerrMatch()
        {
            // A withdrawal is the newer, more specific intent than a watchlist entry: the
            // tombstone check runs before the monitor's, so un-reviewing a title the user
            // also watchlisted holds it and schedules its files for removal.
            var config = SeerrConfig();
            config.UnreviewedVodStreamIdsJson = "[42]";
            RegisterSeerr(MovieRequestsJson(603), null);
            Handler.RespondWith("get_vod_streams", VodStreamsJson(
                VodStream(streamId: 42, name: "Wanted Movie", added: 1000, tmdbId: "603")));

            var service = MakeSeerrService();
            await service.SyncMoviesAsync(config, None, SaveConfig);

            Assert.False(File.Exists(MovieStrmPath("Wanted Movie")));
        }

        [Fact]
        public async Task Movie_WithoutProviderTmdbId_StillHeld()
        {
            // The match is on the provider-supplied ID only — a held title costs no TMDB
            // fallback lookup (ADR-C002), so a provider that does not populate tmdb_id
            // keeps holding the title even when it is watchlisted.
            var config = SeerrConfig();
            RegisterSeerr(MovieRequestsJson(603), null);
            Handler.RespondWith("get_vod_streams", VodStreamsJson(
                VodStream(streamId: 42, name: "Wanted Movie", added: 1000, tmdbId: "")));

            var service = MakeSeerrService();
            await service.SyncMoviesAsync(config, None, SaveConfig);

            Assert.False(File.Exists(MovieStrmPath("Wanted Movie")));
        }

        [Fact]
        public async Task Movie_AlreadyOnDiskExemption_StillAppliesWithoutSeerr()
        {
            // The gate's existing on-disk exemption is untouched by the monitor: an
            // established title under a new StreamId syncs even when Seerr knows nothing
            // about it (empty watchlist and requests).
            var config = SeerrConfig();
            RegisterSeerr(null, null);
            var existing = Path.Combine(TempDir.Path, "Movies", "Old Name [tmdbid=603]");
            Directory.CreateDirectory(existing);
            File.WriteAllText(
                Path.Combine(existing, "Old Name [tmdbid=603].strm"),
                "http://fake-xtream/movie/user/pass/99.mkv");
            Handler.RespondWith("get_vod_streams", VodStreamsJson(
                VodStream(streamId: 42, name: "Renamed Title", added: 1000, tmdbId: "603")));

            var service = MakeSeerrService();
            await service.SyncMoviesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(MovieStrmPath("Renamed Title")));
            Assert.Contains(42, ParseIds(config.ReviewedVodStreamIdsJson));
        }

        [Fact]
        public async Task Movie_SeerrUnreachable_StandsDownWithoutFailingSync()
        {
            // No Seerr responses registered: the poll fails, the monitor stands down for
            // the run, and the gate holds everything — its pre-existing, reversible
            // behavior. The sync itself must not fail.
            var config = SeerrConfig();
            Handler.RespondWith("get_vod_streams", VodStreamsJson(
                VodStream(streamId: 42, name: "Wanted Movie", added: 1000, tmdbId: "603")));

            var service = MakeSeerrService();
            await service.SyncMoviesAsync(config, None, SaveConfig);

            Assert.False(File.Exists(MovieStrmPath("Wanted Movie")));
            Assert.Equal(0, service.MovieProgress.Failed);
        }

        [Fact]
        public async Task Movie_MonitorOff_DoesNotPollSeerr()
        {
            var config = SeerrConfig();
            config.EnableSeerrMonitor = false;
            Handler.RespondWith("get_vod_streams", VodStreamsJson(
                VodStream(streamId: 42, name: "Wanted Movie", added: 1000, tmdbId: "603")));

            var service = MakeSeerrService();
            await service.SyncMoviesAsync(config, None, SaveConfig);

            Assert.DoesNotContain(Handler.ReceivedUrls, u => u.Contains("fake-seerr"));
            Assert.False(File.Exists(MovieStrmPath("Wanted Movie")));
        }

        [Fact]
        public async Task Movie_GateOff_DoesNotPollSeerr()
        {
            // The monitor only has work when the gate holds titles; without it, nothing
            // is held, so polling Seerr would be cost without effect.
            var config = SeerrConfig();
            config.RequireReviewBeforeSync = false;
            Handler.RespondWith("get_vod_streams", VodStreamsJson(
                VodStream(streamId: 42, name: "Wanted Movie", added: 1000, tmdbId: "603")));

            var service = MakeSeerrService();
            await service.SyncMoviesAsync(config, None, SaveConfig);

            Assert.DoesNotContain(Handler.ReceivedUrls, u => u.Contains("fake-seerr"));
            Assert.True(File.Exists(MovieStrmPath("Wanted Movie")));
        }

        // -----------------------------------------------------------------
        // Series
        // -----------------------------------------------------------------

        [Fact]
        public async Task Series_WatchlistedInSeerr_SyncedAndMarkedReviewed()
        {
            var config = SeerrConfig();
            RegisterSeerr(null, WatchlistJson((1234, "Wanted Show", "tv")));
            // Detail rule first: "action=get_series" is a substring of the detail URL
            // (registration order is load-bearing).
            Handler.RespondWith("action=get_series_info&series_id=7", SeriesDetailJson(seriesId: 7));
            Handler.RespondWith("action=get_series", SeriesListJson(
                Series(seriesId: 7, name: "Wanted Show"),
                Series(seriesId: 8, name: "Other Show")));

            var service = MakeSeerrService();
            await service.SyncSeriesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(EpisodeStrmPath("Wanted Show", 1, 1)));
            Assert.False(Directory.Exists(Path.Combine(TempDir.Path, "Shows", "Other Show")));
            Assert.Contains(7, ParseIds(config.ReviewedSeriesIdsJson));
            Assert.DoesNotContain(8, ParseIds(config.ReviewedSeriesIdsJson));
        }

        [Fact]
        public async Task Series_NameMatchedThroughSanitizePipeline()
        {
            // Both sides go through the same clean-and-sanitize pipeline, so a colon in a
            // Seerr title matches the provider's identically-titled series rather than
            // missing on punctuation.
            var config = SeerrConfig();
            RegisterSeerr(null, WatchlistJson((1234, "Wanted: Show", "tv")));
            Handler.RespondWith("action=get_series_info&series_id=7", SeriesDetailJson(seriesId: 7));
            Handler.RespondWith("action=get_series", SeriesListJson(
                Series(seriesId: 7, name: "Wanted: Show")));

            var service = MakeSeerrService();
            await service.SyncSeriesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(EpisodeStrmPath("Wanted Show", 1, 1)));
        }

        [Fact]
        public async Task Series_MonitorOff_HeldNormally()
        {
            var config = SeerrConfig();
            config.EnableSeerrMonitor = false;
            Handler.RespondWith("action=get_series", SeriesListJson(
                Series(seriesId: 7, name: "Wanted Show")));

            var service = MakeSeerrService();
            await service.SyncSeriesAsync(config, None, SaveConfig);

            Assert.False(Directory.Exists(Path.Combine(TempDir.Path, "Shows", "Wanted Show")));
        }

        // -----------------------------------------------------------------
        // Local path helpers (same conventions as the movie/series suites)
        // -----------------------------------------------------------------

        private string MovieStrmPath(string movieName)
            => Path.Combine(TempDir.Path, "Movies", movieName, movieName + ".strm");

        private string EpisodeStrmPath(string seriesName, int season, int episode)
        {
            var seasonFolder = $"Season {season:D2}";
            var fileName = $"{seriesName} - S{season:D2}E{episode:D2}.strm";
            return Path.Combine(TempDir.Path, "Shows", seriesName, seasonFolder, fileName);
        }
    }
}
