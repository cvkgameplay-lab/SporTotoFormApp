using OpenQA.Selenium.Chrome;
using OpenQA.Selenium;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SporTotoFormApp.Client;
using SporTotoFormApp.Object;
using SporTotoFormApp.Interfaces;
using AngleSharp.Text;
using OpenQA.Selenium.BiDi.Input;

namespace SporTotoFormApp.Services
{
    public class MoneyFilterService
    {
        private readonly ITestView _view;
        public static IWebDriver driver;
        public static ChromeDriverService service;
        public static List<Coupon> coupons;
        public static List<FilteredCoupon> FilteredCoupons;
        public int kolonSayisi;
        public MoneyFilterService(ITestView view,int kolonSayisi) 
        {
            _view = view;
            this.kolonSayisi = kolonSayisi;
        }
        public  async Task Run()
        {
            int counter = 0;
            int kolonCounter = 0;
            PredictionListHelper predictionListHelper = new PredictionListHelper();

            var readPredictionList = predictionListHelper.FiltreliUret();
            var predictionList = readPredictionList.Distinct().Where(x => !(x.Contains("XXX") || x.Contains("222") || x.Contains("11111")) && x.Contains("1") && x.Contains("X") && x.Contains("2"));
            predictionList = predictionList.Where(x =>
            {
                int count1 = x.Count(c => c == '1');
                int countX = x.Count(c => c == 'X');
                int count2 = x.Count(c => c == '2');

                return count1 >= 5 && count1 <= 9
                    && countX >= 2 && countX <= 6
                    && count2 >= 2 && count2 <= 6;
            });

            //predictionList = predictionList.Where(x => Entropy(x) > 1.3 && Entropy(x) < 1.58);
            //predictionList = predictionList.OrderBy(x => Score(x)).Take(50000);
            var sorted = predictionList
            .AsParallel()              // hız için
            .OrderBy(x => Score(x))
            .Take(600000)              // önce 200K al
            .ToList();

            List<string> final = new();

            foreach (var item in sorted)
            {
                if (final.Any(f => Distance(item, f) < 5))
                    continue;

                final.Add(item);

                if (final.Count == 50000)
                    break;
            }
            predictionList = final;
            string i15, i14, i13, i12;

            coupons = new List<Coupon>();
            _view.Log("Ham Kupon Sayisi :  " + readPredictionList.Count(), Color.Yellow);
            _view.Log("İşlenenecek Kupon Sayisi :  " + predictionList.Count(), Color.Yellow);
            //_view.ProgressBarMaxValue = predictionList.Count();
            _view.ProgressBarMaxValue = kolonSayisi;

            var sporTotoClient = new SporTotoClient();
            //foreach (var prediction in predictionList)
            foreach (var prediction in predictionList)
            {
                try
                {
                    counter++;
                    //_view.ProgressBarValue = counter;

                    var result = await sporTotoClient.SubmitPredictionStringAsync(prediction.ToLower());

                    if (result.FirstOrDefault(x => x.Bilen.Contains("15")).Tutar.Equals("DEVİR"))
                    {
                        continue;
                    }

                    i15 = result.FirstOrDefault(x => x.Bilen.Contains("15")).KisiSayisi.Replace(".", "").Replace(" ₺", "");
                    i14 = result.FirstOrDefault(x => x.Bilen.Contains("14")).KisiSayisi.Replace(".", "").Replace(" ₺", "");
                    i13 = result.FirstOrDefault(x => x.Bilen.Contains("13")).KisiSayisi.Replace(".", "").Replace(" ₺", "");
                    i12 = result.FirstOrDefault(x => x.Bilen.Contains("12")).KisiSayisi.Replace(".", "").Replace(" ₺", "");
                    //i12 = result.FirstOrDefault(x => x.Bilen.Contains("12")).Tutar.Replace(".", "").Replace(" ₺", "");

                    if (!(Convert.ToDouble(i15)<10 && Convert.ToDouble(i15) >= 1))
                      //  if (!(Convert.ToDouble(i15)==1 || Convert.ToDouble(i15)==2 || Convert.ToDouble(i15) == 7))
                    {
                        continue;
                    }

                    kolonCounter++;
                    _view.ProgressBarValue = kolonCounter;
                    Coupon coupon = new Coupon()
                    {
                        prediction = prediction,
                        bonus = new Bonus()
                        {
                            i15 = i15,
                            i14 = i14,
                            i13 = i13,
                            i12 = i12
                        }
                    };

                    coupons.Add(coupon);
                    _view.Log(prediction, Color.LimeGreen);

                    if (kolonCounter==kolonSayisi)
                    {
                        break;
                    }

                }
                catch (Exception ex)
                {
                    _view.Log($"Hata (tahmin: {prediction}): {ex.Message}\n");
                }

            }
            ExcelExporter.ExportCouponsToExcel(coupons, "Kuponlar.xlsx");
           // FilterCoupon();
            NesneListesiniDosyayaYaz();

            return;
        }
        int Distance(string a, string b)
        {
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                    diff++;
            return diff;
        }
        double Entropy(string x)
        {
            int len = x.Length;
            double p1 = x.Count(c => c == '1') / (double)len;
            double p0 = x.Count(c => c == 'X') / (double)len;
            double p2 = x.Count(c => c == '2') / (double)len;

            double e = 0;
            if (p1 > 0) e -= p1 * Math.Log2(p1);
            if (p0 > 0) e -= p0 * Math.Log2(p0);
            if (p2 > 0) e -= p2 * Math.Log2(p2);

            return e;
        }

