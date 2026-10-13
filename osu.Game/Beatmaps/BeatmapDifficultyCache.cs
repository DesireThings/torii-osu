// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Audio.Track;
using osu.Framework.Bindables;
using osu.Framework.Extensions;
using osu.Framework.Graphics.Textures;
using osu.Framework.Lists;
using osu.Framework.Logging;
using osu.Framework.Threading;
using osu.Game.Configuration;
using osu.Game.Database;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.UI;
using osu.Game.Scoring;
using osu.Game.Skinning;
using osu.Game.Storyboards;
using osu.Game.Utils;

namespace osu.Game.Beatmaps
{
    /// <summary>
    /// A component which performs and acts as a central cache for difficulty calculations of beatmap/ruleset/mod combinations.
    /// Currently not persisted between game sessions.
    /// </summary>
    public partial class BeatmapDifficultyCache : MemoryCachingComponent<BeatmapDifficultyCache.DifficultyCacheLookup, StarDifficulty?>
    {
        // Too many simultaneous updates can lead to stutters. One thread seems to work fine for song select display purposes.
        private readonly ThreadedTaskScheduler updateScheduler = new ThreadedTaskScheduler(1, nameof(BeatmapDifficultyCache));

        /// <summary>
        /// All bindables that should be updated along with the current ruleset + mods.
        /// </summary>
        private readonly WeakList<BindableStarDifficulty> trackedBindables = new WeakList<BindableStarDifficulty>();

        /// <summary>
        /// Cancellation sources used by tracked bindables.
        /// </summary>
        private readonly List<CancellationTokenSource> linkedCancellationSources = new List<CancellationTokenSource>();

        /// <summary>
        /// Lock to be held when operating on <see cref="trackedBindables"/> or <see cref="linkedCancellationSources"/>.
        /// </summary>
        private readonly object bindableUpdateLock = new object();

        private CancellationTokenSource trackedUpdateCancellationSource = new CancellationTokenSource();

        [Resolved]
        private BeatmapManager beatmapManager { get; set; } = null!;

        [Resolved]
        private Bindable<RulesetInfo> currentRuleset { get; set; } = null!;

        [Resolved]
        private Bindable<IReadOnlyList<Mod>> currentMods { get; set; } = null!;

        private ModSettingChangeTracker? modSettingChangeTracker;
        private ScheduledDelegate? debouncedModSettingsChange;

        protected override void LoadComplete()
        {
            base.LoadComplete();

            currentRuleset.BindValueChanged(_ => Scheduler.AddOnce(updateTrackedBindables));

            currentMods.BindValueChanged(mods =>
            {
                // A change in bindable here doesn't guarantee that mods have actually changed.
                // However, we *do* want to make sure that the mod *references* are the same;
                // `SequenceEqual()` without a comparer would fall back to `IEquatable`.
                // Failing to ensure reference equality can cause setting change tracking to fail later.
                if (mods.OldValue.SequenceEqual(mods.NewValue, ReferenceEqualityComparer.Instance))
                    return;

                modSettingChangeTracker?.Dispose();

                Scheduler.AddOnce(updateTrackedBindables);

                modSettingChangeTracker = new ModSettingChangeTracker(mods.NewValue);
                modSettingChangeTracker.SettingChanged += _ =>
                {
                    lock (bindableUpdateLock)
                    {
                        debouncedModSettingsChange?.Cancel();
                        debouncedModSettingsChange = Scheduler.AddDelayed(updateTrackedBindables, 100);
                    }
                };
            }, true);
        }

        /// <summary>
        /// Notify this cache that a beatmap has been invalidated/updated.
        /// </summary>
        /// <param name="oldBeatmap">The old beatmap model.</param>
        /// <param name="newBeatmap">The updated beatmap model.</param>
        public void Invalidate(IBeatmapInfo oldBeatmap, IBeatmapInfo newBeatmap)
        {
            base.Invalidate(lookup => lookup.BeatmapInfo.Equals(oldBeatmap));

            lock (bindableUpdateLock)
            {
                bool trackedBindablesRefreshRequired = false;

                foreach (var bsd in trackedBindables.Where(bsd => bsd.BeatmapInfo.Equals(oldBeatmap)))
                {
                    bsd.BeatmapInfo = newBeatmap;
                    trackedBindablesRefreshRequired = true;
                }

                if (trackedBindablesRefreshRequired)
                    Scheduler.AddOnce(updateTrackedBindables);
            }
        }

