// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Extensions.IEnumerableExtensions;
using osu.Game.Beatmaps;
using osu.Game.Graphics.Carousel;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Screens.Select;
using osu.Game.Screens.Select.Filter;
using osu.Game.Tests.Resources;

namespace osu.Game.Tests.Visual.SongSelect
{
    [TestFixture]
    public partial class BeatmapCarouselFilterSortingTest
    {
        [Test]
        public async Task TestSorting()
        {
            List<BeatmapSetInfo> beatmapSets = new List<BeatmapSetInfo>();

            const string zzz_lowercase = "zzzzz";
            const string zzz_uppercase = "ZZZZZ";
            const int diff_count = 5;

            for (int i = 0; i < 20; i++)
            {
                var set = TestResources.CreateTestBeatmapSetInfo(diff_count);

                if (i == 4)
                    set.Beatmaps.ForEach(b => b.Metadata.Artist = zzz_uppercase);

                if (i == 8)
                    set.Beatmaps.ForEach(b => b.Metadata.Artist = zzz_lowercase);

                if (i == 12)
                    set.Beatmaps.ForEach(b => b.Metadata.Author.Username = zzz_uppercase);

                if (i == 16)
                    set.Beatmaps.ForEach(b => b.Metadata.Author.Username = zzz_lowercase);

                beatmapSets.Add(set);
            }

            var results = await runSorting(SortMode.Author, beatmapSets);

            Assert.That(results.Last().Metadata.Author.Username, Is.EqualTo(zzz_uppercase));
            Assert.That(results.SkipLast(diff_count).Last().Metadata.Author.Username, Is.EqualTo(zzz_lowercase));

            results = await runSorting(SortMode.Artist, beatmapSets);

            Assert.That(results.Last().Metadata.Artist, Is.EqualTo(zzz_uppercase));
            Assert.That(results.SkipLast(diff_count).Last().Metadata.Artist, Is.EqualTo(zzz_lowercase));
        }

        [Test]
        public async Task TestSortingDateSubmitted()
        {
            List<BeatmapSetInfo> beatmapSets = new List<BeatmapSetInfo>();

            const string zzz_string = "zzzzz";

            for (int i = 0; i < 10; i++)
            {
                var set = TestResources.CreateTestBeatmapSetInfo(5);

                // A total of 6 sets have date submitted (4 don't)
                // A total of 5 sets have artist string (3 of which also have date submitted)

                if (i >= 2 && i < 8) // i = 2, 3, 4, 5, 6, 7 have submitted date
                    set.DateSubmitted = DateTimeOffset.Now.AddMinutes(i);
                if (i < 5) // i = 0, 1, 2, 3, 4 have matching string
                    set.Beatmaps.ForEach(b => b.Metadata.Artist = zzz_string);

                set.Beatmaps.ForEach(b => b.Metadata.Title = $"submitted: {set.DateSubmitted}");

                beatmapSets.Add(set);
            }

            var results = await runSorting(SortMode.DateSubmitted, beatmapSets);

            Assert.That(results.Count(), Is.EqualTo(50));

            Assert.That(results.Reverse().TakeWhile(b => b.BeatmapSet!.DateSubmitted == null).Count(), Is.EqualTo(20), () => "missing dates should be at the end");
            Assert.That(results.TakeWhile(b => b.BeatmapSet!.DateSubmitted != null).Count(), Is.EqualTo(30), () => "non-missing dates should be at the start");
        }

        [Test]
        public async Task TestSortByBpmUsesTitleAsTiebreaker()
        {
            List<BeatmapSetInfo> beatmapSets = [];

            // 2 sets with same BPM but different titles
            const int diff_count = 1;
            {
                var set = TestResources.CreateTestBeatmapSetInfo(diff_count);
                set.DateAdded = new DateTimeOffset(2025, 6, 11, 10, 0, 0, TimeSpan.Zero);
                set.Beatmaps.ForEach(b =>
                {
                    b.ID = Guid.Parse("00000000-0000-0000-0000-000000000000");
                    b.BPM = 175;
                    b.Metadata.Title = "ZZZ";
                });
                beatmapSets.Add(set);
            }
            {
                var set = TestResources.CreateTestBeatmapSetInfo(diff_count);
                set.DateAdded = new DateTimeOffset(2025, 6, 10, 10, 0, 0, TimeSpan.Zero);
                set.Beatmaps.ForEach(b =>
                {
                    b.ID = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
                    b.BPM = 175;
                    b.Metadata.Title = "AAA";
                });
                beatmapSets.Add(set);
            }

            var results = (await runSorting(SortMode.BPM, beatmapSets)).ToList();

            Assert.That(results[0].Metadata.Title, Is.EqualTo("AAA"));
            Assert.That(results[1].Metadata.Title, Is.EqualTo("ZZZ"));
        }

