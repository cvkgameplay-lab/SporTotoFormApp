using System.Text.Json;
using System.Text.Json.Serialization;

namespace SporTotoFormApp.Services
{
    public sealed class NesineProgramService
    {
        private const string ProgramUrl = "https://st.nesine.com/v2/Program";
        private readonly HttpClient _httpClient;

        public NesineProgramService(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(10);
            if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            {
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 SporTotoFormApp/2.0");
            }
        }

        public async Task<NesineProgram?> GetProgramAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var response = await _httpClient.GetAsync(ProgramUrl, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(content))
                {
                    return null;
                }

                var raw = JsonSerializer.Deserialize<NesineProgramResponse>(content, JsonOptions);
                if (raw?.Data?.Matches == null)
                {
                    return null;
                }

                var matches = raw.Data.Matches
                    .Where(x => x.MatchNo is >= 1 and <= 15)
                    .Select(x => new NesineMatchPopularity(
                        x.MatchNo,
                        x.BahisKod,
                        x.HomeTeam ?? string.Empty,
                        x.AwayTeam ?? string.Empty,
                        x.Percentage1,
                        x.Percentage0,
                        x.Percentage2))
                    .ToDictionary(x => x.MatchNo);

                return new NesineProgram(raw.Data.ProgramNo, raw.Data.Week, raw.Data.ProgramEndDate, matches);
            }
            catch
            {
                return null;
            }
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private sealed class NesineProgramResponse
        {
            [JsonPropertyName("d")]
            public NesineProgramData? Data { get; set; }
        }

        private sealed class NesineProgramData
        {
            [JsonPropertyName("pNo")]
            public int ProgramNo { get; set; }

            [JsonPropertyName("week")]
            public int Week { get; set; }

            [JsonPropertyName("programEndDate")]
            public DateTimeOffset? ProgramEndDate { get; set; }

            [JsonPropertyName("matches")]
            public List<NesineProgramMatch>? Matches { get; set; }
        }

        private sealed class NesineProgramMatch
        {
            [JsonPropertyName("matchNo")]
            public int MatchNo { get; set; }

            [JsonPropertyName("homeTeam")]
            public string? HomeTeam { get; set; }

            [JsonPropertyName("awayTeam")]
            public string? AwayTeam { get; set; }

            [JsonPropertyName("bahiskod")]
            public int BahisKod { get; set; }

            [JsonPropertyName("percentage1")]
            public int Percentage1 { get; set; }

            [JsonPropertyName("percentage0")]
            public int Percentage0 { get; set; }

            [JsonPropertyName("percentage2")]
            public int Percentage2 { get; set; }
        }
    }

    public sealed record NesineProgram(
        int ProgramNo,
        int Week,
        DateTimeOffset? ProgramEndDate,
        IReadOnlyDictionary<int, NesineMatchPopularity> Matches);

    public sealed record NesineMatchPopularity(
        int MatchNo,
        int BahisKod,
        string HomeTeam,
        string AwayTeam,
        int Percentage1,
        int PercentageX,
        int Percentage2);
}
