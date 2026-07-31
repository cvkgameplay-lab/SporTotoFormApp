using SporTotoFormApp.Data;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;

namespace SporTotoFormApp.Services
{
    public sealed class ParameterStabilityAnalysisService
    {
        private const int MaxCouponCount = 100;
        private const double XBinWidth = 0.02;
        private const double YBinWidth = 0.02;
        private const double ZBinWidth = 0.05;
        private const double XMaximum = 1.01;
        private const double YMaximum = 0.35;
        private const double ZMaximum = 2.0;

        public ParameterStabilityAnalysisResult Build(
            string outputDirectory,
            IReadOnlyList<CounterfactualStabilityRow> sourceRows)
        {
            Directory.CreateDirectory(outputDirectory);
            var rows = sourceRows
                .Where(x => x.CouponCount <= MaxCouponCount)
                .ToList();
            var generatedFiles = new List<string>();
            var sb = new StringBuilder();

            sb.AppendLine();
            sb.AppendLine("ROBUST PARAMETRE STABILITE ANALIZI");
            sb.AppendLine("Basari tanimi: BestHitCount >= 14 VE ROI > 0. 15/15 tek basina degil, tekrar eden bolge olarak degerlendirilir.");
            sb.AppendLine($"Maliyet filtresi: Kolon <= {MaxCouponCount}.");
            sb.AppendLine("Veri cekimi: exact/14+/pozitif ROI satirlari oncelikli, kalan basarisiz denemeler heatmap ve negatif bolge analizi icin deterministik sample olarak alinir.");
            sb.AppendLine();

            if (rows.Count == 0)
            {
                sb.AppendLine("- Stabilite analizi icin 100 kolon altinda otopsi denemesi bulunamadi.");
                return new ParameterStabilityAnalysisResult(sb.ToString(), generatedFiles);
            }

            var roundCount = rows.Select(x => x.SourceRoundId).Distinct().Count();
            var robustRows = rows.Where(x => x.IsRobustSuccess).ToList();
            var exactRows = rows.Where(x => x.IsExact).ToList();
            var positiveRoiRows = rows.Where(x => x.IsPositiveRoi).ToList();

            sb.AppendLine("STABILITE GENEL OZET");
            sb.AppendLine($"- Analiz orneklem satiri: {rows.Count:n0}");
            sb.AppendLine($"- Round sayisi: {roundCount:n0}");
            sb.AppendLine($"- 15/15 satiri: {exactRows.Count:n0}");
            sb.AppendLine($"- 14+ ve ROI>0 robust basari satiri: {robustRows.Count:n0}");
            sb.AppendLine($"- Pozitif ROI satiri: {positiveRoiRows.Count:n0} ({Ratio(positiveRoiRows.Count, rows.Count):P1})");
            sb.AppendLine();

            AppendRoundBestSettings(sb, rows);

            var stableRegions = BuildStableRegions(rows);
            AppendStableRegions(sb, stableRegions);

            var leaveOneRoundOut = BuildLeaveOneRoundOut(rows);
            AppendLeaveOneRoundOut(sb, leaveOneRoundOut);

            var seedNeighborhoods = BuildSeedNeighborhoods(rows);
            AppendSeedNeighborhoods(sb, seedNeighborhoods);

            AppendSamplingComparison(sb);

            generatedFiles.Add(WriteStableRegionsCsv(outputDirectory, stableRegions));
            generatedFiles.Add(WriteLeaveOneRoundOutCsv(outputDirectory, leaveOneRoundOut));
            generatedFiles.Add(WriteSeedNeighborhoodCsv(outputDirectory, seedNeighborhoods));
            generatedFiles.Add(RenderHeatmap(outputDirectory, rows, HeatmapMetric.Roi));
            generatedFiles.Add(RenderHeatmap(outputDirectory, rows, HeatmapMetric.CorrectCount));
            generatedFiles.Add(RenderPatternFacetHeatmap(outputDirectory, rows));
            generatedFiles.Add(RenderScatter(outputDirectory, rows));

            sb.AppendLine("URETILEN STABILITE DOSYALARI");
            foreach (var file in generatedFiles)
            {
                sb.AppendLine($"- {file}");
            }

            return new ParameterStabilityAnalysisResult(sb.ToString(), generatedFiles);
        }

