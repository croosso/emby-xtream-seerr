using System.IO;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Service;
using Emby.Xtream.Plugin.Tests.Fakes;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// ADR-C015: with the review gate on, the collapse representative is a copy the user
    /// reviewed. "EN| Some Show" (an English production) and "FR| Some Show" (a French one of
    /// the same story) clean to the same "Some Show" folder. Only the French one was reviewed,
    /// but the lower id won the pick and the gate, which checks the representative alone, held
    /// the show forever.
    /// </summary>
    public class SeriesCollapseReviewedTests : SyncTestBase
    {
        private const int English = 1001;
        private const int French = 2002;

        private PluginConfiguration GatedConfig(string reviewed, string unreviewed = "[]")
        {
            var config = DefaultConfig();
            config.RequireReviewBeforeSync = true;
            config.ReviewedSeriesIdsJson = reviewed;
            config.UnreviewedSeriesIdsJson = unreviewed;
            config.EnableContentNameCleaning = true;
            config.ContentRemoveTerms = "EN|\nFR|";
            return config;
        }

        private string Episode() =>
            Path.Combine(TempDir.Path, "Shows", "Some Show", "Season 01", "Some Show - S01E01.strm");

        private void RegisterBothCopies(string lastModified = "1000")
        {
            Handler.RespondWith("action=get_series", SeriesListJson(
                Series(seriesId: English, name: "EN| Some Show", lastModified: lastModified),
                Series(seriesId: French, name: "FR| Some Show", lastModified: lastModified)));
        }

        [Fact]
        public async Task ReviewedCopy_WinsOverALowerUnreviewedId()
        {
            var config = GatedConfig("[" + French + "]");
            RegisterBothCopies();
            // The French copy's episodes are mkv, the English one's mp4, so the file says
            // which copy was written. English detail is deliberately not registered: fetching
            // it would fail the run.
            Handler.RespondWith("action=get_series_info&series_id=" + French,
                SeriesDetailJson(seriesId: French, ext: "mkv"));

            var svc = MakeService();
            await svc.SyncSeriesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(Episode()), "the reviewed French copy must be written");
            Assert.EndsWith(".mkv", File.ReadAllText(Episode()).Trim());
            Assert.Equal(0, svc.SeriesProgress.Failed);
            Assert.DoesNotContain(Handler.ReceivedUrls, u => u.Contains("get_series_info&series_id=" + English));
        }

        [Fact]
        public async Task ReviewedEnglishCopy_IsKept_WhenThatIsTheOneReviewed()
        {
            // The rule is "the reviewed copy", not "French": reviewing the English row gives
            // the English version.
            var config = GatedConfig("[" + English + "]");
            RegisterBothCopies();
            Handler.RespondWith("action=get_series_info&series_id=" + English,
                SeriesDetailJson(seriesId: English, ext: "mp4"));

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.EndsWith(".mp4", File.ReadAllText(Episode()).Trim());
            Assert.DoesNotContain(Handler.ReceivedUrls, u => u.Contains("get_series_info&series_id=" + French));
        }

        [Fact]
        public async Task GateOff_KeepsTheLowestIdPick()
        {
            // Without the gate the reviewed set means nothing to the sync; the pick must stay
            // exactly what it was so no ungated library changes version.
            var config = GatedConfig("[" + French + "]");
            config.RequireReviewBeforeSync = false;
            RegisterBothCopies();
            Handler.RespondWith("action=get_series_info&series_id=" + English,
                SeriesDetailJson(seriesId: English, ext: "mp4"));

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.EndsWith(".mp4", File.ReadAllText(Episode()).Trim());
            Assert.DoesNotContain(Handler.ReceivedUrls, u => u.Contains("get_series_info&series_id=" + French));
        }

        [Fact]
        public async Task SwitchFromAnOnDiskCopy_RefetchesAndReplacesItsFiles_EvenWhenUnchanged()
        {
            // The English copy was synced before (stored hash, files on disk) and the user then
            // reviewed the French one. Both are delta-unchanged and smart skip is on: without the
            // forced fetch the French representative would pre-fetch-skip and keep the English
            // files under its name.
            var config = GatedConfig("[" + French + "]");
            config.SmartSkipExisting = true;
            config.LastSeriesSyncTimestamp = 9999;
            config.SeriesEpisodeHashesJson = "{\"" + English + "\":\"deadbeef\"}";
            Directory.CreateDirectory(Path.GetDirectoryName(Episode()));
            File.WriteAllText(Episode(), "http://fake-xtream/series/user/pass/101.mp4");

            RegisterBothCopies(lastModified: "1000");
            Handler.RespondWith("action=get_series_info&series_id=" + French,
                SeriesDetailJson(seriesId: French, ext: "mkv"));

            var logger = new RecordingLogger();
            await new StrmSyncService(logger, HttpClient) { SeriesDetailRetryDelayMs = 0 }
                .SyncSeriesAsync(config, None, SaveConfig);

            Assert.EndsWith(".mkv", File.ReadAllText(Episode()).Trim());
            Assert.Contains(French.ToString(), config.SeriesEpisodeHashesJson);
            Assert.Contains(logger.Infos, i => i.Contains("switch to the copy you reviewed") && i.Contains("FR| Some Show"));
        }

        [Fact]
        public async Task BothReviewed_EstablishedCopyKeepsItsPlace_AndTheAmbiguityIsLogged()
        {
            // Two reviewed versions, one folder: nothing says which the user prefers, so the
            // copy already on disk stays (no flip-flopping) and the log names both.
            var config = GatedConfig("[" + English + "," + French + "]");
            config.SeriesEpisodeHashesJson = "{\"" + French + "\":\"deadbeef\"}";
            RegisterBothCopies();
            Handler.RespondWith("action=get_series_info&series_id=" + French,
                SeriesDetailJson(seriesId: French, ext: "mkv"));

            var logger = new RecordingLogger();
            await new StrmSyncService(logger, HttpClient) { SeriesDetailRetryDelayMs = 0 }
                .SyncSeriesAsync(config, None, SaveConfig);

            Assert.EndsWith(".mkv", File.ReadAllText(Episode()).Trim());
            Assert.DoesNotContain(Handler.ReceivedUrls, u => u.Contains("get_series_info&series_id=" + English));
            var warning = Assert.Single(logger.Infos.FindAll(i => i.Contains("more than one reviewed version")));
            Assert.Contains("kept 'FR| Some Show' (" + French + ")", warning);
            Assert.Contains("'EN| Some Show' (" + English + ")", warning);
        }

        [Fact]
        public async Task SameNameCopiesReviewedTogether_AreNotReportedAsAmbiguous()
        {
            // Identical provider names are one row in the de-dup view and always reviewed
            // together; that is the ordinary cross-listing, not two versions.
            var config = GatedConfig("[1,2]");
            Handler.RespondWith("action=get_series", SeriesListJson(
                Series(seriesId: 1, name: "Dup Show", lastModified: "1000"),
                Series(seriesId: 2, name: "Dup Show", lastModified: "1000")));
            Handler.RespondWith("action=get_series_info&series_id=1", SeriesDetailJson(seriesId: 1));

            var logger = new RecordingLogger();
            await new StrmSyncService(logger, HttpClient) { SeriesDetailRetryDelayMs = 0 }
                .SyncSeriesAsync(config, None, SaveConfig);

            Assert.DoesNotContain(logger.Infos, i => i.Contains("more than one reviewed version"));
            Assert.DoesNotContain(logger.Infos, i => i.Contains("switch to the copy you reviewed"));
        }

        [Fact]
        public async Task UnreviewedTombstoneOnTheOtherCopy_DoesNotRemoveTheReviewedOne()
        {
            // English row deliberately un-reviewed, French row reviewed. Before, the English id
            // (lower) represented the folder, was held as un-reviewed, and the removal pass
            // deleted "Some Show" — the folder the French copy lives in — on every run.
            var config = GatedConfig("[" + French + "]", unreviewed: "[" + English + "]");
            RegisterBothCopies();
            Handler.RespondWith("action=get_series_info&series_id=" + French,
                SeriesDetailJson(seriesId: French, ext: "mkv"));

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(Episode()));
            Assert.EndsWith(".mkv", File.ReadAllText(Episode()).Trim());
            Assert.Contains(English.ToString(), config.UnreviewedSeriesIdsJson);
        }

        [Fact]
        public async Task NeitherCopyReviewed_StillHeld()
        {
            // The preference only reorders; it never lets an un-reviewed show through.
            var config = GatedConfig("[]");
            RegisterBothCopies();

            var svc = MakeService();
            await svc.SyncSeriesAsync(config, None, SaveConfig);

            Assert.False(Directory.Exists(Path.Combine(TempDir.Path, "Shows", "Some Show")));
            Assert.DoesNotContain(Handler.ReceivedUrls, u => u.Contains("get_series_info"));
            Assert.Equal(0, svc.SeriesProgress.Failed);
        }
    }
}
