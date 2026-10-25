using System;
using System.Text.RegularExpressions;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>
    /// Name similarity for the Seerr resolution view (ADR-C002 follow-up): the gate matches
    /// movies by TMDB ID, and when the provider does not carry the ID the resolution view has
    /// to propose provider entries by title alone. This is deliberately a proposal tool, not a
    /// matcher: every suggestion is shown to the user with its score and reason, and only a
    /// human click turns it into a review decision.
    /// </summary>
    /// <remarks>
    /// Provider titles arrive decorated in ways TMDB titles never are: language tags
    /// (<c>EN| Name</c>, <c>QC - Name</c>), bracketed tags (<c>[MULTI-SUB]</c>) and year
    /// suffixes (<c>Name (2006)</c>). <see cref="Normalize"/> strips all of that on both sides,
    /// so <c>EN| Step Up</c>, <c>Step Up (2006)</c> and <c>Step Up</c> normalize to the same
    /// string. Symmetric stripping is what makes aggressive stripping safe here: even a name
    /// that IS parenthesized (say <c>(500) Days of Summer</c>) normalizes the same way on both
    /// sides, so it still compares equal to itself.
    /// </remarks>
    internal static class SeerrFuzzyMatch
    {
        // |EN| / ┃XX┃ style tags anywhere in the name.
        private static readonly Regex BoxTagRegex = new Regex(
            @"[\u2503\u2502|][^\u2503\u2502|]+[\u2503\u2502|]",
            RegexOptions.Compiled);

        // "EN - " / "QC - " style two-letter country prefixes.
        private static readonly Regex DashPrefixRegex = new Regex(
            @"^[A-Z]{2}\s+-\s+",
            RegexOptions.Compiled);

        // "EN| " style two-letter language prefixes with a trailing pipe. The pipe-suffix
        // form is not covered by the box-tag pattern above, which needs a pipe on both ends,
        // and it is common enough in provider catalogues to deserve its own rule.
        private static readonly Regex PipePrefixRegex = new Regex(
            @"^\s*[A-Z]{2}\|\s*",
            RegexOptions.Compiled);

        // Bracketed or parenthesized groups: [MULTI-SUB], (2006), (MULTI-AUDIO)...
        private static readonly Regex GroupRegex = new Regex(
            @"\[[^\]]*\]|\([^)]*\)",
            RegexOptions.Compiled);

        private static readonly Regex YearRegex = new Regex(
            @"\((19|20)\d{2}\)",
            RegexOptions.Compiled);

        private static readonly Regex CollapseSpaces = new Regex(
            @"\s{2,}",
            RegexOptions.Compiled);

        /// <summary>
        /// A release year embedded in the title, like the 2006 in <c>Step Up (2006)</c>, or null.
        /// Read before <see cref="Normalize"/> strips the group.
        /// </summary>
        public static int? ExtractYear(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            var match = YearRegex.Match(title);
            if (!match.Success) return null;
            int year;
            if (int.TryParse(match.Value.Trim('(', ')'),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out year))
            {
                return year;
            }
            return null;
        }

        /// <summary>
        /// The comparison form of a title: lower-cased, stripped of language tags, bracketed and
        /// parenthesized groups, with collapsed whitespace. Purely for comparison — never shown
        /// to the user.
        /// </summary>
        public static string Normalize(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return string.Empty;
            var s = BoxTagRegex.Replace(title, " ");
            s = DashPrefixRegex.Replace(s, string.Empty);
            s = PipePrefixRegex.Replace(s, string.Empty);
            s = GroupRegex.Replace(s, " ");
            s = s.ToLowerInvariant();
            s = CollapseSpaces.Replace(s, " ");
            return s.Trim();
        }

        /// <summary>
        /// Similarity of two normalized names, 0 to 100, where 100 is equality. Uses the
        /// Levenshtein ratio (edit distance over the longer string), which tolerates the small
        /// word-level differences providers introduce (punctuation turned to spaces, a missing
        /// "The", subtitle junk the group strip did not catch).
        /// </summary>
        public static int Similarity(string normalizedWant, string normalizedCandidate)
        {
            if (string.IsNullOrEmpty(normalizedWant) || string.IsNullOrEmpty(normalizedCandidate))
            {
                return 0;
            }

            if (string.Equals(normalizedWant, normalizedCandidate, StringComparison.Ordinal))
            {
                return 100;
            }

            var distance = Levenshtein(normalizedWant, normalizedCandidate);
            var longer = Math.Max(normalizedWant.Length, normalizedCandidate.Length);
            return (int)Math.Round(100.0 * (longer - distance) / longer);
        }

        /// <summary>
        /// Scores one candidate for one wanted title. The score drives both the ranking and the
        /// cutoff (<see cref="CandidateThreshold"/>): an exact normalized name is a strong
        /// proposal, a matching release year adds confidence, and a known-but-different year
        /// demotes a same-named remake below the exact-name proposals without hiding it.
        /// </summary>
        public static int Score(string wantTitle, int? wantYear, string candidateName)
        {
            var wantYearEmbedded = ExtractYear(wantTitle);
            var candidateYear = ExtractYear(candidateName);

            var normalizedWant = Normalize(wantTitle);
            var normalizedCandidate = Normalize(candidateName);
            var score = Similarity(normalizedWant, normalizedCandidate);

            // The want's year: Seerr's first-air year is authoritative; the title's embedded
            // year is the fallback (a want title like "Step Up (2006)" carries its own).
            var effectiveWantYear = wantYear ?? wantYearEmbedded;

            var yearsBothKnown = effectiveWantYear.HasValue && candidateYear.HasValue;
            if (yearsBothKnown)
            {
                if (effectiveWantYear.Value == candidateYear.Value)
                {
                    score += 10;
                }
                else
                {
                    score -= 25;
                }
            }

            return score;
        }

        /// <summary>
        /// The minimum score a candidate must reach to be proposed at all. Below this the
        /// provider entry is not similar enough to be worth a human's attention.
        /// </summary>
        public const int CandidateThreshold = 70;

        private static int Levenshtein(string a, string b)
        {
            // Single-row edit distance: O(min) memory, no allocations per cell.
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;

            // b is the shorter string, so the row is as small as possible.
            if (b.Length > a.Length)
            {
                var tmp = a;
                a = b;
                b = tmp;
            }

            var row = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++)
            {
                row[j] = j;
            }

            for (var i = 1; i <= a.Length; i++)
            {
                var previous = row[0];
                row[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var temp = row[j];
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + cost);
                    previous = temp;
                }
            }

            return row[b.Length];
        }
    }
}
