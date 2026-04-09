using SporTotoFormApp.Interfaces;
using SporTotoFormApp.Object;
using SporTotoFormApp.Services;
using System.Diagnostics;

namespace SporTotoFormApp
{
    public partial class Form1 : Form, ITestView
    {
        private readonly List<ProfileUi> _profiles = [];
        private TabControl _profileTabs = null!;
        private ToolTip _toolTip = null!;

        public int ProgressBarValue
        {
            get => progressBar1.Value;
            set
            {
                var target = Math.Clamp(value, progressBar1.Minimum, progressBar1.Maximum);
                InvokeOnUiThread(() =>
                {
                    progressBar1.Value = target;
                    label1.Text = target.ToString();
                });
            }
        }

        public int ProgressBarMaxValue
        {
            get => progressBar1.Maximum;
            set
            {
                var max = Math.Max(value, 1);
                InvokeOnUiThread(() => progressBar1.Maximum = max);
            }
        }

        public Form1()
        {
            InitializeComponent();
            ConfigureLayout();
            BuildProfileTabs();
            UpdateTotalCouponCount();
        }

        private async void button1_Click(object sender, EventArgs e)
        {
            var requests = BuildProfileRequests();
            if (requests.Count == 0)
            {
                Log("En az bir profilde kolon sayisi 1 veya daha buyuk olmali.", Color.OrangeRed);
                return;
            }

            button1.Enabled = false;
            progressBar1.Minimum = 0;
            progressBar1.Value = 0;
            rtb_log.Clear();

            try
            {
                var targetTotal = requests.Sum(x => x.DesiredCouponCount);
                var combined = new List<Coupon>(targetTotal * 2);
                var processed = 0;
                var refreshHistoricalData = true;

                ProgressBarMaxValue = targetTotal;
                ProgressBarValue = 0;

                foreach (var request in requests)
                {
                    Log($"{request.Name} basladi | Hedef kolon: {request.DesiredCouponCount}", Color.DeepSkyBlue);
                    var service = new MoneyFilterService(this, request.DesiredCouponCount, request.Options);
                    var profileCoupons = await service.Run(
                        persistOutputs: false,
                        refreshHistoricalData: refreshHistoricalData,
                        manageProgress: false);

                    refreshHistoricalData = false;
                    combined.AddRange(profileCoupons);
                    processed += Math.Min(profileCoupons.Count, request.DesiredCouponCount);
                    ProgressBarValue = processed;

                    Log($"{request.Name} tamamlandi | Uretilen: {profileCoupons.Count}", Color.DeepSkyBlue);
                }

                var merged = DeduplicateCoupons(combined);
                var duplicateCount = combined.Count - merged.Count;
                if (duplicateCount > 0)
                {
                    Log($"Profiller arasi duplicate temizlendi: {duplicateCount}", Color.Orange);
                }

                var finalCoupons = merged
                    .OrderByDescending(x => x.Utility)
                    .Take(targetTotal)
                    .ToList();

                if (finalCoupons.Count < targetTotal)
                {
                    Log($"Uyari: Hedef toplam {targetTotal}, elde edilen {finalCoupons.Count}.", Color.Orange);
                }

                SaveCombinedOutputs(finalCoupons);
                ProgressBarValue = finalCoupons.Count;
                Log("Tum profiller tamamlandi.", Color.LimeGreen);
            }
            catch (Exception ex)
            {
                Log($"Beklenmeyen hata: {ex.Message}", Color.Crimson);
            }
            finally
            {
                button1.Enabled = true;
            }
        }

        public void Log(string message)
        {
            Log(message, Color.Gainsboro);
        }

        public void Log(string message, Color color)
        {
            InvokeOnUiThread(() =>
            {
                rtb_log.SelectionStart = rtb_log.TextLength;
                rtb_log.SelectionLength = 0;
                rtb_log.SelectionColor = color;
                rtb_log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
                rtb_log.ScrollToCaret();
            });
        }

