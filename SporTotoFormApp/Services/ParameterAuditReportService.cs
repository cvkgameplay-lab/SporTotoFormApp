using Microsoft.Data.SqlClient;
using SporTotoFormApp.Data;
using System.Globalization;
using System.Text;

namespace SporTotoFormApp.Services
{
    public sealed class ParameterAuditReportService
    {
        private readonly PredictionRepository _repository;

        public ParameterAuditReportService(PredictionRepository? repository = null)
        {
            _repository = repository ?? new PredictionRepository();
        }

        public async Task<ParameterAuditReportResult> BuildAsync(
            string outputDirectory,
            CancellationToken cancellationToken = default)
        {
            return await BuildAsync(outputDirectory, progress: null, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<ParameterAuditReportResult> BuildAsync(
            string outputDirectory,
            Action<string>? progress,
            CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(outputDirectory);
            var warnings = new List<string>();
            var allRuns = (await ExecuteWithTimeoutRetryAsync(
                    "Run listesi",
                    () => _repository.LoadParameterAuditRunsAsync(cancellationToken),
                    progress,
                    cancellationToken))
                .ToList();
            var runs = allRuns
                .Where(x => x.TotalRequested <= 100)
                .ToList();
            var ignoredHighCostRunCount = allRuns.Count - runs.Count;
            var learnedStrategies = (await ExecuteWithTimeoutRetryAsync(
                    "Ogrenilmis strateji listesi",
                    () => _repository.LoadRecommendedLearnedStrategiesAsync(20, cancellationToken),
                    progress,
                    cancellationToken))
                .ToList();
            var counterfactualRows = (await LoadCounterfactualRowsWithFallbackAsync(
                    warnings,
                    progress,
                    cancellationToken))
                .ToList();
            var stabilityRows = (await LoadStabilityRowsWithFallbackAsync(
                    counterfactualRows,
                    warnings,
                    progress,
                    cancellationToken))
                .ToList();
            var stabilityResult = await BuildStabilityReportWithFallbackAsync(
                    outputDirectory,
                    stabilityRows,
                    warnings,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            var report = BuildReport(
                runs,
                learnedStrategies,
                counterfactualRows,
                ignoredHighCostRunCount,
                stabilityResult.ReportSection);

            var path = Path.Combine(outputDirectory, "ParameterAuditReport.txt");
            await File.WriteAllTextAsync(path, report, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);

            var evaluatedRoundCount = runs
                .Select(x => x.RoundId)
                .Concat(counterfactualRows.Select(x => x.SourceRoundId))
                .Distinct()
                .Count();
            var perfectRunCount =
                runs.Count(x => x.Hit15Count > 0) +
                counterfactualRows.Count(x => x.FoundExact || x.Hit15Count > 0);
            var bestHit = runs
                .Select(x => x.BestHitCount)
                .Concat(counterfactualRows.Select(x => x.BestHitCount))
                .DefaultIfEmpty(0)
                .Max();

            return new ParameterAuditReportResult(
                path,
                runs.Count,
                evaluatedRoundCount,
                perfectRunCount,
                bestHit);
        }

        private async Task<IReadOnlyList<CounterfactualParameterAuditRow>> LoadCounterfactualRowsWithFallbackAsync(
            List<string> warnings,
            Action<string>? progress,
            CancellationToken cancellationToken)
        {
            var plans = new[]
            {
                new AuditQueryPlan("normal", 5000, 60),
                new AuditQueryPlan("hafif", 1500, 35),
                new AuditQueryPlan("minimum", 300, 20)
            };

            foreach (var plan in plans)
            {
                try
                {
                    progress?.Invoke($"Otopsi rapor verisi cekiliyor | Plan:{plan.Name} | Limit:{plan.Limit:n0} | Timeout:{plan.TimeoutSeconds}sn");
                    return await _repository.LoadCounterfactualParameterAuditRowsAsync(
                            plan.Limit,
                            cancellationToken,
                            plan.TimeoutSeconds)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (IsTimeoutLike(ex) && !cancellationToken.IsCancellationRequested)
                {
                    var warning = $"Otopsi rapor verisi timeout aldi | Plan:{plan.Name} | Limit:{plan.Limit:n0} | Timeout:{plan.TimeoutSeconds}sn | Daha hafif plan denenecek.";
                    warnings.Add(warning);
                    progress?.Invoke(warning);
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            const string fallbackWarning = "Otopsi rapor verisi tum planlarda timeout aldi; temel rapor mevcut run/ogrenilmis strateji verisiyle uretilecek.";
            warnings.Add(fallbackWarning);
            progress?.Invoke(fallbackWarning);
            return [];
        }

        private async Task<IReadOnlyList<CounterfactualStabilityRow>> LoadStabilityRowsWithFallbackAsync(
            IReadOnlyList<CounterfactualParameterAuditRow> counterfactualRows,
            List<string> warnings,
            Action<string>? progress,
            CancellationToken cancellationToken)
        {
            var plans = new[]
            {
                new StabilityQueryPlan("zengin", 250000, 250, 60),
                new StabilityQueryPlan("hafif", 120000, 500, 45),
                new StabilityQueryPlan("minimum", 50000, 1000, 30),
                new StabilityQueryPlan("acil", 15000, 2500, 20)
            };

            foreach (var plan in plans)
            {
                try
                {
                    progress?.Invoke($"Stabilite analizi verisi cekiliyor | Plan:{plan.Name} | Limit:{plan.Limit:n0} | Sample:1/{plan.SampleModulo:n0} | Timeout:{plan.TimeoutSeconds}sn");
                    var rows = await _repository.LoadCounterfactualStabilityRowsAsync(
                            plan.Limit,
                            plan.SampleModulo,
                            cancellationToken,
                            plan.TimeoutSeconds)
                        .ConfigureAwait(false);
                    progress?.Invoke($"Stabilite analizi verisi hazir | Plan:{plan.Name} | Satir:{rows.Count:n0}");
                    return rows;
                }
                catch (Exception ex) when (IsTimeoutLike(ex) && !cancellationToken.IsCancellationRequested)
                {
                    var warning = $"Stabilite veri sorgusu timeout aldi | Plan:{plan.Name} | Limit:{plan.Limit:n0} | Sample:1/{plan.SampleModulo:n0} | Timeout:{plan.TimeoutSeconds}sn | Daha hafif plan denenecek.";
                    warnings.Add(warning);
                    progress?.Invoke(warning);
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            if (counterfactualRows.Count > 0)
            {
                const string fallbackWarning = "Stabilite veri sorgusu tum planlarda timeout aldi; analiz yalnizca 15/14/karli otopsi ozet satirlariyla devam edecek.";
                warnings.Add(fallbackWarning);
                progress?.Invoke(fallbackWarning);
                return counterfactualRows
                    .Select(ToStabilityRow)
                    .ToList();
            }

            const string skipWarning = "Stabilite veri sorgusu tum planlarda timeout aldi ve fallback otopsi satiri yok; stabilite analizi atlandi.";
            warnings.Add(skipWarning);
            progress?.Invoke(skipWarning);
            return [];
        }

        private static async Task<IReadOnlyList<T>> ExecuteWithTimeoutRetryAsync<T>(
            string stepName,
            Func<Task<IReadOnlyList<T>>> operation,
            Action<string>? progress,
            CancellationToken cancellationToken)
        {
            const int maxAttempts = 3;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    return await operation().ConfigureAwait(false);
                }
                catch (Exception ex) when (IsTimeoutLike(ex) && !cancellationToken.IsCancellationRequested && attempt < maxAttempts)
                {
                    progress?.Invoke($"{stepName} timeout aldi | Deneme:{attempt}/{maxAttempts} | Tekrar deneniyor.");
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            return await operation().ConfigureAwait(false);
        }

        private static async Task<ParameterStabilityAnalysisResult> BuildStabilityReportWithFallbackAsync(
            string outputDirectory,
            IReadOnlyList<CounterfactualStabilityRow> stabilityRows,
            IReadOnlyList<string> warnings,
            Action<string>? progress,
            CancellationToken cancellationToken)
        {
            try
            {
                progress?.Invoke("Stabilite raporu ve grafikler arka planda uretiliyor...");
                var result = await Task.Run(
                        () => new ParameterStabilityAnalysisService().Build(outputDirectory, stabilityRows),
                        cancellationToken)
                    .ConfigureAwait(false);

                return warnings.Count == 0
                    ? result
                    : result with
                    {
                        ReportSection = BuildWarningSection(warnings) + result.ReportSection
                    };
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                var allWarnings = warnings
                    .Concat([$"Stabilite raporu/grafik uretimi tamamlanamadi: {ex.Message}. Temel parametre raporu yazilmaya devam etti."])
                    .ToList();
                progress?.Invoke(allWarnings[^1]);
                return new ParameterStabilityAnalysisResult(BuildWarningSection(allWarnings), []);
            }
        }

        private static CounterfactualStabilityRow ToStabilityRow(CounterfactualParameterAuditRow row)
        {
            return new CounterfactualStabilityRow(
                row.SourceName,
                row.SourceRoundId,
                row.SourceRunId,
                row.ActualResultLine,
                row.CouponCount,
                row.Options.ThirdChoiceMinRatio,
                row.Options.ProbabilityUniformBlend,
                row.Options.PatternScoreWeight,
                row.BestHitCount,
                row.AverageHitCount,
                row.Hit15Count,
                row.Hit14Count,
                row.Hit13Count,
                row.Hit12Count,
                row.CostAmount,
                row.GrossPrizeAmount,
                row.NetProfitAmount,
                row.Roi,
                row.FoundExact,
                row.CreatedAt);
        }

        private static string BuildWarningSection(IReadOnlyList<string> warnings)
        {
            if (warnings.Count == 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("RAPOR DAYANIKLILIK NOTLARI");
            foreach (var warning in warnings.Distinct())
            {
                sb.AppendLine($"- {warning}");
            }

            return sb.ToString();
        }

        private static bool IsTimeoutLike(Exception exception)
        {
            return exception is TimeoutException
                || exception is SqlException { Number: -2 }
                || exception.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("zaman asimi", StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("zaman aşımı", StringComparison.OrdinalIgnoreCase)
                || exception.InnerException is not null && IsTimeoutLike(exception.InnerException);
        }

        private sealed record AuditQueryPlan(
            string Name,
            int Limit,
            int TimeoutSeconds);

        private sealed record StabilityQueryPlan(
            string Name,
            int Limit,
            int SampleModulo,
            int TimeoutSeconds);

        private static string BuildReport(
            IReadOnlyList<PredictionParameterAuditRun> runs,
            IReadOnlyList<LearnedPredictionStrategyRecommendation> learnedStrategies,
            IReadOnlyList<CounterfactualParameterAuditRow> counterfactualRows,
            int ignoredHighCostRunCount,
            string stabilityReportSection)
        {
            var sb = new StringBuilder();
            sb.AppendLine("SPOR TOTO PARAMETRE OTOPSISI");
            sb.AppendLine($"Uretim zamani: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine("Not: Bu rapor DB'de daha once denenmis run/parametre setlerini ve PARAMETRE OTOPSISI'nin karsi-olgusal kayitlarini inceler.");
            sb.AppendLine("Hic denenmemis parametreler icin 15/15 garantisi veya karsi-olgusal kesinlik vermez.");
            sb.AppendLine("Maliyet filtresi: rapordaki ogrenilmis/otopsi parametreleri 100 kolon / 1.000 TL siniriyle listelenir.");
            sb.AppendLine();

            if (runs.Count == 0 && counterfactualRows.Count == 0)
            {
                sb.AppendLine("Degerlendirilmis run veya otopsi parametre kaydi bulunamadi. Once 'SONUC DEGERLENDIR' veya 'PARAMETRE OTOPSISI' calistirilmalidir.");
                return sb.ToString();
            }

            var roundCount = runs
                .Select(x => x.RoundId)
                .Concat(counterfactualRows.Select(x => x.SourceRoundId))
                .Distinct()
                .Count();
            var perfectRuns = runs
                .Where(x => x.Hit15Count > 0)
                .OrderByDescending(x => x.RoundId)
                .ThenByDescending(x => x.Hit15Count)
                .ThenByDescending(x => x.AverageHitCount)
                .ToList();
            var perfectCounterfactualRows = counterfactualRows
                .Where(x => x.FoundExact || x.Hit15Count > 0)
                .OrderByDescending(x => x.SourceRoundId)
                .ThenByDescending(x => x.Hit15Count)
                .ThenByDescending(x => x.NetProfitAmount)
                .ThenByDescending(x => x.Roi)
                .ToList();
            var bestHit = runs
                .Select(x => x.BestHitCount)
                .Concat(counterfactualRows.Select(x => x.BestHitCount))
                .DefaultIfEmpty(0)
                .Max();

            sb.AppendLine("GENEL OZET");
            sb.AppendLine($"- Degerlendirilen run: {runs.Count:n0}");
            sb.AppendLine($"- Degerlendirilen otopsi parametresi: {counterfactualRows.Count:n0}");
            sb.AppendLine($"- Maliyet siniri disinda birakilan eski run: {ignoredHighCostRunCount:n0}");
            sb.AppendLine($"- Degerlendirilen hafta/round: {roundCount:n0}");
            sb.AppendLine($"- 15/15 ureten run: {perfectRuns.Count:n0}");
            sb.AppendLine($"- 15/15 ureten otopsi parametresi: {perfectCounterfactualRows.Count:n0}");
            sb.AppendLine($"- Simdiye kadarki en iyi isabet: {bestHit}/15");
            sb.AppendLine();

            AppendPerfectSettings(sb, perfectRuns, perfectCounterfactualRows);
            AppendLearnedStrategies(sb, learnedStrategies);
            AppendRoundBreakdown(sb, runs);
            AppendCounterfactualBreakdown(sb, counterfactualRows);
            AppendParameterRanking(sb, runs);
            AppendCounterfactualParameterRanking(sb, counterfactualRows);
            sb.AppendLine(stabilityReportSection);

            return sb.ToString();
        }

        private static void AppendPerfectSettings(
            StringBuilder sb,
            IReadOnlyList<PredictionParameterAuditRun> perfectRuns,
            IReadOnlyList<CounterfactualParameterAuditRow> perfectCounterfactualRows)
        {
            sb.AppendLine("15/15 URETEN AYARLAR");
            if (perfectRuns.Count == 0 && perfectCounterfactualRows.Count == 0)
            {
                sb.AppendLine("- Henuz denenmis run veya otopsi parametreleri icinde 15/15 ureten ayar yok.");
                sb.AppendLine("- Asagidaki round/parametre siralamasi 14/13'e en yakin ayarlari gosterir.");
                sb.AppendLine();
                return;
            }

            foreach (var run in perfectRuns.Take(50))
            {
                sb.AppendLine(
                    $"- Round {run.RoundId} | Run {run.RunId} | 15/15 kolon: {run.Hit15Count:n0} | " +
                    $"14/15 kolon: {run.Hit14Count:n0} | Ort: {run.AverageHitCount:F2} | {run.ParameterSignature}");
            }

            foreach (var row in perfectCounterfactualRows.Take(50))
            {
                sb.AppendLine(
                    $"- [Otopsi:{row.SourceName}] Round {row.SourceRoundId} | Kaynak Run {row.SourceRunId} | " +
                    $"15/15 kolon: {row.Hit15Count:n0} | 14/15 kolon: {row.Hit14Count:n0} | " +
                    $"En iyi:{row.BestHitCount}/15 | Ort:{row.AverageHitCount:F2} | " +
                    $"Maliyet:{row.CostAmount:n2} TL | Brut:{row.GrossPrizeAmount:n2} TL | Net:{row.NetProfitAmount:n2} TL | ROI:{row.Roi:P2} | " +
                    $"{row.ParameterSignature}");
            }

            if (perfectRuns.Count > 50)
            {
                sb.AppendLine($"- ... {perfectRuns.Count - 50:n0} ek 15/15 run raporda kisaltildi.");
            }

            if (perfectCounterfactualRows.Count > 50)
            {
                sb.AppendLine($"- ... {perfectCounterfactualRows.Count - 50:n0} ek 15/15 otopsi parametresi raporda kisaltildi.");
            }

            sb.AppendLine();
        }

        private static void AppendLearnedStrategies(
            StringBuilder sb,
            IReadOnlyList<LearnedPredictionStrategyRecommendation> learnedStrategies)
        {
            sb.AppendLine("OGRENILMIS STRATEJI TABLOSU");
            if (learnedStrategies.Count == 0)
            {
                sb.AppendLine("- LearnedPredictionStrategies tablosunda henuz 15/15 exact kayit yok.");
                sb.AppendLine("- 'PARAMETRE OTOPSISI' butonu geriye donuk exact arama yaptiktan sonra burasi dolacak.");
                sb.AppendLine();
                return;
            }

            foreach (var strategy in learnedStrategies)
            {
                var o = strategy.Options;
                sb.AppendLine(
                    $"- {strategy.Summary} | Kolon:{strategy.CouponCount} | " +
                    $"Ucuncu:{o.ThirdChoiceMinRatio:F2} | Yum:{o.ProbabilityUniformBlend:F2} | " +
                    $"Oruntu:{o.PatternScoreWeight:F2}/Kaz:{o.WinnerPatternWeight:F2}/Son:{o.RecentPatternWeight:F2}/Once:{o.PreviousWeekPatternWeight:F2}/Surp:{o.SurpriseBalanceWeight:F2} | " +
                    $"Dist:{o.MinHammingDistance}/{o.MinHammingDistanceFinal} | MC:{o.MonteCarloScenarioCount:n0}");
            }

            sb.AppendLine();
        }

        private static void AppendRoundBreakdown(
            StringBuilder sb,
            IReadOnlyList<PredictionParameterAuditRun> runs)
        {
            sb.AppendLine("ROUND BAZLI EN IYI AYARLAR");

            foreach (var group in runs
                         .GroupBy(x => x.RoundId)
                         .OrderByDescending(x => x.Key))
            {
                var ordered = group
                    .OrderByDescending(x => x.Hit15Count > 0)
                    .ThenByDescending(x => x.BestHitCount)
                    .ThenByDescending(x => x.Hit14Count)
                    .ThenByDescending(x => x.Hit13Count)
                    .ThenByDescending(x => x.AverageHitCount)
                    .ThenByDescending(x => x.TotalRequested)
                    .ToList();
                var best = ordered.First();
                var exactCount = ordered.Count(x => x.Hit15Count > 0);

                sb.AppendLine(
                    $"Round {group.Key} | Gercek: {best.ActualResultLine} | Run: {group.Count():n0} | " +
                    $"15 ureten run: {exactCount:n0} | En iyi: {best.BestHitCount}/15");

                foreach (var run in ordered.Take(5))
                {
                    sb.AppendLine(
                        $"  * Run {run.RunId} | En iyi:{run.BestHitCount} | 15:{run.Hit15Count:n0} 14:{run.Hit14Count:n0} 13:{run.Hit13Count:n0} 12:{run.Hit12Count:n0} | " +
                        $"Ort:{run.AverageHitCount:F2} | {run.ParameterSignature}");
                }
            }

            sb.AppendLine();
        }

        private static void AppendCounterfactualBreakdown(
            StringBuilder sb,
            IReadOnlyList<CounterfactualParameterAuditRow> rows)
        {
            sb.AppendLine("OTOPSI ROUND BAZLI EN IYI PARAMETRELER");
            if (rows.Count == 0)
            {
                sb.AppendLine("- 100 kolon / 1.000 TL siniri icinde raporlanacak otopsi parametresi bulunamadi.");
                sb.AppendLine();
                return;
            }

            foreach (var group in rows
                         .GroupBy(x => x.SourceRoundId)
                         .OrderByDescending(x => x.Key))
            {
                var ordered = group
                    .OrderByDescending(x => x.FoundExact || x.Hit15Count > 0)
                    .ThenByDescending(x => x.Hit15Count)
                    .ThenByDescending(x => x.BestHitCount)
                    .ThenByDescending(x => x.Hit14Count)
                    .ThenByDescending(x => x.NetProfitAmount)
                    .ThenByDescending(x => x.Roi)
                    .ToList();
                var best = ordered.First();
                var exactCount = ordered.Count(x => x.FoundExact || x.Hit15Count > 0);

                sb.AppendLine(
                    $"Round {group.Key} | Gercek: {best.ActualResultLine} | Parametre:{group.Count():n0} | " +
                    $"15 ureten parametre:{exactCount:n0} | En iyi:{best.BestHitCount}/15 | En iyi net:{best.NetProfitAmount:n2} TL");

                foreach (var row in ordered.Take(5))
                {
                    sb.AppendLine(
                        $"  * {row.SourceName} | KaynakRun:{row.SourceRunId} | En iyi:{row.BestHitCount}/15 | " +
                        $"15:{row.Hit15Count:n0} 14:{row.Hit14Count:n0} 13:{row.Hit13Count:n0} 12:{row.Hit12Count:n0} | " +
                        $"Ort:{row.AverageHitCount:F2} | Maliyet:{row.CostAmount:n2} TL | Net:{row.NetProfitAmount:n2} TL | ROI:{row.Roi:P2} | " +
                        $"{row.ParameterSignature}");
                }
            }

            sb.AppendLine();
        }

        private static void AppendParameterRanking(
            StringBuilder sb,
            IReadOnlyList<PredictionParameterAuditRun> runs)
        {
            sb.AppendLine("PARAMETRE IMZASI BAZLI SIRALAMA");

            var groups = runs
                .GroupBy(x => x.ParameterSignature)
                .Select(x =>
                {
                    var rows = x.ToList();
                    return new ParameterGroupSummary(
                        x.Key,
                        rows.Count,
                        rows.Select(r => r.RoundId).Distinct().Count(),
                        rows.Where(r => r.Hit15Count > 0).Select(r => r.RoundId).Distinct().Count(),
                        rows.Sum(r => r.Hit15Count),
                        rows.Max(r => r.BestHitCount),
                        rows.Average(r => r.BestHitCount),
                        rows.Average(r => r.AverageHitCount),
                        rows.OrderByDescending(r => r.BestHitCount)
                            .ThenByDescending(r => r.Hit14Count)
                            .First().RunId);
                })
                .OrderByDescending(x => x.ExactRoundCount)
                .ThenByDescending(x => x.TotalHit15Coupons)
                .ThenByDescending(x => x.MaxBestHit)
                .ThenByDescending(x => x.AverageBestHit)
                .ThenByDescending(x => x.AverageCouponHit)
                .ToList();

            foreach (var group in groups.Take(40))
            {
                sb.AppendLine(
                    $"- ExactRound:{group.ExactRoundCount:n0}/{group.RoundCount:n0} | Run:{group.RunCount:n0} | " +
                    $"15 kolon:{group.TotalHit15Coupons:n0} | Max:{group.MaxBestHit}/15 | " +
                    $"AvgBest:{group.AverageBestHit.ToString("F2", CultureInfo.InvariantCulture)} | " +
                    $"AvgKolon:{group.AverageCouponHit.ToString("F2", CultureInfo.InvariantCulture)} | " +
                    $"OrnekRun:{group.SampleRunId} | {group.Signature}");
            }

            if (groups.Count > 40)
            {
                sb.AppendLine($"- ... {groups.Count - 40:n0} ek parametre imzasi kisaltildi.");
            }
        }

        private static void AppendCounterfactualParameterRanking(
            StringBuilder sb,
            IReadOnlyList<CounterfactualParameterAuditRow> rows)
        {
            sb.AppendLine();
            sb.AppendLine("OTOPSI PARAMETRE IMZASI BAZLI SIRALAMA");
            if (rows.Count == 0)
            {
                sb.AppendLine("- Raporlanacak otopsi parametre imzasi yok.");
                return;
            }

            var groups = rows
                .GroupBy(x => x.ParameterSignature)
                .Select(x =>
                {
                    var groupRows = x.ToList();
                    var sample = groupRows
                        .OrderByDescending(r => r.FoundExact || r.Hit15Count > 0)
                        .ThenByDescending(r => r.BestHitCount)
                        .ThenByDescending(r => r.NetProfitAmount)
                        .First();

                    return new CounterfactualParameterGroupSummary(
                        x.Key,
                        groupRows.Count,
                        groupRows.Select(r => r.SourceRoundId).Distinct().Count(),
                        groupRows.Where(r => r.FoundExact || r.Hit15Count > 0).Select(r => r.SourceRoundId).Distinct().Count(),
                        groupRows.Sum(r => r.Hit15Count),
                        groupRows.Sum(r => r.Hit14Count),
                        groupRows.Max(r => r.BestHitCount),
                        groupRows.Average(r => r.BestHitCount),
                        groupRows.Average(r => r.AverageHitCount),
                        groupRows.Sum(r => r.NetProfitAmount),
                        groupRows.Average(r => r.Roi),
                        sample.SourceRoundId,
                        sample.SourceRunId,
                        sample.SourceName);
                })
                .OrderByDescending(x => x.ExactRoundCount)
                .ThenByDescending(x => x.TotalHit15Coupons)
                .ThenByDescending(x => x.MaxBestHit)
                .ThenByDescending(x => x.TotalNetProfitAmount)
                .ThenByDescending(x => x.AverageRoi)
                .ThenByDescending(x => x.AverageBestHit)
                .ToList();

            foreach (var group in groups.Take(40))
            {
                sb.AppendLine(
                    $"- ExactRound:{group.ExactRoundCount:n0}/{group.RoundCount:n0} | Kayit:{group.RowCount:n0} | " +
                    $"15 kolon:{group.TotalHit15Coupons:n0} | 14 kolon:{group.TotalHit14Coupons:n0} | Max:{group.MaxBestHit}/15 | " +
                    $"AvgBest:{group.AverageBestHit.ToString("F2", CultureInfo.InvariantCulture)} | " +
                    $"AvgKolon:{group.AverageCouponHit.ToString("F2", CultureInfo.InvariantCulture)} | " +
                    $"Net:{group.TotalNetProfitAmount:n2} TL | ROI:{group.AverageRoi:P2} | " +
                    $"Ornek:{group.SampleSourceName}/Round:{group.SampleRoundId}/Run:{group.SampleRunId} | {group.Signature}");
            }

            if (groups.Count > 40)
            {
                sb.AppendLine($"- ... {groups.Count - 40:n0} ek otopsi parametre imzasi kisaltildi.");
            }
        }

        private sealed record ParameterGroupSummary(
            string Signature,
            int RunCount,
            int RoundCount,
            int ExactRoundCount,
            int TotalHit15Coupons,
            int MaxBestHit,
            double AverageBestHit,
            double AverageCouponHit,
            int SampleRunId);

        private sealed record CounterfactualParameterGroupSummary(
            string Signature,
            int RowCount,
            int RoundCount,
            int ExactRoundCount,
            int TotalHit15Coupons,
            int TotalHit14Coupons,
            int MaxBestHit,
            double AverageBestHit,
            double AverageCouponHit,
            decimal TotalNetProfitAmount,
            double AverageRoi,
            int SampleRoundId,
            int SampleRunId,
            string SampleSourceName);
    }

    public sealed record ParameterAuditReportResult(
        string FilePath,
        int EvaluatedRunCount,
        int EvaluatedRoundCount,
        int PerfectRunCount,
        int BestHitCount);
}