        private static void AppendRoundBestSettings(
            StringBuilder sb,
            IReadOnlyList<CounterfactualStabilityRow> rows)
        {
            sb.AppendLine("ROUND BAZLI 15 / 14 / POZITIF ROI / 100 KOLON OZETI");
            foreach (var group in rows
                         .GroupBy(x => x.SourceRoundId)
                         .OrderByDescending(x => x.Key))
            {
                var roundRows = group.ToList();
                var exactCount = roundRows.Count(x => x.IsExact);
                var hit14Count = roundRows.Count(x => x.BestHitCount >= 14);
                var positiveRoiCount = roundRows.Count(x => x.IsPositiveRoi);
                var best = roundRows
                    .OrderByDescending(x => x.IsExact)
                    .ThenByDescending(x => x.BestHitCount)
                    .ThenByDescending(x => x.NetProfitAmount)
                    .ThenByDescending(x => x.Roi)
                    .First();

                sb.AppendLine(
                    $"- Round {group.Key} | Satir:{roundRows.Count:n0} | 15:{exactCount:n0} | 14+:{hit14Count:n0} | ROI+:{positiveRoiCount:n0} | " +
                    $"En iyi:{best.BestHitCount}/15 | Kolon:{best.CouponCount} | Net:{best.NetProfitAmount:n2} TL | ROI:{best.Roi:P2} | " +
                    $"X:{best.ThirdChoiceMinRatio:F4} Y:{best.ProbabilityUniformBlend:F4} Z:{best.PatternScoreWeight:F4}");
            }

            sb.AppendLine();
        }

        private static IReadOnlyList<StableRegionSummary> BuildStableRegions(
            IReadOnlyList<CounterfactualStabilityRow> rows)
        {
            return rows
                .GroupBy(x => StabilityBinKey.From(x))
                .Select(x =>
                {
                    var groupRows = x.ToList();
                    var successRows = groupRows.Where(r => r.IsRobustSuccess).ToList();
                    var positiveRoiRows = groupRows.Count(r => r.IsPositiveRoi);
                    var exactRows = groupRows.Count(r => r.IsExact);
                    var successRoundCount = successRows.Select(r => r.SourceRoundId).Distinct().Count();
                    var score =
                        (successRoundCount * 1000.0) +
                        (Ratio(successRows.Count, groupRows.Count) * 250.0) +
                        (groupRows.Average(r => r.BestHitCount) * 20.0) +
                        Math.Clamp(groupRows.Average(r => r.Roi), -5.0, 50.0);

                    return new StableRegionSummary(
                        x.Key,
                        groupRows.Count,
                        groupRows.Select(r => r.SourceRoundId).Distinct().Count(),
                        successRows.Count,
                        successRoundCount,
                        exactRows,
                        groupRows.Average(r => r.BestHitCount),
                        groupRows.Average(r => r.Roi),
                        Ratio(positiveRoiRows, groupRows.Count),
                        Ratio(successRows.Count, groupRows.Count),
                        groupRows.Average(r => r.CouponCount),
                        score);
                })
                .Where(x => x.RowCount >= 3 || x.SuccessCount > 0 || x.ExactCount > 0)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.SuccessRoundCount)
                .ThenByDescending(x => x.SuccessRate)
                .ThenByDescending(x => x.AverageCorrectCount)
                .Take(80)
                .ToList();
        }

        private static void AppendStableRegions(
            StringBuilder sb,
            IReadOnlyList<StableRegionSummary> regions)
        {
            sb.AppendLine("STABIL PARAMETRE BOLGELERI");
            sb.AppendLine("Bin genislikleri: X +/-0.02, Y +/-0.02, Z +/-0.05 mantigina yakin okunmalidir.");
            if (regions.Count == 0)
            {
                sb.AppendLine("- Stabil bolge bulunamadi.");
                sb.AppendLine();
                return;
            }

            foreach (var region in regions.Take(20))
            {
                sb.AppendLine(
                    $"- {region.Key.RangeText} | Round:{region.RoundCount:n0} | BasariRound:{region.SuccessRoundCount:n0} | " +
                    $"Satir:{region.RowCount:n0} | Basari:{region.SuccessCount:n0} | Exact:{region.ExactCount:n0} | " +
                    $"AvgCorrect:{region.AverageCorrectCount:F2} | AvgROI:{region.AverageRoi:P2} | ROI+:{region.PositiveRoiRate:P1} | BasariRate:{region.SuccessRate:P1} | AvgKolon:{region.AverageCouponCount:F1}");
            }

            sb.AppendLine();
        }

