using SporTotoFormApp.Data;

namespace SporTotoFormApp.Services
{
    public sealed class WeekPatternModel
    {
        private const int MatchCount = 15;
        private const double LowProbabilityThreshold = 0.18;

        private readonly PatternSummary _winnerSummary;
        private readonly PatternSummary _recentSummary;
        private readonly NumericSummary _previousWeekDistanceSummary;
        private readonly string? _previousResultLine;
        private readonly RankExpectation _rankExpectation;

        private WeekPatternModel(
            int historicalWeekCount,
            int winnerWeekCount,
            int recentWeekCount,
            PatternSummary winnerSummary,
            PatternSummary recentSummary,
            NumericSummary previousWeekDistanceSummary,
            string? previousResultLine,
            RankExpectation rankExpectation,
            string source)
        {
            HistoricalWeekCount = historicalWeekCount;
            WinnerWeekCount = winnerWeekCount;
            RecentWeekCount = recentWeekCount;
            _winnerSummary = winnerSummary;
            _recentSummary = recentSummary;
            _previousWeekDistanceSummary = previousWeekDistanceSummary;
            _previousResultLine = previousResultLine;
            _rankExpectation = rankExpectation;
            Source = source;
        }

        public int HistoricalWeekCount { get; }
        public int WinnerWeekCount { get; }
        public int RecentWeekCount { get; }
        public string Source { get; }

        public string Message =>
            $"Hafta oruntu modeli: {HistoricalWeekCount} hafta | 15 bilenli hafta: {WinnerWeekCount} | " +
            $"son hafta penceresi: {RecentWeekCount} | onceki hafta mesafe ort: {_previousWeekDistanceSummary.Mean:F1}";

        public static WeekPatternModel Create(
            string baseDirectory,
            HistoricalOutcomeModel outcomeModel)
        {
            var rows = LoadRows(baseDirectory)
                .Where(x => IsValidLine(x.ResultLine))
                .GroupBy(x => x.RoundId.HasValue ? $"R:{x.RoundId.Value}" : $"L:{x.ResultLine}")
                .Select(x => x.Last())
                .OrderBy(x => x.SeasonYear ?? 0)
                .ThenBy(x => x.WeekNumber ?? 0)
                .ThenBy(x => x.RoundId ?? x.Id)
                .ThenBy(x => x.Id)
                .ToList();

            if (rows.Count < 20)
            {
                return CreateDefault(outcomeModel, rows.Count);
            }

            var allFeatures = rows
                .Select(row => PatternFeatures.FromLine(row.ResultLine))
                .ToList();
            var winnerFeatures = rows
                .Where(row => row.Hit15WinnerCount.GetValueOrDefault() > 0)
                .Select(row => PatternFeatures.FromLine(row.ResultLine))
                .ToList();
            if (winnerFeatures.Count < 20)
            {
                winnerFeatures = allFeatures;
            }

            var recentFeatures = rows
                .Skip(Math.Max(0, rows.Count - 52))
                .Select(row => PatternFeatures.FromLine(row.ResultLine))
                .ToList();
            var previousDistances = new List<int>();
            for (var i = 1; i < rows.Count; i++)
            {
                previousDistances.Add(Distance(rows[i - 1].ResultLine, rows[i].ResultLine));
            }

            return new WeekPatternModel(
                rows.Count,
                rows.Count(row => row.Hit15WinnerCount.GetValueOrDefault() > 0),
                recentFeatures.Count,
                PatternSummary.From(winnerFeatures),
                PatternSummary.From(recentFeatures),
                NumericSummary.From(previousDistances.Select(x => (double)x)),
                rows.LastOrDefault()?.ResultLine,
                RankExpectation.From(outcomeModel),
                "SQL Server HistoricalResults + HistoricalResultPayouts");
        }

        public double GetPreScoreAdjustment(
            string prediction,
            HistoricalOutcomeModel outcomeModel,
            OptimizationOptions options)
        {
            return options.PatternScoreWeight *
                   Score(prediction, outcomeModel, options);
        }

        public double GetUtilityMultiplier(
            string prediction,
            HistoricalOutcomeModel outcomeModel,
            OptimizationOptions options)
        {
            var score = Score(prediction, outcomeModel, options);
            return Math.Exp(Math.Clamp(options.PatternScoreWeight * score, -1.25, 0.45));
        }

        private double Score(
            string prediction,
            HistoricalOutcomeModel outcomeModel,
            OptimizationOptions options)
        {
            if (!IsValidLine(prediction) || HistoricalWeekCount < 20)
            {
                return 0.0;
            }

            var features = PatternFeatures.FromPrediction(
                prediction,
                outcomeModel,
                _previousResultLine);
            var winnerPenalty = _winnerSummary.Distance(features);
            var recentPenalty = _recentSummary.Distance(features);
            var previousWeekPenalty = features.PreviousWeekDistance.HasValue
                ? _previousWeekDistanceSummary.NormalizedDistance(features.PreviousWeekDistance.Value)
                : 0.0;
            var surprisePenalty = _rankExpectation.Distance(features);

            var penalty =
                (options.WinnerPatternWeight * winnerPenalty) +
                (options.RecentPatternWeight * recentPenalty) +
                (options.PreviousWeekPatternWeight * previousWeekPenalty) +
                (options.SurpriseBalanceWeight * surprisePenalty);

            return -Math.Clamp(penalty, 0.0, 8.0);
        }

