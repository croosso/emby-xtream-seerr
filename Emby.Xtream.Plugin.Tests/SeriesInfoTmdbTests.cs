using System.Collections.Generic;
using System.Text.Json;
using Emby.Xtream.Plugin.Client.Models;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// The series listing's TMDB id: Xtream panels send <c>tmdb</c>, Dispatcharr sends
    /// <c>tmdb_id</c>. Parsed with default options on purpose: not every caller registers the
    /// tolerant converters, so the property must carry them itself.
    /// </summary>
    public class SeriesInfoTmdbTests
    {
        private static SeriesInfo Parse(string fields)
            => JsonSerializer.Deserialize<List<SeriesInfo>>("[{\"series_id\":1,\"name\":\"Show\"" + fields + "}]")[0];

        [Fact]
        public void ReadsDispatcharrsTmdbIdField()
        {
            Assert.Equal("1399", Parse(",\"tmdb_id\":\"1399\"").TmdbId);
        }

        [Fact]
        public void ReadsThePanelsTmdbField()
        {
            Assert.Equal("1399", Parse(",\"tmdb\":\"1399\"").TmdbId);
        }

        [Fact]
        public void PrefersThePanelsFieldWhenBothArePresent()
        {
            Assert.Equal("1399", Parse(",\"tmdb_id\":\"42\",\"tmdb\":\"1399\"").TmdbId);
        }

        [Fact]
        public void FallsBackToTmdbIdWhenThePanelsFieldIsEmpty()
        {
            Assert.Equal("42", Parse(",\"tmdb\":\"\",\"tmdb_id\":\"42\"").TmdbId);
        }

        [Fact]
        public void ANumericOrNullIdDoesNotAbortTheList()
        {
            Assert.Equal("42", Parse(",\"tmdb_id\":42").TmdbId);
            Assert.Equal(string.Empty, Parse(",\"tmdb_id\":null,\"tmdb\":null").TmdbId);
        }

        [Fact]
        public void NoIdAtAllIsEmpty()
        {
            Assert.Equal(string.Empty, Parse(string.Empty).TmdbId);
        }
    }
}
