using Microsoft.Data.SqlClient;
using SporTotoFormApp.Services;

namespace SporTotoFormApp.Data
{
    public sealed class NesineProgramRepository
    {
        public async Task<int> SaveSnapshotAsync(
            CurrentRoundInfo currentRound,
            NesineProgram program,
            CancellationToken cancellationToken = default)
        {
            await EnsureSchemaAsync(cancellationToken);

            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var inserted = 0;
            foreach (var match in program.Matches.Values.OrderBy(x => x.MatchNo))
            {
                await using var command = new SqlCommand(
                    """
                    IF NOT EXISTS (
                        SELECT 1
                        FROM NesinePopularitySnapshots
                        WHERE RoundId = @RoundId
                          AND ProgramNo = @ProgramNo
                          AND MatchOrder = @MatchOrder
                          AND Percentage1 = @Percentage1
                          AND PercentageX = @PercentageX
                          AND Percentage2 = @Percentage2
                          AND CapturedAt >= DATEADD(MINUTE, -30, SYSDATETIME())
                    )
                    BEGIN
                        INSERT INTO NesinePopularitySnapshots
                            (RoundId, RoundName, ProgramNo, NesineWeek, ProgramEndDate,
                             MatchOrder, HomeTeamName, AwayTeamName,
                             Percentage1, PercentageX, Percentage2)
                        VALUES
                            (@RoundId, @RoundName, @ProgramNo, @NesineWeek, @ProgramEndDate,
                             @MatchOrder, @HomeTeamName, @AwayTeamName,
                             @Percentage1, @PercentageX, @Percentage2);
                    END
                    """,
                    connection);

                command.Parameters.AddWithValue("@RoundId", currentRound.RoundId);
                command.Parameters.AddWithValue("@RoundName", currentRound.RoundName);
                command.Parameters.AddWithValue("@ProgramNo", program.ProgramNo);
                command.Parameters.AddWithValue("@NesineWeek", program.Week);
                command.Parameters.AddWithValue("@ProgramEndDate", (object?)program.ProgramEndDate?.DateTime ?? DBNull.Value);
                command.Parameters.AddWithValue("@MatchOrder", match.MatchNo);
                command.Parameters.AddWithValue("@HomeTeamName", match.HomeTeam);
                command.Parameters.AddWithValue("@AwayTeamName", match.AwayTeam);
                command.Parameters.AddWithValue("@Percentage1", match.Percentage1);
                command.Parameters.AddWithValue("@PercentageX", match.PercentageX);
                command.Parameters.AddWithValue("@Percentage2", match.Percentage2);

                inserted += await command.ExecuteNonQueryAsync(cancellationToken);
            }

            return inserted;
        }

        private static async Task EnsureSchemaAsync(CancellationToken cancellationToken)
        {
            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            await using var command = new SqlCommand(
                """
                IF OBJECT_ID('dbo.NesinePopularitySnapshots', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.NesinePopularitySnapshots
                    (
                        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_NesinePopularitySnapshots PRIMARY KEY,
                        RoundId INT NOT NULL,
                        RoundName NVARCHAR(100) NULL,
                        ProgramNo INT NOT NULL,
                        NesineWeek INT NOT NULL,
                        ProgramEndDate DATETIME2 NULL,
                        MatchOrder INT NOT NULL,
                        HomeTeamName NVARCHAR(200) NOT NULL,
                        AwayTeamName NVARCHAR(200) NOT NULL,
                        Percentage1 INT NOT NULL,
                        PercentageX INT NOT NULL,
                        Percentage2 INT NOT NULL,
                        CapturedAt DATETIME2 NOT NULL CONSTRAINT DF_NesinePopularitySnapshots_CapturedAt DEFAULT SYSDATETIME()
                    );
                END;

                IF NOT EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE name = 'IX_NesinePopularitySnapshots_Round_Match_CapturedAt'
                      AND object_id = OBJECT_ID('dbo.NesinePopularitySnapshots')
                )
                    CREATE INDEX IX_NesinePopularitySnapshots_Round_Match_CapturedAt
                        ON dbo.NesinePopularitySnapshots (RoundId, MatchOrder, CapturedAt);
                """,
                connection);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