        /// <summary>
        /// Invoked when a difficulty calculation completes and updates a beatmap's cached <see cref="StarDifficulty"/>.
        /// </summary>
        public event Action<IBeatmapInfo, StarDifficulty>? DifficultyUpdated;



        /// <summary>
        /// Retrieves a bindable containing the star difficulty of a <see cref="BeatmapInfo"/> that follows the currently-selected ruleset and mods.
        /// </summary>
        /// <param name="beatmapInfo">The <see cref="BeatmapInfo"/> to get the difficulty of.</param>
        /// <param name="cancellationToken">An optional <see cref="CancellationToken"/> which stops updating the star difficulty for the given <see cref="BeatmapInfo"/>.</param>
        /// <param name="computationDelay">A delay in milliseconds before performing the </param>
        /// <returns>A bindable that is updated to contain the star difficulty when it becomes available. May be an approximation while in an initial calculating state.</returns>
        public IBindable<StarDifficulty> GetBindableDifficulty(IBeatmapInfo beatmapInfo, CancellationToken cancellationToken = default, int computationDelay = 0)
        {
            double initialStars = beatmapInfo is BeatmapInfo localInfo
                ? GetEffectiveStarRating(localInfo, currentRuleset.Value, currentMods.Value)
                : beatmapInfo.StarRating;

            var bindable = new BindableStarDifficulty(beatmapInfo, cancellationToken)
            {
                // Start with an approximate or cached known value instead of raw base nomod stars.
                Value = new StarDifficulty(initialStars, 0)
            };

            lock (bindableUpdateLock)
            {
                var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(trackedUpdateCancellationSource.Token, cancellationToken);
                linkedCancellationSources.Add(linkedSource);

                updateBindable(bindable, currentRuleset.Value, currentMods.Value, linkedSource, computationDelay);

                trackedBindables.Add(bindable);
            }

            return bindable;
        }

        /// <summary>
        /// Retrieves the difficulty of a <see cref="IBeatmapInfo"/>.
        /// </summary>
        /// <param name="beatmapInfo">The <see cref="IBeatmapInfo"/> to get the difficulty of.</param>
        /// <param name="rulesetInfo">The <see cref="IRulesetInfo"/> to get the difficulty with.</param>
        /// <param name="mods">The <see cref="Mod"/>s to get the difficulty with.</param>
        /// <param name="cancellationToken">An optional <see cref="CancellationToken"/> which stops computing the star difficulty.</param>
        /// <param name="computationDelay">In the case a cached lookup was not possible, a value in milliseconds of to wait until performing potentially intensive lookup.</param>
        /// <returns>
        /// The requested <see cref="StarDifficulty"/>, if non-<see langword="null"/>.
        /// A <see langword="null"/> return value indicates that the difficulty process failed or was interrupted early,
        /// and as such there is no usable star difficulty value to be returned.
        /// </returns>
        public virtual Task<StarDifficulty?> GetDifficultyAsync(IBeatmapInfo beatmapInfo, IRulesetInfo? rulesetInfo = null, IEnumerable<Mod>? mods = null,
                                                                CancellationToken cancellationToken = default, int computationDelay = 0)
        {
            // In the case that the user hasn't given us a ruleset, use the beatmap's default ruleset.
            rulesetInfo ??= beatmapInfo.Ruleset;

            var localBeatmapInfo = beatmapInfo as BeatmapInfo;
            var localRulesetInfo = rulesetInfo as RulesetInfo;

            // Difficulty can only be computed if the beatmap and ruleset are locally available.
            if (localBeatmapInfo == null || localRulesetInfo == null)
            {
                // If not, fall back to the existing star difficulty (e.g. from an online source).
                return Task.FromResult<StarDifficulty?>(new StarDifficulty(beatmapInfo.StarRating, (beatmapInfo as IBeatmapOnlineInfo)?.MaxCombo ?? 0));
            }

            return GetAsync(new DifficultyCacheLookup(localBeatmapInfo, localRulesetInfo, mods), cancellationToken, computationDelay);
        }

