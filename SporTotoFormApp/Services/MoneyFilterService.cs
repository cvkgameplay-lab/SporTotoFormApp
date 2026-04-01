using SporTotoFormApp.Client;
using SporTotoFormApp.Interfaces;
using SporTotoFormApp.Object;
using System.Collections.Concurrent;
using System.Globalization;

namespace SporTotoFormApp.Services
{
    public sealed class MoneyFilterService
    {
        private readonly ITestView _view;
        private readonly OptimizationOptions _options;

        public MoneyFilterService(ITestView view, int kolonSayisi, OptimizationOptions? uiOverrides = null)
        {
            _view = view;
            _options = OptimizationOptions.Create(kolonSayisi, uiOverrides);
        }

        public async Task Run()
        {
            _view.Log("Pipeline baslatildi.", Color.Cyan);
            _view.Log(
                $"Ayarlar | i15: {_options.MinI15WinnerCount}-{_options.MaxI15WinnerCount} | TopK: {_options.InitialTopCandidateLimit} | CesitHavuz: {_options.DiversePrePoolLimit} | ApiCarpan: {_options.ApiBudgetMultiplier} | ApiEszamanlilik: {_options.ApiConcurrency} | MinDist: {_options.MinHammingDistance}/{_options.MinHammingDistanceFinal} | MC: {_options.MonteCarloScenarioCount}",
                Color.LightSteelBlue);

            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            await TryRefreshHistoricalDataAsync(baseDirectory);

            var model = HistoricalOutcomeModel.Create(baseDirectory);
            var evaluator = new CouponEvaluationService(model);
            var generator = new PredictionListHelper(PredictionGenerationRules.Default);

            _view.Log("Aday kuponlar uretilip on skorlanıyor...", Color.Yellow);
            var topCandidates = SelectTopCandidates(generator.FiltreliUret(), evaluator.PreScore, _options.InitialTopCandidateLimit);
            _view.Log($"On skor sonrasi aday: {topCandidates.Count}", Color.Yellow);

            var diversePrePool = EnforceDiversity(
                topCandidates.Select(x => x.Prediction),
                _options.MinHammingDistance,
                _options.DiversePrePoolLimit);

            _view.Log($"Cesitlilik sonrasi aday: {diversePrePool.Count}", Color.Yellow);

            var apiBudget = Math.Min(diversePrePool.Count, _options.GetApiBudget());
            var apiCandidates = diversePrePool
                .Take(apiBudget)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            _view.Log($"API degerlendirme butcesi: {apiCandidates.Count}", Color.Yellow);
            _view.ProgressBarMaxValue = _options.DesiredCouponCount;
            _view.ProgressBarValue = 0;

            var client = new SporTotoClient();
            var apiFilteredCoupons = await EvaluateCandidatesWithApiAsync(apiCandidates, client, evaluator);
            _view.Log($"API filtresini gecen kupon: {apiFilteredCoupons.Count}", Color.Yellow);

            var selected = SelectFinalCoupons(
                apiFilteredCoupons,
                model,
                _options.DesiredCouponCount,
                _options.MinHammingDistanceFinal);

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

            _view.ProgressBarValue = Math.Min(selected.Count, _options.DesiredCouponCount);
            ExcelExporter.ExportCouponsToExcel(selected, "Kuponlar.xlsx");
            WriteCouponsToText(selected);
            PrintMatchSummary(selected);

            _view.Log("Pipeline tamamlandi.", Color.LimeGreen);
        }

        private async Task TryRefreshHistoricalDataAsync(string baseDirectory)
        {
            try
            {
                _view.Log("Gecmis sonuclar resmi API'den cekiliyor...", Color.DeepSkyBlue);
                var updater = new HistoricalResultsUpdateService();
                using var refreshTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                var refreshResult = await updater.RefreshAsync(baseDirectory, refreshTimeoutCts.Token);

                if (refreshResult.Success)
                {
                    _view.Log($"Gecmis veri guncellendi: {refreshResult.LineCount} hafta", Color.DeepSkyBlue);
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

        private List<ScoredCandidate> SelectTopCandidates(IEnumerable<string> candidates, Func<string, double> preScorer, int limit)
        {
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

            return queue.UnorderedItems
                .Select(x => x.Element)
                .OrderByDescending(x => x.Score)
                .ToList();
        }

        private async Task<List<Coupon>> EvaluateCandidatesWithApiAsync(
            List<string> candidates,
            SporTotoClient client,
            CouponEvaluationService evaluator)
        {
            var semaphore = new SemaphoreSlim(_options.ApiConcurrency);
            var bag = new ConcurrentBag<Coupon>();
            var acceptedCounter = 0;
            var processedCounter = 0;

            var tasks = candidates.Select(async prediction =>
            {
                await semaphore.WaitAsync();

                try
                {
                    var result = await client.SubmitPredictionStringAsync(prediction.ToLowerInvariant());
                    if (result.Count == 0)
                    {
                        return;
                    }

                    var i15 = GetKisiSayisi(result, "15");
                    var i14 = GetKisiSayisi(result, "14");
                    var i13 = GetKisiSayisi(result, "13");
                    var i12 = GetKisiSayisi(result, "12");

                    if (i15 < _options.MinI15WinnerCount || i15 > _options.MaxI15WinnerCount)
                    {
                        return;
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

                    bag.Add(coupon);

                    var accepted = Interlocked.Increment(ref acceptedCounter);
                    if (accepted <= _options.DesiredCouponCount)
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
                    _view.Log($"API hatasi ({prediction}): {ex.Message}", Color.Crimson);
                }
                finally
                {
                    var done = Interlocked.Increment(ref processedCounter);
                    if (done % 100 == 0)
                    {
                        _view.Log($"API islenen aday: {done}/{candidates.Count}", Color.DimGray);
                    }

                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
            return bag.ToList();
        }

        private List<Coupon> SelectFinalCoupons(
            List<Coupon> candidates,
            HistoricalOutcomeModel model,
            int desiredCount,
            int minDistance)
        {
            var ordered = candidates
                .OrderByDescending(x => x.Utility)
                .ThenBy(x => ParseDouble(x.bonus.i15))
                .Take(Math.Max(desiredCount * 8, 120))
                .ToList();

            if (ordered.Count == 0)
            {
                return new List<Coupon>();
            }

            _view.Log($"Monte Carlo portfoy optimizasyonu basladi ({_options.MonteCarloScenarioCount} senaryo)", Color.DeepSkyBlue);
            var optimizer = new MonteCarloPortfolioOptimizer(model, _options.MonteCarloScenarioCount);
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
    }
}
