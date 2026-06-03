using Microsoft.Data.SqlClient;
using SporTotoFormApp.Services;

namespace SporTotoFormApp.Data
{
    public sealed class PredictionInsightRepository
    {
        public PredictionInsight Build(
            CurrentRoundInfo currentRound,
            IReadOnlyDictionary<int, NesineMatchPopularity>? nesinePopularityByMatchNo = null,
            IReadOnlyDictionary<int, NesineHeadToHeadSummary>? nesineHeadToHeadByMatchNo = null,
            IReadOnlyDictionary<int, MatchModelFeature>? matchModelFeaturesByMatchNo = null)
        {
            var probabilities = new List<SymbolProbabilities>(currentRound.Matches.Count);
            var rows = new List<MatchInsight>(currentRound.Matches.Count);

            using var connection = Database.CreateConnection();
            connection.Open();

            foreach (var match in currentRound.Matches.OrderBy(x => x.MatchOrder))
            {
                var nesinePopularity = nesinePopularityByMatchNo != null &&
                    nesinePopularityByMatchNo.TryGetValue(match.MatchOrder, out var foundPopularity)
                        ? foundPopularity
                        : null;
                var headToHead = nesineHeadToHeadByMatchNo != null &&
                    nesineHeadToHeadByMatchNo.TryGetValue(match.MatchOrder, out var foundHeadToHead)
                        ? foundHeadToHead
                        : null;
                var feature = matchModelFeaturesByMatchNo != null &&
                    matchModelFeaturesByMatchNo.TryGetValue(match.MatchOrder, out var foundFeature)
                        ? foundFeature
                        : null;

                var stats = LoadMatchStats(connection, match.HomeTeamName, match.AwayTeamName, nesinePopularity, headToHead, feature);
                probabilities.Add(stats.Probabilities);
                rows.Add(new MatchInsight(
                    match.MatchOrder,
                    stats.Probabilities,
                    stats.SampleSize,
                    stats.Count1,
                    stats.CountX,
                    stats.Count2,
                    stats.Components,
                    stats.Details));
            }

            var payout = LoadPayoutInsight(connection);
            return new PredictionInsight(probabilities, rows, payout);
        }

        private static MatchStats LoadMatchStats(
            SqlConnection connection,
            string homeTeamName,
            string awayTeamName,
            NesineMatchPopularity? nesinePopularity,
            NesineHeadToHeadSummary? headToHead,
            MatchModelFeature? feature)
        {
            var count1 = 2.5;
            var countX = 1.5;
            var count2 = 2.0;
            var components = new List<MatchInsightComponent>();

            AddWeightedCounts(
                LoadDistribution(
                    connection,
                    """
                    SELECT ResultSymbol, COUNT(1)
                    FROM HistoricalResultMatches
                    WHERE HomeTeamName = @HomeTeamName
                      AND ResultSymbol IN ('1', 'X', '2')
                    GROUP BY ResultSymbol;
                    """,
                    homeTeamName,
                    awayTeamName),
                1.00,
                "Ev sahibi ic saha gecmisi",
                components,
                ref count1,
                ref countX,
                ref count2);

            AddWeightedCounts(
                LoadDistribution(
                    connection,
                    """
                    SELECT ResultSymbol, COUNT(1)
                    FROM HistoricalResultMatches
                    WHERE AwayTeamName = @AwayTeamName
                      AND ResultSymbol IN ('1', 'X', '2')
                    GROUP BY ResultSymbol;
                    """,
                    homeTeamName,
                    awayTeamName),
                1.00,
                "Deplasman takimi deplasman gecmisi",
                components,
                ref count1,
                ref countX,
                ref count2);

            AddWeightedCounts(
                LoadDistribution(
                    connection,
                    """
                    SELECT ResultSymbol, COUNT(1)
                    FROM HistoricalResultMatches
                    WHERE HomeTeamName = @HomeTeamName
                      AND AwayTeamName = @AwayTeamName
                      AND ResultSymbol IN ('1', 'X', '2')
                    GROUP BY ResultSymbol;
                    """,
                    homeTeamName,
                    awayTeamName),
                1.75,
                "Ayni eslesme gecmisi",
                components,
                ref count1,
                ref countX,
                ref count2);

            AddWeightedCounts(
                LoadRecentDistribution(connection, homeTeamName, awayTeamName),
                1.35,
                "Yakin form",
                components,
                ref count1,
                ref countX,
                ref count2);

            if (nesinePopularity != null)
            {
                AddWeightedCounts(
                    new DistributionCounts(
                        nesinePopularity.Percentage1,
                        nesinePopularity.PercentageX,
                        nesinePopularity.Percentage2),
                    1.80,
                    "Nesine oynanma orani",
                    components,
                    ref count1,
                    ref countX,
                    ref count2);
            }

            if (headToHead != null)
            {
                AddWeightedCounts(
                    new DistributionCounts(
                        headToHead.H2HHomeWinCount,
                        headToHead.H2HDrawCount,
                        headToHead.H2HAwayWinCount),
                    0.85,
                    "Nesine H2H ozeti",
                    components,
                    ref count1,
                    ref countX,
                    ref count2);

                var oddsCounts = ConvertOddsToCounts(headToHead.HomeOdd, headToHead.DrawOdd, headToHead.AwayOdd);
                if (oddsCounts.SampleSize > 0)
                {
                    AddWeightedCounts(
                        oddsCounts,
                        1.15,
                        "Nesine oran olasiligi",
                        components,
                        ref count1,
                        ref countX,
                        ref count2);
                }
            }

            if (feature != null)
            {
                AddWeightedCounts(
                    ConvertFeatureSignalToCounts(feature.FeatureSignal),
                    0.95,
                    "Nesine feature modeli",
                    components,
                    ref count1,
                    ref countX,
                    ref count2);
            }

            var details = LoadMatchDetails(connection, homeTeamName, awayTeamName);
            var rawCount1 = components.Sum(x => x.Count1);
            var rawCountX = components.Sum(x => x.CountX);
            var rawCount2 = components.Sum(x => x.Count2);
            var sampleSize = components.Sum(x => x.SampleSize);

            return new MatchStats(
                SymbolProbabilities.FromCounts(count1, countX, count2),
                sampleSize,
                rawCount1,
                rawCountX,
                rawCount2,
                components,
                details);
        }