        /// <summary>
        /// Retrieves the currently cached difficulty if available without performing an asynchronous computation.
        /// </summary>
        /// <param name="beatmapInfo">The beatmap to check.</param>
        /// <param name="rulesetInfo">The ruleset to check with.</param>
        /// <param name="orderedMods">Pre-ordered and cloned mods array.</param>
        /// <returns>The cached <see cref="StarDifficulty"/> if available; otherwise <c>null</c>.</returns>
        public StarDifficulty? GetExistingDifficulty(IBeatmapInfo beatmapInfo, IRulesetInfo? rulesetInfo, Mod[] orderedMods)
        {
            rulesetInfo ??= beatmapInfo.Ruleset;

            if (beatmapInfo is not BeatmapInfo localBeatmapInfo || rulesetInfo is not RulesetInfo localRulesetInfo)
                return null;

            var lookup = DifficultyCacheLookup.CreateWithPreorderedMods(localBeatmapInfo, localRulesetInfo, orderedMods);
            return CheckExists(lookup, out var existing) ? existing : null;
        }

        /// <summary>
        /// Retrieves the currently cached difficulty if available without performing an asynchronous computation.
        /// </summary>
        /// <param name="beatmapInfo">The beatmap to check.</param>
        /// <param name="rulesetInfo">The ruleset to check with.</param>
        /// <param name="mods">The mods to check with.</param>
        /// <returns>The cached <see cref="StarDifficulty"/> if available; otherwise <c>null</c>.</returns>
        public StarDifficulty? GetExistingDifficulty(IBeatmapInfo beatmapInfo, IRulesetInfo? rulesetInfo = null, IEnumerable<Mod>? mods = null)
        {
            rulesetInfo ??= beatmapInfo.Ruleset;

            if (beatmapInfo is not BeatmapInfo localBeatmapInfo || rulesetInfo is not RulesetInfo localRulesetInfo)
                return null;

            var lookup = new DifficultyCacheLookup(localBeatmapInfo, localRulesetInfo, mods);
            return CheckExists(lookup, out var existing) ? existing : null;
        }

        /// <summary>
        /// Retrieves the effective star rating for a beatmap, considering active mods and cached difficulties.
        /// If not yet computed in the difficulty cache, falls back to a fast approximation for difficulty-altering mods.
        /// </summary>
        /// <param name="beatmapInfo">The beatmap.</param>
        /// <param name="rulesetInfo">The ruleset.</param>
        /// <param name="mods">The mods.</param>
        /// <param name="orderedMods">Optional pre-ordered and cloned mods array to avoid per-item allocations.</param>
        /// <returns>The effective star rating.</returns>
        public double GetEffectiveStarRating(BeatmapInfo beatmapInfo, IRulesetInfo? rulesetInfo = null, IEnumerable<Mod>? mods = null, Mod[]? orderedMods = null)
        {
            if (mods == null || !ModUtils.FlattenMods(mods).Any(ModAffectsDifficulty))
                return beatmapInfo.StarRating;

            var existing = orderedMods != null
                ? GetExistingDifficulty(beatmapInfo, rulesetInfo, orderedMods)
                : GetExistingDifficulty(beatmapInfo, rulesetInfo, mods);

            if (existing != null)
                return existing.Value.Stars;

            return ApproximateStarRating(beatmapInfo, rulesetInfo, orderedMods ?? mods);
        }

        /// <summary>
        /// Determines whether a given mod alters beatmap difficulty / star rating.
        /// </summary>
        public static bool ModAffectsDifficulty(Mod mod) =>
            mod is IApplicableToDifficulty || mod is IApplicableToRate || mod is ModFlashlight || mod is ModHidden;

