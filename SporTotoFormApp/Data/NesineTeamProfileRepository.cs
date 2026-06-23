using Microsoft.Data.SqlClient;
using SporTotoFormApp.Services;

namespace SporTotoFormApp.Data
{
    public sealed class NesineTeamProfileRepository
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
                SELECT lineup.TeamId
                FROM
                (
                    SELECT TeamId, MAX(CapturedAt) AS CapturedAt
                    FROM NesineTeamLineupSnapshots
                    WHERE TeamId IN ({string.Join(", ", parameters)})
                    GROUP BY TeamId
                ) lineup
                INNER JOIN
                (
                    SELECT TeamId, MAX(CapturedAt) AS CapturedAt
                    FROM NesineTeamLeagueTableSnapshots
                    WHERE TeamId IN ({string.Join(", ", parameters)})
                    GROUP BY TeamId
                ) league ON league.TeamId = lineup.TeamId
                WHERE lineup.CapturedAt >= DATEADD(MINUTE, -@MaximumAgeMinutes, SYSDATETIME())
                  AND league.CapturedAt >= DATEADD(MINUTE, -@MaximumAgeMinutes, SYSDATETIME());
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

            var current = new HashSet<int>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                current.Add(reader.GetInt32(0));
            }

            requestedIds.ExceptWith(current);
            return requestedIds;
        }

        public async Task<NesineTeamProfileSaveResult> SaveProfilesAsync(
            CurrentRoundInfo currentRound,
            IReadOnlyDictionary<int, int> matchOrderByTeamId,
            IReadOnlyList<NesineTeamProfileFeed> profiles,
            CancellationToken cancellationToken = default)
        {
            await EnsureSchemaAsync(cancellationToken);

            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

            var lineupSnapshots = 0;
            var playerRows = 0;
            var leagueSnapshots = 0;
            var leagueRows = 0;

            try
            {
                foreach (var profile in profiles)
                {
                    var matchOrder = matchOrderByTeamId.TryGetValue(profile.TeamId, out var foundOrder)
                        ? foundOrder
                        : null as int?;
                    var teamRow = profile.LeagueTable?.Rows
                        .FirstOrDefault(x => x.TeamId == profile.TeamId);

                    await EnsureTeamAsync(
                        connection,
                        transaction,
                        profile.TeamId,
                        teamRow?.TeamName,
                        teamRow?.ShortName,
                        teamRow?.Abbreviation,
                        cancellationToken);

                    if (profile.Lineup != null)
                    {
                        var snapshotId = await InsertLineupSnapshotAsync(
                            connection,
                            transaction,
                            currentRound,
                            matchOrder,
                            profile.Lineup,
                            cancellationToken);
                        lineupSnapshots++;

                        foreach (var player in profile.Lineup.Players)
                        {
                            playerRows += await InsertPlayerAsync(
                                connection,
                                transaction,
                                snapshotId,
                                player,
                                cancellationToken);
                        }
                    }

                    if (profile.LeagueTable != null)
                    {
                        var snapshotId = await InsertLeagueSnapshotAsync(
                            connection,
                            transaction,
                            currentRound,
                            matchOrder,
                            profile.LeagueTable,
                            cancellationToken);
                        leagueSnapshots++;

                        foreach (var row in profile.LeagueTable.Rows)
                        {
                            leagueRows += await InsertLeagueRowAsync(
                                connection,
                                transaction,
                                snapshotId,
                                row,
                                cancellationToken);
                        }
                    }
                }

                await transaction.CommitAsync(cancellationToken);
                return new NesineTeamProfileSaveResult(
                    lineupSnapshots,
                    playerRows,
                    leagueSnapshots,
                    leagueRows);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }

        public async Task<IReadOnlyDictionary<int, NesineTeamContextFeature>> LoadLatestFeaturesAsync(
            IEnumerable<int> teamIds,
            CancellationToken cancellationToken = default)
        {
            await EnsureSchemaAsync(cancellationToken);

            var requestedIds = teamIds.Where(x => x > 0).Distinct().ToList();
            if (requestedIds.Count == 0)
            {
                return new Dictionary<int, NesineTeamContextFeature>();
            }

            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var parameters = requestedIds
                .Select((_, index) => $"@TeamId{index}")
                .ToList();
            await using var command = new SqlCommand(
                $"""
                WITH LatestLineup AS
                (
                    SELECT *, ROW_NUMBER() OVER (PARTITION BY TeamId ORDER BY CapturedAt DESC, Id DESC) AS rn
                    FROM NesineTeamLineupSnapshots
                    WHERE TeamId IN ({string.Join(", ", parameters)})
                ),
                LineupFeatures AS
                (
                    SELECT
                        l.TeamId,
                        l.ManagerName,
                        COUNT(p.Id) AS SquadSize,
                        AVG(CAST(p.Age AS FLOAT)) AS AverageAge,
                        SUM(CASE WHEN p.PositionCode = 'G' THEN 1 ELSE 0 END) AS GoalkeeperCount,
                        SUM(CASE WHEN p.PositionCode = 'D' THEN 1 ELSE 0 END) AS DefenderCount,
                        SUM(CASE WHEN p.PositionCode = 'M' THEN 1 ELSE 0 END) AS MidfielderCount,
                        SUM(CASE WHEN p.PositionCode = 'F' THEN 1 ELSE 0 END) AS ForwardCount
                    FROM LatestLineup l
                    LEFT JOIN NesineTeamLineupPlayers p ON p.SnapshotId = l.Id
                    WHERE l.rn = 1
                    GROUP BY l.TeamId, l.ManagerName
                ),
                LatestLeague AS
                (
                    SELECT *, ROW_NUMBER() OVER (PARTITION BY TeamId ORDER BY CapturedAt DESC, Id DESC) AS rn
                    FROM NesineTeamLeagueTableSnapshots
                    WHERE TeamId IN ({string.Join(", ", parameters)})
                ),
                LeagueCandidates AS
                (
                    SELECT
                        l.TeamId,
                        r.LeagueName,
                        r.SeasonId,
                        r.Position,
                        r.Played,
                        r.Wins,
                        r.Draws,
                        r.Losses,
                        r.Points,
                        r.GoalDifference,
                        ROW_NUMBER() OVER
                        (
                            PARTITION BY l.TeamId
                            ORDER BY
                                ISNULL(r.Played, -1) DESC,
                                ISNULL(r.SeasonId, -1) DESC,
                                r.Id DESC
                        ) AS featureRank
                    FROM LatestLeague l
                    LEFT JOIN NesineTeamLeagueTableRows r
                      ON r.SnapshotId = l.Id
                     AND r.TeamId = l.TeamId
                    WHERE l.rn = 1
                ),
                LeagueFeatures AS
                (
                    SELECT *
                    FROM LeagueCandidates
                    WHERE featureRank = 1
                )
                SELECT
                    ids.TeamId,
                    lf.ManagerName,
                    ISNULL(lf.SquadSize, 0),
                    lf.AverageAge,
                    ISNULL(lf.GoalkeeperCount, 0),
                    ISNULL(lf.DefenderCount, 0),
                    ISNULL(lf.MidfielderCount, 0),
                    ISNULL(lf.ForwardCount, 0),
                    lg.LeagueName,
                    lg.SeasonId,
                    lg.Position,
                    lg.Played,
                    lg.Wins,
                    lg.Draws,
                    lg.Losses,
                    lg.Points,
                    lg.GoalDifference
                FROM
                (
                    SELECT TeamId FROM NesineTeams
                    WHERE TeamId IN ({string.Join(", ", parameters)})
                ) ids
                LEFT JOIN LineupFeatures lf ON lf.TeamId = ids.TeamId
                LEFT JOIN LeagueFeatures lg ON lg.TeamId = ids.TeamId;
                """,
                connection);

            for (var index = 0; index < requestedIds.Count; index++)
            {
                command.Parameters.AddWithValue($"@TeamId{index}", requestedIds[index]);
            }

            var result = new Dictionary<int, NesineTeamContextFeature>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var played = ReadNullableInt(reader, 11);
                var points = ReadNullableInt(reader, 15);

                result[reader.GetInt32(0)] = new NesineTeamContextFeature(
                    reader.GetInt32(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetDouble(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5),
                    reader.GetInt32(6),
                    reader.GetInt32(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    ReadNullableInt(reader, 9),
                    ReadNullableInt(reader, 10),
                    played,
                    ReadNullableInt(reader, 12),
                    ReadNullableInt(reader, 13),
                    ReadNullableInt(reader, 14),
                    points,
                    ReadNullableInt(reader, 16),
                    played > 0 && points.HasValue ? (double)points.Value / played.Value : null);
            }

            return result;
        }

        private static async Task EnsureTeamAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            int teamId,
            string? name,
            string? shortName,
            string? abbreviation,
            CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand(
                """
                UPDATE NesineTeams
                SET Name = COALESCE(@Name, Name),
                    ShortName = COALESCE(@ShortName, ShortName),
                    Abbreviation = COALESCE(@Abbreviation, Abbreviation),
                    LastSeenAt = SYSDATETIME()
                WHERE TeamId = @TeamId;

                IF @@ROWCOUNT = 0
                BEGIN
                    INSERT INTO NesineTeams
                        (TeamId, Name, ShortName, Abbreviation)
                    VALUES
                        (@TeamId, COALESCE(@Name, CONCAT('Team ', @TeamId)), @ShortName, @Abbreviation);
                END;
                """,
                connection,
                transaction);

            command.Parameters.AddWithValue("@TeamId", teamId);
            command.Parameters.AddWithValue("@Name", (object?)name ?? DBNull.Value);
            command.Parameters.AddWithValue("@ShortName", (object?)shortName ?? DBNull.Value);
            command.Parameters.AddWithValue("@Abbreviation", (object?)abbreviation ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task<int> InsertLineupSnapshotAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            CurrentRoundInfo currentRound,
            int? matchOrder,
            NesineTeamLineup lineup,
            CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand(
                """
                INSERT INTO NesineTeamLineupSnapshots
                    (RoundId, RoundName, MatchOrder, TeamId,
                     ManagerName, ManagerCountryCode, ManagerNationality,
                     PlayerCount, RawJson)
                OUTPUT INSERTED.Id
                VALUES
                    (@RoundId, @RoundName, @MatchOrder, @TeamId,
                     @ManagerName, @ManagerCountryCode, @ManagerNationality,
                     @PlayerCount, @RawJson);
                """,
                connection,
                transaction);

            command.Parameters.AddWithValue("@RoundId", currentRound.RoundId);
            command.Parameters.AddWithValue("@RoundName", currentRound.RoundName);
            command.Parameters.AddWithValue("@MatchOrder", (object?)matchOrder ?? DBNull.Value);
            command.Parameters.AddWithValue("@TeamId", lineup.TeamId);
            command.Parameters.AddWithValue("@ManagerName", (object?)lineup.Manager?.Name ?? DBNull.Value);
            command.Parameters.AddWithValue("@ManagerCountryCode", (object?)lineup.Manager?.CountryCode ?? DBNull.Value);
            command.Parameters.AddWithValue("@ManagerNationality", (object?)lineup.Manager?.Nationality ?? DBNull.Value);
            command.Parameters.AddWithValue("@PlayerCount", lineup.Players.Count);
            command.Parameters.AddWithValue("@RawJson", lineup.RawJson);

            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        }

        private static async Task<int> InsertPlayerAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            int snapshotId,
            NesineSquadPlayer player,
            CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand(
                """
                INSERT INTO NesineTeamLineupPlayers
                    (SnapshotId, PlayerId, PlayerName, PositionCode, PositionName,
                     Age, NationalityCode, NationalityName, ShirtNumber,
                     Height, Weight, StartingElevenCount, TotalMinutes,
                     Goals, Assists, SubstitutionCount, RedCards, YellowCards, SecondYellowCards)
                VALUES
                    (@SnapshotId, @PlayerId, @PlayerName, @PositionCode, @PositionName,
                     @Age, @NationalityCode, @NationalityName, @ShirtNumber,
                     @Height, @Weight, @StartingElevenCount, @TotalMinutes,
                     @Goals, @Assists, @SubstitutionCount, @RedCards, @YellowCards, @SecondYellowCards);
                """,
                connection,
                transaction);

            command.Parameters.AddWithValue("@SnapshotId", snapshotId);
            command.Parameters.AddWithValue("@PlayerId", player.PlayerId);
            command.Parameters.AddWithValue("@PlayerName", player.Name);
            command.Parameters.AddWithValue("@PositionCode", (object?)player.PositionCode ?? DBNull.Value);
            command.Parameters.AddWithValue("@PositionName", (object?)player.PositionName ?? DBNull.Value);
            command.Parameters.AddWithValue("@Age", (object?)player.Age ?? DBNull.Value);
            command.Parameters.AddWithValue("@NationalityCode", (object?)player.NationalityCode ?? DBNull.Value);
            command.Parameters.AddWithValue("@NationalityName", (object?)player.NationalityName ?? DBNull.Value);
            command.Parameters.AddWithValue("@ShirtNumber", (object?)player.ShirtNumber ?? DBNull.Value);
            command.Parameters.AddWithValue("@Height", (object?)player.Height ?? DBNull.Value);
            command.Parameters.AddWithValue("@Weight", (object?)player.Weight ?? DBNull.Value);
            command.Parameters.AddWithValue("@StartingElevenCount", (object?)player.StartingElevenCount ?? DBNull.Value);
            command.Parameters.AddWithValue("@TotalMinutes", (object?)player.TotalMinutes ?? DBNull.Value);
            command.Parameters.AddWithValue("@Goals", (object?)player.Goals ?? DBNull.Value);
            command.Parameters.AddWithValue("@Assists", (object?)player.Assists ?? DBNull.Value);
            command.Parameters.AddWithValue("@SubstitutionCount", (object?)player.SubstitutionCount ?? DBNull.Value);
            command.Parameters.AddWithValue("@RedCards", (object?)player.RedCards ?? DBNull.Value);
            command.Parameters.AddWithValue("@YellowCards", (object?)player.YellowCards ?? DBNull.Value);
            command.Parameters.AddWithValue("@SecondYellowCards", (object?)player.SecondYellowCards ?? DBNull.Value);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task<int> InsertLeagueSnapshotAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            CurrentRoundInfo currentRound,
            int? matchOrder,
            NesineTeamLeagueTable table,
            CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand(
                """
                INSERT INTO NesineTeamLeagueTableSnapshots
                    (RoundId, RoundName, MatchOrder, TeamId, TableRowCount, RawJson)
                OUTPUT INSERTED.Id
                VALUES
                    (@RoundId, @RoundName, @MatchOrder, @TeamId, @RowCount, @RawJson);
                """,
                connection,
                transaction);

            command.Parameters.AddWithValue("@RoundId", currentRound.RoundId);
            command.Parameters.AddWithValue("@RoundName", currentRound.RoundName);
            command.Parameters.AddWithValue("@MatchOrder", (object?)matchOrder ?? DBNull.Value);
            command.Parameters.AddWithValue("@TeamId", table.TeamId);
            command.Parameters.AddWithValue("@RowCount", table.Rows.Count);
            command.Parameters.AddWithValue("@RawJson", table.RawJson);
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        }

        private static async Task<int> InsertLeagueRowAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            int snapshotId,
            NesineLeagueTableRow row,
            CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand(
                """
                INSERT INTO NesineTeamLeagueTableRows
                    (SnapshotId, LeagueName, SeasonId, TeamId, TeamName, ShortName, Abbreviation,
                     Position, Played, Wins, Draws, Losses, Points, WinRate,
                     GoalDifference, GoalAverage, IsSelected, PositionChange)
                VALUES
                    (@SnapshotId, @LeagueName, @SeasonId, @TeamId, @TeamName, @ShortName, @Abbreviation,
                     @Position, @Played, @Wins, @Draws, @Losses, @Points, @WinRate,
                     @GoalDifference, @GoalAverage, @IsSelected, @PositionChange);
                """,
                connection,
                transaction);

            command.Parameters.AddWithValue("@SnapshotId", snapshotId);
            command.Parameters.AddWithValue("@LeagueName", (object?)row.LeagueName ?? DBNull.Value);
            command.Parameters.AddWithValue("@SeasonId", (object?)row.SeasonId ?? DBNull.Value);
            command.Parameters.AddWithValue("@TeamId", row.TeamId);
            command.Parameters.AddWithValue("@TeamName", row.TeamName);
            command.Parameters.AddWithValue("@ShortName", (object?)row.ShortName ?? DBNull.Value);
            command.Parameters.AddWithValue("@Abbreviation", (object?)row.Abbreviation ?? DBNull.Value);
            command.Parameters.AddWithValue("@Position", (object?)row.Position ?? DBNull.Value);
            command.Parameters.AddWithValue("@Played", (object?)row.Played ?? DBNull.Value);
            command.Parameters.AddWithValue("@Wins", (object?)row.Wins ?? DBNull.Value);
            command.Parameters.AddWithValue("@Draws", (object?)row.Draws ?? DBNull.Value);
            command.Parameters.AddWithValue("@Losses", (object?)row.Losses ?? DBNull.Value);
            command.Parameters.AddWithValue("@Points", (object?)row.Points ?? DBNull.Value);
            command.Parameters.AddWithValue("@WinRate", (object?)row.WinRate ?? DBNull.Value);
            command.Parameters.AddWithValue("@GoalDifference", (object?)row.GoalDifference ?? DBNull.Value);
            command.Parameters.AddWithValue("@GoalAverage", (object?)row.GoalAverage ?? DBNull.Value);
            command.Parameters.AddWithValue("@IsSelected", row.IsSelected);
            command.Parameters.AddWithValue("@PositionChange", (object?)row.PositionChange ?? DBNull.Value);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static int? ReadNullableInt(SqlDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
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

                IF OBJECT_ID('dbo.NesineTeamLineupSnapshots', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.NesineTeamLineupSnapshots
                    (
                        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_NesineTeamLineupSnapshots PRIMARY KEY,
                        RoundId INT NOT NULL,
                        RoundName NVARCHAR(100) NULL,
                        MatchOrder INT NULL,
                        TeamId INT NOT NULL,
                        ManagerName NVARCHAR(200) NULL,
                        ManagerCountryCode NVARCHAR(20) NULL,
                        ManagerNationality NVARCHAR(100) NULL,
                        PlayerCount INT NOT NULL,
                        RawJson NVARCHAR(MAX) NOT NULL,
                        CapturedAt DATETIME2 NOT NULL CONSTRAINT DF_NesineTeamLineupSnapshots_CapturedAt DEFAULT SYSDATETIME(),
                        CONSTRAINT FK_NesineTeamLineupSnapshots_Team FOREIGN KEY (TeamId) REFERENCES dbo.NesineTeams(TeamId)
                    );
                END;

                IF OBJECT_ID('dbo.NesineTeamLineupPlayers', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.NesineTeamLineupPlayers
                    (
                        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_NesineTeamLineupPlayers PRIMARY KEY,
                        SnapshotId INT NOT NULL,
                        PlayerId INT NOT NULL,
                        PlayerName NVARCHAR(200) NOT NULL,
                        PositionCode NVARCHAR(20) NULL,
                        PositionName NVARCHAR(100) NULL,
                        Age INT NULL,
                        NationalityCode NVARCHAR(20) NULL,
                        NationalityName NVARCHAR(100) NULL,
                        ShirtNumber NVARCHAR(20) NULL,
                        Height FLOAT NULL,
                        Weight FLOAT NULL,
                        StartingElevenCount INT NULL,
                        TotalMinutes INT NULL,
                        Goals INT NULL,
                        Assists INT NULL,
                        SubstitutionCount INT NULL,
                        RedCards INT NULL,
                        YellowCards INT NULL,
                        SecondYellowCards INT NULL,
                        CONSTRAINT FK_NesineTeamLineupPlayers_Snapshot FOREIGN KEY (SnapshotId)
                            REFERENCES dbo.NesineTeamLineupSnapshots(Id)
                    );
                END;

                IF OBJECT_ID('dbo.NesineTeamLeagueTableSnapshots', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.NesineTeamLeagueTableSnapshots
                    (
                        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_NesineTeamLeagueTableSnapshots PRIMARY KEY,
                        RoundId INT NOT NULL,
                        RoundName NVARCHAR(100) NULL,
                        MatchOrder INT NULL,
                        TeamId INT NOT NULL,
                        TableRowCount INT NOT NULL,
                        RawJson NVARCHAR(MAX) NOT NULL,
                        CapturedAt DATETIME2 NOT NULL CONSTRAINT DF_NesineTeamLeagueTableSnapshots_CapturedAt DEFAULT SYSDATETIME(),
                        CONSTRAINT FK_NesineTeamLeagueTableSnapshots_Team FOREIGN KEY (TeamId) REFERENCES dbo.NesineTeams(TeamId)
                    );
                END;

                IF OBJECT_ID('dbo.NesineTeamLeagueTableRows', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.NesineTeamLeagueTableRows
                    (
                        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_NesineTeamLeagueTableRows PRIMARY KEY,
                        SnapshotId INT NOT NULL,
                        LeagueName NVARCHAR(200) NULL,
                        SeasonId INT NULL,
                        TeamId INT NOT NULL,
                        TeamName NVARCHAR(200) NOT NULL,
                        ShortName NVARCHAR(200) NULL,
                        Abbreviation NVARCHAR(30) NULL,
                        Position INT NULL,
                        Played INT NULL,
                        Wins INT NULL,
                        Draws INT NULL,
                        Losses INT NULL,
                        Points INT NULL,
                        WinRate FLOAT NULL,
                        GoalDifference INT NULL,
                        GoalAverage NVARCHAR(30) NULL,
                        IsSelected BIT NOT NULL,
                        PositionChange INT NULL,
                        CONSTRAINT FK_NesineTeamLeagueTableRows_Snapshot FOREIGN KEY (SnapshotId)
                            REFERENCES dbo.NesineTeamLeagueTableSnapshots(Id)
                    );
                END;

                IF NOT EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE name = 'IX_NesineTeamLineupSnapshots_Team_CapturedAt'
                      AND object_id = OBJECT_ID('dbo.NesineTeamLineupSnapshots')
                )
                    CREATE INDEX IX_NesineTeamLineupSnapshots_Team_CapturedAt
                        ON dbo.NesineTeamLineupSnapshots (TeamId, CapturedAt);

                IF NOT EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE name = 'IX_NesineTeamLineupPlayers_Snapshot_Position'
                      AND object_id = OBJECT_ID('dbo.NesineTeamLineupPlayers')
                )
                    CREATE INDEX IX_NesineTeamLineupPlayers_Snapshot_Position
                        ON dbo.NesineTeamLineupPlayers (SnapshotId, PositionCode);

                IF NOT EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE name = 'IX_NesineTeamLeagueTableSnapshots_Team_CapturedAt'
                      AND object_id = OBJECT_ID('dbo.NesineTeamLeagueTableSnapshots')
                )
                    CREATE INDEX IX_NesineTeamLeagueTableSnapshots_Team_CapturedAt
                        ON dbo.NesineTeamLeagueTableSnapshots (TeamId, CapturedAt);

                IF NOT EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE name = 'IX_NesineTeamLeagueTableRows_Snapshot_Team'
                      AND object_id = OBJECT_ID('dbo.NesineTeamLeagueTableRows')
                )
                    CREATE INDEX IX_NesineTeamLeagueTableRows_Snapshot_Team
                        ON dbo.NesineTeamLeagueTableRows (SnapshotId, TeamId);
                """,
                connection);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public sealed record NesineTeamProfileSaveResult(
        int LineupSnapshotCount,
        int PlayerRowCount,
        int LeagueSnapshotCount,
        int LeagueRowCount);

    public sealed record NesineTeamContextFeature(
        int TeamId,
        string? ManagerName,
        int SquadSize,
        double? AverageAge,
        int GoalkeeperCount,
        int DefenderCount,
        int MidfielderCount,
        int ForwardCount,
        string? LeagueName,
        int? SeasonId,
        int? Position,
        int? Played,
        int? Wins,
        int? Draws,
        int? Losses,
        int? Points,
        int? GoalDifference,
        double? PointsPerMatch);
}
