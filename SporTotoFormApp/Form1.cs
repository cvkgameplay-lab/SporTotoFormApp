using SporTotoFormApp.Interfaces;
using SporTotoFormApp.Data;
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
        private GroupBox _currentRoundGroup = null!;
        private Label _currentRoundLabel = null!;
        private ListView _currentMatchesList = null!;
        private ContextMenuStrip _currentMatchesMenu = null!;
        private Button _evaluateResultsButton = null!;
        private Button _experimentButton = null!;
        private CurrentRoundInfo? _currentRound;
        private PredictionInsight? _predictionInsight;
        private NesineProgram? _nesineProgram;
        private IReadOnlyDictionary<int, NesineHeadToHeadSummary>? _nesineHeadToHeadByMatchNo;
        private IReadOnlyDictionary<int, IReadOnlyList<NesineHeadToHeadExtraSnapshot>>? _nesineHeadToHeadExtrasByMatchNo;
        private IReadOnlyDictionary<int, MatchModelFeature>? _matchModelFeaturesByMatchNo;
        private IReadOnlyDictionary<int, NesineTeamContextFeature>? _nesineTeamContextByTeamId;
        private IReadOnlyDictionary<int, TeamModelPrediction>? _teamModelPredictionsByMatchNo;
        private IReadOnlyList<NesineMatchTeamIds>? _resolvedNesineMatches;
        private TeamModelEnsembleResult? _teamModelEnsemble;
        private DateTime _experimentModelRefreshedAtUtc = DateTime.MinValue;
        private CancellationTokenSource? _experimentCts;
        private int _experimentRunCounter;

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
                var profileNamesByPrediction = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var processed = 0;

                ProgressBarMaxValue = targetTotal;
                ProgressBarValue = 0;

                var historicalRefreshSucceeded = await RefreshHistoricalResultsAndEvaluateRunsAsync();
                if (historicalRefreshSucceeded && _currentRound != null)
                {
                    _predictionInsight = TryBuildPredictionInsight(
                        _currentRound,
                        _nesineProgram,
                        _nesineHeadToHeadByMatchNo,
                        _matchModelFeaturesByMatchNo);
                    Log("Guncel gecmis veri ile DB tahmin modeli yeniden hesaplandi.", Color.LightSteelBlue);
                }

                if (_predictionInsight?.Payout.SampleSize > 0)
                {
                    Log(
                        $"i15 filtre araligi DB ikramiye gecmisine gore revize edildi: {_predictionInsight.Payout.RecommendedI15Min}-{_predictionInsight.Payout.RecommendedI15Max}",
                        Color.LightSteelBlue);
                }

                foreach (var request in requests)
                {
                    Log($"{request.Name} basladi | Hedef kolon: {request.DesiredCouponCount}", Color.DeepSkyBlue);
                    var service = new MoneyFilterService(
                        this,
                        request.DesiredCouponCount,
                        request.Options,
                        _predictionInsight?.MatchProbabilities);
                    var profileCoupons = await service.Run(
                        persistOutputs: false,
                        refreshHistoricalData: !historicalRefreshSucceeded,
                        manageProgress: false);

                    historicalRefreshSucceeded = true;
                    combined.AddRange(profileCoupons);
                    foreach (var coupon in profileCoupons)
                    {
                        var normalized = NormalizePrediction(coupon.prediction);
                        if (!profileNamesByPrediction.ContainsKey(normalized))
                        {
                            profileNamesByPrediction[normalized] = request.Name;
                        }
                    }

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

                await SaveCombinedOutputsAsync(finalCoupons, targetTotal, profileNamesByPrediction, requests[0].Options);
                UpdateCurrentMatchMatrix(finalCoupons);
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

        private async void Form1_Load(object sender, EventArgs e)
        {
            UpdateTotalCouponCount();
            await LoadCurrentRoundMatchesAsync();
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

        private async void EvaluateResultsButton_Click(object? sender, EventArgs e)
        {
            _evaluateResultsButton.Enabled = false;
            try
            {
                await RefreshHistoricalResultsAndEvaluateRunsAsync();
            }
            catch (Exception ex)
            {
                Log($"Run sonuc degerlendirme hatasi: {ex.Message}", Color.OrangeRed);
            }
            finally
            {
                _evaluateResultsButton.Enabled = true;
            }
        }

        private async void ExperimentButton_Click(object? sender, EventArgs e)
        {
            if (_experimentCts != null)
            {
                _experimentCts.Cancel();
                _experimentButton.Enabled = false;
                _experimentButton.Text = "DENEY DURDURULUYOR";
                Log("Deney modu iptal istendi. Mevcut tur tamamlaninca duracak.", Color.Orange);
                return;
            }

            var requests = BuildProfileRequests();
            if (requests.Count == 0)
            {
                Log("Deney modu icin en az bir profilde kolon sayisi 1 veya daha buyuk olmali.", Color.OrangeRed);
                return;
            }

            _experimentCts = new CancellationTokenSource();
            button1.Enabled = false;
            _evaluateResultsButton.Enabled = false;
            _experimentButton.Text = "DENEY IPTAL";
            _experimentButton.Enabled = true;

            try
            {
                await RunExperimentLoopAsync(_experimentCts.Token);
            }
            catch (Exception ex)
            {
                Log($"Deney modu hatasi: {ex.Message}", Color.Crimson);
            }
            finally
            {
                _experimentCts?.Dispose();
                _experimentCts = null;
                button1.Enabled = true;
                _evaluateResultsButton.Enabled = true;
                _experimentButton.Enabled = true;
                _experimentButton.Text = "DENEY MODU BASLAT";
                Log("Deney modu durdu.", Color.Yellow);
            }
        }

        private async Task RunExperimentLoopAsync(CancellationToken cancellationToken)
        {
            var experimentRound = _currentRound ?? await TryLoadExperimentRoundAsync(cancellationToken);
            if (experimentRound == null)
            {
                Log("Deney modu baslatilamadi: tahmin haftasi belirlenemedi.", Color.OrangeRed);
                return;
            }

            if (_nesineProgram == null || _currentRound?.RoundId != experimentRound.RoundId)
            {
                await LoadCurrentRoundMatchesAsync(cancellationToken);
                experimentRound = _currentRound ?? experimentRound;
            }

            var experimentRoundId = experimentRound.RoundId;
            _currentRound = experimentRound;
            Log(
                $"Deney modu aktif. RoundId {experimentRoundId} ({experimentRound.RoundName}) degisene veya iptal edilene kadar yeni tahminler uretilecek.",
                Color.DeepSkyBlue);

            var historicalRefreshSucceeded = await RefreshHistoricalResultsAndEvaluateRunsAsync();
            await RefreshExperimentPredictionModelAsync(
                experimentRound,
                cancellationToken,
                "baslangic");

            while (!cancellationToken.IsCancellationRequested)
            {
                if (!await IsExperimentRoundStillCurrentAsync(experimentRoundId, cancellationToken))
                {
                    break;
                }

                if (DateTime.UtcNow - _experimentModelRefreshedAtUtc >= TimeSpan.FromHours(6))
                {
                    await RefreshExperimentPredictionModelAsync(
                        experimentRound,
                        cancellationToken,
                        "periyodik");
                }

                _experimentRunCounter++;
                var request = BuildExperimentRequest(_experimentRunCounter);

                Log(
                    $"Deney turu {_experimentRunCounter} basladi | Hedef kolon: {request.DesiredCouponCount} | {FormatOptionsForLog(request.Options)}",
                    Color.DeepSkyBlue);

                var watch = Stopwatch.StartNew();
                var service = new MoneyFilterService(
                    this,
                    request.DesiredCouponCount,
                    request.Options,
                    _predictionInsight?.MatchProbabilities);

                var coupons = await service.Run(
                    persistOutputs: false,
                    refreshHistoricalData: !historicalRefreshSucceeded,
                    manageProgress: false);
                historicalRefreshSucceeded = true;

                var deduplicated = DeduplicateCoupons(coupons)
                    .OrderByDescending(x => x.Utility)
                    .Take(request.DesiredCouponCount)
                    .ToList();

                await SaveExperimentRunToDatabaseAsync(deduplicated, request, _experimentRunCounter);
                UpdateCurrentMatchMatrix(deduplicated);
                watch.Stop();

                Log(
                    $"Deney turu {_experimentRunCounter} tamamlandi | Uretilen: {deduplicated.Count} | Sure: {watch.Elapsed.TotalMinutes:F1} dk",
                    Color.LimeGreen);

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        private async Task<CurrentRoundInfo?> TryLoadExperimentRoundAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(45));
                return await new HistoricalResultsUpdateService()
                    .GetLatestRoundForPredictionAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                Log($"Deney haftasi kontrol hatasi: {ex.Message}", Color.OrangeRed);
                return null;
            }
        }

        private async Task<bool> IsExperimentRoundStillCurrentAsync(
            int experimentRoundId,
            CancellationToken cancellationToken)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(45));
                var latestRound = await new HistoricalResultsUpdateService()
                    .GetLatestRoundForPredictionAsync(timeoutCts.Token);

                if (latestRound == null)
                {
                    Log("Deney modu hafta kontrolu yapilamadi, mevcut round ile devam ediliyor.", Color.Orange);
                    return true;
                }

                if (latestRound.RoundId == experimentRoundId)
                {
                    return true;
                }

                Log(
                    $"Yeni tahmin haftasi algilandi. Eski RoundId: {experimentRoundId}, Yeni RoundId: {latestRound.RoundId}. Deney modu durduruldu.",
                    Color.Yellow);
                _currentRound = latestRound;
                return false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (OperationCanceledException)
            {
                Log("Deney modu hafta kontrolu zaman asimina ugradi, mevcut round ile devam ediliyor.", Color.Orange);
                return true;
            }
            catch (Exception ex)
            {
                Log($"Deney modu hafta kontrol hatasi: {ex.Message}", Color.Orange);
                return true;
            }
        }

        private ProfileRunRequest BuildExperimentRequest(int iteration)
        {
            var baseRequest = BuildProfileRequests().First();
            var options = baseRequest.Options;
            var minI15 = Random.Shared.Next(0, 4) == 0
                ? Math.Max(options.MinI15WinnerCount - 1, 1)
                : options.MinI15WinnerCount;
            var maxI15 = Random.Shared.Next(0, 4) == 0
                ? Math.Min(options.MaxI15WinnerCount + 2, 20)
                : options.MaxI15WinnerCount;
            if (minI15 > maxI15)
            {
                maxI15 = minI15;
            }

            var varied = new OptimizationOptions
            {
                InitialTopCandidateLimit = VaryInt(options.InitialTopCandidateLimit, 0.90, 1.10, 1000, 5000000),
                DiversePrePoolLimit = VaryInt(options.DiversePrePoolLimit, 0.85, 1.15, 1000, 5000000),
                ApiBudgetMultiplier = options.ApiBudgetMultiplier,
                ApiConcurrency = options.ApiConcurrency,
                MinHammingDistance = Random.Shared.Next(0, 10) switch
                {
                    < 7 => 3,
                    < 9 => 4,
                    _ => 2
                },
                MinHammingDistanceFinal = Random.Shared.Next(0, 10) switch
                {
                    < 7 => 3,
                    < 9 => 4,
                    _ => 2
                },
                MonteCarloScenarioCount = VaryInt(options.MonteCarloScenarioCount, 0.85, 1.15, 500, 5000000),
                MinI15WinnerCount = minI15,
                MaxI15WinnerCount = maxI15
            };

            return new ProfileRunRequest($"Deney #{iteration}", baseRequest.DesiredCouponCount, varied);
        }

        private static int VaryInt(int value, double minMultiplier, double maxMultiplier, int minimum, int maximum)
        {
            var factor = minMultiplier + Random.Shared.NextDouble() * (maxMultiplier - minMultiplier);
            var varied = (int)Math.Round(value * factor);
            return Math.Clamp(varied, minimum, maximum);
        }

        private async Task SaveExperimentRunToDatabaseAsync(
            List<Coupon> coupons,
            ProfileRunRequest request,
            int iteration)
        {
            try
            {
                var profileNamesByPrediction = coupons
                    .Select(x => NormalizePrediction(x.prediction))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x, _ => request.Name, StringComparer.OrdinalIgnoreCase);
                var context = BuildPredictionRunContext(request.Options);
                var matrix = BuildPredictionRunMatrix(coupons);
                var notes =
                    $"Experiment run #{iteration} | {FormatOptionsForLog(request.Options)} | {FormatTeamModelForLog()}";
                var runId = await new PredictionRepository().SaveRunAsync(
                    coupons,
                    request.DesiredCouponCount,
                    notes,
                    profileNamesByPrediction,
                    context,
                    matrix);

                Log($"Deney run DB'ye yazildi. RunId: {runId} | Matrix satiri: {matrix.Count}", Color.Yellow);
            }
            catch (Exception ex)
            {
                Log($"Deney DB yazim hatasi: {ex.Message}", Color.Crimson);
            }
        }

        private static string FormatOptionsForLog(OptimizationOptions options)
        {
            return
                $"i15:{options.MinI15WinnerCount}-{options.MaxI15WinnerCount} | " +
                $"TopK:{options.InitialTopCandidateLimit:n0} | " +
                $"Havuz:{options.DiversePrePoolLimit:n0} | " +
                $"ApiCarpan:{options.ApiBudgetMultiplier:n0} | " +
                $"Esz:{options.ApiConcurrency} | " +
                $"Dist:{options.MinHammingDistance}/{options.MinHammingDistanceFinal} | " +
                $"MC:{options.MonteCarloScenarioCount:n0}";
        }

        private async Task<bool> RefreshHistoricalResultsAndEvaluateRunsAsync()
        {
            var historicalRefreshSucceeded = false;

            try
            {
                Log("Gecmis sonuclar resmi API'den guncelleniyor...", Color.DeepSkyBlue);
                using var refreshTimeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var refreshResult = await new HistoricalResultsUpdateService()
                    .RefreshAsync(AppDomain.CurrentDomain.BaseDirectory, refreshTimeoutCts.Token);
                historicalRefreshSucceeded = refreshResult.Success;

                if (refreshResult.Success)
                {
                    Log(
                        $"Gecmis veri guncellendi: {refreshResult.LineCount} hafta | Ikramiye satiri: {refreshResult.PayoutCount} | Mac satiri: {refreshResult.MatchCount}",
                        Color.DeepSkyBlue);
                }
                else
                {
                    Log("Gecmis veri guncellenemedi, mevcut DB ile devam ediliyor.", Color.Orange);
                }
            }
            catch (OperationCanceledException)
            {
                Log("Gecmis veri guncelleme zaman asimina ugradi, mevcut DB ile devam ediliyor.", Color.Orange);
            }
            catch (Exception ex)
            {
                Log($"Gecmis veri guncelleme hatasi: {ex.Message}", Color.OrangeRed);
            }

            try
            {
                Log("Sonucu gelmis tahmin run'lari degerlendiriliyor...", Color.LightSteelBlue);
                var summaries = await new PredictionRepository().EvaluatePendingRunsAsync();
                if (summaries.Count == 0)
                {
                    Log("Degerlendirilecek tamamlanmis run bulunamadi.", Color.LightSteelBlue);
                    return historicalRefreshSucceeded;
                }

                foreach (var summary in summaries)
                {
                    Log(
                        $"Run {summary.RunId} | Round {summary.RoundId} | En iyi: {summary.BestHitCount} | Ort: {summary.AverageHitCount:F2} | 15:{summary.Hit15Count} 14:{summary.Hit14Count} 13:{summary.Hit13Count} 12:{summary.Hit12Count}",
                        summary.BestHitCount >= 13 ? Color.LimeGreen : Color.LightSteelBlue);
                }

                Log($"Run sonuc degerlendirme tamamlandi: {summaries.Count} run", Color.Yellow);
            }
            catch (Exception ex)
            {
                Log($"Run sonuc degerlendirme hatasi: {ex.Message}", Color.OrangeRed);
            }

            return historicalRefreshSucceeded;
        }

        private async Task LoadCurrentRoundMatchesAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                _currentRoundLabel.Text = "Tahmin haftasi maclari yukleniyor...";
                _currentMatchesList.Items.Clear();

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));
                var currentRound = await new HistoricalResultsUpdateService()
                    .GetLatestRoundForPredictionAsync(timeoutCts.Token);

                if (currentRound == null)
                {
                    _currentRoundLabel.Text = "Tahmin haftasi maclari alinamadi.";
                    Log("Tahmin haftasi maclari alinamadi.", Color.Orange);
                    return;
                }

                _currentRound = currentRound;
                _teamModelPredictionsByMatchNo = null;
                _nesineProgram = await TryLoadNesineProgramAsync(timeoutCts.Token);
                if (_nesineProgram != null)
                {
                    await SaveNesineSnapshotAsync(currentRound, _nesineProgram, timeoutCts.Token);
                    _nesineHeadToHeadByMatchNo = await TryLoadAndSaveHeadToHeadSnapshotsAsync(
                        currentRound,
                        _nesineProgram,
                        timeoutCts.Token);
                    _matchModelFeaturesByMatchNo = await TryBuildMatchModelFeaturesAsync(currentRound, timeoutCts.Token);
                }

                _predictionInsight = TryBuildPredictionInsight(
                    currentRound,
                    _nesineProgram,
                    _nesineHeadToHeadByMatchNo,
                    _matchModelFeaturesByMatchNo);
                _currentRoundLabel.Text =
                    $"{currentRound.RoundName} | RoundId: {currentRound.RoundId} | Mac: {currentRound.Matches.Count}";

                foreach (var match in currentRound.Matches.OrderBy(x => x.MatchOrder))
                {
                    var insight = _predictionInsight?.MatchInsights
                        .FirstOrDefault(x => x.MatchOrder == match.MatchOrder);
                    var matchText = $"{match.HomeTeamName} - {match.AwayTeamName}";
                    var dateText = match.MatchDate?.ToString("dd.MM.yyyy HH:mm") ?? string.Empty;
                    var leagueText = string.Join(" / ", new[] { match.StageName, match.LeagueRoundName }
                        .Where(x => !string.IsNullOrWhiteSpace(x)));

                    var item = new ListViewItem(match.MatchOrder.ToString());
                    item.SubItems.Add(matchText);
                    item.SubItems.Add(dateText);
                    item.SubItems.Add(FormatProbability(insight?.Probabilities.One));
                    item.SubItems.Add(FormatProbability(insight?.Probabilities.Draw));
                    item.SubItems.Add(FormatProbability(insight?.Probabilities.Two));
                    item.SubItems.Add("-");
                    item.SubItems.Add("-");
                    item.SubItems.Add("-");
                    item.SubItems.Add(leagueText);
                    _currentMatchesList.Items.Add(item);
                }

                Log($"Tahmin haftasi yuklendi: {currentRound.RoundName} ({currentRound.RoundId})", Color.DeepSkyBlue);
                if (_predictionInsight != null)
                {
                    Log(_predictionInsight.Payout.Message, Color.LightSteelBlue);
                    Log("DB takim gecmisi 1/X/2 olasiliklari mac listesindeki P kolonlarina yansitildi.", Color.LightSteelBlue);
                    if (_nesineProgram != null)
                    {
                        Log($"Nesine oynanma oranlari modele eklendi. Program: {_nesineProgram.ProgramNo}", Color.LightSteelBlue);
                    }

                    if (_nesineHeadToHeadByMatchNo?.Count > 0)
                    {
                        Log($"Nesine H2H/oran ozetleri modele eklendi: {_nesineHeadToHeadByMatchNo.Count} mac", Color.LightSteelBlue);
                    }

                    if (_matchModelFeaturesByMatchNo?.Count > 0)
                    {
                        Log($"Nesine raw feature modeli eklendi: {_matchModelFeaturesByMatchNo.Count} mac", Color.LightSteelBlue);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                _currentRoundLabel.Text = "Tahmin haftasi maclari zaman asimina ugradi.";
                Log("Tahmin haftasi maclari zaman asimina ugradi.", Color.Orange);
            }
            catch (Exception ex)
            {
                _currentRoundLabel.Text = "Tahmin haftasi maclari alinamadi.";
                Log($"Tahmin haftasi maclari hatasi: {ex.Message}", Color.OrangeRed);
            }
        }

        private async Task<NesineProgram?> TryLoadNesineProgramAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await new NesineProgramService().GetProgramAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                Log($"Nesine program verisi alinamadi: {ex.Message}", Color.Orange);
                return null;
            }
        }

        private async Task SaveNesineSnapshotAsync(
            CurrentRoundInfo currentRound,
            NesineProgram nesineProgram,
            CancellationToken cancellationToken)
        {
            try
            {
                var inserted = await new NesineProgramRepository()
                    .SaveSnapshotAsync(currentRound, nesineProgram, cancellationToken);

                Log($"Nesine snapshot DB'ye yazildi: {inserted} satir", Color.LightSteelBlue);
            }
            catch (Exception ex)
            {
                Log($"Nesine snapshot DB yazim hatasi: {ex.Message}", Color.OrangeRed);
            }
        }

        private async Task<IReadOnlyDictionary<int, NesineHeadToHeadSummary>?> TryLoadAndSaveHeadToHeadSnapshotsAsync(
            CurrentRoundInfo currentRound,
            NesineProgram nesineProgram,
            CancellationToken cancellationToken)
        {
            try
            {
                var service = new NesineHeadToHeadService();
                var result = new Dictionary<int, NesineHeadToHeadSummary>();
                var extras = new Dictionary<int, IReadOnlyList<NesineHeadToHeadExtraSnapshot>>();

                foreach (var match in nesineProgram.Matches.Values.OrderBy(x => x.MatchNo))
                {
                    var summary = await service.GetSummaryAsync(match.BahisKod, cancellationToken);
                    if (summary != null)
                    {
                        result[match.MatchNo] = summary;
                    }

                    var extraSnapshots = await service.GetExtraSnapshotsAsync(match.BahisKod, cancellationToken);
                    if (extraSnapshots.Count > 0)
                    {
                        extras[match.MatchNo] = extraSnapshots;
                    }
                }

                var inserted = await new NesineHeadToHeadRepository()
                    .SaveSnapshotsAsync(currentRound, nesineProgram, result, extras, cancellationToken);
                _nesineHeadToHeadExtrasByMatchNo = extras;
                var extraWithData = extras.Values.SelectMany(x => x).Count(x => x.HasData);
                Log($"Nesine H2H snapshot DB'ye yazildi: {inserted} satir | Ek endpoint veri: {extraWithData}", Color.LightSteelBlue);

                try
                {
                    await TryRefreshNesineTeamMatchesAsync(
                        currentRound,
                        nesineProgram,
                        result,
                        extras,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Log("Nesine takim mac gecmisi zaman asimina ugradi; mevcut H2H verisiyle devam.", Color.Orange);
                }
                catch (Exception ex)
                {
                    Log($"Nesine takim mac gecmisi guncelleme hatasi: {ex.Message}", Color.OrangeRed);
                }

                return result;
            }
            catch (Exception ex)
            {
                Log($"Nesine H2H verisi alinamadi: {ex.Message}", Color.OrangeRed);
                return null;
            }
        }

        private async Task TryRefreshNesineTeamMatchesAsync(
            CurrentRoundInfo currentRound,
            NesineProgram nesineProgram,
            IReadOnlyDictionary<int, NesineHeadToHeadSummary> summaries,
            IReadOnlyDictionary<int, IReadOnlyList<NesineHeadToHeadExtraSnapshot>> extras,
            CancellationToken cancellationToken)
        {
            var resolvedMatches = new List<NesineMatchTeamIds>();

            foreach (var match in nesineProgram.Matches.Values.OrderBy(x => x.MatchNo))
            {
                summaries.TryGetValue(match.MatchNo, out var summary);
                extras.TryGetValue(match.MatchNo, out var matchExtras);

                var resolved = NesineTeamIdentityResolver.Resolve(match, summary, matchExtras);
                if (resolved != null)
                {
                    resolvedMatches.Add(resolved);
                }
            }

            var matchOrderByTeamId = resolvedMatches
                .SelectMany(x => new[]
                {
                    new KeyValuePair<int, int>(x.HomeTeam.TeamId, x.MatchOrder),
                    new KeyValuePair<int, int>(x.AwayTeam.TeamId, x.MatchOrder)
                })
                .GroupBy(x => x.Key)
                .ToDictionary(x => x.Key, x => x.Min(y => y.Value));
            _resolvedNesineMatches = resolvedMatches;

            if (matchOrderByTeamId.Count == 0)
            {
                Log("Nesine takim kimlikleri cozumlenemedi; takim mac gecmisi atlandi.", Color.Orange);
                return;
            }

            var repository = new NesineTeamMatchRepository();
            var teamIdsToRefresh = await repository.GetTeamIdsNeedingRefreshAsync(
                matchOrderByTeamId.Keys,
                TimeSpan.FromHours(6),
                cancellationToken);

            var feeds = new List<NesineTeamMatchFeed>();
            if (teamIdsToRefresh.Count > 0)
            {
                var service = new NesineTeamMatchesService();
                using var semaphore = new SemaphoreSlim(4);
                var tasks = teamIdsToRefresh.Select(async teamId =>
                {
                    await semaphore.WaitAsync(cancellationToken);
                    try
                    {
                        return await service.GetMatchesAsync(teamId, cancellationToken);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                feeds = (await Task.WhenAll(tasks))
                    .Where(x => x != null)
                    .Cast<NesineTeamMatchFeed>()
                    .ToList();
            }

            if (feeds.Count > 0)
            {
                var saveResult = await repository.SaveFeedsAsync(
                    currentRound,
                    matchOrderByTeamId,
                    feeds,
                    cancellationToken);

                Log(
                    $"Nesine takim mac gecmisi guncellendi | Kimlik: {matchOrderByTeamId.Count} | Feed: {saveResult.FetchCount} | Mac: {saveResult.MatchUpsertCount} | Tamamlanmis: {saveResult.CompletedMatchCount}",
                    Color.LightSteelBlue);
            }
            else
            {
                Log(
                    $"Nesine takim mac gecmisi cache'den kullaniliyor | Kimlik: {matchOrderByTeamId.Count}",
                    Color.LightSteelBlue);
            }

            var quality = await repository.GetDataQualityAsync(cancellationToken);
            Log(
                $"Nesine veri kapsami | Takim: {quality.TeamCount} | Lig: {quality.CompetitionCount} | Mac: {quality.MatchCount} | Skorlu: {quality.CompletedMatchCount}",
                quality.CompletedMatchCount > 0 ? Color.LightSteelBlue : Color.Orange);

            await TryRefreshNesineTeamProfilesAsync(
                currentRound,
                matchOrderByTeamId,
                cancellationToken);

            var teamModel = await new TeamModelEnsembleService(repository)
                .BuildAsync(currentRound, resolvedMatches, cancellationToken);
            _teamModelEnsemble = teamModel;
            _teamModelPredictionsByMatchNo = teamModel.Predictions;
            var comparison = teamModel.Comparison;
            if (comparison.EvaluatedMatches > 0)
            {
                Log(
                    $"Elo walk-forward | Ortak ornek: {comparison.EvaluatedMatches}/{comparison.TotalCompletedMatches} | Brier: {comparison.Elo.BrierScore:F4} | LogLoss: {comparison.Elo.LogLoss:F4} | RPS: {comparison.Elo.RankedProbabilityScore:F4} | Isabet: %{comparison.Elo.Accuracy * 100:F1}",
                    Color.LightSteelBlue);
                Log(
                    $"Dixon-Coles walk-forward | Brier: {comparison.DixonColes.BrierScore:F4} | LogLoss: {comparison.DixonColes.LogLoss:F4} | RPS: {comparison.DixonColes.RankedProbabilityScore:F4} | Isabet: %{comparison.DixonColes.Accuracy * 100:F1} | Rho: {comparison.DixonColesRho:F2}",
                    Color.LightSteelBlue);
                Log(
                    $"Ortak ornek LogLoss karsilastirmasi | Daha iyi: {comparison.BetterLogLossModel}",
                    Color.LightSteelBlue);

                if (comparison.Ensemble.Count > 0)
                {
                    Log(
                        $"Kalibre ensemble walk-forward | Ornek: {comparison.Ensemble.Count} | Brier: {comparison.Ensemble.BrierScore:F4} | LogLoss: {comparison.Ensemble.LogLoss:F4} | RPS: {comparison.Ensemble.RankedProbabilityScore:F4} | Isabet: %{comparison.Ensemble.Accuracy * 100:F1}",
                        Color.LightSteelBlue);
                }

                var settings = comparison.EnsembleSettings;
                Log(
                    $"Ensemble ayarlari | Kalibrasyon: {settings.CalibrationSampleCount} | Elo sicaklik: {settings.EloTemperature:F2} | Dixon-Coles sicaklik: {settings.DixonColesTemperature:F2} | Agirlik Elo/DC: %{settings.EloWeight * 100:F0}/%{settings.DixonColesWeight * 100:F0} | Guncel mac: {_teamModelPredictionsByMatchNo.Count}",
                    settings.IsCalibrated ? Color.LightSteelBlue : Color.Orange);
            }
            else
            {
                Log(
                    $"Elo/Dixon-Coles karsilastirmasi icin takim basi en az 3 onceki mac bekleniyor | Skorlu veri: {comparison.TotalCompletedMatches}",
                    Color.Orange);
            }
        }

        private async Task TryRefreshNesineTeamProfilesAsync(
            CurrentRoundInfo currentRound,
            IReadOnlyDictionary<int, int> matchOrderByTeamId,
            CancellationToken cancellationToken)
        {
            var repository = new NesineTeamProfileRepository();
            var teamIdsToRefresh = await repository.GetTeamIdsNeedingRefreshAsync(
                matchOrderByTeamId.Keys,
                TimeSpan.FromHours(6),
                cancellationToken);

            var profiles = new List<NesineTeamProfileFeed>();
            if (teamIdsToRefresh.Count > 0)
            {
                var service = new NesineTeamProfileService();
                using var semaphore = new SemaphoreSlim(4);
                var tasks = teamIdsToRefresh.Select(async teamId =>
                {
                    await semaphore.WaitAsync(cancellationToken);
                    try
                    {
                        return await service.GetProfileAsync(teamId, cancellationToken);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                profiles = (await Task.WhenAll(tasks))
                    .Where(x => x.Lineup != null || x.LeagueTable != null)
                    .ToList();
            }

            if (profiles.Count > 0)
            {
                var saveResult = await repository.SaveProfilesAsync(
                    currentRound,
                    matchOrderByTeamId,
                    profiles,
                    cancellationToken);

                Log(
                    $"Nesine takim profil verisi guncellendi | Takim: {profiles.Count} | Kadro: {saveResult.LineupSnapshotCount} snapshot/{saveResult.PlayerRowCount} oyuncu | Puan tablosu: {saveResult.LeagueSnapshotCount} snapshot/{saveResult.LeagueRowCount} satir",
                    Color.LightSteelBlue);
            }
            else
            {
                Log(
                    $"Nesine kadro ve puan tablosu cache'den kullaniliyor | Takim: {matchOrderByTeamId.Count}",
                    Color.LightSteelBlue);
            }

            _nesineTeamContextByTeamId = await repository.LoadLatestFeaturesAsync(
                matchOrderByTeamId.Keys,
                cancellationToken);

            var lineupCoverage = _nesineTeamContextByTeamId.Values.Count(x => x.SquadSize > 0);
            var tableCoverage = _nesineTeamContextByTeamId.Values.Count(x => x.Position.HasValue);
            Log(
                $"Takim context feature kapsami | Kadro: {lineupCoverage}/{matchOrderByTeamId.Count} | Puan durumu: {tableCoverage}/{matchOrderByTeamId.Count}",
                lineupCoverage > 0 || tableCoverage > 0 ? Color.LightSteelBlue : Color.Orange);
        }

        private async Task<IReadOnlyDictionary<int, MatchModelFeature>?> TryBuildMatchModelFeaturesAsync(
            CurrentRoundInfo currentRound,
            CancellationToken cancellationToken)
        {
            try
            {
                var repository = new MatchModelFeatureRepository();
                var changed = await repository.BuildAndSaveAsync(currentRound.RoundId, cancellationToken);
                var features = repository.LoadForRound(currentRound.RoundId);
                Log($"Mac model feature tablosu guncellendi: {changed} islem | {features.Count} mac", Color.LightSteelBlue);
                return features;
            }
            catch (Exception ex)
            {
                Log($"Mac model feature uretim hatasi: {ex.Message}", Color.OrangeRed);
                return null;
            }
        }

        private PredictionInsight? TryBuildPredictionInsight(
            CurrentRoundInfo currentRound,
            NesineProgram? nesineProgram,
            IReadOnlyDictionary<int, NesineHeadToHeadSummary>? headToHeadByMatchNo,
            IReadOnlyDictionary<int, MatchModelFeature>? featuresByMatchNo)
        {
            try
            {
                return new PredictionInsightRepository().Build(
                    currentRound,
                    nesineProgram?.Matches,
                    headToHeadByMatchNo,
                    featuresByMatchNo,
                    _teamModelPredictionsByMatchNo);
            }
            catch (Exception ex)
            {
                Log($"DB tahmin modeli okunamadi: {ex.Message}", Color.OrangeRed);
                return null;
            }
        }

        private async Task RefreshExperimentPredictionModelAsync(
            CurrentRoundInfo currentRound,
            CancellationToken cancellationToken,
            string reason)
        {
            try
            {
                if (_nesineProgram != null &&
                    _nesineHeadToHeadByMatchNo != null &&
                    _nesineHeadToHeadExtrasByMatchNo != null)
                {
                    await TryRefreshNesineTeamMatchesAsync(
                        currentRound,
                        _nesineProgram,
                        _nesineHeadToHeadByMatchNo,
                        _nesineHeadToHeadExtrasByMatchNo,
                        cancellationToken);
                }
                else if (_resolvedNesineMatches?.Count > 0)
                {
                    var teamModel = await new TeamModelEnsembleService()
                        .BuildAsync(
                            currentRound,
                            _resolvedNesineMatches,
                            cancellationToken);
                    _teamModelEnsemble = teamModel;
                    _teamModelPredictionsByMatchNo = teamModel.Predictions;
                }
                else
                {
                    Log(
                        "Deney modu takim modeli yenilenemedi: Nesine takim kimlikleri hazir degil.",
                        Color.Orange);
                }

                _predictionInsight = TryBuildPredictionInsight(
                    currentRound,
                    _nesineProgram,
                    _nesineHeadToHeadByMatchNo,
                    _matchModelFeaturesByMatchNo);
                _experimentModelRefreshedAtUtc = DateTime.UtcNow;

                Log(
                    $"Deney modu modeli yenilendi ({reason}) | {FormatTeamModelForLog()}",
                    _teamModelPredictionsByMatchNo?.Count > 0
                        ? Color.LightSteelBlue
                        : Color.Orange);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log(
                    $"Deney modu model yenileme hatasi ({reason}): {ex.Message}",
                    Color.OrangeRed);
            }
        }

        private string FormatTeamModelForLog()
        {
            var settings = _teamModelEnsemble?.Comparison.EnsembleSettings;
            if (settings == null || !settings.IsCalibrated)
            {
                return "Ensemble: pasif";
            }

            return
                $"Ensemble: aktif | Kalibrasyon:{settings.CalibrationSampleCount} | " +
                $"Elo/DC:%{settings.EloWeight * 100:F0}/%{settings.DixonColesWeight * 100:F0} | " +
                $"Mac:{_teamModelPredictionsByMatchNo?.Count ?? 0}";
        }

        private void UpdateCurrentMatchMatrix(List<Coupon> coupons)
        {
            if (_currentMatchesList.Items.Count == 0 || coupons.Count == 0)
            {
                return;
            }

            const int firstMatrixColumn = 6;
            for (var i = 0; i < _currentMatchesList.Items.Count && i < 15; i++)
            {
                var count1 = coupons.Count(x => x.prediction.Length > i && x.prediction[i] == '1');
                var countX = coupons.Count(x => x.prediction.Length > i && x.prediction[i] == 'X');
                var count2 = coupons.Count(x => x.prediction.Length > i && x.prediction[i] == '2');

                var item = _currentMatchesList.Items[i];
                item.SubItems[firstMatrixColumn].Text = count1.ToString();
                item.SubItems[firstMatrixColumn + 1].Text = countX.ToString();
                item.SubItems[firstMatrixColumn + 2].Text = count2.ToString();
            }
        }

        private static string FormatProbability(double? value)
        {
            return value.HasValue ? $"{value.Value:P0}" : "-";
        }

        private void CurrentMatchesList_MouseClick(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right)
            {
                return;
            }

            var item = _currentMatchesList.GetItemAt(e.X, e.Y);
            if (item == null)
            {
                return;
            }

            item.Selected = true;
            _currentMatchesMenu.Show(_currentMatchesList, e.Location);
        }

        private void ShowSelectedMatchInsightDetail()
        {
            if (_currentMatchesList.SelectedItems.Count == 0)
            {
                return;
            }

            var selected = _currentMatchesList.SelectedItems[0];
            if (!int.TryParse(selected.Text, out var matchOrder))
            {
                return;
            }

            var currentMatch = _currentRound?.Matches.FirstOrDefault(x => x.MatchOrder == matchOrder);
            var insight = _predictionInsight?.MatchInsights.FirstOrDefault(x => x.MatchOrder == matchOrder);

            using var form = new Form
            {
                Text = currentMatch == null
                    ? "Mac Detay"
                    : $"{currentMatch.HomeTeamName} - {currentMatch.AwayTeamName}",
                Size = new Size(980, 620),
                StartPosition = FormStartPosition.CenterParent
            };

            var summary = new Label
            {
                Location = new Point(12, 12),
                Size = new Size(940, 72),
                Text = BuildInsightSummary(currentMatch, insight)
            };

            var detailList = new ListView
            {
                Location = new Point(12, 190),
                Size = new Size(940, 382),
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };

            detailList.Columns.Add("Round", 70);
            detailList.Columns.Add("Hafta", 150);
            detailList.Columns.Add("#", 36);
            detailList.Columns.Add("Tarih", 120);
            detailList.Columns.Add("Mac", 330);
            detailList.Columns.Add("Skor", 70);
            detailList.Columns.Add("Sonuc", 60);

            var componentList = new ListView
            {
                Location = new Point(12, 92),
                Size = new Size(940, 90),
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };

            componentList.Columns.Add("Bilesen", 250);
            componentList.Columns.Add("Ornek", 70);
            componentList.Columns.Add("1", 50);
            componentList.Columns.Add("X", 50);
            componentList.Columns.Add("2", 50);
            componentList.Columns.Add("Agirlik", 70);

            if (insight != null)
            {
                foreach (var component in insight.Components)
                {
                    var item = new ListViewItem(component.Name);
                    item.SubItems.Add(component.SampleSize.ToString());
                    item.SubItems.Add(component.Count1.ToString());
                    item.SubItems.Add(component.CountX.ToString());
                    item.SubItems.Add(component.Count2.ToString());
                    item.SubItems.Add(component.Weight.ToString("0.00"));
                    componentList.Items.Add(item);
                }

                foreach (var detail in insight.Details)
                {
                    var item = new ListViewItem(detail.RoundId?.ToString() ?? string.Empty);
                    item.SubItems.Add(detail.RoundName ?? string.Empty);
                    item.SubItems.Add(detail.MatchOrder.ToString());
                    item.SubItems.Add(detail.MatchDate?.ToString("dd.MM.yyyy") ?? string.Empty);
                    item.SubItems.Add($"{detail.HomeTeamName} - {detail.AwayTeamName}");
                    item.SubItems.Add(detail.HomeScore.HasValue && detail.AwayScore.HasValue
                        ? $"{detail.HomeScore}-{detail.AwayScore}"
                        : string.Empty);
                    item.SubItems.Add(detail.ResultSymbol);
                    detailList.Items.Add(item);
                }
            }

            form.Controls.Add(summary);
            form.Controls.Add(componentList);
            form.Controls.Add(detailList);
            form.ShowDialog(this);
        }

        private static string BuildInsightSummary(CurrentRoundMatch? match, MatchInsight? insight)
        {
            if (match == null || insight == null)
            {
                return "Bu mac icin DB detay modeli bulunamadi.";
            }

            return
                $"{match.HomeTeamName} - {match.AwayTeamName}{Environment.NewLine}" +
                $"P1: {insight.Probabilities.One:P1} | PX: {insight.Probabilities.Draw:P1} | P2: {insight.Probabilities.Two:P1}{Environment.NewLine}" +
                $"Ham sayim: 1={insight.Count1}, X={insight.CountX}, 2={insight.Count2} | Ornek mac: {insight.SampleSize} | " +
                "Olasiliklarda az veri icin smoothing uygulanir.";
        }

        private void ConfigureLayout()
        {
            ClientSize = new Size(1240, 760);

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

            _evaluateResultsButton = new Button
            {
                Location = new Point(390, 64),
                Size = new Size(170, 36),
                Text = "SONUC DEGERLENDIR"
            };
            _evaluateResultsButton.Click += EvaluateResultsButton_Click;
            Controls.Add(_evaluateResultsButton);

            _experimentButton = new Button
            {
                Location = new Point(580, 64),
                Size = new Size(190, 36),
                Text = "DENEY MODU BASLAT"
            };
            _experimentButton.Click += ExperimentButton_Click;
            Controls.Add(_experimentButton);

            BuildCurrentRoundPanel();

            rtb_log.Location = new Point(12, 508);
            rtb_log.Size = new Size(840, 192);

            button2.Location = new Point(12, 708);
            button2.Size = new Size(840, 32);
        }

        private void BuildCurrentRoundPanel()
        {
            _currentRoundGroup = new GroupBox
            {
                Text = "Tahmin Edilecek Hafta",
                Location = new Point(12, 110),
                Size = new Size(840, 390)
            };

            _currentRoundLabel = new Label
            {
                Location = new Point(12, 24),
                Size = new Size(810, 22),
                Text = "Maclar yukleniyor..."
            };

            _currentMatchesList = new ListView
            {
                Location = new Point(12, 52),
                Size = new Size(810, 322),
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };

            _currentMatchesList.Columns.Add("#", 38);
            _currentMatchesList.Columns.Add("Mac", 305);
            _currentMatchesList.Columns.Add("Tarih", 112);
            _currentMatchesList.Columns.Add("P1", 48);
            _currentMatchesList.Columns.Add("PX", 48);
            _currentMatchesList.Columns.Add("P2", 48);
            _currentMatchesList.Columns.Add("K1", 42);
            _currentMatchesList.Columns.Add("KX", 42);
            _currentMatchesList.Columns.Add("K2", 42);
            _currentMatchesList.Columns.Add("Lig/Tur", 120);
            _currentMatchesList.MouseClick += CurrentMatchesList_MouseClick;

            _currentMatchesMenu = new ContextMenuStrip();
            _currentMatchesMenu.Items.Add("Detay", null, (_, _) => ShowSelectedMatchInsightDetail());

            _currentRoundGroup.Controls.Add(_currentRoundLabel);
            _currentRoundGroup.Controls.Add(_currentMatchesList);
            Controls.Add(_currentRoundGroup);
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
                Size = new Size(360, 630),
                Name = "profileTabs"
            };

            _profiles.Clear();
            _profiles.Add(CreateProfileTab("DB Model", 1024, 1, 20));

            Controls.Add(_profileTabs);
        }

        private ProfileUi CreateProfileTab(
            string profileName,
            int defaultCouponCount,
            int defaultI15Min,
            int defaultI15Max)
        {
            var page = new TabPage(profileName);

            var couponCount = AddNumericInput(
                page,
                "Kolon Sayisi",
                "Bu profilden kac kolon uretilecegini belirler.",
                defaultCouponCount,
                0,
                2500,
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
                "Ikramiye paylasim riskini raporlamak icin kullanilan hedef araligin alt siniri.",
                defaultI15Min,
                0,
                100000,
                24);

            var i15Max = AddNumericInput(
                apiGroup,
                "i15 Max",
                "Ikramiye paylasim riskini raporlamak icin kullanilan hedef araligin ust siniri.",
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
                3200000,
                2000,
                5000000,
                24);

            var diversePrePool = AddNumericInput(
                optimizationGroup,
                "DiversePrePoolLimit",
                "Cesitlilik filtresi sonrasi API'ye gitmeden once tutulacak aday havuzu limiti.",
                750000,
                2000,
                5000000,
                58);

            var apiBudgetMultiplier = AddNumericInput(
                optimizationGroup,
                "ApiBudgetMultiplier",
                "API'de degerlendirilecek kupon butcesi = hedef kolon * bu carpim.",
                1000,
                1,
                100000,
                92);

            var apiConcurrency = AddNumericInput(
                optimizationGroup,
                "ApiConcurrency",
                "Ayni anda kac API cagrisi yapilacagini belirler.",
                6,
                1,
                128,
                126);

            var minDistance = AddNumericInput(
                optimizationGroup,
                "MinHammingDistance",
                "On havuzda iki kupon arasindaki minimum fark (karakter bazli mesafe).",
                3,
                1,
                15,
                160);

            var minDistanceFinal = AddNumericInput(
                optimizationGroup,
                "MinHammingDistanceFinal",
                "Final secimde iki kupon arasindaki minimum fark.",
                3,
                1,
                15,
                194);

            var monteCarlo = AddNumericInput(
                optimizationGroup,
                "MonteCarloScenarioCount",
                "Portfoy optimizasyonunda simulasyon icin uretilecek senaryo sayisi.",
                400000,
                500,
                5000000,
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

                var effectiveI15Min = ClampI15WinnerCount(_predictionInsight?.Payout.SampleSize > 0
                    ? _predictionInsight.Payout.RecommendedI15Min
                    : i15Min);
                var effectiveI15Max = ClampI15WinnerCount(_predictionInsight?.Payout.SampleSize > 0
                    ? _predictionInsight.Payout.RecommendedI15Max
                    : i15Max);

                if (effectiveI15Min > effectiveI15Max)
                {
                    effectiveI15Max = effectiveI15Min;
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
                    MinI15WinnerCount = effectiveI15Min,
                    MaxI15WinnerCount = effectiveI15Max
                };

                result.Add(new ProfileRunRequest(profile.Name, desiredCount, options));
            }

            return result;
        }

        private static int DecimalToInt(decimal value)
        {
            return decimal.ToInt32(value);
        }

        private static int ClampI15WinnerCount(int value)
        {
            return Math.Clamp(value, 1, 20);
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

        private async Task SaveCombinedOutputsAsync(
            List<Coupon> coupons,
            int totalRequested,
            IReadOnlyDictionary<string, string> profileNamesByPrediction,
            OptimizationOptions options)
        {
            ExcelExporter.ExportCouponsToExcel(coupons, "Kuponlar.xlsx");
            WriteCouponsToText(coupons);
            await SaveCouponsToDatabaseAsync(coupons, totalRequested, profileNamesByPrediction, options);
            PrintMatchSummary(coupons);
        }

        private async Task SaveCouponsToDatabaseAsync(
            List<Coupon> coupons,
            int totalRequested,
            IReadOnlyDictionary<string, string> profileNamesByPrediction,
            OptimizationOptions options)
        {
            try
            {
                var context = BuildPredictionRunContext(options);
                var matrix = BuildPredictionRunMatrix(coupons);
                var runId = await new PredictionRepository().SaveRunAsync(
                    coupons,
                    totalRequested,
                    "Form1 combined profile run",
                    profileNamesByPrediction,
                    context,
                    matrix);

                Log($"Kuponlar DB'ye yazildi. RunId: {runId}", Color.Yellow);
                Log($"Run model bilgisi ve mac matrix'i DB'ye yazildi. Matrix satiri: {matrix.Count}", Color.Yellow);
            }
            catch (Exception ex)
            {
                Log($"DB yazim hatasi: {ex.Message}", Color.Crimson);
            }
        }

        private PredictionRunContext BuildPredictionRunContext(OptimizationOptions options)
        {
            return new PredictionRunContext(
                _currentRound?.RoundId,
                _currentRound?.RoundName,
                _nesineProgram?.ProgramNo,
                _nesineProgram != null,
                _nesineHeadToHeadByMatchNo?.Count > 0,
                _matchModelFeaturesByMatchNo?.Count > 0,
                _teamModelEnsemble?.Comparison.EnsembleSettings.IsCalibrated == true,
                _teamModelEnsemble?.Comparison.EnsembleSettings.CalibrationSampleCount ?? 0,
                _teamModelEnsemble?.Comparison.EnsembleSettings.EloWeight,
                _teamModelEnsemble?.Comparison.EnsembleSettings.DixonColesWeight,
                _teamModelEnsemble?.Comparison.EnsembleSettings.EloTemperature,
                _teamModelEnsemble?.Comparison.EnsembleSettings.DixonColesTemperature,
                _teamModelPredictionsByMatchNo?.Count ?? 0,
                options);
        }

        private IReadOnlyList<PredictionRunMatchMatrixRow> BuildPredictionRunMatrix(List<Coupon> coupons)
        {
            var rows = new List<PredictionRunMatchMatrixRow>();
            var matches = _currentRound?.Matches.OrderBy(x => x.MatchOrder).ToList();
            if (matches == null || matches.Count == 0)
            {
                return rows;
            }

            for (var i = 0; i < matches.Count && i < 15; i++)
            {
                var match = matches[i];
                var insight = _predictionInsight?.MatchInsights.FirstOrDefault(x => x.MatchOrder == match.MatchOrder);
                rows.Add(new PredictionRunMatchMatrixRow(
                    match.MatchOrder,
                    match.HomeTeamName,
                    match.AwayTeamName,
                    insight?.Probabilities.One,
                    insight?.Probabilities.Draw,
                    insight?.Probabilities.Two,
                    coupons.Count(x => x.prediction.Length > i && x.prediction[i] == '1'),
                    coupons.Count(x => x.prediction.Length > i && x.prediction[i] == 'X'),
                    coupons.Count(x => x.prediction.Length > i && x.prediction[i] == '2')));
            }

            return rows;
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