        /// <summary>
        /// Computes a fast O(1) approximation of star rating when difficulty-altering mods are applied
        /// and the exact calculation is not yet available in the cache.
        /// </summary>
        public static double ApproximateStarRating(IBeatmapInfo beatmapInfo, IRulesetInfo? rulesetInfo, IEnumerable<Mod> mods)
        {
            double baseStars = beatmapInfo.StarRating;
            if (baseStars <= 0)
                return baseStars;

            string rulesetName = rulesetInfo?.ShortName ?? beatmapInfo.Ruleset?.ShortName ?? @"osu";
            double rate = ModUtils.CalculateRateWithMods(mods);
            var flattenedMods = ModUtils.FlattenMods(mods).ToList();

            double multiplier = 1.0;

            switch (rulesetName)
            {
                case "taiko":
                    if (Math.Abs(rate - 1.0) > 0.001)
                    {
                        double exp = rate > 1.0 ? 0.726 : 0.85;
                        multiplier *= Math.Pow(rate, exp);
                    }

                    foreach (var mod in flattenedMods)
                    {
                        switch (mod)
                        {
                            case ModHardRock:
                                multiplier *= 1.05;
                                break;

                            case ModEasy:
                                multiplier *= 0.90;
                                break;
                        }
                    }
                    break;

                case "fruits":
                    if (Math.Abs(rate - 1.0) > 0.001)
                    {
                        double exp = rate > 1.0 ? 0.602 : 0.75;
                        multiplier *= Math.Pow(rate, exp);
                    }

                    foreach (var mod in flattenedMods)
                    {
                        switch (mod)
                        {
                            case ModHardRock:
                                multiplier *= 1.10;
                                break;

                            case ModEasy:
                                multiplier *= 0.85;
                                break;
                        }
                    }
                    break;

                case "mania":
                    if (Math.Abs(rate - 1.0) > 0.001)
                    {
                        double exp = rate > 1.0 ? 0.430 : 0.55;
                        multiplier *= Math.Pow(rate, exp);
                    }
                    break;

                case "osu":
                default:
                    var diff = beatmapInfo.Difficulty;
                    double cs = diff?.CircleSize ?? 4f;

                    // Clock rate scaling
                    if (Math.Abs(rate - 1.0) > 0.001)
                    {
                        if (rate > 1.0)
                        {
                            // In Torii pp-dev, star rating is driven by the norm of Aim, Speed, and Reading skills.
                            // The effective clock rate scaling exponent is strictly monotonic with base star rating,
                            // scaling from ~0.45-0.54 at 0-1.5★ up to ~0.89 at 6.6★ and ~0.94 at 8★+.
                            double rateExp = baseStars <= 1.5
                                ? 0.45 + 0.12 * Math.Max(0, 1.5 - baseStars) / 1.5
                                : 0.45 + 0.52 * (1.0 - Math.Exp(-0.28 * (baseStars - 1.5)));

                            multiplier *= Math.Pow(rate, rateExp);
                        }
                        else
                        {
                            // HT scaling (rate < 1.0): drops faster for high star maps due to collapsing speed/reading strains
                            double rateExp = 0.58 + 0.35 * Math.Clamp((baseStars - 1.5) / 7.0, 0.0, 1.0);
                            multiplier *= Math.Pow(rate, rateExp);
                        }
                    }

                    // Additional mod multipliers
                    foreach (var mod in flattenedMods)
                    {
                        switch (mod)
                        {
                            case ModHardRock:
                            {
                                double hrFactor = 1.05 + 0.008 * Math.Min(8.0, baseStars) + 0.01 * Math.Max(0, cs - 5.0);
                                multiplier *= hrFactor;
                                break;
                            }

                            case ModEasy:
                            {
                                if (diff != null && diff.ApproachRate >= 7.0f)
                                {
                                    // In Torii pp-dev, EZ halves AR. On higher-AR and higher-star maps, dense overlapping objects
                                    // produce heavy reading strain (raising star rating), whereas low-AR/starter maps see a difficulty reduction.
                                    double ar = diff.ApproachRate;
                                    double ezFactor = 0.85 + 0.03 * Math.Clamp(baseStars - 2.0, 0.0, 5.0) + 0.025 * (ar - 7.0);
                                    multiplier *= ezFactor;
                                }
                                else
                                {
                                    multiplier *= 0.85;
                                }
                                break;
                            }

                            case ModDifficultyAdjust da:
                            {
                                if (diff != null)
                                {
                                    if (da.SettingsMap.TryGetValue("circle_size", out var csSetting) && csSetting is IBindable<float?> csBindable && csBindable.Value is float csVal)
                                        multiplier *= Math.Pow(1.05, csVal - diff.CircleSize);
                                    if (da.SettingsMap.TryGetValue("approach_rate", out var arSetting) && arSetting is IBindable<float?> arBindable && arBindable.Value is float arVal)
                                        multiplier *= Math.Pow(1.02, arVal - diff.ApproachRate);
                                }
                                break;
                            }

                            case ModFlashlight:
                            {
                                multiplier *= 1.15;
                                break;
                            }

                            case ModHidden:
                                multiplier *= 1.035;
                                break;
                        }
                    }
                    break;
            }

            return baseStars * multiplier;
        }

