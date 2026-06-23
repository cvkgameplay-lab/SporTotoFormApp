using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SporTotoFormApp.Services
{
    public sealed class NesineTeamMatchesService
    {
        private const string TeamMatchesUrl = "https://apistats.nesine.com/api/v3/Team/{0}/Matches";
        private readonly HttpClient _httpClient;

        public NesineTeamMatchesService(HttpClient? httpClient = null)
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

        public async Task<NesineTeamMatchFeed?> GetMatchesAsync(
            int teamId,
            CancellationToken cancellationToken = default)
        {
            if (teamId <= 0)
            {
                return null;
            }

            try
            {
                var url = string.Format(CultureInfo.InvariantCulture, TeamMatchesUrl, teamId);
                using var response = await _httpClient.GetAsync(url, cancellationToken);
                var content = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(content))
                {
                    return null;
                }

                using var document = JsonDocument.Parse(content);
                if (!TryGetInt(document.RootElement, "sc", out var statusCode) || statusCode != 200)
                {
                    return null;
                }

                var teams = new Dictionary<int, NesineTeamIdentity>();
                var matches = new Dictionary<long, NesineTeamMatch>();

                foreach (var item in EnumerateObjects(document.RootElement))
                {
                    if (!TryParseMatch(item, out var match))
                    {
                        continue;
                    }

                    teams[match.HomeTeam.TeamId] = match.HomeTeam;
                    teams[match.AwayTeam.TeamId] = match.AwayTeam;

                    if (!matches.TryGetValue(match.MatchId, out var existing) ||
                        (!existing.IsCompleted && match.IsCompleted))
                    {
                        matches[match.MatchId] = match;
                    }
                }

                var requestedTeam = teams.TryGetValue(teamId, out var foundTeam)
                    ? foundTeam
                    : FindRequestedTeam(document.RootElement, teamId);

                return new NesineTeamMatchFeed(
                    teamId,
                    requestedTeam,
                    matches.Values.OrderBy(x => x.MatchDate).ToList(),
                    content);
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

        private static bool TryParseMatch(JsonElement item, out NesineTeamMatch match)
        {
            match = default!;

            if (!TryGetLong(item, "MID", out var matchId) ||
                !item.TryGetProperty("HT", out var homeElement) ||
                !item.TryGetProperty("AT", out var awayElement) ||
                !TryParseTeam(homeElement, out var homeTeam) ||
                !TryParseTeam(awayElement, out var awayTeam))
            {
                return false;
            }

            DateTime? matchDate = null;
            if (TryGetString(item, "MD", out var dateText) &&
                DateTime.TryParse(
                    dateText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out var parsedDate) &&
                parsedDate.Year > 1900)
            {
                matchDate = parsedDate;
            }

            int? homeScore = null;
            int? awayScore = null;
            if (item.TryGetProperty("SC", out var scores) && scores.ValueKind == JsonValueKind.Array)
            {
                var scoreRows = scores.EnumerateArray().ToList();
                var finalScore = scoreRows.FirstOrDefault(x =>
                    TryGetInt(x, "TY", out var type) && type == 2);

                if (finalScore.ValueKind == JsonValueKind.Undefined)
                {
                    finalScore = scoreRows.FirstOrDefault(x =>
                        TryGetInt(x, "OBI", out var order) && order == 99);
                }

                if (finalScore.ValueKind != JsonValueKind.Undefined &&
                    TryGetInt(finalScore, "HTS", out var parsedHomeScore) &&
                    TryGetInt(finalScore, "ATS", out var parsedAwayScore))
                {
                    homeScore = parsedHomeScore;
                    awayScore = parsedAwayScore;
                }
            }

            NesineCompetitionIdentity? competition = null;
            if (item.TryGetProperty("LG", out var leagueElement) &&
                TryGetInt(leagueElement, "TID", out var competitionId))
            {
                TryGetString(leagueElement, "N", out var competitionName);
                TryGetString(leagueElement, "ABR", out var competitionAbbreviation);
                competition = new NesineCompetitionIdentity(
                    competitionId,
                    competitionName,
                    competitionAbbreviation);
            }

            TryGetInt(item, "BID", out var bettingId);
            TryGetInt(item, "SID", out var sportId);
            TryGetString(item, "SEA", out var season);
            TryGetString(item, "RD", out var roundName);
            var isNeutral = TryGetBool(item, "NG", out var neutral) && neutral;

            match = new NesineTeamMatch(
                matchId,
                bettingId == 0 ? null : bettingId,
                sportId == 0 ? null : sportId,
                matchDate,
                season,
                roundName,
                isNeutral,
                homeTeam,
                awayTeam,
                competition,
                homeScore,
                awayScore);

            return true;
        }

        private static NesineTeamIdentity? FindRequestedTeam(JsonElement root, int teamId)
        {
            foreach (var item in EnumerateObjects(root))
            {
                if (TryParseTeam(item, out var team) && team.TeamId == teamId)
                {
                    return team;
                }
            }

            return null;
        }

        private static bool TryParseTeam(JsonElement element, out NesineTeamIdentity team)
        {
            team = default!;
            if (element.ValueKind != JsonValueKind.Object ||
                !TryGetInt(element, "TID", out var teamId) ||
                !TryGetString(element, "N", out var name) ||
                string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            TryGetString(element, "NS", out var shortName);
            TryGetString(element, "ABR", out var abbreviation);

            team = new NesineTeamIdentity(teamId, name, shortName, abbreviation);
            return true;
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
                JsonValueKind.String => int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value),
                _ => false
            };
        }

        private static bool TryGetLong(JsonElement element, string propertyName, out long value)
        {
            value = 0;
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(propertyName, out var property))
            {
                return false;
            }

            return property.ValueKind switch
            {
                JsonValueKind.Number => property.TryGetInt64(out value),
                JsonValueKind.String => long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value),
                _ => false
            };
        }

        private static bool TryGetString(JsonElement element, string propertyName, out string? value)
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

        private static bool TryGetBool(JsonElement element, string propertyName, out bool value)
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

    public static class NesineTeamIdentityResolver
    {
        public static NesineMatchTeamIds? Resolve(
            NesineMatchPopularity match,
            NesineHeadToHeadSummary? summary,
            IReadOnlyList<NesineHeadToHeadExtraSnapshot>? extras)
        {
            var jsonDocuments = new List<JsonDocument>();
            try
            {
                AddDocument(jsonDocuments, summary?.RawJson);
                if (extras != null)
                {
                    foreach (var extra in extras.Where(x => x.HasData))
                    {
                        AddDocument(jsonDocuments, extra.RawJson);
                    }
                }

                var candidates = jsonDocuments
                    .SelectMany(x => EnumerateTeams(x.RootElement))
                    .DistinctBy(x => x.TeamId)
                    .ToList();

                var home = FindBestMatch(candidates, match.HomeTeam);
                var away = FindBestMatch(candidates, match.AwayTeam);
                if (home == null || away == null || home.TeamId == away.TeamId)
                {
                    return null;
                }

                return new NesineMatchTeamIds(match.MatchNo, home, away);
            }
            finally
            {
                foreach (var document in jsonDocuments)
                {
                    document.Dispose();
                }
            }
        }

        private static void AddDocument(List<JsonDocument> documents, string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            try
            {
                documents.Add(JsonDocument.Parse(json));
            }
            catch
            {
            }
        }

        private static NesineTeamIdentity? FindBestMatch(
            IReadOnlyList<NesineTeamIdentity> candidates,
            string targetName)
        {
            var normalizedTarget = NormalizeName(targetName);
            if (normalizedTarget.Length == 0)
            {
                return null;
            }

            var exact = candidates.FirstOrDefault(x =>
                GetNormalizedNames(x).Any(name => name == normalizedTarget));
            if (exact != null)
            {
                return exact;
            }

            return candidates.FirstOrDefault(x =>
                GetNormalizedNames(x).Any(name =>
                    name.Length >= 5 &&
                    (name.Contains(normalizedTarget, StringComparison.Ordinal) ||
                     normalizedTarget.Contains(name, StringComparison.Ordinal))));
        }

        private static IEnumerable<string> GetNormalizedNames(NesineTeamIdentity team)
        {
            yield return NormalizeName(team.Name);

            if (!string.IsNullOrWhiteSpace(team.ShortName))
            {
                yield return NormalizeName(team.ShortName);
            }

            if (!string.IsNullOrWhiteSpace(team.Abbreviation))
            {
                yield return NormalizeName(team.Abbreviation);
            }
        }

        private static IEnumerable<NesineTeamIdentity> EnumerateTeams(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("TID", out var idProperty) &&
                    idProperty.TryGetInt32(out var teamId) &&
                    element.TryGetProperty("N", out var nameProperty) &&
                    nameProperty.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(nameProperty.GetString()))
                {
                    var shortName = element.TryGetProperty("NS", out var shortNameProperty) &&
                                    shortNameProperty.ValueKind == JsonValueKind.String
                        ? shortNameProperty.GetString()
                        : null;
                    var abbreviation = element.TryGetProperty("ABR", out var abbreviationProperty) &&
                                       abbreviationProperty.ValueKind == JsonValueKind.String
                        ? abbreviationProperty.GetString()
                        : null;

                    yield return new NesineTeamIdentity(
                        teamId,
                        nameProperty.GetString()!,
                        shortName,
                        abbreviation);
                }

                foreach (var property in element.EnumerateObject())
                {
                    foreach (var team in EnumerateTeams(property.Value))
                    {
                        yield return team;
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var team in EnumerateTeams(item))
                    {
                        yield return team;
                    }
                }
            }
        }

        private static string NormalizeName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);

            foreach (var character in normalized)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(character);
                if (category == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsLetterOrDigit(character))
                {
                    builder.Append(char.ToUpperInvariant(character));
                }
            }

            return builder.ToString();
        }
    }

    public sealed record NesineTeamMatchFeed(
        int RequestedTeamId,
        NesineTeamIdentity? RequestedTeam,
        IReadOnlyList<NesineTeamMatch> Matches,
        string RawJson);

    public sealed record NesineTeamMatch(
        long MatchId,
        int? BettingId,
        int? SportId,
        DateTime? MatchDate,
        string? Season,
        string? RoundName,
        bool IsNeutral,
        NesineTeamIdentity HomeTeam,
        NesineTeamIdentity AwayTeam,
        NesineCompetitionIdentity? Competition,
        int? HomeScore,
        int? AwayScore)
    {
        public bool IsCompleted => HomeScore.HasValue && AwayScore.HasValue;
    }

    public sealed record NesineTeamIdentity(
        int TeamId,
        string Name,
        string? ShortName,
        string? Abbreviation);

    public sealed record NesineCompetitionIdentity(
        int CompetitionId,
        string? Name,
        string? Abbreviation);

    public sealed record NesineMatchTeamIds(
        int MatchOrder,
        NesineTeamIdentity HomeTeam,
        NesineTeamIdentity AwayTeam);
}
