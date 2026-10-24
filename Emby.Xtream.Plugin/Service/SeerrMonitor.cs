using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Client;
using MediaBrowser.Model.Logging;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>
    /// The monitored titles pulled from Seerr for one sync run (ADR-C002):
    /// a TMDB-ID set for movies and a normalized-name map for series.
    /// </summary>
    /// <remarks>
    /// Series carry no TMDB ID on the provider's <c>get_series</c> list payload (measured
    /// 0 of 9,979), so a series cannot be recognized by ID at gate time. The names here go
    /// through the same clean-and-sanitize pipeline the gate applies to provider titles,
    /// so both sides land on the same string.
    /// <para>
    /// Providers tag the same series two ways: a language prefix (<c>EN| Name</c>, handled
    /// by the cleaning pipeline) and a first-air-year suffix (<c>Name (2008)</c>, kept by
    /// it). The map therefore carries the watched series' known first-air year (from
    /// Seerr's TMDB proxy), and <see cref="MatchesSeries"/> accepts a year-suffixed
    /// provider name only when the suffix equals that year — so a same-named remake with
    /// a different year does not over-match. A watched series with no known year matches
    /// its exact name only.
    /// </para>
    /// </remarks>
    internal sealed class SeerrMonitoredSnapshot
    {
        private static readonly System.Text.RegularExpressions.Regex YearSuffixRegex = new System.Text.RegularExpressions.Regex(
            @"^(.+?)\s+\((\d{4})\)$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        public static readonly SeerrMonitoredSnapshot Empty = Create(
            new HashSet<int>(), new Dictionary<string, HashSet<int>>(0, StringComparer.OrdinalIgnoreCase));

        public HashSet<int> MovieTmdbIds { get; private set; }

        /// <summary>Normalized series name → known first-air years; an empty set means the year is unknown.</summary>
        public Dictionary<string, HashSet<int>> SeriesByYear { get; private set; }

        public int SeriesNameCount { get { return SeriesByYear.Count; } }

        private SeerrMonitoredSnapshot(HashSet<int> movieTmdbIds, Dictionary<string, HashSet<int>> seriesByYear)
        {
            MovieTmdbIds = movieTmdbIds;
            SeriesByYear = seriesByYear;
        }

        private static SeerrMonitoredSnapshot Create(HashSet<int> movieTmdbIds, Dictionary<string, HashSet<int>> seriesByYear)
        {
            return new SeerrMonitoredSnapshot(movieTmdbIds, seriesByYear);
        }

        /// <summary>
        /// Does a held series match a watched one? Exact normalized name always matches;
        /// a name suffixed with <c>(YYYY)</c> matches only when the base name is watched
        /// and the year is its known first-air year.
        /// </summary>
        public bool MatchesSeries(string normalizedSeriesName)
        {
            if (string.IsNullOrEmpty(normalizedSeriesName) || SeriesByYear.Count == 0) return false;
            if (SeriesByYear.ContainsKey(normalizedSeriesName)) return true;

            var match = YearSuffixRegex.Match(normalizedSeriesName);
            if (!match.Success) return false;

            HashSet<int> years;
            if (!SeriesByYear.TryGetValue(match.Groups[1].Value.Trim(), out years)) return false;
            int year;
            return int.TryParse(match.Groups[2].Value, out year) && years.Contains(year);
        }

        public static SeerrMonitoredSnapshot Build(IEnumerable<SeerrWantedItem> items, Func<string, string> normalizeSeriesName)
        {
            var movies = new HashSet<int>();
            var series = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                if (item == null || item.TmdbId <= 0) continue;
                if (item.IsSeries)
                {
                    if (string.IsNullOrWhiteSpace(item.Title)) continue;
                    var normalized = normalizeSeriesName(item.Title);
                    if (string.IsNullOrWhiteSpace(normalized)) continue;
                    HashSet<int> years;
                    if (!series.TryGetValue(normalized, out years))
                    {
                        years = new HashSet<int>();
                        series[normalized] = years;
                    }
                    if (item.FirstAirYear.HasValue) years.Add(item.FirstAirYear.Value);
                }
                else
                {
                    movies.Add(item.TmdbId);
                }
            }
            return Create(movies, series);
        }
    }

    /// <summary>
    /// Pulls the user's Seerr requests and watchlists into a monitored snapshot for the
    /// review gates (ADR-C002). Pull, not webhook: every plugin route is admin-only and
    /// Seerr cannot authenticate as an Emby admin, and the moment that matters is the
    /// sync discovering the title in the provider catalogue — not a request event.
    /// <para>
    /// The snapshot is derived data with an authoritative owner (Seerr), so it is cached in
    /// memory with a short TTL and never persisted — the opposite of the ADR-C001 analysis
    /// for decisions, which are unrecoverable user intent. A failed poll stands the
    /// monitor down for that run: the gate holds everything, which is its pre-existing,
    /// reversible behavior. The monitor never fails the sync.
    /// </para>
    /// <para>
    /// Internal rather than public on purpose: Emby's SimpleInjector scan instantiates public
    /// service classes before <see cref="Plugin.Instance"/> exists.
    /// </para>
    /// </summary>
    internal sealed class SeerrMonitor
    {
        // A back-to-back movie and series sync should poll Seerr once, not twice.
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

        private readonly ILogger _logger;
        private readonly SeerrClient _client;
        private readonly object _cacheLock = new object();
        private SeerrMonitoredSnapshot _cached;
        private DateTime _cachedAtUtc = DateTime.MinValue;

        public SeerrMonitor(ILogger logger, SeerrClient client)
        {
            _logger = logger;
            _client = client;
        }

        /// <summary>
        /// True when the configuration asks for monitoring. The caller (a review gate)
        /// already knows the gate is on; this is the monitor's own half of the check.
        /// </summary>
        internal static bool IsConfigured(PluginConfiguration config)
        {
            return config.EnableSeerrMonitor
                && !string.IsNullOrWhiteSpace(config.SeerrUrl)
                && !string.IsNullOrWhiteSpace(config.SeerrApiKey);
        }

        /// <summary>
        /// The monitored snapshot for this sync run. Returns <see cref="SeerrMonitoredSnapshot.Empty"/>
        /// when monitoring is off or the poll fails — never blocks a sync on Seerr.
        /// </summary>
        internal async Task<SeerrMonitoredSnapshot> GetSnapshotAsync(
            PluginConfiguration config, Func<string, string> normalizeSeriesName, CancellationToken cancellationToken)
        {
            if (!IsConfigured(config))
            {
                return SeerrMonitoredSnapshot.Empty;
            }

            lock (_cacheLock)
            {
                if (_cached != null && DateTime.UtcNow - _cachedAtUtc < CacheTtl)
                {
                    return _cached;
                }
            }

            var snapshot = await PollAsync(config, normalizeSeriesName, cancellationToken).ConfigureAwait(false);
            if (snapshot != null)
            {
                lock (_cacheLock)
                {
                    _cached = snapshot;
                    _cachedAtUtc = DateTime.UtcNow;
                }
            }
            // A failed poll is not cached: standing down is for one run only, and the next
            // sync retries rather than serving an empty snapshot for the TTL.
            return snapshot ?? SeerrMonitoredSnapshot.Empty;
        }

        private async Task<SeerrMonitoredSnapshot> PollAsync(
            PluginConfiguration config, Func<string, string> normalizeSeriesName, CancellationToken cancellationToken)
        {
            var url = config.SeerrUrl;
            var apiKey = config.SeerrApiKey;
            try
            {
                var userIds = await _client.GetUserIdsAsync(url, apiKey, cancellationToken).ConfigureAwait(false);
                var items = await _client.GetRequestsAsync(url, apiKey, cancellationToken).ConfigureAwait(false);
                var requestCount = items.Count;
                foreach (var userId in userIds)
                {
                    var watchlist = await _client.GetWatchlistAsync(url, apiKey, userId, cancellationToken).ConfigureAwait(false);
                    items.AddRange(watchlist);
                }

                // Request rows carry no title (measured on a live instance: only
                // media.tmdbId), and a series matches by name. Look the name and first-air
                // year up from Seerr's TMDB proxy once per distinct series ID; a row that
                // still has no name is skipped rather than matched on nothing. Watchlist
                // rows carry their title and skip this fetch (their year stays unknown).
                var tvTitlesById = new Dictionary<int, SeerrWantedItem>();
                foreach (var item in items)
                {
                    if (!item.IsSeries || !string.IsNullOrWhiteSpace(item.Title)) continue;
                    SeerrWantedItem fetched;
                    if (!tvTitlesById.TryGetValue(item.TmdbId, out fetched))
                    {
                        fetched = await _client.GetTvTitleAsync(url, apiKey, item.TmdbId, cancellationToken).ConfigureAwait(false);
                        tvTitlesById[item.TmdbId] = fetched;
                        if (fetched == null || string.IsNullOrWhiteSpace(fetched.Title))
                        {
                            _logger.Warn(
                                "Seerr monitor: could not read the name of series tmdb {0} — it will not match this run",
                                item.TmdbId);
                        }
                    }
                    if (fetched != null)
                    {
                        item.Title = fetched.Title;
                        item.FirstAirYear = fetched.FirstAirYear;
                    }
                }

                var snapshot = SeerrMonitoredSnapshot.Build(items, normalizeSeriesName);
                _logger.Info(
                    "Seerr monitor: watching {0} movie id(s) and {1} series name(s) — {2} request row(s) and {3} watchlist row(s) from {4} user(s)",
                    snapshot.MovieTmdbIds.Count, snapshot.SeriesNameCount,
                    requestCount, items.Count - requestCount, userIds.Count);
                return snapshot;
            }
            catch (Exception ex)
            {
                // Only exceptions PollAsync itself can throw (e.g. a null client); the HTTP
                // layer reports failures as warnings and empty lists. Either way the monitor
                // stands down for this run rather than failing the sync.
                _logger.Warn("Seerr monitor poll failed: [{0}] {1}", ex.GetType().Name, ex.Message);
                return null;
            }
        }
    }
}
