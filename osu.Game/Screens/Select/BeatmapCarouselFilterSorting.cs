// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using osu.Game.Beatmaps;
using osu.Game.Graphics.Carousel;
using osu.Game.Rulesets.Mods;
using osu.Game.Screens.Select.Filter;
using osu.Game.Utils;

namespace osu.Game.Screens.Select
{
    public class BeatmapCarouselFilterSorting : ICarouselFilter
    {
        public int BeatmapItemsCount { get; private set; }

        public BeatmapDifficultyCache? DifficultyCache { get; set; }

        private readonly Func<FilterCriteria> getCriteria;

        public BeatmapCarouselFilterSorting(Func<FilterCriteria> getCriteria)
        {
            this.getCriteria = getCriteria;
        }

        public async Task<List<CarouselItem>> Run(IEnumerable<CarouselItem> items, CancellationToken cancellationToken) => await Task.Run(() =>
        {
            var criteria = getCriteria();

            bool groupedSets = BeatmapCarouselFilterGrouping.ShouldGroupBeatmapsTogether(criteria);

            BeatmapItemsCount = items.Count();

            var starRatingCache = new Dictionary<BeatmapInfo, double>();
            var setMaxStarsCache = new Dictionary<BeatmapSetInfo, double>();
            var setMaxBaseStarsCache = new Dictionary<BeatmapSetInfo, double>();
            var setMaxBpmCache = new Dictionary<BeatmapSetInfo, double>();

            Mod[]? orderedMods = criteria.Mods != null
                ? criteria.Mods.OrderBy(m => m.Acronym).Select(mod => mod.DeepClone()).ToArray()
                : null;

            double getStarRating(BeatmapInfo b)
            {
                if (!starRatingCache.TryGetValue(b, out double sr))
                {
                    sr = DifficultyCache != null
                        ? DifficultyCache.GetEffectiveStarRating(b, criteria.Ruleset, criteria.Mods, orderedMods)
                        : (criteria.Mods != null
                            ? BeatmapDifficultyCache.ApproximateStarRating(b, criteria.Ruleset, orderedMods ?? (IEnumerable<Mod>)criteria.Mods)
                            : b.StarRating);

                    starRatingCache[b] = sr;
                }

                return sr;
            }

            double getSetMaxStars(BeatmapSetInfo set)
            {
                if (!setMaxStarsCache.TryGetValue(set, out double max))
                {
                    max = double.MinValue;
                    bool any = false;

                    foreach (var b in set.Beatmaps)
                    {
                        if (!b.Hidden)
                        {
                            double s = getStarRating(b);
                            if (s > max)
                                max = s;
                            any = true;
                        }
                    }

                    setMaxStarsCache[set] = any ? max : 0;
                }

                return setMaxStarsCache[set];
            }

            double getSetMaxBaseStars(BeatmapSetInfo set)
            {
                if (!setMaxBaseStarsCache.TryGetValue(set, out double max))
                {
                    max = double.MinValue;
                    bool any = false;

                    foreach (var b in set.Beatmaps)
                    {
                        if (!b.Hidden)
                        {
                            if (b.StarRating > max)
                                max = b.StarRating;
                            any = true;
                        }
                    }

                    setMaxBaseStarsCache[set] = any ? max : 0;
                }

                return setMaxBaseStarsCache[set];
            }

            double getSetMaxBpm(BeatmapSetInfo set)
            {
                if (!setMaxBpmCache.TryGetValue(set, out double max))
                {
                    max = double.MinValue;
                    bool any = false;

                    foreach (var b in set.Beatmaps)
                    {
                        if (!b.Hidden)
                        {
                            if (b.BPM > max)
                                max = b.BPM;
                            any = true;
                        }
                    }

                    setMaxBpmCache[set] = any ? max : 0;
                }

                return setMaxBpmCache[set];
            }

            if (!groupedSets && criteria.Sort == SortMode.Difficulty)
            {
                var itemList = items as IList<CarouselItem> ?? items.ToList();
                var array = new (CarouselItem item, BeatmapInfo beatmap, double sr)[itemList.Count];

                for (int i = 0; i < itemList.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = itemList[i];
                    var b = (BeatmapInfo)item.Model;
                    array[i] = (item, b, getStarRating(b));
                }

                Array.Sort(array, (a, b) =>
                {
                    int comparison = a.sr.CompareTo(b.sr);

                    if (comparison == 0)
                        comparison = a.beatmap.StarRating.CompareTo(b.beatmap.StarRating);

                    if (comparison == 0)
                        comparison = OrdinalSortByCaseStringComparer.DEFAULT.Compare(a.beatmap.BeatmapSet!.Metadata.Title, b.beatmap.BeatmapSet!.Metadata.Title);

                    if (comparison == 0)
                        comparison = b.beatmap.BeatmapSet!.DateAdded.CompareTo(a.beatmap.BeatmapSet!.DateAdded);

                    if (comparison == 0)
                        comparison = b.beatmap.BeatmapSet!.ID.CompareTo(a.beatmap.BeatmapSet!.ID);

                    return comparison;
                });

                var result = new List<CarouselItem>(array.Length);
                for (int i = 0; i < array.Length; i++)
                    result.Add(array[i].item);

                return result;
            }

            return items.Order(Comparer<CarouselItem>.Create((a, b) =>
            {
                var ab = (BeatmapInfo)a.Model;
                var bb = (BeatmapInfo)b.Model;

                if (groupedSets)
                {
                    if (ab.BeatmapSet!.Equals(bb.BeatmapSet))
                        return compareDifficulty(ab, bb, criteria.Sort, getStarRating);

                    // If we're grouping by sets, all fallback sorts need to be aggregates for the set.
                    return compare(ab, bb, criteria.Sort, aggregate: true, getStarRating, getSetMaxStars, getSetMaxBaseStars, getSetMaxBpm);
                }

                return compare(ab, bb, criteria.Sort, aggregate: false, getStarRating, getSetMaxStars, getSetMaxBaseStars, getSetMaxBpm);
            })).ToList();
        }, cancellationToken).ConfigureAwait(false);

