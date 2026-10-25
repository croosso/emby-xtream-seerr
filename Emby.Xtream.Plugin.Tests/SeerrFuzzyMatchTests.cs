using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Tests for the title comparison the Seerr resolution view proposes with
    /// (<see cref="SeerrFuzzyMatch"/>). The normalization has to absorb every decoration
    /// providers add that TMDB titles never carry, or the exact title a user asked for
    /// shows up as a low-scoring stranger.
    /// </summary>
    public class SeerrFuzzyMatchTests
    {
        [Theory]
        [InlineData("EN| Step Up", "step up")]
        [InlineData("QC - Indomptables (2026) (CA)", "indomptables")]
        [InlineData("Step Up (2006)", "step up")]
        [InlineData("Sweet Tooth [MULTI-SUB]", "sweet tooth")]
        [InlineData("Midnight Asia Eat · Dance · Dream (2022)", "midnight asia eat · dance · dream")]
        [InlineData("(500) Days of Summer", "days of summer")]
        [InlineData("  Multiple   Spaces  ", "multiple spaces")]
        public void Normalize_StripsProviderDecorations(string providerTitle, string expected)
        {
            Assert.Equal(expected, SeerrFuzzyMatch.Normalize(providerTitle));
        }

        [Fact]
        public void Normalize_IsSymmetricForTMDBTitles()
        {
            // A TMDB title goes through the same pipeline, so even a name that is entirely
            // parenthesized compares equal to its provider-decorated form.
            Assert.Equal(
                SeerrFuzzyMatch.Normalize("(500) Days of Summer"),
                SeerrFuzzyMatch.Normalize("EN| (500) Days of Summer (2009)"));
        }

        [Fact]
        public void ExtractYear_ReadsTheParenthesizedYear()
        {
            Assert.Equal(2006, SeerrFuzzyMatch.ExtractYear("Step Up (2006)"));
            Assert.Null(SeerrFuzzyMatch.ExtractYear("Step Up"));
            Assert.Null(SeerrFuzzyMatch.ExtractYear("Alien: Earth"));
        }

        [Fact]
        public void Similarity_IsHundredForEqualStrings()
        {
            Assert.Equal(100, SeerrFuzzyMatch.Similarity("step up", "step up"));
        }

        [Fact]
        public void Similarity_ToleratesSmallDifferences()
        {
            // One substitution in a nine-character name.
            var score = SeerrFuzzyMatch.Similarity("step up 2", "step ups 2");
            Assert.InRange(score, 80, 99);
        }

        [Fact]
        public void Similarity_IsLowForUnrelatedNames()
        {
            // Not zero: the edit-distance ratio of two unrelated strings depends on their
            // lengths. What matters is that it stays far below the candidate threshold.
            var score = SeerrFuzzyMatch.Similarity("step up", "baby gorilla cam");
            Assert.InRange(score, 0, 60);
        }

        [Fact]
        public void Score_BoostsAMatchingYear()
        {
            var withYear = SeerrFuzzyMatch.Score("Step Up", 2006, "Step Up (2006)");
            var withoutYear = SeerrFuzzyMatch.Score("Step Up", 2006, "Step Up (2008)");
            Assert.Equal(110, withYear);
            // A same-named remake from a different year is demoted, not hidden.
            Assert.Equal(75, withoutYear);
        }

        [Fact]
        public void Score_UsesTheTitleYearWhenSeerrHasNone()
        {
            // Movies carry their year in the want title when the detail fetch had none.
            Assert.Equal(110, SeerrFuzzyMatch.Score("Step Up (2006)", null, "Step Up (2006)"));
        }

        [Fact]
        public void Threshold_HidesUnrelatedTitles()
        {
            Assert.True(SeerrFuzzyMatch.Score("Step Up", 2006, "Baby Gorilla Cam")
                < SeerrFuzzyMatch.CandidateThreshold);
        }
    }
}
