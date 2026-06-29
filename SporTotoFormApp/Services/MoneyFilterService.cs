using SporTotoFormApp.Client;
using SporTotoFormApp.Data;
using SporTotoFormApp.Interfaces;
using SporTotoFormApp.Object;
using System.Diagnostics;
using System.Globalization;

namespace SporTotoFormApp.Services
{
    public sealed class MoneyFilterService
    {
        private readonly ITestView _view;
        private readonly OptimizationOptions _options;
        private readonly IReadOnlyList<SymbolProbabilities>? _currentRoundProbabilities;

        public MoneyFilterService(
            ITestView view,
            int kolonSayisi,
            OptimizationOptions? uiOverrides = null,
            IReadOnlyList<SymbolProbabilities>? currentRoundProbabilities = null)
        {
            _view = view;
            _options = OptimizationOptions.Create(kolonSayisi, uiOverrides);
            _currentRoundProbabilities = currentRoundProbabilities;
        }

        public async Task<List<Coupon>> Run(
            bool persistOutputs = true,
            bool refreshHistoricalData = true,
            bool manageProgress = true)
        {
            _view.Log("Pipeline baslatildi.", Color.Cyan);
            _view.Log(
                $"Dogruluk ayarlari | Kolon: {_options.DesiredCouponCount} | UcuncuEsik: {_options.ThirdChoiceMinRatio:F2} | Yumusatma: {_options.ProbabilityUniformBlend:F2} | TopK: {_options.InitialTopCandidateLimit} | CesitHavuz: {_options.DiversePrePoolLimit} | MinDist: {_options.MinHammingDistance}/{_options.MinHammingDistanceFinal} | MC: {_options.MonteCarloScenarioCount}",
                Color.LightSteelBlue);

            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            await TrySeedHistoricalDataAsync(baseDirectory);

            if (refreshHistoricalData)
            {
                await TryRefreshHistoricalDataAsync(baseDirectory);
            }

            var model = HistoricalOutcomeModel.Create(baseDirectory, _currentRoundProbabilities);
            _view.Log(
                $"Model kaynagi: {model.Source} | Gecmis sonuc satiri: {model.SampleSize} | Mac bazli DB/Nesine harmani: {(model.UsedCurrentRoundBlend ? "var" : "yok")}",
                model.SampleSize >= 20 ? Color.LightSteelBlue : Color.Orange);
            var weekPatternModel = WeekPatternModel.Create(baseDirectory, model);
            _view.Log(weekPatternModel.Message, Color.LightSteelBlue);
            var evaluator = new CouponEvaluationService(
                model,
                weekPatternModel,
                _options);
            var pipelineWatch = Stopwatch.StartNew();
            List<string> evaluationCandidates;
            if (_options.DesiredCouponCount <= 2500 && model.UsedCurrentRoundBlend)
            {
                var systematicCandidateCount = Math.Clamp(
                    _options.DesiredCouponCount * 24,
                    5000,
                    32768);
                evaluationCandidates = CoverageScenarioGenerator.Generate(
                        model,
                        systematicCandidateCount,
                        _options.ThirdChoiceMinRatio,
                        _options.ProbabilityUniformBlend)
                    .ToList();
                _view.Log(
                    $"Sistematik senaryo modu: {evaluationCandidates.Count:n0} aday | Hedef: {_options.DesiredCouponCount:n0} kolon",
                    Color.Yellow);
            }
            else
            {
                var generator = new PredictionListHelper(PredictionGenerationRules.Default);
                _view.Log("Aday kuponlar uretilip on skorlaniyor...", Color.Yellow);
                var topCandidateSelection = await Task.Run(() =>
                    SelectTopCandidates(generator.FiltreliUret(), evaluator.PreScore, _options.InitialTopCandidateLimit));
                var topCandidates = topCandidateSelection.Candidates;
                _view.Log(
                    $"Aday tarama tamamlandi: {topCandidateSelection.ScannedCount:n0} kombinasyon | TopK: {topCandidates.Count:n0} | Sure: {topCandidateSelection.Elapsed.TotalSeconds:F1} sn",
                    Color.Yellow);

                var diversityLimit = Math.Min(
                    _options.DiversePrePoolLimit,
                    Math.Max(5000, _options.DesiredCouponCount * 8));
                var diversityWatch = Stopwatch.StartNew();
                var diversePrePool = await Task.Run(() => EnforceDiversity(
                    topCandidates.Select(x => x.Prediction),
                    _options.MinHammingDistance,
                    diversityLimit));
                diversityWatch.Stop();

                _view.Log(
                    $"Cesitlilik sonrasi aday: {diversePrePool.Count:n0} | Sure: {diversityWatch.Elapsed.TotalSeconds:F1} sn",
                    Color.Yellow);

                var evaluationLimit = Math.Min(
                    topCandidates.Count,
                    Math.Clamp(_options.DesiredCouponCount * 300, 20000, 60000));
                evaluationCandidates = BuildEvaluationCandidateList(
                    diversePrePool,
                    topCandidates.Select(x => x.Prediction),
                    model,
                    _options.DesiredCouponCount,
                    _options.ThirdChoiceMinRatio,
                    _options.ProbabilityUniformBlend,
                    evaluationLimit);

                _view.Log(
                    $"Dogruluk aday havuzu: {evaluationCandidates.Count:n0}/{evaluationLimit:n0} | Sistematik senaryo + cesitli + yuksek olasilikli adaylar",
                    evaluationCandidates.Count >= evaluationLimit ? Color.Yellow : Color.Orange);
            }
            if (manageProgress)
            {
                _view.ProgressBarMaxValue = _options.DesiredCouponCount;
                _view.ProgressBarValue = 0;
            }

            var evaluationWatch = Stopwatch.StartNew();
            var evaluatedCoupons = await Task.Run(() =>
                EvaluateCandidatesLocally(evaluationCandidates, evaluator));
            evaluationWatch.Stop();
            _view.Log(
                $"Yerel dogruluk degerlendirmesi: {evaluatedCoupons.Count:n0} aday | Sure: {evaluationWatch.Elapsed.TotalSeconds:F1} sn",
                Color.Yellow);

            var monteCarloWatch = Stopwatch.StartNew();
            var selected = await Task.Run(() => SelectFinalCoupons(
                evaluatedCoupons,
                model,
                _options.DesiredCouponCount,
                _options.MinHammingDistanceFinal));
            monteCarloWatch.Stop();
            _view.Log($"Monte Carlo/final secim suresi: {monteCarloWatch.Elapsed.TotalSeconds:F1} sn", Color.DeepSkyBlue);

            var deduplicated = DeduplicateCoupons(selected);
            if (deduplicated.Count != selected.Count)
            {
                _view.Log($"Duplicate kupon temizlendi: {selected.Count - deduplicated.Count}", Color.Orange);
            }
            selected = deduplicated;

            if (selected.Count < _options.DesiredCouponCount)
            {
                _view.Log($"Uyari: Hedef {_options.DesiredCouponCount} kolon, elde edilen {selected.Count}.", Color.Orange);
            }

            if (manageProgress)
            {
                _view.ProgressBarValue = Math.Min(selected.Count, _options.DesiredCouponCount);
            }
            if (persistOutputs)
            {
                ExcelExporter.ExportCouponsToExcel(selected, "Kuponlar.xlsx");
                WriteCouponsToText(selected);
                PrintMatchSummary(selected);
            }

            _view.Log("Pipeline tamamlandi.", Color.LimeGreen);
            pipelineWatch.Stop();
            _view.Log($"Toplam profil suresi: {pipelineWatch.Elapsed.TotalSeconds:F1} sn", Color.LightSteelBlue);
            return selected;
        }

