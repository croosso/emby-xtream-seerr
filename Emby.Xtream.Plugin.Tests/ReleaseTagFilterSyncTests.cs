using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// The pre-release filter inside the movie sync (ADR-C014): titles dropped by name without
    /// Dispatcharr, versions dropped by their own provider name with it, and the files of a
    /// dropped title removed like an exclusion's.
    /// </summary>
    public class ReleaseTagFilterSyncTests : SyncTestBase
    {
        private const string ProvidersUrl = "api/vod/movies/1/providers/";

        private void RegisterVodStreams(string json) => Handler.RespondWith("get_vod_streams", json);

        private string MovieDir(string folderName) => Path.Combine(TempDir.Path, "Movies", folderName);

        private static string VersionUrl(int streamId)
            => "http://fake-dispatcharr/proxy/vod/movie/movie-uuid?stream_id=" + streamId;

        private static string ProvidersJson(params (int StreamId, string Name)[] versions)
            => "[" + string.Join(",", versions.Select((v, i) => string.Format(
                CultureInfo.InvariantCulture,
                "{{\"id\":{0},\"stream_id\":{1},\"custom_properties\":{{\"basic_data\":{{\"name\":\"{2}\"}}}}}}",
                i + 1, v.StreamId, v.Name))) + "]";

        /// <summary>
        /// Dispatcharr for movie 1. <paramref name="providerCalls"/> is how many times the sync
        /// asks for the versions: once at write time, plus once at filter time for a title
        /// whose own name is tagged. Providers are registered before the detail rule, whose
        /// URL is a prefix of theirs (FakeHttpHandler matches in registration order).
        /// </summary>
        private void EnableDispatcharr(PluginConfiguration config, string providersJson, int providerCalls)
        {
            config.EnableDispatcharr = true;
            config.DispatcharrUrl = "http://fake-dispatcharr";
            config.DispatcharrUser = "admin";
            config.DispatcharrPass = "secret";
            Handler.RespondWith("/api/accounts/token/", "{\"access\":\"tok\",\"refresh\":\"ref\"}");
            Handler.RespondWithSequence(ProvidersUrl, Enumerable.Repeat(providersJson, providerCalls));
            Handler.RespondWith("api/vod/movies/1/", "{\"id\":1,\"uuid\":\"movie-uuid\"}");
        }

        private static PluginConfiguration WithFilter(PluginConfiguration config)
        {
            config.EnableReleaseTagFilter = true;
            return config;
        }

        private static string PlantOwnedMovie(string dir, string folderName, string url)
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, folderName + ".strm");
            File.WriteAllText(path, url);
            return path;
        }

        [Fact]
        public async Task FilterOff_ATaggedTitleIsWrittenAsBefore()
        {
            var config = DefaultConfig();
            RegisterVodStreams(VodStreamsJson(VodStream(streamId: 1, name: "Some Film [HDTS]")));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.True(Directory.Exists(MovieDir("Some Film [HDTS]")));
        }

        [Fact]
        public async Task WithoutDispatcharr_ATaggedTitleIsLeftOutAndItsFilesRemoved()
        {
            var config = WithFilter(DefaultConfig());
            var oldDir = MovieDir("Some Film [HDTS]");
            var planted = PlantOwnedMovie(oldDir, "Some Film [HDTS]", "http://fake-xtream/movie/user/pass/1.mkv");

            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 1, name: "Some Film [HDTS]"),
                VodStream(streamId: 2, name: "Other Film")));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.False(File.Exists(planted), "a left-out title's own files leave the library");
            Assert.True(File.Exists(Path.Combine(MovieDir("Other Film"), "Other Film.strm")));
        }

        [Fact]
        public async Task WithDispatcharr_ATaggedTitleKeepsItsCleanVersion()
        {
            // The Odyssey case: the merged movie is named "[HDTS]" but one version is clean.
            // The clean one is written as a proxy entry, even alone: the plain Xtream URL would
            // let Dispatcharr pick the telesync by account priority.
            var config = WithFilter(DefaultConfig());
            EnableDispatcharr(config,
                ProvidersJson((11, "EN - Some Film (TELESYNC) (2026)"), (12, "EN - Some Film (2026)")),
                providerCalls: 2);
            RegisterVodStreams(VodStreamsJson(VodStream(streamId: 1, name: "Some Film [HDTS]")));

            var svc = MakeService();
            svc.DispatcharrHandler = Handler;
            await svc.SyncMoviesAsync(config, None, SaveConfig);

            var dir = MovieDir("Some Film [HDTS]");
            Assert.Equal(new[] { Path.Combine(dir, "Some Film [HDTS].strm") }, Directory.GetFiles(dir, "*.strm"));
            Assert.Equal(VersionUrl(12), File.ReadAllText(Path.Combine(dir, "Some Film [HDTS].strm")));
        }

        [Fact]
        public async Task WithDispatcharr_TaggedVersionsAreDroppedAndTheRestRenumbered()
        {
            var config = WithFilter(DefaultConfig());
            EnableDispatcharr(config,
                ProvidersJson((11, "EN - Some Film (2026)"), (12, "EN - Some Film (CAM) (2026)"), (13, "4K-TOP - Some Film (2026)")),
                providerCalls: 1);
            RegisterVodStreams(VodStreamsJson(VodStream(streamId: 1, name: "Some Film")));

            var svc = MakeService();
            svc.DispatcharrHandler = Handler;
            await svc.SyncMoviesAsync(config, None, SaveConfig);

            var dir = MovieDir("Some Film");
            Assert.Equal(VersionUrl(11), File.ReadAllText(Path.Combine(dir, "Some Film.strm")));
            Assert.Equal(VersionUrl(13), File.ReadAllText(Path.Combine(dir, "Some Film - Version 2.strm")));
            Assert.Equal(2, Directory.GetFiles(dir, "*.strm").Length);
        }

        [Fact]
        public async Task WithDispatcharr_EveryVersionTaggedLeavesTheTitleOut()
        {
            // The title's own name is clean, so only the write-time check can see it.
            var config = WithFilter(DefaultConfig());
            var dir = MovieDir("Some Film");
            var planted = PlantOwnedMovie(dir, "Some Film", "http://fake-xtream/movie/user/pass/1.mkv");
            EnableDispatcharr(config,
                ProvidersJson((11, "EN - Some Film (TELESYNC) (2026)"), (12, "FR - Some Film [HDCAM]")),
                providerCalls: 1);
            RegisterVodStreams(VodStreamsJson(VodStream(streamId: 1, name: "Some Film")));

            var svc = MakeService();
            svc.DispatcharrHandler = Handler;
            await svc.SyncMoviesAsync(config, None, SaveConfig);

            Assert.False(File.Exists(planted), "a title with only pre-release copies leaves the library");
        }

        [Fact]
        public async Task WithDispatcharr_ATaggedTitleWithOnlyTaggedVersionsIsLeftOutBeforeTheWrite()
        {
            var config = WithFilter(DefaultConfig());
            EnableDispatcharr(config, ProvidersJson((11, "EN| Some Film - 2026 [HDTS]")), providerCalls: 1);
            RegisterVodStreams(VodStreamsJson(VodStream(streamId: 1, name: "EN| Some Film - 2026 [HDTS]")));

            var svc = MakeService();
            svc.DispatcharrHandler = Handler;
            await svc.SyncMoviesAsync(config, None, SaveConfig);

            Assert.False(Directory.Exists(Path.Combine(TempDir.Path, "Movies")) &&
                         Directory.GetFiles(Path.Combine(TempDir.Path, "Movies"), "*.strm", SearchOption.AllDirectories).Any());
        }
    }
}