        private static IReadOnlyList<LeaveOneRoundOutSummary> BuildLeaveOneRoundOut(
            IReadOnlyList<CounterfactualStabilityRow> rows)
        {
            var result = new List<LeaveOneRoundOutSummary>();
            foreach (var roundId in rows.Select(x => x.SourceRoundId).Distinct().OrderBy(x => x))
            {
                var trainRows = rows.Where(x => x.SourceRoundId != roundId).ToList();
                var testRowsForRound = rows.Where(x => x.SourceRoundId == roundId).ToList();
                var region = BuildStableRegions(trainRows).FirstOrDefault(x => x.SuccessCount > 0);
                if (region == null)
                {
                    result.Add(LeaveOneRoundOutSummary.Empty(roundId));
                    continue;
                }

                var testRows = testRowsForRound
                    .Where(x => region.Key.Contains(x))
                    .ToList();

                result.Add(new LeaveOneRoundOutSummary(
                    roundId,
                    region.Key,
                    region.SuccessRoundCount,
                    region.SuccessRate,
                    testRows.Count,
                    testRows.Select(x => x.SourceRoundId).Distinct().Count(),
                    testRows.Count == 0 ? 0 : testRows.Average(x => x.BestHitCount),
                    testRows.Count == 0 ? 0 : testRows.Max(x => x.BestHitCount),
                    testRows.Count == 0 ? 0 : testRows.Average(x => x.Roi),
                    testRows.Count == 0 ? 0 : Ratio(testRows.Count(x => x.IsPositiveRoi), testRows.Count),
                    testRows.Count == 0 ? 0 : Ratio(testRows.Count(x => x.IsRobustSuccess), testRows.Count),
                    testRows.Any(x => x.IsExact)));
            }

            return result;
        }

        private static void AppendLeaveOneRoundOut(
            StringBuilder sb,
            IReadOnlyList<LeaveOneRoundOutSummary> rows)
        {
            sb.AppendLine("LEAVE-ONE-ROUND-OUT TESTI");
            if (rows.Count == 0)
            {
                sb.AppendLine("- Test edilecek round yok.");
                sb.AppendLine();
                return;
            }

            foreach (var row in rows)
            {
                if (!row.HasRegion)
                {
                    sb.AppendLine($"- Round {row.TestRoundId}: Egitim round'larinda stabil bolge bulunamadi.");
                    continue;
                }

                sb.AppendLine(
                    $"- TestRound:{row.TestRoundId} | TrainBolge:{row.Region.RangeText} | TrainBasariRound:{row.TrainSuccessRoundCount:n0} | " +
                    $"TrainBasariRate:{row.TrainSuccessRate:P1} | TestSatir:{row.TestRowCount:n0} | TestAvg:{row.TestAverageCorrect:F2} | " +
                    $"TestBest:{row.TestBestCorrect}/15 | TestROI:{row.TestAverageRoi:P2} | TestROI+:{row.TestPositiveRoiRate:P1} | TestBasari:{row.TestSuccessRate:P1} | Exact:{(row.TestHasExact ? "evet" : "hayir")}");
            }

            sb.AppendLine();
        }

        private static IReadOnlyList<SeedNeighborhoodSummary> BuildSeedNeighborhoods(
            IReadOnlyList<CounterfactualStabilityRow> rows)
        {
            var seeds = rows
                .Where(x => x.IsExact)
                .OrderByDescending(x => x.NetProfitAmount)
                .ThenByDescending(x => x.Roi)
                .GroupBy(x => $"{x.ThirdChoiceMinRatio:F4}|{x.ProbabilityUniformBlend:F4}|{x.PatternScoreWeight:F4}|{x.CouponCount}")
                .Select(x => x.First())
                .Take(10)
                .ToList();

            var result = new List<SeedNeighborhoodSummary>();
            foreach (var seed in seeds)
            {
                var neighbors = rows
                    .Where(x =>
                        Math.Abs(x.ThirdChoiceMinRatio - seed.ThirdChoiceMinRatio) <= 0.02 &&
                        Math.Abs(x.ProbabilityUniformBlend - seed.ProbabilityUniformBlend) <= 0.02 &&
                        Math.Abs(x.PatternScoreWeight - seed.PatternScoreWeight) <= 0.05)
                    .ToList();

                result.Add(new SeedNeighborhoodSummary(
                    seed.SourceRoundId,
                    seed.CouponCount,
                    seed.ThirdChoiceMinRatio,
                    seed.ProbabilityUniformBlend,
                    seed.PatternScoreWeight,
                    neighbors.Count,
                    neighbors.Select(x => x.SourceRoundId).Distinct().Count(),
                    neighbors.Count == 0 ? 0 : neighbors.Average(x => x.BestHitCount),
                    neighbors.Count == 0 ? 0 : neighbors.Max(x => x.BestHitCount),
                    neighbors.Count == 0 ? 0 : neighbors.Average(x => x.Roi),
                    neighbors.Count == 0 ? 0 : Ratio(neighbors.Count(x => x.IsPositiveRoi), neighbors.Count),
                    neighbors.Count == 0 ? 0 : Ratio(neighbors.Count(x => x.IsRobustSuccess), neighbors.Count),
                    neighbors.Count(x => x.IsExact)));
            }

            return result;
        }

