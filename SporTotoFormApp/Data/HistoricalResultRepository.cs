using Microsoft.Data.SqlClient;

namespace SporTotoFormApp.Data
{
    public sealed class HistoricalResultRepository
    {
        private const int MatchCount = 15;

        public async Task<int> SeedFromFileIfEmptyAsync(string appBaseDirectory, CancellationToken cancellationToken = default)
        {
            await EnsureSchemaAsync(cancellationToken);

            if (await CountAsync(cancellationToken) > 0)
            {
                return 0;
            }

            var filePath = FindHistoricalFile(appBaseDirectory);
            if (filePath == null || !File.Exists(filePath))
            {
                return 0;
            }

            var lines = await File.ReadAllLinesAsync(filePath, cancellationToken);
            var validRows = lines
                .Select(NormalizeResultLine)
                .Where(x => x != null)
                .Select(x => new HistoricalResultImport(null, x!, null, null, "Seeded from historical_results.txt", [], []))
                .DistinctBy(x => x.ResultLine, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return await InsertManyAsync(validRows, cancellationToken);
        }

        public async Task<int> ReplaceAllAsync(IEnumerable<HistoricalResultImport> results, CancellationToken cancellationToken = default)
        {
            await EnsureSchemaAsync(cancellationToken);

            var normalized = results
                .Select(x => x with { ResultLine = NormalizeResultLine(x.ResultLine) ?? string.Empty })
                .Where(x => x.ResultLine.Length == MatchCount)
                .DistinctBy(x => x.RoundId.HasValue ? $"R:{x.RoundId.Value}" : $"L:{x.ResultLine}")
                .ToList();

            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                var inserted = 0;
                foreach (var row in normalized)
                {
                    var historicalResultId = await InsertAsync(connection, transaction, row, cancellationToken);
                    if (historicalResultId > 0)
                    {
                        inserted++;
                        await InsertPayoutsAsync(connection, transaction, historicalResultId, row, cancellationToken);
                        await InsertMatchesAsync(connection, transaction, historicalResultId, row, cancellationToken);
                    }
                }

                await transaction.CommitAsync(cancellationToken);
                return inserted;
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }

        public List<string> GetAllResultLines()
        {
            var lines = new List<string>();

            using var connection = Database.CreateConnection();
            connection.Open();

            using var command = new SqlCommand(
                "SELECT ResultLine FROM HistoricalResults ORDER BY COALESCE(RoundId, Id);",
                connection);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var line = NormalizeResultLine(reader.GetString(0));
                if (line != null)
                {
                    lines.Add(line);
                }
            }

            return lines;
        }

        public List<HistoricalResultPatternRow> GetPatternRows()
        {
            var rows = new List<HistoricalResultPatternRow>();

            using var connection = Database.CreateConnection();
            connection.Open();

            using var command = new SqlCommand(
                """
                SELECT
                    hr.Id,
                    hr.RoundId,
                    hr.ResultLine,
                    hr.SeasonYear,
                    hr.WeekNumber,
                    hr.RoundName,
                    MAX(CASE WHEN p.HitCount = 15 THEN p.WinnerCount END) AS Hit15WinnerCount
                FROM HistoricalResults hr
                LEFT JOIN HistoricalResultPayouts p ON p.HistoricalResultId = hr.Id
                GROUP BY
                    hr.Id,
                    hr.RoundId,
                    hr.ResultLine,
                    hr.SeasonYear,
                    hr.WeekNumber,
                    hr.RoundName
                ORDER BY
                    COALESCE(hr.SeasonYear, 0),
                    COALESCE(hr.WeekNumber, 0),
                    COALESCE(hr.RoundId, hr.Id),
                    hr.Id;
                """,
                connection);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var resultLine = NormalizeResultLine(reader.GetString(2));
                if (resultLine == null)
                {
                    continue;
                }

                rows.Add(new HistoricalResultPatternRow(
                    reader.GetInt32(0),
                    reader.IsDBNull(1) ? null : reader.GetInt32(1),
                    resultLine,
                    reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6)));
            }

