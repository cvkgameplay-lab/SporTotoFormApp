using Microsoft.Data.SqlClient;
using SporTotoFormApp.Services;

namespace SporTotoFormApp.Data
{
    public sealed class NesineHeadToHeadRepository
    {
        public async Task<int> SaveSnapshotsAsync(
            CurrentRoundInfo currentRound,
            NesineProgram program,
            IReadOnlyDictionary<int, NesineHeadToHeadSummary> summariesByMatchNo,
            IReadOnlyDictionary<int, IReadOnlyList<NesineHeadToHeadExtraSnapshot>>? extraSnapshotsByMatchNo = null,
            CancellationToken cancellationToken = default)
        {
            await EnsureSchemaAsync(cancellationToken);

            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var inserted = 0;
            foreach (var match in program.Matches.Values.OrderBy(x => x.MatchNo))
            {
                if (!summariesByMatchNo.TryGetValue(match.MatchNo, out var summary))
                {
                    if (extraSnapshotsByMatchNo != null &&
                        extraSnapshotsByMatchNo.TryGetValue(match.MatchNo, out var extrasOnly))
                    {
                        inserted += await SaveExtraSnapshotsAsync(
                            connection,
                            currentRound,
                            match,
                            extrasOnly,
                            cancellationToken);
                    }

                    continue;
                }

                await using var command = new SqlCommand(
                    """
                    IF NOT EXISTS (
                        SELECT 1
                        FROM NesineHeadToHeadSnapshots
                        WHERE RoundId = @RoundId
                          AND MatchOrder = @MatchOrder
                          AND BahisKod = @BahisKod
                          AND CapturedAt >= DATEADD(HOUR, -6, SYSDATETIME())
                    )
                    BEGIN
                        INSERT INTO NesineHeadToHeadSnapshots
                            (RoundId, RoundName, MatchOrder, BahisKod,
                             HomeTeamName, AwayTeamName,
                             H2HHomeWinCount, H2HDrawCount, H2HAwayWinCount,
                             HomeOdd, DrawOdd, AwayOdd,
                             HomeMissingPlayerCount, AwayMissingPlayerCount,
                             RawJson)
                        VALUES
                            (@RoundId, @RoundName, @MatchOrder, @BahisKod,
                             @HomeTeamName, @AwayTeamName,
                             @H2HHomeWinCount, @H2HDrawCount, @H2HAwayWinCount,
                             @HomeOdd, @DrawOdd, @AwayOdd,
                             @HomeMissingPlayerCount, @AwayMissingPlayerCount,
                             @RawJson);
                        SELECT 1;
                    END
                    ELSE
                        SELECT 0;
                    """,
                    connection);

                command.Parameters.AddWithValue("@RoundId", currentRound.RoundId);
                command.Parameters.AddWithValue("@RoundName", currentRound.RoundName);
                command.Parameters.AddWithValue("@MatchOrder", match.MatchNo);
                command.Parameters.AddWithValue("@BahisKod", summary.BahisKod);
                command.Parameters.AddWithValue("@HomeTeamName", match.HomeTeam);
                command.Parameters.AddWithValue("@AwayTeamName", match.AwayTeam);
                command.Parameters.AddWithValue("@H2HHomeWinCount", summary.H2HHomeWinCount);
                command.Parameters.AddWithValue("@H2HDrawCount", summary.H2HDrawCount);
                command.Parameters.AddWithValue("@H2HAwayWinCount", summary.H2HAwayWinCount);
                command.Parameters.AddWithValue("@HomeOdd", (object?)summary.HomeOdd ?? DBNull.Value);
                command.Parameters.AddWithValue("@DrawOdd", (object?)summary.DrawOdd ?? DBNull.Value);
                command.Parameters.AddWithValue("@AwayOdd", (object?)summary.AwayOdd ?? DBNull.Value);
                command.Parameters.AddWithValue("@HomeMissingPlayerCount", summary.HomeMissingPlayerCount);
                command.Parameters.AddWithValue("@AwayMissingPlayerCount", summary.AwayMissingPlayerCount);
                command.Parameters.AddWithValue("@RawJson", summary.RawJson);

                inserted += Convert.ToInt32(
                    await command.ExecuteScalarAsync(cancellationToken) ?? 0);

                if (extraSnapshotsByMatchNo != null &&
                    extraSnapshotsByMatchNo.TryGetValue(match.MatchNo, out var extras))
                {
                    inserted += await SaveExtraSnapshotsAsync(
                        connection,
                        currentRound,
                        match,
                        extras,
                        cancellationToken);
                }
            }

            return inserted;
        }

