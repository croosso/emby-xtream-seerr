using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// ADR-C013: excluding one of two titles that share a folder name. With TMDB folder naming
    /// each has its own "Name [tmdbid=N]" folder; the exclusion pass used to index only the first
    /// folder per name, so it could miss the excluded title's folder entirely or pick the other.
    ///
    /// Orphan cleanup is off in every test, so whatever is removed was removed by the exclusion
    /// pass itself.
    /// </summary>
    public class ExclusionSameNameTests : SyncTestBase
    {
        private void RegisterVodStreams(string json) => Handler.RespondWith("get_vod_streams", json);

        private string Seed(string folder, int streamId)
        {
            var path = Path.Combine(TempDir.Path, "Movies", folder, folder + ".strm");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "http://fake-xtream/movie/user/pass/" + streamId + ".mkv");
            return Path.GetDirectoryName(path);
        }

        private Emby.Xtream.Plugin.PluginConfiguration Config()
        {
            var config = DefaultConfig();
            config.EnableContentNameCleaning = true;
            config.EnableTmdbFolderNaming = true;
            config.CleanupOrphans = false;
            config.ExcludedVodStreamIds = new[] { 2 };
            return config;
        }

        [Fact]
        public async Task ExcludingOneOfTwoSameNameTitles_RemovesItsOwnFolderAndKeepsTheOther()
        {
            var kept = Seed("Shared Name (2025) [tmdbid=111]", 1);
            var excluded = Seed("Shared Name (2025) [tmdbid=222]", 2);
            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 1, name: "Shared Name (2025)", added: 1000, tmdbId: "111"),
                VodStream(streamId: 2, name: "4K - Shared Name (2025)", added: 1000, tmdbId: "222")));

            await MakeService().SyncMoviesAsync(Config(), None, SaveConfig);

            Assert.True(Directory.Exists(kept), "the included title's folder must stay");
            Assert.False(Directory.Exists(excluded), "the excluded title's own folder must go, whatever order the folders list in");
        }

        [Fact]
        public async Task ExcludingATitle_LeavesASameNameFolderOfAnotherTmdbTitle_EvenWhenThatTitleIsMissing()
        {
            // The other title is not in this run's catalogue (a provider hiccup), so nothing marks
            // its folder as written. Its TMDB id says it is not the excluded title.
            var other = Seed("Shared Name (2025) [tmdbid=111]", 1);
            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 2, name: "4K - Shared Name (2025)", added: 1000, tmdbId: "222")));

            await MakeService().SyncMoviesAsync(Config(), None, SaveConfig);

            Assert.True(Directory.Exists(other));
        }

        [Fact]
        public async Task ExcludingATitle_StillRemovesAnUntaggedFolderWithItsName()
        {
            // Written while folder naming was off: the name is all there is to go on, as before.
            var config = Config();
            config.EnableTmdbFolderNaming = false;
            var untagged = Seed("Shared Name (2025)", 2);
            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 2, name: "4K - Shared Name (2025)", added: 1000, tmdbId: "222")));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.False(Directory.Exists(untagged));
        }
    }
}
