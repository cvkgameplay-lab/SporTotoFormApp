using SporTotoFormApp.Data;
using SporTotoFormApp.Interfaces;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace SporTotoFormApp.Services
{
    public sealed class CounterfactualParameterSearchService
    {
        private const int ProgressLoopSize = 1000;
        private const int LogInterval = 500;
        private const int CheckpointInterval = 2500;
        private const int AttemptFlushInterval = 50;
        private const int ResponsivenessInterval = 10;
        private const decimal CouponUnitCost = 10m;
        private const decimal MaxAuditCostAmount = 1000m;
        private const int MaxAuditCouponCount = (int)(MaxAuditCostAmount / CouponUnitCost);
        private readonly PredictionRepository _repository;

        public CounterfactualParameterSearchService(PredictionRepository? repository = null)
        {
            _repository = repository ?? new PredictionRepository();
        }

        public async Task<CounterfactualParameterSearchResult> SearchAndStoreAsync(
            ITestView view,
            int maxRounds = 4,
            int? roundId = null,
            CancellationToken cancellationToken = default)
        {
            var targets = await _repository.LoadCounterfactualBacktestTargetsAsync(
                maxRounds,
                roundId,
                cancellationToken);
            if (targets.Count == 0)
            {
                var targetText = roundId.HasValue
                    ? $"Round {roundId.Value}"
                    : "secili aralik";
                view.Log($"{targetText} icin mac matrisi olan tamamlanmis run bulunamadi.", Color.Orange);
                return new CounterfactualParameterSearchResult(0, 0, 0, 0, 0, 0, 0, []);
            }

            var searchBatchId = Guid.NewGuid();
            var initialGrid = BuildInitialCoverageExactOptionGrid().ToList();
            var testedCount = 0;
            var storedStrategyCount = 0;
            var exactCount = 0;
            var summaries = new List<string>();
            var visualizer = view as ICounterfactualSearchVisualization;

            view.ProgressBarMaxValue = ProgressLoopSize;
            view.ProgressBarValue = 0;
            view.Log(
                $"Geriye donuk surekli otopsi basladi | Hafta:{targets.Count} | Ilk grid:{initialGrid.Count:n0} | Batch:{searchBatchId}",
                Color.DeepSkyBlue);
            view.Log(
                $"Mod: Maliyet siniri {MaxAuditCostAmount:n0} TL ({MaxAuditCouponCount:n0} kolon). Arama 14+ bilen/karli parametrelerin cevresinde baslar; seed komsulugu biterse orbit fazi 15/15 bulana kadar devam eder.",
                Color.LightSteelBlue);

            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var model = HistoricalOutcomeModel.Create(
                    AppDomain.CurrentDomain.BaseDirectory,
                    target.MatchProbabilities);
                var payoutProfile = await _repository.LoadRoundPayoutProfileAsync(
                    target.RoundId,
                    cancellationToken);
                var searchSeeds = BuildMultiSeedPortfolio(await _repository.LoadCounterfactualSearchSeedsAsync(
                    target.RoundId,
                    60,
                    cancellationToken));
                var bootstrapGrid = searchSeeds.Count > 0
                    ? BuildSeedBootstrapOptionGrid(searchSeeds).ToList()
                    : initialGrid;
                var seenOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                LearnedPredictionStrategyCandidate? bestCandidate = null;
                LearnedPredictionStrategyCandidate? exactCandidate = null;
                var roundTestedCount = 0;
                var roundCheckpointCount = 0;
                var skippedAlreadyTriedCount = 0;
                LearnedPredictionStrategyCandidate? lastCheckpointCandidate = null;
                var attemptBuffer = new List<LearnedPredictionStrategyCandidate>(AttemptFlushInterval);

                var triedOptions = await _repository.LoadCounterfactualTriedOptionKeysAsync(
                    target.RoundId,
                    cancellationToken);
                foreach (var triedOption in triedOptions)
                {
                    seenOptions.Add(GetCoverageSignature(
                        triedOption.CouponCount,
                        triedOption.ThirdChoiceMinRatio,
                        triedOption.ProbabilityUniformBlend,
                        triedOption.PatternScoreWeight));
                }

                view.Log(
                    $"Round {target.RoundId} otopsi basladi | Kaynak run:{target.SourceRunId} | Gercek:{target.ActualResultLine}",
                    Color.LightSteelBlue);
                visualizer?.ResetCounterfactualSearchChart(target.RoundId, target.ActualResultLine);
                if (payoutProfile != null)
                {
                    view.Log(
                        $"Round {target.RoundId}: ikramiye profili | 15:{payoutProfile.Prize15:n2} | 14:{payoutProfile.Prize14:n2} | 13:{payoutProfile.Prize13:n2} | 12:{payoutProfile.Prize12:n2} | Kolon:{CouponUnitCost:n2} TL",
                        Color.LightSteelBlue);
                }
                else
                {
                    view.Log(
                        $"Round {target.RoundId}: ikramiye profili bulunamadi; ROI/kâr 0 hesaplanacak.",
                        Color.Orange);
                }
                if (triedOptions.Count > 0)
                {
                    view.Log(
                        $"Round {target.RoundId}: DB resume aktif | Daha once denenmis farkli parametre:{triedOptions.Count:n0} | Arama bunlari atlayacak.",
                        Color.LightSteelBlue);
                }
                if (searchSeeds.Count > 0)
                {
                    var bestSeed = searchSeeds.First();
                    view.Log(
                        $"Round {target.RoundId}: basarili merkez bulundu | Seed:{searchSeeds.Count:n0} | Seed round:{bestSeed.SourceRoundId} | Exact:{(bestSeed.FoundExact ? "evet" : "hayir")} | En iyi:{bestSeed.BestHitCount}/15 | 14 kolon:{bestSeed.Hit14Count:n0} | Net:{bestSeed.NetProfitAmount:n2} TL | ROI:{bestSeed.Roi:P2} | Tam grid yerine seed cevresi deneniyor.",
                        Color.LightSteelBlue);
                    view.Log(
                        $"Round {target.RoundId}: multi-seed aktif | exact + en yuksek ROI + stabil 14 + dusuk kolon/yuksek basari | Endless faz: %60 exploitation / %40 global low-discrepancy.",
                        Color.LightSteelBlue);
                }

                var existingExactSeed = searchSeeds.FirstOrDefault(x =>
                    x.SourceRoundId == target.RoundId &&
                    (x.FoundExact || x.BestHitCount >= 15));
                if (existingExactSeed != null)
                {
                    var existingSummary = BuildExistingExactSummary(target.RoundId, existingExactSeed);
                    summaries.Add(existingSummary);
                    exactCount++;
                    view.Log(
                        $"Round {target.RoundId}: 15/15 parametre DB'de zaten mevcut; yeniden arama yapilmadi. | {existingSummary}",
                        Color.LimeGreen);
                    continue;
                }

                foreach (var option in bootstrapGrid)
                {
                    if (await EvaluateOptionAsync(
                            option,
                            searchSeeds.Count > 0 ? "SeedBootstrap" : "InitialGrid",
                            shouldSkipSeen: true))
                    {
                        break;
                    }
                }

                if (exactCandidate == null)
                {
                    view.Log(
                        $"Round {target.RoundId}: exact yok. Maliyet sinirli seed-cevre genisleme fazina gecildi. Durdurmak icin OTOPSIYI DURDUR'a bas.",
                        Color.Yellow);

                    await foreach (var option in BuildEndlessCoverageExactOptionsAsync(
                                       searchSeeds,
                                       cancellationToken))
                    {
                        if (await EvaluateOptionAsync(option, "EndlessExpansion", shouldSkipSeen: true))
                        {
                            break;
                        }
                    }
                }

                if (exactCandidate == null)
                {
                    throw new OperationCanceledException(
                        $"Round {target.RoundId} exact bulunmadan iptal edildi.",
                        cancellationToken);
                }

                await FlushAttemptBufferAsync();
                await _repository.SaveLearnedPredictionStrategiesAsync([exactCandidate], cancellationToken);
                storedStrategyCount++;
                exactCount++;

                var summary = BuildSummary(exactCandidate);
                summaries.Add(summary);
                view.Log($"15/15 PARAMETRE BULUNDU VE DB'YE YAZILDI | {summary}", Color.LimeGreen);
                view.Log(
                    $"Round {target.RoundId} tamam | Denenen:{roundTestedCount:n0} | Checkpoint:{roundCheckpointCount:n0} | Exact:1",
                    Color.LimeGreen);
                continue;

                async Task<bool> EvaluateOptionAsync(
                    OptimizationOptions option,
                    string mode,
                    bool shouldSkipSeen)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (shouldSkipSeen && !seenOptions.Add(GetCoverageSignature(option)))
                    {
                        skippedAlreadyTriedCount++;
                        if (skippedAlreadyTriedCount == 1 ||
                            skippedAlreadyTriedCount % 5000 == 0)
                        {
                            view.Log(
                                $"Round {target.RoundId}: daha once denenmis parametre atlandi:{skippedAlreadyTriedCount:n0}",
                                Color.DarkGray);
                            await Task.Yield();
                        }

                        return false;
                    }

                    testedCount++;
                    roundTestedCount++;
                    UpdateLoopProgress(view, testedCount);

                    var coverage = CoverageScenarioGenerator.Generate(
                        model,
                        option.DesiredCouponCount,
                        option.ThirdChoiceMinRatio,
                        option.ProbabilityUniformBlend,
                        option.PatternScoreWeight);
                    var hitSummary = BuildCoverageHitSummary(coverage, target.ActualResultLine);
                    hitSummary = ApplyFinancials(hitSummary, payoutProfile);
                    var candidate = BuildCandidate(
                        searchBatchId,
                        target,
                        option,
                        hitSummary,
                        hitSummary.Hit15Count > 0
                            ? $"{mode}:Exact"
                            : $"{mode}:BestSoFar");
                    attemptBuffer.Add(candidate);
                    bestCandidate = BetterOf(bestCandidate, candidate);

                    if (ShouldReportVisualizationPoint(candidate, roundTestedCount))
                    {
                        visualizer?.ReportCounterfactualSearchPoint(
                            target.RoundId,
                            option.ThirdChoiceMinRatio,
                            option.ProbabilityUniformBlend,
                            option.DesiredCouponCount,
                            candidate.BestHitCount,
                            candidate.NetProfitAmount,
                            candidate.Roi,
                            candidate.FoundExact);
                    }

                    if (attemptBuffer.Count >= AttemptFlushInterval)
                    {
                        await FlushAttemptBufferAsync();
                    }

                    if (candidate.FoundExact)
                    {
                        exactCandidate = candidate;
                        return true;
                    }

                    if (roundTestedCount == 1 || roundTestedCount % LogInterval == 0)
                    {
                        view.Log(
                            $"Round {target.RoundId}: devam ediyor | Faz:{mode} | Denenen:{roundTestedCount:n0} | En iyi:{bestCandidate?.BestHitCount ?? 0}/15 | 14 kolon:{bestCandidate?.Hit14Count ?? 0:n0} | Son kolon:{option.DesiredCouponCount:n0}",
                            Color.DimGray);
                    }

                    if (roundTestedCount % CheckpointInterval == 0)
                    {
                        await FlushAttemptBufferAsync();
                        var checkpoint = candidate with
                        {
                            Notes = $"{candidate.Notes} | Checkpoint=1 | RoundTested={roundTestedCount:n0} | BestSoFar={bestCandidate?.BestHitCount ?? candidate.BestHitCount}/15"
                        };
                        if (lastCheckpointCandidate == null ||
                            !string.Equals(
                                GetCoverageSignature(lastCheckpointCandidate.Options),
                                GetCoverageSignature(checkpoint.Options),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            await _repository.SaveLearnedPredictionStrategiesAsync([checkpoint], cancellationToken);
                            storedStrategyCount++;
                            roundCheckpointCount++;
                            lastCheckpointCandidate = checkpoint;
                            view.Log(
                                $"Round {target.RoundId}: checkpoint DB'ye yazildi | Denenen:{roundTestedCount:n0} | Checkpoint param:{FormatOption(checkpoint.Options)} | Bu param:{checkpoint.BestHitCount}/15 | Net:{checkpoint.NetProfitAmount:n2} TL | ROI:{checkpoint.Roi:P2} | En iyi:{bestCandidate?.BestHitCount ?? checkpoint.BestHitCount}/15",
                                Color.DarkGray);
                        }
                    }

                    if (roundTestedCount % ResponsivenessInterval == 0)
                    {
                        await Task.Yield();
                    }

                    return false;
                }

                async Task FlushAttemptBufferAsync()
                {
                    if (attemptBuffer.Count == 0)
                    {
                        return;
                    }

                    await _repository.SaveCounterfactualParameterAttemptsAsync(
                        attemptBuffer,
                        cancellationToken);
                    attemptBuffer.Clear();
                }
            }

            view.Log(
                $"Geriye donuk otopsi tamam | Denenen:{testedCount:n0} | DB kayit:{storedStrategyCount:n0} | Exact:{exactCount:n0}",
                exactCount > 0 ? Color.LimeGreen : Color.Yellow);

            return new CounterfactualParameterSearchResult(
                targets.Count,
                initialGrid.Count,
                testedCount,
                0,
                0,
                storedStrategyCount,
                exactCount,
                summaries);
        }

        private static IEnumerable<OptimizationOptions> BuildInitialCoverageExactOptionGrid()
        {
            int[] couponCounts = [10, 20, 30, 40, 50, 75, 100];
            var thirdChoiceRatios = BuildRange(0.0, 1.01, 0.01);
            var uniformBlends = BuildRange(0.0, 0.35, 0.01);

            foreach (var couponCount in couponCounts)
            foreach (var thirdChoiceRatio in thirdChoiceRatios)
            foreach (var uniformBlend in uniformBlends)
            {
                yield return CreateOptions(couponCount, thirdChoiceRatio, uniformBlend);
            }
        }

        private static IEnumerable<OptimizationOptions> BuildSeedBootstrapOptionGrid(
            IReadOnlyList<CounterfactualSearchSeed> seeds)
        {
            int[] couponCounts = [10, 20, 30, 40, 50, 75, 100];
            var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var seed in seeds)
            foreach (var radius in new[] { 1, 2, 3 })
            {
                var delta = 0.005 * radius;
                var patternDelta = 0.03 * radius;
                foreach (var couponCount in BuildCouponNeighborhood(seed.CouponCount, couponCounts))
                foreach (var thirdChoiceRatio in BuildNeighborhoodValues(seed.ThirdChoiceMinRatio, 0.0, 1.01, delta))
                foreach (var uniformBlend in BuildNeighborhoodValues(seed.ProbabilityUniformBlend, 0.0, 0.35, delta))
                foreach (var patternScoreWeight in BuildNeighborhoodValues(seed.PatternScoreWeight, 0.0, 2.0, patternDelta))
                {
                    var option = CreateOptions(couponCount, thirdChoiceRatio, uniformBlend, patternScoreWeight);
                    if (yielded.Add(GetCoverageSignature(option)))
                    {
                        yield return option;
                    }
                }
            }
        }

        private static IReadOnlyList<CounterfactualSearchSeed> BuildMultiSeedPortfolio(
            IReadOnlyList<CounterfactualSearchSeed> seeds)
        {
            if (seeds.Count == 0)
            {
                return seeds;
            }

            var result = new List<CounterfactualSearchSeed>();
            AddSeed(seeds
                .Where(x => x.FoundExact || x.BestHitCount >= 15)
                .OrderByDescending(x => x.FoundExact)
                .ThenByDescending(x => x.NetProfitAmount)
                .ThenByDescending(x => x.Roi)
                .FirstOrDefault());

            AddSeed(seeds
                .Where(x => x.NetProfitAmount > 0m || x.Roi > 0)
                .OrderByDescending(x => x.NetProfitAmount)
                .ThenByDescending(x => x.Roi)
                .ThenByDescending(x => x.BestHitCount)
                .FirstOrDefault());

            AddSeed(seeds
                .Where(x => x.BestHitCount >= 14)
                .OrderByDescending(x => x.Hit14Count)
                .ThenByDescending(x => x.BestHitCount)
                .ThenByDescending(x => x.NetProfitAmount)
                .FirstOrDefault());

            AddSeed(seeds
                .Where(x => x.BestHitCount >= 14 || x.NetProfitAmount > 0m)
                .OrderBy(x => x.CouponCount)
                .ThenByDescending(x => x.BestHitCount)
                .ThenByDescending(x => x.Roi)
                .FirstOrDefault());

            foreach (var seed in seeds
                         .OrderByDescending(x => x.FoundExact)
                         .ThenByDescending(x => x.BestHitCount)
                         .ThenByDescending(x => x.NetProfitAmount)
                         .ThenByDescending(x => x.Roi))
            {
                AddSeed(seed);
                if (result.Count >= 40)
                {
                    break;
                }
            }

            return result;

            void AddSeed(CounterfactualSearchSeed? seed)
            {
                if (seed == null)
                {
                    return;
                }

                var exists = result.Any(x =>
                    x.CouponCount == seed.CouponCount &&
                    Math.Abs(x.ThirdChoiceMinRatio - seed.ThirdChoiceMinRatio) < 0.00001 &&
                    Math.Abs(x.ProbabilityUniformBlend - seed.ProbabilityUniformBlend) < 0.00001 &&
                    Math.Abs(x.PatternScoreWeight - seed.PatternScoreWeight) < 0.00001);
                if (!exists)
                {
                    result.Add(seed);
                }
            }
        }

        private static async IAsyncEnumerable<OptimizationOptions> BuildEndlessCoverageExactOptionsAsync(
            IReadOnlyList<CounterfactualSearchSeed> seeds,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            int[] couponCounts = [10, 20, 30, 40, 50, 75, 100];
            var effectiveSeeds = seeds.Count > 0
                ? seeds
                : [new CounterfactualSearchSeed(0, false, 100, 0.35, 0.08, 0.35, 0, 0, 0m, 0.0)];
            var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var orbitIndex = 0;
            var globalIndex = 0;
            var exploitationYieldCounter = 0;

            for (var radius = 1; ; radius++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var delta = Math.Min(0.005 * radius, 0.35);
                var patternDelta = Math.Min(0.02 * radius, 2.0);
                var yieldedThisRadius = false;

                foreach (var seed in effectiveSeeds)
                foreach (var couponCount in BuildCouponNeighborhood(seed.CouponCount, couponCounts))
                foreach (var thirdChoiceRatio in BuildNeighborhoodValues(seed.ThirdChoiceMinRatio, 0.0, 1.01, delta))
                foreach (var uniformBlend in BuildNeighborhoodValues(seed.ProbabilityUniformBlend, 0.0, 0.35, delta))
                foreach (var patternScoreWeight in BuildNeighborhoodValues(seed.PatternScoreWeight, 0.0, 2.0, patternDelta))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var option = CreateOptions(couponCount, thirdChoiceRatio, uniformBlend, patternScoreWeight);
                    if (yielded.Add(GetCoverageSignature(option)))
                    {
                        yieldedThisRadius = true;
                        yield return option;
                        exploitationYieldCounter++;
                        foreach (var globalOption in BuildScheduledGlobalOptions(
                                     couponCounts,
                                     yielded,
                                     ref globalIndex,
                                     ref exploitationYieldCounter))
                        {
                            yield return globalOption;
                        }
                    }
                }

                if (!yieldedThisRadius)
                {
                    var orbitBatchSize = Math.Min(Math.Max(effectiveSeeds.Count, 1) * 6, 180);
                    for (var i = 0; i < orbitBatchSize; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var seed = effectiveSeeds[orbitIndex % effectiveSeeds.Count];
                        var seedCoupons = BuildCouponNeighborhood(seed.CouponCount, couponCounts).ToArray();
                        var couponCount = seedCoupons[(orbitIndex / Math.Max(effectiveSeeds.Count, 1)) % seedCoupons.Length];
                        var option = CreateOptions(
                            couponCount,
                            BuildOrbitValue(seed.ThirdChoiceMinRatio, 0.0, 1.01, orbitIndex, 0),
                            BuildOrbitValue(seed.ProbabilityUniformBlend, 0.0, 0.35, orbitIndex, 1),
                            BuildOrbitValue(seed.PatternScoreWeight, 0.0, 2.0, orbitIndex, 2));
                        orbitIndex++;

                        if (yielded.Add(GetCoverageSignature(option)))
                        {
                            yieldedThisRadius = true;
                            yield return option;
                            exploitationYieldCounter++;
                            foreach (var globalOption in BuildScheduledGlobalOptions(
                                         couponCounts,
                                         yielded,
                                         ref globalIndex,
                                         ref exploitationYieldCounter))
                            {
                                yield return globalOption;
                            }
                        }
                    }
                }

                await Task.Yield();
            }
        }

        private static IReadOnlyList<OptimizationOptions> BuildScheduledGlobalOptions(
            IReadOnlyList<int> couponCounts,
            HashSet<string> yielded,
            ref int globalIndex,
            ref int exploitationYieldCounter)
        {
            var result = new List<OptimizationOptions>(2);
            if (exploitationYieldCounter < 3)
            {
                return result;
            }

            exploitationYieldCounter = 0;
            var emitted = 0;
            var guard = 0;
            while (emitted < 2 && guard < 200)
            {
                guard++;
                var option = BuildGlobalExplorationOption(couponCounts, globalIndex++);
                if (yielded.Add(GetCoverageSignature(option)))
                {
                    emitted++;
                    result.Add(option);
                }
            }

            return result;
        }

        private static OptimizationOptions BuildGlobalExplorationOption(
            IReadOnlyList<int> couponCounts,
            int globalIndex)
        {
            var index = globalIndex + 1;
            var couponUnit = BuildHaltonUnit(index, 4);
            var couponIndex = Math.Clamp(
                (int)Math.Floor(couponUnit * couponCounts.Count),
                0,
                couponCounts.Count - 1);

            return CreateOptions(
                couponCounts[couponIndex],
                BuildGlobalValue(index, 0.0, 1.01, 5),
                BuildGlobalValue(index, 0.0, 0.35, 6),
                BuildGlobalValue(index, 0.0, 2.0, 7));
        }

        private static IEnumerable<int> BuildCouponNeighborhood(
            int seedCouponCount,
            IReadOnlyList<int> allowedCouponCounts)
        {
            var clampedSeed = Math.Clamp(seedCouponCount, allowedCouponCounts.Min(), MaxAuditCouponCount);
            return allowedCouponCounts
                .OrderBy(x => Math.Abs(x - clampedSeed))
                .ThenBy(x => x)
                .Take(5);
        }

        private static IEnumerable<double> BuildNeighborhoodValues(
            double center,
            double minimum,
            double maximum,
            double delta)
        {
            return new[]
                {
                    center,
                    center - delta,
                    center + delta,
                    center - (delta * 2.0),
                    center + (delta * 2.0)
                }
                .Select(x => Math.Clamp(x, minimum, maximum))
                .Select(x => Math.Round(x, 4))
                .Distinct();
        }

        private static double BuildOrbitValue(
            double center,
            double minimum,
            double maximum,
            int orbitIndex,
            int salt)
        {
            var span = maximum - minimum;
            if (span <= 0)
            {
                return Math.Round(minimum, 4);
            }

            var localUnit = BuildHaltonUnit(orbitIndex + 1, salt);
            var globalUnit = BuildHaltonUnit(orbitIndex + 7919 + (salt * 3571), salt + 3);
            var useGlobalProbe = (orbitIndex + (salt * 17)) % 11 == 0;
            var value = useGlobalProbe
                ? minimum + (globalUnit * span)
                : BuildLocalOrbitValue(center, minimum, maximum, span, orbitIndex, localUnit);

            return Math.Round(Math.Clamp(value, minimum, maximum), 4);
        }

        private static double BuildGlobalValue(
            int index,
            double minimum,
            double maximum,
            int salt)
        {
            var unit = BuildHaltonUnit(index, salt);
            return Math.Round(minimum + (unit * (maximum - minimum)), 4);
        }

        private static double BuildLocalOrbitValue(
            double center,
            double minimum,
            double maximum,
            double span,
            int orbitIndex,
            double unit)
        {
            var signed = (unit * 2.0) - 1.0;
            var amplitude = Math.Min(
                span / 2.0,
                0.01 + (Math.Log(orbitIndex + 2.0) * span * 0.035));
            var value = center + (signed * amplitude);

            while (value < minimum)
            {
                value = minimum + (minimum - value);
            }

            while (value > maximum)
            {
                value = maximum - (value - maximum);
            }

            return value;
        }

        private static double BuildHaltonUnit(int index, int salt)
        {
            int[] bases = [2, 3, 5, 7, 11, 13, 17, 19];
            var baseValue = bases[Math.Abs(salt) % bases.Length];
            var rotationBase = bases[Math.Abs(salt + 5) % bases.Length];
            var unit = RadicalInverse(Math.Max(index, 1), baseValue);
            var rotation = RadicalInverse((Math.Abs(salt) + 1) * 997, rotationBase);
            return FractionalPart(unit + rotation);
        }

        private static double RadicalInverse(int index, int baseValue)
        {
            var result = 0.0;
            var fraction = 1.0 / baseValue;
            var value = index;

            while (value > 0)
            {
                result += (value % baseValue) * fraction;
                value /= baseValue;
                fraction /= baseValue;
            }

            return result;
        }

        private static double FractionalPart(double value)
        {
            return value - Math.Floor(value);
        }

        private static OptimizationOptions CreateOptions(
            int couponCount,
            double thirdChoiceRatio,
            double uniformBlend,
            double patternScoreWeight = 0.35)
        {
            var safeCouponCount = Math.Clamp(couponCount, 1, MaxAuditCouponCount);
            return new OptimizationOptions
            {
                DesiredCouponCount = safeCouponCount,
                InitialTopCandidateLimit = 3200000,
                DiversePrePoolLimit = 750000,
                ApiBudgetMultiplier = 1000,
                ApiConcurrency = 6,
                MinHammingDistance = 3,
                MinHammingDistanceFinal = 2,
                MonteCarloScenarioCount = 50000,
                ThirdChoiceMinRatio = thirdChoiceRatio,
                ProbabilityUniformBlend = uniformBlend,
                PatternScoreWeight = Math.Clamp(patternScoreWeight, 0.0, 2.0),
                WinnerPatternWeight = 0.45,
                RecentPatternWeight = 0.20,
                PreviousWeekPatternWeight = 0.12,
                SurpriseBalanceWeight = 0.30,
                MinI15WinnerCount = 1,
                MaxI15WinnerCount = 20
            };
        }

        private static IReadOnlyList<double> BuildRange(double minimum, double maximum, double step)
        {
            var result = new List<double>();
            for (var value = minimum; value <= maximum + 1e-9; value += step)
            {
                result.Add(Math.Round(Math.Min(value, maximum), 2));
            }

            return result
                .Distinct()
                .ToList();
        }

        private static CoverageHitSummary BuildCoverageHitSummary(
            IReadOnlyList<string> coverage,
            string actualResultLine)
        {
            if (coverage.Count == 0)
            {
                return new CoverageHitSummary(0, 0.0, 0, 0, 0, 0, 0, 0m, 0m, 0m, 0.0);
            }

            var best = 0;
            var total = 0;
            var hit15 = 0;
            var hit14 = 0;
            var hit13 = 0;
            var hit12 = 0;

            foreach (var prediction in coverage)
            {
                var hits = CountHits(prediction, actualResultLine);
                best = Math.Max(best, hits);
                total += hits;
                switch (hits)
                {
                    case 15: hit15++; break;
                    case 14: hit14++; break;
                    case 13: hit13++; break;
                    case 12: hit12++; break;
                }
            }

            return new CoverageHitSummary(
                best,
                total / (double)coverage.Count,
                hit15,
                hit14,
                hit13,
                hit12,
                coverage.Count,
                0m,
                0m,
                0m,
                0.0);
        }

        private static CoverageHitSummary ApplyFinancials(
            CoverageHitSummary hitSummary,
            RoundPayoutProfile? payoutProfile)
        {
            var cost = hitSummary.GeneratedCount * CouponUnitCost;
            var gross = payoutProfile == null
                ? 0m
                : (hitSummary.Hit15Count * payoutProfile.Prize15) +
                  (hitSummary.Hit14Count * payoutProfile.Prize14) +
                  (hitSummary.Hit13Count * payoutProfile.Prize13) +
                  (hitSummary.Hit12Count * payoutProfile.Prize12);
            var net = gross - cost;
            var roi = cost <= 0m
                ? 0.0
                : (double)(net / cost);

            return hitSummary with
            {
                CostAmount = cost,
                GrossPrizeAmount = gross,
                NetProfitAmount = net,
                Roi = roi
            };
        }

        private static LearnedPredictionStrategyCandidate BuildCandidate(
            Guid searchBatchId,
            CounterfactualBacktestTarget target,
            OptimizationOptions option,
            CoverageHitSummary hitSummary,
            string mode)
        {
            return new LearnedPredictionStrategyCandidate(
                searchBatchId,
                target.RoundId,
                target.SourceRunId,
                target.ActualResultLine,
                option.DesiredCouponCount,
                option,
                hitSummary.BestHitCount,
                hitSummary.AverageHitCount,
                hitSummary.Hit15Count,
                hitSummary.Hit14Count,
                hitSummary.Hit13Count,
                hitSummary.Hit12Count,
                hitSummary.CostAmount,
                hitSummary.GrossPrizeAmount,
                hitSummary.NetProfitAmount,
                hitSummary.Roi,
                hitSummary.Hit15Count > 0,
                $"Counterfactual continuous coverage search | Mode={mode} | Generated={hitSummary.GeneratedCount:n0} | Net={hitSummary.NetProfitAmount:n2} | ROI={hitSummary.Roi:P2}");
        }

        private static LearnedPredictionStrategyCandidate? BetterOf(
            LearnedPredictionStrategyCandidate? left,
            LearnedPredictionStrategyCandidate right)
        {
            if (left == null)
            {
                return right;
            }

            if (right.FoundExact != left.FoundExact)
            {
                return right.FoundExact ? right : left;
            }

            if (right.BestHitCount != left.BestHitCount)
            {
                return right.BestHitCount > left.BestHitCount ? right : left;
            }

            if (right.Hit14Count != left.Hit14Count)
            {
                return right.Hit14Count > left.Hit14Count ? right : left;
            }

            if (right.NetProfitAmount != left.NetProfitAmount)
            {
                return right.NetProfitAmount > left.NetProfitAmount ? right : left;
            }

            if (Math.Abs(right.Roi - left.Roi) > 0.000001)
            {
                return right.Roi > left.Roi ? right : left;
            }

            if (right.Hit13Count != left.Hit13Count)
            {
                return right.Hit13Count > left.Hit13Count ? right : left;
            }

            if (right.AverageHitCount != left.AverageHitCount)
            {
                return right.AverageHitCount > left.AverageHitCount ? right : left;
            }

            return right.CouponCount < left.CouponCount ? right : left;
        }

        private static bool ShouldReportVisualizationPoint(
            LearnedPredictionStrategyCandidate candidate,
            int roundTestedCount)
        {
            return roundTestedCount <= 200 ||
                   roundTestedCount % 25 == 0 ||
                   candidate.FoundExact ||
                   candidate.BestHitCount >= 14 ||
                   candidate.NetProfitAmount > 0m;
        }

        private static string BuildSummary(LearnedPredictionStrategyCandidate candidate)
        {
            var o = candidate.Options;
            return
                $"Round:{candidate.SourceRoundId} | Kolon:{candidate.CouponCount:n0} | " +
                $"EnIyi:{candidate.BestHitCount}/15 | 15Kolon:{candidate.Hit15Count:n0} | 14:{candidate.Hit14Count:n0} | " +
                $"12:{candidate.Hit12Count:n0} | Net:{candidate.NetProfitAmount:n2} TL | ROI:{candidate.Roi:P2} | " +
                $"Ort:{candidate.AverageHitCount:F2} | Ucuncu:{o.ThirdChoiceMinRatio:F4} | Yum:{o.ProbabilityUniformBlend:F4} | " +
                $"Oruntu:{o.PatternScoreWeight:F2} | Dist:{o.MinHammingDistance}/{o.MinHammingDistanceFinal}";
        }

        private static string BuildExistingExactSummary(
            int roundId,
            CounterfactualSearchSeed seed)
        {
            return
                $"Round:{roundId} | DB'de mevcut exact | Kolon:{seed.CouponCount:n0} | " +
                $"SeedRound:{seed.SourceRoundId} | EnIyi:{seed.BestHitCount}/15 | 14:{seed.Hit14Count:n0} | Net:{seed.NetProfitAmount:n2} TL | ROI:{seed.Roi:P2} | " +
                $"Ucuncu:{seed.ThirdChoiceMinRatio:F4} | Yum:{seed.ProbabilityUniformBlend:F4} | Oruntu:{seed.PatternScoreWeight:F4}";
        }

        private static string GetCoverageSignature(OptimizationOptions option)
        {
            return GetCoverageSignature(
                option.DesiredCouponCount,
                option.ThirdChoiceMinRatio,
                option.ProbabilityUniformBlend,
                option.PatternScoreWeight);
        }

        private static string GetCoverageSignature(
            int couponCount,
            double thirdChoiceMinRatio,
            double probabilityUniformBlend,
            double patternScoreWeight)
        {
            return
                $"K:{couponCount.ToString(CultureInfo.InvariantCulture)}|" +
                $"T:{thirdChoiceMinRatio.ToString("F4", CultureInfo.InvariantCulture)}|" +
                $"U:{probabilityUniformBlend.ToString("F4", CultureInfo.InvariantCulture)}|" +
                $"P:{patternScoreWeight.ToString("F4", CultureInfo.InvariantCulture)}";
        }

        private static string FormatOption(OptimizationOptions option)
        {
            return
                $"Kolon:{option.DesiredCouponCount:n0} | Ucuncu:{option.ThirdChoiceMinRatio:F4} | " +
                $"Yum:{option.ProbabilityUniformBlend:F4} | Oruntu:{option.PatternScoreWeight:F2}";
        }

        private static void UpdateLoopProgress(ITestView view, int totalWork)
        {
            view.ProgressBarMaxValue = ProgressLoopSize;
            view.ProgressBarValue = Math.Clamp(totalWork % ProgressLoopSize, 0, ProgressLoopSize);
        }

        private static int CountHits(string prediction, string actual)
        {
            if (prediction.Length != actual.Length)
            {
                return 0;
            }

            var hits = 0;
            for (var i = 0; i < prediction.Length; i++)
            {
                if (char.ToUpperInvariant(prediction[i]) == char.ToUpperInvariant(actual[i]))
                {
                    hits++;
                }
            }

            return hits;
        }

        private sealed record CoverageHitSummary(
            int BestHitCount,
            double AverageHitCount,
            int Hit15Count,
            int Hit14Count,
            int Hit13Count,
            int Hit12Count,
            int GeneratedCount,
            decimal CostAmount,
            decimal GrossPrizeAmount,
            decimal NetProfitAmount,
            double Roi);
    }

    public sealed record CounterfactualParameterSearchResult(
        int RoundCount,
        int InitialFullGridCount,
        int TestedCount,
        int CoverageProbeCount,
        int ForcedCandidateCount,
        int StoredStrategyCount,
        int ExactCount,
        IReadOnlyList<string> ExactSummaries);
}
