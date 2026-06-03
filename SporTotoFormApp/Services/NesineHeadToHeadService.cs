using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SporTotoFormApp.Services
{
    public sealed class NesineHeadToHeadService
    {
        private static readonly IReadOnlyList<NesineHeadToHeadEndpoint> ExtraEndpoints =
        [
            new("CompetitionHistory", "v3", "CompetitionHistory"),
            new("LastMatches", "v3", "LastMatches"),
            new("CornerAndCards", "v3", "CornerAndCards"),
            new("LineUps", "v4", "LineUps"),
            new("Referee", "v3", "Referee"),
            new("LeagueTable", "v3", "LeagueTable"),
            new("Fixture", "v4", "Fixture")
        ];

        private readonly HttpClient _httpClient;

        public NesineHeadToHeadService(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(12);
            if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            {
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 SporTotoFormApp/2.0");
            }
        }

        public async Task<NesineHeadToHeadSummary?> GetSummaryAsync(int bahisKod, CancellationToken cancellationToken = default)
        {
            if (bahisKod <= 0)
            {
                return null;
            }

            try
            {
                var url = $"https://apistats.nesine.com/api/v3/HeadToHead/{bahisKod}/Summary?competitionHistoryCount=6";
                using var response = await _httpClient.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(content))
                {
                    return null;
                }

                var raw = JsonSerializer.Deserialize<NesineHeadToHeadResponse>(content, JsonOptions);
                if (raw?.Data == null)
                {
                    return null;
                }

                using var document = JsonDocument.Parse(content);
                var root = document.RootElement;
                var odds = ExtractMainOdds(root);
                var h2h = ExtractH2H(root);
                var missingPlayers = CountMissingPlayers(root);

                return new NesineHeadToHeadSummary(
                    bahisKod,
                    h2h.Count1,
                    h2h.CountX,
                    h2h.Count2,
                    odds.HomeOdd,
                    odds.DrawOdd,
                    odds.AwayOdd,
                    missingPlayers.HomeMissing,
                    missingPlayers.AwayMissing,
                    content);
            }
            catch
            {
                return null;
            }
        }

        public async Task<IReadOnlyList<NesineHeadToHeadExtraSnapshot>> GetExtraSnapshotsAsync(
            int bahisKod,
            CancellationToken cancellationToken = default)
        {
            var result = new List<NesineHeadToHeadExtraSnapshot>(ExtraEndpoints.Count);
            if (bahisKod <= 0)
            {
                return result;
            }

            foreach (var endpoint in ExtraEndpoints)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var url = $"https://apistats.nesine.com/api/{endpoint.ApiVersion}/HeadToHead/{bahisKod}/{endpoint.Path}";
                try
                {
                    using var response = await _httpClient.GetAsync(url, cancellationToken);
                    var content = await response.Content.ReadAsStringAsync(cancellationToken);
                    var hasData = response.IsSuccessStatusCode && HasData(content);

                    result.Add(new NesineHeadToHeadExtraSnapshot(
                        bahisKod,
                        endpoint.Name,
                        endpoint.ApiVersion,
                        (int)response.StatusCode,
                        hasData,
                        string.IsNullOrWhiteSpace(content) ? null : content));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result.Add(new NesineHeadToHeadExtraSnapshot(
                        bahisKod,
                        endpoint.Name,
                        endpoint.ApiVersion,
                        0,
                        false,
                        ex.Message));
                }
            }

            return result;
        }

        private static bool HasData(string? content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(content);
                if (!document.RootElement.TryGetProperty("d", out var data))
                {
                    return false;
                }

                return data.ValueKind switch
                {
                    JsonValueKind.Null => false,
                    JsonValueKind.Array => data.GetArrayLength() > 0,
                    JsonValueKind.Object => data.EnumerateObject().Any(),
                    JsonValueKind.String => !string.IsNullOrWhiteSpace(data.GetString()),
                    _ => true
                };
            }
            catch
            {
                return false;
            }
        }

        private static (decimal? HomeOdd, decimal? DrawOdd, decimal? AwayOdd) ExtractMainOdds(JsonElement root)
        {
            decimal? home = null;
            decimal? draw = null;
            decimal? away = null;

            foreach (var odds in FindProperties(root, "ODDS"))
            {
                if (odds.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in odds.EnumerateArray())
                {
                    if (!TryGetInt(item, "GTID", out var gtid) || gtid != 1 ||
                        !TryGetInt(item, "SID", out var sid) ||
                        !TryGetDecimal(item, "ODD", out var odd))
                    {
                        continue;
                    }

                    if (sid == 1)
                    {
                        home ??= odd;
                    }
                    else if (sid == 2)
                    {
                        draw ??= odd;
                    }
                    else if (sid == 3)
                    {
                        away ??= odd;
                    }
                }

                if (home.HasValue && draw.HasValue && away.HasValue)
                {
                    break;
                }
            }

            return (home, draw, away);
        }

        private static (int Count1, int CountX, int Count2) ExtractH2H(JsonElement root)
        {
            var count1 = 0;
            var countX = 0;
            var count2 = 0;

            foreach (var match in FindProperties(root, "SC").SelectMany(x => x.ValueKind == JsonValueKind.Array
                ? x.EnumerateArray()
                : []))
            {
                if (!TryGetInt(match, "TY", out var type) || type != 2 ||
                    !TryGetInt(match, "HTS", out var homeScore) ||
                    !TryGetInt(match, "ATS", out var awayScore))
                {
                    continue;
                }

                if (homeScore > awayScore)
                {
                    count1++;
                }
                else if (homeScore == awayScore)
                {
                    countX++;
                }
                else
                {
                    count2++;
                }
            }

            return (count1, countX, count2);
        }

        private static (int HomeMissing, int AwayMissing) CountMissingPlayers(JsonElement root)
        {
            var home = 0;
            var away = 0;

            foreach (var property in FindProperties(root, "MSP"))
            {
                if (property.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var _ in property.EnumerateArray())
                {
                    away++;
                }
            }

            return (home, away);
        }

        private static IEnumerable<JsonElement> FindProperties(JsonElement element, string propertyName)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals(propertyName))
                    {
                        yield return property.Value;
                    }

                    foreach (var child in FindProperties(property.Value, propertyName))
                    {
                        yield return child;
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var child in FindProperties(item, propertyName))
                    {
                        yield return child;
                    }
                }
            }
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

        private static bool TryGetDecimal(JsonElement element, string propertyName, out decimal value)
        {
            value = 0;
            if (!element.TryGetProperty(propertyName, out var property))
            {
                return false;
            }

            return property.ValueKind switch
            {
                JsonValueKind.Number => property.TryGetDecimal(out value),
                JsonValueKind.String => decimal.TryParse(property.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value),
                _ => false
            };
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private sealed class NesineHeadToHeadResponse
        {
            [JsonPropertyName("d")]
            public object? Data { get; set; }
        }
    }

    public sealed record NesineHeadToHeadSummary(
        int BahisKod,
        int H2HHomeWinCount,
        int H2HDrawCount,
        int H2HAwayWinCount,
        decimal? HomeOdd,
        decimal? DrawOdd,
        decimal? AwayOdd,
        int HomeMissingPlayerCount,
        int AwayMissingPlayerCount,
        string RawJson);

    public sealed record NesineHeadToHeadExtraSnapshot(
        int BahisKod,
        string EndpointName,
        string ApiVersion,
        int StatusCode,
        bool HasData,
        string? RawJson);

    public sealed record NesineHeadToHeadEndpoint(string Name, string ApiVersion, string Path);
}
