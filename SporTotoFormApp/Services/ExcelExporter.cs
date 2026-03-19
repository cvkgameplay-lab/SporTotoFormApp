using ClosedXML.Excel;
using SporTotoFormApp.Object;


namespace SporTotoFormApp.Services
{
    public static class ExcelExporter
    {
        public static void ExportCouponsToExcel(List<Coupon> coupons, string filePath)
        {
            var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Kuponlar");

            // Başlıklar
            worksheet.Cell(1, 1).Value = "Tahmin";
            worksheet.Cell(1, 2).Value = "15 Bilen";
            worksheet.Cell(1, 3).Value = "14 Bilen";
            worksheet.Cell(1, 4).Value = "13 Bilen";
            worksheet.Cell(1, 5).Value = "12 Bilen";

            // Satırları doldur
            int row = 2;
            foreach (var coupon in coupons)
            {
                worksheet.Cell(row, 1).Value = coupon.prediction;
                worksheet.Cell(row, 2).Value = ParseTutar(coupon.bonus?.i15);
                worksheet.Cell(row, 3).Value = ParseTutar(coupon.bonus?.i14);
                worksheet.Cell(row, 4).Value = ParseTutar(coupon.bonus?.i13);
                worksheet.Cell(row, 5).Value = ParseTutar(coupon.bonus?.i12);
                row++;
            }

            // Otomatik sütun genişliği
            worksheet.Columns().AdjustToContents();

            // Dosyayı kaydet
            workbook.SaveAs(filePath);
            Console.WriteLine($"\nExcel dosyası oluşturuldu: {filePath}");
        }
        // Yardımcı: string tutarı sayıya çevirir
        private static double ParseTutar(string tutar)
        {
            if (string.IsNullOrWhiteSpace(tutar)) return 0;

            // "3.858 ₺" → "3858"
            var cleaned = new string(tutar
                .Where(c => char.IsDigit(c) || c == ',' || c == '.')
                .ToArray());

            // Türkçe formatta ondalık ayırıcı "," olabilir
            double.TryParse(cleaned, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double value);

            return value;
        }

    }
}