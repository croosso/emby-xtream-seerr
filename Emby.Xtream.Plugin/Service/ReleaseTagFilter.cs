using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Client.Models;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>
    /// Drops pre-release copies (telesync, cam, screener...) by the tag the provider puts in the
    /// name (ADR-C014).
    ///
    /// Dispatcharr gives a merged movie one name, taken from one of its relations, and that name
    /// goes stale as relations move between rows: "The Odyssey - 2026 [HDTS]" can hold untagged
    /// copies only. So with Dispatcharr each version is judged by its own provider name, and the
    /// title's name only decides when there is nothing better to go on.
    ///
    /// Deliberately a static class with no constructor (see CLAUDE.md on SimpleInjector).
    /// </summary>
    internal static class ReleaseTagFilter
    {
        /// <summary>
        /// The default pattern: a bracketed or parenthesized pre-release tag, e.g. "[HDTS]",
        /// "(TELESYNC)", "[HDCAM]", "(CAM)". Only bracketed tags, so a title that merely
        /// contains the letters ("Cats", "TS Eliot") never matches.
        /// </summary>
        public const string DefaultPattern =
            @"[\[(]\s*(HD[\s.-]?TS|TS|TELESYNC|HD[\s.-]?CAM|CAM|CAMRIP|HD[\s.-]?TC|TC|TELECINE|SCR|SCREENER|DVDSCR)\s*[\])]";

        private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// The configured filter, or null when it is off. An invalid pattern also yields null,
        /// with the parse error in <paramref name="error"/>: a broken pattern must not drop the
        /// whole catalogue, and it must not silently drop nothing either without saying so.
        /// </summary>
        public static Regex Build(PluginConfiguration config, out string error)
        {
            error = null;
            if (config == null || !config.EnableReleaseTagFilter)
            {
                return null;
            }

            var pattern = string.IsNullOrWhiteSpace(config.ReleaseTagFilterPattern)
                ? DefaultPattern
                : config.ReleaseTagFilterPattern;
            try
            {
                return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
            }
            catch (ArgumentException ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>True when the name carries a pre-release tag. A null filter matches nothing.</summary>
        public static bool IsTagged(Regex filter, string name)
        {
            if (filter == null || string.IsNullOrEmpty(name))
            {
                return false;
            }

            try
            {
                return filter.IsMatch(name);
            }
            catch (RegexMatchTimeoutException)
            {
                // A pathological user pattern: keep the title rather than guess.
                return false;
            }
        }

        /// <summary>
        /// The versions to keep. A version whose provider name is unknown is kept: the absence
        /// of a name is not evidence of a pre-release copy.
        /// </summary>
        public static List<DispatcharrVodProvider> KeepVersions(Regex filter, IEnumerable<DispatcharrVodProvider> providers)
        {
            return (providers ?? Enumerable.Empty<DispatcharrVodProvider>())
                .Where(p => p != null && !IsTagged(filter, p.ProviderName))
                .ToList();
        }

        /// <summary>
        /// Whether a whole title is filtered out. Without Dispatcharr (<paramref name="fetchVersions"/>
        /// null) the title's name decides. With it, a tagged title is filtered only when every
        /// version is tagged too; a title with no tag in its name is left to the per-version
        /// check at write time, so the catalogue costs one Dispatcharr call per tagged title, not
        /// one per title.
        ///
        /// Fails open: when the versions cannot be read (an exception or an empty answer, since a
        /// listed movie always has at least one relation) the title is kept and the reason is
        /// passed to <paramref name="onLookupFailed"/>.
        /// </summary>
        public static async Task<bool> IsTitleFilteredAsync(
            Regex filter,
            string titleName,
            Func<Task<List<DispatcharrVodProvider>>> fetchVersions,
            Action<string> onLookupFailed = null)
        {
            if (!IsTagged(filter, titleName))
            {
                return false;
            }

            if (fetchVersions == null)
            {
                return true;
            }

            List<DispatcharrVodProvider> versions;
            try
            {
                versions = await fetchVersions().ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                onLookupFailed?.Invoke(ex.Message);
                return false;
            }

            if (versions == null || versions.Count == 0)
            {
                onLookupFailed?.Invoke("Dispatcharr returned no versions");
                return false;
            }

            return KeepVersions(filter, versions).Count == 0;
        }
    }
}