        private static async Task<int> SaveExtraSnapshotsAsync(
            SqlConnection connection,
            CurrentRoundInfo currentRound,
            NesineMatchPopularity match,
            IReadOnlyList<NesineHeadToHeadExtraSnapshot> snapshots,
            CancellationToken cancellationToken)
        {
            var inserted = 0;
            foreach (var snapshot in snapshots)
            {
                await using var command = new SqlCommand(
                    """
                    IF NOT EXISTS (
                        SELECT 1
                        FROM NesineHeadToHeadExtraSnapshots
                        WHERE RoundId = @RoundId
                          AND MatchOrder = @MatchOrder
                          AND BahisKod = @BahisKod
                          AND EndpointName = @EndpointName
                          AND CapturedAt >= DATEADD(HOUR, -6, SYSDATETIME())
                    )
                    BEGIN
                        INSERT INTO NesineHeadToHeadExtraSnapshots
                            (RoundId, RoundName, MatchOrder, BahisKod,
                             HomeTeamName, AwayTeamName,
                             EndpointName, ApiVersion, StatusCode, HasData, RawJson)
                        VALUES
                            (@RoundId, @RoundName, @MatchOrder, @BahisKod,
                             @HomeTeamName, @AwayTeamName,
                             @EndpointName, @ApiVersion, @StatusCode, @HasData, @RawJson);
                        SELECT 1;
                    END
                    ELSE
                        SELECT 0;
                    """,
                    connection);

                command.Parameters.AddWithValue("@RoundId", currentRound.RoundId);
                command.Parameters.AddWithValue("@RoundName", currentRound.RoundName);
                command.Parameters.AddWithValue("@MatchOrder", match.MatchNo);
                command.Parameters.AddWithValue("@BahisKod", snapshot.BahisKod);
                command.Parameters.AddWithValue("@HomeTeamName", match.HomeTeam);
                command.Parameters.AddWithValue("@AwayTeamName", match.AwayTeam);
                command.Parameters.AddWithValue("@EndpointName", snapshot.EndpointName);
                command.Parameters.AddWithValue("@ApiVersion", snapshot.ApiVersion);
                command.Parameters.AddWithValue("@StatusCode", snapshot.StatusCode);
                command.Parameters.AddWithValue("@HasData", snapshot.HasData);
                command.Parameters.AddWithValue("@RawJson", (object?)snapshot.RawJson ?? DBNull.Value);

                inserted += Convert.ToInt32(
                    await command.ExecuteScalarAsync(cancellationToken) ?? 0);
            }

            return inserted;
        }

        private static async Task EnsureSchemaAsync(CancellationToken cancellationToken)
        {
            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            await using var command = new SqlCommand(
                """
                IF OBJECT_ID('dbo.NesineHeadToHeadSnapshots', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.NesineHeadToHeadSnapshots
                    (
                        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_NesineHeadToHeadSnapshots PRIMARY KEY,
                        RoundId INT NOT NULL,
                        RoundName NVARCHAR(100) NULL,
                        MatchOrder INT NOT NULL,
                        BahisKod INT NOT NULL,
                        HomeTeamName NVARCHAR(200) NOT NULL,
                        AwayTeamName NVARCHAR(200) NOT NULL,
                        H2HHomeWinCount INT NOT NULL,
                        H2HDrawCount INT NOT NULL,
                        H2HAwayWinCount INT NOT NULL,
                        HomeOdd DECIMAL(10,2) NULL,
                        DrawOdd DECIMAL(10,2) NULL,
                        AwayOdd DECIMAL(10,2) NULL,
                        HomeMissingPlayerCount INT NOT NULL,
                        AwayMissingPlayerCount INT NOT NULL,
                        RawJson NVARCHAR(MAX) NOT NULL,
                        CapturedAt DATETIME2 NOT NULL CONSTRAINT DF_NesineHeadToHeadSnapshots_CapturedAt DEFAULT SYSDATETIME()
                    );
                END;

                IF NOT EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE name = 'IX_NesineHeadToHeadSnapshots_Round_Match_CapturedAt'
                      AND object_id = OBJECT_ID('dbo.NesineHeadToHeadSnapshots')
                )
                    CREATE INDEX IX_NesineHeadToHeadSnapshots_Round_Match_CapturedAt
                        ON dbo.NesineHeadToHeadSnapshots (RoundId, MatchOrder, CapturedAt);

                IF OBJECT_ID('dbo.NesineHeadToHeadExtraSnapshots', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.NesineHeadToHeadExtraSnapshots
                    (
                        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_NesineHeadToHeadExtraSnapshots PRIMARY KEY,
                        RoundId INT NOT NULL,
                        RoundName NVARCHAR(100) NULL,
                        MatchOrder INT NOT NULL,
                        BahisKod INT NOT NULL,
                        HomeTeamName NVARCHAR(200) NOT NULL,
                        AwayTeamName NVARCHAR(200) NOT NULL,
                        EndpointName NVARCHAR(50) NOT NULL,
                        ApiVersion NVARCHAR(10) NOT NULL,
                        StatusCode INT NOT NULL,
                        HasData BIT NOT NULL,
                        RawJson NVARCHAR(MAX) NULL,
                        CapturedAt DATETIME2 NOT NULL CONSTRAINT DF_NesineHeadToHeadExtraSnapshots_CapturedAt DEFAULT SYSDATETIME()
                    );
                END;

                IF NOT EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE name = 'IX_NesineHeadToHeadExtraSnapshots_Round_Match_Endpoint'
                      AND object_id = OBJECT_ID('dbo.NesineHeadToHeadExtraSnapshots')
                )
                    CREATE INDEX IX_NesineHeadToHeadExtraSnapshots_Round_Match_Endpoint
                        ON dbo.NesineHeadToHeadExtraSnapshots (RoundId, MatchOrder, EndpointName, CapturedAt);
                """,
                connection);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