        private static void AppendSeedNeighborhoods(
            StringBuilder sb,
            IReadOnlyList<SeedNeighborhoodSummary> rows)
        {
            sb.AppendLine("15/15 SEED EPSILON KOMSULUK ANALIZI");
            sb.AppendLine("Epsilon: X +/-0.02, Y +/-0.02, Z +/-0.05.");
            if (rows.Count == 0)
            {
                sb.AppendLine("- 15/15 seed bulunamadi; komsuluk analizi yapilamadi.");
                sb.AppendLine();
                return;
            }

            foreach (var row in rows)
            {
                sb.AppendLine(
                    $"- SeedRound:{row.SeedRoundId} | Kolon:{row.CouponCount} | X:{row.ThirdChoiceMinRatio:F4} Y:{row.ProbabilityUniformBlend:F4} Z:{row.PatternScoreWeight:F4} | " +
                    $"Komsu:{row.NeighborCount:n0} | Round:{row.RoundCount:n0} | AvgCorrect:{row.AverageCorrectCount:F2} | Best:{row.BestCorrectCount}/15 | " +
                    $"AvgROI:{row.AverageRoi:P2} | ROI+:{row.PositiveRoiRate:P1} | Basari:{row.SuccessRate:P1} | ExactKomsu:{row.ExactNeighborCount:n0}");
            }

            sb.AppendLine();
        }

        private static void AppendSamplingComparison(StringBuilder sb)
        {
            const int sampleCount = 2000;
            var haltonBins = BuildSamplingCoverage(sampleCount, useHalton: true);
            var randomBins = BuildSamplingCoverage(sampleCount, useHalton: false);

            sb.AppendLine("LOW-DISCREPANCY VS RANDOM SAMPLING KARSILASTIRMASI");
            sb.AppendLine($"- Simulasyon ornek sayisi: {sampleCount:n0}");
            sb.AppendLine($"- Halton/low-discrepancy dolu bin: {haltonBins:n0}");
            sb.AppendLine($"- Sabit seed random dolu bin: {randomBins:n0}");
            sb.AppendLine("- Yorum: Halton deterministik ve tekrar edilebilir oldugu icin otopsi resume/skip mantigina daha uygundur. Random ise lokal tuzaklardan kacmak icin kucuk oranda perturbasyon olarak kullanilmali.");
            sb.AppendLine("- Uygulama stratejisi: yeni aramada %60 multi-seed exploitation, %40 global low-discrepancy exploration.");
            sb.AppendLine();
        }

        private static int BuildSamplingCoverage(int count, bool useHalton)
        {
            var random = new Random(42);
            var bins = new HashSet<StabilityBinKey>();
            for (var i = 1; i <= count; i++)
            {
                var x = useHalton ? Scale(RadicalInverse(i, 2), 0, XMaximum) : random.NextDouble() * XMaximum;
                var y = useHalton ? Scale(RadicalInverse(i, 3), 0, YMaximum) : random.NextDouble() * YMaximum;
                var z = useHalton ? Scale(RadicalInverse(i, 5), 0, ZMaximum) : random.NextDouble() * ZMaximum;
                bins.Add(StabilityBinKey.FromValues(x, y, z));
            }

            return bins.Count;
        }

