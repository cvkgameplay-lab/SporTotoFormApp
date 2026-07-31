using Microsoft.Data.SqlClient;
using SporTotoFormApp.Object;
using SporTotoFormApp.Services;
using System.Globalization;

namespace SporTotoFormApp.Data
{
    public sealed class PredictionRepository
    {
        private const int MaxPlayableCouponCount = 100;

        public async Task<int> SaveRunAsync(
            IReadOnlyList<Coupon> coupons,
            int totalRequested,
            string? notes = null,
            IReadOnlyDictionary<string, string>? profileNamesByPrediction = null,
            PredictionRunContext? context = null,
            IReadOnlyList<PredictionRunMatchMatrixRow>? matchMatrix = null,
            CancellationToken cancellationToken = default)
        {
            await EnsureSchemaAsync(cancellationToken);

            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                var runId = await InsertRunAsync(connection, transaction, totalRequested, coupons.Count, notes, cancellationToken);
                if (context != null)
                {
                    await InsertRunModelInfoAsync(connection, transaction, runId, context, cancellationToken);
                }

                foreach (var coupon in coupons)
                {
                    var prediction = NormalizePrediction(coupon.prediction);
                    if (prediction.Length != 15)
                    {
                        continue;
                    }

                    var profileName = profileNamesByPrediction != null &&
                        profileNamesByPrediction.TryGetValue(prediction, out var foundProfileName)
                            ? foundProfileName
                            : null;

                    await InsertPredictionAsync(connection, transaction, runId, coupon, prediction, profileName, cancellationToken);
                }

                if (matchMatrix != null)
                {
                    foreach (var row in matchMatrix)
                    {
                        await InsertMatchMatrixAsync(connection, transaction, runId, row, cancellationToken);
                    }
                }

                await TryInsertRunResultAsync(connection, transaction, runId, context?.RoundId, coupons, cancellationToken);

                await transaction.CommitAsync(cancellationToken);
                return runId;
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }

        public async Task<IReadOnlyList<PredictionRunEvaluationSummary>> EvaluatePendingRunsAsync(
            int batchSize = 200,
            int maxScannedRuns = 5000,
            Action<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            await EnsureSchemaAsync(cancellationToken);

            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var result = new List<PredictionRunEvaluationSummary>();
            var afterRunId = 0;
            var scannedRunCount = 0;
            var safeBatchSize = Math.Clamp(batchSize, 25, 500);
            var safeMaxScannedRuns = Math.Max(maxScannedRuns, safeBatchSize);

            while (!cancellationToken.IsCancellationRequested &&
                   scannedRunCount < safeMaxScannedRuns)
            {
                var pendingRuns = await LoadPendingRunsBatchAsync(
                    connection,
                    afterRunId,
                    Math.Min(safeBatchSize, safeMaxScannedRuns - scannedRunCount),
                    cancellationToken);
                if (pendingRuns.Count == 0)
                {
                    break;
                }

                afterRunId = pendingRuns[^1].RunId;
                scannedRunCount += pendingRuns.Count;
                progress?.Invoke($"Run degerlendirme batch | Run:{pendingRuns[0].RunId}-{pendingRuns[^1].RunId} | Aday:{pendingRuns.Count:n0} | Taranan:{scannedRunCount:n0}");

                foreach (var pending in pendingRuns)
                {
                    try
                    {
                        var summary = await TryEvaluatePendingRunAsync(
                            connection,
                            pending,
                            cancellationToken);
                        if (summary == null)
                        {
                            continue;
                        }

                        result.Add(summary);
                    }
                    catch (Exception ex) when (IsTimeoutLike(ex) && !cancellationToken.IsCancellationRequested)
                    {
                        progress?.Invoke($"Run {pending.RunId} degerlendirme timeout aldi; atlanip devam ediliyor.");
                    }
                    catch (SqlException ex) when (ex.Number is 2601 or 2627)
                    {
                        progress?.Invoke($"Run {pending.RunId} daha once degerlendirilmis; atlanip devam ediliyor.");
                    }
                }
            }

            if (scannedRunCount >= safeMaxScannedRuns)
            {
                progress?.Invoke($"Run degerlendirme tek seferlik tarama sinirina ulasti: {safeMaxScannedRuns:n0}. Kalanlar icin butona tekrar basinca kaldigi yerden devam eder.");
            }

            progress?.Invoke($"Run degerlendirme ozeti | Taranan:{scannedRunCount:n0} | Degerlendirilen:{result.Count:n0}");

            return result;
        }

        private static async Task<IReadOnlyList<PendingPredictionRun>> LoadPendingRunsBatchAsync(
            SqlConnection connection,
            int afterRunId,
            int batchSize,
            CancellationToken cancellationToken)
        {
            var result = new List<PendingPredictionRun>();
            await using var command = new SqlCommand(
                """
                SELECT TOP (@BatchSize)
                    r.Id,
                    info.RoundId,
                    info.RoundName,
                    r.CreatedAt
                FROM dbo.PredictionRuns r
                LEFT JOIN dbo.PredictionRunModelInfo info ON info.RunId = r.Id
                WHERE r.Id > @AfterRunId
                  AND EXISTS
                  (
                      SELECT 1
                      FROM dbo.Predictions p
                      WHERE p.RunId = r.Id
                        AND p.PredictionLine IS NOT NULL
                        AND LEN(p.PredictionLine) = 15
                  )
                  AND NOT EXISTS
                  (
                      SELECT 1
                      FROM dbo.PredictionRunResults rr
                      WHERE rr.RunId = r.Id
                  )
                ORDER BY r.Id;
                """,
                connection);
            command.CommandTimeout = 45;
            command.Parameters.AddWithValue("@AfterRunId", afterRunId);
            command.Parameters.AddWithValue("@BatchSize", Math.Max(batchSize, 1));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new PendingPredictionRun(
                    reader.GetInt32(0),
                    reader.IsDBNull(1) ? null : reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetDateTime(3)));
            }