        private static WeekPatternModel CreateDefault(
            HistoricalOutcomeModel outcomeModel,
            int weekCount)
        {
            var synthetic = new List<PatternFeatures>();
            for (var one = 4; one <= 8; one++)
            {
                for (var draw = 2; draw <= 5; draw++)
                {
                    var two = MatchCount - one - draw;
                    if (two < 2 || two > 7)
                    {
                        continue;
                    }

                    synthetic.Add(new PatternFeatures(
                        one,
                        draw,
                        two,
                        9,
                        3,
                        null,
                        0,
                        0,
                        0,
                        0));
                }
            }

            return new WeekPatternModel(
                weekCount,
                0,
                synthetic.Count,
                PatternSummary.From(synthetic),
                PatternSummary.From(synthetic),
                new NumericSummary(9.0, 2.0),
                null,
                RankExpectation.From(outcomeModel),
                "Varsayilan hafta oruntu modeli");
        }

        private static List<HistoricalResultPatternRow> LoadRows(string baseDirectory)
        {
            try
            {
                return new HistoricalResultRepository().GetPatternRows();
            }
            catch
            {
                return ReadHistoricalFile(baseDirectory)
                    .Select((line, index) => new HistoricalResultPatternRow(
                        index + 1,
                        null,
                        line,
                        null,
                        null,
                        null,
                        null))
                    .ToList();
            }
        }

        private static List<string> ReadHistoricalFile(string baseDirectory)
        {
            var dataPath = FindHistoricalFile(baseDirectory);
            if (dataPath == null || !File.Exists(dataPath))
            {
                return new List<string>();
            }

            return File.ReadAllLines(dataPath)
                .Select(NormalizeLine)
                .Where(x => x != null)
                .Select(x => x!)
                .ToList();
        }

        private static string? FindHistoricalFile(string baseDirectory)
        {
            const string relativePath = "Data/historical_results.txt";
            var current = new DirectoryInfo(baseDirectory);

            for (var i = 0; i < 6 && current != null; i++)
            {
                var candidate = Path.Combine(current.FullName, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                current = current.Parent;
            }

            return null;
        }

        private static string? NormalizeLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            var normalized = new string(line
                .Where(c => !char.IsWhiteSpace(c))
                .Select(char.ToUpperInvariant)
                .ToArray());

            return IsValidLine(normalized) ? normalized : null;
        }

        private static bool IsValidLine(string? line)
        {
            return line is { Length: MatchCount } &&
                   line.All(c => c is '1' or 'X' or '2');
        }

        private static int Distance(string left, string right)
        {
            var distance = 0;
            for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
            {
                if (left[i] != right[i])
                {
                    distance++;
                }
            }

            return distance + Math.Abs(left.Length - right.Length);
        }

        private sealed record PatternFeatures(
            int OneCount,
            int DrawCount,
            int TwoCount,
            int Transitions,
            int LongestRun,
            int? PreviousWeekDistance,
            int FavoriteCount,
            int SecondChoiceCount,
            int ThirdChoiceCount,
            int LowProbabilityCount)
        {
            public static PatternFeatures FromLine(string line)
            {
                var one = 0;
                var draw = 0;
                var two = 0;
                var transitions = 0;
                var longestRun = 0;
                var currentRun = 0;
                char? previous = null;

                foreach (var symbol in line)
                {
                    switch (symbol)
                    {
                        case '1': one++; break;
                        case 'X': draw++; break;
                        case '2': two++; break;
                    }

                    if (previous == symbol)
                    {
                        currentRun++;
                    }
                    else
                    {
                        if (previous.HasValue)
                        {
                            transitions++;
                        }

                        currentRun = 1;
                    }

                    longestRun = Math.Max(longestRun, currentRun);
                    previous = symbol;
                }

                return new PatternFeatures(
                    one,
                    draw,
                    two,
                    transitions,
                    longestRun,
                    null,
                    0,
                    0,
                    0,
                    0);
            }

            public static PatternFeatures FromPrediction(
                string prediction,
                HistoricalOutcomeModel outcomeModel,
                string? previousResultLine)
            {
                var baseFeatures = FromLine(prediction);
                var favorite = 0;
                var second = 0;
                var third = 0;
                var lowProbability = 0;

                for (var i = 0; i < prediction.Length; i++)
                {
                    var p = outcomeModel.GetForPosition(i);
                    var ordered = new[]
                        {
                            new RankedSymbol('1', p.One),
                            new RankedSymbol('X', p.Draw),
                            new RankedSymbol('2', p.Two)
                        }
                        .OrderByDescending(x => x.Probability)
                        .ToArray();
                    var selected = ordered
                        .Select((choice, index) => new { choice.Symbol, choice.Probability, Rank = index })
                        .First(x => x.Symbol == prediction[i]);

                    switch (selected.Rank)
                    {
                        case 0: favorite++; break;
                        case 1: second++; break;
                        default: third++; break;
                    }

                    if (selected.Probability < LowProbabilityThreshold)
                    {
                        lowProbability++;
                    }
                }

                return baseFeatures with
                {
                    PreviousWeekDistance = previousResultLine == null
                        ? null
                        : Distance(previousResultLine, prediction),
                    FavoriteCount = favorite,
                    SecondChoiceCount = second,
                    ThirdChoiceCount = third,
                    LowProbabilityCount = lowProbability
                };
            }
        }