        private static string WriteStableRegionsCsv(
            string outputDirectory,
            IReadOnlyList<StableRegionSummary> rows)
        {
            var path = Path.Combine(outputDirectory, "ParameterStability_StableRegions.csv");
            var sb = new StringBuilder();
            sb.AppendLine("XMin,XMax,YMin,YMax,ZMin,ZMax,RowCount,RoundCount,SuccessCount,SuccessRoundCount,ExactCount,AvgCorrect,AvgROI,PositiveRoiRate,SuccessRate,AvgCoupon,Score");
            foreach (var row in rows)
            {
                sb.AppendLine(string.Join(",",
                    row.Key.XMin.ToString(CultureInfo.InvariantCulture),
                    row.Key.XMax.ToString(CultureInfo.InvariantCulture),
                    row.Key.YMin.ToString(CultureInfo.InvariantCulture),
                    row.Key.YMax.ToString(CultureInfo.InvariantCulture),
                    row.Key.ZMin.ToString(CultureInfo.InvariantCulture),
                    row.Key.ZMax.ToString(CultureInfo.InvariantCulture),
                    row.RowCount,
                    row.RoundCount,
                    row.SuccessCount,
                    row.SuccessRoundCount,
                    row.ExactCount,
                    row.AverageCorrectCount.ToString(CultureInfo.InvariantCulture),
                    row.AverageRoi.ToString(CultureInfo.InvariantCulture),
                    row.PositiveRoiRate.ToString(CultureInfo.InvariantCulture),
                    row.SuccessRate.ToString(CultureInfo.InvariantCulture),
                    row.AverageCouponCount.ToString(CultureInfo.InvariantCulture),
                    row.Score.ToString(CultureInfo.InvariantCulture)));
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return path;
        }

        private static string WriteLeaveOneRoundOutCsv(
            string outputDirectory,
            IReadOnlyList<LeaveOneRoundOutSummary> rows)
        {
            var path = Path.Combine(outputDirectory, "ParameterStability_LeaveOneRoundOut.csv");
            var sb = new StringBuilder();
            sb.AppendLine("TestRoundId,HasRegion,XMin,XMax,YMin,YMax,ZMin,ZMax,TrainSuccessRoundCount,TrainSuccessRate,TestRowCount,TestAvgCorrect,TestBestCorrect,TestAvgROI,TestPositiveRoiRate,TestSuccessRate,TestHasExact");
            foreach (var row in rows)
            {
                sb.AppendLine(string.Join(",",
                    row.TestRoundId,
                    row.HasRegion,
                    row.HasRegion ? row.Region.XMin.ToString(CultureInfo.InvariantCulture) : string.Empty,
                    row.HasRegion ? row.Region.XMax.ToString(CultureInfo.InvariantCulture) : string.Empty,
                    row.HasRegion ? row.Region.YMin.ToString(CultureInfo.InvariantCulture) : string.Empty,
                    row.HasRegion ? row.Region.YMax.ToString(CultureInfo.InvariantCulture) : string.Empty,
                    row.HasRegion ? row.Region.ZMin.ToString(CultureInfo.InvariantCulture) : string.Empty,
                    row.HasRegion ? row.Region.ZMax.ToString(CultureInfo.InvariantCulture) : string.Empty,
                    row.TrainSuccessRoundCount,
                    row.TrainSuccessRate.ToString(CultureInfo.InvariantCulture),
                    row.TestRowCount,
                    row.TestAverageCorrect.ToString(CultureInfo.InvariantCulture),
                    row.TestBestCorrect.ToString(CultureInfo.InvariantCulture),
                    row.TestAverageRoi.ToString(CultureInfo.InvariantCulture),
                    row.TestPositiveRoiRate.ToString(CultureInfo.InvariantCulture),
                    row.TestSuccessRate.ToString(CultureInfo.InvariantCulture),
                    row.TestHasExact));
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return path;
        }

        private static string WriteSeedNeighborhoodCsv(
            string outputDirectory,
            IReadOnlyList<SeedNeighborhoodSummary> rows)
        {
            var path = Path.Combine(outputDirectory, "ParameterStability_SeedNeighborhoods.csv");
            var sb = new StringBuilder();
            sb.AppendLine("SeedRoundId,CouponCount,ThirdChoiceMinRatio,ProbabilityUniformBlend,PatternScoreWeight,NeighborCount,RoundCount,AverageCorrect,BestCorrect,AverageROI,PositiveRoiRate,SuccessRate,ExactNeighborCount");
            foreach (var row in rows)
            {
                sb.AppendLine(string.Join(",",
                    row.SeedRoundId,
                    row.CouponCount,
                    row.ThirdChoiceMinRatio.ToString(CultureInfo.InvariantCulture),
                    row.ProbabilityUniformBlend.ToString(CultureInfo.InvariantCulture),
                    row.PatternScoreWeight.ToString(CultureInfo.InvariantCulture),
                    row.NeighborCount,
                    row.RoundCount,
                    row.AverageCorrectCount.ToString(CultureInfo.InvariantCulture),
                    row.BestCorrectCount,
                    row.AverageRoi.ToString(CultureInfo.InvariantCulture),
                    row.PositiveRoiRate.ToString(CultureInfo.InvariantCulture),
                    row.SuccessRate.ToString(CultureInfo.InvariantCulture),
                    row.ExactNeighborCount));
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return path;
        }

        private static string RenderHeatmap(
            string outputDirectory,
            IReadOnlyList<CounterfactualStabilityRow> rows,
            HeatmapMetric metric)
        {
            var path = Path.Combine(
                outputDirectory,
                metric == HeatmapMetric.Roi
                    ? "ParameterStability_ROIHeatmap.png"
                    : "ParameterStability_CorrectCountHeatmap.png");
            var title = metric == HeatmapMetric.Roi
                ? "ROI Heatmap"
                : "CorrectCount Heatmap";

            using var bitmap = new Bitmap(1200, 760);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.FromArgb(24, 24, 24));
            RenderHeatmapPanel(
                graphics,
                new Rectangle(72, 72, 1060, 590),
                rows,
                title,
                metric,
                drawAxes: true);
            bitmap.Save(path);
            return path;
        }

        private static string RenderPatternFacetHeatmap(
            string outputDirectory,
            IReadOnlyList<CounterfactualStabilityRow> rows)
        {
            var path = Path.Combine(outputDirectory, "ParameterStability_PatternFacets_ROI.png");
            using var bitmap = new Bitmap(1300, 900);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.FromArgb(24, 24, 24));
            graphics.SmoothingMode = SmoothingMode.AntiAlias;

            using var titleFont = new Font("Segoe UI", 13, FontStyle.Bold);
            graphics.DrawString(
                "PatternScoreWeight Facet ROI Heatmap",
                titleFont,
                Brushes.Gainsboro,
                24,
                18);

            var panels = new[]
            {
                (Min: 0.0, Max: 0.5, Rect: new Rectangle(60, 70, 560, 350)),
                (Min: 0.5, Max: 1.0, Rect: new Rectangle(700, 70, 560, 350)),
                (Min: 1.0, Max: 1.5, Rect: new Rectangle(60, 500, 560, 350)),
                (Min: 1.5, Max: 2.01, Rect: new Rectangle(700, 500, 560, 350))
            };

            foreach (var panel in panels)
            {
                var panelRows = rows
                    .Where(x => x.PatternScoreWeight >= panel.Min && x.PatternScoreWeight < panel.Max)
                    .ToList();
                RenderHeatmapPanel(
                    graphics,
                    panel.Rect,
                    panelRows,
                    $"Z {panel.Min:F1}-{Math.Min(panel.Max, 2.0):F1}",
                    HeatmapMetric.Roi,
                    drawAxes: false);
            }

            bitmap.Save(path);
            return path;
        }

        private static string RenderScatter(
            string outputDirectory,
            IReadOnlyList<CounterfactualStabilityRow> rows)
        {
            var path = Path.Combine(outputDirectory, "ParameterStability_Scatter.png");
            using var bitmap = new Bitmap(1300, 780);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.FromArgb(20, 20, 20));
            graphics.SmoothingMode = SmoothingMode.AntiAlias;

            var plot = new Rectangle(70, 70, 1120, 600);
            DrawAxes(graphics, plot, "ThirdSelectionThreshold", "UniformBlend");
            using var titleFont = new Font("Segoe UI", 12, FontStyle.Bold);
            graphics.DrawString(
                "Parametre Scatter | Renk=ROI, Boyut=Kolon, Kirmizi halka=15/15",
                titleFont,
                Brushes.Gainsboro,
                24,
                22);

            var maxPoints = 35000;
            var step = Math.Max(rows.Count / maxPoints, 1);
            foreach (var row in rows.Where((_, i) => i % step == 0))
            {
                var x = plot.Left + (float)(Math.Clamp(row.ThirdChoiceMinRatio, 0, XMaximum) / XMaximum * plot.Width);
                var y = plot.Bottom - (float)(Math.Clamp(row.ProbabilityUniformBlend, 0, YMaximum) / YMaximum * plot.Height);
                var size = Math.Clamp(2f + (row.CouponCount / 25f), 3f, 8f);
                using var brush = new SolidBrush(GetRoiColor(row.Roi));
                graphics.FillEllipse(brush, x - (size / 2f), y - (size / 2f), size, size);
                if (row.IsExact)
                {
                    using var pen = new Pen(Color.Red, 1.8f);
                    graphics.DrawEllipse(pen, x - 6, y - 6, 12, 12);
                }
            }

            bitmap.Save(path);
            return path;
        }

