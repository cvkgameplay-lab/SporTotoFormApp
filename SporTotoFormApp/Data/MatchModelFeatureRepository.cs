using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace SporTotoFormApp.Data
{
    public sealed class MatchModelFeatureRepository
    {
        public async Task<int> BuildAndSaveAsync(int roundId, CancellationToken cancellationToken = default)
        {
            await EnsureSchemaAsync(cancellationToken);

            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var snapshots = LoadLatestSnapshots(connection, roundId);
            var inserted = 0;

            foreach (var snapshot in snapshots)
            {
                var leagueTableJson = LoadLatestExtraJson(connection, roundId, snapshot.MatchOrder, "LeagueTable");
                var feature = BuildFeature(roundId, snapshot, leagueTableJson);
                inserted += await UpsertFeatureAsync(connection, feature, cancellationToken);
            }

            return inserted;
        }

        public IReadOnlyDictionary<int, MatchModelFeature> LoadForRound(int roundId)
        {
            using var connection = Database.CreateConnection();
            connection.Open();

            using var command = new SqlCommand(
                """
                SELECT MatchOrder, HomeFormScore, AwayFormScore, FormDiff,
                       HomeLeaguePosition, AwayLeaguePosition, LeaguePositionDiff,
                       HomeGoalDiff, AwayGoalDiff, GoalDiffDelta,
                       HomeMissingCount, AwayMissingCount, MissingDelta,
                       H2HHomeWinCount, H2HDrawCount, H2HAwayWinCount,
                       FeatureSignal
                FROM MatchModelFeatures
                WHERE RoundId = @RoundId;
                """,
                connection);

            command.Parameters.AddWithValue("@RoundId", roundId);

            var result = new Dictionary<int, MatchModelFeature>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var feature = new MatchModelFeature(
                    roundId,
                    reader.GetInt32(0),
                    ReadNullableDouble(reader, 1),
                    ReadNullableDouble(reader, 2),
                    ReadNullableDouble(reader, 3),
                    ReadNullableInt(reader, 4),
                    ReadNullableInt(reader, 5),
                    ReadNullableInt(reader, 6),
                    ReadNullableInt(reader, 7),
                    ReadNullableInt(reader, 8),
                    ReadNullableInt(reader, 9),
                    reader.GetInt32(10),
                    reader.GetInt32(11),
                    reader.GetInt32(12),
                    reader.GetInt32(13),
                    reader.GetInt32(14),
                    reader.GetInt32(15),
                    reader.GetDouble(16));

                result[feature.MatchOrder] = feature;
            }

            return result;
        }

        private static List<FeatureSnapshot> LoadLatestSnapshots(SqlConnection connection, int roundId)
        {
            using var command = new SqlCommand(
                """
                SELECT MatchOrder, HomeTeamName, AwayTeamName,
                       H2HHomeWinCount, H2HDrawCount, H2HAwayWinCount,
                       HomeMissingPlayerCount, AwayMissingPlayerCount
                FROM
                (
                    SELECT *,
                           ROW_NUMBER() OVER (PARTITION BY MatchOrder ORDER BY CapturedAt DESC, Id DESC) AS rn
                    FROM NesineHeadToHeadSnapshots
                    WHERE RoundId = @RoundId
                ) x
                WHERE rn = 1
                ORDER BY MatchOrder;
                """,
                connection);

            command.Parameters.AddWithValue("@RoundId", roundId);

            var result = new List<FeatureSnapshot>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new FeatureSnapshot(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5),
                    reader.GetInt32(6),
                    reader.GetInt32(7)));
            }

            return result;
        }

        private static string? LoadLatestExtraJson(SqlConnection connection, int roundId, int matchOrder, string endpointName)
        {
            using var command = new SqlCommand(
                """
                SELECT TOP (1) RawJson
                FROM NesineHeadToHeadExtraSnapshots
                WHERE RoundId = @RoundId
                  AND MatchOrder = @MatchOrder
                  AND EndpointName = @EndpointName
                  AND HasData = 1
                ORDER BY CapturedAt DESC, Id DESC;
                """,
                connection);

            command.Parameters.AddWithValue("@RoundId", roundId);
            command.Parameters.AddWithValue("@MatchOrder", matchOrder);
            command.Parameters.AddWithValue("@EndpointName", endpointName);

            var value = command.ExecuteScalar();
            return value == null || value == DBNull.Value ? null : Convert.ToString(value);
        }

        private static MatchModelFeature BuildFeature(int roundId, FeatureSnapshot snapshot, string? leagueTableJson)
        {
            var homeTable = TeamTableStats.Empty;
            var awayTable = TeamTableStats.Empty;

            if (!string.IsNullOrWhiteSpace(leagueTableJson))
            {
                using var document = JsonDocument.Parse(leagueTableJson);
                homeTable = FindTeamStats(document.RootElement, snapshot.HomeTeamName);
                awayTable = FindTeamStats(document.RootElement, snapshot.AwayTeamName);
            }

            var formDiff = NullableDiff(homeTable.FormScore, awayTable.FormScore);
            var leaguePositionDiff = NullableDiff(homeTable.Position, awayTable.Position);
            var goalDiffDelta = NullableDiff(homeTable.GoalDiff, awayTable.GoalDiff);
            var missingDelta = snapshot.HomeMissingCount - snapshot.AwayMissingCount;

            var signal = 0.0;
            if (formDiff.HasValue)
            {
                signal += formDiff.Value * 2.8;
            }

            if (leaguePositionDiff.HasValue)
            {
                signal += -leaguePositionDiff.Value * 1.7;
            }

            if (goalDiffDelta.HasValue)
            {
                signal += goalDiffDelta.Value * 0.18;
            }

            signal += -missingDelta * 1.3;
            signal = Math.Clamp(signal, -35.0, 35.0);

            return new MatchModelFeature(
                roundId,
                snapshot.MatchOrder,
                homeTable.FormScore,
                awayTable.FormScore,
                formDiff,
                homeTable.Position,
                awayTable.Position,
                leaguePositionDiff,
                homeTable.GoalDiff,
                awayTable.GoalDiff,
                goalDiffDelta,
                snapshot.HomeMissingCount,
                snapshot.AwayMissingCount,
                missingDelta,
                snapshot.H2HHomeWinCount,
                snapshot.H2HDrawCount,
                snapshot.H2HAwayWinCount,
                signal);
        }

        private static TeamTableStats FindTeamStats(JsonElement root, string teamName)
        {
            foreach (var element in EnumerateObjects(root))
            {
                if (!TryGetString(element, "N", out var name) ||
                    !IsSameTeamName(name, teamName) ||
                    !TryGetInt(element, "PST", out var position))
                {
                    continue;
                }

                var goalDiff = TryGetInt(element, "AD", out var ad) ? ad : null as int?;
                var formScore = ExtractFormScore(element);
                return new TeamTableStats(position, goalDiff, formScore);
            }

            return TeamTableStats.Empty;
        }

        private static double? ExtractFormScore(JsonElement teamElement)
        {
            if (!teamElement.TryGetProperty("TT", out var tt) || tt.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var scores = new List<int>();
            foreach (var item in tt.EnumerateArray())
            {
                if (!TryGetString(item, "FS", out var form) || form.Equals("Pre", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                scores.Add(form.ToUpperInvariant() switch
                {
                    "W" => 3,
                    "D" => 1,
                    "L" => 0,
                    _ => 0
                });
            }

            return scores.Count == 0 ? null : scores.TakeLast(5).Average();
        }

        private static async Task<int> UpsertFeatureAsync(
            SqlConnection connection,
            MatchModelFeature feature,
            CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand(
                """
                MERGE MatchModelFeatures AS target
                USING (SELECT @RoundId AS RoundId, @MatchOrder AS MatchOrder) AS source
                    ON target.RoundId = source.RoundId AND target.MatchOrder = source.MatchOrder
                WHEN MATCHED THEN
                    UPDATE SET
                        HomeFormScore = @HomeFormScore,
                        AwayFormScore = @AwayFormScore,
                        FormDiff = @FormDiff,
                        HomeLeaguePosition = @HomeLeaguePosition,
                        AwayLeaguePosition = @AwayLeaguePosition,
                        LeaguePositionDiff = @LeaguePositionDiff,
                        HomeGoalDiff = @HomeGoalDiff,
                        AwayGoalDiff = @AwayGoalDiff,
                        GoalDiffDelta = @GoalDiffDelta,
                        HomeMissingCount = @HomeMissingCount,
                        AwayMissingCount = @AwayMissingCount,
                        MissingDelta = @MissingDelta,
                        H2HHomeWinCount = @H2HHomeWinCount,
                        H2HDrawCount = @H2HDrawCount,
                        H2HAwayWinCount = @H2HAwayWinCount,
                        FeatureSignal = @FeatureSignal,
                        UpdatedAt = SYSDATETIME()
                WHEN NOT MATCHED THEN
                    INSERT
                        (RoundId, MatchOrder, HomeFormScore, AwayFormScore, FormDiff,
                         HomeLeaguePosition, AwayLeaguePosition, LeaguePositionDiff,
                         HomeGoalDiff, AwayGoalDiff, GoalDiffDelta,
                         HomeMissingCount, AwayMissingCount, MissingDelta,
                         H2HHomeWinCount, H2HDrawCount, H2HAwayWinCount, FeatureSignal)
                    VALUES
                        (@RoundId, @MatchOrder, @HomeFormScore, @AwayFormScore, @FormDiff,
                         @HomeLeaguePosition, @AwayLeaguePosition, @LeaguePositionDiff,
                         @HomeGoalDiff, @AwayGoalDiff, @GoalDiffDelta,
                         @HomeMissingCount, @AwayMissingCount, @MissingDelta,
                         @H2HHomeWinCount, @H2HDrawCount, @H2HAwayWinCount, @FeatureSignal);
                """,
                connection);

            command.Parameters.AddWithValue("@RoundId", feature.RoundId);
            command.Parameters.AddWithValue("@MatchOrder", feature.MatchOrder);
            command.Parameters.AddWithValue("@HomeFormScore", (object?)feature.HomeFormScore ?? DBNull.Value);
            command.Parameters.AddWithValue("@AwayFormScore", (object?)feature.AwayFormScore ?? DBNull.Value);
            command.Parameters.AddWithValue("@FormDiff", (object?)feature.FormDiff ?? DBNull.Value);
            command.Parameters.AddWithValue("@HomeLeaguePosition", (object?)feature.HomeLeaguePosition ?? DBNull.Value);
            command.Parameters.AddWithValue("@AwayLeaguePosition", (object?)feature.AwayLeaguePosition ?? DBNull.Value);
            command.Parameters.AddWithValue("@LeaguePositionDiff", (object?)feature.LeaguePositionDiff ?? DBNull.Value);
            command.Parameters.AddWithValue("@HomeGoalDiff", (object?)feature.HomeGoalDiff ?? DBNull.Value);
            command.Parameters.AddWithValue("@AwayGoalDiff", (object?)feature.AwayGoalDiff ?? DBNull.Value);
            command.Parameters.AddWithValue("@GoalDiffDelta", (object?)feature.GoalDiffDelta ?? DBNull.Value);
            command.Parameters.AddWithValue("@HomeMissingCount", feature.HomeMissingCount);
            command.Parameters.AddWithValue("@AwayMissingCount", feature.AwayMissingCount);
            command.Parameters.AddWithValue("@MissingDelta", feature.MissingDelta);
            command.Parameters.AddWithValue("@H2HHomeWinCount", feature.H2HHomeWinCount);
            command.Parameters.AddWithValue("@H2HDrawCount", feature.H2HDrawCount);
            command.Parameters.AddWithValue("@H2HAwayWinCount", feature.H2HAwayWinCount);
            command.Parameters.AddWithValue("@FeatureSignal", feature.FeatureSignal);

            return await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task EnsureSchemaAsync(CancellationToken cancellationToken)
        {
            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            await using var command = new SqlCommand(
                """
                IF OBJECT_ID('dbo.MatchModelFeatures', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.MatchModelFeatures
                    (
                        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_MatchModelFeatures PRIMARY KEY,
                        RoundId INT NOT NULL,
                        MatchOrder INT NOT NULL,
                        HomeFormScore FLOAT NULL,
                        AwayFormScore FLOAT NULL,
                        FormDiff FLOAT NULL,
                        HomeLeaguePosition INT NULL,
                        AwayLeaguePosition INT NULL,
                        LeaguePositionDiff INT NULL,
                        HomeGoalDiff INT NULL,
                        AwayGoalDiff INT NULL,
                        GoalDiffDelta INT NULL,
                        HomeMissingCount INT NOT NULL,
                        AwayMissingCount INT NOT NULL,
                        MissingDelta INT NOT NULL,
                        H2HHomeWinCount INT NOT NULL,
                        H2HDrawCount INT NOT NULL,
                        H2HAwayWinCount INT NOT NULL,
                        FeatureSignal FLOAT NOT NULL,
                        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_MatchModelFeatures_CreatedAt DEFAULT SYSDATETIME(),
                        UpdatedAt DATETIME2 NULL
                    );

                    CREATE UNIQUE INDEX UX_MatchModelFeatures_Round_Match
                        ON dbo.MatchModelFeatures (RoundId, MatchOrder);
                END;
                """,
                connection);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static IEnumerable<JsonElement> EnumerateObjects(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                yield return element;
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var child in EnumerateObjects(property.Value))
                    {
                        yield return child;
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var child in EnumerateObjects(item))
                    {
                        yield return child;
                    }
                }
            }
        }

        private static bool IsSameTeamName(string left, string right)
        {
            return NormalizeTeamName(left).Equals(NormalizeTeamName(right), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeTeamName(string value)
        {
            return new string(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        }

        private static bool TryGetString(JsonElement element, string propertyName, out string value)
        {
            value = string.Empty;
            if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            value = property.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }

        private static bool TryGetInt(JsonElement element, string propertyName, out int value)
        {
            value = 0;
            if (!element.TryGetProperty(propertyName, out var property))
            {
                return false;
            }

            return property.ValueKind switch
            {
                JsonValueKind.Number => property.TryGetInt32(out value),
                JsonValueKind.String => int.TryParse(property.GetString(), out value),
                _ => false
            };
        }

        private static double? NullableDiff(double? left, double? right)
        {
            return left.HasValue && right.HasValue ? left.Value - right.Value : null;
        }

        private static int? NullableDiff(int? left, int? right)
        {
            return left.HasValue && right.HasValue ? left.Value - right.Value : null;
        }

        private static double? ReadNullableDouble(SqlDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
        }

        private static int? ReadNullableInt(SqlDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
        }

        private sealed record FeatureSnapshot(
            int MatchOrder,
            string HomeTeamName,
            string AwayTeamName,
            int H2HHomeWinCount,
            int H2HDrawCount,
            int H2HAwayWinCount,
            int HomeMissingCount,
            int AwayMissingCount);

        private sealed record TeamTableStats(int? Position, int? GoalDiff, double? FormScore)
        {
            public static TeamTableStats Empty { get; } = new(null, null, null);
        }
    }

    public sealed record MatchModelFeature(
        int RoundId,
        int MatchOrder,
        double? HomeFormScore,
        double? AwayFormScore,
        double? FormDiff,
        int? HomeLeaguePosition,
        int? AwayLeaguePosition,
        int? LeaguePositionDiff,
        int? HomeGoalDiff,
        int? AwayGoalDiff,
        int? GoalDiffDelta,
        int HomeMissingCount,
        int AwayMissingCount,
        int MissingDelta,
        int H2HHomeWinCount,
        int H2HDrawCount,
        int H2HAwayWinCount,
        double FeatureSignal);
}
