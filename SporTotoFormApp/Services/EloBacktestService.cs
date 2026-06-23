using SporTotoFormApp.Data;

namespace SporTotoFormApp.Services
{
    public sealed class EloBacktestService
    {
        private readonly NesineTeamMatchRepository _repository;

        public EloBacktestService(NesineTeamMatchRepository? repository = null)
        {
            _repository = repository ?? new NesineTeamMatchRepository();
        }

        public async Task<EloBacktestResult> RunWalkForwardAsync(
            DateTime? cutoff = null,
            CancellationToken cancellationToken = default)
        {
            var matches = await _repository.LoadCompletedMatchesBeforeAsync(
                cutoff ?? DateTime.MaxValue,
                cancellationToken);

            var model = new EloRatingState();
            var metrics = new ProbabilisticForecastAccumulator();

            foreach (var match in matches.OrderBy(x => x.MatchDate).ThenBy(x => x.MatchId))
            {
                if (model.CanPredict(match))
                {
                    metrics.Add(
                        model.Predict(match),
                        ProbabilisticForecastAccumulator.GetActual(
                            match.HomeScore,
                            match.AwayScore));
                }

                model.Observe(match);
            }

            var summary = metrics.ToSummary();
            return new EloBacktestResult(
                matches.Count,
                summary.Count,
                summary.BrierScore,
                summary.LogLoss,
                summary.RankedProbabilityScore,
                summary.Accuracy,
                summary.ActualDrawRate,
                summary.PredictedDrawRate,
                model.RatedTeamCount);
        }
    }

    public sealed class EloRatingState
    {
        private const double InitialRating = 1500.0;
        private const double RatingScale = 400.0;
        private const double KFactor = 24.0;
        private const double HomeAdvantage = 60.0;
        private const int MinimumPriorMatches = 3;

        private readonly Dictionary<int, double> _ratings = [];
        private readonly Dictionary<int, int> _teamMatchCounts = [];
        private readonly Dictionary<int, CompetitionOutcomeStats> _competitionStats = [];

        public int RatedTeamCount => _ratings.Count;

        public bool CanPredict(NesineCompletedMatch match)
        {
            return _teamMatchCounts.GetValueOrDefault(match.HomeTeamId) >= MinimumPriorMatches &&
                   _teamMatchCounts.GetValueOrDefault(match.AwayTeamId) >= MinimumPriorMatches;
        }

        public SymbolProbabilities Predict(NesineCompletedMatch match)
        {
            var homeRating = _ratings.GetValueOrDefault(match.HomeTeamId, InitialRating);
            var awayRating = _ratings.GetValueOrDefault(match.AwayTeamId, InitialRating);
            var competitionStats = match.CompetitionId.HasValue
                ? _competitionStats.GetValueOrDefault(match.CompetitionId.Value)
                : null;
            var effectiveHomeRating = homeRating + (match.IsNeutral ? 0.0 : HomeAdvantage);
            var ratingDifference = effectiveHomeRating - awayRating;
            var homeShare = 1.0 / (1.0 + Math.Pow(10.0, -ratingDifference / RatingScale));

            var historicalDrawRate = competitionStats == null
                ? 0.28
                : (competitionStats.DrawCount + 7.0) / (competitionStats.MatchCount + 25.0);
            var drawProbability = historicalDrawRate *
                                  Math.Exp(-Math.Abs(ratingDifference) / 900.0);
            drawProbability = Math.Clamp(drawProbability, 0.12, 0.34);

            var decisiveProbability = 1.0 - drawProbability;
            return SymbolProbabilities.Normalize(
                decisiveProbability * homeShare,
                drawProbability,
                decisiveProbability * (1.0 - homeShare));
        }

        public void Observe(NesineCompletedMatch match)
        {
            var homeRating = _ratings.GetValueOrDefault(match.HomeTeamId, InitialRating);
            var awayRating = _ratings.GetValueOrDefault(match.AwayTeamId, InitialRating);
            var effectiveHomeRating = homeRating + (match.IsNeutral ? 0.0 : HomeAdvantage);
            var expectedHome = 1.0 / (1.0 + Math.Pow(10.0, (awayRating - effectiveHomeRating) / RatingScale));
            var actualHome = match.HomeScore > match.AwayScore
                ? 1.0
                : match.HomeScore == match.AwayScore ? 0.5 : 0.0;
            var goalDifference = Math.Abs(match.HomeScore - match.AwayScore);
            var goalMultiplier = goalDifference <= 1
                ? 1.0
                : Math.Min(1.0 + Math.Log(goalDifference), 2.5);
            var change = KFactor * goalMultiplier * (actualHome - expectedHome);

            _ratings[match.HomeTeamId] = homeRating + change;
            _ratings[match.AwayTeamId] = awayRating - change;
            _teamMatchCounts[match.HomeTeamId] =
                _teamMatchCounts.GetValueOrDefault(match.HomeTeamId) + 1;
            _teamMatchCounts[match.AwayTeamId] =
                _teamMatchCounts.GetValueOrDefault(match.AwayTeamId) + 1;

            if (match.CompetitionId.HasValue)
            {
                var stats = _competitionStats.GetValueOrDefault(match.CompetitionId.Value)
                            ?? new CompetitionOutcomeStats();
                stats.MatchCount++;
                if (match.HomeScore == match.AwayScore)
                {
                    stats.DrawCount++;
                }

                _competitionStats[match.CompetitionId.Value] = stats;
            }
        }
    }

    public sealed class CompetitionOutcomeStats
    {
        public int MatchCount { get; set; }
        public int DrawCount { get; set; }
    }

    public sealed record EloBacktestResult(
        int TotalCompletedMatches,
        int EvaluatedMatches,
        double? BrierScore,
        double? LogLoss,
        double? RankedProbabilityScore,
        double? Accuracy,
        double? ActualDrawRate,
        double? PredictedDrawRate,
        int RatedTeamCount);
}
