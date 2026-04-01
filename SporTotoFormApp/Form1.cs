using SporTotoFormApp.Interfaces;
using SporTotoFormApp.Services;
using System.Diagnostics;

namespace SporTotoFormApp
{
    public partial class Form1 : Form, ITestView
    {
        private NumericUpDown _nudI15Min = null!;
        private NumericUpDown _nudI15Max = null!;
        private NumericUpDown _nudInitialTopCandidateLimit = null!;
        private NumericUpDown _nudDiversePrePoolLimit = null!;
        private NumericUpDown _nudApiBudgetMultiplier = null!;
        private NumericUpDown _nudApiConcurrency = null!;
        private NumericUpDown _nudMinHammingDistance = null!;
        private NumericUpDown _nudMinHammingDistanceFinal = null!;
        private NumericUpDown _nudMonteCarloScenarioCount = null!;
        private ToolTip _toolTip = null!;

        public int ProgressBarValue
        {
            get => progressBar1.Value;
            set
            {
                if (progressBar1.InvokeRequired)
                {
                    progressBar1.Invoke(new Action(() =>
                        progressBar1.Value = value));
                }
                else
                {
                    progressBar1.Value = value;
                }

                label1.Text = value.ToString();
            }
        }

        public int ProgressBarMaxValue
        {
            get => progressBar1.Maximum;
            set
            {
                if (progressBar1.InvokeRequired)
                {
                    progressBar1.Invoke(new Action(() =>
                        progressBar1.Maximum = value));
                }
                else
                {
                    progressBar1.Maximum = value;
                }
            }
        }

        public Form1()
        {
            InitializeComponent();
            ConfigureLayout();
            BuildAdvancedSettingsPanels();
        }