        private void rtb_log_TextChanged(object sender, EventArgs e)
        {
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            UpdateTotalCouponCount();
        }

        private void textBox1_TextChanged(object sender, EventArgs e)
        {
        }

        private void button2_Click(object sender, EventArgs e)
        {
            var path = Path.Combine(Application.StartupPath, "BestScoreCoupon.txt");

            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        }

        private void ConfigureLayout()
        {
            ClientSize = new Size(1240, 580);

            progressBar1.Location = new Point(12, 20);
            progressBar1.Size = new Size(1216, 23);

            label1.Location = new Point(12, 48);

            label2.Location = new Point(12, 76);
            label2.Text = "Toplam Kolon";

            textBox1.Location = new Point(100, 73);
            textBox1.Size = new Size(80, 23);
            textBox1.ReadOnly = true;

            button1.Location = new Point(200, 64);
            button1.Size = new Size(170, 36);
            button1.Text = "ÇALIŞTIR";

            rtb_log.Location = new Point(12, 110);
            rtb_log.Size = new Size(840, 420);

            button2.Location = new Point(12, 538);
            button2.Size = new Size(840, 32);
        }

        private void BuildProfileTabs()
        {
            _toolTip = new ToolTip
            {
                AutoPopDelay = 10000,
                InitialDelay = 250,
                ReshowDelay = 150,
                ShowAlways = true
            };

            _profileTabs = new TabControl
            {
                Location = new Point(870, 110),
                Size = new Size(360, 460),
                Name = "profileTabs"
            };

            _profiles.Clear();
            _profiles.Add(CreateProfileTab("Profil A (Dengeli)", 12, 25, 75, 600000, 140000, 130, 6, 3, 3, 25000));
            _profiles.Add(CreateProfileTab("Profil B (Guvenli)", 10, 35, 70, 500000, 120000, 110, 6, 2, 2, 15000));
            _profiles.Add(CreateProfileTab("Profil C (Surpriz)", 8, 15, 90, 700000, 170000, 150, 6, 4, 4, 30000));

            Controls.Add(_profileTabs);
        }

