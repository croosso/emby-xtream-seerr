using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Service;
using Emby.Xtream.Plugin.Tests.Fakes;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Pins the observable edges of the delete paths and their record keeping: the log
    /// lines that are the only witness of a guard firing, the exact retention boundaries
    /// of every prune, the sorted-oldest-first pruning order, and the failed-item
    /// recording that protects a folder an exclusion would otherwise delete.
    /// </summary>
    public class CleanupRecordKeepingTests : SyncTestBase
    {
        private string MovieStrmPath(string movieName)
            => Path.Combine(TempDir.Path, "Movies", movieName, movieName + ".strm");

        private string SeedMovieStrm(string movieName, string url = "http://fake-xtream/movie/user/pass/9999.mkv")
        {
            var path = MovieStrmPath(movieName);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, url);
            return path;
        }

        private string SeedEpisode(string show, string fileName,
            string url = "http://fake-xtream/series/user/pass/101.mp4", string season = "Season 01")
        {
            var path = Path.Combine(TempDir.Path, "Shows", show, season, fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, url);
            return path;
        }

        private void RegisterMovies(params object[] streams)
            => Handler.RespondWith("get_vod_streams", VodStreamsJson(streams));

        private string RecordDir()
        {
            var dir = Path.Combine(TempDir.Path, "records");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string[] Records(string dir)
            => Directory.GetFiles(dir, "xtream-deleted-*.txt");

        private StrmSyncService ServiceWithRecords(string recordDir, RecordingLogger logger)
            => new StrmSyncService(logger, HttpClient) { DeletionRecordDirectory = recordDir };

        private static string EmptyDetail(int seriesId) =>
            "{\"info\":{\"series_id\":" + seriesId + ",\"name\":\"Test Show\",\"tmdb\":\"\"}," +
            "\"seasons\":[],\"episodes\":{}}";

        // -----------------------------------------------------------------
        // The exclusion pass reports what it did, and stays silent when it did nothing
        // -----------------------------------------------------------------

        [Fact]
        public async Task Movie_Exclusion_ReportsRemovedFolders()
        {
            var config = DefaultConfig();
            config.ExcludedVodStreamIds = new[] { 2 };
            SeedMovieStrm("Gone Movie");
            RegisterMovies(
                VodStream(streamId: 1, name: "Kept Movie", added: 1000),
                VodStream(streamId: 2, name: "Gone Movie", added: 1000));

            var logger = new RecordingLogger();
            await new StrmSyncService(logger, HttpClient).SyncMoviesAsync(config, None, SaveConfig);

            Assert.False(File.Exists(MovieStrmPath("Gone Movie")));
            Assert.Contains(logger.Infos, i => i.Contains("Removed 1 folder(s) for explicitly excluded items under Movies"));
        }

        [Fact]
        public async Task Movie_Exclusion_IsSilentWhenNothingWasRemoved()
        {
            var config = DefaultConfig();
            config.ExcludedVodStreamIds = new[] { 2 };
            RegisterMovies(
                VodStream(streamId: 1, name: "Kept Movie", added: 1000),
                VodStream(streamId: 2, name: "Never On Disk", added: 1000));

            var logger = new RecordingLogger();
            await new StrmSyncService(logger, HttpClient).SyncMoviesAsync(config, None, SaveConfig);

            Assert.DoesNotContain(logger.Infos, i => i.Contains("excluded items under Movies"));
            Assert.DoesNotContain(logger.Infos, i => i.Contains("Kept "));
        }

        // -----------------------------------------------------------------
        // The empty-catalogue refusal only fires when files actually exist
        // -----------------------------------------------------------------

        [Fact]
        public async Task Cleanup_EmptyCatalogueWithFilesOnDisk_RefusesAndWarns()
        {
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            SeedMovieStrm("Old Movie");
            RegisterMovies();

            var logger = new RecordingLogger();
            var svc = new StrmSyncService(logger, HttpClient);
            await svc.SyncMoviesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(MovieStrmPath("Old Movie")));
            Assert.Contains(logger.Warnings, w => w.Contains("refusing to treat an empty catalogue as a deletion"));
        }

        [Fact]
        public async Task Cleanup_EmptyCatalogueWithNothingOnDisk_IsQuiet()
        {
            // The refusal guard must not fire when there is nothing to protect: a library
            // that is genuinely empty is not a provider outage. The Movies root has to
            // exist for the cleanup to walk far enough to evaluate the guard at all.
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            Directory.CreateDirectory(Path.Combine(TempDir.Path, "Movies"));
            RegisterMovies();

            var logger = new RecordingLogger();
            var svc = new StrmSyncService(logger, HttpClient);
            await svc.SyncMoviesAsync(config, None, SaveConfig);

            Assert.True(Directory.Exists(Path.Combine(TempDir.Path, "Movies")));
            Assert.Empty(logger.Warnings);
            Assert.DoesNotContain(logger.Infos, i => i.Contains("orphaned STRM files"));
        }

        [Fact]
        public async Task Movie_ExclusionOfAnUnplaceableItem_SkipsItWithoutFailingTheSync()
        {
            // An excluded entry whose category has no folder mapping (multiple mode) has
            // nowhere to look, and an entry whose name sanitizes away entirely has no
            // folder to match. Both are skipped, not crashed on: one strange catalogue
            // entry must never take the whole exclusion pass down.
            var config = DefaultConfig();
            config.MovieFolderMode = "multiple";
            config.SelectedVodCategoryIds = new[] { 1 };
            config.ExcludedVodStreamIds = new[] { 2, 3 };
            SeedMovieStrm("Normal Movie");
            Handler.RespondWith("get_vod_streams",
                VodStreamsJson(new { stream_id = 1, name = "Normal Movie", added = 1000 },
                    new { stream_id = 2, name = "?::?", added = 1000, category_id = (int?)null }));
            Handler.RespondWith("get_vod_categories",
                "[{\"category_id\":1,\"category_name\":\"One\"}]");

            var logger = new RecordingLogger();
            var svc = new StrmSyncService(logger, HttpClient);
            await svc.SyncMoviesAsync(config, None, SaveConfig);

            Assert.Equal(0, svc.MovieProgress.Failed);
            Assert.True(File.Exists(MovieStrmPath("Normal Movie")));
        }

        // -----------------------------------------------------------------
        // Foreign files are counted and named; non-STRM files are neither
        // -----------------------------------------------------------------

        [Fact]
        public async Task Cleanup_ForeignStrm_IsCountedLoggedAndKept()
        {
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            RegisterMovies(VodStream(streamId: 1, name: "Kept Movie", added: 1000));
            var foreign = SeedMovieStrm("Foreign Movie", "http://somewhere-else/movie.mkv");

            var logger = new RecordingLogger();
            var svc = new StrmSyncService(logger, HttpClient);
            await svc.SyncMoviesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(foreign));
            Assert.Contains(logger.Infos, i => i.Contains("leaving 1 STRM file(s)"));
        }

        [Fact]
        public async Task Cleanup_NonStrmFiles_AreNotCountedAsForeignStrms()
        {
            // A poster beside the streams is not a foreign STRM and must not be reported
            // as one. This is also what separates the "*.strm" search from a wildcard:
            // a mutation that widens the search counts the jpg as a foreign STRM.
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            RegisterMovies(VodStream(streamId: 1, name: "Kept Movie", added: 1000));
            var poster = Path.Combine(TempDir.Path, "Movies", "Kept Movie", "poster.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(poster));
            File.WriteAllText(poster, "not a strm");

            var logger = new RecordingLogger();
            var svc = new StrmSyncService(logger, HttpClient);
            await svc.SyncMoviesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(poster));
            Assert.DoesNotContain(logger.Infos, i => i.Contains("leaving"));
            Assert.DoesNotContain(logger.Infos, i => i.Contains("orphaned STRM files"));
        }

        [Fact]
        public async Task Cleanup_ReportsTheRemovedCount()
        {
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            RegisterMovies(VodStream(streamId: 1, name: "Kept Movie", added: 1000));
            SeedMovieStrm("Gone Movie");

            var logger = new RecordingLogger();
            var svc = new StrmSyncService(logger, HttpClient);
            await svc.SyncMoviesAsync(config, None, SaveConfig);

            Assert.False(File.Exists(MovieStrmPath("Gone Movie")));
            Assert.Contains(logger.Infos, i => i.Contains("Removed 1 orphaned STRM files"));
        }

        // -----------------------------------------------------------------
        // Pruning empty parents stops where foreign content remains
        // -----------------------------------------------------------------

        [Fact]
        public async Task Cleanup_KeepsAFolderThatStillHoldsForeignContent()
        {
            // The empty-parent prune walks up from a deleted orphan. A folder that still
            // holds someone else's file must stop the walk there: pushing past it means
            // attempting a delete of a non-empty directory, which fails noisily.
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            Handler.RespondWith("action=get_series_info&series_id=1",
                SeriesDetailJson(seriesId: 1, seasonNum: 1, episodeNum: 1, title: "One"));
            Handler.RespondWith("action=get_series",
                SeriesListJson(Series(seriesId: 1, name: "Kept Show", lastModified: "2000")));
            var orphan = SeedEpisode("Show", "Show - S01E01.strm", "http://fake-xtream/series/user/pass/55.mp4");
            var trailer = Path.Combine(TempDir.Path, "Shows", "Show", "trailer.jpg");
            File.WriteAllText(trailer, "not a strm");

            var logger = new RecordingLogger();
            var svc = new StrmSyncService(logger, HttpClient);
            await svc.SyncSeriesAsync(config, None, SaveConfig);

            Assert.False(File.Exists(orphan));
            Assert.True(File.Exists(trailer), "The show folder still holds a foreign file and must survive");
            Assert.True(Directory.Exists(Path.Combine(TempDir.Path, "Shows", "Show")));
            Assert.False(Directory.Exists(Path.Combine(TempDir.Path, "Shows", "Show", "Season 01")),
                "The emptied season folder is pruned");
            Assert.DoesNotContain(logger.Debugs, d => d.Contains("Failed to cleanup orphan"));
        }

        // -----------------------------------------------------------------
        // The deleted-path sample and record
        // -----------------------------------------------------------------

        [Fact]
        public async Task Cleanup_AtExactlyTheSampleSize_WritesNoRecordAndNoFullListNote()
        {
            // Fifteen is what the log line carries in full; the record exists for the runs
            // that go past it. At exactly fifteen the log IS the record.
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            RegisterMovies(VodStream(streamId: 1, name: "Kept Movie", added: 1000));
            for (var i = 0; i < 15; i++)
            {
                SeedMovieStrm(string.Format("Gone Movie {0:D2}", i));
            }

            var dir = RecordDir();
            var logger = new RecordingLogger();
            await ServiceWithRecords(dir, logger).SyncMoviesAsync(config, None, SaveConfig);

            Assert.Empty(Records(dir));
            Assert.DoesNotContain(logger.Infos, i => i.Contains("full list"));
        }

        [Fact]
        public async Task Cleanup_PastTheSample_TheLogCarriesTheSortedHeadAndNamesTheRecord()
        {
            // The sample is the alphabetically-first fifteen, not whichever fifteen the
            // filesystem enumerated first, so the log line is the same on every run.
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            RegisterMovies(VodStream(streamId: 1, name: "Kept Movie", added: 1000));
            // Created in reverse name order, so filesystem enumeration order is not
            // alphabetical wherever the platform returns creation order.
            for (var i = 19; i >= 0; i--)
            {
                SeedMovieStrm(string.Format("Gone Movie {0:D2}", i));
            }

            var dir = RecordDir();
            var logger = new RecordingLogger();
            await ServiceWithRecords(dir, logger).SyncMoviesAsync(config, None, SaveConfig);

            var summary = logger.Infos.Single(i => i.Contains("orphaned STRM files"));
            Assert.Contains("Gone Movie 00/Gone Movie 00.strm", summary);
            Assert.DoesNotContain("Gone Movie 19/", summary);

            var record = Records(dir).Single();
            Assert.EndsWith("-Movies.txt", Path.GetFileName(record));
            var lines = File.ReadAllLines(record);
            Assert.Contains("20 file(s) removed from", lines[0]);
            Assert.Contains("Paths are relative to the root", lines[1]);
        }

        [Fact]
        public async Task Cleanup_WithoutADeletionRecordDirectory_SaysSoAndDoesNotWarn()
        {
            // The record directory is normally Emby's log folder; in a context where it
            // cannot be resolved the record is simply unwritten. That is a notice in the
            // log line, not a warning: nothing is wrong.
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            RegisterMovies(VodStream(streamId: 1, name: "Kept Movie", added: 1000));
            for (var i = 0; i < 20; i++)
            {
                SeedMovieStrm(string.Format("Gone Movie {0:D2}", i));
            }

            var logger = new RecordingLogger();
            // No DeletionRecordDirectory: the service has to resolve it, and in tests it cannot.
            await new StrmSyncService(logger, HttpClient).SyncMoviesAsync(config, None, SaveConfig);

            Assert.Contains(logger.Infos, i => i.Contains("could not be written"));
            Assert.DoesNotContain(logger.Warnings, w => w.Contains("deleted-path record"));
        }

        [Fact]
        public async Task Cleanup_CreatesTheRecordDirectoryWhenItDoesNotExist()
        {
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            RegisterMovies(VodStream(streamId: 1, name: "Kept Movie", added: 1000));
            for (var i = 0; i < 20; i++)
            {
                SeedMovieStrm(string.Format("Gone Movie {0:D2}", i));
            }

            var dir = Path.Combine(TempDir.Path, "never-created");
            var logger = new RecordingLogger();
            await ServiceWithRecords(dir, logger).SyncMoviesAsync(config, None, SaveConfig);

            Assert.Single(Records(dir));
        }

        [Fact]
        public async Task Record_Prune_AtExactlyTenKeepsEverything()
        {
            // Ten is the retention. At exactly ten there is nothing to prune, and the
            // new record is an eleventh: pruning at the boundary would eat the record
            // that was just written on the previous run.
            var dir = RecordDir();
            // Nine seeds plus the one this run writes makes ten at prune time.
            for (var i = 1; i <= 9; i++)
            {
                File.WriteAllText(Path.Combine(dir,
                    string.Format("xtream-deleted-202501{0:D2}-000000-Movies.txt", i)), "seed");
            }

            var config = DefaultConfig();
            config.CleanupOrphans = true;
            RegisterMovies(VodStream(streamId: 1, name: "Kept Movie", added: 1000));
            for (var i = 0; i < 20; i++)
            {
                SeedMovieStrm(string.Format("Gone Movie {0:D2}", i));
            }

            var logger = new RecordingLogger();
            await ServiceWithRecords(dir, logger).SyncMoviesAsync(config, None, SaveConfig);

            Assert.Equal(10, Records(dir).Length);
        }

        [Fact]
        public async Task Record_Prune_DeletesTheOldestByName()
        {
            // Retention is by name sort, which is chronological because the timestamp
            // leads the filename. If the sort ever stops happening the prune deletes
            // whichever records the filesystem happened to list first.
            var dir = RecordDir();
            // Created largest-name first, so creation order is reverse of name order.
            for (var i = 12; i >= 1; i--)
            {
                File.WriteAllText(Path.Combine(dir,
                    string.Format("xtream-deleted-202501{0:D2}-000000-Movies.txt", i)), "seed");
            }

            var config = DefaultConfig();
            config.CleanupOrphans = true;
            RegisterMovies(VodStream(streamId: 1, name: "Kept Movie", added: 1000));
            for (var i = 0; i < 20; i++)
            {
                SeedMovieStrm(string.Format("Gone Movie {0:D2}", i));
            }

            var logger = new RecordingLogger();
            await ServiceWithRecords(dir, logger).SyncMoviesAsync(config, None, SaveConfig);

            var after = Records(dir).Select(Path.GetFileName).ToList();
            Assert.Equal(10, after.Count);
            Assert.DoesNotContain(after, f => f.Contains("20250101"));
            Assert.DoesNotContain(after, f => f.Contains("20250102"));
            Assert.DoesNotContain(after, f => f.Contains("20250103"));
            Assert.Contains(after, f => f.Contains("20250104"));
        }

        // -----------------------------------------------------------------
        // Configuration rollback copies (ADR-F005 mechanism 3)
        // -----------------------------------------------------------------

        private string SeedConfigFile(string contents = "<PluginConfiguration><A>1</A></PluginConfiguration>")
        {
            var path = Path.Combine(TempDir.Path, "cfg", "Emby.Xtream.Plugin.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, contents);
            return path;
        }

        private string RollbackDir() => Path.Combine(TempDir.Path, "cfg", "xtream-rollback");

        private string[] Rollbacks()
            => Directory.Exists(RollbackDir())
                ? Directory.GetFiles(RollbackDir(), "*.xml")
                : new string[0];

        private StrmSyncService ServiceWithRollback(string configPath, RecordingLogger logger)
            => new StrmSyncService(logger, HttpClient) { ConfigRollbackSourcePath = configPath };

        [Fact]
        public async Task Rollback_MissingSource_IsQuietNotAWarn()
        {
            var config = DefaultConfig();
            RegisterMovies(VodStream(streamId: 1, name: "Kept Movie", added: 1000));

            var logger = new RecordingLogger();
            var svc = ServiceWithRollback(Path.Combine(TempDir.Path, "nope", "missing.xml"), logger);
            await svc.SyncMoviesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(MovieStrmPath("Kept Movie")));
            Assert.Empty(logger.Warnings);
        }

        [Fact]
        public async Task Rollback_TheCopyIsNamedByItsTimestamp()
        {
            // The prune that keeps these bounded sorts by name and relies on the name
            // being the timestamp. A copy without the stamp would sort arbitrarily and
            // the retention would delete the wrong ones.
            var config = DefaultConfig();
            var cfgPath = SeedConfigFile();
            RegisterMovies(VodStream(streamId: 1, name: "Kept Movie", added: 1000));

            var logger = new RecordingLogger();
            await ServiceWithRollback(cfgPath, logger).SyncMoviesAsync(config, None, SaveConfig);

            var copy = Rollbacks().Single();
            Assert.Matches(new Regex(@"^\d{8}-\d{6}-\d{3}\.xml$"), Path.GetFileName(copy));
        }

        [Fact]
        public async Task Rollback_Prune_AtExactlyTheKeepCountKeepsEverything()
        {
            var config = DefaultConfig();
            config.ConfigRollbackCount = 3;
            var cfgPath = SeedConfigFile();
            Directory.CreateDirectory(RollbackDir());
            // Two seeds plus the one this run writes makes three at prune time.
            for (var i = 1; i <= 2; i++)
            {
                File.WriteAllText(Path.Combine(RollbackDir(),
                    string.Format("2025010{0}-000000-000.xml", i)), "old");
            }
            RegisterMovies(VodStream(streamId: 1, name: "Kept Movie", added: 1000));

            var logger = new RecordingLogger();
            await ServiceWithRollback(cfgPath, logger).SyncMoviesAsync(config, None, SaveConfig);

            Assert.Equal(3, Rollbacks().Length);
        }

        [Fact]
        public async Task Rollback_Prune_DeletesTheOldestByName()
        {
            var config = DefaultConfig();
            config.ConfigRollbackCount = 3;
            var cfgPath = SeedConfigFile();
            Directory.CreateDirectory(RollbackDir());
            // Created largest-name first, so creation order is reverse of name order.
            for (var i = 5; i >= 1; i--)
            {
                File.WriteAllText(Path.Combine(RollbackDir(),
                    string.Format("2025010{0}-000000-000.xml", i)), "old");
            }
            RegisterMovies(VodStream(streamId: 1, name: "Kept Movie", added: 1000));

            var logger = new RecordingLogger();
            await ServiceWithRollback(cfgPath, logger).SyncMoviesAsync(config, None, SaveConfig);

            var after = Rollbacks().Select(Path.GetFileName).ToList();
            Assert.Equal(3, after.Count);
            Assert.DoesNotContain(after, f => f.Contains("20250101"));
            Assert.DoesNotContain(after, f => f.Contains("20250102"));
            Assert.DoesNotContain(after, f => f.Contains("20250103"));
            Assert.Contains(after, f => f.Contains("20250104"));
        }

        [Fact]
        public async Task Rollback_TwoChangesWithinOneMillisecond_BothCopiesSurvive()
        {
            // Back-to-back syncs have been observed completing inside a single
            // millisecond, so the stamp cannot be the whole name: the second copy
            // must take a suffixed one instead of overwriting the first.
            var config = DefaultConfig();
            config.ConfigRollbackCount = 50;
            var cfgPath = SeedConfigFile();
            var svc = ServiceWithRollback(cfgPath, new RecordingLogger());

            for (var i = 0; i < 30; i++)
            {
                File.WriteAllText(cfgPath, "<PluginConfiguration><A>" + i + "</A></PluginConfiguration>");
                await Task.Yield();
                svc.SnapshotConfigurationForRollback(config);
            }

            var copies = Rollbacks();
            // Every copy has to survive: an overwrite bug loses one per shared millisecond.
            Assert.Equal(30, copies.Length);
            Assert.Contains(copies, f => Path.GetFileName(f).Contains("_"));
            Assert.Contains(copies, f => File.ReadAllText(f).Contains("<A>0</A>"));
        }

        // -----------------------------------------------------------------
        // Catalogue snapshot retention (ADR-F005 mechanism 6)
        // -----------------------------------------------------------------

        private string SnapshotsDir() => Path.Combine(TempDir.Path, "cfg", "xtream-backups", "snapshots");

        private void SeedSnapshots(params string[] dates)
        {
            Directory.CreateDirectory(SnapshotsDir());
            // Created last-name first, so creation order is reverse of name order.
            foreach (var date in dates.Reverse())
            {
                File.WriteAllText(Path.Combine(SnapshotsDir(), "catalogue-ids-" + date + ".tsv"), "seed");
            }
        }

        [Fact]
        public async Task Snapshot_Prune_AtExactlyTheKeepCountKeepsEverything()
        {
            var config = DefaultConfig();
            config.CatalogueSnapshotCount = 3;
            var cfgPath = SeedConfigFile();
            SeedSnapshots("2001-01-01", "2001-01-02");
            RegisterMovies(VodStream(streamId: 1, name: "Kept Movie", added: 1000));

            var logger = new RecordingLogger();
            await ServiceWithRollback(cfgPath, logger).SyncMoviesAsync(config, None, SaveConfig);

            Assert.Equal(3, Directory.GetFiles(SnapshotsDir(), "catalogue-ids-*.tsv").Length);
        }

        [Fact]
        public async Task Snapshot_Prune_DeletesTheOldestByName()
        {
            var config = DefaultConfig();
            config.CatalogueSnapshotCount = 3;
            var cfgPath = SeedConfigFile();
            SeedSnapshots("2001-01-01", "2001-01-02", "2001-01-03", "2001-01-04", "2001-01-05");
            RegisterMovies(VodStream(streamId: 1, name: "Kept Movie", added: 1000));

            var logger = new RecordingLogger();
            await ServiceWithRollback(cfgPath, logger).SyncMoviesAsync(config, None, SaveConfig);

            var after = Directory.GetFiles(SnapshotsDir(), "catalogue-ids-*.tsv").Select(Path.GetFileName).ToList();
            Assert.Equal(3, after.Count);
            Assert.DoesNotContain(after, f => f.Contains("2001-01-01"));
            Assert.DoesNotContain(after, f => f.Contains("2001-01-02"));
            Assert.DoesNotContain(after, f => f.Contains("2001-01-03"));
            Assert.Contains(after, f => f.Contains("2001-01-04"));
        }

        // -----------------------------------------------------------------
        // Episode filename migration: ownership and once-only
        // -----------------------------------------------------------------

        [Fact]
        public void Migration_RunsOncePerUpgrade()
        {
            // The migration records that it ran. Without the version write-back it would
            // rescan every library on every sync: cheap for a small library, a full-tree
            // walk for a large one, on every run, forever.
            var config = DefaultConfig();
            config.EpisodeFilenameMigrationVersion = 0;
            var titled = SeedEpisode("Test Show", "Test Show - S01E01 - Some Title.strm");
            var svc = new StrmSyncService(new RecordingLogger(), HttpClient);

            svc.MigrateEpisodeFilenames(config, () => { });
            Assert.True(File.Exists(Path.Combine(TempDir.Path, "Shows", "Test Show", "Season 01", "Test Show - S01E01.strm")));

            // A new titled file arrives afterwards. The migration must not touch it,
            // because the upgrade already happened.
            var late = SeedEpisode("Test Show", "Test Show - S01E05 - Later.strm");
            Assert.Equal(0, svc.MigrateEpisodeFilenames(config, () => { }));

            Assert.True(File.Exists(late),
                "The migration ran once; a later titled file is none of its business");
        }

        [Fact]
        public void Migration_LeavesForeignTitledFilesAlone()
        {
            // The title-bearing pattern is generic. A hand-written file that happens to
            // match it is not this plugin's to rename.
            var config = DefaultConfig();
            config.EpisodeFilenameMigrationVersion = 0;
            var foreign = SeedEpisode("Test Show", "Test Show - S01E02 - Hand Written.strm",
                "http://elsewhere/whatever.mkv");
            var svc = new StrmSyncService(new RecordingLogger(), HttpClient);

            var renamed = svc.MigrateEpisodeFilenames(config, () => { });

            Assert.Equal(0, renamed);
            Assert.True(File.Exists(foreign));
        }
    }
}