        private static int compare(BeatmapInfo a, BeatmapInfo b, SortMode sort, bool aggregate,
            Func<BeatmapInfo, double> getStarRating,
            Func<BeatmapSetInfo, double> getSetMaxStars,
            Func<BeatmapSetInfo, double> getSetMaxBaseStars,
            Func<BeatmapSetInfo, double> getSetMaxBpm)
        {
            int comparison;

            switch (sort)
            {
                case SortMode.Artist:
                    comparison = OrdinalSortByCaseStringComparer.DEFAULT.Compare(a.BeatmapSet!.Metadata.Artist, b.BeatmapSet!.Metadata.Artist);
                    if (comparison == 0)
                        goto case SortMode.Title;
                    break;

                case SortMode.Title:
                    comparison = OrdinalSortByCaseStringComparer.DEFAULT.Compare(a.BeatmapSet!.Metadata.Title, b.BeatmapSet!.Metadata.Title);
                    break;

                case SortMode.Author:
                    comparison = OrdinalSortByCaseStringComparer.DEFAULT.Compare(a.BeatmapSet!.Metadata.Author.Username, b.BeatmapSet!.Metadata.Author.Username);
                    break;

                case SortMode.Source:
                    comparison = OrdinalSortByCaseStringComparer.DEFAULT.Compare(a.BeatmapSet!.Metadata.Source, b.BeatmapSet!.Metadata.Source);
                    break;

                case SortMode.Difficulty:
                    if (aggregate)
                    {
                        comparison = getSetMaxStars(a.BeatmapSet!).CompareTo(getSetMaxStars(b.BeatmapSet!));
                        if (comparison == 0)
                            comparison = getSetMaxBaseStars(a.BeatmapSet!).CompareTo(getSetMaxBaseStars(b.BeatmapSet!));
                        if (comparison == 0)
                            comparison = getSetMaxBpm(a.BeatmapSet!).CompareTo(getSetMaxBpm(b.BeatmapSet!));
                    }
                    else
                    {
                        comparison = getStarRating(a).CompareTo(getStarRating(b));
                        if (comparison == 0)
                            comparison = a.StarRating.CompareTo(b.StarRating);
                    }
                    break;

                case SortMode.DateAdded:
                    comparison = b.BeatmapSet!.DateAdded.CompareTo(a.BeatmapSet!.DateAdded);
                    break;

                case SortMode.DateRanked:
                    comparison = Nullable.Compare(b.BeatmapSet!.DateRanked, a.BeatmapSet!.DateRanked);
                    break;

                case SortMode.DateSubmitted:
                    comparison = Nullable.Compare(b.BeatmapSet!.DateSubmitted, a.BeatmapSet!.DateSubmitted);
                    break;

                case SortMode.LastPlayed:
                    if (aggregate)
                        comparison = compareUsingAggregateMax(b, a, static b => (b.LastPlayed ?? DateTimeOffset.MinValue).ToUnixTimeSeconds());
                    else
                        comparison = Nullable.Compare(b.LastPlayed, a.LastPlayed);
                    break;

                case SortMode.BPM:
                    if (aggregate)
                        comparison = compareUsingAggregateMax(a, b, static b => b.BPM);
                    else
                        comparison = a.BPM.CompareTo(b.BPM);
                    break;

                case SortMode.Length:
                    if (aggregate)
                        comparison = compareUsingAggregateMax(a, b, static b => b.Length);
                    else
                        comparison = a.Length.CompareTo(b.Length);
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }

            // If the initial sort could not differentiate, attempt to use Title then DateAdded to order sets in a stable fashion.
            if (comparison == 0)
                comparison = OrdinalSortByCaseStringComparer.DEFAULT.Compare(a.BeatmapSet!.Metadata.Title, b.BeatmapSet!.Metadata.Title);

            // The directionality of this matches the current SortMode.DateAdded, but we may want to reconsider if that becomes a user decision (ie. asc / desc).
            if (comparison == 0)
                comparison = b.BeatmapSet!.DateAdded.CompareTo(a.BeatmapSet!.DateAdded);

            // If both failed to break the tie, fallback to our internal GUID for stability.
            // This basically means it's a stable random sort.
            if (comparison == 0)
                comparison = b.BeatmapSet!.ID.CompareTo(a.BeatmapSet!.ID);

            return comparison;
        }

