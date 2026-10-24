using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace Emby.Xtream.Plugin.Client
{
    /// <summary>One wanted title read from Seerr — a request or a watchlist row.</summary>
    public sealed class SeerrWantedItem
    {
        public int TmdbId;
        public bool IsSeries;
        public string Title;
    }

    /// <summary>
    /// Minimal read-only client for a Seerr server (https://github.com/seerr-team/seerr),
    /// used by the review-gate monitor (ADR-C002).
    /// <para>
    /// The payloads are parsed by walking the JSON document rather than deserializing into
    /// fixed DTOs: Seerr's own API surface has already drifted between the request
    /// envelope (<c>pageInfo</c>) and the watchlist envelope (<c>page</c>/<c>totalPages</c>),
    /// and between <c>media</c> and <c>mediaInfo</c> object shapes — the same tolerance the
    /// Xtream provider models carry per ADR-010. Every field is optional to the parser;
    /// a row that yields no TMDB ID is dropped rather than thrown on.
    /// </para>
    /// </summary>
    public sealed class SeerrClient
    {
        // Seerr request statuses: 1 pending, 2 approved, 3 declined. Pending and approved
        // both mean "I want this" — declined rows are skipped so un-wanting a title in Seerr
        // withdraws it from the monitor too.
        private const int DeclinedStatus = 3;

        private const int PageSize = 50;

        // A hard ceiling on pagination so a server that reports a bogus page count cannot
        // make the sync loop forever. 100 pages at 50 rows is far past any real watchlist.
        private const int MaxPages = 100;

        private readonly ILogger _logger;
        private readonly HttpMessageHandler _handler;
        private HttpClient _httpClient;
        private readonly object _clientLock = new object();

        public SeerrClient(ILogger logger, HttpMessageHandler handler = null)
        {
            _logger = logger;
            _handler = handler;
        }

        private HttpClient Client
        {
            get
            {
                lock (_clientLock)
                {
                    if (_httpClient == null)
                    {
                        _httpClient = _handler != null
                            ? new HttpClient(_handler)
                            : new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                    }
                    return _httpClient;
                }
            }
        }

        /// <summary>All user IDs, for enumerating per-user watchlists.</summary>
        public async Task<List<int>> GetUserIdsAsync(string baseUrl, string apiKey, CancellationToken cancellationToken)
        {
            var users = new List<int>();
            for (var page = 0; page < MaxPages; page++)
            {
                var url = CombineUrl(baseUrl, "/api/v1/user")
                    + "?take=" + PageSize + "&skip=" + (page * PageSize);
                using (var doc = await GetJsonAsync(url, apiKey, cancellationToken).ConfigureAwait(false))
                {
                    if (doc == null) break;
                    var root = doc.RootElement;
                    if (!TryGetArray(root, "results", out var results)) break;
                    var found = 0;
                    foreach (var row in results.EnumerateArray())
                    {
                        int id;
                        if (TryGetInt(row, "id", out id))
                        {
                            users.Add(id);
                            found++;
                        }
                    }
                    if (found == 0 || ReadPageCount(root) <= page + 1) break;
                }
            }
            return users;
        }

        /// <summary>Every request the server holds, pending or approved (declined excluded).</summary>
        public async Task<List<SeerrWantedItem>> GetRequestsAsync(string baseUrl, string apiKey, CancellationToken cancellationToken)
        {
            var items = new List<SeerrWantedItem>();
            for (var page = 0; page < MaxPages; page++)
            {
                var url = CombineUrl(baseUrl, "/api/v1/request")
                    + "?take=" + PageSize + "&skip=" + (page * PageSize);
                using (var doc = await GetJsonAsync(url, apiKey, cancellationToken).ConfigureAwait(false))
                {
                    if (doc == null) break;
                    var root = doc.RootElement;
                    if (!TryGetArray(root, "results", out var results)) break;
                    foreach (var row in results.EnumerateArray())
                    {
                        int status;
                        if (TryGetInt(row, "status", out status) && status == DeclinedStatus) continue;
                        var item = ReadWantedRow(row);
                        if (item != null) items.Add(item);
                    }
                    if (ReadPageCount(root) <= page + 1) break;
                }
            }
            return items;
        }

        /// <summary>One user's watchlist rows. Returns an empty list on any error.</summary>
        public async Task<List<SeerrWantedItem>> GetWatchlistAsync(
            string baseUrl, string apiKey, int userId, CancellationToken cancellationToken)
        {
            var items = new List<SeerrWantedItem>();
            for (var page = 1; page <= MaxPages; page++)
            {
                var url = CombineUrl(baseUrl, "/api/v1/user/" + userId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "/watchlist?page=" + page);
                using (var doc = await GetJsonAsync(url, apiKey, cancellationToken).ConfigureAwait(false))
                {
                    if (doc == null) break;
                    var root = doc.RootElement;
                    if (!TryGetArray(root, "results", out var results)) break;
                    foreach (var row in results.EnumerateArray())
                    {
                        var item = ReadWantedRow(row);
                        if (item != null) items.Add(item);
                    }
                    if (ReadPageCount(root) <= page) break;
                }
            }
            return items;
        }

        private SeerrWantedItem ReadWantedRow(JsonElement row)
        {
            int tmdbId;
            if (!TryGetNestedInt(row, out tmdbId)) return null;

            string mediaType = null;
            TryGetNestedString(row, "mediaType", out mediaType);
            var isSeries = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mediaType, "series", StringComparison.OrdinalIgnoreCase);

            // Watchlist rows carry the title in subject; request rows in title (or name
            // for the mediaInfo shape). Read all three, container-first.
            string title = null;
            foreach (var field in new[] { "subject", "title", "name" })
            {
                if (TryGetNestedString(row, field, out title)) break;
            }

            return new SeerrWantedItem { TmdbId = tmdbId, IsSeries = isSeries, Title = title };
        }

        // The TMDB id lives in media, mediaInfo, or at the top level depending on endpoint
        // and Seerr version; read the first location that carries a plausible number.
        private bool TryGetNestedInt(JsonElement row, out int value)
        {
            foreach (var containerName in new[] { "media", "mediaInfo" })
            {
                JsonElement container;
                if (row.TryGetProperty(containerName, out container)
                    && container.ValueKind == JsonValueKind.Object
                    && TryGetInt(container, "tmdbId", out value)
                    && value > 0)
                {
                    return true;
                }
            }
            if (TryGetInt(row, "tmdbId", out value) && value > 0) return true;
            return false;
        }

        private bool TryGetNestedString(JsonElement row, string field, out string value)
        {
            foreach (var containerName in new[] { "media", "mediaInfo" })
            {
                JsonElement container;
                if (row.TryGetProperty(containerName, out container)
                    && container.ValueKind == JsonValueKind.Object
                    && TryGetString(container, field, out value)
                    && !string.IsNullOrWhiteSpace(value))
                {
                    return true;
                }
            }
            return TryGetString(row, field, out value) && !string.IsNullOrWhiteSpace(value);
        }

        /// <summary>
        /// Page count from either envelope shape: <c>pageInfo.pages</c> (requests, users)
        /// or <c>totalPages</c> (watchlists). Unreadable means "one page", which stops
        /// pagination after the current page rather than guessing more.
        /// </summary>
        private static int ReadPageCount(JsonElement root)
        {
            JsonElement pageInfo;
            if (root.TryGetProperty("pageInfo", out pageInfo)
                && pageInfo.ValueKind == JsonValueKind.Object
                && TryGetInt(pageInfo, "pages", out var pages))
            {
                return pages;
            }
            if (TryGetInt(root, "totalPages", out var total)) return total;
            return 1;
        }

        private static bool TryGetArray(JsonElement root, string field, out JsonElement array)
        {
            return root.TryGetProperty(field, out array) && array.ValueKind == JsonValueKind.Array;
        }

        private static bool TryGetInt(JsonElement element, string field, out int value)
        {
            value = 0;
            JsonElement property;
            if (!element.TryGetProperty(field, out property)) return false;
            if (property.ValueKind == JsonValueKind.Number) return property.TryGetInt32(out value);
            if (property.ValueKind == JsonValueKind.String)
            {
                return int.TryParse(property.GetString(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out value);
            }
            return false;
        }

        private static bool TryGetString(JsonElement element, string field, out string value)
        {
            value = null;
            JsonElement property;
            if (!element.TryGetProperty(field, out property)) return false;
            if (property.ValueKind == JsonValueKind.String) value = property.GetString();
            return value != null;
        }

        private async Task<JsonDocument> GetJsonAsync(string url, string apiKey, CancellationToken cancellationToken)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.Add("X-Api-Key", apiKey);
                    using (var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            _logger.Warn("Seerr request failed: {0} -> {1}", RedactUrl(url), (int)response.StatusCode);
                            return null;
                        }
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        return JsonDocument.Parse(body);
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is JsonException || ex is OperationCanceledException)
            {
                // One warn per failed poll is enough; the monitor stands down for the run.
                _logger.Warn("Seerr request to {0} failed: [{1}] {2}", RedactUrl(url), ex.GetType().Name, ex.Message);
                return null;
            }
        }

        /// <summary>The base URL is configuration, not a secret, but the query string can carry
        /// one on servers configured differently — drop it from anything logged.</summary>
        private static string RedactUrl(string url)
        {
            var query = url.IndexOf('?');
            return query >= 0 ? url.Substring(0, query) : url;
        }

        private static string CombineUrl(string baseUrl, string path)
        {
            var trimmed = (baseUrl ?? string.Empty).TrimEnd('/');
            return trimmed + path;
        }
    }
}