            return rows;
        }

        private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
        {
            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var sql = """
                IF COL_LENGTH('dbo.HistoricalResults', 'SeasonYear') IS NULL
                    ALTER TABLE dbo.HistoricalResults ADD SeasonYear INT NULL;

                IF COL_LENGTH('dbo.HistoricalResults', 'WeekNumber') IS NULL
                    ALTER TABLE dbo.HistoricalResults ADD WeekNumber INT NULL;

                IF COL_LENGTH('dbo.HistoricalResults', 'RoundName') IS NULL
                    ALTER TABLE dbo.HistoricalResults ADD RoundName NVARCHAR(100) NULL;

                IF OBJECT_ID('dbo.HistoricalResultPayouts', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.HistoricalResultPayouts
                    (
                        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_HistoricalResultPayouts PRIMARY KEY,
                        HistoricalResultId INT NOT NULL,
                        RoundId INT NULL,
                        HitCount INT NOT NULL,
                        WinnerCount INT NULL,
                        PrizeAmount DECIMAL(18,2) NULL,
                        PrizeAmountText NVARCHAR(100) NULL,
                        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_HistoricalResultPayouts_CreatedAt DEFAULT SYSDATETIME(),
                        CONSTRAINT FK_HistoricalResultPayouts_HistoricalResults
                            FOREIGN KEY (HistoricalResultId) REFERENCES dbo.HistoricalResults(Id)
                    );
                END;

                IF NOT EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE name = 'IX_HistoricalResultPayouts_RoundId_HitCount'
                      AND object_id = OBJECT_ID('dbo.HistoricalResultPayouts')
                )
                    CREATE INDEX IX_HistoricalResultPayouts_RoundId_HitCount
                        ON dbo.HistoricalResultPayouts (RoundId, HitCount);

                IF OBJECT_ID('dbo.Teams', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.Teams
                    (
                        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Teams PRIMARY KEY,
                        ExternalTeamId INT NULL,
                        ApiTeamId INT NULL,
                        Name NVARCHAR(200) NOT NULL,
                        ShortName NVARCHAR(50) NULL,
                        MediumName NVARCHAR(100) NULL,
                        CountryId INT NULL,
                        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_Teams_CreatedAt DEFAULT SYSDATETIME()
                    );
                END;

                IF NOT EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE name = 'UX_Teams_ApiTeamId'
                      AND object_id = OBJECT_ID('dbo.Teams')
                )
                    CREATE UNIQUE INDEX UX_Teams_ApiTeamId
                        ON dbo.Teams (ApiTeamId)
                        WHERE ApiTeamId IS NOT NULL;

                IF OBJECT_ID('dbo.HistoricalResultMatches', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.HistoricalResultMatches
                    (
                        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_HistoricalResultMatches PRIMARY KEY,
                        HistoricalResultId INT NOT NULL,
                        RoundId INT NULL,
                        MatchOrder INT NOT NULL,
                        ExternalMatchId INT NULL,
                        MatchDate DATETIME2 NULL,
                        HomeTeamId INT NULL,
                        AwayTeamId INT NULL,
                        HomeTeamName NVARCHAR(200) NULL,
                        AwayTeamName NVARCHAR(200) NULL,
                        TournamentId INT NULL,
                        TournamentName NVARCHAR(100) NULL,
                        StageId INT NULL,
                        StageName NVARCHAR(100) NULL,
                        LeagueRoundName NVARCHAR(100) NULL,
                        ResultSymbol CHAR(1) NOT NULL,
                        HomeScore INT NULL,
                        AwayScore INT NULL,
                        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_HistoricalResultMatches_CreatedAt DEFAULT SYSDATETIME(),
                        CONSTRAINT FK_HistoricalResultMatches_HistoricalResults
                            FOREIGN KEY (HistoricalResultId) REFERENCES dbo.HistoricalResults(Id),
                        CONSTRAINT FK_HistoricalResultMatches_HomeTeam
                            FOREIGN KEY (HomeTeamId) REFERENCES dbo.Teams(Id),
                        CONSTRAINT FK_HistoricalResultMatches_AwayTeam
                            FOREIGN KEY (AwayTeamId) REFERENCES dbo.Teams(Id)
                    );
                END;

                IF NOT EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE name = 'IX_HistoricalResultMatches_RoundId_MatchOrder'
                      AND object_id = OBJECT_ID('dbo.HistoricalResultMatches')
                )
                    CREATE INDEX IX_HistoricalResultMatches_RoundId_MatchOrder
                        ON dbo.HistoricalResultMatches (RoundId, MatchOrder);

                UPDATE dbo.HistoricalResults
                SET
                    SeasonYear = TRY_CONVERT(INT, LEFT(RoundName, 4)),
                    WeekNumber = TRY_CONVERT(
                        INT,
                        SUBSTRING(
                            RoundName,
                            CHARINDEX(' ', RoundName + ' ') + 1,
                            NULLIF(CHARINDEX('.', RoundName + '.', CHARINDEX(' ', RoundName + ' ') + 1), 0)
                                - CHARINDEX(' ', RoundName + ' ') - 1
                        )
                    )
                WHERE RoundName LIKE '[1-2][0-9][0-9][0-9]/[1-2][0-9][0-9][0-9] %. Hafta%'
                  AND (SeasonYear IS NULL OR WeekNumber IS NULL);
                """;

            await using var command = new SqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private async Task<int> CountAsync(CancellationToken cancellationToken)
        {
            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            await using var command = new SqlCommand("SELECT COUNT(1) FROM HistoricalResults;", connection);
            var count = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt32(count);
        }

        private async Task<int> InsertManyAsync(IEnumerable<HistoricalResultImport> rows, CancellationToken cancellationToken)
        {
            var inserted = 0;

            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            foreach (var row in rows)
            {
                var historicalResultId = await InsertAsync(connection, null, row, cancellationToken);
                if (historicalResultId > 0)
                {
                    inserted++;
                    await InsertPayoutsAsync(connection, null, historicalResultId, row, cancellationToken);
                    await InsertMatchesAsync(connection, null, historicalResultId, row, cancellationToken);
                }
            }

            return inserted;
        }

        private static async Task<int> InsertAsync(
            SqlConnection connection,
            SqlTransaction? transaction,
            HistoricalResultImport row,
            CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand(
                """
                DECLARE @ExistingId INT;

                SELECT TOP (1) @ExistingId = Id
                FROM HistoricalResults
                WHERE (@RoundId IS NOT NULL AND RoundId = @RoundId)
                   OR (@RoundId IS NULL AND ResultLine = @ResultLine);

                IF @ExistingId IS NULL
                BEGIN
                    INSERT INTO HistoricalResults (RoundId, ResultLine, SeasonYear, WeekNumber, RoundName)
                    OUTPUT INSERTED.Id
                    VALUES (@RoundId, @ResultLine, @SeasonYear, @WeekNumber, @RoundName);
                END
                ELSE
                BEGIN
                    UPDATE HistoricalResults
                    SET
                        ResultLine = @ResultLine,
                        SeasonYear = COALESCE(@SeasonYear, SeasonYear),
                        WeekNumber = COALESCE(@WeekNumber, WeekNumber),
                        RoundName = COALESCE(@RoundName, RoundName)
                    WHERE Id = @ExistingId;

                    SELECT @ExistingId;
                END
                """,
                connection,
                transaction);

            command.Parameters.AddWithValue("@RoundId", (object?)row.RoundId ?? DBNull.Value);
            command.Parameters.AddWithValue("@ResultLine", row.ResultLine);
            command.Parameters.AddWithValue("@SeasonYear", (object?)row.SeasonYear ?? DBNull.Value);
            command.Parameters.AddWithValue("@WeekNumber", (object?)row.WeekNumber ?? DBNull.Value);
            command.Parameters.AddWithValue("@RoundName", (object?)row.RoundName ?? DBNull.Value);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
        }

        private static async Task InsertPayoutsAsync(
            SqlConnection connection,
            SqlTransaction? transaction,
            int historicalResultId,
            HistoricalResultImport row,
            CancellationToken cancellationToken)
        {
            await using (var deleteCommand = new SqlCommand(
                "DELETE FROM HistoricalResultPayouts WHERE HistoricalResultId = @HistoricalResultId;",
                connection,
                transaction))
            {
                deleteCommand.Parameters.AddWithValue("@HistoricalResultId", historicalResultId);
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var payout in row.Payouts)
            {
                await using var command = new SqlCommand(
                    """
                    INSERT INTO HistoricalResultPayouts
                        (HistoricalResultId, RoundId, HitCount, WinnerCount, PrizeAmount, PrizeAmountText)
                    VALUES
                        (@HistoricalResultId, @RoundId, @HitCount, @WinnerCount, @PrizeAmount, @PrizeAmountText);
                    """,
                    connection,
                    transaction);

                command.Parameters.AddWithValue("@HistoricalResultId", historicalResultId);
                command.Parameters.AddWithValue("@RoundId", (object?)row.RoundId ?? DBNull.Value);
                command.Parameters.AddWithValue("@HitCount", payout.HitCount);
                command.Parameters.AddWithValue("@WinnerCount", (object?)payout.WinnerCount ?? DBNull.Value);
                command.Parameters.AddWithValue("@PrizeAmount", (object?)payout.PrizeAmount ?? DBNull.Value);
                command.Parameters.AddWithValue("@PrizeAmountText", (object?)payout.PrizeAmountText ?? DBNull.Value);

                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        private static async Task InsertMatchesAsync(
            SqlConnection connection,
            SqlTransaction? transaction,
            int historicalResultId,
            HistoricalResultImport row,
            CancellationToken cancellationToken)
        {
            await using (var deleteCommand = new SqlCommand(
                "DELETE FROM HistoricalResultMatches WHERE HistoricalResultId = @HistoricalResultId;",
                connection,
                transaction))
            {
                deleteCommand.Parameters.AddWithValue("@HistoricalResultId", historicalResultId);
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var match in row.Matches)
            {
                var homeTeamId = await UpsertTeamAsync(connection, transaction, match.HomeTeam, cancellationToken);
                var awayTeamId = await UpsertTeamAsync(connection, transaction, match.AwayTeam, cancellationToken);

                await using var command = new SqlCommand(
                    """
                    INSERT INTO HistoricalResultMatches
                        (HistoricalResultId, RoundId, MatchOrder, ExternalMatchId, MatchDate,
                         HomeTeamId, AwayTeamId, HomeTeamName, AwayTeamName,
                         TournamentId, TournamentName, StageId, StageName, LeagueRoundName,
                         ResultSymbol, HomeScore, AwayScore)
                    VALUES
                        (@HistoricalResultId, @RoundId, @MatchOrder, @ExternalMatchId, @MatchDate,
                         @HomeTeamId, @AwayTeamId, @HomeTeamName, @AwayTeamName,
                         @TournamentId, @TournamentName, @StageId, @StageName, @LeagueRoundName,
                         @ResultSymbol, @HomeScore, @AwayScore);
                    """,
                    connection,
                    transaction);

                command.Parameters.AddWithValue("@HistoricalResultId", historicalResultId);
                command.Parameters.AddWithValue("@RoundId", (object?)row.RoundId ?? DBNull.Value);
                command.Parameters.AddWithValue("@MatchOrder", match.MatchOrder);
                command.Parameters.AddWithValue("@ExternalMatchId", (object?)match.ExternalMatchId ?? DBNull.Value);
                command.Parameters.AddWithValue("@MatchDate", (object?)match.MatchDate ?? DBNull.Value);
                command.Parameters.AddWithValue("@HomeTeamId", (object?)homeTeamId ?? DBNull.Value);
                command.Parameters.AddWithValue("@AwayTeamId", (object?)awayTeamId ?? DBNull.Value);
                command.Parameters.AddWithValue("@HomeTeamName", (object?)match.HomeTeam.Name ?? DBNull.Value);
                command.Parameters.AddWithValue("@AwayTeamName", (object?)match.AwayTeam.Name ?? DBNull.Value);
                command.Parameters.AddWithValue("@TournamentId", (object?)match.TournamentId ?? DBNull.Value);
                command.Parameters.AddWithValue("@TournamentName", (object?)match.TournamentName ?? DBNull.Value);
                command.Parameters.AddWithValue("@StageId", (object?)match.StageId ?? DBNull.Value);
                command.Parameters.AddWithValue("@StageName", (object?)match.StageName ?? DBNull.Value);
                command.Parameters.AddWithValue("@LeagueRoundName", (object?)match.LeagueRoundName ?? DBNull.Value);
                command.Parameters.AddWithValue("@ResultSymbol", match.ResultSymbol.ToString());
                command.Parameters.AddWithValue("@HomeScore", (object?)match.HomeScore ?? DBNull.Value);
                command.Parameters.AddWithValue("@AwayScore", (object?)match.AwayScore ?? DBNull.Value);

                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        private static async Task<int?> UpsertTeamAsync(
            SqlConnection connection,
            SqlTransaction? transaction,
            HistoricalTeamImport team,
            CancellationToken cancellationToken)
        {
            if (team.ApiTeamId == null && string.IsNullOrWhiteSpace(team.Name))
            {
                return null;
            }

            await using var command = new SqlCommand(
                """
                DECLARE @TeamId INT;

                SELECT TOP (1) @TeamId = Id
                FROM Teams
                WHERE (@ApiTeamId IS NOT NULL AND ApiTeamId = @ApiTeamId)
                   OR (@ApiTeamId IS NULL AND Name = @Name);

                IF @TeamId IS NULL
                BEGIN
                    INSERT INTO Teams (ExternalTeamId, ApiTeamId, Name, ShortName, MediumName, CountryId)
                    OUTPUT INSERTED.Id
                    VALUES (@ExternalTeamId, @ApiTeamId, @Name, @ShortName, @MediumName, @CountryId);
                END
                ELSE
                BEGIN
                    UPDATE Teams
                    SET
                        ExternalTeamId = COALESCE(@ExternalTeamId, ExternalTeamId),
                        Name = COALESCE(@Name, Name),
                        ShortName = COALESCE(@ShortName, ShortName),
                        MediumName = COALESCE(@MediumName, MediumName),
                        CountryId = COALESCE(@CountryId, CountryId)
                    WHERE Id = @TeamId;

                    SELECT @TeamId;
                END
                """,
                connection,
                transaction);

            command.Parameters.AddWithValue("@ExternalTeamId", (object?)team.ExternalTeamId ?? DBNull.Value);
            command.Parameters.AddWithValue("@ApiTeamId", (object?)team.ApiTeamId ?? DBNull.Value);
            command.Parameters.AddWithValue("@Name", (object?)team.Name ?? DBNull.Value);
            command.Parameters.AddWithValue("@ShortName", (object?)team.ShortName ?? DBNull.Value);
            command.Parameters.AddWithValue("@MediumName", (object?)team.MediumName ?? DBNull.Value);
            command.Parameters.AddWithValue("@CountryId", (object?)team.CountryId ?? DBNull.Value);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result == null || result == DBNull.Value ? null : Convert.ToInt32(result);
        }

        private static string? NormalizeResultLine(string? resultLine)
        {
            if (string.IsNullOrWhiteSpace(resultLine))
            {
                return null;
            }

            var normalized = new string(resultLine
                .Where(c => !char.IsWhiteSpace(c))
                .Select(char.ToUpperInvariant)
                .ToArray());

            if (normalized.Length != MatchCount || normalized.Any(c => c is not ('1' or 'X' or '2')))
            {
                return null;
            }

            return normalized;
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
    }

    public sealed record HistoricalResultImport(
        int? RoundId,
        string ResultLine,
        int? SeasonYear,
        int? WeekNumber,
        string? RoundName,
        IReadOnlyList<HistoricalPrizeImport> Payouts,
        IReadOnlyList<HistoricalMatchImport> Matches);

    public sealed record HistoricalPrizeImport(
        int HitCount,
        int? WinnerCount,
        decimal? PrizeAmount,
        string? PrizeAmountText);

    public sealed record HistoricalMatchImport(
        int MatchOrder,
        int? ExternalMatchId,
        DateTime? MatchDate,
        HistoricalTeamImport HomeTeam,
        HistoricalTeamImport AwayTeam,
        int? TournamentId,
        string? TournamentName,
        int? StageId,
        string? StageName,
        string? LeagueRoundName,
        char ResultSymbol,
        int? HomeScore,
        int? AwayScore);

    public sealed record HistoricalTeamImport(
        int? ApiTeamId,
        int? ExternalTeamId,
        string? Name,
        string? ShortName,
        string? MediumName,
        int? CountryId);

    public sealed record HistoricalResultPatternRow(
        int Id,
        int? RoundId,
        string ResultLine,
        int? SeasonYear,
        int? WeekNumber,
        string? RoundName,
        int? Hit15WinnerCount);
}
