using System.Globalization;
using System.Text.Json;

namespace SporTotoFormApp.Services
{
    public sealed class NesineTeamProfileService
    {
        private const string BaseUrl = "https://apistats.nesine.com/api/v3/Team/{0}/{1}";
        private readonly HttpClient _httpClient;

        public NesineTeamProfileService(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(20);

            if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            {
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 SporTotoFormApp/2.0");
            }

            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("accept", "application/json, text/plain, */*");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("origin", "https://istatistik.nesine.com");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("platformid", "82");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("referer", "https://istatistik.nesine.com/");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("x-requested-with", "XMLHttpRequest");
        }

        public async Task<NesineTeamProfileFeed> GetProfileAsync(
            int teamId,
            CancellationToken cancellationToken = default)
        {
            var lineupTask = GetLineupAsync(teamId, cancellationToken);
            var leagueTableTask = GetLeagueTableAsync(teamId, cancellationToken);
            await Task.WhenAll(lineupTask, leagueTableTask);

            return new NesineTeamProfileFeed(
                teamId,
                await lineupTask,
                await leagueTableTask);
        }

        public async Task<NesineTeamLineup?> GetLineupAsync(
            int teamId,
            CancellationToken cancellationToken = default)
        {
            var content = await GetContentAsync(teamId, "LineUp", cancellationToken);
            if (content == null)
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(content);
                if (!TryGetData(document.RootElement, out var data))
                {
                    return null;
                }

                NesineManager? manager = null;
                if (data.TryGetProperty("MN", out var managerElement) &&
                    managerElement.ValueKind == JsonValueKind.Object)
                {
                    TryGetString(managerElement, "N", out var managerName);
                    TryGetString(managerElement, "CC", out var countryCode);
                    TryGetString(managerElement, "NTN", out var nationality);

                    if (!string.IsNullOrWhiteSpace(managerName))
                    {
                        manager = new NesineManager(
                            managerName.Trim(),
                            countryCode,
                            nationality);
                    }
                }

                var players = new List<NesineSquadPlayer>();
                if (data.TryGetProperty("TLU", out var lineup) &&
                    lineup.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in lineup.EnumerateArray())
                    {
                        if (!TryGetInt(item, "PID", out var playerId) ||
                            !TryGetString(item, "PNM", out var playerName) ||
                            string.IsNullOrWhiteSpace(playerName))
                        {
                            continue;
                        }

                        TryGetString(item, "PST", out var positionCode);
                        TryGetString(item, "PSTN", out var positionName);
                        TryGetString(item, "NTL", out var nationalityCode);
                        TryGetString(item, "NTN", out var nationalityName);
                        TryGetString(item, "PSNB", out var shirtNumber);

                        players.Add(new NesineSquadPlayer(
                            playerId,
                            playerName,
                            positionCode,
                            positionName,
                            ParseNullableInt(item, "AGE"),
                            nationalityCode,
                            nationalityName,
                            shirtNumber,
                            ParseNullableDouble(item, "HEI"),
                            ParseNullableDouble(item, "WEI"),
                            ParseNullableInt(item, "SXI"),
                            ParseNullableInt(item, "TMT"),
                            ParseNullableInt(item, "GFT"),
                            ParseNullableInt(item, "AFT"),
                            ParseNullableInt(item, "SI"),
                            ParseNullableInt(item, "RCFT"),
                            ParseNullableInt(item, "YCFT"),
                            ParseNullableInt(item, "YRCFT")));
                    }
                }