        double Score(string x)
        {
            int c1 = 0, c0 = 0, c2 = 0;
            int transitions = 0;

            int max1 = 0, max0 = 0, max2 = 0;
            int cur1 = 0, cur0 = 0, cur2 = 0;

            char prev = x[0];

            foreach (char ch in x)
            {
                // Count
                if (ch == '1') { c1++; cur1++; cur0 = cur2 = 0; }
                else if (ch == 'X') { c0++; cur0++; cur1 = cur2 = 0; }
                else { c2++; cur2++; cur1 = cur0 = 0; }

                // Streak max
                if (cur1 > max1) max1 = cur1;
                if (cur0 > max0) max0 = cur0;
                if (cur2 > max2) max2 = cur2;

                // Transition
                if (ch != prev)
                    transitions++;

                prev = ch;
            }

            double score = 0;

            // Distribution
            score += Math.Pow(c1 - 7.5, 2) * 1.5;
            score += Math.Pow(c0 - 3.5, 2) * 1.5;
            score += Math.Pow(c2 - 4.0, 2) * 1.5;

            // Transition band
            if (transitions < 7) score += (7 - transitions) * 2;
            if (transitions > 12) score += (transitions - 12) * 2;

            // Streak threshold
            if (max1 > 4) score += (max1 - 4) * 2;
            if (max0 > 3) score += (max0 - 3) * 2;
            if (max2 > 4) score += (max2 - 4) * 2;

            // Entropy
            int len = x.Length;
            double p1 = c1 / (double)len;
            double p0 = c0 / (double)len;
            double p2 = c2 / (double)len;

            double entropy = 0;
            if (p1 > 0) entropy -= p1 * Math.Log2(p1);
            if (p0 > 0) entropy -= p0 * Math.Log2(p0);
            if (p2 > 0) entropy -= p2 * Math.Log2(p2);

            score += Math.Pow(entropy - 1.53, 2) * 5;

            return score;
        }


        int LongestStreak(string s, char c)
        {
            int max = 0;
            int current = 0;

            foreach (var ch in s)
            {
                if (ch == c)
                {
                    current++;
                    if (current > max)
                        max = current;
                }
                else
                {
                    current = 0;
                }
            }

            return max;
        }

        int TransitionCount(string x)
        {
            int count = 0;

            for (int i = 1; i < x.Length; i++)
            {
                if (x[i] != x[i - 1])
                    count++;
            }

            return count;
        }

        public static void FilterCoupon()
        {
            FilteredCoupons = new List<FilteredCoupon>();

            foreach (var item in coupons)
            {
                //if (Convert.ToDouble(item.bonus.i15) > 4000000 && Convert.ToDouble(item.bonus.i15) < 8000000)
                if (true)
                {
                    FilteredCoupon filteredCoupon = new FilteredCoupon()
                    {
                        prediction = item.prediction,
                        bonus = new doubleBonus()
                        {
                            i15 = Convert.ToDouble(item.bonus.i15),
                            i14 = Convert.ToDouble(item.bonus.i14),
                            i13 = Convert.ToDouble(item.bonus.i13),
                            i12 = Convert.ToDouble(item.bonus.i12),
                        }
                    };
                    FilteredCoupons.Add(filteredCoupon);
                }
            }
        }

        public  void NesneListesiniDosyayaYaz()
        {
            try
            {
                _view.Log("Kupon Sayısı = " + coupons.Count, Color.Yellow);

                string dizin = AppDomain.CurrentDomain.BaseDirectory;
                string dosyaYolu = Path.Combine(dizin, "BestScoreCoupon.txt");

                using (StreamWriter sw = new StreamWriter(dosyaYolu))
                {
                    foreach (var item in coupons)
                    {
                        sw.WriteLine(item.prediction);
                    }
                }

                int matchCount = 15;

                for (int i = 0; i < matchCount; i++)
                {
                    int count1 = 0;
                    int countX = 0;
                    int count2 = 0;

                    foreach (var coupon in coupons)
                    {
                        char prediction = coupon.prediction[i];

                        if (prediction == '1')
                            count1++;
                        else if (prediction == 'X')
                            countX++;
                        else if (prediction == '2')
                            count2++;
                    }

                    _view.Log($"{i+1}.Mac | {count1} || {countX} || {count2} |",Color.Green);
                }


            }
            catch (Exception ex)
            {
                _view.Log("Hata oluştu: " + ex.Message, Color.Crimson);
            }
        }

        public static void PrintMatchMatrix()
        {
            
        }
    }
}
