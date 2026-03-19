using HtmlAgilityPack;
using System.Globalization;

namespace SporTotoFormApp.Client
{
    public sealed class SporTotoClient
    {
        private readonly HttpClient _httpClient;
        private const string RequestUrl = "https://sporzip.com/spor-toto-ne-verir";

        public SporTotoClient(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(20);

            if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            {
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) SporTotoFormApp/2.0");
            }
        }

        public async Task<List<BonusResult>> SubmitPredictionStringAsync(string predictionString, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(predictionString) || predictionString.Length != 15)
            {
                throw new ArgumentException("Tahmin stringi 15 karakter uzunluğunda olmalıdır.", nameof(predictionString));
            }

            const int maxRetry = 3;

            for (var attempt = 1; attempt <= maxRetry; attempt++)
            {
                using var formData = CreateFormData(predictionString);

                try
                {
                    using var response = await _httpClient.PostAsync(RequestUrl, formData, cancellationToken);
                    response.EnsureSuccessStatusCode();

                    var html = await response.Content.ReadAsStringAsync(cancellationToken);
                    var parsed = ParseBonusResults(html);

                    if (parsed.Count > 0)
                    {
                        return parsed;
                    }
                }
                catch when (attempt < maxRetry)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken);
                }
            }

            return new List<BonusResult>();
        }

        private static MultipartFormDataContent CreateFormData(string predictionString)
        {
            var formData = new MultipartFormDataContent();
            for (var i = 0; i < predictionString.Length; i++)
            {
                formData.Add(new StringContent(predictionString[i].ToString().ToLowerInvariant()), $"m_{i + 1}");
            }

            return formData;
        }

        public static List<BonusResult> ParseBonusResults(string html)
        {
            var results = new List<BonusResult>();
            if (string.IsNullOrWhiteSpace(html))
            {
                return results;
            }

            var doc = new HtmlAgilityPack.HtmlDocument();
            doc.LoadHtml(html);

            var rows = doc.DocumentNode.SelectNodes("//tr");
            if (rows == null)
            {
                return results;
            }

            foreach (var row in rows)
            {
                var cells = row.SelectNodes("td");
                if (cells == null || cells.Count() < 3)
                {
                    continue;
                }

                var bilen = HtmlEntity.DeEntitize(cells[0].InnerText).Trim();
                if (!bilen.Contains("BİLEN", StringComparison.OrdinalIgnoreCase) &&
                    !bilen.Contains("BILEN", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var kisiRaw = HtmlEntity.DeEntitize(cells[1].InnerText).Trim();
                var tutar = HtmlEntity.DeEntitize(cells[2].InnerText).Trim();

                var kisiSayisi = kisiRaw.Contains("DEVİR", StringComparison.OrdinalIgnoreCase)
                    ? "0"
                    : ExtractFirstNumberToken(kisiRaw);

                results.Add(new BonusResult
                {
                    Bilen = bilen,
                    KisiSayisi = kisiSayisi,
                    Tutar = tutar
                });
            }

            return results;
        }

        private static string ExtractFirstNumberToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "0";
            }

            var token = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "0";
            var cleaned = new string(token.Where(c => char.IsDigit(c) || c is '.' or ',').ToArray());

            if (double.TryParse(cleaned.Replace('.', ',').Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                return Math.Max(parsed, 0).ToString(CultureInfo.InvariantCulture);
            }

            return "0";
        }
    }

    public sealed class BonusResult
    {
        public string Bilen { get; set; } = string.Empty;
        public string KisiSayisi { get; set; } = "0";
        public string Tutar { get; set; } = string.Empty;

        public override string ToString()
        {
            return $"{Bilen}: {Tutar}";
        }
    }
}