        private async void button1_Click(object sender, EventArgs e)
        {
            if (!int.TryParse(textBox1.Text, out var kolonSayisi) || kolonSayisi <= 0)
            {
                Log("Lutfen gecerli bir kolon sayisi girin.", Color.OrangeRed);
                return;
            }

            if (_nudI15Min.Value > _nudI15Max.Value)
            {
                Log("i15 min degeri, i15 max degerinden buyuk olamaz.", Color.OrangeRed);
                return;
            }

            button1.Enabled = false;
            progressBar1.Minimum = 0;
            progressBar1.Value = 0;
            rtb_log.Clear();

            try
            {
                var uiOverrides = BuildUiOverrides();
                MoneyFilterService moneyFilterService = new MoneyFilterService(this, kolonSayisi, uiOverrides);
                await moneyFilterService.Run();
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
            if (rtb_log.InvokeRequired)
            {
                rtb_log.Invoke(new Action(() => Log(message, color)));
            }
            else
            {
                rtb_log.SelectionStart = rtb_log.TextLength;
                rtb_log.SelectionLength = 0;
                rtb_log.SelectionColor = color;
                rtb_log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
                rtb_log.ScrollToCaret();
            }
        }

        private void rtb_log_TextChanged(object sender, EventArgs e)
        {
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            textBox1.Text = "30";
        }

        private void textBox1_TextChanged(object sender, EventArgs e)
        {
        }

        private void button2_Click(object sender, EventArgs e)
        {
            string path = Path.Combine(Application.StartupPath, "BestScoreCoupon.txt");

            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        }

        private OptimizationOptions BuildUiOverrides()
        {
            return new OptimizationOptions
            {
                InitialTopCandidateLimit = DecimalToInt(_nudInitialTopCandidateLimit.Value),
                DiversePrePoolLimit = DecimalToInt(_nudDiversePrePoolLimit.Value),
                ApiBudgetMultiplier = DecimalToInt(_nudApiBudgetMultiplier.Value),
                ApiConcurrency = DecimalToInt(_nudApiConcurrency.Value),
                MinHammingDistance = DecimalToInt(_nudMinHammingDistance.Value),
                MinHammingDistanceFinal = DecimalToInt(_nudMinHammingDistanceFinal.Value),
                MonteCarloScenarioCount = DecimalToInt(_nudMonteCarloScenarioCount.Value),
                MinI15WinnerCount = DecimalToInt(_nudI15Min.Value),
                MaxI15WinnerCount = DecimalToInt(_nudI15Max.Value)
            };
        }

        private static int DecimalToInt(decimal value)
        {
            return decimal.ToInt32(value);
        }

        private void ConfigureLayout()
        {
            ClientSize = new Size(1240, 580);

            progressBar1.Location = new Point(12, 20);
            progressBar1.Size = new Size(1216, 23);

            label1.Location = new Point(12, 48);

            label2.Location = new Point(12, 76);
            textBox1.Location = new Point(85, 73);

            button1.Location = new Point(200, 64);
            button1.Size = new Size(170, 36);

            rtb_log.Location = new Point(12, 110);
            rtb_log.Size = new Size(840, 420);

            button2.Location = new Point(12, 538);
            button2.Size = new Size(840, 32);
        }

        private void BuildAdvancedSettingsPanels()
        {
            _toolTip = new ToolTip
            {
                AutoPopDelay = 10000,
                InitialDelay = 250,
                ReshowDelay = 150,
                ShowAlways = true
            };

            var apiFilterGroup = new GroupBox
            {
                Text = "API Filtre Ayarlari",
                Location = new Point(870, 110),
                Size = new Size(360, 110)
            };

            _nudI15Min = AddNumericInput(
                apiFilterGroup,
                "i15 Min",
                "API donusunde 15 bilen kisi sayisi bu degerin altindaysa kupon elenir.",
                10,
                0,
                100000,
                24);

            _nudI15Max = AddNumericInput(
                apiFilterGroup,
                "i15 Max",
                "API donusunde 15 bilen kisi sayisi bu degerin ustundeyse kupon elenir.",
                20,
                0,
                100000,
                58);

            var optimizationGroup = new GroupBox
            {
                Text = "OptimizationOptions",
                Location = new Point(870, 230),
                Size = new Size(360, 340)
            };

            _nudInitialTopCandidateLimit = AddNumericInput(
                optimizationGroup,
                "InitialTopCandidateLimit",
                "On skorlama sonrasi tutulacak maksimum aday kupon sayisi (Top-K).",
                500000,
                1000,
                5000000,
                24);

            _nudDiversePrePoolLimit = AddNumericInput(
                optimizationGroup,
                "DiversePrePoolLimit",
                "Cesitlilik filtresi sonrasi API'ye gitmeden once tutulacak aday havuzu limiti.",
                120000,
                1000,
                5000000,
                58);

            _nudApiBudgetMultiplier = AddNumericInput(
                optimizationGroup,
                "ApiBudgetMultiplier",
                "API'de degerlendirilecek kupon butcesi = hedef kolon * bu carpim.",
                120,
                1,
                10000,
                92);

            _nudApiConcurrency = AddNumericInput(
                optimizationGroup,
                "ApiConcurrency",
                "Ayni anda kac API cagrisi yapilacagini belirler.",
                6,
                1,
                128,
                126);

            _nudMinHammingDistance = AddNumericInput(
                optimizationGroup,
                "MinHammingDistance",
                "On havuzda iki kupon arasindaki minimum fark (karakter bazli mesafe).",
                5,
                1,
                15,
                160);

            _nudMinHammingDistanceFinal = AddNumericInput(
                optimizationGroup,
                "MinHammingDistanceFinal",
                "Final secimde iki kupon arasindaki minimum fark.",
                4,
                1,
                15,
                194);

            _nudMonteCarloScenarioCount = AddNumericInput(
                optimizationGroup,
                "MonteCarloScenarioCount",
                "Portfoy optimizasyonunda simulasyon icin uretilecek senaryo sayisi.",
                15000,
                500,
                1000000,
                228);

            Controls.Add(apiFilterGroup);
            Controls.Add(optimizationGroup);
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
                Size = new Size(195, 20)
            };

            var numeric = new NumericUpDown
            {
                Location = new Point(210, top),
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
    }
}
