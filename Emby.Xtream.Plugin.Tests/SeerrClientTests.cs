using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Client;
using Emby.Xtream.Plugin.Tests.Fakes;
using MediaBrowser.Model.Logging;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Tests for <see cref="SeerrClient"/>'s tolerant reading of the Seerr API (ADR-C002).
    ///
    /// Seerr's own API surface already carries two pagination envelopes (the request
    /// endpoint's <c>pageInfo</c> and the watchlist's <c>page</c>/<c>totalPages</c>) and two
    /// row shapes (<c>media</c> vs <c>mediaInfo</c>), so every fixture exercises one of the
    /// shapes the wild presents rather than a single idealized one.
    /// </summary>
    public class SeerrClientTests : SyncTestBase
    {
        private const string BaseUrl = "http://fake-seerr";
        private const string ApiKey = "test-key";

        private SeerrClient MakeClient() => new SeerrClient(new NullLogger(), Handler);

        // -----------------------------------------------------------------
        // Users
        // -----------------------------------------------------------------

        [Fact]
        public async Task GetUserIds_PagesThroughAndReturnsIds()
        {
            Handler.RespondWith("/api/v1/user", "{\"pageInfo\":{\"pages\":1,\"results\":2},\"results\":[" +
                "{\"id\":1},{\"id\":2}]}");

            var users = await MakeClient().GetUserIdsAsync(BaseUrl, ApiKey, None);

            Assert.Equal(new[] { 1, 2 }, users);
            Assert.Contains(BaseUrl + "/api/v1/user?take=50&skip=0", Handler.ReceivedUrls);
        }

        // -----------------------------------------------------------------
        // Requests
        // -----------------------------------------------------------------

        [Fact]
        public async Task GetRequests_ReadsPageInfoEnvelope_PagesThrough_AndSkipsDeclined()
        {
            Handler.RespondWithSequence("/api/v1/request", new[]
            {
                "{\"pageInfo\":{\"pages\":2,\"results\":2},\"results\":[" +
                    "{\"id\":1,\"status\":2,\"title\":\"Kept Movie\",\"media\":{\"mediaType\":\"movie\",\"tmdbId\":11}}," +
                    "{\"id\":2,\"status\":3,\"title\":\"Declined Movie\",\"media\":{\"mediaType\":\"movie\",\"tmdbId\":12}}]}",
                "{\"pageInfo\":{\"pages\":2,\"results\":1},\"results\":[" +
                    "{\"id\":3,\"status\":1,\"title\":\"Kept Show\",\"media\":{\"mediaType\":\"tv\",\"tmdbId\":21}}]}"
            });

            var items = await MakeClient().GetRequestsAsync(BaseUrl, ApiKey, None);

            Assert.Equal(2, items.Count);
            Assert.Contains(items, i => i.TmdbId == 11 && !i.IsSeries && i.Title == "Kept Movie");
            Assert.Contains(items, i => i.TmdbId == 21 && i.IsSeries && i.Title == "Kept Show");
            // Declined is withdrawn: a decline in Seerr must remove the title from the monitor.
            Assert.DoesNotContain(items, i => i.TmdbId == 12);
            Assert.Contains(BaseUrl + "/api/v1/request?take=50&skip=50", Handler.ReceivedUrls);
        }

        [Fact]
        public async Task GetRequests_ReadsTmdbIdFromMediaInfoOrTopLevel()
        {
            // The tmdb id has been seen in media, mediaInfo, and at the top level across
            // Seerr versions; a row must be readable from any of them.
            Handler.RespondWith("/api/v1/request", "{\"pageInfo\":{\"pages\":1,\"results\":3},\"results\":[" +
                "{\"id\":1,\"mediaInfo\":{\"mediaType\":\"movie\",\"tmdbId\":31}}," +
                "{\"id\":2,\"tmdbId\":32,\"type\":\"movie\"}," +
                "{\"id\":3,\"title\":\"No id row\",\"media\":{}}]}");

            var items = await MakeClient().GetRequestsAsync(BaseUrl, ApiKey, None);

            Assert.Equal(2, items.Count);
            Assert.Equal(new[] { 31, 32 }, items.Select(i => i.TmdbId).OrderBy(id => id).ToArray());
        }

        [Fact]
        public async Task GetRequests_ServerError_YieldsEmptyList()
        {
            Handler.RespondWith("/api/v1/request", "boom", HttpStatusCode.InternalServerError);

            var items = await MakeClient().GetRequestsAsync(BaseUrl, ApiKey, None);

            Assert.Empty(items);
        }

        // -----------------------------------------------------------------
        // Watchlist
        // -----------------------------------------------------------------

        [Fact]
        public async Task GetWatchlist_ReadsPageEnvelope_AndSubjectTitle()
        {
            Handler.RespondWithSequence("/api/v1/user/1/watchlist", new[]
            {
                "{\"page\":1,\"totalPages\":2,\"totalResults\":2,\"results\":[" +
                    "{\"id\":1,\"subject\":\"Wanted Movie\",\"media\":{\"mediaType\":\"movie\",\"tmdbId\":41}}]}",
                "{\"page\":2,\"totalPages\":2,\"totalResults\":2,\"results\":[" +
                    "{\"id\":2,\"subject\":\"Wanted Show\",\"media\":{\"mediaType\":\"tv\",\"tmdbId\":42}}]}"
            });

            var items = await MakeClient().GetWatchlistAsync(BaseUrl, ApiKey, 1, None);

            Assert.Equal(2, items.Count);
            Assert.Contains(items, i => i.TmdbId == 41 && i.Title == "Wanted Movie");
            Assert.Contains(items, i => i.TmdbId == 42 && i.IsSeries);
            Assert.Contains(BaseUrl + "/api/v1/user/1/watchlist?page=2", Handler.ReceivedUrls);
        }

        [Fact]
        public async Task GetWatchlist_ServerError_YieldsEmptyList()
        {
            // A transport-level failure is a warning and an empty list — never an
            // exception into the sync.
            Handler.RespondWith("/api/v1/user/1/watchlist", "gone", HttpStatusCode.ServiceUnavailable);

            var items = await MakeClient().GetWatchlistAsync(BaseUrl, ApiKey, 1, None);

            Assert.Empty(items);
        }

        [Fact]
        public async Task GetWatchlist_StringTmdbId_IsTolerated()
        {
            // ADR-010's lesson applied to the other side of the fence: a nominally-numeric
            // field can arrive typed as a string without that being an error.
            Handler.RespondWith("/api/v1/user/1/watchlist",
                "{\"page\":1,\"totalPages\":1,\"results\":[" +
                "{\"id\":1,\"subject\":\"Typed Movie\",\"media\":{\"mediaType\":\"movie\",\"tmdbId\":\"51\"}}]}");

            var items = await MakeClient().GetWatchlistAsync(BaseUrl, ApiKey, 1, None);

            Assert.Single(items);
            Assert.Equal(51, items[0].TmdbId);
        }
    }
}
