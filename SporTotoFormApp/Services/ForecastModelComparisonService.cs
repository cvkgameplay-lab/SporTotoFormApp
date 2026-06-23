using SporTotoFormApp.Data;

namespace SporTotoFormApp.Services
{
    public sealed class ForecastModelComparisonService
    {
        private readonly NesineTeamMatchRepository _repository;

        public ForecastModelComparisonService(NesineTeamMatchRepository? repository = null)
        {
            _repository = repository ?? new NesineTeamMatchRepository();
        }

        public async Task<ForecastModelComparisonResult> RunWalkForwardAsync(
            DateTime? cutoff = null,
            CancellationToken cancellationToken = default)
        {
            var matches = await _repository.LoadCompletedMatchesBeforeAsync(
                cutoff ?? DateTime.MaxValue,
                cancellationToken);

            return Evaluate(matches);
        }

        public ForecastModelComparisonResult Evaluate(
            IReadOnlyList<NesineCompletedMatch> matches)
        {
            var elo = new EloRatingState();
            var dixonColes = new DixonColesState();
            var eloMetrics = new ProbabilisticForecastAccumulator();
            var dixonColesMetrics = new ProbabilisticForecastAccumulator();
            var ensembleMetrics = new ProbabilisticForecastAccumulator();
            var observations = new List<ForecastModelObservation>();
            ForecastEnsembleSettings? priorSettings = null;

            foreach (var match in matches.OrderBy(x => x.MatchDate).ThenBy(x => x.MatchId))
            {
                if (elo.CanPredict(match) && dixonColes.CanPredict(match))
                {
                    var actual = ProbabilisticForecastAccumulator.GetActual(
                        match.HomeScore,
                        match.AwayScore);

                    var eloProbabilities = elo.Predict(match);
                    var dixonColesProbabilities =
                        dixonColes.Predict(match).Probabilities;

                    eloMetrics.Add(eloProbabilities, actual);
                    dixonColesMetrics.Add(dixonColesProbabilities, actual);

                    if (observations.Count >= ForecastEnsembleFitter.MinimumCalibrationSamples)
                    {
                        if (priorSettings == null || observations.Count % 20 == 0)
                        {
                            priorSettings = ForecastEnsembleFitter.Fit(observations);
                        }

                        ensembleMetrics.Add(
                            priorSettings.Combine(
                                eloProbabilities,
                                dixonColesProbabilities),
                            actual);
                    }

                    observations.Add(new ForecastModelObservation(
                        eloProbabilities,
                        dixonColesProbabilities,
                        actual));
                }

                elo.Observe(match);
                dixonColes.Observe(match);
            }

            var finalSettings = ForecastEnsembleFitter.Fit(observations);
            return new ForecastModelComparisonResult(
                matches.Count,
                eloMetrics.ToSummary(),
                dixonColesMetrics.ToSummary(),
                ensembleMetrics.ToSummary(),
                dixonColes.CurrentRho,
                elo.RatedTeamCount,
                dixonColes.RatedTeamCount,
                finalSettings);
        }
    }

    public sealed record ForecastModelComparisonResult(
        int TotalCompletedMatches,
        ProbabilisticForecastSummary Elo,
        ProbabilisticForecastSummary DixonColes,
        ProbabilisticForecastSummary Ensemble,
        double DixonColesRho,
        int EloRatedTeamCount,
        int DixonColesRatedTeamCount,
        ForecastEnsembleSettings EnsembleSettings)
    {
        public int EvaluatedMatches => Math.Min(Elo.Count, DixonColes.Count);

        public string? BetterLogLossModel
        {
            get
            {
                if (!Elo.LogLoss.HasValue || !DixonColes.LogLoss.HasValue)
                {
                    return null;
                }

                if (Math.Abs(Elo.LogLoss.Value - DixonColes.LogLoss.Value) < 0.0001)
                {
                    return "Berabere";
                }

                return Elo.LogLoss.Value < DixonColes.LogLoss.Value
                    ? "Elo"
                    : "Dixon-Coles";
            }
        }
    }

    public sealed record ForecastModelObservation(
        SymbolProbabilities Elo,
        SymbolProbabilities DixonColes,
        char Actual);