        protected override Task<StarDifficulty?> ComputeValueAsync(DifficultyCacheLookup lookup, CancellationToken cancellationToken = default)
        {
            return Task.Factory.StartNew(() =>
            {
                if (CheckExists(lookup, out var existing))
                    return existing;

                return computeDifficulty(lookup, cancellationToken);
            }, cancellationToken, TaskCreationOptions.HideScheduler | TaskCreationOptions.RunContinuationsAsynchronously, updateScheduler);
        }

        protected override bool CacheNullValues => false;

        public Task<List<TimedDifficultyAttributes>> GetTimedDifficultyAttributesAsync(IWorkingBeatmap beatmap, Ruleset ruleset, Mod[] mods, CancellationToken cancellationToken = default)
        {
            return Task.Factory.StartNew(() => ruleset.CreateDifficultyCalculator(beatmap).CalculateTimed(mods, cancellationToken),
                cancellationToken,
                TaskCreationOptions.HideScheduler | TaskCreationOptions.RunContinuationsAsynchronously,
                updateScheduler);
        }

        /// <summary>
        /// Updates all tracked <see cref="BindableStarDifficulty"/> using the current ruleset and mods.
        /// </summary>
        private void updateTrackedBindables()
        {
            lock (bindableUpdateLock)
            {
                cancelTrackedBindableUpdate();

                foreach (var b in trackedBindables)
                {
                    if (b.BeatmapInfo is BeatmapInfo localInfo)
                    {
                        double effective = GetEffectiveStarRating(localInfo, currentRuleset.Value, currentMods.Value);
                        b.Value = new StarDifficulty(effective, b.Value.MaxCombo);
                    }

                    var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(trackedUpdateCancellationSource.Token, b.CancellationToken);
                    linkedCancellationSources.Add(linkedSource);

                    updateBindable(b, currentRuleset.Value, currentMods.Value, linkedSource);
                }
            }
        }

        /// <summary>
        /// Cancels the existing update of all tracked <see cref="BindableStarDifficulty"/> via <see cref="updateTrackedBindables"/>.
        /// </summary>
        private void cancelTrackedBindableUpdate()
        {
            lock (bindableUpdateLock)
            {
                debouncedModSettingsChange?.Cancel();
                debouncedModSettingsChange = null;

                trackedUpdateCancellationSource.Cancel();
                trackedUpdateCancellationSource = new CancellationTokenSource();

                foreach (var c in linkedCancellationSources)
                    c.Dispose();

                linkedCancellationSources.Clear();
            }
        }

        /// <summary>
        /// Updates the value of a <see cref="BindableStarDifficulty"/> with a given ruleset + mods.
        /// </summary>
        /// <param name="bindable">The <see cref="BindableStarDifficulty"/> to update.</param>
        /// <param name="rulesetInfo">The <see cref="IRulesetInfo"/> to update with.</param>
        /// <param name="mods">The <see cref="Mod"/>s to update with.</param>
        /// <param name="linkedCancellationTokenSource">
        /// A cancellation token source that may be used to cancel this update.
        /// This token will be cancelled in one of two scenarios:
        /// <list type="bullet">
        /// <item>The owner of the bindable has requested the cancellation.</item>
        /// <item>An <see cref="Invalidate"/> call has been issued, and as such ongoing calculations must be aborted to avoid stale values being potentially written to bindables.</item>
        /// </list>
        /// </param>
        /// <param name="computationDelay">In the case a cached lookup was not possible, a value in milliseconds of to wait until performing potentially intensive lookup.</param>
        private void updateBindable(BindableStarDifficulty bindable, IRulesetInfo? rulesetInfo, IEnumerable<Mod>? mods, CancellationTokenSource linkedCancellationTokenSource, int computationDelay = 0)
        {
            // GetDifficultyAsync will fall back to existing data from IBeatmapInfo if not locally available
            // (contrary to GetAsync)
            GetDifficultyAsync(bindable.BeatmapInfo, rulesetInfo, mods, linkedCancellationTokenSource.Token, computationDelay)
                .ContinueWith(task =>
                    {
                        // We're on a threadpool thread, but we should exit back to the update thread so consumers can safely handle value-changed events.
                        Schedule(() =>
                        {
                            if (!linkedCancellationTokenSource.IsCancellationRequested)
                            {
                                StarDifficulty? starDifficulty = task.GetResultSafely();

                                if (starDifficulty != null)
                                {
                                    double oldStars = bindable.Value.Stars;
                                    bindable.Value = starDifficulty.Value;

                                    // Only fire DifficultyUpdated if the star rating actually changed noticeably (> 0.01★).
                                    // This prevents firing on cache hits and eliminates infinite carousel re-filter loops.
                                    if (Math.Abs(oldStars - starDifficulty.Value.Stars) > 0.01)
                                        DifficultyUpdated?.Invoke(bindable.BeatmapInfo, starDifficulty.Value);
                                }
                            }

                            // Once the linked cancellation token source is of no remaining use to anybody, clean it up.
                            lock (bindableUpdateLock)
                            {
                                linkedCancellationSources.Remove(linkedCancellationTokenSource);
                                linkedCancellationTokenSource.Dispose();
                            }
                        });
                    },
                    // This continuation MUST run even if the antecedent `GetDifficultyAsync()` call was canceled in order to clean up `linkedCancellationTokenSource`.
                    // Due to this, `ContinueWith()` CANNOT accept `linkedCancellationTokenSource.Token` here, because if it did, then in an event of a cancellation,
                    // the continuation would never be scheduled for execution.
                    CancellationToken.None);
        }