        private static List<Coupon> EvaluateCandidatesLocally(
            IEnumerable<string> candidates,
            CouponEvaluationService evaluator)
        {
            var emptyBonus = new Bonus();
            return candidates
                .Select(prediction =>
                {
                    var analysis = evaluator.Analyze(prediction, emptyBonus);
                    return new Coupon
                    {
                        prediction = prediction,
                        bonus = new Bonus(),
                        Utility = analysis.Utility,
                        P15Probability = analysis.P15,
                        P14Probability = analysis.P14,
                        P13Probability = analysis.P13
                    };
                })
                .OrderByDescending(x => x.Utility)
                .ToList();
        }

        private async Task TryRefreshHistoricalDataAsync(string baseDirectory)
        {
            try
            {
                _view.Log("Gecmis sonuclar resmi API'den cekiliyor...", Color.DeepSkyBlue);
                var updater = new HistoricalResultsUpdateService();
                using var refreshTimeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var refreshResult = await updater.RefreshAsync(baseDirectory, refreshTimeoutCts.Token);

                if (refreshResult.Success)
                {
                    var payoutInfo = refreshResult.PayoutCount > 0
                        ? $" | Ikramiye satiri: {refreshResult.PayoutCount}"
                        : string.Empty;
                    var matchInfo = refreshResult.MatchCount > 0
                        ? $" | Mac satiri: {refreshResult.MatchCount}"
                        : string.Empty;
                    _view.Log($"Gecmis veri guncellendi: {refreshResult.LineCount} hafta{payoutInfo}{matchInfo}", Color.DeepSkyBlue);
                }
                else
                {
                    _view.Log("Gecmis veri guncellenemedi, yerel dosya ile devam.", Color.Orange);
                }
            }
            catch (OperationCanceledException)
            {
                _view.Log("Gecmis veri cekimi zaman asimina ugradi, yerel dosya ile devam.", Color.Orange);
            }
            catch (Exception ex)
            {
                _view.Log($"Gecmis veri guncelleme hatasi: {ex.Message}", Color.OrangeRed);
            }
        }