        private static DistributionCounts LoadDistribution(
            SqlConnection connection,
            string sql,
            string homeTeamName,
            string awayTeamName)
        {
            using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@HomeTeamName", homeTeamName);
            command.Parameters.AddWithValue("@AwayTeamName", awayTeamName);

            var counts = new DistributionCounts();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                counts.Add(reader.GetString(0), reader.GetInt32(1));
            }

            return counts;
        }

        private static DistributionCounts LoadRecentDistribution(
            SqlConnection connection,
            string homeTeamName,
            string awayTeamName)
        {
            using var command = new SqlCommand(
                """
                SELECT ResultSymbol
                FROM
                (
                    SELECT TOP (18) hr.RoundId, m.ResultSymbol
                    FROM HistoricalResultMatches m
                    INNER JOIN HistoricalResults hr ON hr.Id = m.HistoricalResultId
                    WHERE (m.HomeTeamName = @HomeTeamName OR m.AwayTeamName = @HomeTeamName)
                      AND m.ResultSymbol IN ('1', 'X', '2')
                    ORDER BY hr.RoundId DESC
                ) h
                UNION ALL
                SELECT ResultSymbol
                FROM
                (
                    SELECT TOP (18) hr.RoundId, m.ResultSymbol
                    FROM HistoricalResultMatches m
                    INNER JOIN HistoricalResults hr ON hr.Id = m.HistoricalResultId
                    WHERE (m.HomeTeamName = @AwayTeamName OR m.AwayTeamName = @AwayTeamName)
                      AND m.ResultSymbol IN ('1', 'X', '2')
                    ORDER BY hr.RoundId DESC
                ) a;
                """,
                connection);

            command.Parameters.AddWithValue("@HomeTeamName", homeTeamName);
            command.Parameters.AddWithValue("@AwayTeamName", awayTeamName);

            var counts = new DistributionCounts();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                counts.Add(reader.GetString(0), 1);
            }