            return result;
        }

        private static async Task<PredictionRunEvaluationSummary?> TryEvaluatePendingRunAsync(
            SqlConnection connection,
            PendingPredictionRun pending,
            CancellationToken cancellationToken)
        {
            var actual = await TryResolveActualResultAsync(connection, pending, cancellationToken);
            if (actual == null)
            {
                return null;
            }

            var predictions = await LoadPredictionLinesAsync(connection, pending.RunId, cancellationToken);
            if (predictions.Count == 0)
            {
                return null;
            }

            var hits = predictions
                .Select(x => CountHits(NormalizePrediction(x), actual.ActualResultLine))
                .ToList();

            if (hits.Count == 0)
            {
                return null;
            }

            var summary = new PredictionRunEvaluationSummary(
                pending.RunId,
                actual.RoundId,
                actual.ActualResultLine,
                hits.Max(),
                hits.Average(),
                hits.Count(x => x == 15),
                hits.Count(x => x == 14),
                hits.Count(x => x == 13),
                hits.Count(x => x == 12));

            await InsertRunResultAsync(connection, null, summary, cancellationToken);
            return summary;
        }

        public async Task<int> GetExperimentRunCountAsync(
            int roundId,
            CancellationToken cancellationToken = default)
        {
            await EnsureSchemaAsync(cancellationToken);

            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(
                """
                SELECT COUNT(1)
                FROM dbo.PredictionRuns r
                INNER JOIN dbo.PredictionRunModelInfo info ON info.RunId = r.Id
                WHERE info.RoundId = @RoundId
                  AND r.Notes LIKE 'Experiment run #%'
                  AND r.Notes LIKE '%UcuncuEsik:%'
                  AND r.Notes LIKE '%Yumusatma:%';
                """,
                connection);
            command.CommandTimeout = 15;
            command.Parameters.AddWithValue("@RoundId", roundId);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt32(result);
        }

        public async Task<PredictionRunExperimentConfiguration?> GetExperimentConfigurationAsync(
            int runId,
            CancellationToken cancellationToken = default)
        {
            await EnsureSchemaAsync(cancellationToken);

            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(
                """
                SELECT
                    r.TotalRequested,
                    info.ThirdChoiceMinRatio,
                    info.ProbabilityUniformBlend,
                    info.PatternScoreWeight,
                    info.WinnerPatternWeight,
                    info.RecentPatternWeight,
                    info.PreviousWeekPatternWeight,
                    info.SurpriseBalanceWeight,
                    info.MinHammingDistance,
                    info.MinHammingDistanceFinal,
                    info.MonteCarloScenarioCount
                FROM dbo.PredictionRuns r
                INNER JOIN dbo.PredictionRunModelInfo info ON info.RunId = r.Id
                WHERE r.Id = @RunId;
                """,
                connection);
            command.CommandTimeout = 20;
            command.Parameters.AddWithValue("@RunId", runId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            return new PredictionRunExperimentConfiguration(
                reader.GetInt32(0),
                reader.GetDouble(1),
                reader.GetDouble(2),
                reader.GetDouble(3),
                reader.GetDouble(4),
                reader.GetDouble(5),
                reader.GetDouble(6),
                reader.GetDouble(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                reader.GetInt32(10));
        }

        public async Task<IReadOnlyList<PredictionParameterAuditRun>> LoadParameterAuditRunsAsync(
            CancellationToken cancellationToken = default)
        {
            await EnsureSchemaAsync(cancellationToken);

            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(
                """
                SELECT
                    r.Id,
                    r.TotalRequested,
                    r.Notes,
                    r.CreatedAt,
                    rr.RoundId,
                    rr.ActualResultLine,
                    rr.BestHitCount,
                    rr.AverageHitCount,
                    rr.Hit15Count,
                    rr.Hit14Count,
                    rr.Hit13Count,
                    rr.Hit12Count,
                    COALESCE(info.I15Min, 0),
                    COALESCE(info.I15Max, 0),
                    COALESCE(info.InitialTopCandidateLimit, 0),
                    COALESCE(info.DiversePrePoolLimit, 0),
                    COALESCE(info.ApiBudgetMultiplier, 0),
                    COALESCE(info.ApiConcurrency, 0),
                    COALESCE(info.MinHammingDistance, 0),
                    COALESCE(info.MinHammingDistanceFinal, 0),
                    COALESCE(info.MonteCarloScenarioCount, 0),
                    COALESCE(info.ThirdChoiceMinRatio, 1.01),
                    COALESCE(info.ProbabilityUniformBlend, 0),
                    COALESCE(info.PatternScoreWeight, 0),
                    COALESCE(info.WinnerPatternWeight, 0),
                    COALESCE(info.RecentPatternWeight, 0),
                    COALESCE(info.PreviousWeekPatternWeight, 0),
                    COALESCE(info.SurpriseBalanceWeight, 0),
                    CAST(COALESCE(info.UsedNesinePopularity, 0) AS bit),
                    CAST(COALESCE(info.UsedHeadToHead, 0) AS bit),
                    CAST(COALESCE(info.UsedFeatureModel, 0) AS bit),
                    CAST(COALESCE(info.UsedTeamEnsemble, 0) AS bit),
                    COALESCE(info.EnsembleCalibrationSampleCount, 0),
                    COALESCE(info.EnsembleEloWeight, 0),
                    COALESCE(info.EnsembleDixonColesWeight, 0),
                    COALESCE(info.EnsembleMatchCount, 0)
                FROM dbo.PredictionRunResults rr
                INNER JOIN dbo.PredictionRuns r ON r.Id = rr.RunId
                LEFT JOIN dbo.PredictionRunModelInfo info ON info.RunId = r.Id
                ORDER BY rr.RoundId DESC, rr.BestHitCount DESC, rr.Hit15Count DESC, r.Id DESC;
                """,
                connection);

            var rows = new List<PredictionParameterAuditRun>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new PredictionParameterAuditRun(
                    reader.GetInt32(0),
                    reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetDateTime(3),
                    reader.GetInt32(4),
                    reader.GetString(5),
                    reader.GetInt32(6),
                    reader.GetDouble(7),
                    reader.GetInt32(8),
                    reader.GetInt32(9),
                    reader.GetInt32(10),
                    reader.GetInt32(11),
                    reader.GetInt32(12),
                    reader.GetInt32(13),
                    reader.GetInt32(14),
                    reader.GetInt32(15),
                    reader.GetInt32(16),
                    reader.GetInt32(17),
                    reader.GetInt32(18),
                    reader.GetInt32(19),
                    reader.GetInt32(20),
                    reader.GetDouble(21),
                    reader.GetDouble(22),
                    reader.GetDouble(23),
                    reader.GetDouble(24),
                    reader.GetDouble(25),
                    reader.GetDouble(26),
                    reader.GetDouble(27),
                    reader.GetBoolean(28),
                    reader.GetBoolean(29),
                    reader.GetBoolean(30),
                    reader.GetBoolean(31),
                    reader.GetInt32(32),
                    reader.GetDouble(33),
                    reader.GetDouble(34),
                    reader.GetInt32(35)));
            }

            return rows;
        }

        public async Task<IReadOnlyList<CounterfactualBacktestTarget>> LoadCounterfactualBacktestTargetsAsync(
            int maxRounds,
            int? roundId = null,
            CancellationToken cancellationToken = default)
        {
            await EnsureSchemaAsync(cancellationToken);

            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await EnsureLearnedStrategySchemaAsync(connection, cancellationToken);

            await using var command = new SqlCommand(
                """
                WITH CandidateRuns AS
                (
                    SELECT
                        rr.RoundId,
                        rr.ActualResultLine,
                        r.Id AS RunId,
                        r.CreatedAt,
                        COUNT(m.Id) AS MatrixRows
                    FROM dbo.PredictionRunResults rr
                    INNER JOIN dbo.PredictionRuns r ON r.Id = rr.RunId
                    INNER JOIN dbo.PredictionRunMatchMatrix m ON m.RunId = r.Id
                    WHERE LEN(rr.ActualResultLine) = 15
                      AND m.P1 IS NOT NULL
                      AND m.PX IS NOT NULL
                      AND m.P2 IS NOT NULL
                    GROUP BY rr.RoundId, rr.ActualResultLine, r.Id, r.CreatedAt
                    HAVING COUNT(m.Id) = 15
                ),
                RankedRuns AS
                (
                    SELECT *,
                        ROW_NUMBER() OVER
                        (
                            PARTITION BY RoundId
                            ORDER BY CreatedAt DESC, RunId DESC
                        ) AS RowNo
                    FROM CandidateRuns
                )
                SELECT TOP (@MaxRounds)
                    RoundId,
                    ActualResultLine,
                    RunId
                FROM RankedRuns
                WHERE RowNo = 1
                  AND (@RoundId IS NULL OR RoundId = @RoundId)
                ORDER BY RoundId DESC;
                """,
                connection);
            command.Parameters.AddWithValue("@MaxRounds", Math.Max(maxRounds, 1));
            command.Parameters.AddWithValue("@RoundId", (object?)roundId ?? DBNull.Value);

            var headers = new List<(int RoundId, string ActualResultLine, int RunId)>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    headers.Add((
                        reader.GetInt32(0),
                        reader.GetString(1),
                        reader.GetInt32(2)));
                }
            }

            var result = new List<CounterfactualBacktestTarget>();
            foreach (var header in headers)
            {
                var probabilities = await LoadRunMatrixProbabilitiesAsync(
                    connection,
                    header.RunId,
                    cancellationToken);
                if (probabilities.Count == 15)
                {
                    result.Add(new CounterfactualBacktestTarget(
                        header.RoundId,
                        header.ActualResultLine,
                        header.RunId,
                        probabilities));
                }
            }

            return result;
        }

        public async Task<IReadOnlyList<CounterfactualBacktestRoundChoice>> LoadAvailableCounterfactualBacktestRoundsAsync(
            int limit = 100,
            CancellationToken cancellationToken = default)
        {
            await EnsureSchemaAsync(cancellationToken);

            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await EnsureLearnedStrategySchemaAsync(connection, cancellationToken);

            await using var command = new SqlCommand(
                """
                WITH CandidateRuns AS
                (
                    SELECT
                        rr.RoundId,
                        rr.ActualResultLine,
                        r.Id AS RunId,
                        r.CreatedAt,
                        COALESCE(MAX(info.RoundName), MAX(hr.RoundName)) AS RoundName,
                        COUNT(m.Id) AS MatrixRows
                    FROM dbo.PredictionRunResults rr
                    INNER JOIN dbo.PredictionRuns r ON r.Id = rr.RunId
                    INNER JOIN dbo.PredictionRunMatchMatrix m ON m.RunId = r.Id
                    LEFT JOIN dbo.PredictionRunModelInfo info ON info.RunId = r.Id
                    OUTER APPLY
                    (
                        SELECT TOP (1) h.RoundName
                        FROM dbo.HistoricalResults h
                        WHERE h.RoundId = rr.RoundId
                        ORDER BY h.Id DESC
                    ) hr
                    WHERE LEN(rr.ActualResultLine) = 15
                      AND m.P1 IS NOT NULL
                      AND m.PX IS NOT NULL
                      AND m.P2 IS NOT NULL
                    GROUP BY rr.RoundId, rr.ActualResultLine, r.Id, r.CreatedAt
                    HAVING COUNT(m.Id) = 15
                ),
                RankedRuns AS
                (
                    SELECT *,
                        ROW_NUMBER() OVER
                        (
                            PARTITION BY RoundId
                            ORDER BY CreatedAt DESC, RunId DESC
                        ) AS RowNo
                    FROM CandidateRuns
                )
                SELECT TOP (@Limit)
                    RoundId,
                    ActualResultLine,
                    RunId,
                    RoundName,
                    CreatedAt
                FROM RankedRuns
                WHERE RowNo = 1
                ORDER BY RoundId DESC;
                """,
                connection);
            command.Parameters.AddWithValue("@Limit", Math.Max(limit, 1));

            var result = new List<CounterfactualBacktestRoundChoice>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new CounterfactualBacktestRoundChoice(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetDateTime(4)));
            }

            return result;
        }

        public async Task<IReadOnlyList<CounterfactualTriedOptionKey>> LoadCounterfactualTriedOptionKeysAsync(
            int roundId,
            CancellationToken cancellationToken = default)
        {
            await EnsureSchemaAsync(cancellationToken);

            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await EnsureLearnedStrategySchemaAsync(connection, cancellationToken);
            await EnsureCounterfactualAttemptSchemaAsync(connection, cancellationToken);

            await using var command = new SqlCommand(
                """
                SELECT DISTINCT CouponCount, ThirdChoiceMinRatio, ProbabilityUniformBlend, PatternScoreWeight
                FROM
                (
                    SELECT
                        CouponCount,
                        CAST(ThirdChoiceMinRatio AS DECIMAL(10,4)) AS ThirdChoiceMinRatio,
                        CAST(ProbabilityUniformBlend AS DECIMAL(10,4)) AS ProbabilityUniformBlend,
                        CAST(PatternScoreWeight AS DECIMAL(10,4)) AS PatternScoreWeight
                    FROM dbo.LearnedPredictionStrategies
                    WHERE SourceRoundId = @RoundId

                    UNION

                    SELECT
                        CouponCount,
                        ThirdChoiceMinRatio,
                        ProbabilityUniformBlend,
                        PatternScoreWeight
                    FROM dbo.CounterfactualParameterAttempts
                    WHERE SourceRoundId = @RoundId
                ) tried;
                """,
                connection);
            command.Parameters.AddWithValue("@RoundId", roundId);

            var result = new List<CounterfactualTriedOptionKey>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new CounterfactualTriedOptionKey(
                    reader.GetInt32(0),
                    Convert.ToDouble(reader.GetDecimal(1)),
                    Convert.ToDouble(reader.GetDecimal(2)),
                    Convert.ToDouble(reader.GetDecimal(3))));
            }

            return result;
        }

        public async Task<RoundPayoutProfile?> LoadRoundPayoutProfileAsync(
            int roundId,
            CancellationToken cancellationToken = default)
        {
            await EnsureSchemaAsync(cancellationToken);

            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            await using var command = new SqlCommand(
                """
                SELECT
                    COALESCE(MAX(CASE WHEN HitCount = 15 THEN PrizeAmount END), 0),
                    COALESCE(MAX(CASE WHEN HitCount = 14 THEN PrizeAmount END), 0),
                    COALESCE(MAX(CASE WHEN HitCount = 13 THEN PrizeAmount END), 0),
                    COALESCE(MAX(CASE WHEN HitCount = 12 THEN PrizeAmount END), 0)
                FROM dbo.HistoricalResultPayouts
                WHERE RoundId = @RoundId;
                """,
                connection);
            command.Parameters.AddWithValue("@RoundId", roundId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var profile = new RoundPayoutProfile(
                roundId,
                reader.GetDecimal(0),
                reader.GetDecimal(1),
                reader.GetDecimal(2),
                reader.GetDecimal(3));

            return profile.Prize12 <= 0m &&
                   profile.Prize13 <= 0m &&
                   profile.Prize14 <= 0m &&
                   profile.Prize15 <= 0m
                ? null
                : profile;
        }

        public async Task<IReadOnlyList<CounterfactualSearchSeed>> LoadCounterfactualSearchSeedsAsync(
            int roundId,
            int limit = 30,
            CancellationToken cancellationToken = default)
        {
            await EnsureSchemaAsync(cancellationToken);

            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await EnsureLearnedStrategySchemaAsync(connection, cancellationToken);
            await EnsureCounterfactualAttemptSchemaAsync(connection, cancellationToken);

            await using var command = new SqlCommand(
                """
                WITH Rows AS
                (
                    SELECT
                        SourceRoundId,
                        FoundExact,
                        CouponCount,
                        CAST(ThirdChoiceMinRatio AS FLOAT) AS ThirdChoiceMinRatio,
                        CAST(ProbabilityUniformBlend AS FLOAT) AS ProbabilityUniformBlend,
                        CAST(PatternScoreWeight AS FLOAT) AS PatternScoreWeight,
                        BestHitCount,
                        Hit14Count,
                        NetProfitAmount,
                        Roi,
                        CreatedAt
                    FROM dbo.CounterfactualParameterAttempts
                    WHERE CouponCount <= @MaxCouponCount
                      AND
                      (
                          SourceRoundId = @RoundId
                          OR FoundExact = 1
                          OR BestHitCount >= 14
                          OR NetProfitAmount > 0
                      )

                    UNION ALL

                    SELECT
                        SourceRoundId,
                        FoundExact,
                        CouponCount,
                        ThirdChoiceMinRatio,
                        ProbabilityUniformBlend,
                        PatternScoreWeight,
                        BestHitCount,
                        Hit14Count,
                        NetProfitAmount,
                        Roi,
                        CreatedAt
                    FROM dbo.LearnedPredictionStrategies
                    WHERE CouponCount <= @MaxCouponCount
                      AND
                      (
                          SourceRoundId = @RoundId
                          OR FoundExact = 1
                          OR BestHitCount >= 14
                          OR NetProfitAmount > 0
                      )
                ),
                Ranked AS
                (
                    SELECT *,
                        ROW_NUMBER() OVER
                        (
                            PARTITION BY CouponCount, ThirdChoiceMinRatio, ProbabilityUniformBlend, PatternScoreWeight
                            ORDER BY
                                CASE
                                    WHEN SourceRoundId = @RoundId AND FoundExact = 1 THEN 0
                                    WHEN FoundExact = 1 THEN 1
                                    WHEN SourceRoundId = @RoundId THEN 2
                                    WHEN BestHitCount >= 14 THEN 3
                                    ELSE 4
                                END,
                                FoundExact DESC,
                                BestHitCount DESC,
                                Hit14Count DESC,
                                NetProfitAmount DESC,
                                Roi DESC,
                                CreatedAt DESC
                        ) AS RowNo
                    FROM Rows
                )
                SELECT TOP (@Limit)
                    SourceRoundId,
                    FoundExact,
                    CouponCount,
                    ThirdChoiceMinRatio,
                    ProbabilityUniformBlend,
                    PatternScoreWeight,
                    BestHitCount,
                    Hit14Count,
                    NetProfitAmount,
                    Roi
                FROM Ranked
                WHERE RowNo = 1
                ORDER BY
                    CASE
                        WHEN SourceRoundId = @RoundId AND FoundExact = 1 THEN 0
                        WHEN FoundExact = 1 THEN 1
                        WHEN SourceRoundId = @RoundId THEN 2
                        WHEN BestHitCount >= 14 THEN 3
                        ELSE 4
                    END,
                    FoundExact DESC,
                    CASE WHEN BestHitCount >= 14 THEN 0 ELSE 1 END,
                    BestHitCount DESC,
                    Hit14Count DESC,
                    NetProfitAmount DESC,
                    Roi DESC;
                """,
                connection);
            command.Parameters.AddWithValue("@RoundId", roundId);
            command.Parameters.AddWithValue("@Limit", Math.Max(limit, 1));
            command.Parameters.AddWithValue("@MaxCouponCount", MaxPlayableCouponCount);

            var result = new List<CounterfactualSearchSeed>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new CounterfactualSearchSeed(
                    reader.GetInt32(0),
                    reader.GetBoolean(1),
                    reader.GetInt32(2),
                    reader.GetDouble(3),
                    reader.GetDouble(4),
                    reader.GetDouble(5),
                    reader.GetInt32(6),
                    reader.GetInt32(7),
                    reader.GetDecimal(8),
                    reader.GetDouble(9)));
            }

            return result;
        }

        public async Task<IReadOnlyList<CounterfactualParameterAuditRow>> LoadCounterfactualParameterAuditRowsAsync(
            int limit = 5000,
            CancellationToken cancellationToken = default,
            int commandTimeoutSeconds = 120)
        {
            await EnsureSchemaAsync(cancellationToken);

            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await EnsureLearnedStrategySchemaAsync(connection, cancellationToken);
            await EnsureCounterfactualAttemptSchemaAsync(connection, cancellationToken);

            await using var command = new SqlCommand(
                """
                WITH Rows AS
                (
                    SELECT
                        CAST('Learned' AS NVARCHAR(32)) AS SourceName,
                        SourceRoundId,
                        SourceRunId,
                        ActualResultLine,
                        CouponCount,
                        I15Min,
                        I15Max,
                        InitialTopCandidateLimit,
                        DiversePrePoolLimit,
                        ApiBudgetMultiplier,
                        ApiConcurrency,
                        MinHammingDistance,
                        MinHammingDistanceFinal,
                        MonteCarloScenarioCount,
                        ThirdChoiceMinRatio,
                        ProbabilityUniformBlend,
                        PatternScoreWeight,
                        WinnerPatternWeight,
                        RecentPatternWeight,
                        PreviousWeekPatternWeight,
                        SurpriseBalanceWeight,
                        BestHitCount,
                        AverageHitCount,
                        Hit15Count,
                        Hit14Count,
                        Hit13Count,
                        Hit12Count,
                        CostAmount,
                        GrossPrizeAmount,
                        NetProfitAmount,
                        Roi,
                        FoundExact,
                        Notes,
                        CreatedAt
                    FROM dbo.LearnedPredictionStrategies
                    WHERE CouponCount <= @MaxCouponCount
                      AND (FoundExact = 1 OR BestHitCount >= 14 OR NetProfitAmount > 0)

                    UNION ALL

                    SELECT
                        CAST('Attempt' AS NVARCHAR(32)) AS SourceName,
                        SourceRoundId,
                        SourceRunId,
                        ActualResultLine,
                        CouponCount,
                        1 AS I15Min,
                        20 AS I15Max,
                        3200000 AS InitialTopCandidateLimit,
                        750000 AS DiversePrePoolLimit,
                        1000 AS ApiBudgetMultiplier,
                        6 AS ApiConcurrency,
                        3 AS MinHammingDistance,
                        2 AS MinHammingDistanceFinal,
                        50000 AS MonteCarloScenarioCount,
                        CAST(ThirdChoiceMinRatio AS FLOAT) AS ThirdChoiceMinRatio,
                        CAST(ProbabilityUniformBlend AS FLOAT) AS ProbabilityUniformBlend,
                        CAST(PatternScoreWeight AS FLOAT) AS PatternScoreWeight,
                        0.45 AS WinnerPatternWeight,
                        0.20 AS RecentPatternWeight,
                        0.12 AS PreviousWeekPatternWeight,
                        0.30 AS SurpriseBalanceWeight,
                        BestHitCount,
                        AverageHitCount,
                        Hit15Count,
                        Hit14Count,
                        Hit13Count,
                        Hit12Count,
                        CostAmount,
                        GrossPrizeAmount,
                        NetProfitAmount,
                        Roi,
                        FoundExact,
                        AttemptMode AS Notes,
                        CreatedAt
                    FROM dbo.CounterfactualParameterAttempts
                    WHERE CouponCount <= @MaxCouponCount
                      AND (FoundExact = 1 OR BestHitCount >= 14 OR NetProfitAmount > 0)
                ),
                Ranked AS
                (
                    SELECT *,
                        ROW_NUMBER() OVER
                        (
                            PARTITION BY SourceRoundId, CouponCount, ThirdChoiceMinRatio, ProbabilityUniformBlend, PatternScoreWeight
                            ORDER BY FoundExact DESC, Hit15Count DESC, BestHitCount DESC, NetProfitAmount DESC, Roi DESC, CreatedAt DESC
                        ) AS RowNo
                    FROM Rows
                )
                SELECT TOP (@Limit)
                    SourceName,
                    SourceRoundId,
                    SourceRunId,
                    ActualResultLine,
                    CouponCount,
                    I15Min,
                    I15Max,
                    InitialTopCandidateLimit,
                    DiversePrePoolLimit,
                    ApiBudgetMultiplier,
                    ApiConcurrency,
                    MinHammingDistance,
                    MinHammingDistanceFinal,
                    MonteCarloScenarioCount,
                    ThirdChoiceMinRatio,
                    ProbabilityUniformBlend,
                    PatternScoreWeight,
                    WinnerPatternWeight,
                    RecentPatternWeight,
                    PreviousWeekPatternWeight,
                    SurpriseBalanceWeight,
                    BestHitCount,
                    AverageHitCount,
                    Hit15Count,
                    Hit14Count,
                    Hit13Count,
                    Hit12Count,
                    CostAmount,
                    GrossPrizeAmount,
                    NetProfitAmount,
                    Roi,
                    FoundExact,
                    Notes,
                    CreatedAt
                FROM Ranked
                WHERE RowNo = 1
                ORDER BY
                    FoundExact DESC,
                    Hit15Count DESC,
                    BestHitCount DESC,
                    Hit14Count DESC,
                    NetProfitAmount DESC,
                    Roi DESC,
                    AverageHitCount DESC,
                    CreatedAt DESC;
                """,
                connection);
            command.CommandTimeout = Math.Max(commandTimeoutSeconds, 5);
            command.Parameters.AddWithValue("@Limit", Math.Max(limit, 1));
            command.Parameters.AddWithValue("@MaxCouponCount", MaxPlayableCouponCount);

            var result = new List<CounterfactualParameterAuditRow>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var options = new OptimizationOptions
                {
                    MinI15WinnerCount = reader.GetInt32(5),
                    MaxI15WinnerCount = reader.GetInt32(6),
                    InitialTopCandidateLimit = reader.GetInt32(7),
                    DiversePrePoolLimit = reader.GetInt32(8),
                    ApiBudgetMultiplier = reader.GetInt32(9),
                    ApiConcurrency = reader.GetInt32(10),
                    MinHammingDistance = reader.GetInt32(11),
                    MinHammingDistanceFinal = reader.GetInt32(12),
                    MonteCarloScenarioCount = reader.GetInt32(13),
                    ThirdChoiceMinRatio = reader.GetDouble(14),
                    ProbabilityUniformBlend = reader.GetDouble(15),
                    PatternScoreWeight = reader.GetDouble(16),
                    WinnerPatternWeight = reader.GetDouble(17),
                    RecentPatternWeight = reader.GetDouble(18),
                    PreviousWeekPatternWeight = reader.GetDouble(19),
                    SurpriseBalanceWeight = reader.GetDouble(20)
                };

                result.Add(new CounterfactualParameterAuditRow(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetInt32(4),
                    options,
                    reader.GetInt32(21),
                    reader.GetDouble(22),
                    reader.GetInt32(23),
                    reader.GetInt32(24),
                    reader.GetInt32(25),
                    reader.GetInt32(26),
                    reader.GetDecimal(27),
                    reader.GetDecimal(28),
                    reader.GetDecimal(29),
                    reader.GetDouble(30),
                    reader.GetBoolean(31),
                    reader.IsDBNull(32) ? null : reader.GetString(32),
                    reader.GetDateTime(33)));
            }

            return result;
        }

        public async Task<IReadOnlyList<CounterfactualStabilityRow>> LoadCounterfactualStabilityRowsAsync(
            int limit = 250000,
            int sampleModulo = 250,
            CancellationToken cancellationToken = default,
            int commandTimeoutSeconds = 180)
        {
            await EnsureSchemaAsync(cancellationToken);

            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await EnsureLearnedStrategySchemaAsync(connection, cancellationToken);
            await EnsureCounterfactualAttemptSchemaAsync(connection, cancellationToken);

            await using var command = new SqlCommand(
                """
                WITH Rows AS
                (
                    SELECT
                        CAST('Learned' AS NVARCHAR(32)) AS SourceName,
                        SourceRoundId,
                        SourceRunId,
                        ActualResultLine,
                        CouponCount,
                        ThirdChoiceMinRatio,
                        ProbabilityUniformBlend,
                        PatternScoreWeight,
                        BestHitCount,
                        AverageHitCount,
                        Hit15Count,
                        Hit14Count,
                        Hit13Count,
                        Hit12Count,
                        CostAmount,
                        GrossPrizeAmount,
                        NetProfitAmount,
                        Roi,
                        FoundExact,
                        CreatedAt
                    FROM dbo.LearnedPredictionStrategies
                    WHERE CouponCount <= @MaxCouponCount

                    UNION ALL

                    SELECT
                        CAST('Attempt' AS NVARCHAR(32)) AS SourceName,
                        SourceRoundId,
                        SourceRunId,
                        ActualResultLine,
                        CouponCount,
                        CAST(ThirdChoiceMinRatio AS FLOAT) AS ThirdChoiceMinRatio,
                        CAST(ProbabilityUniformBlend AS FLOAT) AS ProbabilityUniformBlend,
                        CAST(PatternScoreWeight AS FLOAT) AS PatternScoreWeight,
                        BestHitCount,
                        AverageHitCount,
                        Hit15Count,
                        Hit14Count,
                        Hit13Count,
                        Hit12Count,
                        CostAmount,
                        GrossPrizeAmount,
                        NetProfitAmount,
                        Roi,
                        FoundExact,
                        CreatedAt
                    FROM dbo.CounterfactualParameterAttempts
                    WHERE CouponCount <= @MaxCouponCount
                      AND
                      (
                          FoundExact = 1
                          OR BestHitCount >= 14
                          OR Roi > 0
                          OR NetProfitAmount > 0
                          OR CHECKSUM(SourceRoundId, CouponCount, ThirdChoiceMinRatio, ProbabilityUniformBlend, PatternScoreWeight) % @SampleModulo = 0
                      )
                ),
                Ranked AS
                (
                    SELECT *,
                        ROW_NUMBER() OVER
                        (
                            PARTITION BY SourceRoundId, CouponCount, ThirdChoiceMinRatio, ProbabilityUniformBlend, PatternScoreWeight
                            ORDER BY FoundExact DESC, Hit15Count DESC, BestHitCount DESC, NetProfitAmount DESC, Roi DESC, CreatedAt DESC
                        ) AS RowNo
                    FROM Rows
                )
                SELECT TOP (@Limit)
                    SourceName,
                    SourceRoundId,
                    SourceRunId,
                    ActualResultLine,
                    CouponCount,
                    ThirdChoiceMinRatio,
                    ProbabilityUniformBlend,
                    PatternScoreWeight,
                    BestHitCount,
                    AverageHitCount,
                    Hit15Count,
                    Hit14Count,
                    Hit13Count,
                    Hit12Count,
                    CostAmount,
                    GrossPrizeAmount,
                    NetProfitAmount,
                    Roi,
                    FoundExact,
                    CreatedAt
                FROM Ranked
                WHERE RowNo = 1
                ORDER BY
                    CASE
                        WHEN FoundExact = 1 THEN 0
                        WHEN BestHitCount >= 14 AND Roi > 0 THEN 1
                        WHEN Roi > 0 THEN 2
                        WHEN BestHitCount >= 14 THEN 3
                        ELSE 4
                    END,
                    CHECKSUM(SourceRoundId, CouponCount, ThirdChoiceMinRatio, ProbabilityUniformBlend, PatternScoreWeight),
                    SourceRoundId DESC,
                    CreatedAt DESC;
                """,
                connection);
            command.CommandTimeout = Math.Max(commandTimeoutSeconds, 5);
            command.Parameters.AddWithValue("@Limit", Math.Max(limit, 1));
            command.Parameters.AddWithValue("@MaxCouponCount", MaxPlayableCouponCount);
            command.Parameters.AddWithValue("@SampleModulo", Math.Max(sampleModulo, 1));

            var result = new List<CounterfactualStabilityRow>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new CounterfactualStabilityRow(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetInt32(4),
                    reader.GetDouble(5),
                    reader.GetDouble(6),
                    reader.GetDouble(7),
                    reader.GetInt32(8),
                    reader.GetDouble(9),
                    reader.GetInt32(10),
                    reader.GetInt32(11),
                    reader.GetInt32(12),
                    reader.GetInt32(13),
                    reader.GetDecimal(14),
                    reader.GetDecimal(15),
                    reader.GetDecimal(16),
                    reader.GetDouble(17),
                    reader.GetBoolean(18),
                    reader.GetDateTime(19)));
            }

            return result;
        }

        public async Task SaveCounterfactualParameterAttemptsAsync(
            IReadOnlyList<LearnedPredictionStrategyCandidate> attempts,
            CancellationToken cancellationToken = default)
        {
            if (attempts.Count == 0)
            {
                return;
            }

            await EnsureSchemaAsync(cancellationToken);

            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await EnsureCounterfactualAttemptSchemaAsync(connection, cancellationToken);

            foreach (var attempt in attempts)
            {
                await using var command = new SqlCommand(
                    """
                    IF NOT EXISTS
                    (
                        SELECT 1
                        FROM dbo.CounterfactualParameterAttempts
                        WHERE SourceRoundId = @SourceRoundId
                          AND CouponCount = @CouponCount
                          AND ThirdChoiceMinRatio = @ThirdChoiceMinRatio
                          AND ProbabilityUniformBlend = @ProbabilityUniformBlend
                          AND PatternScoreWeight = @PatternScoreWeight
                    )
                    BEGIN
                        INSERT INTO dbo.CounterfactualParameterAttempts
                            (SearchBatchId, SourceRoundId, SourceRunId, ActualResultLine,
                             CouponCount, ThirdChoiceMinRatio, ProbabilityUniformBlend, PatternScoreWeight,
                             BestHitCount, AverageHitCount, Hit15Count, Hit14Count, Hit13Count, Hit12Count,
                             CostAmount, GrossPrizeAmount, NetProfitAmount, Roi,
                             FoundExact, AttemptMode)
                        VALUES
                            (@SearchBatchId, @SourceRoundId, @SourceRunId, @ActualResultLine,
                             @CouponCount, @ThirdChoiceMinRatio, @ProbabilityUniformBlend, @PatternScoreWeight,
                             @BestHitCount, @AverageHitCount, @Hit15Count, @Hit14Count, @Hit13Count, @Hit12Count,
                             @CostAmount, @GrossPrizeAmount, @NetProfitAmount, @Roi,
                             @FoundExact, @AttemptMode);
                    END;
                    """,
                    connection);

                command.Parameters.AddWithValue("@SearchBatchId", attempt.SearchBatchId);
                command.Parameters.AddWithValue("@SourceRoundId", attempt.SourceRoundId);
                command.Parameters.AddWithValue("@SourceRunId", attempt.SourceRunId);
                command.Parameters.AddWithValue("@ActualResultLine", attempt.ActualResultLine);
                command.Parameters.AddWithValue("@CouponCount", attempt.CouponCount);
                command.Parameters.AddWithValue("@ThirdChoiceMinRatio", RoundParameter(attempt.Options.ThirdChoiceMinRatio));
                command.Parameters.AddWithValue("@ProbabilityUniformBlend", RoundParameter(attempt.Options.ProbabilityUniformBlend));
                command.Parameters.AddWithValue("@PatternScoreWeight", RoundParameter(attempt.Options.PatternScoreWeight));
                command.Parameters.AddWithValue("@BestHitCount", attempt.BestHitCount);
                command.Parameters.AddWithValue("@AverageHitCount", attempt.AverageHitCount);
                command.Parameters.AddWithValue("@Hit15Count", attempt.Hit15Count);
                command.Parameters.AddWithValue("@Hit14Count", attempt.Hit14Count);
                command.Parameters.AddWithValue("@Hit13Count", attempt.Hit13Count);
                command.Parameters.AddWithValue("@Hit12Count", attempt.Hit12Count);
                command.Parameters.AddWithValue("@CostAmount", attempt.CostAmount);
                command.Parameters.AddWithValue("@GrossPrizeAmount", attempt.GrossPrizeAmount);
                command.Parameters.AddWithValue("@NetProfitAmount", attempt.NetProfitAmount);
                command.Parameters.AddWithValue("@Roi", attempt.Roi);
                command.Parameters.AddWithValue("@FoundExact", attempt.FoundExact);
                command.Parameters.AddWithValue("@AttemptMode", (object?)attempt.Notes ?? DBNull.Value);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        public async Task SaveLearnedPredictionStrategiesAsync(
            IReadOnlyList<LearnedPredictionStrategyCandidate> strategies,
            CancellationToken cancellationToken = default)
        {
            if (strategies.Count == 0)
            {
                return;
            }

            await EnsureSchemaAsync(cancellationToken);

            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await EnsureLearnedStrategySchemaAsync(connection, cancellationToken);

            foreach (var strategy in strategies)
            {
                await using var command = new SqlCommand(
                    """
                    INSERT INTO dbo.LearnedPredictionStrategies
                        (SearchBatchId, SourceRoundId, SourceRunId, ActualResultLine,
                         CouponCount, I15Min, I15Max, InitialTopCandidateLimit, DiversePrePoolLimit,
                         ApiBudgetMultiplier, ApiConcurrency, MinHammingDistance,
                         MinHammingDistanceFinal, MonteCarloScenarioCount,
                         ThirdChoiceMinRatio, ProbabilityUniformBlend,
                         PatternScoreWeight, WinnerPatternWeight, RecentPatternWeight,
                         PreviousWeekPatternWeight, SurpriseBalanceWeight,
                         BestHitCount, AverageHitCount, Hit15Count, Hit14Count, Hit13Count, Hit12Count,
                         CostAmount, GrossPrizeAmount, NetProfitAmount, Roi,
                         FoundExact, Notes)
                    VALUES
                        (@SearchBatchId, @SourceRoundId, @SourceRunId, @ActualResultLine,
                         @CouponCount, @I15Min, @I15Max, @InitialTopCandidateLimit, @DiversePrePoolLimit,
                         @ApiBudgetMultiplier, @ApiConcurrency, @MinHammingDistance,
                         @MinHammingDistanceFinal, @MonteCarloScenarioCount,
                         @ThirdChoiceMinRatio, @ProbabilityUniformBlend,
                         @PatternScoreWeight, @WinnerPatternWeight, @RecentPatternWeight,
                         @PreviousWeekPatternWeight, @SurpriseBalanceWeight,
                         @BestHitCount, @AverageHitCount, @Hit15Count, @Hit14Count, @Hit13Count, @Hit12Count,
                         @CostAmount, @GrossPrizeAmount, @NetProfitAmount, @Roi,
                         @FoundExact, @Notes);
                    """,
                    connection);

                AddLearnedStrategyParameters(command, strategy);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        public async Task<IReadOnlyList<LearnedPredictionStrategyRecommendation>> LoadRecommendedLearnedStrategiesAsync(
            int limit,
            CancellationToken cancellationToken = default)
        {
            await EnsureSchemaAsync(cancellationToken);

            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await EnsureLearnedStrategySchemaAsync(connection, cancellationToken);
            await EnsureCounterfactualAttemptSchemaAsync(connection, cancellationToken);

            await using var command = new SqlCommand(
                """
                WITH StrategyRows AS
                (
                    SELECT
                        CAST(0 AS INT) AS SourcePriority,
                        CouponCount,
                        I15Min,
                        I15Max,
                        InitialTopCandidateLimit,
                        DiversePrePoolLimit,
                        ApiBudgetMultiplier,
                        ApiConcurrency,
                        MinHammingDistance,
                        MinHammingDistanceFinal,
                        MonteCarloScenarioCount,
                        ThirdChoiceMinRatio,
                        ProbabilityUniformBlend,
                        PatternScoreWeight,
                        WinnerPatternWeight,
                        RecentPatternWeight,
                        PreviousWeekPatternWeight,
                        SurpriseBalanceWeight,
                        SourceRoundId,
                        FoundExact,
                        Hit15Count,
                        BestHitCount,
                        AverageHitCount,
                        NetProfitAmount,
                        Roi
                    FROM dbo.LearnedPredictionStrategies
                    WHERE CouponCount <= @MaxCouponCount
                      AND (FoundExact = 1 OR (BestHitCount >= 14 AND (NetProfitAmount > 0 OR Roi > 0)))

                    UNION ALL

                    SELECT
                        CAST(1 AS INT) AS SourcePriority,
                        CouponCount,
                        1 AS I15Min,
                        20 AS I15Max,
                        3200000 AS InitialTopCandidateLimit,
                        750000 AS DiversePrePoolLimit,
                        1000 AS ApiBudgetMultiplier,
                        6 AS ApiConcurrency,
                        3 AS MinHammingDistance,
                        2 AS MinHammingDistanceFinal,
                        50000 AS MonteCarloScenarioCount,
                        CAST(ThirdChoiceMinRatio AS FLOAT) AS ThirdChoiceMinRatio,
                        CAST(ProbabilityUniformBlend AS FLOAT) AS ProbabilityUniformBlend,
                        CAST(PatternScoreWeight AS FLOAT) AS PatternScoreWeight,
                        0.45 AS WinnerPatternWeight,
                        0.20 AS RecentPatternWeight,
                        0.12 AS PreviousWeekPatternWeight,
                        0.30 AS SurpriseBalanceWeight,
                        SourceRoundId,
                        FoundExact,
                        Hit15Count,
                        BestHitCount,
                        AverageHitCount,
                        NetProfitAmount,
                        Roi
                    FROM dbo.CounterfactualParameterAttempts
                    WHERE CouponCount <= @MaxCouponCount
                      AND (FoundExact = 1 OR (BestHitCount >= 14 AND (NetProfitAmount > 0 OR Roi > 0)))
                )
                SELECT TOP (@Limit)
                    CouponCount,
                    I15Min,
                    I15Max,
                    InitialTopCandidateLimit,
                    DiversePrePoolLimit,
                    ApiBudgetMultiplier,
                    ApiConcurrency,
                    MinHammingDistance,
                    MinHammingDistanceFinal,
                    MonteCarloScenarioCount,
                    ThirdChoiceMinRatio,
                    ProbabilityUniformBlend,
                    PatternScoreWeight,
                    WinnerPatternWeight,
                    RecentPatternWeight,
                    PreviousWeekPatternWeight,
                    SurpriseBalanceWeight,
                    COUNT(1) AS SampleCount,
                    COUNT(DISTINCT SourceRoundId) AS RoundCount,
                    COUNT(DISTINCT CASE WHEN FoundExact = 1 THEN SourceRoundId END) AS ExactRoundCount,
                    COUNT(DISTINCT CASE WHEN BestHitCount >= 14 AND (NetProfitAmount > 0 OR Roi > 0) THEN SourceRoundId END) AS RobustRoundCount,
                    COUNT(DISTINCT CASE WHEN NetProfitAmount > 0 OR Roi > 0 THEN SourceRoundId END) AS PositiveRoiRoundCount,
                    MIN(SourcePriority) AS BestSourcePriority,
                    SUM(Hit15Count) AS TotalHit15Count,
                    MAX(BestHitCount) AS MaxBestHit,
                    AVG(CAST(BestHitCount AS FLOAT)) AS AvgBestHit,
                    AVG(AverageHitCount) AS AvgCouponHit,
                    SUM(NetProfitAmount) AS TotalNetProfitAmount,
                    AVG(Roi) AS AvgRoi
                FROM StrategyRows
                GROUP BY
                    CouponCount,
                    I15Min,
                    I15Max,
                    InitialTopCandidateLimit,
                    DiversePrePoolLimit,
                    ApiBudgetMultiplier,
                    ApiConcurrency,
                    MinHammingDistance,
                    MinHammingDistanceFinal,
                    MonteCarloScenarioCount,
                    ThirdChoiceMinRatio,
                    ProbabilityUniformBlend,
                    PatternScoreWeight,
                    WinnerPatternWeight,
                    RecentPatternWeight,
                    PreviousWeekPatternWeight,
                    SurpriseBalanceWeight
                ORDER BY
                    COUNT(DISTINCT CASE WHEN FoundExact = 1 THEN SourceRoundId END) DESC,
                    COUNT(DISTINCT CASE WHEN BestHitCount >= 14 AND (NetProfitAmount > 0 OR Roi > 0) THEN SourceRoundId END) DESC,
                    COUNT(DISTINCT CASE WHEN NetProfitAmount > 0 OR Roi > 0 THEN SourceRoundId END) DESC,
                    MIN(SourcePriority) ASC,
                    SUM(NetProfitAmount) DESC,
                    AVG(Roi) DESC,
                    SUM(Hit15Count) DESC,
                    MAX(BestHitCount) DESC,
                    AVG(CAST(BestHitCount AS FLOAT)) DESC,
                    AVG(AverageHitCount) DESC;
                """,
                connection);
            command.Parameters.AddWithValue("@Limit", Math.Max(limit, 1));
            command.Parameters.AddWithValue("@MaxCouponCount", MaxPlayableCouponCount);

            var result = new List<LearnedPredictionStrategyRecommendation>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new LearnedPredictionStrategyRecommendation(
                    reader.GetInt32(0),
                    new OptimizationOptions
                    {
                        MinI15WinnerCount = reader.GetInt32(1),
                        MaxI15WinnerCount = reader.GetInt32(2),
                        InitialTopCandidateLimit = reader.GetInt32(3),
                        DiversePrePoolLimit = reader.GetInt32(4),
                        ApiBudgetMultiplier = reader.GetInt32(5),
                        ApiConcurrency = reader.GetInt32(6),
                        MinHammingDistance = reader.GetInt32(7),
                        MinHammingDistanceFinal = reader.GetInt32(8),
                        MonteCarloScenarioCount = reader.GetInt32(9),
                        ThirdChoiceMinRatio = reader.GetDouble(10),
                        ProbabilityUniformBlend = reader.GetDouble(11),
                        PatternScoreWeight = reader.GetDouble(12),
                        WinnerPatternWeight = reader.GetDouble(13),
                        RecentPatternWeight = reader.GetDouble(14),
                        PreviousWeekPatternWeight = reader.GetDouble(15),
                        SurpriseBalanceWeight = reader.GetDouble(16)
                    },
                    reader.GetInt32(17),
                    reader.GetInt32(18),
                    reader.GetInt32(19),
                    reader.GetInt32(20),
                    reader.GetInt32(21),
                    reader.GetInt32(23),
                    reader.GetInt32(24),
                    reader.GetDouble(25),
                    reader.GetDouble(26),
                    reader.GetDecimal(27),
                    reader.GetDouble(28)));
            }

            return result;
        }

        private static async Task<ResolvedActualResult?> TryResolveActualResultAsync(
            SqlConnection connection,
            PendingPredictionRun pending,
            CancellationToken cancellationToken)
        {
            if (pending.RoundId != null)
            {
                var byRoundId = await TryResolveActualByRoundIdAsync(connection, pending.RoundId.Value, cancellationToken);
                if (byRoundId != null)
                {
                    return byRoundId;
                }
            }

            if (!string.IsNullOrWhiteSpace(pending.RoundName))
            {
                var byRoundName = await TryResolveActualByRoundNameAsync(connection, pending.RoundName, cancellationToken);
                if (byRoundName != null)
                {
                    return byRoundName;
                }
            }

            var hasHistoricalMatches = await HasTableAsync(connection, "dbo.HistoricalResultMatches", cancellationToken);
            if (hasHistoricalMatches)
            {
                var byMatchMatrix = await TryResolveActualByMatchMatrixAsync(connection, pending.RunId, cancellationToken);
                if (byMatchMatrix != null)
                {
                    return byMatchMatrix;
                }
            }

            return null;
        }

        private static async Task<IReadOnlyList<SymbolProbabilities>> LoadRunMatrixProbabilitiesAsync(
            SqlConnection connection,
            int runId,
            CancellationToken cancellationToken)
        {
            var rows = new List<(int MatchOrder, SymbolProbabilities Probabilities)>();
            await using var command = new SqlCommand(
                """
                SELECT MatchOrder, P1, PX, P2
                FROM dbo.PredictionRunMatchMatrix
                WHERE RunId = @RunId
                  AND P1 IS NOT NULL
                  AND PX IS NOT NULL
                  AND P2 IS NOT NULL
                ORDER BY MatchOrder;
                """,
                connection);
            command.Parameters.AddWithValue("@RunId", runId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    reader.GetInt32(0),
                    SymbolProbabilities.Normalize(
                        reader.GetDouble(1),
                        reader.GetDouble(2),
                        reader.GetDouble(3))));
            }

            return rows
                .OrderBy(x => x.MatchOrder)
                .Select(x => x.Probabilities)
                .ToList();
        }

        private static void AddLearnedStrategyParameters(
            SqlCommand command,
            LearnedPredictionStrategyCandidate strategy)
        {
            command.Parameters.AddWithValue("@SearchBatchId", strategy.SearchBatchId);
            command.Parameters.AddWithValue("@SourceRoundId", strategy.SourceRoundId);
            command.Parameters.AddWithValue("@SourceRunId", strategy.SourceRunId);
            command.Parameters.AddWithValue("@ActualResultLine", strategy.ActualResultLine);
            command.Parameters.AddWithValue("@CouponCount", strategy.CouponCount);
            command.Parameters.AddWithValue("@I15Min", strategy.Options.MinI15WinnerCount);
            command.Parameters.AddWithValue("@I15Max", strategy.Options.MaxI15WinnerCount);
            command.Parameters.AddWithValue("@InitialTopCandidateLimit", strategy.Options.InitialTopCandidateLimit);
            command.Parameters.AddWithValue("@DiversePrePoolLimit", strategy.Options.DiversePrePoolLimit);
            command.Parameters.AddWithValue("@ApiBudgetMultiplier", strategy.Options.ApiBudgetMultiplier);
            command.Parameters.AddWithValue("@ApiConcurrency", strategy.Options.ApiConcurrency);
            command.Parameters.AddWithValue("@MinHammingDistance", strategy.Options.MinHammingDistance);
            command.Parameters.AddWithValue("@MinHammingDistanceFinal", strategy.Options.MinHammingDistanceFinal);
            command.Parameters.AddWithValue("@MonteCarloScenarioCount", strategy.Options.MonteCarloScenarioCount);
            command.Parameters.AddWithValue("@ThirdChoiceMinRatio", strategy.Options.ThirdChoiceMinRatio);
            command.Parameters.AddWithValue("@ProbabilityUniformBlend", strategy.Options.ProbabilityUniformBlend);
            command.Parameters.AddWithValue("@PatternScoreWeight", strategy.Options.PatternScoreWeight);
            command.Parameters.AddWithValue("@WinnerPatternWeight", strategy.Options.WinnerPatternWeight);
            command.Parameters.AddWithValue("@RecentPatternWeight", strategy.Options.RecentPatternWeight);
            command.Parameters.AddWithValue("@PreviousWeekPatternWeight", strategy.Options.PreviousWeekPatternWeight);
            command.Parameters.AddWithValue("@SurpriseBalanceWeight", strategy.Options.SurpriseBalanceWeight);
            command.Parameters.AddWithValue("@BestHitCount", strategy.BestHitCount);
            command.Parameters.AddWithValue("@AverageHitCount", strategy.AverageHitCount);
            command.Parameters.AddWithValue("@Hit15Count", strategy.Hit15Count);
            command.Parameters.AddWithValue("@Hit14Count", strategy.Hit14Count);
            command.Parameters.AddWithValue("@Hit13Count", strategy.Hit13Count);
            command.Parameters.AddWithValue("@Hit12Count", strategy.Hit12Count);
            command.Parameters.AddWithValue("@CostAmount", strategy.CostAmount);
            command.Parameters.AddWithValue("@GrossPrizeAmount", strategy.GrossPrizeAmount);
            command.Parameters.AddWithValue("@NetProfitAmount", strategy.NetProfitAmount);
            command.Parameters.AddWithValue("@Roi", strategy.Roi);
            command.Parameters.AddWithValue("@FoundExact", strategy.FoundExact);
            command.Parameters.AddWithValue("@Notes", (object?)strategy.Notes ?? DBNull.Value);
        }

        private static decimal RoundParameter(double value)
        {
            return decimal.Round(Convert.ToDecimal(value), 4, MidpointRounding.AwayFromZero);
        }

        private static async Task EnsureLearnedStrategySchemaAsync(
            SqlConnection connection,
            CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand(
                """
                IF OBJECT_ID('dbo.LearnedPredictionStrategies', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.LearnedPredictionStrategies
                    (
                        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_LearnedPredictionStrategies PRIMARY KEY,
                        SearchBatchId UNIQUEIDENTIFIER NOT NULL,
                        SourceRoundId INT NOT NULL,
                        SourceRunId INT NOT NULL,
                        ActualResultLine CHAR(15) NOT NULL,
                        CouponCount INT NOT NULL,
                        I15Min INT NOT NULL,
                        I15Max INT NOT NULL,
                        InitialTopCandidateLimit INT NOT NULL,
                        DiversePrePoolLimit INT NOT NULL,
                        ApiBudgetMultiplier INT NOT NULL,
                        ApiConcurrency INT NOT NULL,
                        MinHammingDistance INT NOT NULL,
                        MinHammingDistanceFinal INT NOT NULL,
                        MonteCarloScenarioCount INT NOT NULL,
                        ThirdChoiceMinRatio FLOAT NOT NULL,
                        ProbabilityUniformBlend FLOAT NOT NULL,
                        PatternScoreWeight FLOAT NOT NULL,
                        WinnerPatternWeight FLOAT NOT NULL,
                        RecentPatternWeight FLOAT NOT NULL,
                        PreviousWeekPatternWeight FLOAT NOT NULL,
                        SurpriseBalanceWeight FLOAT NOT NULL,
                        BestHitCount INT NOT NULL,
                        AverageHitCount FLOAT NOT NULL,
                        Hit15Count INT NOT NULL,
                        Hit14Count INT NOT NULL,
                        Hit13Count INT NOT NULL,
                        Hit12Count INT NOT NULL,
                        FoundExact BIT NOT NULL,
                        Notes NVARCHAR(500) NULL,
                        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_LearnedPredictionStrategies_CreatedAt DEFAULT SYSDATETIME()
                    );
                END;

                IF COL_LENGTH('dbo.LearnedPredictionStrategies', 'CostAmount') IS NULL
                    ALTER TABLE dbo.LearnedPredictionStrategies ADD CostAmount DECIMAL(18,2) NOT NULL CONSTRAINT DF_LearnedPredictionStrategies_CostAmount DEFAULT 0;

                IF COL_LENGTH('dbo.LearnedPredictionStrategies', 'GrossPrizeAmount') IS NULL
                    ALTER TABLE dbo.LearnedPredictionStrategies ADD GrossPrizeAmount DECIMAL(18,2) NOT NULL CONSTRAINT DF_LearnedPredictionStrategies_GrossPrizeAmount DEFAULT 0;

                IF COL_LENGTH('dbo.LearnedPredictionStrategies', 'NetProfitAmount') IS NULL
                    ALTER TABLE dbo.LearnedPredictionStrategies ADD NetProfitAmount DECIMAL(18,2) NOT NULL CONSTRAINT DF_LearnedPredictionStrategies_NetProfitAmount DEFAULT 0;

                IF COL_LENGTH('dbo.LearnedPredictionStrategies', 'Roi') IS NULL
                    ALTER TABLE dbo.LearnedPredictionStrategies ADD Roi FLOAT NOT NULL CONSTRAINT DF_LearnedPredictionStrategies_Roi DEFAULT 0;

                IF NOT EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE name = 'IX_LearnedPredictionStrategies_Ranking'
                      AND object_id = OBJECT_ID('dbo.LearnedPredictionStrategies')
                )
                    CREATE INDEX IX_LearnedPredictionStrategies_Ranking
                        ON dbo.LearnedPredictionStrategies
                        (FoundExact, BestHitCount, SourceRoundId);

                IF NOT EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE name = 'IX_LearnedPredictionStrategies_SourceRound'
                      AND object_id = OBJECT_ID('dbo.LearnedPredictionStrategies')
                )
                    CREATE INDEX IX_LearnedPredictionStrategies_SourceRound
                        ON dbo.LearnedPredictionStrategies
                        (SourceRoundId, SourceRunId);
                """,
                connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task EnsureCounterfactualAttemptSchemaAsync(
            SqlConnection connection,
            CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand(
                """
                IF OBJECT_ID('dbo.CounterfactualParameterAttempts', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.CounterfactualParameterAttempts
                    (
                        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CounterfactualParameterAttempts PRIMARY KEY,
                        SearchBatchId UNIQUEIDENTIFIER NOT NULL,
                        SourceRoundId INT NOT NULL,
                        SourceRunId INT NOT NULL,
                        ActualResultLine CHAR(15) NOT NULL,
                        CouponCount INT NOT NULL,
                        ThirdChoiceMinRatio DECIMAL(10,4) NOT NULL,
                        ProbabilityUniformBlend DECIMAL(10,4) NOT NULL,
                        PatternScoreWeight DECIMAL(10,4) NOT NULL,
                        BestHitCount INT NOT NULL,
                        AverageHitCount FLOAT NOT NULL,
                        Hit15Count INT NOT NULL,
                        Hit14Count INT NOT NULL,
                        Hit13Count INT NOT NULL,
                        Hit12Count INT NOT NULL,
                        CostAmount DECIMAL(18,2) NOT NULL CONSTRAINT DF_CounterfactualParameterAttempts_CostAmount DEFAULT 0,
                        GrossPrizeAmount DECIMAL(18,2) NOT NULL CONSTRAINT DF_CounterfactualParameterAttempts_GrossPrizeAmount DEFAULT 0,
                        NetProfitAmount DECIMAL(18,2) NOT NULL CONSTRAINT DF_CounterfactualParameterAttempts_NetProfitAmount DEFAULT 0,
                        Roi FLOAT NOT NULL CONSTRAINT DF_CounterfactualParameterAttempts_Roi DEFAULT 0,
                        FoundExact BIT NOT NULL,
                        AttemptMode NVARCHAR(500) NULL,
                        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_CounterfactualParameterAttempts_CreatedAt DEFAULT SYSDATETIME()
                    );
                END;

                IF COL_LENGTH('dbo.CounterfactualParameterAttempts', 'CostAmount') IS NULL
                    ALTER TABLE dbo.CounterfactualParameterAttempts ADD CostAmount DECIMAL(18,2) NOT NULL CONSTRAINT DF_CounterfactualParameterAttempts_CostAmount_Alter DEFAULT 0;

                IF COL_LENGTH('dbo.CounterfactualParameterAttempts', 'GrossPrizeAmount') IS NULL
                    ALTER TABLE dbo.CounterfactualParameterAttempts ADD GrossPrizeAmount DECIMAL(18,2) NOT NULL CONSTRAINT DF_CounterfactualParameterAttempts_GrossPrizeAmount_Alter DEFAULT 0;

                IF COL_LENGTH('dbo.CounterfactualParameterAttempts', 'NetProfitAmount') IS NULL
                    ALTER TABLE dbo.CounterfactualParameterAttempts ADD NetProfitAmount DECIMAL(18,2) NOT NULL CONSTRAINT DF_CounterfactualParameterAttempts_NetProfitAmount_Alter DEFAULT 0;

                IF COL_LENGTH('dbo.CounterfactualParameterAttempts', 'Roi') IS NULL
                    ALTER TABLE dbo.CounterfactualParameterAttempts ADD Roi FLOAT NOT NULL CONSTRAINT DF_CounterfactualParameterAttempts_Roi_Alter DEFAULT 0;

                IF NOT EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE name = 'UX_CounterfactualParameterAttempts_Param'
                      AND object_id = OBJECT_ID('dbo.CounterfactualParameterAttempts')
                )
                    CREATE UNIQUE INDEX UX_CounterfactualParameterAttempts_Param
                        ON dbo.CounterfactualParameterAttempts
                        (SourceRoundId, CouponCount, ThirdChoiceMinRatio, ProbabilityUniformBlend, PatternScoreWeight);

                IF NOT EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE name = 'IX_CounterfactualParameterAttempts_Ranking'
                      AND object_id = OBJECT_ID('dbo.CounterfactualParameterAttempts')
                )
                    CREATE INDEX IX_CounterfactualParameterAttempts_Ranking
                        ON dbo.CounterfactualParameterAttempts
                        (SourceRoundId, FoundExact, BestHitCount);
                """,
                connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task<bool> HasTableAsync(
            SqlConnection connection,
            string tableName,
            CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand(
                "SELECT CASE WHEN OBJECT_ID(@TableName, 'U') IS NULL THEN 0 ELSE 1 END;",
                connection);
            command.Parameters.AddWithValue("@TableName", tableName);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt32(result) == 1;
        }

        private static async Task<ResolvedActualResult?> TryResolveActualByRoundIdAsync(
            SqlConnection connection,
            int roundId,
            CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand(
                """
                SELECT TOP (1) RoundId, ResultLine
                FROM dbo.HistoricalResults
                WHERE RoundId = @RoundId
                  AND ResultLine IS NOT NULL
                  AND LEN(ResultLine) = 15;
                """,
                connection);
            command.Parameters.AddWithValue("@RoundId", roundId);

            return await ReadResolvedActualResultAsync(command, cancellationToken);
        }

        private static async Task<ResolvedActualResult?> TryResolveActualByRoundNameAsync(
            SqlConnection connection,
            string roundName,
            CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand(
                """
                SELECT TOP (1) RoundId, ResultLine
                FROM dbo.HistoricalResults
                WHERE RoundName = @RoundName
                  AND RoundId IS NOT NULL
                  AND ResultLine IS NOT NULL
                  AND LEN(ResultLine) = 15
                ORDER BY RoundId DESC;
                """,
                connection);
            command.CommandTimeout = 15;
            command.Parameters.AddWithValue("@RoundName", roundName);

            return await ReadResolvedActualResultAsync(command, cancellationToken);
        }

        private static async Task<ResolvedActualResult?> TryResolveActualByMatchMatrixAsync(
            SqlConnection connection,
            int runId,
            CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand(
                """
                SELECT TOP (1)
                    hr.RoundId,
                    hr.ResultLine
                FROM dbo.PredictionRunMatchMatrix m
                INNER JOIN dbo.HistoricalResultMatches hm
                    ON hm.MatchOrder = m.MatchOrder
                   AND hm.HomeTeamName = m.HomeTeamName
                   AND hm.AwayTeamName = m.AwayTeamName
                INNER JOIN dbo.HistoricalResults hr
                    ON hr.Id = hm.HistoricalResultId
                WHERE m.RunId = @RunId
                  AND hr.RoundId IS NOT NULL
                  AND hr.ResultLine IS NOT NULL
                  AND LEN(hr.ResultLine) = 15
                GROUP BY hr.RoundId, hr.ResultLine
                HAVING COUNT(*) >= 12
                ORDER BY COUNT(*) DESC, hr.RoundId DESC;
                """,
                connection);
            command.CommandTimeout = 20;
            command.Parameters.AddWithValue("@RunId", runId);

            return await ReadResolvedActualResultAsync(command, cancellationToken);
        }

        private static async Task<ResolvedActualResult?> ReadResolvedActualResultAsync(
            SqlCommand command,
            CancellationToken cancellationToken)
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            return new ResolvedActualResult(reader.GetInt32(0), reader.GetString(1));
        }

        private static async Task EnsureSchemaAsync(CancellationToken cancellationToken)
        {
            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            await using var command = new SqlCommand(
                """
                IF COL_LENGTH('dbo.PredictionRuns', 'CreatedAt') IS NULL
                    ALTER TABLE dbo.PredictionRuns
                    ADD CreatedAt DATETIME2 NOT NULL
                        CONSTRAINT DF_PredictionRuns_CreatedAt DEFAULT SYSDATETIME() WITH VALUES;

                IF OBJECT_ID('dbo.PredictionRunModelInfo', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.PredictionRunModelInfo
                    (
                        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PredictionRunModelInfo PRIMARY KEY,
                        RunId INT NOT NULL,
                        RoundId INT NULL,
                        RoundName NVARCHAR(100) NULL,
                        NesineProgramNo INT NULL,
                        UsedNesinePopularity BIT NOT NULL,
                        UsedHeadToHead BIT NOT NULL,
                        UsedFeatureModel BIT NOT NULL,
                        UsedTeamEnsemble BIT NOT NULL CONSTRAINT DF_PredictionRunModelInfo_UsedTeamEnsemble DEFAULT 0,
                        EnsembleCalibrationSampleCount INT NOT NULL CONSTRAINT DF_PredictionRunModelInfo_EnsembleCalibration DEFAULT 0,
                        EnsembleEloWeight FLOAT NULL,
                        EnsembleDixonColesWeight FLOAT NULL,
                        EnsembleEloTemperature FLOAT NULL,
                        EnsembleDixonColesTemperature FLOAT NULL,
                        EnsembleMatchCount INT NOT NULL CONSTRAINT DF_PredictionRunModelInfo_EnsembleMatchCount DEFAULT 0,
                        I15Min INT NOT NULL,
                        I15Max INT NOT NULL,
                        InitialTopCandidateLimit INT NOT NULL,
                        DiversePrePoolLimit INT NOT NULL,
                        ApiBudgetMultiplier INT NOT NULL,
                        ApiConcurrency INT NOT NULL,
                        MinHammingDistance INT NOT NULL,
                        MinHammingDistanceFinal INT NOT NULL,
                        MonteCarloScenarioCount INT NOT NULL,
                        ThirdChoiceMinRatio FLOAT NOT NULL CONSTRAINT DF_PredictionRunModelInfo_ThirdChoiceRatio DEFAULT 1.01,
                        ProbabilityUniformBlend FLOAT NOT NULL CONSTRAINT DF_PredictionRunModelInfo_UniformBlend DEFAULT 0,
                        PatternScoreWeight FLOAT NOT NULL CONSTRAINT DF_PredictionRunModelInfo_PatternScoreWeight DEFAULT 0,
                        WinnerPatternWeight FLOAT NOT NULL CONSTRAINT DF_PredictionRunModelInfo_WinnerPatternWeight DEFAULT 0,
                        RecentPatternWeight FLOAT NOT NULL CONSTRAINT DF_PredictionRunModelInfo_RecentPatternWeight DEFAULT 0,
                        PreviousWeekPatternWeight FLOAT NOT NULL CONSTRAINT DF_PredictionRunModelInfo_PreviousWeekPatternWeight DEFAULT 0,
                        SurpriseBalanceWeight FLOAT NOT NULL CONSTRAINT DF_PredictionRunModelInfo_SurpriseBalanceWeight DEFAULT 0,
                        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_PredictionRunModelInfo_CreatedAt DEFAULT SYSDATETIME(),
                        CONSTRAINT FK_PredictionRunModelInfo_PredictionRuns FOREIGN KEY (RunId) REFERENCES dbo.PredictionRuns(Id)
                    );
                END;

                IF COL_LENGTH('dbo.PredictionRunModelInfo', 'UsedTeamEnsemble') IS NULL
                    ALTER TABLE dbo.PredictionRunModelInfo
                    ADD UsedTeamEnsemble BIT NOT NULL
                        CONSTRAINT DF_PredictionRunModelInfo_UsedTeamEnsemble DEFAULT 0 WITH VALUES;

                IF COL_LENGTH('dbo.PredictionRunModelInfo', 'EnsembleCalibrationSampleCount') IS NULL
                    ALTER TABLE dbo.PredictionRunModelInfo
                    ADD EnsembleCalibrationSampleCount INT NOT NULL
                        CONSTRAINT DF_PredictionRunModelInfo_EnsembleCalibration DEFAULT 0 WITH VALUES;

                IF COL_LENGTH('dbo.PredictionRunModelInfo', 'EnsembleEloWeight') IS NULL
                    ALTER TABLE dbo.PredictionRunModelInfo ADD EnsembleEloWeight FLOAT NULL;

                IF COL_LENGTH('dbo.PredictionRunModelInfo', 'EnsembleDixonColesWeight') IS NULL
                    ALTER TABLE dbo.PredictionRunModelInfo ADD EnsembleDixonColesWeight FLOAT NULL;

                IF COL_LENGTH('dbo.PredictionRunModelInfo', 'EnsembleEloTemperature') IS NULL
                    ALTER TABLE dbo.PredictionRunModelInfo ADD EnsembleEloTemperature FLOAT NULL;

                IF COL_LENGTH('dbo.PredictionRunModelInfo', 'EnsembleDixonColesTemperature') IS NULL
                    ALTER TABLE dbo.PredictionRunModelInfo ADD EnsembleDixonColesTemperature FLOAT NULL;

                IF COL_LENGTH('dbo.PredictionRunModelInfo', 'EnsembleMatchCount') IS NULL
                    ALTER TABLE dbo.PredictionRunModelInfo
                    ADD EnsembleMatchCount INT NOT NULL
                        CONSTRAINT DF_PredictionRunModelInfo_EnsembleMatchCount DEFAULT 0 WITH VALUES;

                IF COL_LENGTH('dbo.PredictionRunModelInfo', 'ThirdChoiceMinRatio') IS NULL
                    ALTER TABLE dbo.PredictionRunModelInfo
                    ADD ThirdChoiceMinRatio FLOAT NOT NULL
                        CONSTRAINT DF_PredictionRunModelInfo_ThirdChoiceRatio DEFAULT 1.01 WITH VALUES;

                IF COL_LENGTH('dbo.PredictionRunModelInfo', 'ProbabilityUniformBlend') IS NULL
                    ALTER TABLE dbo.PredictionRunModelInfo
                    ADD ProbabilityUniformBlend FLOAT NOT NULL
                        CONSTRAINT DF_PredictionRunModelInfo_UniformBlend DEFAULT 0 WITH VALUES;

                IF COL_LENGTH('dbo.PredictionRunModelInfo', 'PatternScoreWeight') IS NULL
                    ALTER TABLE dbo.PredictionRunModelInfo
                    ADD PatternScoreWeight FLOAT NOT NULL
                        CONSTRAINT DF_PredictionRunModelInfo_PatternScoreWeight DEFAULT 0 WITH VALUES;

                IF COL_LENGTH('dbo.PredictionRunModelInfo', 'WinnerPatternWeight') IS NULL
                    ALTER TABLE dbo.PredictionRunModelInfo
                    ADD WinnerPatternWeight FLOAT NOT NULL
                        CONSTRAINT DF_PredictionRunModelInfo_WinnerPatternWeight DEFAULT 0 WITH VALUES;

                IF COL_LENGTH('dbo.PredictionRunModelInfo', 'RecentPatternWeight') IS NULL
                    ALTER TABLE dbo.PredictionRunModelInfo
                    ADD RecentPatternWeight FLOAT NOT NULL
                        CONSTRAINT DF_PredictionRunModelInfo_RecentPatternWeight DEFAULT 0 WITH VALUES;

                IF COL_LENGTH('dbo.PredictionRunModelInfo', 'PreviousWeekPatternWeight') IS NULL
                    ALTER TABLE dbo.PredictionRunModelInfo
                    ADD PreviousWeekPatternWeight FLOAT NOT NULL
                        CONSTRAINT DF_PredictionRunModelInfo_PreviousWeekPatternWeight DEFAULT 0 WITH VALUES;

                IF COL_LENGTH('dbo.PredictionRunModelInfo', 'SurpriseBalanceWeight') IS NULL
                    ALTER TABLE dbo.PredictionRunModelInfo
                    ADD SurpriseBalanceWeight FLOAT NOT NULL
                        CONSTRAINT DF_PredictionRunModelInfo_SurpriseBalanceWeight DEFAULT 0 WITH VALUES;

                IF OBJECT_ID('dbo.PredictionRunMatchMatrix', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.PredictionRunMatchMatrix
                    (
                        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PredictionRunMatchMatrix PRIMARY KEY,
                        RunId INT NOT NULL,
                        MatchOrder INT NOT NULL,
                        HomeTeamName NVARCHAR(200) NULL,
                        AwayTeamName NVARCHAR(200) NULL,
                        P1 FLOAT NULL,
                        PX FLOAT NULL,
                        P2 FLOAT NULL,
                        K1 INT NOT NULL,
                        KX INT NOT NULL,
                        K2 INT NOT NULL,
                        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_PredictionRunMatchMatrix_CreatedAt DEFAULT SYSDATETIME(),
                        CONSTRAINT FK_PredictionRunMatchMatrix_PredictionRuns FOREIGN KEY (RunId) REFERENCES dbo.PredictionRuns(Id)
                    );
                END;

                IF OBJECT_ID('dbo.PredictionRunResults', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.PredictionRunResults
                    (
                        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PredictionRunResults PRIMARY KEY,
                        RunId INT NOT NULL,
                        RoundId INT NOT NULL,
                        ActualResultLine CHAR(15) NOT NULL,
                        BestHitCount INT NOT NULL,
                        AverageHitCount FLOAT NOT NULL,
                        Hit15Count INT NOT NULL,
                        Hit14Count INT NOT NULL,
                        Hit13Count INT NOT NULL,
                        Hit12Count INT NOT NULL,
                        EvaluatedAt DATETIME2 NOT NULL CONSTRAINT DF_PredictionRunResults_EvaluatedAt DEFAULT SYSDATETIME(),
                        CONSTRAINT FK_PredictionRunResults_PredictionRuns FOREIGN KEY (RunId) REFERENCES dbo.PredictionRuns(Id)
                    );
                END;

                IF OBJECT_ID('dbo.Predictions', 'U') IS NOT NULL
                   AND NOT EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE name = 'IX_Predictions_RunId_PredictionLine'
                      AND object_id = OBJECT_ID('dbo.Predictions')
                )
                    CREATE INDEX IX_Predictions_RunId_PredictionLine
                        ON dbo.Predictions (RunId)
                        INCLUDE (PredictionLine);

                IF NOT EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE name = 'IX_PredictionRunResults_RunId'
                      AND object_id = OBJECT_ID('dbo.PredictionRunResults')
                )
                    CREATE INDEX IX_PredictionRunResults_RunId
                        ON dbo.PredictionRunResults (RunId);

                IF OBJECT_ID('dbo.PredictionRunModelInfo', 'U') IS NOT NULL
                   AND NOT EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE name = 'IX_PredictionRunModelInfo_RunId'
                      AND object_id = OBJECT_ID('dbo.PredictionRunModelInfo')
                )
                    CREATE INDEX IX_PredictionRunModelInfo_RunId
                        ON dbo.PredictionRunModelInfo (RunId)
                        INCLUDE (RoundId, RoundName);

                IF OBJECT_ID('dbo.PredictionRunMatchMatrix', 'U') IS NOT NULL
                   AND NOT EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE name = 'IX_PredictionRunMatchMatrix_RunId_MatchOrder'
                      AND object_id = OBJECT_ID('dbo.PredictionRunMatchMatrix')
                )
                    CREATE INDEX IX_PredictionRunMatchMatrix_RunId_MatchOrder
                        ON dbo.PredictionRunMatchMatrix (RunId, MatchOrder)
                        INCLUDE (HomeTeamName, AwayTeamName, P1, PX, P2);

                IF OBJECT_ID('dbo.HistoricalResults', 'U') IS NOT NULL
                   AND NOT EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE name = 'IX_HistoricalResults_RoundId_ResultLine'
                      AND object_id = OBJECT_ID('dbo.HistoricalResults')
                )
                    CREATE INDEX IX_HistoricalResults_RoundId_ResultLine
                        ON dbo.HistoricalResults (RoundId)
                        INCLUDE (ResultLine);

                IF OBJECT_ID('dbo.HistoricalResults', 'U') IS NOT NULL
                   AND NOT EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE name = 'IX_HistoricalResults_RoundName_ResultLine'
                      AND object_id = OBJECT_ID('dbo.HistoricalResults')
                )
                    CREATE INDEX IX_HistoricalResults_RoundName_ResultLine
                        ON dbo.HistoricalResults (RoundName)
                        INCLUDE (RoundId, ResultLine);

                IF OBJECT_ID('dbo.HistoricalResultMatches', 'U') IS NOT NULL
                   AND NOT EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE name = 'IX_HistoricalResultMatches_MatchLookup'
                      AND object_id = OBJECT_ID('dbo.HistoricalResultMatches')
                )
                    CREATE INDEX IX_HistoricalResultMatches_MatchLookup
                        ON dbo.HistoricalResultMatches (MatchOrder, HomeTeamName, AwayTeamName)
                        INCLUDE (HistoricalResultId, RoundId);
                """,
                connection);
            command.CommandTimeout = 180;

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task<int> InsertRunAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            int totalRequested,
            int totalGenerated,
            string? notes,
            CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand(
                """
                INSERT INTO PredictionRuns (TotalRequested, TotalGenerated, Notes)
                OUTPUT INSERTED.Id
                VALUES (@TotalRequested, @TotalGenerated, @Notes);
                """,
                connection,
                transaction);

            command.Parameters.AddWithValue("@TotalRequested", totalRequested);
            command.Parameters.AddWithValue("@TotalGenerated", totalGenerated);
            command.Parameters.AddWithValue("@Notes", (object?)notes ?? DBNull.Value);

            var id = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt32(id);
        }

        private static async Task InsertPredictionAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            int runId,
            Coupon coupon,
            string prediction,
            string? profileName,
            CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand(
                """
                INSERT INTO Predictions
                    (RunId, ProfileName, PredictionLine, Utility, P15Probability, P14Probability, P13Probability, I15, I14, I13, I12)
                VALUES
                    (@RunId, @ProfileName, @PredictionLine, @Utility, @P15Probability, @P14Probability, @P13Probability, @I15, @I14, @I13, @I12);
                """,
                connection,
                transaction);

            command.Parameters.AddWithValue("@RunId", runId);
            command.Parameters.AddWithValue("@ProfileName", (object?)profileName ?? DBNull.Value);
            command.Parameters.AddWithValue("@PredictionLine", prediction);
            command.Parameters.AddWithValue("@Utility", coupon.Utility);
            command.Parameters.AddWithValue("@P15Probability", coupon.P15Probability);
            command.Parameters.AddWithValue("@P14Probability", coupon.P14Probability);
            command.Parameters.AddWithValue("@P13Probability", coupon.P13Probability);
            command.Parameters.AddWithValue("@I15", ParseInt(coupon.bonus.i15));
            command.Parameters.AddWithValue("@I14", ParseInt(coupon.bonus.i14));
            command.Parameters.AddWithValue("@I13", ParseInt(coupon.bonus.i13));
            command.Parameters.AddWithValue("@I12", ParseInt(coupon.bonus.i12));

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task InsertRunModelInfoAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            int runId,
            PredictionRunContext context,
            CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand(
                """
                INSERT INTO PredictionRunModelInfo
                    (RunId, RoundId, RoundName, NesineProgramNo,
                     UsedNesinePopularity, UsedHeadToHead, UsedFeatureModel, UsedTeamEnsemble,
                     EnsembleCalibrationSampleCount, EnsembleEloWeight,
                     EnsembleDixonColesWeight, EnsembleEloTemperature,
                     EnsembleDixonColesTemperature, EnsembleMatchCount,
                     I15Min, I15Max, InitialTopCandidateLimit, DiversePrePoolLimit,
                      ApiBudgetMultiplier, ApiConcurrency, MinHammingDistance,
                      MinHammingDistanceFinal, MonteCarloScenarioCount,
                      ThirdChoiceMinRatio, ProbabilityUniformBlend,
                      PatternScoreWeight, WinnerPatternWeight, RecentPatternWeight,
                      PreviousWeekPatternWeight, SurpriseBalanceWeight)
                VALUES
                    (@RunId, @RoundId, @RoundName, @NesineProgramNo,
                     @UsedNesinePopularity, @UsedHeadToHead, @UsedFeatureModel, @UsedTeamEnsemble,
                     @EnsembleCalibrationSampleCount, @EnsembleEloWeight,
                     @EnsembleDixonColesWeight, @EnsembleEloTemperature,
                     @EnsembleDixonColesTemperature, @EnsembleMatchCount,
                     @I15Min, @I15Max, @InitialTopCandidateLimit, @DiversePrePoolLimit,
                      @ApiBudgetMultiplier, @ApiConcurrency, @MinHammingDistance,
                      @MinHammingDistanceFinal, @MonteCarloScenarioCount,
                      @ThirdChoiceMinRatio, @ProbabilityUniformBlend,
                      @PatternScoreWeight, @WinnerPatternWeight, @RecentPatternWeight,
                      @PreviousWeekPatternWeight, @SurpriseBalanceWeight);
                """,
                connection,
                transaction);

            command.Parameters.AddWithValue("@RunId", runId);
            command.Parameters.AddWithValue("@RoundId", (object?)context.RoundId ?? DBNull.Value);
            command.Parameters.AddWithValue("@RoundName", (object?)context.RoundName ?? DBNull.Value);
            command.Parameters.AddWithValue("@NesineProgramNo", (object?)context.NesineProgramNo ?? DBNull.Value);
            command.Parameters.AddWithValue("@UsedNesinePopularity", context.UsedNesinePopularity);
            command.Parameters.AddWithValue("@UsedHeadToHead", context.UsedHeadToHead);
            command.Parameters.AddWithValue("@UsedFeatureModel", context.UsedFeatureModel);
            command.Parameters.AddWithValue("@UsedTeamEnsemble", context.UsedTeamEnsemble);
            command.Parameters.AddWithValue(
                "@EnsembleCalibrationSampleCount",
                context.EnsembleCalibrationSampleCount);
            command.Parameters.AddWithValue(
                "@EnsembleEloWeight",
                (object?)context.EnsembleEloWeight ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "@EnsembleDixonColesWeight",
                (object?)context.EnsembleDixonColesWeight ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "@EnsembleEloTemperature",
                (object?)context.EnsembleEloTemperature ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "@EnsembleDixonColesTemperature",
                (object?)context.EnsembleDixonColesTemperature ?? DBNull.Value);
            command.Parameters.AddWithValue("@EnsembleMatchCount", context.EnsembleMatchCount);
            command.Parameters.AddWithValue("@I15Min", context.Options.MinI15WinnerCount);
            command.Parameters.AddWithValue("@I15Max", context.Options.MaxI15WinnerCount);
            command.Parameters.AddWithValue("@InitialTopCandidateLimit", context.Options.InitialTopCandidateLimit);
            command.Parameters.AddWithValue("@DiversePrePoolLimit", context.Options.DiversePrePoolLimit);
            command.Parameters.AddWithValue("@ApiBudgetMultiplier", context.Options.ApiBudgetMultiplier);
            command.Parameters.AddWithValue("@ApiConcurrency", context.Options.ApiConcurrency);
            command.Parameters.AddWithValue("@MinHammingDistance", context.Options.MinHammingDistance);
            command.Parameters.AddWithValue("@MinHammingDistanceFinal", context.Options.MinHammingDistanceFinal);
            command.Parameters.AddWithValue("@MonteCarloScenarioCount", context.Options.MonteCarloScenarioCount);
            command.Parameters.AddWithValue("@ThirdChoiceMinRatio", context.Options.ThirdChoiceMinRatio);
            command.Parameters.AddWithValue("@ProbabilityUniformBlend", context.Options.ProbabilityUniformBlend);
            command.Parameters.AddWithValue("@PatternScoreWeight", context.Options.PatternScoreWeight);
            command.Parameters.AddWithValue("@WinnerPatternWeight", context.Options.WinnerPatternWeight);
            command.Parameters.AddWithValue("@RecentPatternWeight", context.Options.RecentPatternWeight);
            command.Parameters.AddWithValue("@PreviousWeekPatternWeight", context.Options.PreviousWeekPatternWeight);
            command.Parameters.AddWithValue("@SurpriseBalanceWeight", context.Options.SurpriseBalanceWeight);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task InsertMatchMatrixAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            int runId,
            PredictionRunMatchMatrixRow row,
            CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand(
                """
                INSERT INTO PredictionRunMatchMatrix
                    (RunId, MatchOrder, HomeTeamName, AwayTeamName, P1, PX, P2, K1, KX, K2)
                VALUES
                    (@RunId, @MatchOrder, @HomeTeamName, @AwayTeamName, @P1, @PX, @P2, @K1, @KX, @K2);
                """,
                connection,
                transaction);

            command.Parameters.AddWithValue("@RunId", runId);
            command.Parameters.AddWithValue("@MatchOrder", row.MatchOrder);
            command.Parameters.AddWithValue("@HomeTeamName", (object?)row.HomeTeamName ?? DBNull.Value);
            command.Parameters.AddWithValue("@AwayTeamName", (object?)row.AwayTeamName ?? DBNull.Value);
            command.Parameters.AddWithValue("@P1", (object?)row.P1 ?? DBNull.Value);
            command.Parameters.AddWithValue("@PX", (object?)row.PX ?? DBNull.Value);
            command.Parameters.AddWithValue("@P2", (object?)row.P2 ?? DBNull.Value);
            command.Parameters.AddWithValue("@K1", row.K1);
            command.Parameters.AddWithValue("@KX", row.KX);
            command.Parameters.AddWithValue("@K2", row.K2);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task TryInsertRunResultAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            int runId,
            int? roundId,
            IReadOnlyList<Coupon> coupons,
            CancellationToken cancellationToken)
        {
            if (roundId == null)
            {
                return;
            }

            await using var actualCommand = new SqlCommand(
                "SELECT TOP (1) ResultLine FROM HistoricalResults WHERE RoundId = @RoundId AND ResultLine IS NOT NULL;",
                connection,
                transaction);
            actualCommand.CommandTimeout = 15;
            actualCommand.Parameters.AddWithValue("@RoundId", roundId.Value);
            var actual = Convert.ToString(await actualCommand.ExecuteScalarAsync(cancellationToken));
            if (string.IsNullOrWhiteSpace(actual) || actual.Length != 15)
            {
                return;
            }

            var hits = coupons
                .Select(x => CountHits(NormalizePrediction(x.prediction), actual))
                .ToList();
            if (hits.Count == 0)
            {
                return;
            }

            var summary = new PredictionRunEvaluationSummary(
                runId,
                roundId.Value,
                actual,
                hits.Max(),
                hits.Average(),
                hits.Count(x => x == 15),
                hits.Count(x => x == 14),
                hits.Count(x => x == 13),
                hits.Count(x => x == 12));

            await InsertRunResultAsync(connection, transaction, summary, cancellationToken);
        }

        private static async Task<IReadOnlyList<string>> LoadPredictionLinesAsync(
            SqlConnection connection,
            int runId,
            CancellationToken cancellationToken)
        {
            var result = new List<string>();
            await using var command = new SqlCommand(
                """
                SELECT PredictionLine
                FROM dbo.Predictions
                WHERE RunId = @RunId
                  AND PredictionLine IS NOT NULL
                  AND LEN(PredictionLine) = 15;
                """,
                connection);
            command.Parameters.AddWithValue("@RunId", runId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(reader.GetString(0));
            }

            return result;
        }

        private static async Task InsertRunResultAsync(
            SqlConnection connection,
            SqlTransaction? transaction,
            PredictionRunEvaluationSummary summary,
            CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand(
                """
                INSERT INTO PredictionRunResults
                    (RunId, RoundId, ActualResultLine, BestHitCount, AverageHitCount,
                     Hit15Count, Hit14Count, Hit13Count, Hit12Count)
                SELECT
                    @RunId, @RoundId, @ActualResultLine, @BestHitCount, @AverageHitCount,
                    @Hit15Count, @Hit14Count, @Hit13Count, @Hit12Count
                WHERE NOT EXISTS
                (
                    SELECT 1
                    FROM dbo.PredictionRunResults
                    WHERE RunId = @RunId
                );
                """,
                connection,
                transaction);
            command.CommandTimeout = 20;

            command.Parameters.AddWithValue("@RunId", summary.RunId);
            command.Parameters.AddWithValue("@RoundId", summary.RoundId);
            command.Parameters.AddWithValue("@ActualResultLine", summary.ActualResultLine);
            command.Parameters.AddWithValue("@BestHitCount", summary.BestHitCount);
            command.Parameters.AddWithValue("@AverageHitCount", summary.AverageHitCount);
            command.Parameters.AddWithValue("@Hit15Count", summary.Hit15Count);
            command.Parameters.AddWithValue("@Hit14Count", summary.Hit14Count);
            command.Parameters.AddWithValue("@Hit13Count", summary.Hit13Count);
            command.Parameters.AddWithValue("@Hit12Count", summary.Hit12Count);

            await command.ExecuteNonQueryAsync(cancellationToken);
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
                if (prediction[i] == actual[i])
                {
                    hits++;
                }
            }

            return hits;
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

        private static int ParseInt(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return 0;
            }

            var cleaned = new string(raw.Where(char.IsDigit).ToArray());
            return int.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
                ? value
                : 0;
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
    }

    public sealed record PredictionRunContext(
        int? RoundId,
        string? RoundName,
        int? NesineProgramNo,
        bool UsedNesinePopularity,
        bool UsedHeadToHead,
        bool UsedFeatureModel,
        bool UsedTeamEnsemble,
        int EnsembleCalibrationSampleCount,
        double? EnsembleEloWeight,
        double? EnsembleDixonColesWeight,
        double? EnsembleEloTemperature,
        double? EnsembleDixonColesTemperature,
        int EnsembleMatchCount,
        OptimizationOptions Options);

    public sealed record PredictionRunMatchMatrixRow(
        int MatchOrder,
        string? HomeTeamName,
        string? AwayTeamName,
        double? P1,
        double? PX,
        double? P2,
        int K1,
        int KX,
        int K2);

    public sealed record PredictionRunEvaluationSummary(
        int RunId,
        int RoundId,
        string ActualResultLine,
        int BestHitCount,
        double AverageHitCount,
        int Hit15Count,
        int Hit14Count,
        int Hit13Count,
        int Hit12Count);

    public sealed record PredictionRunExperimentConfiguration(
        int CouponCount,
        double ThirdChoiceMinRatio,
        double ProbabilityUniformBlend,
        double PatternScoreWeight,
        double WinnerPatternWeight,
        double RecentPatternWeight,
        double PreviousWeekPatternWeight,
        double SurpriseBalanceWeight,
        int MinHammingDistance,
        int MinHammingDistanceFinal,
        int MonteCarloScenarioCount);

    public sealed record PredictionParameterAuditRun(
        int RunId,
        int TotalRequested,
        string? Notes,
        DateTime CreatedAt,
        int RoundId,
        string ActualResultLine,
        int BestHitCount,
        double AverageHitCount,
        int Hit15Count,
        int Hit14Count,
        int Hit13Count,
        int Hit12Count,
        int I15Min,
        int I15Max,
        int InitialTopCandidateLimit,
        int DiversePrePoolLimit,
        int ApiBudgetMultiplier,
        int ApiConcurrency,
        int MinHammingDistance,
        int MinHammingDistanceFinal,
        int MonteCarloScenarioCount,
        double ThirdChoiceMinRatio,
        double ProbabilityUniformBlend,
        double PatternScoreWeight,
        double WinnerPatternWeight,
        double RecentPatternWeight,
        double PreviousWeekPatternWeight,
        double SurpriseBalanceWeight,
        bool UsedNesinePopularity,
        bool UsedHeadToHead,
        bool UsedFeatureModel,
        bool UsedTeamEnsemble,
        int EnsembleCalibrationSampleCount,
        double EnsembleEloWeight,
        double EnsembleDixonColesWeight,
        int EnsembleMatchCount)
    {
        public string ParameterSignature =>
            $"Kolon:{TotalRequested} | i15:{I15Min}-{I15Max} | " +
            $"TopK:{InitialTopCandidateLimit:n0} | Havuz:{DiversePrePoolLimit:n0} | " +
            $"ApiCarpan:{ApiBudgetMultiplier:n0} | Esz:{ApiConcurrency} | " +
            $"Dist:{MinHammingDistance}/{MinHammingDistanceFinal} | MC:{MonteCarloScenarioCount:n0} | " +
            $"Ucuncu:{ThirdChoiceMinRatio:F2} | Yum:{ProbabilityUniformBlend:F2} | " +
            $"Oruntu:{PatternScoreWeight:F2}/Kaz:{WinnerPatternWeight:F2}/Son:{RecentPatternWeight:F2}/Once:{PreviousWeekPatternWeight:F2}/Surp:{SurpriseBalanceWeight:F2}";
    }

    public sealed record CounterfactualBacktestTarget(
        int RoundId,
        string ActualResultLine,
        int SourceRunId,
        IReadOnlyList<SymbolProbabilities> MatchProbabilities);

    public sealed record CounterfactualBacktestRoundChoice(
        int RoundId,
        string ActualResultLine,
        int SourceRunId,
        string? RoundName,
        DateTime CreatedAt);

    public sealed record CounterfactualTriedOptionKey(
        int CouponCount,
        double ThirdChoiceMinRatio,
        double ProbabilityUniformBlend,
        double PatternScoreWeight);

    public sealed record CounterfactualSearchSeed(
        int SourceRoundId,
        bool FoundExact,
        int CouponCount,
        double ThirdChoiceMinRatio,
        double ProbabilityUniformBlend,
        double PatternScoreWeight,
        int BestHitCount,
        int Hit14Count,
        decimal NetProfitAmount,
        double Roi);

    public sealed record CounterfactualParameterAuditRow(
        string SourceName,
        int SourceRoundId,
        int SourceRunId,
        string ActualResultLine,
        int CouponCount,
        OptimizationOptions Options,
        int BestHitCount,
        double AverageHitCount,
        int Hit15Count,
        int Hit14Count,
        int Hit13Count,
        int Hit12Count,
        decimal CostAmount,
        decimal GrossPrizeAmount,
        decimal NetProfitAmount,
        double Roi,
        bool FoundExact,
        string? Notes,
        DateTime CreatedAt)
    {
        public string ParameterSignature =>
            $"Kolon:{CouponCount} | i15:{Options.MinI15WinnerCount}-{Options.MaxI15WinnerCount} | " +
            $"TopK:{Options.InitialTopCandidateLimit:n0} | Havuz:{Options.DiversePrePoolLimit:n0} | " +
            $"ApiCarpan:{Options.ApiBudgetMultiplier:n0} | Esz:{Options.ApiConcurrency} | " +
            $"Dist:{Options.MinHammingDistance}/{Options.MinHammingDistanceFinal} | MC:{Options.MonteCarloScenarioCount:n0} | " +
            $"Ucuncu:{Options.ThirdChoiceMinRatio:F4} | Yum:{Options.ProbabilityUniformBlend:F4} | " +
            $"Oruntu:{Options.PatternScoreWeight:F4}/Kaz:{Options.WinnerPatternWeight:F2}/Son:{Options.RecentPatternWeight:F2}/Once:{Options.PreviousWeekPatternWeight:F2}/Surp:{Options.SurpriseBalanceWeight:F2}";
    }

    public sealed record CounterfactualStabilityRow(
        string SourceName,
        int SourceRoundId,
        int SourceRunId,
        string ActualResultLine,
        int CouponCount,
        double ThirdChoiceMinRatio,
        double ProbabilityUniformBlend,
        double PatternScoreWeight,
        int BestHitCount,
        double AverageHitCount,
        int Hit15Count,
        int Hit14Count,
        int Hit13Count,
        int Hit12Count,
        decimal CostAmount,
        decimal GrossPrizeAmount,
        decimal NetProfitAmount,
        double Roi,
        bool FoundExact,
        DateTime CreatedAt)
    {
        public bool IsExact => FoundExact || Hit15Count > 0 || BestHitCount >= 15;
        public bool IsPositiveRoi => Roi > 0.0 || NetProfitAmount > 0m;
        public bool IsRobustSuccess => BestHitCount >= 14 && IsPositiveRoi;
    }

    public sealed record LearnedPredictionStrategyCandidate(
        Guid SearchBatchId,
        int SourceRoundId,
        int SourceRunId,
        string ActualResultLine,
        int CouponCount,
        OptimizationOptions Options,
        int BestHitCount,
        double AverageHitCount,
        int Hit15Count,
        int Hit14Count,
        int Hit13Count,
        int Hit12Count,
        decimal CostAmount,
        decimal GrossPrizeAmount,
        decimal NetProfitAmount,
        double Roi,
        bool FoundExact,
        string? Notes);

    public sealed record LearnedPredictionStrategyRecommendation(
        int CouponCount,
        OptimizationOptions Options,
        int SampleCount,
        int RoundCount,
        int ExactRoundCount,
        int RobustRoundCount,
        int PositiveRoiRoundCount,
        int TotalHit15Count,
        int MaxBestHit,
        double AverageBestHit,
        double AverageCouponHit,
        decimal TotalNetProfitAmount,
        double AverageRoi)
    {
        public string Summary =>
            $"ExactRound:{ExactRoundCount}/{RoundCount} | Run:{SampleCount} | " +
            $"RobustRound:{RobustRoundCount} | ROI+Round:{PositiveRoiRoundCount} | " +
            $"15Kolon:{TotalHit15Count} | Max:{MaxBestHit}/15 | " +
            $"AvgBest:{AverageBestHit:F2} | AvgKolon:{AverageCouponHit:F2} | " +
            $"Net:{TotalNetProfitAmount:n0} TL | ROI:{AverageRoi:P1}";
    }

    public sealed record RoundPayoutProfile(
        int RoundId,
        decimal Prize15,
        decimal Prize14,
        decimal Prize13,
        decimal Prize12)
    {
        public decimal GetPrizeForHit(int hitCount)
        {
            return hitCount switch
            {
                15 => Prize15,
                14 => Prize14,
                13 => Prize13,
                12 => Prize12,
                _ => 0m
            };
        }
    }

    internal sealed record PendingPredictionRun(
        int RunId,
        int? RoundId,
        string? RoundName,
        DateTime CreatedAt);

    internal sealed record ResolvedActualResult(
        int RoundId,
        string ActualResultLine);
}