        private async Task TrySeedHistoricalDataAsync(string baseDirectory)
        {
            try
            {
                var seededCount = await new HistoricalResultRepository().SeedFromFileIfEmptyAsync(baseDirectory);
                if (seededCount > 0)
                {
                    _view.Log($"Yerel gecmis veri DB'ye aktarildi: {seededCount} hafta", Color.DeepSkyBlue);
                }
            }
            catch (Exception ex)
            {
                _view.Log($"Yerel gecmis veri DB aktarim hatasi: {ex.Message}", Color.OrangeRed);
            }
        }

        private TopCandidateSelection SelectTopCandidates(IEnumerable<string> candidates, Func<string, double> preScorer, int limit)
        {
            var watch = Stopwatch.StartNew();
            var queue = new PriorityQueue<ScoredCandidate, double>();
            var total = 0;

            foreach (var prediction in candidates)
            {
                total++;
                var score = preScorer(prediction);
                var item = new ScoredCandidate(prediction, score);

                if (queue.Count < limit)
                {
                    queue.Enqueue(item, score);
                }
                else if (queue.TryPeek(out _, out var minScore) && score > minScore)
                {
                    queue.Dequeue();
                    queue.Enqueue(item, score);
                }

                if (total % 400000 == 0)
                {
                    _view.Log($"Taranan aday: {total:n0}", Color.DimGray);
                }
            }

            watch.Stop();

            var selected = queue.UnorderedItems
                .Select(x => x.Element)
                .OrderByDescending(x => x.Score)
                .ToList();

            return new TopCandidateSelection(selected, total, watch.Elapsed);
        }