        private static void RenderHeatmapPanel(
            Graphics graphics,
            Rectangle plot,
            IReadOnlyList<CounterfactualStabilityRow> rows,
            string title,
            HeatmapMetric metric,
            bool drawAxes)
        {
            using var titleFont = new Font("Segoe UI", 11, FontStyle.Bold);
            using var smallFont = new Font("Segoe UI", 8);

            if (drawAxes)
            {
                graphics.DrawString(title, titleFont, Brushes.Gainsboro, 24, 24);
                DrawAxes(graphics, plot, "ThirdSelectionThreshold", "UniformBlend");
            }
            else
            {
                graphics.DrawString(title, titleFont, Brushes.Gainsboro, plot.Left, plot.Top - 24);
                DrawPlotFrame(graphics, plot);
            }

            var groups = rows
                .GroupBy(x => new XYBinKey(
                    Bin(x.ThirdChoiceMinRatio, XBinWidth, XMaximum),
                    Bin(x.ProbabilityUniformBlend, YBinWidth, YMaximum)))
                .ToDictionary(
                    x => x.Key,
                    x => metric == HeatmapMetric.Roi
                        ? x.Average(r => Math.Clamp(r.Roi, -5.0, 50.0))
                        : x.Average(r => (double)r.BestHitCount));

            var xBinCount = BinCount(XMaximum, XBinWidth);
            var yBinCount = BinCount(YMaximum, YBinWidth);
            var cellWidth = plot.Width / (float)xBinCount;
            var cellHeight = plot.Height / (float)yBinCount;

            foreach (var pair in groups)
            {
                var x = plot.Left + (pair.Key.XBin * cellWidth);
                var y = plot.Bottom - ((pair.Key.YBin + 1) * cellHeight);
                using var brush = new SolidBrush(metric == HeatmapMetric.Roi
                    ? GetRoiColor(pair.Value)
                    : GetCorrectColor(pair.Value));
                graphics.FillRectangle(brush, x, y, Math.Max(cellWidth, 1), Math.Max(cellHeight, 1));
            }

            foreach (var exact in rows.Where(x => x.IsExact).Take(250))
            {
                var x = plot.Left + (float)(Math.Clamp(exact.ThirdChoiceMinRatio, 0, XMaximum) / XMaximum * plot.Width);
                var y = plot.Bottom - (float)(Math.Clamp(exact.ProbabilityUniformBlend, 0, YMaximum) / YMaximum * plot.Height);
                using var pen = new Pen(Color.Red, 2f);
                graphics.DrawEllipse(pen, x - 5, y - 5, 10, 10);
            }

            graphics.DrawString(
                $"Satir:{rows.Count:n0}",
                smallFont,
                Brushes.LightGray,
                plot.Left,
                plot.Bottom + 8);
        }

