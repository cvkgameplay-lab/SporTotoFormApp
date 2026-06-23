using Microsoft.Data.SqlClient;
using SporTotoFormApp.Services;

namespace SporTotoFormApp.Data
{
    public sealed class NesineTeamMatchRepository
    {
        public async Task<IReadOnlySet<int>> GetTeamIdsNeedingRefreshAsync(
            IEnumerable<int> teamIds,
            TimeSpan maximumAge,
            CancellationToken cancellationToken = default)
        {
            await EnsureSchemaAsync(cancellationToken);

            var requestedIds = teamIds.Where(x => x > 0).Distinct().ToHashSet();
            if (requestedIds.Count == 0)
            {
                return requestedIds;
            }

            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var parameters = requestedIds
                .Select((_, index) => $"@TeamId{index}")
                .ToList();

            await using var command = new SqlCommand(
                $"""
                SELECT RequestedTeamId
                FROM NesineTeamMatchFetches
                WHERE RequestedTeamId IN ({string.Join(", ", parameters)})
                GROUP BY RequestedTeamId
                HAVING MAX(CapturedAt) >= DATEADD(MINUTE, -@MaximumAgeMinutes, SYSDATETIME());
                """,
                connection);

            var index = 0;
            foreach (var teamId in requestedIds)
            {
                command.Parameters.AddWithValue($"@TeamId{index++}", teamId);
            }

            command.Parameters.AddWithValue(
                "@MaximumAgeMinutes",
                Math.Max(1, (int)Math.Ceiling(maximumAge.TotalMinutes)));

            var recentlyFetched = new HashSet<int>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                recentlyFetched.Add(reader.GetInt32(0));
            }

            requestedIds.ExceptWith(recentlyFetched);
            return requestedIds;
        }

