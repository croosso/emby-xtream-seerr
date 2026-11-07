using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// ADR-C007: a movie missing from a complete catalogue keeps its files until it has been
    /// missing for the whole grace period.
    ///
    /// The real case: Dispatcharr moves a merged movie's relations onto fresh duplicate rows on
    /// every nightly refresh and merges them back over the day, so a movie can vanish from the
    /// categories the plugin reads for hours and return unchanged. Each of those runs used to
    /// delete it, and the next one wrote it again.
    ///
    /// Every test seeds files on disk first: a test that starts empty passes trivially.
    /// </summary>
    public class OrphanGracePeriodTests : SyncTestBase
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 6, 5, 42, 0, DateTimeKind.Utc);

        private string RecordsRoot => Path.Combine(TempDir.Path, "records");

        private string LedgerPath => Path.Combine(RecordsRoot, StrmSyncService.OrphanGraceLedgerFileName);

        private string MovieStrmPath(string folder, string file = null)
            => Path.Combine(TempDir.Path, "Movies", folder, (file ?? folder) + ".strm");

        private string Seed(string folder, string content, string file = null)
        {
            var path = MovieStrmPath(folder, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
            return path;
        }

        private static string XtreamUrl(int streamId) => "http://fake-xtream/movie/user/pass/" + streamId + ".mkv";

        private Emby.Xtream.Plugin.PluginConfiguration GraceConfig(int hours = 24)
        {
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            config.OrphanGracePeriodHours = hours;
            config.RecordsPath = RecordsRoot;
            config.DispatcharrUrl = "http://fake-dispatcharr";
            return config;
        }

        /// <summary>Queues the same catalogue for <paramref name="times"/> syncs.</summary>
        private void Catalogue(int times, params object[] streams)
        {
            var json = VodStreamsJson(streams);
            Handler.RespondWithSequence("get_vod_streams", Enumerable.Repeat(json, times).ToArray());
        }

        private Task SyncAt(Emby.Xtream.Plugin.PluginConfiguration config, DateTime now)
        {
            var svc = MakeService();
            svc.UtcNow = () => now;
            return svc.SyncMoviesAsync(config, None, SaveConfig);
        }

        [Fact]
        public async Task MissingMovie_IsKeptOnTheFirstSyncThatMissesIt()
        {
            var config = GraceConfig();
            Catalogue(1, VodStream(streamId: 1, name: "Kept Movie"));
            var main = Seed("Eternity (2025)", XtreamUrl(99));
            var version = Seed("Eternity (2025)", "http://fake-dispatcharr/proxy/vod/movie/abc?stream_id=7",
                "Eternity (2025) - Version 2");

            await SyncAt(config, T0);

            Assert.True(File.Exists(main), "one sync without the title is not proof it is gone");
            Assert.True(File.Exists(version), "its Dispatcharr version entries wait with it");
            var ledger = File.ReadAllText(LedgerPath);
            Assert.Contains("Eternity (2025) - Version 2.strm", ledger);
            Assert.Contains("2026-11-06T05:42:00Z", ledger);
        }

        [Fact]
        public async Task MissingMovie_IsDeletedOnceTheGracePeriodHasElapsed()
        {
            var config = GraceConfig(hours: 24);
            Catalogue(3, VodStream(streamId: 1, name: "Kept Movie"));
            var gone = Seed("Gone Movie", XtreamUrl(99));

            await SyncAt(config, T0);
            await SyncAt(config, T0.AddHours(23));
            Assert.True(File.Exists(gone), "23 hours missing is still inside a 24 hour grace period");

            await SyncAt(config, T0.AddHours(24));
            Assert.False(File.Exists(gone), "missing for the whole grace period, so it is a real removal");
            Assert.DoesNotContain("Gone Movie", File.ReadAllText(LedgerPath));
        }

        [Fact]
        public async Task MovieThatComesBack_StartsAFreshGracePeriodNextTimeItGoes()
        {
            var config = GraceConfig(hours: 24);
            var kept = VodStream(streamId: 1, name: "Kept Movie");
            var flapping = VodStream(streamId: 99, name: "Flapping Movie");
            Handler.RespondWithSequence("get_vod_streams", new[]
            {
                VodStreamsJson(kept),
                VodStreamsJson(kept, flapping),
                VodStreamsJson(kept),
                VodStreamsJson(kept),
            });
            var path = Seed("Flapping Movie", XtreamUrl(99));

            await SyncAt(config, T0);                 // missing: clock starts
            await SyncAt(config, T0.AddHours(20));    // back: clock cleared
            await SyncAt(config, T0.AddHours(30));    // missing again: clock restarts here
            Assert.True(File.Exists(path), "the first absence must not count once the title came back");

            await SyncAt(config, T0.AddHours(54));    // 24 hours after it went the second time
            Assert.False(File.Exists(path));
        }

        [Fact]
        public async Task MovedMovie_OldFolderIsDeletedImmediately()
        {
            // The provider renamed the title, or folder naming changed: the same stream is
            // written under a new folder. Waiting would show both folders in Emby for a day.
            var config = GraceConfig(hours: 24);
            Catalogue(1, VodStream(streamId: 5, name: "New Name"));
            var old = Seed("Old Name", XtreamUrl(5));

            await SyncAt(config, T0);

            Assert.True(File.Exists(MovieStrmPath("New Name")));
            Assert.False(File.Exists(old), "the run wrote this URL elsewhere, so the old file was moved, not lost");
            Assert.DoesNotContain("Old Name", File.ReadAllText(LedgerPath));
        }

        [Fact]
        public async Task ZeroGracePeriod_DeletesOnTheFirstSyncAsBefore()
        {
            var config = GraceConfig(hours: 0);
            Catalogue(1, VodStream(streamId: 1, name: "Kept Movie"));
            var gone = Seed("Gone Movie", XtreamUrl(99));

            await SyncAt(config, T0);

            Assert.False(File.Exists(gone));
        }

        [Fact]
        public async Task UnreadableLedger_RestartsTheClockInsteadOfDeleting()
        {
            var config = GraceConfig(hours: 24);
            Catalogue(2, VodStream(streamId: 1, name: "Kept Movie"));
            var gone = Seed("Gone Movie", XtreamUrl(99));

            await SyncAt(config, T0);
            File.WriteAllText(LedgerPath, "{ not json");
            await SyncAt(config, T0.AddHours(48));

            Assert.True(File.Exists(gone), "a damaged ledger may only delay a deletion, never cause one");
            Assert.Contains("Gone Movie", File.ReadAllText(LedgerPath));
            Assert.Contains("2026-11-08T05:42:00Z", File.ReadAllText(LedgerPath));
        }

        [Fact]
        public async Task FutureFirstMissingTime_IsNotTrusted()
        {
            var config = GraceConfig(hours: 24);
            Catalogue(1, VodStream(streamId: 1, name: "Kept Movie"));
            var gone = Seed("Gone Movie", XtreamUrl(99));
            Directory.CreateDirectory(RecordsRoot);
            File.WriteAllText(LedgerPath,
                "{\"Version\":1,\"Pending\":{\"Gone Movie/Gone Movie.strm\":\"2030-01-01T00:00:00Z\"}}");

            await SyncAt(config, T0);

            Assert.True(File.Exists(gone));
            Assert.Contains("2026-11-06T05:42:00Z", File.ReadAllText(LedgerPath));
        }

        [Fact]
        public async Task NoRecordsRoot_KeepsMissingFilesRatherThanDeletingThem()
        {
            var config = GraceConfig(hours: 24);
            config.RecordsPath = null; // and no plugin instance to derive one from
            Catalogue(1, VodStream(streamId: 1, name: "Kept Movie"));
            var gone = Seed("Gone Movie", XtreamUrl(99));

            await SyncAt(config, T0.AddDays(30));

            Assert.True(File.Exists(gone), "without a ledger the grace period cannot be measured, so nothing absent is due");
        }

        [Fact]
        public async Task ForeignStrm_IsNeverTrackedOrDeleted()
        {
            var config = GraceConfig(hours: 24);
            Catalogue(2, VodStream(streamId: 1, name: "Kept Movie"));
            var foreign = Seed("Home Video", "http://my-nas/home-video.mkv");

            await SyncAt(config, T0);
            await SyncAt(config, T0.AddDays(10));

            Assert.True(File.Exists(foreign));
            Assert.DoesNotContain("Home Video", File.ReadAllText(LedgerPath));
        }
    }
}
