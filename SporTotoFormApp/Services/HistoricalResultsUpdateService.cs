using System.Text.Json;
using System.Text.Json.Serialization;

namespace SporTotoFormApp.Services
{
    public sealed class HistoricalResultsUpdateService
    {
        private const string ApiUrl = "https://webapi.sportoto.gov.tr/api/GameMatch/GetGameMatches/?gameRoundId=";
        private readonly HttpClient _httpClient;

        public HistoricalResultsUpdateService(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(20);
            if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            {
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 SporTotoFormApp/2.0");
            }
        }

        public async Task<HistoricalRefreshResult> RefreshAsync(string appBaseDirectory, CancellationToken cancellationToken = default)
        {
            var targetPath = ResolveHistoryFilePath(appBaseDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            var historicalLines = await DownloadHistoricalLinesAsync(cancellationToken);
            if (historicalLines.Count == 0)
            {
                return new HistoricalRefreshResult(false, targetPath, 0);
            }

            var unique = historicalLines.Distinct().ToList();
            await File.WriteAllLinesAsync(targetPath, unique, cancellationToken);

            return new HistoricalRefreshResult(true, targetPath, unique.Count);
        }

        private async Task<List<string>> DownloadHistoricalLinesAsync(CancellationToken cancellationToken)
        {
            var result = new List<string>();
            var notFoundStreak = 0;

            // Start from known historical range and continue until too many misses.
            for (var roundId = 300; roundId <= 900; roundId++)
            {
                var round = await TryGetRoundAsync(roundId, cancellationToken);
                if (round == null)
                {
                    notFoundStreak++;
                    if (roundId > 500 && notFoundStreak >= 50)
                    {
                        break;
                    }

                    continue;
                }

                notFoundStreak = 0;

                var line = ConvertRoundToPredictionLine(round.Object);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    result.Add(line);
                }
            }

            return result;
        }

        private async Task<RoundResponse?> TryGetRoundAsync(int roundId, CancellationToken cancellationToken)
        {
            try
            {
                using var response = await _httpClient.GetAsync(ApiUrl + roundId, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(content))
                {
                    return null;
                }

                return JsonSerializer.Deserialize<RoundResponse>(content, JsonOptions);
            }
            catch
            {
                return null;
            }
        }

        private static string? ConvertRoundToPredictionLine(List<RoundMatchItem>? items)
        {
            if (items == null || items.Count == 0)
            {
                return null;
            }

            var symbols = new List<char>(15);

            foreach (var item in items)
            {
                var win = item.Match?.FullTimeWin ?? item.Match?.NoterWin;
                if (win is null)
                {
                    return null;
                }

                var symbol = win.Value switch
                {
                    0 => 'X',
                    1 => '1',
                    2 => '2',
                    _ => '\0'
                };

                if (symbol == '\0')
                {
                    return null;
                }

                symbols.Add(symbol);
            }

            if (symbols.Count != 15)
            {
                return null;
            }

            return new string(symbols.ToArray());
        }

        private static string ResolveHistoryFilePath(string appBaseDirectory)
        {
            const string relativePath = "Data/historical_results.txt";
            var current = new DirectoryInfo(appBaseDirectory);

            for (var i = 0; i < 6 && current != null; i++)
            {
                var candidate = Path.Combine(current.FullName, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                current = current.Parent;
            }

            // Fallback to app directory.
            return Path.Combine(appBaseDirectory, relativePath);
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private sealed class RoundResponse
        {
            [JsonPropertyName("object")]
            public List<RoundMatchItem>? Object { get; set; }
        }

        private sealed class RoundMatchItem
        {
            [JsonPropertyName("match")]
            public RoundMatch? Match { get; set; }
        }

        private sealed class RoundMatch
        {
            [JsonPropertyName("fullTimeWin")]
            public int? FullTimeWin { get; set; }

            [JsonPropertyName("noterWin")]
            public int? NoterWin { get; set; }
        }
    }

    public sealed record HistoricalRefreshResult(bool Success, string FilePath, int LineCount);
}