            return counts;
        }

        private static void AddWeightedCounts(
            DistributionCounts counts,
            double weight,
            string label,
            List<MatchInsightComponent> components,
            ref double count1,
            ref double countX,
            ref double count2)
        {
            if (counts.SampleSize == 0)
            {
                return;
            }

            count1 += counts.Count1 * weight;
            countX += counts.CountX * weight;
            count2 += counts.Count2 * weight;
            components.Add(new MatchInsightComponent(
                label,
                counts.SampleSize,
                counts.Count1,
                counts.CountX,
                counts.Count2,
                weight));
        }

        private static DistributionCounts ConvertOddsToCounts(decimal? homeOdd, decimal? drawOdd, decimal? awayOdd)
        {
            if (homeOdd is null or <= 1 || drawOdd is null or <= 1 || awayOdd is null or <= 1)
            {
                return new DistributionCounts();
            }

            var p1 = 1.0 / (double)homeOdd.Value;
            var px = 1.0 / (double)drawOdd.Value;
            var p2 = 1.0 / (double)awayOdd.Value;
            var sum = p1 + px + p2;
            if (sum <= 0)
            {
                return new DistributionCounts();
            }

            const int scale = 100;
            return new DistributionCounts(
                (int)Math.Round(p1 / sum * scale),
                (int)Math.Round(px / sum * scale),
                (int)Math.Round(p2 / sum * scale));
        }

        private static DistributionCounts ConvertFeatureSignalToCounts(double signal)
        {
            var clamped = Math.Clamp(signal, -35.0, 35.0);
            var home = Math.Clamp(35 + clamped, 5, 75);
            var away = Math.Clamp(35 - clamped, 5, 75);
            var draw = Math.Clamp(30 - Math.Abs(clamped) * 0.25, 12, 45);

            return new DistributionCounts(
                (int)Math.Round(home),
                (int)Math.Round(draw),
                (int)Math.Round(away));
        }

        private static IReadOnlyList<MatchInsightDetail> LoadMatchDetails(
            SqlConnection connection,
            string homeTeamName,
            string awayTeamName)
        {
            using var command = new SqlCommand(
                """
                SELECT TOP (80)
                    hr.RoundId,
                    hr.RoundName,
                    m.MatchOrder,
                    m.MatchDate,
                    m.HomeTeamName,
                    m.AwayTeamName,
                    m.HomeScore,
                    m.AwayScore,
                    m.ResultSymbol
                FROM HistoricalResultMatches m
                INNER JOIN HistoricalResults hr ON hr.Id = m.HistoricalResultId
                WHERE (m.HomeTeamName = @HomeTeamName OR m.AwayTeamName = @AwayTeamName)
                ORDER BY hr.RoundId DESC, m.MatchOrder;
                """,
                connection);

            command.Parameters.AddWithValue("@HomeTeamName", homeTeamName);
            command.Parameters.AddWithValue("@AwayTeamName", awayTeamName);

            var result = new List<MatchInsightDetail>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new MatchInsightDetail(
                    reader.IsDBNull(0) ? null : reader.GetInt32(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    reader.GetString(8)));
            }

            return result;
        }

        private static PayoutInsight LoadPayoutInsight(SqlConnection connection)
        {
            using var command = new SqlCommand(
                """
                SELECT WinnerCount
                FROM HistoricalResultPayouts
                WHERE HitCount = 15 AND WinnerCount IS NOT NULL
                ORDER BY RoundId;
                """,
                connection);

            var values = new List<int>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                values.Add(reader.GetInt32(0));
            }

            if (values.Count == 0)
            {
                return new PayoutInsight(25, 75, 0, "DB ikramiye verisi henuz yeterli degil.");
            }

            values.Sort();
            var q25 = Percentile(values, 0.25);
            var q75 = Percentile(values, 0.75);
            var min = Math.Clamp((int)Math.Floor(q25), 1, 20);
            var max = Math.Clamp((int)Math.Ceiling(q75), min, 20);
            var avg = values.Average();

            return new PayoutInsight(
                min,
                max,
                values.Count,
                $"DB ikramiye modeli: 15 bilen kisi hedef araligi {min}-{max} (1-20 bandi, n={values.Count}, ort={avg:F1}).");
        }

        private static double Percentile(IReadOnlyList<int> sortedValues, double percentile)
        {
            if (sortedValues.Count == 1)
            {
                return sortedValues[0];
            }

            var position = (sortedValues.Count - 1) * percentile;
            var lower = (int)Math.Floor(position);
            var upper = (int)Math.Ceiling(position);
            if (lower == upper)
            {
                return sortedValues[lower];
            }

            var fraction = position - lower;
            return sortedValues[lower] + (sortedValues[upper] - sortedValues[lower]) * fraction;
        }

        private sealed record MatchStats(
            SymbolProbabilities Probabilities,
            int SampleSize,
            int Count1,
            int CountX,
            int Count2,
            IReadOnlyList<MatchInsightComponent> Components,
            IReadOnlyList<MatchInsightDetail> Details);

        private sealed class DistributionCounts
        {
            public DistributionCounts()
            {
            }

            public DistributionCounts(int count1, int countX, int count2)
            {
                Count1 = count1;
                CountX = countX;
                Count2 = count2;
            }

            public int Count1 { get; private set; }
            public int CountX { get; private set; }
            public int Count2 { get; private set; }
            public int SampleSize => Count1 + CountX + Count2;

            public void Add(string symbol, int count)
            {
                switch (symbol)
                {
                    case "1": Count1 += count; break;
                    case "X": CountX += count; break;
                    case "2": Count2 += count; break;
                }
            }
        }
    }

    public sealed record PredictionInsight(
        IReadOnlyList<SymbolProbabilities> MatchProbabilities,
        IReadOnlyList<MatchInsight> MatchInsights,
        PayoutInsight Payout);

    public sealed record MatchInsight(
        int MatchOrder,
        SymbolProbabilities Probabilities,
        int SampleSize,
        int Count1,
        int CountX,
        int Count2,
        IReadOnlyList<MatchInsightComponent> Components,
        IReadOnlyList<MatchInsightDetail> Details);

    public sealed record MatchInsightComponent(
        string Name,
        int SampleSize,
        int Count1,
        int CountX,
        int Count2,
        double Weight);

    public sealed record MatchInsightDetail(
        int? RoundId,
        string? RoundName,
        int MatchOrder,
        DateTime? MatchDate,
        string? HomeTeamName,
        string? AwayTeamName,
        int? HomeScore,
        int? AwayScore,
        string ResultSymbol);

    public sealed record PayoutInsight(int RecommendedI15Min, int RecommendedI15Max, int SampleSize, string Message);
}