        private static void DrawAxes(Graphics graphics, Rectangle plot, string xLabel, string yLabel)
        {
            DrawPlotFrame(graphics, plot);
            using var smallFont = new Font("Segoe UI", 8);
            using var gridPen = new Pen(Color.FromArgb(55, 55, 55));

            for (var i = 0; i <= 10; i++)
            {
                var x = plot.Left + (plot.Width * i / 10f);
                graphics.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);
                graphics.DrawString((XMaximum * i / 10.0).ToString("0.00"), smallFont, Brushes.Gray, x - 12, plot.Bottom + 6);
            }

            for (var i = 0; i <= 7; i++)
            {
                var y = plot.Bottom - (plot.Height * i / 7f);
                graphics.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                graphics.DrawString((YMaximum * i / 7.0).ToString("0.00"), smallFont, Brushes.Gray, 18, y - 7);
            }

            graphics.DrawString(xLabel, smallFont, Brushes.Gainsboro, plot.Left + (plot.Width / 2) - 64, plot.Bottom + 32);
            graphics.DrawString(yLabel, smallFont, Brushes.Gainsboro, plot.Left - 48, plot.Top - 24);
        }

        private static void DrawPlotFrame(Graphics graphics, Rectangle plot)
        {
            using var axisPen = new Pen(Color.FromArgb(150, 150, 150));
            using var background = new SolidBrush(Color.FromArgb(24, 24, 24));
            graphics.FillRectangle(background, plot);
            graphics.DrawRectangle(axisPen, plot);
        }

        private static Color GetRoiColor(double roi)
        {
            if (roi > 0)
            {
                var v = (int)Math.Clamp(80 + Math.Log10(roi + 1.0) * 70, 80, 255);
                return Color.FromArgb(40, v, 220);
            }

            if (roi < 0)
            {
                var v = (int)Math.Clamp(90 + Math.Abs(roi) * 35, 90, 220);
                return Color.FromArgb(v, 70, 70);
            }

            return Color.FromArgb(90, 90, 90);
        }

        private static Color GetCorrectColor(double correct)
        {
            return correct switch
            {
                >= 15 => Color.Red,
                >= 14 => Color.LimeGreen,
                >= 13 => Color.Orange,
                >= 12 => Color.DeepSkyBlue,
                _ => Color.FromArgb(90, 90, 90)
            };
        }

        private static double Ratio(int numerator, int denominator)
        {
            return denominator <= 0 ? 0.0 : numerator / (double)denominator;
        }

        private static int Bin(double value, double width, double maximum)
        {
            return (int)Math.Clamp(Math.Floor(Math.Clamp(value, 0.0, maximum) / width), 0, BinCount(maximum, width) - 1);
        }

        private static int BinCount(double maximum, double width)
        {
            return (int)Math.Ceiling(maximum / width);
        }

        private static double Scale(double unit, double min, double max)
        {
            return min + (unit * (max - min));
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

        private enum HeatmapMetric
        {
            Roi,
            CorrectCount
        }

        private readonly record struct XYBinKey(int XBin, int YBin);

        private readonly record struct StabilityBinKey(int XBin, int YBin, int ZBin)
        {
            public double XMin => XBin * XBinWidth;
            public double XMax => Math.Min(XMaximum, XMin + XBinWidth);
            public double YMin => YBin * YBinWidth;
            public double YMax => Math.Min(YMaximum, YMin + YBinWidth);
            public double ZMin => ZBin * ZBinWidth;
            public double ZMax => Math.Min(ZMaximum, ZMin + ZBinWidth);

            public string RangeText =>
                $"X:{XMin:F2}-{XMax:F2} | Y:{YMin:F2}-{YMax:F2} | Z:{ZMin:F2}-{ZMax:F2}";

            public static StabilityBinKey From(CounterfactualStabilityRow row)
            {
                return FromValues(row.ThirdChoiceMinRatio, row.ProbabilityUniformBlend, row.PatternScoreWeight);
            }

            public static StabilityBinKey FromValues(double x, double y, double z)
            {
                return new StabilityBinKey(
                    Bin(x, XBinWidth, XMaximum),
                    Bin(y, YBinWidth, YMaximum),
                    Bin(z, ZBinWidth, ZMaximum));
            }

            public bool Contains(CounterfactualStabilityRow row)
            {
                return row.ThirdChoiceMinRatio >= XMin &&
                       row.ThirdChoiceMinRatio < XMax &&
                       row.ProbabilityUniformBlend >= YMin &&
                       row.ProbabilityUniformBlend < YMax &&
                       row.PatternScoreWeight >= ZMin &&
                       row.PatternScoreWeight < ZMax;
            }
        }

        private sealed record StableRegionSummary(
            StabilityBinKey Key,
            int RowCount,
            int RoundCount,
            int SuccessCount,
            int SuccessRoundCount,
            int ExactCount,
            double AverageCorrectCount,
            double AverageRoi,
            double PositiveRoiRate,
            double SuccessRate,
            double AverageCouponCount,
            double Score);

        private sealed record LeaveOneRoundOutSummary(
            int TestRoundId,
            StabilityBinKey Region,
            int TrainSuccessRoundCount,
            double TrainSuccessRate,
            int TestRowCount,
            int TestRoundCoverage,
            double TestAverageCorrect,
            int TestBestCorrect,
            double TestAverageRoi,
            double TestPositiveRoiRate,
            double TestSuccessRate,
            bool TestHasExact)
        {
            public bool HasRegion => Region != default || TrainSuccessRoundCount > 0 || TestRowCount > 0;

            public static LeaveOneRoundOutSummary Empty(int testRoundId)
            {
                return new LeaveOneRoundOutSummary(
                    testRoundId,
                    default,
                    0,
                    0.0,
                    0,
                    0,
                    0.0,
                    0,
                    0.0,
                    0.0,
                    0.0,
                    false);
            }
        }

        private sealed record SeedNeighborhoodSummary(
            int SeedRoundId,
            int CouponCount,
            double ThirdChoiceMinRatio,
            double ProbabilityUniformBlend,
            double PatternScoreWeight,
            int NeighborCount,
            int RoundCount,
            double AverageCorrectCount,
            int BestCorrectCount,
            double AverageRoi,
            double PositiveRoiRate,
            double SuccessRate,
            int ExactNeighborCount);
    }

    public sealed record ParameterStabilityAnalysisResult(
        string ReportSection,
        IReadOnlyList<string> GeneratedFiles);
}