        private ProfileUi CreateProfileTab(
            string profileName,
            int defaultCouponCount,
            int defaultI15Min,
            int defaultI15Max,
            int defaultInitialTopLimit,
            int defaultDiversePrePool,
            int defaultApiBudgetMultiplier,
            int defaultApiConcurrency,
            int defaultMinDistance,
            int defaultMinDistanceFinal,
            int defaultMonteCarlo)
        {
            var page = new TabPage(profileName);

            var couponCount = AddNumericInput(
                page,
                "Kolon Sayisi",
                "Bu profilden kac kolon uretilecegini belirler.",
                defaultCouponCount,
                0,
                200,
                18);

            couponCount.ValueChanged += (_, _) => UpdateTotalCouponCount();

            var apiGroup = new GroupBox
            {
                Text = "API Filtre Ayarlari",
                Location = new Point(6, 52),
                Size = new Size(336, 96)
            };

            var i15Min = AddNumericInput(
                apiGroup,
                "i15 Min",
                "API donusunde 15 bilen kisi sayisi bu degerin altindaysa kupon elenir.",
                defaultI15Min,
                0,
                100000,
                24);

            var i15Max = AddNumericInput(
                apiGroup,
                "i15 Max",
                "API donusunde 15 bilen kisi sayisi bu degerin ustundeyse kupon elenir.",
                defaultI15Max,
                0,
                100000,
                58);

            var optimizationGroup = new GroupBox
            {
                Text = "OptimizationOptions",
                Location = new Point(6, 154),
                Size = new Size(336, 258)
            };

            var initialTopLimit = AddNumericInput(
                optimizationGroup,
                "InitialTopCandidateLimit",
                "On skorlama sonrasi tutulacak maksimum aday kupon sayisi (Top-K).",
                defaultInitialTopLimit,
                1000,
                5000000,
                24);

            var diversePrePool = AddNumericInput(
                optimizationGroup,
                "DiversePrePoolLimit",
                "Cesitlilik filtresi sonrasi API'ye gitmeden once tutulacak aday havuzu limiti.",
                defaultDiversePrePool,
                1000,
                5000000,
                58);

            var apiBudgetMultiplier = AddNumericInput(
                optimizationGroup,
                "ApiBudgetMultiplier",
                "API'de degerlendirilecek kupon butcesi = hedef kolon * bu carpim.",
                defaultApiBudgetMultiplier,
                1,
                10000,
                92);

            var apiConcurrency = AddNumericInput(
                optimizationGroup,
                "ApiConcurrency",
                "Ayni anda kac API cagrisi yapilacagini belirler.",
                defaultApiConcurrency,
                1,
                128,
                126);

            var minDistance = AddNumericInput(
                optimizationGroup,
                "MinHammingDistance",
                "On havuzda iki kupon arasindaki minimum fark (karakter bazli mesafe).",
                defaultMinDistance,
                1,
                15,
                160);

            var minDistanceFinal = AddNumericInput(
                optimizationGroup,
                "MinHammingDistanceFinal",
                "Final secimde iki kupon arasindaki minimum fark.",
                defaultMinDistanceFinal,
                1,
                15,
                194);

            var monteCarlo = AddNumericInput(
                optimizationGroup,
                "MonteCarloScenarioCount",
                "Portfoy optimizasyonunda simulasyon icin uretilecek senaryo sayisi.",
                defaultMonteCarlo,
                500,
                1000000,
                228);

            page.Controls.Add(apiGroup);
            page.Controls.Add(optimizationGroup);
            _profileTabs.TabPages.Add(page);

            return new ProfileUi(
                profileName,
                couponCount,
                i15Min,
                i15Max,
                initialTopLimit,
                diversePrePool,
                apiBudgetMultiplier,
                apiConcurrency,
                minDistance,
                minDistanceFinal,
                monteCarlo);
        }

        private NumericUpDown AddNumericInput(
            Control parent,
            string labelText,
            string info,
            int defaultValue,
            int minimum,
            int maximum,
            int top)
        {
            var label = new Label
            {
                Text = labelText,
                Location = new Point(12, top + 4),
                Size = new Size(175, 20)
            };

            var numeric = new NumericUpDown
            {
                Location = new Point(190, top),
                Size = new Size(130, 23),
                Minimum = minimum,
                Maximum = maximum,
                Value = Math.Clamp(defaultValue, minimum, maximum),
                ThousandsSeparator = true
            };

            _toolTip.SetToolTip(label, info);
            _toolTip.SetToolTip(numeric, info);

            parent.Controls.Add(label);
            parent.Controls.Add(numeric);

            return numeric;
        }

        private List<ProfileRunRequest> BuildProfileRequests()
        {
            var result = new List<ProfileRunRequest>(_profiles.Count);
            foreach (var profile in _profiles)
            {
                var desiredCount = DecimalToInt(profile.CouponCount.Value);
                if (desiredCount <= 0)
                {
                    continue;
                }

                var i15Min = DecimalToInt(profile.I15Min.Value);
                var i15Max = DecimalToInt(profile.I15Max.Value);
                if (i15Min > i15Max)
                {
                    throw new InvalidOperationException($"{profile.Name} icin i15 min, i15 max'tan buyuk olamaz.");
                }

                var options = new OptimizationOptions
                {
                    InitialTopCandidateLimit = DecimalToInt(profile.InitialTopCandidateLimit.Value),
                    DiversePrePoolLimit = DecimalToInt(profile.DiversePrePoolLimit.Value),
                    ApiBudgetMultiplier = DecimalToInt(profile.ApiBudgetMultiplier.Value),
                    ApiConcurrency = DecimalToInt(profile.ApiConcurrency.Value),
                    MinHammingDistance = DecimalToInt(profile.MinHammingDistance.Value),
                    MinHammingDistanceFinal = DecimalToInt(profile.MinHammingDistanceFinal.Value),
                    MonteCarloScenarioCount = DecimalToInt(profile.MonteCarloScenarioCount.Value),
                    MinI15WinnerCount = i15Min,
                    MaxI15WinnerCount = i15Max
                };

                result.Add(new ProfileRunRequest(profile.Name, desiredCount, options));
            }

            return result;
        }