        [Test]
        public async Task TestSortByArtistUsesTitleAsTiebreaker()
        {
            List<BeatmapSetInfo> beatmapSets = new List<BeatmapSetInfo>();

            const int diff_count = 5;

            for (int i = 0; i < 20; i++)
            {
                var set = TestResources.CreateTestBeatmapSetInfo(diff_count);

                if (i == 4)
                {
                    set.Beatmaps.ForEach(b =>
                    {
                        b.Metadata.Artist = "ZZZ";
                        b.Metadata.Title = "AAA";
                    });
                }

                if (i == 8)
                {
                    set.Beatmaps.ForEach(b =>
                    {
                        b.Metadata.Artist = "ZZZ";
                        b.Metadata.Title = "ZZZ";
                    });
                }

                beatmapSets.Add(set);
            }

            var results = await runSorting(SortMode.Artist, beatmapSets);

            Assert.That(() =>
            {
                var lastItem = results.Last();
                return lastItem.Metadata.Artist == "ZZZ" && lastItem.Metadata.Title == "ZZZ";
            });

            Assert.That(() =>
            {
                var secondLastItem = results.SkipLast(diff_count).Last();
                return secondLastItem.Metadata.Artist == "ZZZ" && secondLastItem.Metadata.Title == "AAA";
            });
        }

        /// <summary>
        /// Ensures stability is maintained on different sort modes for items with equal properties.
        /// </summary>
        [Test]
        public async Task TestSortingStabilityDateAdded()
        {
            List<BeatmapSetInfo> beatmapSets = new List<BeatmapSetInfo>();

            for (int i = 0; i < 10; i++)
            {
                var set = TestResources.CreateTestBeatmapSetInfo();

                set.DateAdded = DateTimeOffset.FromUnixTimeSeconds(i);

                // only need to set the first as they are a shared reference.
                var beatmap = set.Beatmaps.First();

                beatmap.Metadata.Artist = "a";
                beatmap.Metadata.Title = "b";

                beatmapSets.Add(set);
            }

            var results = await runSorting(SortMode.Title, beatmapSets);

            Assert.That(results.Select(b => b.BeatmapSet!.DateAdded), Is.Ordered.Descending);

            results = await runSorting(SortMode.Artist, beatmapSets);

            Assert.That(results.Select(b => b.BeatmapSet!.DateAdded), Is.Ordered.Descending);
        }

        [Test]
        public async Task TestSortByDifficultyNomod()
        {
            var set = TestResources.CreateTestBeatmapSetInfo(5);
            set.Beatmaps[0].StarRating = 5.0;
            set.Beatmaps[1].StarRating = 1.0;
            set.Beatmaps[2].StarRating = 4.0;
            set.Beatmaps[3].StarRating = 2.0;
            set.Beatmaps[4].StarRating = 3.0;

            var results = (await runSorting(SortMode.Difficulty, new List<BeatmapSetInfo> { set })).ToList();

            Assert.That(results.Select(b => b.StarRating), Is.EqualTo(new[] { 1.0, 2.0, 3.0, 4.0, 5.0 }));
        }

        [Test]
        public async Task TestSortByDifficultyWithModAdjustment()
        {
            var setA = TestResources.CreateTestBeatmapSetInfo(1);
            setA.Beatmaps[0].StarRating = 4.0;

            var setB = TestResources.CreateTestBeatmapSetInfo(1);
            setB.Beatmaps[0].StarRating = 5.0;

            // Nomod: setA (4.0) < setB (5.0)
            var nomodResults = (await runSorting(SortMode.Difficulty, new List<BeatmapSetInfo> { setA, setB })).ToList();
            Assert.That(nomodResults[0], Is.EqualTo(setA.Beatmaps[0]));
            Assert.That(nomodResults[1], Is.EqualTo(setB.Beatmaps[0]));

            // With DoubleTime, both scale up and preserve ascending order
            var dtResults = (await runSorting(SortMode.Difficulty, new List<BeatmapSetInfo> { setA, setB }, new[] { new OsuModDoubleTime() })).ToList();
            Assert.That(dtResults[0], Is.EqualTo(setA.Beatmaps[0]));
            Assert.That(dtResults[1], Is.EqualTo(setB.Beatmaps[0]));
        }