        public async Task<NesineTeamMatchSaveResult> SaveFeedsAsync(
            CurrentRoundInfo currentRound,
            IReadOnlyDictionary<int, int> matchOrderByTeamId,
            IReadOnlyList<NesineTeamMatchFeed> feeds,
            CancellationToken cancellationToken = default)
        {
            await EnsureSchemaAsync(cancellationToken);

            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

            var savedFetches = 0;
            var savedMatches = 0;
            var completedMatches = 0;

            try
            {
                foreach (var feed in feeds)
                {
                    foreach (var team in feed.Matches
                                 .SelectMany(x => new[] { x.HomeTeam, x.AwayTeam })
                                 .Append(feed.RequestedTeam)
                                 .Where(x => x != null)
                                 .Cast<NesineTeamIdentity>()
                                 .DistinctBy(x => x.TeamId))
                    {
                        await UpsertTeamAsync(connection, transaction, team, cancellationToken);
                    }

                    foreach (var competition in feed.Matches
                                 .Select(x => x.Competition)
                                 .Where(x => x != null)
                                 .Cast<NesineCompetitionIdentity>()
                                 .DistinctBy(x => x.CompetitionId))
                    {
                        await UpsertCompetitionAsync(connection, transaction, competition, cancellationToken);
                    }

                    foreach (var match in feed.Matches)
                    {
                        savedMatches += await UpsertMatchAsync(
                            connection,
                            transaction,
                            match,
                            cancellationToken);

                        if (match.IsCompleted)
                        {
                            completedMatches++;
                        }
                    }

                    savedFetches += await InsertFetchAsync(
                        connection,
                        transaction,
                        currentRound,
                        matchOrderByTeamId.TryGetValue(feed.RequestedTeamId, out var matchOrder)
                            ? matchOrder
                            : null,
                        feed,
                        cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
                return new NesineTeamMatchSaveResult(
                    savedFetches,
                    savedMatches,
                    completedMatches);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }

        public async Task<NesineTeamMatchDataQuality> GetDataQualityAsync(
            CancellationToken cancellationToken = default)
        {
            await EnsureSchemaAsync(cancellationToken);

            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(
                """
                SELECT
                    (SELECT COUNT(*) FROM NesineTeams),
                    (SELECT COUNT(*) FROM NesineCompetitions),
                    COUNT(*),
                    SUM(CASE WHEN IsCompleted = 1 THEN 1 ELSE 0 END),
                    SUM(CASE WHEN IsCompleted = 1 AND MatchDate IS NOT NULL THEN 1 ELSE 0 END),
                    MIN(CASE WHEN IsCompleted = 1 THEN MatchDate END),
                    MAX(CASE WHEN IsCompleted = 1 THEN MatchDate END)
                FROM NesineTeamMatches;
                """,
                connection);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return new NesineTeamMatchDataQuality(0, 0, 0, 0, 0, null, null);
            }

            return new NesineTeamMatchDataQuality(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                reader.IsDBNull(6) ? null : reader.GetDateTime(6));
        }

        public async Task<IReadOnlyList<NesineCompletedMatch>> LoadCompletedMatchesBeforeAsync(
            DateTime cutoff,
            CancellationToken cancellationToken = default)
        {
            await EnsureSchemaAsync(cancellationToken);

            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(
                """
                SELECT MatchId, MatchDate, HomeTeamId, AwayTeamId, CompetitionId,
                       HomeScore, AwayScore, IsNeutral
                FROM NesineTeamMatches
                WHERE IsCompleted = 1
                  AND MatchDate IS NOT NULL
                  AND MatchDate < @Cutoff
                ORDER BY MatchDate, MatchId;
                """,
                connection);
            command.Parameters.AddWithValue("@Cutoff", cutoff);

            var result = new List<NesineCompletedMatch>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new NesineCompletedMatch(
                    reader.GetInt64(0),
                    reader.GetDateTime(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    reader.GetInt32(5),
                    reader.GetInt32(6),
                    reader.GetBoolean(7)));
            }

            return result;
        }

        private static async Task UpsertTeamAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            NesineTeamIdentity team,
            CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand(
                """
                UPDATE NesineTeams
                SET Name = @Name,
                    ShortName = @ShortName,
                    Abbreviation = @Abbreviation,
                    LastSeenAt = SYSDATETIME()
                WHERE TeamId = @TeamId;

                IF @@ROWCOUNT = 0
                BEGIN
                    INSERT INTO NesineTeams
                        (TeamId, Name, ShortName, Abbreviation)
                    VALUES
                        (@TeamId, @Name, @ShortName, @Abbreviation);
                END;
                """,
                connection,
                transaction);

            command.Parameters.AddWithValue("@TeamId", team.TeamId);
            command.Parameters.AddWithValue("@Name", team.Name);
            command.Parameters.AddWithValue("@ShortName", (object?)team.ShortName ?? DBNull.Value);
            command.Parameters.AddWithValue("@Abbreviation", (object?)team.Abbreviation ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task UpsertCompetitionAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            NesineCompetitionIdentity competition,
            CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand(
                """
                UPDATE NesineCompetitions
                SET Name = COALESCE(@Name, Name),
                    Abbreviation = COALESCE(@Abbreviation, Abbreviation),
                    LastSeenAt = SYSDATETIME()
                WHERE CompetitionId = @CompetitionId;

                IF @@ROWCOUNT = 0
                BEGIN
                    INSERT INTO NesineCompetitions
                        (CompetitionId, Name, Abbreviation)
                    VALUES
                        (@CompetitionId, @Name, @Abbreviation);
                END;
                """,
                connection,
                transaction);

            command.Parameters.AddWithValue("@CompetitionId", competition.CompetitionId);
            command.Parameters.AddWithValue("@Name", (object?)competition.Name ?? DBNull.Value);
            command.Parameters.AddWithValue("@Abbreviation", (object?)competition.Abbreviation ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task<int> UpsertMatchAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            NesineTeamMatch match,
            CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand(
                """
                UPDATE NesineTeamMatches
                SET BettingId = COALESCE(@BettingId, BettingId),
                    SportId = COALESCE(@SportId, SportId),
                    MatchDate = COALESCE(@MatchDate, MatchDate),
                    Season = COALESCE(@Season, Season),
                    RoundName = COALESCE(@RoundName, RoundName),
                    CompetitionId = COALESCE(@CompetitionId, CompetitionId),
                    HomeTeamId = @HomeTeamId,
                    AwayTeamId = @AwayTeamId,
                    HomeScore = COALESCE(@HomeScore, HomeScore),
                    AwayScore = COALESCE(@AwayScore, AwayScore),
                    IsCompleted = CASE WHEN @IsCompleted = 1 THEN 1 ELSE IsCompleted END,
                    IsNeutral = @IsNeutral,
                    LastFetchedAt = SYSDATETIME()
                WHERE MatchId = @MatchId;

                IF @@ROWCOUNT = 0
                BEGIN
                    INSERT INTO NesineTeamMatches
                        (MatchId, BettingId, SportId, MatchDate, Season, RoundName,
                         CompetitionId, HomeTeamId, AwayTeamId,
                         HomeScore, AwayScore, IsCompleted, IsNeutral)
                    VALUES
                        (@MatchId, @BettingId, @SportId, @MatchDate, @Season, @RoundName,
                         @CompetitionId, @HomeTeamId, @AwayTeamId,
                         @HomeScore, @AwayScore, @IsCompleted, @IsNeutral);
                END;
                """,
                connection,
                transaction);

            command.Parameters.AddWithValue("@MatchId", match.MatchId);
            command.Parameters.AddWithValue("@BettingId", (object?)match.BettingId ?? DBNull.Value);
            command.Parameters.AddWithValue("@SportId", (object?)match.SportId ?? DBNull.Value);
            command.Parameters.AddWithValue("@MatchDate", (object?)match.MatchDate ?? DBNull.Value);
            command.Parameters.AddWithValue("@Season", (object?)match.Season ?? DBNull.Value);
            command.Parameters.AddWithValue("@RoundName", (object?)match.RoundName ?? DBNull.Value);
            command.Parameters.AddWithValue("@CompetitionId", (object?)match.Competition?.CompetitionId ?? DBNull.Value);
            command.Parameters.AddWithValue("@HomeTeamId", match.HomeTeam.TeamId);
            command.Parameters.AddWithValue("@AwayTeamId", match.AwayTeam.TeamId);
            command.Parameters.AddWithValue("@HomeScore", (object?)match.HomeScore ?? DBNull.Value);
            command.Parameters.AddWithValue("@AwayScore", (object?)match.AwayScore ?? DBNull.Value);
            command.Parameters.AddWithValue("@IsCompleted", match.IsCompleted);
            command.Parameters.AddWithValue("@IsNeutral", match.IsNeutral);

            return await command.ExecuteNonQueryAsync(cancellationToken) > 0 ? 1 : 0;
        }

        private static async Task<int> InsertFetchAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            CurrentRoundInfo currentRound,
            int? matchOrder,
            NesineTeamMatchFeed feed,
            CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand(
                """
                INSERT INTO NesineTeamMatchFetches
                    (RoundId, RoundName, MatchOrder, RequestedTeamId, MatchCount, RawJson)
                VALUES
                    (@RoundId, @RoundName, @MatchOrder, @RequestedTeamId, @MatchCount, @RawJson);
                """,
                connection,
                transaction);

            command.Parameters.AddWithValue("@RoundId", currentRound.RoundId);
            command.Parameters.AddWithValue("@RoundName", currentRound.RoundName);
            command.Parameters.AddWithValue("@MatchOrder", (object?)matchOrder ?? DBNull.Value);
            command.Parameters.AddWithValue("@RequestedTeamId", feed.RequestedTeamId);
            command.Parameters.AddWithValue("@MatchCount", feed.Matches.Count);
            command.Parameters.AddWithValue("@RawJson", feed.RawJson);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task EnsureSchemaAsync(CancellationToken cancellationToken)
        {
            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(
                """
                IF OBJECT_ID('dbo.NesineTeams', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.NesineTeams
                    (
                        TeamId INT NOT NULL CONSTRAINT PK_NesineTeams PRIMARY KEY,
                        Name NVARCHAR(200) NOT NULL,
                        ShortName NVARCHAR(200) NULL,
                        Abbreviation NVARCHAR(30) NULL,
                        FirstSeenAt DATETIME2 NOT NULL CONSTRAINT DF_NesineTeams_FirstSeenAt DEFAULT SYSDATETIME(),
                        LastSeenAt DATETIME2 NOT NULL CONSTRAINT DF_NesineTeams_LastSeenAt DEFAULT SYSDATETIME()
                    );
                END;

                IF OBJECT_ID('dbo.NesineCompetitions', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.NesineCompetitions
                    (
                        CompetitionId INT NOT NULL CONSTRAINT PK_NesineCompetitions PRIMARY KEY,
                        Name NVARCHAR(200) NULL,
                        Abbreviation NVARCHAR(30) NULL,
                        FirstSeenAt DATETIME2 NOT NULL CONSTRAINT DF_NesineCompetitions_FirstSeenAt DEFAULT SYSDATETIME(),
                        LastSeenAt DATETIME2 NOT NULL CONSTRAINT DF_NesineCompetitions_LastSeenAt DEFAULT SYSDATETIME()
                    );
                END;

                IF OBJECT_ID('dbo.NesineTeamMatches', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.NesineTeamMatches
                    (
                        MatchId BIGINT NOT NULL CONSTRAINT PK_NesineTeamMatches PRIMARY KEY,
                        BettingId INT NULL,
                        SportId INT NULL,
                        MatchDate DATETIME2 NULL,
                        Season NVARCHAR(30) NULL,
                        RoundName NVARCHAR(200) NULL,
                        CompetitionId INT NULL,
                        HomeTeamId INT NOT NULL,
                        AwayTeamId INT NOT NULL,
                        HomeScore INT NULL,
                        AwayScore INT NULL,
                        IsCompleted BIT NOT NULL,
                        IsNeutral BIT NOT NULL,
                        FirstFetchedAt DATETIME2 NOT NULL CONSTRAINT DF_NesineTeamMatches_FirstFetchedAt DEFAULT SYSDATETIME(),
                        LastFetchedAt DATETIME2 NOT NULL CONSTRAINT DF_NesineTeamMatches_LastFetchedAt DEFAULT SYSDATETIME(),
                        CONSTRAINT FK_NesineTeamMatches_Competition FOREIGN KEY (CompetitionId) REFERENCES dbo.NesineCompetitions(CompetitionId),
                        CONSTRAINT FK_NesineTeamMatches_HomeTeam FOREIGN KEY (HomeTeamId) REFERENCES dbo.NesineTeams(TeamId),
                        CONSTRAINT FK_NesineTeamMatches_AwayTeam FOREIGN KEY (AwayTeamId) REFERENCES dbo.NesineTeams(TeamId)
                    );
                END;

                IF NOT EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE name = 'IX_NesineTeamMatches_Date_Completed'
                      AND object_id = OBJECT_ID('dbo.NesineTeamMatches')
                )
                    CREATE INDEX IX_NesineTeamMatches_Date_Completed
                        ON dbo.NesineTeamMatches (MatchDate, IsCompleted)
                        INCLUDE (HomeTeamId, AwayTeamId, HomeScore, AwayScore, CompetitionId);

                IF NOT EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE name = 'IX_NesineTeamMatches_HomeTeam_Date'
                      AND object_id = OBJECT_ID('dbo.NesineTeamMatches')
                )
                    CREATE INDEX IX_NesineTeamMatches_HomeTeam_Date
                        ON dbo.NesineTeamMatches (HomeTeamId, MatchDate);

                IF NOT EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE name = 'IX_NesineTeamMatches_AwayTeam_Date'
                      AND object_id = OBJECT_ID('dbo.NesineTeamMatches')
                )
                    CREATE INDEX IX_NesineTeamMatches_AwayTeam_Date
                        ON dbo.NesineTeamMatches (AwayTeamId, MatchDate);

                IF OBJECT_ID('dbo.NesineTeamMatchFetches', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.NesineTeamMatchFetches
                    (
                        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_NesineTeamMatchFetches PRIMARY KEY,
                        RoundId INT NOT NULL,
                        RoundName NVARCHAR(100) NULL,
                        MatchOrder INT NULL,
                        RequestedTeamId INT NOT NULL,
                        MatchCount INT NOT NULL,
                        RawJson NVARCHAR(MAX) NOT NULL,
                        CapturedAt DATETIME2 NOT NULL CONSTRAINT DF_NesineTeamMatchFetches_CapturedAt DEFAULT SYSDATETIME()
                    );
                END;

                IF NOT EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE name = 'IX_NesineTeamMatchFetches_Team_CapturedAt'
                      AND object_id = OBJECT_ID('dbo.NesineTeamMatchFetches')
                )
                    CREATE INDEX IX_NesineTeamMatchFetches_Team_CapturedAt
                        ON dbo.NesineTeamMatchFetches (RequestedTeamId, CapturedAt);
                """,
                connection);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public sealed record NesineTeamMatchSaveResult(
        int FetchCount,
        int MatchUpsertCount,
        int CompletedMatchCount);

    public sealed record NesineTeamMatchDataQuality(
        int TeamCount,
        int CompetitionCount,
        int MatchCount,
        int CompletedMatchCount,
        int DatedCompletedMatchCount,
        DateTime? EarliestCompletedMatch,
        DateTime? LatestCompletedMatch);

    public sealed record NesineCompletedMatch(
        long MatchId,
        DateTime MatchDate,
        int HomeTeamId,
        int AwayTeamId,
        int? CompetitionId,
        int HomeScore,
        int AwayScore,
        bool IsNeutral);
}