        private sealed record PatternSummary(
            NumericSummary OneCount,
            NumericSummary DrawCount,
            NumericSummary TwoCount,
            NumericSummary Transitions,
            NumericSummary LongestRun)
        {
            public static PatternSummary From(IReadOnlyList<PatternFeatures> features)
            {
                return new PatternSummary(
                    NumericSummary.From(features.Select(x => (double)x.OneCount)),
                    NumericSummary.From(features.Select(x => (double)x.DrawCount)),
                    NumericSummary.From(features.Select(x => (double)x.TwoCount)),
                    NumericSummary.From(features.Select(x => (double)x.Transitions)),
                    NumericSummary.From(features.Select(x => (double)x.LongestRun)));
            }

            public double Distance(PatternFeatures features)
            {
                return
                    (OneCount.NormalizedDistance(features.OneCount) * 1.00) +
                    (DrawCount.NormalizedDistance(features.DrawCount) * 1.15) +
                    (TwoCount.NormalizedDistance(features.TwoCount) * 1.00) +
                    (Transitions.NormalizedDistance(features.Transitions) * 0.65) +
                    (LongestRun.NormalizedDistance(features.LongestRun) * 0.45);
            }
        }

        private sealed record RankExpectation(
            NumericSummary FavoriteCount,
            NumericSummary SecondChoiceCount,
            NumericSummary ThirdChoiceCount,
            NumericSummary LowProbabilityCount)
        {
            public static RankExpectation From(HistoricalOutcomeModel outcomeModel)
            {
                var favoriteMean = 0.0;
                var favoriteVariance = 0.0;
                var secondMean = 0.0;
                var secondVariance = 0.0;
                var thirdMean = 0.0;
                var thirdVariance = 0.0;
                var lowMean = 0.0;
                var lowVariance = 0.0;

                for (var i = 0; i < MatchCount; i++)
                {
                    var p = outcomeModel.GetForPosition(i);
                    var ordered = new[] { p.One, p.Draw, p.Two }
                        .OrderByDescending(x => x)
                        .ToArray();
                    AddBernoulli(ordered[0], ref favoriteMean, ref favoriteVariance);
                    AddBernoulli(ordered[1], ref secondMean, ref secondVariance);
                    AddBernoulli(ordered[2], ref thirdMean, ref thirdVariance);

                    foreach (var probability in ordered.Where(x => x < LowProbabilityThreshold))
                    {
                        AddBernoulli(probability, ref lowMean, ref lowVariance);
                    }
                }

                return new RankExpectation(
                    new NumericSummary(favoriteMean, Math.Sqrt(Math.Max(favoriteVariance, 1.0))),
                    new NumericSummary(secondMean, Math.Sqrt(Math.Max(secondVariance, 1.0))),
                    new NumericSummary(thirdMean, Math.Sqrt(Math.Max(thirdVariance, 1.0))),
                    new NumericSummary(lowMean, Math.Sqrt(Math.Max(lowVariance, 0.75))));
            }

            public double Distance(PatternFeatures features)
            {
                return
                    (FavoriteCount.NormalizedDistance(features.FavoriteCount) * 0.90) +
                    (SecondChoiceCount.NormalizedDistance(features.SecondChoiceCount) * 0.75) +
                    (ThirdChoiceCount.NormalizedDistance(features.ThirdChoiceCount) * 1.10) +
                    (LowProbabilityCount.NormalizedDistance(features.LowProbabilityCount) * 1.20);
            }

            private static void AddBernoulli(
                double probability,
                ref double mean,
                ref double variance)
            {
                var p = Math.Clamp(probability, 0.0, 1.0);
                mean += p;
                variance += p * (1.0 - p);
            }
        }

        private readonly record struct NumericSummary(double Mean, double StandardDeviation)
        {
            public static NumericSummary From(IEnumerable<double> values)
            {
                var list = values.ToList();
                if (list.Count == 0)
                {
                    return new NumericSummary(0.0, 1.0);
                }

                var mean = list.Average();
                var variance = list
                    .Select(x => Math.Pow(x - mean, 2))
                    .DefaultIfEmpty(1.0)
                    .Average();

                return new NumericSummary(
                    mean,
                    Math.Max(Math.Sqrt(variance), 0.75));
            }

            public double NormalizedDistance(double value)
            {
                var z = Math.Abs(value - Mean) / Math.Max(StandardDeviation, 0.75);
                return Math.Min(z * z, 9.0);
            }
        }

        private sealed record RankedSymbol(char Symbol, double Probability);
    }
}
