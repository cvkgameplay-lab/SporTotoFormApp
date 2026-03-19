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

            worksheet.Cell(1, 1).Value = "Tahmin";
            worksheet.Cell(1, 2).Value = "15 Bilen Kisi";
            worksheet.Cell(1, 3).Value = "14 Bilen Kisi";
            worksheet.Cell(1, 4).Value = "13 Bilen Kisi";
            worksheet.Cell(1, 5).Value = "12 Bilen Kisi";
            worksheet.Cell(1, 6).Value = "Utility";
            worksheet.Cell(1, 7).Value = "P15";
            worksheet.Cell(1, 8).Value = "P14";
            worksheet.Cell(1, 9).Value = "P13";

            int row = 2;
            foreach (var coupon in coupons)
            {
                worksheet.Cell(row, 1).Value = coupon.prediction;
                worksheet.Cell(row, 2).Value = ParseCount(coupon.bonus?.i15);
                worksheet.Cell(row, 3).Value = ParseCount(coupon.bonus?.i14);
                worksheet.Cell(row, 4).Value = ParseCount(coupon.bonus?.i13);
                worksheet.Cell(row, 5).Value = ParseCount(coupon.bonus?.i12);
                worksheet.Cell(row, 6).Value = coupon.Utility;
                worksheet.Cell(row, 7).Value = coupon.P15Probability;
                worksheet.Cell(row, 8).Value = coupon.P14Probability;
                worksheet.Cell(row, 9).Value = coupon.P13Probability;
                row++;
            }

            worksheet.Columns().AdjustToContents();

            workbook.SaveAs(filePath);
            Console.WriteLine($"\nExcel dosyası oluşturuldu: {filePath}");
        }

        private static double ParseCount(string? countText)
        {
            if (string.IsNullOrWhiteSpace(countText))
            {
                return 0;
            }

            var cleaned = new string(countText.Where(char.IsDigit).ToArray());
            if (double.TryParse(cleaned, out var value))
            {
                return value;
            }

            return 0;
        }

    }
}
