using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Api;
using Emby.Xtream.Plugin.Client.Models;
using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// The pre-release filter (ADR-C014): the pattern, the per-version rule, and the
    /// de-duplicated listing. The names are the ones the providers actually send.
    /// </summary>
    public class ReleaseTagFilterTests
    {
        private static System.Text.RegularExpressions.Regex DefaultFilter()
        {
            string error;
            var filter = ReleaseTagFilter.Build(new PluginConfiguration { EnableReleaseTagFilter = true }, out error);
            Assert.Null(error);
            Assert.NotNull(filter);
            return filter;
        }

        private static DispatcharrVodProvider Version(int streamId, string providerName) => new DispatcharrVodProvider
        {
            StreamId = streamId,
            CustomProperties = providerName == null ? null : new DispatcharrVodProviderProperties
            {
                BasicData = new DispatcharrVodProviderBasicData { Name = providerName },
            },
        };

        [Theory]
        [InlineData("EN| The Odyssey - 2026 [HDTS]")]
        [InlineData("EN - The Odyssey (TELESYNC) (2026)")]
        [InlineData("FR| L'Odyssée - 2026 [VOSTFR] [HDCAM]")]
        [InlineData("Resident Evil (CAM) (2026)")]
        [InlineData("Some Film [HD-TS]")]
        [InlineData("Some Film (HD TC)")]
        [InlineData("Some Film [ts]")]
        [InlineData("Some Film [ SCREENER ]")]
        public void DefaultPattern_MatchesPreReleaseTags(string name)
        {
            Assert.True(ReleaseTagFilter.IsTagged(DefaultFilter(), name));
        }

        [Theory]
        [InlineData("EN|  The Odyssey - 2026")]
        [InlineData("The Odyssey - 2026  [MULTI-SUB]")]
        [InlineData("4K-TOP - Verity (2026)")]
        [InlineData("Cats (2019)")]
        [InlineData("TS Eliot: The Waste Land")]
        [InlineData("Camp X-Ray")]
        [InlineData("[REC]")]
        [InlineData("Scream (2022)")]
        [InlineData("")]
        [InlineData(null)]
        public void DefaultPattern_LeavesOrdinaryTitlesAlone(string name)
        {
            Assert.False(ReleaseTagFilter.IsTagged(DefaultFilter(), name));
        }

        [Fact]
        public void Build_IsNullWhenTheFilterIsOff()
        {
            string error;
            Assert.Null(ReleaseTagFilter.Build(new PluginConfiguration(), out error));
            Assert.Null(error);
        }

        [Fact]
        public void Build_UsesACustomPattern()
        {
            string error;
            var filter = ReleaseTagFilter.Build(
                new PluginConfiguration { EnableReleaseTagFilter = true, ReleaseTagFilterPattern = @"\bR5\b" }, out error);
            Assert.True(ReleaseTagFilter.IsTagged(filter, "Some Film R5"));
            Assert.False(ReleaseTagFilter.IsTagged(filter, "Some Film [HDTS]"));
        }

        [Fact]
        public void Build_AnInvalidPatternFiltersNothingAndSaysWhy()
        {
            string error;
            var filter = ReleaseTagFilter.Build(
                new PluginConfiguration { EnableReleaseTagFilter = true, ReleaseTagFilterPattern = "[HDTS" }, out error);
            Assert.Null(filter);
            Assert.False(string.IsNullOrEmpty(error));
        }

        [Fact]
        public void KeepVersions_DropsTaggedVersionsAndKeepsUnnamedOnes()
        {
            var kept = ReleaseTagFilter.KeepVersions(DefaultFilter(), new[]
            {
                Version(1, "EN - The Odyssey (TELESYNC) (2026)"),
                Version(2, "EN - The Odyssey (2026)"),
                Version(3, null),
            });

            Assert.Equal(new[] { 2, 3 }, kept.Select(v => v.StreamId).ToArray());
        }

        [Fact]
        public async Task IsTitleFiltered_AnUntaggedTitleIsNeverLookedUp()
        {
            var calls = 0;
            var filtered = await ReleaseTagFilter.IsTitleFilteredAsync(
                DefaultFilter(), "The Odyssey - 2026",
                () => { calls++; return Task.FromResult(new List<DispatcharrVodProvider>()); });

            Assert.False(filtered);
            Assert.Equal(0, calls);
        }

        [Fact]
        public async Task IsTitleFiltered_WithoutDispatcharrTheNameDecides()
        {
            Assert.True(await ReleaseTagFilter.IsTitleFilteredAsync(DefaultFilter(), "The Odyssey - 2026 [HDTS]", null));
        }

        [Fact]
        public async Task IsTitleFiltered_ATaggedTitleWithACleanVersionIsKept()
        {
            // Dispatcharr's movie 493739: named "[HDTS]", holding untagged copies too.
            var filtered = await ReleaseTagFilter.IsTitleFilteredAsync(
                DefaultFilter(), "EN| The Odyssey - 2026 [HDTS]",
                () => Task.FromResult(new List<DispatcharrVodProvider>
                {
                    Version(1404028, "EN - The Odyssey (TELESYNC) (2026)"),
                    Version(1412992, "EN - The Odyssey (2026)"),
                }));

            Assert.False(filtered);
        }

        [Fact]
        public async Task IsTitleFiltered_EveryVersionTaggedFiltersTheTitle()
        {
            var filtered = await ReleaseTagFilter.IsTitleFilteredAsync(
                DefaultFilter(), "EN| The Odyssey - 2026 [HDTS]",
                () => Task.FromResult(new List<DispatcharrVodProvider>
                {
                    Version(1465615, "EN| The Odyssey - 2026 [HDTS]"),
                }));

            Assert.True(filtered);
        }

        [Fact]
        public async Task IsTitleFiltered_AFailedLookupKeepsTheTitle()
        {
            var reasons = new List<string>();
            var filtered = await ReleaseTagFilter.IsTitleFilteredAsync(
                DefaultFilter(), "Some Film [HDTS]",
                () => throw new InvalidOperationException("boom"),
                reasons.Add);

            Assert.False(filtered);
            Assert.Single(reasons);
        }

        [Fact]
        public async Task IsTitleFiltered_NoVersionsAtAllKeepsTheTitle()
        {
            // A listed movie always has a relation, so an empty answer is a failed lookup.
            var reasons = new List<string>();
            var filtered = await ReleaseTagFilter.IsTitleFilteredAsync(
                DefaultFilter(), "Some Film [HDTS]",
                () => Task.FromResult(new List<DispatcharrVodProvider>()),
                reasons.Add);

            Assert.False(filtered);
            Assert.Single(reasons);
        }

        [Fact]
        public void ProviderName_IsReadFromTheRelationsBasicData()
        {
            const string json = "[" +
                "{\"id\":1,\"stream_id\":11,\"custom_properties\":{\"basic_data\":{\"name\":\"EN - The Odyssey (TELESYNC) (2026)\",\"added\":\"1\"},\"quality\":\"1080p\"}}," +
                "{\"id\":2,\"stream_id\":12,\"custom_properties\":null}," +
                "{\"id\":3,\"stream_id\":13,\"custom_properties\":{\"basic_data\":{\"name\":2026}}}," +
                "{\"id\":4,\"stream_id\":14}" +
                "]";

            var providers = JsonSerializer.Deserialize<List<DispatcharrVodProvider>>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            Assert.Equal("EN - The Odyssey (TELESYNC) (2026)", providers[0].ProviderName);
            Assert.Null(providers[1].ProviderName);
            Assert.Equal("2026", providers[2].ProviderName);
            Assert.Null(providers[3].ProviderName);
        }

        [Fact]
        public async Task Listing_HidesWhatTheSyncWouldLeaveOut()
        {
            var items = new List<DedupedItemDto>
            {
                new DedupedItemDto { Name = "EN| The Odyssey - 2026 [HDTS]", Ids = new[] { 493739 } },
                new DedupedItemDto { Name = "FR| L'Odyssée - 2026 [HDTS]", Ids = new[] { 494787 } },
                new DedupedItemDto { Name = "EN|  The Odyssey - 2026", Ids = new[] { 493737 } },
            };
            var lookedUp = new List<int>();

            var shown = await XtreamTunerApi.HidePreReleaseCopiesAsync(
                items,
                DefaultFilter(),
                id =>
                {
                    lock (lookedUp) lookedUp.Add(id);
                    return Task.FromResult(id == 493739
                        ? new List<DispatcharrVodProvider> { Version(1, "EN - The Odyssey (TELESYNC) (2026)"), Version(2, "EN - The Odyssey (2026)") }
                        : new List<DispatcharrVodProvider> { Version(3, "FR| L'Odyssée - 2026 [HDTS]") });
                },
                parallelism: 2);

            Assert.Equal(
                new[] { "EN| The Odyssey - 2026 [HDTS]", "EN|  The Odyssey - 2026" },
                shown.Select(i => i.Name).ToArray());
            Assert.DoesNotContain(493737, lookedUp);
        }

        [Fact]
        public async Task Listing_WithoutDispatcharrHidesTaggedNames()
        {
            var items = new List<DedupedItemDto>
            {
                new DedupedItemDto { Name = "Some Film [HDTS]", Ids = new[] { 1 } },
                new DedupedItemDto { Name = "Some Film", Ids = new[] { 2 } },
            };

            var shown = await XtreamTunerApi.HidePreReleaseCopiesAsync(items, DefaultFilter(), null, 1);

            Assert.Equal(new[] { "Some Film" }, shown.Select(i => i.Name).ToArray());
        }
    }
}