        /// <summary>
        /// Computes the difficulty defined by a <see cref="DifficultyCacheLookup"/> key, and stores it to the timed cache.
        /// </summary>
        /// <param name="key">The <see cref="DifficultyCacheLookup"/> that defines the computation parameters.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The <see cref="StarDifficulty"/>.</returns>
        private StarDifficulty? computeDifficulty(in DifficultyCacheLookup key, CancellationToken cancellationToken = default)
        {
            // In the case that the user hasn't given us a ruleset, use the beatmap's default ruleset.
            var beatmapInfo = key.BeatmapInfo;
            var rulesetInfo = key.Ruleset;

            try
            {
                var ruleset = rulesetInfo.CreateInstance();
                Debug.Assert(ruleset != null);

                PlayableCachedWorkingBeatmap workingBeatmap = new PlayableCachedWorkingBeatmap(beatmapManager.GetWorkingBeatmap(key.BeatmapInfo));
                IBeatmap playableBeatmap = workingBeatmap.GetPlayableBeatmap(ruleset.RulesetInfo, key.OrderedMods, cancellationToken);

                var difficulty = ruleset.CreateDifficultyCalculator(workingBeatmap).Calculate(key.OrderedMods, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                var performanceCalculator = ruleset.CreatePerformanceCalculator();
                if (performanceCalculator == null)
                    return new StarDifficulty(difficulty, new PerformanceAttributes());

                ScoreProcessor scoreProcessor = ruleset.CreateScoreProcessor();
                scoreProcessor.Mods.Value = key.OrderedMods;
                scoreProcessor.ApplyBeatmap(playableBeatmap);
                cancellationToken.ThrowIfCancellationRequested();

                ScoreInfo perfectScore = new ScoreInfo(key.BeatmapInfo, ruleset.RulesetInfo)
                {
                    Passed = true,
                    Accuracy = 1,
                    Mods = key.OrderedMods,
                    MaxCombo = scoreProcessor.MaximumCombo,
                    Combo = scoreProcessor.MaximumCombo,
                    TotalScore = scoreProcessor.MaximumTotalScore,
                    Statistics = scoreProcessor.MaximumStatistics,
                    MaximumStatistics = scoreProcessor.MaximumStatistics
                };

                var performance = performanceCalculator.Calculate(perfectScore, difficulty);
                cancellationToken.ThrowIfCancellationRequested();

                return new StarDifficulty(difficulty, performance);
            }
            catch (OperationCanceledException)
            {
                // no need to log, cancellations are expected as part of normal operation.
                return null;
            }
            catch (BeatmapInvalidForRulesetException invalidForRuleset)
            {
                if (rulesetInfo.Equals(beatmapInfo.Ruleset))
                    Logger.Error(invalidForRuleset, $"Failed to convert {beatmapInfo.OnlineID} to the beatmap's default ruleset ({beatmapInfo.Ruleset}).");

                return null;
            }
            catch (Exception unknownException)
            {
                Logger.Error(unknownException, "Failed to calculate beatmap difficulty");

                return null;
            }
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            modSettingChangeTracker?.Dispose();

            cancelTrackedBindableUpdate();
            updateScheduler.Dispose();
        }

        public readonly struct DifficultyCacheLookup : IEquatable<DifficultyCacheLookup>
        {
            public readonly BeatmapInfo BeatmapInfo;
            public readonly RulesetInfo Ruleset;
            public readonly Mod[] OrderedMods;

            public static DifficultyCacheLookup CreateWithPreorderedMods(BeatmapInfo beatmapInfo, RulesetInfo? ruleset, Mod[] orderedMods)
            {
                return new DifficultyCacheLookup(beatmapInfo, ruleset, orderedMods, isPreordered: true);
            }

            private DifficultyCacheLookup(BeatmapInfo beatmapInfo, RulesetInfo? ruleset, Mod[] orderedMods, bool isPreordered)
            {
                BeatmapInfo = beatmapInfo;
                Ruleset = ruleset ?? BeatmapInfo.Ruleset;
                OrderedMods = orderedMods;
            }

            public DifficultyCacheLookup(BeatmapInfo beatmapInfo, RulesetInfo? ruleset, IEnumerable<Mod>? mods)
            {
                BeatmapInfo = beatmapInfo;
                // In the case that the user hasn't given us a ruleset, use the beatmap's default ruleset.
                Ruleset = ruleset ?? BeatmapInfo.Ruleset;
                OrderedMods = mods?.OrderBy(m => m.Acronym).Select(mod => mod.DeepClone()).ToArray() ?? Array.Empty<Mod>();
            }

            public bool Equals(DifficultyCacheLookup other)
                => BeatmapInfo.Equals(other.BeatmapInfo)
                   && Ruleset.Equals(other.Ruleset)
                   && OrderedMods.SequenceEqual(other.OrderedMods);

            public override int GetHashCode()
            {
                var hashCode = new HashCode();

                hashCode.Add(BeatmapInfo.ID);
                hashCode.Add(Ruleset.ShortName);

                foreach (var mod in OrderedMods)
                    hashCode.Add(mod);

                return hashCode.ToHashCode();
            }
        }

        private class BindableStarDifficulty : Bindable<StarDifficulty>
        {
            public IBeatmapInfo BeatmapInfo;
            public readonly CancellationToken CancellationToken;

            public BindableStarDifficulty(IBeatmapInfo beatmapInfo, CancellationToken cancellationToken)
            {
                BeatmapInfo = beatmapInfo;
                CancellationToken = cancellationToken;
            }
        }

        /// <summary>
        /// A working beatmap that caches its playable representation.
        /// This is intended as single-use for when it is guaranteed that the playable beatmap can be reused.
        /// </summary>
        private class PlayableCachedWorkingBeatmap : IWorkingBeatmap
        {
            private readonly IWorkingBeatmap working;
            private IBeatmap? playable;

            public PlayableCachedWorkingBeatmap(IWorkingBeatmap working)
            {
                this.working = working;
            }

            public IBeatmap GetPlayableBeatmap(IRulesetInfo ruleset, IReadOnlyList<Mod> mods)
                => playable ??= working.GetPlayableBeatmap(ruleset, mods);

            public IBeatmap GetPlayableBeatmap(IRulesetInfo ruleset, IReadOnlyList<Mod> mods, CancellationToken cancellationToken)
                => playable ??= working.GetPlayableBeatmap(ruleset, mods, cancellationToken);

            IBeatmapInfo IWorkingBeatmap.BeatmapInfo => working.BeatmapInfo;
            bool IWorkingBeatmap.BeatmapLoaded => working.BeatmapLoaded;
            bool IWorkingBeatmap.TrackLoaded => working.TrackLoaded;
            IBeatmap IWorkingBeatmap.Beatmap => working.Beatmap;
            Texture IWorkingBeatmap.GetBackground() => working.GetBackground();
            Texture IWorkingBeatmap.GetPanelBackground() => working.GetPanelBackground();
            Waveform IWorkingBeatmap.Waveform => working.Waveform;
            Storyboard IWorkingBeatmap.Storyboard => working.Storyboard;
            ISkin IWorkingBeatmap.Skin => working.Skin;
            Track IWorkingBeatmap.Track => working.Track;
            Track IWorkingBeatmap.LoadTrack() => working.LoadTrack();
            Stream IWorkingBeatmap.GetStream(string storagePath) => working.GetStream(storagePath);
            void IWorkingBeatmap.BeginAsyncLoad() => working.BeginAsyncLoad();
            void IWorkingBeatmap.CancelAsyncLoad() => working.CancelAsyncLoad();
            void IWorkingBeatmap.PrepareTrackForPreview(bool looping, double? offsetFromPreviewPoint) => working.PrepareTrackForPreview(looping, offsetFromPreviewPoint);
        }
    }
}
