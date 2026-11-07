using System;
using System.Text.RegularExpressions;

namespace Emby.Xtream.Plugin.Service
{
    public static class ContentNameCleaner
    {
        private static readonly char[] LineSeparators = new[] { '\n', '\r' };

        // Matches one or more ┃XX┃ / │XX│ / |XX| prefix tags at the start of a string.
        // Handles: ┃UK┃, ┃UK ┃, ┃ UK┃, │EN│, |FR|, with optional whitespace around them.
        // U+2503 = ┃ (heavy vertical), U+2502 = │ (light vertical), | = pipe
        private static readonly Regex BoxPrefixRegex = new Regex(
            @"^(\s*[\u2503\u2502|][^\u2503\u2502|]+[\u2503\u2502|]\s*)+",
            RegexOptions.Compiled);

        // Matches ┃XX┃ / │XX│ / |XX| tags anywhere in the string (not just prefix).
        private static readonly Regex BoxTagAnywhereRegex = new Regex(
            @"\s*[\u2503\u2502|][^\u2503\u2502|]+[\u2503\u2502|]\s*",
            RegexOptions.Compiled);

        // Matches a dash-style prefix label: a 2-letter uppercase country code ("EN - ", "US - "),
        // or a quality label that starts with a resolution token, optionally followed by
        // hyphenated provider/language/HDR tags ("4K - ", "FHD - ", "4K-NF - ", "4K-FR-HDR - ",
        // "4K-D+ - "). Other letter runs are left alone so show acronyms that providers format
        // as "FBI - Most Wanted" or "NCIS - Los Angeles" keep their name (ADR-C009).
        // Applied once per call (single prefix strip).
        private static readonly Regex DashPrefixRegex = new Regex(
            @"^(?:[A-Z]{2}|(?:[48]K|UHD|FHD|HD|SD)(?:-[A-Z0-9+]+)*)\s+-\s+",
            RegexOptions.Compiled);

        // Provider quality tags in square brackets: [MULTI-SUB], [MULTI-SUB-AUDIO], [MULTI-AUDIO].
        // Matches only ALL-CAPS letters/digits containing at least one hyphen, so a bracketed
        // word that IS the title (the film "[REC]") keeps its brackets. Stripping these lets the
        // review gate and the Seerr monitor recognize a tagged variant as its plain-named title
        // (ADR-C005): "YAGA [MULTI-SUB]" cleans to "YAGA" and matches a Seerr request for YAGA.
        private static readonly Regex BracketQualityTagRegex = new Regex(
            @"\s*\[[A-Z0-9 -]*-[A-Z0-9 -]*\]\s*",
            RegexOptions.Compiled);

        private static readonly Regex MultipleSpacesRegex = new Regex(
            @"\s{2,}",
            RegexOptions.Compiled);

        /// <summary>
        /// Cleans content names (movie/series titles) by removing country-code prefix
        /// tags like ┃UK┃, │EN│, |FR| and user-specified additional terms.
        /// </summary>
        public static string CleanContentName(string name, string userRemoveTerms = null, bool enableCleaning = true)
        {
            if (string.IsNullOrWhiteSpace(name)) return name;
            if (!enableCleaning) return name.Trim();

            string result = name;

            // Remove ┃XX┃ style prefix tags (country codes, labels)
            result = BoxPrefixRegex.Replace(result, string.Empty);

            // Also remove any remaining ┃XX┃ tags in the middle/end of the string
            result = BoxTagAnywhereRegex.Replace(result, " ");

            // Remove plain dash-style prefix labels like "EN - ", "4K - ", "4K-NF - "
            result = DashPrefixRegex.Replace(result, string.Empty);

            // Remove square-bracketed quality tags like "[MULTI-SUB]", anywhere in the name.
            result = BracketQualityTagRegex.Replace(result, " ");

            // Remove user-specified terms (one per line)
            if (!string.IsNullOrWhiteSpace(userRemoveTerms))
            {
                var lines = userRemoveTerms.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var term = line.Trim();
                    if (!string.IsNullOrEmpty(term))
                    {
                        int idx;
                        while ((idx = result.IndexOf(term, StringComparison.OrdinalIgnoreCase)) >= 0)
                        {
                            result = result.Remove(idx, term.Length);
                        }
                    }
                }
            }

            result = MultipleSpacesRegex.Replace(result, " ");
            result = result.Trim();

            return string.IsNullOrWhiteSpace(result) ? name.Trim() : result;
        }
    }
}
