using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Client;
using Emby.Xtream.Plugin.Client.Models;
using MediaBrowser.Model.Logging;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>One provider entry proposed for a wanted title, with why.</summary>
    public sealed class SeerrResolutionCandidate
    {
        /// <summary>The entry's own provider id.</summary>
        public int ProviderId { get; set; }

        /// <summary>
        /// Every provider id the proposal covers: for series, all selected entries that clean to
        /// the same gate name (cross-listed copies collapse to one representative, so reviewing
        /// only one copy could leave the title held); for movies, the single shared stream id.
        /// </summary>
        public int[] ProviderIds { get; set; }

        public string Name { get; set; }
        public int[] Categories { get; set; }
        public string CategoryNames { get; set; }

        /// <summary>True when the entry sits in one of the categories the sync fetches.</summary>
        public bool InSelectedCategories { get; set; }

        /// <summary>True when the entry carries the wanted title's TMDB ID. Movies only.</summary>
        public bool ExactTmdb { get; set; }

        /// <summary>The fuzzy score (see <see cref="SeerrFuzzyMatch.Score"/>), for display.</summary>
        public int Score { get; set; }
    }

    /// <summary>One wanted title with no automatic match, and the provider entries that might be it.</summary>
    public sealed class SeerrResolutionRow
    {
        public int TmdbId { get; set; }
        public string Title { get; set; }
        public string ReleaseDate { get; set; }
        public SeerrResolutionCandidate[] Candidates { get; set; }
    }

    /// <summary>
    /// Builds the Seerr resolution view (ADR-C002 follow-up): for every title the user asked
    /// for that the review gate cannot match automatically (no provider TMDB ID for movies, no
    /// name match for series), list the provider entries that fuzzy-match by title so a human
    /// can review the right one with a click.
    /// <para>
    /// Titles that have not been released yet are excluded up front: they cannot be in a
    /// provider catalogue, so proposing candidates for them is noise (and the gate will pick
    /// them up the normal way once the provider carries them).
    /// </para>
    /// <para>
    /// Candidates already carrying an exact TMDB match outside the selected categories are
    /// surfaced with <see cref="SeerrResolutionCandidate.InSelectedCategories"/> false: the
    /// remedy there is selecting the category, not reviewing the entry, because the sync
    /// never fetches what it is not told to.
    /// </para>
    /// <para>
    /// Internal rather than public on purpose: Emby's SimpleInjector scan instantiates public
    /// service classes before <see cref="Plugin.Instance"/> exists.
    /// </para>
    /// </summary>
    internal sealed class SeerrResolutionService
    {
        private const int MaxCandidatesPerRow = 5;

        // The catalogue is fetched for the whole provider (not just selected categories: the
        // exact-ID-outside-selection hint needs it), so it is worth caching across view loads.
        private static readonly TimeSpan CatalogueCacheTtl = TimeSpan.FromMinutes(5);

        private readonly ILogger _logger;
        private readonly SeerrMonitor _monitor;
        private readonly SeerrClient _seerrClient;
        private readonly HttpClient _catalogueClient;

        private readonly object _cacheLock = new object();
        private DateTime _catalogueCachedAtUtc = DateTime.MinValue;
        private List<VodStreamInfo> _movieCatalogue;
        private List<SeriesInfo> _seriesCatalogue;
        private Dictionary<int, string> _movieCategoryNames;
        private Dictionary<int, string> _seriesCategoryNames;

        public SeerrResolutionService(
            ILogger logger,
            SeerrMonitor monitor,
            SeerrClient seerrClient,
            HttpClient catalogueClient)
        {
            _logger = logger;
            _monitor = monitor;
            _seerrClient = seerrClient;
            _catalogueClient = catalogueClient;
        }

        /// <summary>
        /// Resolution rows for one media type ("movie" or "series"). Read-only: the review
        /// decision itself is made in the dashboard and saved through the ordinary settings
        /// save, exactly like the de-dup view's reviewed toggle, so the decision store stays
        /// the single writer (ADR-C001).
        /// </summary>
        internal async Task<List<SeerrResolutionRow>> GetResolutionsAsync(
            PluginConfiguration config, string mediaType, Func<string, string> normalizeSeriesName,
            HashSet<int> reviewedIds, CancellationToken cancellationToken)
        {
            var wantSeries = string.Equals(mediaType, "series", StringComparison.OrdinalIgnoreCase);
            var rows = new List<SeerrResolutionRow>();
            if (!SeerrMonitor.IsConfigured(config))
            {
                return rows;
            }

            var snapshot = await _monitor.GetSnapshotAsync(config, normalizeSeriesName, cancellationToken).ConfigureAwait(false);
            var wants = snapshot.Wanted.Where(w => w != null && w.IsSeries == wantSeries).ToList();
            if (wants.Count == 0)
            {
                return rows;
            }

            await LoadCataloguesAsync(config, cancellationToken).ConfigureAwait(false);

            var today = DateTime.UtcNow.Date;
            var selectedCategories = wantSeries
                ? new HashSet<int>(config.SelectedSeriesCategoryIds ?? new int[0])
                : new HashSet<int>(config.SelectedVodCategoryIds ?? new int[0]);
            var categoryNames = wantSeries ? _seriesCategoryNames : _movieCategoryNames;

            foreach (var want in wants)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Canonical title and release date. The monitor's poll already resolved both
                // for series request rows (they carry no title of their own), so this fetch is
                // only needed for movies and for rows the poll could not enrich.
                SeerrWantedItem detail = want;
                if (string.IsNullOrWhiteSpace(detail.Title) || string.IsNullOrWhiteSpace(detail.ReleaseDate))
                {
                    detail = want.IsSeries
                        ? await _seerrClient.GetTvTitleAsync(config.SeerrUrl, config.SeerrApiKey, want.TmdbId, cancellationToken).ConfigureAwait(false)
                        : await _seerrClient.GetMovieTitleAsync(config.SeerrUrl, config.SeerrApiKey, want.TmdbId, cancellationToken).ConfigureAwait(false)
                        ?? want;
                }

                if (detail == null || string.IsNullOrWhiteSpace(detail.Title))
                {
                    _logger.Debug("Seerr resolution: could not read the details of tmdb {0}, skipping", want.TmdbId);
                    continue;
                }

                // Not released yet: no provider can carry it, and the gate will handle it when
                // one does. This is the filter that keeps the view free of future releases.
                if (IsFutureDate(detail.ReleaseDate, today))
                {
                    continue;
                }

                var wantYear = detail.FirstAirYear ?? SeerrFuzzyMatch.ExtractYear(detail.Title);
                var candidates = wantSeries
                    ? BuildSeriesCandidates(want, detail, wantYear, selectedCategories, categoryNames, reviewedIds, normalizeSeriesName)
                    : BuildMovieCandidates(want, detail, wantYear, selectedCategories, categoryNames, reviewedIds);
                if (candidates.Count == 0)
                {
                    // Matched, or nothing plausible to propose. Either way not a resolution row.
                    continue;
                }

                rows.Add(new SeerrResolutionRow
                {
                    TmdbId = want.TmdbId,
                    Title = detail.Title,
                    ReleaseDate = detail.ReleaseDate,
                    Candidates = candidates.Take(MaxCandidatesPerRow).ToArray(),
                });
            }

            return rows.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private List<SeerrResolutionCandidate> BuildMovieCandidates(
            SeerrWantedItem want, SeerrWantedItem detail, int? wantYear,
            HashSet<int> selectedCategories, Dictionary<int, string> categoryNames,
            HashSet<int> reviewedIds)
        {
            var exactMatches = new List<SeerrResolutionCandidate>();
            var fuzzy = new List<SeerrResolutionCandidate>();
            var matchedInSelection = false;

            foreach (var stream in _movieCatalogue)
            {
                if (stream == null) continue;

                var exactTmdb = TryParseTmdb(stream.TmdbId) == want.TmdbId;
                if (exactTmdb && IsSelected(stream.CategoryId, selectedCategories))
                {
                    // The gate will auto-review this on the next sync; nothing to resolve.
                    matchedInSelection = true;
                    break;
                }

                if (reviewedIds.Contains(stream.StreamId))
                {
                    // Already reviewed by hand or by the gate: it syncs; proposing it again
                    // would read as unresolved.
                    continue;
                }

                var inSelected = IsSelected(stream.CategoryId, selectedCategories);
                if (exactTmdb)
                {
                    // The exact ID, in a category the sync does not fetch. Surfaced so the
                    // user can enable the category; reviewing the entry would be useless.
                    exactMatches.Add(CreateCandidate(stream, categoryNames, inSelected, exactTmdb: true, score: 110));
                    continue;
                }

                if (!inSelected)
                {
                    // Fuzzy proposals are only worth reviewing when the sync will fetch them.
                    continue;
                }

                var score = SeerrFuzzyMatch.Score(detail.Title, wantYear, stream.Name);
                if (score >= SeerrFuzzyMatch.CandidateThreshold)
                {
                    fuzzy.Add(CreateCandidate(stream, categoryNames, inSelected, exactTmdb: false, score: score));
                }
            }

            if (matchedInSelection)
            {
                return new List<SeerrResolutionCandidate>();
            }

            return exactMatches.Concat(fuzzy.OrderByDescending(c => c.Score)).ToList();
        }

        private List<SeerrResolutionCandidate> BuildSeriesCandidates(
            SeerrWantedItem want, SeerrWantedItem detail, int? wantYear,
            HashSet<int> selectedCategories, Dictionary<int, string> categoryNames,
            HashSet<int> reviewedIds, Func<string, string> normalizeSeriesName)
        {
            var fuzzy = new Dictionary<string, SeerrResolutionCandidate>(StringComparer.OrdinalIgnoreCase);
            var normalizedWant = SeerrFuzzyMatch.Normalize(detail.Title);

            foreach (var series in _seriesCatalogue)
            {
                if (series == null) continue;
                if (!IsSelected(series.CategoryId, selectedCategories)) continue;
                if (reviewedIds.Contains(series.SeriesId)) continue;

                // Gate-equivalent match: the same normalized name, with the year rules from
                // ADR-C002. If the gate will auto-review this entry, the want is resolved.
                var normalizedCandidate = SeerrFuzzyMatch.Normalize(series.Name);
                if (string.Equals(normalizedWant, normalizedCandidate, StringComparison.Ordinal)
                    && YearsAgree(wantYear, SeerrFuzzyMatch.ExtractYear(series.Name)))
                {
                    return new List<SeerrResolutionCandidate>();
                }

                var score = SeerrFuzzyMatch.Score(detail.Title, wantYear, series.Name);
                if (score < SeerrFuzzyMatch.CandidateThreshold)
                {
                    continue;
                }

                // Cross-listed copies collapse to one representative at sync time, so a
                // proposal must carry every copy's id or reviewing it can leave the title
                // held under the representative the sync actually picks. The group key is
                // the gate's own series name, the same string the collapse uses.
                var groupKey = normalizeSeriesName(series.Name ?? string.Empty);
                SeerrResolutionCandidate candidate;
                if (!fuzzy.TryGetValue(groupKey, out candidate))
                {
                    candidate = new SeerrResolutionCandidate
                    {
                        ProviderId = series.SeriesId,
                        ProviderIds = new[] { series.SeriesId },
                        Name = series.Name,
                        Categories = series.CategoryId.HasValue ? new[] { series.CategoryId.Value } : new int[0],
                        CategoryNames = CategoryNamesFor(series.CategoryId, categoryNames),
                        InSelectedCategories = true,
                        ExactTmdb = false,
                        Score = score,
                    };
                    fuzzy[groupKey] = candidate;
                }
                else
                {
                    var ids = candidate.ProviderIds.ToList();
                    if (!ids.Contains(series.SeriesId))
                    {
                        ids.Add(series.SeriesId);
                        candidate.ProviderIds = ids.ToArray();
                    }
                }
            }

            return fuzzy.Values.OrderByDescending(c => c.Score).ToList();
        }

        private static bool YearsAgree(int? wantYear, int? candidateYear)
        {
            // A year in parentheses on the provider entry only counts as the first-air year
            // when the want's year is known (the same rule the gate applies).
            return !wantYear.HasValue || !candidateYear.HasValue || wantYear.Value == candidateYear.Value;
        }

        private SeerrResolutionCandidate CreateCandidate(
            VodStreamInfo stream, Dictionary<int, string> categoryNames, bool inSelected,
            bool exactTmdb, int score)
        {
            return new SeerrResolutionCandidate
            {
                ProviderId = stream.StreamId,
                ProviderIds = new[] { stream.StreamId },
                Name = stream.Name,
                Categories = stream.CategoryId.HasValue ? new[] { stream.CategoryId.Value } : new int[0],
                CategoryNames = CategoryNamesFor(stream.CategoryId, categoryNames),
                InSelectedCategories = inSelected,
                ExactTmdb = exactTmdb,
                Score = score,
            };
        }

        private static string CategoryNamesFor(int? categoryId, Dictionary<int, string> categoryNames)
        {
            if (!categoryId.HasValue) return string.Empty;
            string name;
            if (categoryNames != null && categoryNames.TryGetValue(categoryId.Value, out name))
            {
                return name;
            }
            return categoryId.Value.ToString(CultureInfo.InvariantCulture);
        }

        private static bool IsSelected(int? categoryId, HashSet<int> selectedCategories)
        {
            // An empty selection means "all categories", the same rule the sync applies.
            if (selectedCategories.Count == 0) return true;
            return categoryId.HasValue && selectedCategories.Contains(categoryId.Value);
        }

        private static int? TryParseTmdb(string raw)
        {
            int value;
            if (!string.IsNullOrWhiteSpace(raw)
                && int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value)
                && value > 0)
            {
                return value;
            }
            return null;
        }

        private static bool IsFutureDate(string isoDate, DateTime todayUtc)
        {
            DateTime parsed;
            if (string.IsNullOrWhiteSpace(isoDate)) return false;
            if (DateTime.TryParse(isoDate, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed))
            {
                return parsed.Date > todayUtc;
            }
            return false;
        }

        private async Task LoadCataloguesAsync(PluginConfiguration config, CancellationToken cancellationToken)
        {
            lock (_cacheLock)
            {
                if (_movieCatalogue != null && DateTime.UtcNow - _catalogueCachedAtUtc < CatalogueCacheTtl)
                {
                    return;
                }
            }

            var movies = await FetchCatalogueAsync<List<VodStreamInfo>>(config, "get_vod_streams", cancellationToken).ConfigureAwait(false)
                ?? new List<VodStreamInfo>();
            var series = await FetchCatalogueAsync<List<SeriesInfo>>(config, "get_series", cancellationToken).ConfigureAwait(false)
                ?? new List<SeriesInfo>();
            var movieCats = await FetchCatalogueAsync<List<Category>>(config, "get_vod_categories", cancellationToken).ConfigureAwait(false);
            var seriesCats = await FetchCatalogueAsync<List<Category>>(config, "get_series_categories", cancellationToken).ConfigureAwait(false);

            lock (_cacheLock)
            {
                _movieCatalogue = movies;
                _seriesCatalogue = series;
                _movieCategoryNames = (movieCats ?? new List<Category>())
                    .Where(c => c != null).ToDictionary(c => c.CategoryId, c => c.CategoryName ?? string.Empty);
                _seriesCategoryNames = (seriesCats ?? new List<Category>())
                    .Where(c => c != null).ToDictionary(c => c.CategoryId, c => c.CategoryName ?? string.Empty);
                _catalogueCachedAtUtc = DateTime.UtcNow;
            }
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
            PropertyNameCaseInsensitive = true,
        };

        private async Task<T> FetchCatalogueAsync<T>(PluginConfiguration config, string action, CancellationToken cancellationToken)
        {
            try
            {
                var url = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}/player_api.php?username={1}&password={2}&action={3}",
                    (config.BaseUrl ?? string.Empty).TrimEnd('/'),
                    Uri.EscapeDataString(config.Username),
                    Uri.EscapeDataString(config.Password),
                    action);
                var json = await _catalogueClient.GetStringAsync(url).ConfigureAwait(false);
                return JsonSerializer.Deserialize<T>(json, JsonOptions);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is JsonException || ex is OperationCanceledException)
            {
                _logger.Warn("Seerr resolution: could not fetch {0} from the provider: {1}", action, ex.Message);
                return default(T);
            }
        }
    }
}
