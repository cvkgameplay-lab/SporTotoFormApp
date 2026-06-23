using SporTotoFormApp.Data;

namespace SporTotoFormApp.Services
{
    public sealed class TeamModelEnsembleService
    {
        private readonly NesineTeamMatchRepository _repository;

        public TeamModelEnsembleService(NesineTeamMatchRepository? repository = null)
        {
            _repository = repository ?? new NesineTeamMatchRepository();
        }

        public async Task<TeamModelEnsembleResult> BuildAsync(
            CurrentRoundInfo currentRound,
            IReadOnlyList<NesineMatchTeamIds> resolvedMatches,
            CancellationToken cancellationToken = default)
        {
            var cutoff = currentRound.Matches
                .Where(x => x.MatchDate.HasValue)
                .Select(x => x.MatchDate!.Value)
                .DefaultIfEmpty(DateTime.MaxValue)
                .Min();
            var completedMatches = await _repository.LoadCompletedMatchesBeforeAsync(
                cutoff,
                cancellationToken);

            return Build(currentRound, resolvedMatches, completedMatches);
        }

        public TeamModelEnsembleResult Build(
            CurrentRoundInfo currentRound,
            IReadOnlyList<NesineMatchTeamIds> resolvedMatches,
            IReadOnlyList<NesineCompletedMatch> completedMatches)
        {
            var comparison = new ForecastModelComparisonService(_repository)
                .Evaluate(completedMatches);

            if (!comparison.EnsembleSettings.IsCalibrated)
            {
                return new TeamModelEnsembleResult(
                    comparison,
                    new Dictionary<int, TeamModelPrediction>());
            }

            var elo = new EloRatingState();
            var dixonColes = new DixonColesState();
            foreach (var match in completedMatches
                         .OrderBy(x => x.MatchDate)
                         .ThenBy(x => x.MatchId))
            {
                elo.Observe(match);
                dixonColes.Observe(match);
            }

            var currentMatchesByOrder = currentRound.Matches
                .ToDictionary(x => x.MatchOrder);
            var predictions = new Dictionary<int, TeamModelPrediction>();

            foreach (var resolved in resolvedMatches)
            {
                currentMatchesByOrder.TryGetValue(
                    resolved.MatchOrder,
                    out var currentMatch);
                var fixture = new NesineCompletedMatch(
                    -resolved.MatchOrder,
                    currentMatch?.MatchDate ?? DateTime.MaxValue,
                    resolved.HomeTeam.TeamId,
                    resolved.AwayTeam.TeamId,
                    null,
                    0,
                    0,
                    false);

                if (!elo.CanPredict(fixture) || !dixonColes.CanPredict(fixture))
                {
                    continue;
                }

                var eloProbabilities = elo.Predict(fixture);
                var dixonColesPrediction = dixonColes.Predict(fixture);
                var probabilities = comparison.EnsembleSettings.Combine(
                    eloProbabilities,
                    dixonColesPrediction.Probabilities);
                var blendWeight = Math.Clamp(
                    0.25 +
                    (comparison.EnsembleSettings.CalibrationSampleCount / 1000.0),
                    0.25,
                    0.45);

                predictions[resolved.MatchOrder] = new TeamModelPrediction(
                    resolved.MatchOrder,
                    probabilities,
                    eloProbabilities,
                    dixonColesPrediction.Probabilities,
                    dixonColesPrediction.ExpectedHomeGoals,
                    dixonColesPrediction.ExpectedAwayGoals,
                    blendWeight,
                    comparison.EnsembleSettings.CalibrationSampleCount);
            }

            return new TeamModelEnsembleResult(comparison, predictions);
        }
    }

    public sealed record TeamModelEnsembleResult(
        ForecastModelComparisonResult Comparison,
        IReadOnlyDictionary<int, TeamModelPrediction> Predictions);

    public sealed record TeamModelPrediction(
        int MatchOrder,
        SymbolProbabilities Probabilities,
        SymbolProbabilities EloProbabilities,
        SymbolProbabilities DixonColesProbabilities,
        double ExpectedHomeGoals,
        double ExpectedAwayGoals,
        double HistoricalModelBlendWeight,
        int CalibrationSampleCount);
}