        private async Task<List<Coupon>> EvaluateCandidatesWithApiAsync(
            List<string> candidates,
            SporTotoClient client,
            CouponEvaluationService evaluator,
            bool manageProgress)
        {
            var semaphore = new SemaphoreSlim(_options.ApiConcurrency);
            var retainedLimit = Math.Clamp(_options.DesiredCouponCount * 250, 5000, 20000);
            var retained = new PriorityQueue<Coupon, double>();
            var retainedLock = new object();
            var acceptedCounter = 0;
            var processedCounter = 0;
            var errorCounter = 0;
            var emptyResponseCounter = 0;
            var i15OutsideTargetCounter = 0;
            var progressLogInterval = Math.Max(250, candidates.Count / 1000);

            var nextIndex = -1;
            var workers = Enumerable.Range(0, _options.ApiConcurrency).Select(async _ =>
            {
                while (true)
                {
                    var index = Interlocked.Increment(ref nextIndex);
                    if (index >= candidates.Count)
                    {
                        break;
                    }

                    var prediction = candidates[index];
                    await semaphore.WaitAsync();

                    try
                    {
                        var result = await client.SubmitPredictionStringAsync(prediction.ToLowerInvariant());
                        if (result.Count == 0)
                        {
                            Interlocked.Increment(ref emptyResponseCounter);
                            continue;
                        }

                        var i15 = GetKisiSayisi(result, "15");
                        var i14 = GetKisiSayisi(result, "14");
                        var i13 = GetKisiSayisi(result, "13");
                        var i12 = GetKisiSayisi(result, "12");

                        if (i15 < _options.MinI15WinnerCount || i15 > _options.MaxI15WinnerCount)
                        {
                            Interlocked.Increment(ref i15OutsideTargetCounter);
                        }

                        var bonus = new Bonus
                        {
                            i15 = i15.ToString(CultureInfo.InvariantCulture),
                            i14 = i14.ToString(CultureInfo.InvariantCulture),
                            i13 = i13.ToString(CultureInfo.InvariantCulture),
                            i12 = i12.ToString(CultureInfo.InvariantCulture)
                        };

                        var analysis = evaluator.Analyze(prediction, bonus);
                        var coupon = new Coupon
                        {
                            prediction = prediction,
                            bonus = bonus,
                            Utility = analysis.Utility,
                            P15Probability = analysis.P15,
                            P14Probability = analysis.P14,
                            P13Probability = analysis.P13
                        };

                        lock (retainedLock)
                        {
                            if (retained.Count < retainedLimit)
                            {
                                retained.Enqueue(coupon, coupon.Utility);
                            }
                            else if (retained.TryPeek(out var _, out var minimumUtility) &&
                                     coupon.Utility > minimumUtility)
                            {
                                retained.Dequeue();
                                retained.Enqueue(coupon, coupon.Utility);
                            }
                        }

                        var accepted = Interlocked.Increment(ref acceptedCounter);
                        if (manageProgress &&
                            accepted <= _options.DesiredCouponCount &&
                            (accepted == 1 || accepted == _options.DesiredCouponCount || accepted % 5 == 0))
                        {
                            _view.ProgressBarValue = accepted;
                        }

                        if (accepted % 25 == 0)
                        {
                            _view.Log($"API filtresini gecen kupon: {accepted}", Color.Green);
                        }
                    }
                    catch (Exception ex)
                    {
                        var errors = Interlocked.Increment(ref errorCounter);
                        if (errors <= 3 || errors % 50 == 0)
                        {
                            _view.Log($"API hatasi sayisi: {errors} | Son hata: {ex.Message}", Color.Crimson);
                        }
                    }
                    finally
                    {
                        var done = Interlocked.Increment(ref processedCounter);
                        if (done == 1 || done % progressLogInterval == 0 || done == candidates.Count)
                        {
                            _view.Log($"API islenen aday: {done}/{candidates.Count}", Color.DimGray);
                        }

                        semaphore.Release();
                    }
                }
            });

            await Task.WhenAll(workers);
            if (errorCounter > 0)
            {
                _view.Log($"Toplam API hatasi: {errorCounter}", Color.OrangeRed);
            }

            _view.Log(
                $"API ozet | Bos cevap: {emptyResponseCounter:n0} | i15 hedef disi: {i15OutsideTargetCounter:n0} | Degerlendirilen: {acceptedCounter:n0} | Tutulan: {retained.Count:n0}",
                Color.LightSteelBlue);

            return retained.UnorderedItems
                .Select(x => x.Element)
                .OrderByDescending(x => x.Utility)
                .ToList();
        }

        private List<Coupon> SelectFinalCoupons(
            List<Coupon> candidates,
            HistoricalOutcomeModel model,
            int desiredCount,
            int minDistance)
        {
            var monteCarloCandidateLimit = Math.Clamp(desiredCount * 50, 1500, 3000);
            var coverageTargets = CoverageScenarioGenerator.Generate(
                    model,
                    Math.Min(desiredCount, 2500),
                    _options.ThirdChoiceMinRatio,
                    _options.ProbabilityUniformBlend)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var coverageCandidates = candidates
                .Where(x => coverageTargets.Contains(x.prediction))
                .OrderByDescending(x => x.Utility);
            var rankedCandidates = candidates
                .OrderByDescending(x => x.Utility)
                .ThenBy(x => ParseDouble(x.bonus.i15));
            var ordered = coverageCandidates
                .Concat(rankedCandidates)
                .GroupBy(x => x.prediction, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .Take(Math.Max(monteCarloCandidateLimit, coverageTargets.Count))
                .ToList();

            if (ordered.Count == 0)
            {
                return new List<Coupon>();
            }

            _view.Log(
                $"Monte Carlo portfoy optimizasyonu basladi | Aday: {ordered.Count:n0} | Senaryo: {_options.MonteCarloScenarioCount:n0}",
                Color.DeepSkyBlue);
            var optimizer = new MonteCarloPortfolioOptimizer(
                model,
                _options.MonteCarloScenarioCount,
                Random.Shared.Next(),
                _options.ThirdChoiceMinRatio,
                _options.ProbabilityUniformBlend);
            var selected = optimizer.SelectPortfolio(ordered, desiredCount, minDistance);

            foreach (var candidate in selected)
            {
                _view.Log($"Secilen: {candidate.prediction} | U={candidate.Utility:F8}", Color.LimeGreen);
            }

            return selected;
        }

        private static List<string> EnforceDiversity(IEnumerable<string> candidates, int minDistance, int limit)
        {
            var selected = new List<string>(limit);

            foreach (var candidate in candidates)
            {
                if (selected.Any(existing => Distance(existing, candidate) < minDistance))
                {
                    continue;
                }

                selected.Add(candidate);
                if (selected.Count == limit)
                {
                    break;
                }
            }

            return selected;
        }

        private static List<string> BuildEvaluationCandidateList(
            IReadOnlyList<string> diversePrePool,
            IEnumerable<string> rankedCandidates,
            HistoricalOutcomeModel model,
            int systematicScenarioCount,
            double thirdChoiceMinRatio,
            double probabilityUniformBlend,
            int targetCount)
        {
            var result = new List<string>(targetCount);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in CoverageScenarioGenerator.Generate(
                         model,
                         Math.Min(32768, Math.Max(5000, systematicScenarioCount * 4)),
                         thirdChoiceMinRatio,
                         probabilityUniformBlend))
            {
                if (seen.Add(candidate))
                {
                    result.Add(candidate);
                }
            }

            var diverseTarget = Math.Min(
                targetCount,
                Math.Max(result.Count, (int)Math.Ceiling(targetCount * 0.60)));
            foreach (var candidate in diversePrePool)
            {
                if (!seen.Add(candidate))
                {
                    continue;
                }

                result.Add(candidate);
                if (result.Count >= diverseTarget)
                {
                    break;
                }
            }

            foreach (var candidate in rankedCandidates)
            {
                if (!seen.Add(candidate))
                {
                    continue;
                }

                result.Add(candidate);
                if (result.Count >= targetCount)
                {
                    break;
                }
            }

            return result;
        }