        private static int compareDifficulty(BeatmapInfo a, BeatmapInfo b, SortMode sort, Func<BeatmapInfo, double> getStarRating)
        {
            int comparison = a.Ruleset.CompareTo(b.Ruleset);

            if (comparison == 0)
                comparison = getStarRating(a).CompareTo(getStarRating(b));

            if (comparison == 0)
                comparison = a.StarRating.CompareTo(b.StarRating);

            return comparison;
        }

        private static int compareUsingAggregateMax(BeatmapInfo a, BeatmapInfo b, Func<BeatmapInfo, double> func)
        {
            var aMatchedBeatmaps = a.BeatmapSet!.Beatmaps.Where(bb => !bb.Hidden);
            var bMatchedBeatmaps = b.BeatmapSet!.Beatmaps.Where(bb => !bb.Hidden);

            bool aAny = aMatchedBeatmaps.Any();
            bool bAny = bMatchedBeatmaps.Any();

            if (!aAny && !bAny) return 0;
            if (!aAny) return -1;
            if (!bAny) return 1;

            int comparison = aMatchedBeatmaps.Max(func).CompareTo(bMatchedBeatmaps.Max(func));
            if (comparison == 0)
                comparison = aMatchedBeatmaps.Max(bb => bb.StarRating).CompareTo(bMatchedBeatmaps.Max(bb => bb.StarRating));
            if (comparison == 0)
                comparison = aMatchedBeatmaps.Max(bb => bb.BPM).CompareTo(bMatchedBeatmaps.Max(bb => bb.BPM));

            return comparison;
        }
    }
}