                return new NesineTeamLineup(
                    teamId,
                    manager,
                    players.DistinctBy(x => x.PlayerId).ToList(),
                    content);
            }
            catch
            {
                return null;
            }
        }

        public async Task<NesineTeamLeagueTable?> GetLeagueTableAsync(
            int teamId,
            CancellationToken cancellationToken = default)
        {
            var content = await GetContentAsync(teamId, "LeagueTable", cancellationToken);
            if (content == null)
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(content);
                if (!TryGetData(document.RootElement, out var data) ||
                    !data.TryGetProperty("LTL", out var leagues) ||
                    leagues.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var rows = new Dictionary<string, NesineLeagueTableRow>(StringComparer.Ordinal);

                foreach (var league in leagues.EnumerateArray())
                {
                    TryGetString(league, "LN", out var leagueName);
                    var seasonId = ParseNullableInt(league, "SID");

                    foreach (var table in FindArrays(league, "LTR"))
                    {
                        foreach (var row in table.EnumerateArray())
                        {
                            if (!TryGetInt(row, "TID", out var rowTeamId) ||
                                !TryGetString(row, "N", out var teamName) ||
                                string.IsNullOrWhiteSpace(teamName))
                            {
                                continue;
                            }

                            TryGetString(row, "NS", out var shortName);
                            TryGetString(row, "ABR", out var abbreviation);
                            TryGetString(row, "AVG", out var goalAverage);

                            var parsed = new NesineLeagueTableRow(
                                leagueName,
                                seasonId,
                                rowTeamId,
                                teamName,
                                shortName,
                                abbreviation,
                                ParseNullableInt(row, "PST"),
                                ParseNullableInt(row, "MC"),
                                ParseNullableInt(row, "WIN"),
                                ParseNullableInt(row, "DRW"),
                                ParseNullableInt(row, "LO"),
                                ParseNullableInt(row, "PNT"),
                                ParseNullableDouble(row, "WR"),
                                ParseNullableInt(row, "AD"),
                                goalAverage,
                                TryGetBool(row, "ST", out var selected) && selected,
                                ParseNullableInt(row, "CHNG"));

                            var key = $"{leagueName}|{seasonId}|{rowTeamId}";
                            rows[key] = parsed;
                        }
                    }
                }

                return new NesineTeamLeagueTable(
                    teamId,
                    rows.Values.ToList(),
                    content);
            }
            catch
            {
                return null;
            }
        }

        private async Task<string?> GetContentAsync(
            int teamId,
            string endpoint,
            CancellationToken cancellationToken)
        {
            if (teamId <= 0)
            {
                return null;
            }

            try
            {
                var url = string.Format(
                    CultureInfo.InvariantCulture,
                    BaseUrl,
                    teamId,
                    endpoint);
                using var response = await _httpClient.GetAsync(url, cancellationToken);
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                return response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(content)
                    ? content
                    : null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        private static bool TryGetData(JsonElement root, out JsonElement data)
        {
            data = default;
            return TryGetInt(root, "sc", out var statusCode) &&
                   statusCode == 200 &&
                   root.TryGetProperty("d", out data) &&
                   data.ValueKind == JsonValueKind.Object;
        }

        private static IEnumerable<JsonElement> FindArrays(
            JsonElement element,
            string propertyName)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals(propertyName) &&
                        property.Value.ValueKind == JsonValueKind.Array)
                    {
                        yield return property.Value;
                    }

                    foreach (var child in FindArrays(property.Value, propertyName))
                    {
                        yield return child;
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var child in FindArrays(item, propertyName))
                    {
                        yield return child;
                    }
                }
            }
        }

        private static int? ParseNullableInt(JsonElement element, string propertyName)
        {
            return TryGetInt(element, propertyName, out var value) ? value : null;
        }

        private static double? ParseNullableDouble(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(propertyName, out var property))
            {
                return null;
            }

            if (property.ValueKind == JsonValueKind.Number &&
                property.TryGetDouble(out var number))
            {
                return number;
            }

            if (property.ValueKind == JsonValueKind.String &&
                double.TryParse(
                    property.GetString(),
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out var parsed))
            {
                return parsed;
            }

            return null;
        }

        private static bool TryGetInt(JsonElement element, string propertyName, out int value)
        {
            value = 0;
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(propertyName, out var property))
            {
                return false;
            }

            return property.ValueKind switch
            {
                JsonValueKind.Number => property.TryGetInt32(out value),
                JsonValueKind.String => int.TryParse(
                    property.GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out value),
                _ => false
            };
        }

        private static bool TryGetString(
            JsonElement element,
            string propertyName,
            out string? value)
        {
            value = null;
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(propertyName, out var property) ||
                property.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            value = property.GetString();
            return true;
        }

        private static bool TryGetBool(
            JsonElement element,
            string propertyName,
            out bool value)
        {
            value = false;
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(propertyName, out var property) ||
                property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return false;
            }

            value = property.GetBoolean();
            return true;
        }
    }

    public sealed record NesineTeamProfileFeed(
        int TeamId,
        NesineTeamLineup? Lineup,
        NesineTeamLeagueTable? LeagueTable);

    public sealed record NesineTeamLineup(
        int TeamId,
        NesineManager? Manager,
        IReadOnlyList<NesineSquadPlayer> Players,
        string RawJson);

    public sealed record NesineManager(
        string Name,
        string? CountryCode,
        string? Nationality);

    public sealed record NesineSquadPlayer(
        int PlayerId,
        string Name,
        string? PositionCode,
        string? PositionName,
        int? Age,
        string? NationalityCode,
        string? NationalityName,
        string? ShirtNumber,
        double? Height,
        double? Weight,
        int? StartingElevenCount,
        int? TotalMinutes,
        int? Goals,
        int? Assists,
        int? SubstitutionCount,
        int? RedCards,
        int? YellowCards,
        int? SecondYellowCards);

    public sealed record NesineTeamLeagueTable(
        int TeamId,
        IReadOnlyList<NesineLeagueTableRow> Rows,
        string RawJson);

    public sealed record NesineLeagueTableRow(
        string? LeagueName,
        int? SeasonId,
        int TeamId,
        string TeamName,
        string? ShortName,
        string? Abbreviation,
        int? Position,
        int? Played,
        int? Wins,
        int? Draws,
        int? Losses,
        int? Points,
        double? WinRate,
        int? GoalDifference,
        string? GoalAverage,
        bool IsSelected,
        int? PositionChange);
}
