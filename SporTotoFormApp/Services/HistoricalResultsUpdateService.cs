using SporTotoFormApp.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SporTotoFormApp.Services
{
    public sealed class HistoricalResultsUpdateService
    {
        private const string ApiUrl = "https://webapi.sportoto.gov.tr/api/GameMatch/GetGameMatches/?gameRoundId=";
        private const string ResultApiUrl = "https://webapi.sportoto.gov.tr/api/GameResult/GetGameResultByGameRoundId?id=";
        private const int LegacyRoundStart = 300;
        private const int LegacyRoundEnd = 900;
        private const int ModernRoundStart = 1300;
        private const int ModernRoundHardEnd = 1800;
        private const int ModernStopAfterMisses = 30;
        private readonly HttpClient _httpClient;

        public HistoricalResultsUpdateService(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(8);
            if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            {
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 SporTotoFormApp/2.0");
            }
        }

        public async Task<HistoricalRefreshResult> RefreshAsync(string appBaseDirectory, CancellationToken cancellationToken = default)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            var historicalResults = await DownloadHistoricalResultsAsync(timeoutCts.Token);
            if (historicalResults.Count == 0)
            {
                return new HistoricalRefreshResult(false, "SQL Server", 0, 0, 0);
            }

            var savedCount = await new HistoricalResultRepository().ReplaceAllAsync(historicalResults, cancellationToken);
            var payoutCount = historicalResults.Sum(x => x.Payouts.Count);
            var matchCount = historicalResults.Sum(x => x.Matches.Count);

            return new HistoricalRefreshResult(true, "SQL Server", savedCount, payoutCount, matchCount);
        }

        private async Task<List<HistoricalResultImport>> DownloadHistoricalResultsAsync(CancellationToken cancellationToken)
        {
            var result = new List<HistoricalResultImport>();

            await DownloadLegacyRangeAsync(result, cancellationToken);
            await DownloadModernRangeAsync(result, cancellationToken);

            return result
                .DistinctBy(x => x.RoundId ?? 0)
                .OrderBy(x => x.RoundId)
                .ToList();
        }

        private async Task DownloadLegacyRangeAsync(List<HistoricalResultImport> result, CancellationToken cancellationToken)
        {
            var notFoundStreak = 0;

            for (var roundId = LegacyRoundStart; roundId <= LegacyRoundEnd; roundId++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var round = await TryGetRoundAsync(roundId, cancellationToken);
                if (round == null)
                {
                    notFoundStreak++;
                    if (result.Count > 0 && notFoundStreak >= 120)
                    {
                        break;
                    }

                    if (roundId > 600 && notFoundStreak >= 180)
                    {
                        break;
                    }

                    continue;
                }

                notFoundStreak = 0;

                var row = ConvertRoundToHistoricalResult(roundId, round);
                if (row != null)
                {
                    var resultDetail = await TryGetResultAsync(roundId, cancellationToken);
                    result.Add(row with
                    {
                        Payouts = resultDetail?.ToPayouts() ?? row.Payouts
                    });
                }
            }
        }

        private async Task DownloadModernRangeAsync(List<HistoricalResultImport> result, CancellationToken cancellationToken)
        {
            var foundAny = false;
            var notFoundStreak = 0;

            for (var roundId = ModernRoundStart; roundId <= ModernRoundHardEnd; roundId++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var round = await TryGetRoundAsync(roundId, cancellationToken);
                if (round == null)
                {
                    if (foundAny)
                    {
                        notFoundStreak++;
                        if (notFoundStreak >= ModernStopAfterMisses)
                        {
                            break;
                        }
                    }

                    continue;
                }

                var row = ConvertRoundToHistoricalResult(roundId, round);
                if (row == null)
                {
                    if (foundAny)
                    {
                        notFoundStreak++;
                        if (notFoundStreak >= ModernStopAfterMisses)
                        {
                            break;
                        }
                    }

                    continue;
                }

                var resultDetail = await TryGetResultAsync(roundId, cancellationToken);
                foundAny = true;
                notFoundStreak = 0;

                result.Add(row with
                {
                    Payouts = resultDetail?.ToPayouts() ?? row.Payouts
                });
            }
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

                using var document = JsonDocument.Parse(content);
                var metadata = RoundMetadata.FromJson(document.RootElement);
                var payouts = RoundPayoutParser.FromJson(document.RootElement);
                var typed = JsonSerializer.Deserialize<RoundResponse>(content, JsonOptions);

                return typed == null
                    ? null
                    : typed with { Metadata = metadata, Payouts = payouts };
            }
            catch
            {
                return null;
            }
        }

        private async Task<GameResultObject?> TryGetResultAsync(int roundId, CancellationToken cancellationToken)
        {
            try
            {
                using var response = await _httpClient.GetAsync(ResultApiUrl + roundId, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(content))
                {
                    return null;
                }

                var typed = JsonSerializer.Deserialize<GameResultResponse>(content, JsonOptions);
                return typed?.IsSucceed == true ? typed.Object : null;
            }
            catch
            {
                return null;
            }
        }

        private static HistoricalResultImport? ConvertRoundToHistoricalResult(int roundId, RoundResponse round)
        {
            var line = ConvertRoundToPredictionLine(round.Object);
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            return new HistoricalResultImport(
                roundId,
                line,
                round.Metadata.SeasonYear,
                round.Metadata.WeekNumber,
                round.Metadata.RoundName,
                round.Payouts,
                ConvertRoundToMatches(round.Object));
        }

        private static IReadOnlyList<HistoricalMatchImport> ConvertRoundToMatches(List<RoundMatchItem>? items)
        {
            if (items == null || items.Count == 0)
            {
                return [];
            }

            var result = new List<HistoricalMatchImport>(items.Count);

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var match = item.Match;
                var win = match?.FullTimeWin ?? match?.NoterWin;
                var symbol = win switch
                {
                    0 => 'X',
                    1 => '1',
                    2 => '2',
                    _ => '\0'
                };

                if (match == null || symbol == '\0')
                {
                    continue;
                }

                result.Add(new HistoricalMatchImport(
                    i + 1,
                    match.ExternalMatchId,
                    match.Date,
                    ToTeamImport(match.HomeTeam),
                    ToTeamImport(match.AwayTeam),
                    match.TournamentId,
                    null,
                    match.StageId,
                    match.Stage?.Name,
                    match.Round?.Name,
                    symbol,
                    match.Score?.HomeRegular,
                    match.Score?.AwayRegular));
            }

            return result;
        }

        private static HistoricalTeamImport ToTeamImport(RoundTeam? team)
        {
            return new HistoricalTeamImport(
                team?.Id,
                team?.ExternalTeamId,
                team?.Name,
                team?.ShortName,
                team?.MediumName,
                team?.CountryId);
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

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private sealed record RoundResponse
        {
            [JsonPropertyName("object")]
            public List<RoundMatchItem>? Object { get; init; }

            public RoundMetadata Metadata { get; init; } = RoundMetadata.Empty;

            public IReadOnlyList<HistoricalPrizeImport> Payouts { get; init; } = [];
        }

        private sealed class RoundMatchItem
        {
            [JsonPropertyName("gameRoundName")]
            public string? GameRoundName { get; set; }

            [JsonPropertyName("match")]
            public RoundMatch? Match { get; set; }
        }

        private sealed class RoundMatch
        {
            [JsonPropertyName("date")]
            public DateTime? Date { get; set; }

            [JsonPropertyName("externalMatchId")]
            public int? ExternalMatchId { get; set; }

            [JsonPropertyName("tournamentId")]
            public int? TournamentId { get; set; }

            [JsonPropertyName("stageId")]
            public int? StageId { get; set; }

            [JsonPropertyName("fullTimeWin")]
            public int? FullTimeWin { get; set; }

            [JsonPropertyName("noterWin")]
            public int? NoterWin { get; set; }

            [JsonPropertyName("score")]
            public RoundScore? Score { get; set; }

            [JsonPropertyName("round")]
            public RoundLeagueRound? Round { get; set; }

            [JsonPropertyName("stage")]
            public RoundStage? Stage { get; set; }

            [JsonPropertyName("homeTeam")]
            public RoundTeam? HomeTeam { get; set; }

            [JsonPropertyName("awayTeam")]
            public RoundTeam? AwayTeam { get; set; }
        }

        private sealed class RoundScore
        {
            [JsonPropertyName("homeRegular")]
            public int? HomeRegular { get; set; }

            [JsonPropertyName("awayRegular")]
            public int? AwayRegular { get; set; }
        }

        private sealed class RoundLeagueRound
        {
            [JsonPropertyName("name")]
            public string? Name { get; set; }
        }

        private sealed class RoundStage
        {
            [JsonPropertyName("name")]
            public string? Name { get; set; }
        }

        private sealed class RoundTeam
        {
            [JsonPropertyName("id")]
            public int? Id { get; set; }

            [JsonPropertyName("externalTeamId")]
            public int? ExternalTeamId { get; set; }

            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("shortName")]
            public string? ShortName { get; set; }

            [JsonPropertyName("mediumName")]
            public string? MediumName { get; set; }

            [JsonPropertyName("countryId")]
            public int? CountryId { get; set; }
        }

        private sealed class GameResultResponse
        {
            [JsonPropertyName("object")]
            public GameResultObject? Object { get; set; }

            [JsonPropertyName("isSucceed")]
            public bool IsSucceed { get; set; }
        }

        private sealed class GameResultObject
        {
            [JsonPropertyName("fifteenWinPrize")]
            public decimal? FifteenWinPrize { get; set; }

            [JsonPropertyName("fifteenWinCount")]
            public int? FifteenWinCount { get; set; }

            [JsonPropertyName("fourteenWinPrize")]
            public decimal? FourteenWinPrize { get; set; }

            [JsonPropertyName("fourteenWinCount")]
            public int? FourteenWinCount { get; set; }

            [JsonPropertyName("thirteenWinPrize")]
            public decimal? ThirteenWinPrize { get; set; }

            [JsonPropertyName("thirteenWinCount")]
            public int? ThirteenWinCount { get; set; }

            [JsonPropertyName("twelveWinPrize")]
            public decimal? TwelveWinPrize { get; set; }

            [JsonPropertyName("twelveWinCount")]
            public int? TwelveWinCount { get; set; }

            public IReadOnlyList<HistoricalPrizeImport> ToPayouts()
            {
                return
                [
                    new HistoricalPrizeImport(15, FifteenWinCount, FifteenWinPrize, FormatPrizeText(FifteenWinPrize)),
                    new HistoricalPrizeImport(14, FourteenWinCount, FourteenWinPrize, FormatPrizeText(FourteenWinPrize)),
                    new HistoricalPrizeImport(13, ThirteenWinCount, ThirteenWinPrize, FormatPrizeText(ThirteenWinPrize)),
                    new HistoricalPrizeImport(12, TwelveWinCount, TwelveWinPrize, FormatPrizeText(TwelveWinPrize))
                ];
            }

            private static string? FormatPrizeText(decimal? value)
            {
                return value?.ToString("0.00", CultureInfo.InvariantCulture);
            }
        }

        private sealed record RoundMetadata(int? SeasonYear, int? WeekNumber, string? RoundName)
        {
            public static RoundMetadata Empty { get; } = new(null, null, null);

            public static RoundMetadata FromJson(JsonElement root)
            {
                var roundName = FindString(root, IsRoundNameProperty);
                var parsedFromName = ParseRoundName(roundName);

                return new RoundMetadata(
                    FindInt(root, IsYearProperty, value => value is >= 2000 and <= 2100) ?? parsedFromName.SeasonYear,
                    FindInt(root, IsWeekProperty, value => value is >= 1 and <= 60) ?? parsedFromName.WeekNumber,
                    roundName);
            }

            private static RoundMetadata ParseRoundName(string? roundName)
            {
                if (string.IsNullOrWhiteSpace(roundName))
                {
                    return Empty;
                }

                var seasonMatch = Regex.Match(roundName, @"(?<year>20\d{2})\s*/\s*20\d{2}");
                var weekMatch = Regex.Match(roundName, @"(?<week>\d{1,2})\s*\.\s*Hafta", RegexOptions.IgnoreCase);

                var seasonYear = seasonMatch.Success &&
                    int.TryParse(seasonMatch.Groups["year"].Value, out var parsedYear)
                        ? parsedYear
                        : null as int?;

                var weekNumber = weekMatch.Success &&
                    int.TryParse(weekMatch.Groups["week"].Value, out var parsedWeek)
                        ? parsedWeek
                        : null as int?;

                return new RoundMetadata(seasonYear, weekNumber, roundName);
            }

            private static int? FindInt(JsonElement element, Func<string, bool> propertyNameMatch, Func<int, bool> valueMatch)
            {
                foreach (var property in EnumerateProperties(element))
                {
                    if (!propertyNameMatch(property.Name))
                    {
                        continue;
                    }

                    if (property.Value.ValueKind == JsonValueKind.Number &&
                        property.Value.TryGetInt32(out var number) &&
                        valueMatch(number))
                    {
                        return number;
                    }

                    if (property.Value.ValueKind == JsonValueKind.String &&
                        int.TryParse(property.Value.GetString(), out number) &&
                        valueMatch(number))
                    {
                        return number;
                    }
                }

                return null;
            }

            private static string? FindString(JsonElement element, Func<string, bool> propertyNameMatch)
            {
                foreach (var property in EnumerateProperties(element))
                {
                    if (propertyNameMatch(property.Name) && property.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = property.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            return value.Length <= 100 ? value : value[..100];
                        }
                    }
                }

                return null;
            }

            private static IEnumerable<JsonProperty> EnumerateProperties(JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in element.EnumerateObject())
                    {
                        yield return property;

                        foreach (var child in EnumerateProperties(property.Value))
                        {
                            yield return child;
                        }
                    }
                }
                else if (element.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in element.EnumerateArray())
                    {
                        foreach (var property in EnumerateProperties(item))
                        {
                            yield return property;
                        }
                    }
                }
            }

            private static bool IsYearProperty(string name)
            {
                return name.Contains("year", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("season", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("yil", StringComparison.OrdinalIgnoreCase);
            }

            private static bool IsWeekProperty(string name)
            {
                return name.Contains("week", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("hafta", StringComparison.OrdinalIgnoreCase);
            }

            private static bool IsRoundNameProperty(string name)
            {
                return name.Contains("roundName", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("name", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("title", StringComparison.OrdinalIgnoreCase);
            }
        }

        private static class RoundPayoutParser
        {
            public static IReadOnlyList<HistoricalPrizeImport> FromJson(JsonElement root)
            {
                var payouts = new List<HistoricalPrizeImport>();

                foreach (var element in EnumerateObjects(root))
                {
                    var payout = TryCreatePayout(element);
                    if (payout != null)
                    {
                        payouts.Add(payout);
                    }
                }

                return payouts
                    .GroupBy(x => x.HitCount)
                    .Select(x => x.First())
                    .OrderByDescending(x => x.HitCount)
                    .ToList();
            }

            private static HistoricalPrizeImport? TryCreatePayout(JsonElement element)
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                int? hitCount = null;
                int? winnerCount = null;
                decimal? amount = null;
                string? amountText = null;

                foreach (var property in element.EnumerateObject())
                {
                    var name = property.Name;
                    var valueText = GetScalarText(property.Value);

                    if (hitCount == null && IsHitCountProperty(name))
                    {
                        hitCount = ExtractHitCount(valueText);
                    }

                    if (winnerCount == null && IsWinnerCountProperty(name))
                    {
                        winnerCount = ExtractInteger(valueText);
                    }

                    if (amount == null && IsAmountProperty(name))
                    {
                        amountText = valueText;
                        amount = ExtractDecimal(valueText);
                    }

                    if (hitCount == null && valueText != null && IsHitText(valueText))
                    {
                        hitCount = ExtractHitCount(valueText);
                    }
                }

                if (hitCount is not >= 12 or not <= 15)
                {
                    return null;
                }

                if (winnerCount == null && amount == null && string.IsNullOrWhiteSpace(amountText))
                {
                    return null;
                }

                return new HistoricalPrizeImport(hitCount.Value, winnerCount, amount, amountText);
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

            private static string? GetScalarText(JsonElement value)
            {
                return value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Number => value.GetRawText(),
                    _ => null
                };
            }

            private static bool IsHitCountProperty(string name)
            {
                return name.Contains("bilen", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("hit", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("matchCount", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("correct", StringComparison.OrdinalIgnoreCase);
            }

            private static bool IsWinnerCountProperty(string name)
            {
                return name.Contains("kisi", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("winner", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("person", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("count", StringComparison.OrdinalIgnoreCase);
            }

            private static bool IsAmountProperty(string name)
            {
                return name.Contains("ikramiye", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("tutar", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("amount", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("prize", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("payout", StringComparison.OrdinalIgnoreCase);
            }

            private static bool IsHitText(string value)
            {
                return value.Contains("bilen", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains("correct", StringComparison.OrdinalIgnoreCase);
            }

            private static int? ExtractHitCount(string? value)
            {
                var number = ExtractInteger(value);
                return number is >= 12 and <= 15 ? number : null;
            }

            private static int? ExtractInteger(string? value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return null;
                }

                var digits = new string(value.Where(char.IsDigit).ToArray());
                return int.TryParse(digits, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;
            }

            private static decimal? ExtractDecimal(string? value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return null;
                }

                var cleaned = new string(value.Where(c => char.IsDigit(c) || c is '.' or ',').ToArray());
                if (string.IsNullOrWhiteSpace(cleaned))
                {
                    return null;
                }

                cleaned = cleaned.Replace(".", string.Empty).Replace(',', '.');
                return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;
            }
        }
    }

    public sealed record HistoricalRefreshResult(bool Success, string Target, int LineCount, int PayoutCount, int MatchCount);
}
