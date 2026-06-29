using Microsoft.Data.SqlClient;
using SporTotoFormApp.Object;
using SporTotoFormApp.Services;
using System.Globalization;

namespace SporTotoFormApp.Data
{
    public sealed class PredictionRepository
    {
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
            CancellationToken cancellationToken = default)
        {
            await EnsureSchemaAsync(cancellationToken);

            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var pendingRuns = new List<PendingPredictionRun>();
            await using (var command = new SqlCommand(
                """
                SELECT r.Id, info.RoundId, info.RoundName, r.CreatedAt
                FROM dbo.PredictionRuns r
                LEFT JOIN dbo.PredictionRunModelInfo info ON info.RunId = r.Id
                WHERE EXISTS
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
                connection))
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    pendingRuns.Add(new PendingPredictionRun(
                        reader.GetInt32(0),
                        reader.IsDBNull(1) ? null : reader.GetInt32(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.GetDateTime(3)));
                }
            }

            var result = new List<PredictionRunEvaluationSummary>();
            foreach (var pending in pendingRuns)
            {
                var actual = await TryResolveActualResultAsync(connection, pending, cancellationToken);
                if (actual == null)
                {
                    continue;
                }

                var predictions = await LoadPredictionLinesAsync(connection, pending.RunId, cancellationToken);
                if (predictions.Count == 0)
                {
                    continue;
                }

                var hits = predictions
                    .Select(x => CountHits(NormalizePrediction(x), actual.ActualResultLine))
                    .ToList();

                if (hits.Count == 0)
                {
                    continue;
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
                result.Add(summary);
            }

            return result;
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

        private static async Task<ResolvedActualResult?> TryResolveActualResultAsync(
            SqlConnection connection,
            PendingPredictionRun pending,
            CancellationToken cancellationToken)
        {
            var hasHistoricalMatches = await HasTableAsync(connection, "dbo.HistoricalResultMatches", cancellationToken);
            if (hasHistoricalMatches)
            {
                var byMatchMatrix = await TryResolveActualByMatchMatrixAsync(connection, pending.RunId, cancellationToken);
                if (byMatchMatrix != null)
                {
                    return byMatchMatrix;
                }
            }

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

            return null;
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
                """,
                connection);

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
                VALUES
                    (@RunId, @RoundId, @ActualResultLine, @BestHitCount, @AverageHitCount,
                     @Hit15Count, @Hit14Count, @Hit13Count, @Hit12Count);
                """,
                connection,
                transaction);

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

    internal sealed record PendingPredictionRun(
        int RunId,
        int? RoundId,
        string? RoundName,
        DateTime CreatedAt);

    internal sealed record ResolvedActualResult(
        int RoundId,
        string ActualResultLine);
}