    public sealed record ForecastEnsembleSettings(
        int CalibrationSampleCount,
        double EloTemperature,
        double DixonColesTemperature,
        double EloWeight)
    {
        public bool IsCalibrated =>
            CalibrationSampleCount >= ForecastEnsembleFitter.MinimumCalibrationSamples;

        public double DixonColesWeight => 1.0 - EloWeight;

        public SymbolProbabilities Combine(
            SymbolProbabilities elo,
            SymbolProbabilities dixonColes)
        {
            var calibratedElo = ForecastEnsembleFitter.ApplyTemperature(
                elo,
                EloTemperature);
            var calibratedDixonColes = ForecastEnsembleFitter.ApplyTemperature(
                dixonColes,
                DixonColesTemperature);

            return SymbolProbabilities.Normalize(
                (calibratedElo.One * EloWeight) +
                (calibratedDixonColes.One * DixonColesWeight),
                (calibratedElo.Draw * EloWeight) +
                (calibratedDixonColes.Draw * DixonColesWeight),
                (calibratedElo.Two * EloWeight) +
                (calibratedDixonColes.Two * DixonColesWeight));
        }
    }

    public static class ForecastEnsembleFitter
    {
        public const int MinimumCalibrationSamples = 30;
        public const int MaximumCalibrationSamples = 500;

        public static ForecastEnsembleSettings Fit(
            IReadOnlyList<ForecastModelObservation> observations)
        {
            var calibrationSet = observations.Count > MaximumCalibrationSamples
                ? observations
                    .Skip(observations.Count - MaximumCalibrationSamples)
                    .ToList()
                : observations;

            if (calibrationSet.Count < MinimumCalibrationSamples)
            {
                return new ForecastEnsembleSettings(
                    calibrationSet.Count,
                    1.0,
                    1.0,
                    0.5);
            }

            var eloTemperature = FitTemperature(calibrationSet, x => x.Elo);
            var dixonColesTemperature = FitTemperature(
                calibrationSet,
                x => x.DixonColes);
            var rawEloWeight = FitEloWeight(
                calibrationSet,
                eloTemperature,
                dixonColesTemperature);
            var reliability = Math.Min(1.0, calibrationSet.Count / 200.0);
            var eloWeight = 0.5 + ((rawEloWeight - 0.5) * reliability);

            return new ForecastEnsembleSettings(
                calibrationSet.Count,
                eloTemperature,
                dixonColesTemperature,
                Math.Clamp(eloWeight, 0.20, 0.80));
        }

        public static SymbolProbabilities ApplyTemperature(
            SymbolProbabilities probabilities,
            double temperature)
        {
            var safeTemperature = Math.Clamp(temperature, 0.50, 2.00);
            var exponent = 1.0 / safeTemperature;

            return SymbolProbabilities.Normalize(
                Math.Pow(Math.Max(probabilities.One, 1e-9), exponent),
                Math.Pow(Math.Max(probabilities.Draw, 1e-9), exponent),
                Math.Pow(Math.Max(probabilities.Two, 1e-9), exponent));
        }

        private static double FitTemperature(
            IReadOnlyList<ForecastModelObservation> observations,
            Func<ForecastModelObservation, SymbolProbabilities> selector)
        {
            var bestTemperature = 1.0;
            var bestLoss = double.MaxValue;

            for (var step = 10; step <= 40; step++)
            {
                var temperature = step / 20.0;
                var loss = 0.0;

                foreach (var observation in observations)
                {
                    var calibrated = ApplyTemperature(
                        selector(observation),
                        temperature);
                    loss -= Math.Log(
                        Math.Max(calibrated.ForSymbol(observation.Actual), 1e-12));
                }

                if (loss < bestLoss)
                {
                    bestLoss = loss;
                    bestTemperature = temperature;
                }
            }

            return bestTemperature;
        }

        private static double FitEloWeight(
            IReadOnlyList<ForecastModelObservation> observations,
            double eloTemperature,
            double dixonColesTemperature)
        {
            var bestWeight = 0.5;
            var bestLoss = double.MaxValue;

            for (var step = 4; step <= 16; step++)
            {
                var eloWeight = step / 20.0;
                var loss = 0.0;

                foreach (var observation in observations)
                {
                    var elo = ApplyTemperature(
                        observation.Elo,
                        eloTemperature);
                    var dixonColes = ApplyTemperature(
                        observation.DixonColes,
                        dixonColesTemperature);
                    var combined = SymbolProbabilities.Normalize(
                        (elo.One * eloWeight) +
                        (dixonColes.One * (1.0 - eloWeight)),
                        (elo.Draw * eloWeight) +
                        (dixonColes.Draw * (1.0 - eloWeight)),
                        (elo.Two * eloWeight) +
                        (dixonColes.Two * (1.0 - eloWeight)));

                    loss -= Math.Log(
                        Math.Max(combined.ForSymbol(observation.Actual), 1e-12));
                }

                if (loss < bestLoss)
                {
                    bestLoss = loss;
                    bestWeight = eloWeight;
                }
            }

            return bestWeight;
        }
    }
}