        [Test]
        public async Task TestSortByDifficultyRespectsModDifference()
        {
            var setA = TestResources.CreateTestBeatmapSetInfo(1);
            setA.Beatmaps[0].StarRating = 4.0;

            var setB = TestResources.CreateTestBeatmapSetInfo(1);
            setB.Beatmaps[0].StarRating = 5.0;

            // With HalfTime, 5.0 base becomes 5.0 * 0.75^0.75 ≈ 4.03.
            // With DoubleTime, 4.0 base becomes 4.0 * 1.5^0.75 ≈ 5.42.
            // When sorting with DT, setA (~5.42) > setA nomod (4.0).
            var dtResults = (await runSorting(SortMode.Difficulty, new List<BeatmapSetInfo> { setA }, new[] { new OsuModDoubleTime() })).ToList();
            Assert.That(dtResults, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task TestModAdjustedDifficultyMatching()
        {
            var beatmap = TestResources.CreateTestBeatmapSetInfo(1).Beatmaps[0];
            beatmap.StarRating = 4.0;

            var criteriaNomod = new FilterCriteria
            {
                StarDifficulty = new FilterCriteria.OptionalRange<double> { Min = 5.0 }
            };

            // Nomod: 4.0 does not match stars>=5.0
            Assert.That(BeatmapCarouselFilterMatching.CheckCriteriaMatch(beatmap, criteriaNomod), Is.False);

            var criteriaWithDt = new FilterCriteria
            {
                StarDifficulty = new FilterCriteria.OptionalRange<double> { Min = 5.0 },
                Mods = new[] { new OsuModDoubleTime() }
            };

            // With DoubleTime: approximate rating is 4.0 * 1.5^0.75 ≈ 5.42, which matches stars>=5.0
            Assert.That(BeatmapCarouselFilterMatching.CheckCriteriaMatch(beatmap, criteriaWithDt), Is.True);
        }

        [Test]
        public async Task TestModAdjustedUserStarDifficultyMatching()
        {
            var beatmap = TestResources.CreateTestBeatmapSetInfo(1).Beatmaps[0];
            beatmap.StarRating = 4.0;

            var criteriaNomod = new FilterCriteria
            {
                UserStarDifficulty = new FilterCriteria.OptionalRange<double> { Min = 5.0, Max = 6.0 }
            };

            // Nomod: 4.0 is not in [5.0, 6.0]
            Assert.That(BeatmapCarouselFilterMatching.CheckCriteriaMatch(beatmap, criteriaNomod), Is.False);

            var criteriaWithDt = new FilterCriteria
            {
                UserStarDifficulty = new FilterCriteria.OptionalRange<double> { Min = 5.0, Max = 6.0 },
                Mods = new[] { new OsuModDoubleTime() }
            };

            // With DoubleTime: ~5.42 is in [5.0, 6.0]
            Assert.That(BeatmapCarouselFilterMatching.CheckCriteriaMatch(beatmap, criteriaWithDt), Is.True);
        }

        [Test]
        public async Task TestSortByDifficultyCompoundMods()
        {
            var setA = TestResources.CreateTestBeatmapSetInfo(1);
            setA.Beatmaps[0].StarRating = 4.0;

            var setB = TestResources.CreateTestBeatmapSetInfo(1);
            setB.Beatmaps[0].StarRating = 5.0;

            // With DT + HR:
            // setA: 4.0 * 1.5^0.75 * 1.08 ≈ 5.86 stars
            // setB: 5.0 * 1.5^0.75 * 1.08 ≈ 7.32 stars
            var modsDtHr = new Mod[] { new OsuModDoubleTime(), new OsuModHardRock() };
            var resultsDtHr = (await runSorting(SortMode.Difficulty, new List<BeatmapSetInfo> { setB, setA }, modsDtHr)).ToList();
            Assert.That(resultsDtHr[0], Is.EqualTo(setA.Beatmaps[0]));
            Assert.That(resultsDtHr[1], Is.EqualTo(setB.Beatmaps[0]));

            // With EZ + HT:
            // setA: 4.0 * 0.75^0.75 * 0.88 ≈ 2.84 stars
            // setB: 5.0 * 0.75^0.75 * 0.88 ≈ 3.55 stars
            var modsEzHt = new Mod[] { new OsuModEasy(), new OsuModHalfTime() };
            var resultsEzHt = (await runSorting(SortMode.Difficulty, new List<BeatmapSetInfo> { setB, setA }, modsEzHt)).ToList();
            Assert.That(resultsEzHt[0], Is.EqualTo(setA.Beatmaps[0]));
            Assert.That(resultsEzHt[1], Is.EqualTo(setB.Beatmaps[0]));
        }

        [Test]
        public async Task TestSortByDifficultyNonDifficultyModsPreserveBaseOrder()
        {
            var setA = TestResources.CreateTestBeatmapSetInfo(1);
            setA.Beatmaps[0].StarRating = 3.0;

            var setB = TestResources.CreateTestBeatmapSetInfo(1);
            setB.Beatmaps[0].StarRating = 4.0;

            var setC = TestResources.CreateTestBeatmapSetInfo(1);
            setC.Beatmaps[0].StarRating = 5.0;

            var nonDifficultyMods = new Mod[] { new OsuModNoFail(), new OsuModSpunOut(), new OsuModAutoplay() };
            var results = (await runSorting(SortMode.Difficulty, new List<BeatmapSetInfo> { setC, setA, setB }, nonDifficultyMods)).ToList();
            Assert.That(results[0], Is.EqualTo(setA.Beatmaps[0]));
            Assert.That(results[1], Is.EqualTo(setB.Beatmaps[0]));
            Assert.That(results[2], Is.EqualTo(setC.Beatmaps[0]));
        }

        [Test]
        public async Task TestSortByDifficultyZeroStarsHandledGracefully()
        {
            var setZero = TestResources.CreateTestBeatmapSetInfo(1);
            setZero.Beatmaps[0].StarRating = 0.0;

            var setNormal = TestResources.CreateTestBeatmapSetInfo(1);
            setNormal.Beatmaps[0].StarRating = 3.5;

            var results = (await runSorting(SortMode.Difficulty, new List<BeatmapSetInfo> { setNormal, setZero }, new[] { new OsuModDoubleTime() })).ToList();
            Assert.That(results[0], Is.EqualTo(setZero.Beatmaps[0]));
            Assert.That(results[1], Is.EqualTo(setNormal.Beatmaps[0]));
        }

        [Test]
        public async Task TestSortByDifficultyExactCachedOverrulesApproximation()
        {
            var setA = TestResources.CreateTestBeatmapSetInfo(1);
            setA.Beatmaps[0].StarRating = 4.0;

            var setB = TestResources.CreateTestBeatmapSetInfo(1);
            setB.Beatmaps[0].StarRating = 5.0;

            var cache = new TestBeatmapDifficultyCache();
            var dtMod = new OsuModDoubleTime();
            var mods = new[] { dtMod };

            // Under approximation:
            // setA (4.0 * 1.5^0.75 ≈ 5.42) would be < setB (5.0 * 1.5^0.75 ≈ 6.78).
            // But if setA has an exact cached difficulty of 8.0 stars, it should sort AFTER setB!
            cache.PresetDifficulty = lookup =>
            {
                if (lookup.BeatmapInfo.Equals(setA.Beatmaps[0]))
                    return new StarDifficulty(8.0, 0);

                return new StarDifficulty(5.0 * 1.5, 0);
            };

            // Pre-populate the cache for setA
            await cache.GetDifficultyAsync(setA.Beatmaps[0], null, mods);

            var results = (await runSorting(SortMode.Difficulty, new List<BeatmapSetInfo> { setA, setB }, mods, cache)).ToList();
            // setB (approx ~6.78) comes before setA (cached 8.0)
            Assert.That(results[0], Is.EqualTo(setB.Beatmaps[0]));
            Assert.That(results[1], Is.EqualTo(setA.Beatmaps[0]));
        }

        [Test]
        public async Task TestSortByDifficultyTiedEffectiveStarsFallsBackToBaseStars()
        {
            var setA = TestResources.CreateTestBeatmapSetInfo(1);
            setA.Beatmaps[0].StarRating = 4.2;
            setA.Beatmaps[0].Metadata.Title = "ZZZ";

            var setB = TestResources.CreateTestBeatmapSetInfo(1);
            setB.Beatmaps[0].StarRating = 4.5;
            setB.Beatmaps[0].Metadata.Title = "AAA";

            // If effective stars tie, higher base star rating should sort after lower base star rating
            var cache = new TestBeatmapDifficultyCache
            {
                PresetDifficulty = _ => new StarDifficulty(5.0, 0)
            };
            var mods = new[] { new OsuModDoubleTime() };

            var results = (await runSorting(SortMode.Difficulty, new List<BeatmapSetInfo> { setB, setA }, mods, cache)).ToList();
            Assert.That(results[0], Is.EqualTo(setA.Beatmaps[0]));
            Assert.That(results[1], Is.EqualTo(setB.Beatmaps[0]));
        }

        [Test]
        public async Task TestSortByDifficultyTiedStarsFallsBackToTitle()
        {
            var setZzz = TestResources.CreateTestBeatmapSetInfo(1);
            setZzz.Beatmaps[0].StarRating = 5.0;
            setZzz.Beatmaps[0].Metadata.Title = "ZZZ";

            var setAaa = TestResources.CreateTestBeatmapSetInfo(1);
            setAaa.Beatmaps[0].StarRating = 5.0;
            setAaa.Beatmaps[0].Metadata.Title = "AAA";

            // When stars tie, Title alphabetical order breaks the tie
            var results = (await runSorting(SortMode.Difficulty, new List<BeatmapSetInfo> { setZzz, setAaa })).ToList();
            Assert.That(results[0], Is.EqualTo(setAaa.Beatmaps[0]));
            Assert.That(results[1], Is.EqualTo(setZzz.Beatmaps[0]));
        }

        [Test]
        public void TestApproximateStarRatingReflectsRateSlider()
        {
            var beatmap = TestResources.CreateTestBeatmapSetInfo(1).Beatmaps[0];
            beatmap.StarRating = 5.0;

            var dt12 = new OsuModDoubleTime { SpeedChange = { Value = 1.2 } };
            var dt15 = new OsuModDoubleTime { SpeedChange = { Value = 1.5 } };
            var dt18 = new OsuModDoubleTime { SpeedChange = { Value = 1.8 } };

            double sr12 = BeatmapDifficultyCache.ApproximateStarRating(beatmap, null, new[] { dt12 });
            double sr15 = BeatmapDifficultyCache.ApproximateStarRating(beatmap, null, new[] { dt15 });
            double sr18 = BeatmapDifficultyCache.ApproximateStarRating(beatmap, null, new[] { dt18 });

            Assert.That(sr12, Is.LessThan(sr15));
            Assert.That(sr15, Is.LessThan(sr18));
        }

        private static async Task<IEnumerable<BeatmapInfo>> runSorting(SortMode sort, List<BeatmapSetInfo> beatmapSets, IReadOnlyList<Mod>? mods = null, BeatmapDifficultyCache? difficultyCache = null)
        {
            var sorter = new BeatmapCarouselFilterSorting(() => new FilterCriteria { Sort = sort, Mods = mods })
            {
                DifficultyCache = difficultyCache
            };
            var carouselItems = await sorter.Run(beatmapSets.SelectMany(s => s.Beatmaps.Select(b => new CarouselItem(b))), CancellationToken.None);
            return carouselItems.Select(ci => ci.Model).OfType<BeatmapInfo>();
        }

        private partial class TestBeatmapDifficultyCache : BeatmapDifficultyCache
        {
            public Func<DifficultyCacheLookup, StarDifficulty>? PresetDifficulty { get; set; }

            protected override Task<StarDifficulty?> ComputeValueAsync(DifficultyCacheLookup lookup, CancellationToken token = default)
            {
                if (PresetDifficulty != null)
                    return Task.FromResult<StarDifficulty?>(PresetDifficulty(lookup));

                return Task.FromResult<StarDifficulty?>(new StarDifficulty(lookup.BeatmapInfo.StarRating, 0));
            }
        }
    }
}
