using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HtmlAgilityPack;
namespace SporTotoFormApp.Client
{
    public class SporTotoClient
    {
        private readonly HttpClient _httpClient;
        private const string RequestUrl = "https://sporzip.com/spor-toto-ne-verir";
        private const string SessionCookie = "PHPSESSID=9c6jh0vbkvlrkc9o4p1ertha85; __rev=9c6jh0vbkvlrkc9o4p1ertha85_1749158249_1";

        public SporTotoClient()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("Cookie", SessionCookie);
        }

        public async Task<List<BonusResult>> SubmitPredictionStringAsync(string predictionString)
        {
            if (predictionString.Length != 15)
                throw new ArgumentException("Tahmin stringi 15 karakter uzunluğunda olmalıdır.");

            using (var formData = new MultipartFormDataContent())
            {
                for (int i = 0; i < 15; i++)
                {
                    string fieldName = $"m_{i + 1}";
                    string value = predictionString[i].ToString(); // "1", "X", veya "2"
                    formData.Add(new StringContent(value), fieldName);
                }

                try
                {
                    var response = await _httpClient.PostAsync(RequestUrl, formData);
                    response.EnsureSuccessStatusCode();
                    var html = await response.Content.ReadAsStringAsync();
                    var bonusResults = ParseBonusResults(html);

                    return bonusResults;
                }
                catch (Exception ex)
                {

                    return null;
                }

            }
        }

        public static List<BonusResult> ParseBonusResults(string html)
        {
            var results = new List<BonusResult>();

            var doc = new HtmlAgilityPack.HtmlDocument();
            doc.LoadHtml(html);

            var rows = doc.DocumentNode.SelectNodes("//tr");
            if (rows == null) return results;

            foreach (var row in rows)
            {
                var cells = row.SelectNodes("td");
                if (cells == null || cells.Count < 3) continue;

                string bilen = cells[0].InnerText.Trim();
                string kisiSayisi;
                string tutar = cells[2].InnerText.Trim();

                if (cells[1].InnerText.Trim().Contains("DEVİR"))
                {
                     kisiSayisi ="0";
                }
                else
                {
                    kisiSayisi = cells[1].InnerText.Split(' ')[0];
                }

                // Sadece "BİLEN" geçen satırları al
                if (bilen.Contains("BİLEN") && !string.IsNullOrWhiteSpace(tutar))
                {
                    results.Add(new BonusResult
                    {
                        Bilen = bilen,
                        KisiSayisi = kisiSayisi,
                        Tutar = tutar
                    });
                }
            }

            return results;
        }
    }


    public class BonusResult
    {
        public string Bilen { get; set; }
        public string KisiSayisi { get; set; }
        public string Tutar { get; set; }

        public override string ToString()
        {
            return $"{Bilen}: {Tutar}";
        }
    }
}