        private static int Distance(string left, string right)
        {
            var diff = 0;
            for (var i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    diff++;
                }
            }

            return diff;
        }

        private static int GetKisiSayisi(IEnumerable<BonusResult> results, string bilenContains)
        {
            var item = results.FirstOrDefault(x => x.Bilen.Contains(bilenContains, StringComparison.OrdinalIgnoreCase));
            if (item == null)
            {
                return 0;
            }

            return ParseInt(item.KisiSayisi);
        }

        private static int ParseInt(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return 0;
            }

            var cleaned = new string(raw.Where(char.IsDigit).ToArray());
            if (int.TryParse(cleaned, out var value))
            {
                return value;
            }

            return 0;
        }

        private static double ParseDouble(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return 0;
            }

            var cleaned = new string(raw.Where(c => char.IsDigit(c) || c is '.' or ',').ToArray()).Replace(',', '.');
            if (double.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            return 0;
        }

        private void WriteCouponsToText(List<Coupon> coupons)
        {
            try
            {
                var filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BestScoreCoupon.txt");
                using var writer = new StreamWriter(filePath, false);
                var seenPredictions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var coupon in coupons)
                {
                    var normalized = NormalizePrediction(coupon.prediction);
                    if (!seenPredictions.Add(normalized))
                    {
                        continue;
                    }

                    writer.WriteLine(normalized);
                }

                _view.Log($"Kupon dosyasi yazildi: {filePath}", Color.Yellow);
            }
            catch (Exception ex)
            {
                _view.Log($"Dosya yazim hatasi: {ex.Message}", Color.Crimson);
            }
        }

        private void PrintMatchSummary(List<Coupon> coupons)
        {
            _view.Log($"Kupon sayisi = {coupons.Count}", Color.Yellow);

            const int matchCount = 15;
            for (var i = 0; i < matchCount; i++)
            {
                var count1 = 0;
                var countX = 0;
                var count2 = 0;

                foreach (var coupon in coupons)
                {
                    switch (coupon.prediction[i])
                    {
                        case '1': count1++; break;
                        case 'X': countX++; break;
                        case '2': count2++; break;
                    }
                }

                _view.Log($"{i + 1}.Mac | 1:{count1} X:{countX} 2:{count2}", Color.Green);
            }
        }

        private static List<Coupon> DeduplicateCoupons(List<Coupon> coupons)
        {
            var result = new List<Coupon>(coupons.Count);
            var seenPredictions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var coupon in coupons)
            {
                var normalized = NormalizePrediction(coupon.prediction);
                if (!seenPredictions.Add(normalized))
                {
                    continue;
                }

                coupon.prediction = normalized;
                result.Add(coupon);
            }

            return result;
        }

        private static string NormalizePrediction(string prediction)
        {
            if (string.IsNullOrWhiteSpace(prediction))
            {
                return string.Empty;
            }

            return new string(prediction
                .Where(c => !char.IsWhiteSpace(c))
                .Select(char.ToUpperInvariant)
                .ToArray());
        }

        private sealed record ScoredCandidate(string Prediction, double Score);
        private sealed record TopCandidateSelection(List<ScoredCandidate> Candidates, int ScannedCount, TimeSpan Elapsed);
    }
}