        private static int DecimalToInt(decimal value)
        {
            return decimal.ToInt32(value);
        }

        private void InvokeOnUiThread(Action action)
        {
            if (IsDisposed)
            {
                return;
            }

            if (InvokeRequired)
            {
                BeginInvoke(action);
                return;
            }

            action();
        }

        private void UpdateTotalCouponCount()
        {
            var total = _profiles.Sum(x => DecimalToInt(x.CouponCount.Value));
            textBox1.Text = total.ToString();
        }

        private static List<Coupon> DeduplicateCoupons(IEnumerable<Coupon> coupons)
        {
            var result = new List<Coupon>();
            var seenPredictions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var coupon in coupons)
            {
                var normalized = NormalizePrediction(coupon.prediction);
                if (!seenPredictions.Add(normalized))
                {
                    continue;
                }

                coupon.prediction = normalized;
                result.Add(coupon);
            }

            return result;
        }

        private void SaveCombinedOutputs(List<Coupon> coupons)
        {
            ExcelExporter.ExportCouponsToExcel(coupons, "Kuponlar.xlsx");
            WriteCouponsToText(coupons);
            PrintMatchSummary(coupons);
        }

        private void WriteCouponsToText(List<Coupon> coupons)
        {
            try
            {
                var filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BestScoreCoupon.txt");
                using var writer = new StreamWriter(filePath, false);
                var seenPredictions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var coupon in coupons)
                {
                    var normalized = NormalizePrediction(coupon.prediction);
                    if (!seenPredictions.Add(normalized))
                    {
                        continue;
                    }

                    writer.WriteLine(normalized);
                }

                Log($"Kupon dosyasi yazildi: {filePath}", Color.Yellow);
            }
            catch (Exception ex)
            {
                Log($"Dosya yazim hatasi: {ex.Message}", Color.Crimson);
            }
        }

        private void PrintMatchSummary(List<Coupon> coupons)
        {
            Log($"Kupon sayisi = {coupons.Count}", Color.Yellow);

            const int matchCount = 15;
            for (var i = 0; i < matchCount; i++)
            {
                var count1 = 0;
                var countX = 0;
                var count2 = 0;

                foreach (var coupon in coupons)
                {
                    switch (coupon.prediction[i])
                    {
                        case '1': count1++; break;
                        case 'X': countX++; break;
                        case '2': count2++; break;
                    }
                }

                Log($"{i + 1}.Mac | 1:{count1} X:{countX} 2:{count2}", Color.Green);
            }
        }

        private static string NormalizePrediction(string prediction)
        {
            if (string.IsNullOrWhiteSpace(prediction))
            {
                return string.Empty;
            }

            return new string(prediction
                .Where(c => !char.IsWhiteSpace(c))
                .Select(char.ToUpperInvariant)
                .ToArray());
        }

        private sealed record ProfileRunRequest(string Name, int DesiredCouponCount, OptimizationOptions Options);

        private sealed record ProfileUi(
            string Name,
            NumericUpDown CouponCount,
            NumericUpDown I15Min,
            NumericUpDown I15Max,
            NumericUpDown InitialTopCandidateLimit,
            NumericUpDown DiversePrePoolLimit,
            NumericUpDown ApiBudgetMultiplier,
            NumericUpDown ApiConcurrency,
            NumericUpDown MinHammingDistance,
            NumericUpDown MinHammingDistanceFinal,
            NumericUpDown MonteCarloScenarioCount);
    }
}
