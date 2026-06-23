using SporTotoFormApp.Data;

namespace SporTotoFormApp.Services
{
    public sealed class DixonColesBacktestService
    {
        private readonly NesineTeamMatchRepository _repository;

        public DixonColesBacktestService(NesineTeamMatchRepository? repository = null)
        {
            _repository = repository ?? new NesineTeamMatchRepository();
        }

        public async Task<DixonColesBacktestResult> RunWalkForwardAsync(
            DateTime? cutoff = null,
            CancellationToken cancellationToken = default)
        {
            var matches = await _repository.LoadCompletedMatchesBeforeAsync(
                cutoff ?? DateTime.MaxValue,
                cancellationToken);
            var model = new DixonColesState();
            var metrics = new ProbabilisticForecastAccumulator();

            foreach (var match in matches.OrderBy(x => x.MatchDate).ThenBy(x => x.MatchId))
            {
                if (model.CanPredict(match))
                {
                    metrics.Add(
                        model.Predict(match).Probabilities,
                        ProbabilisticForecastAccumulator.GetActual(
                            match.HomeScore,
                            match.AwayScore));
                }

                model.Observe(match);
            }

            var summary = metrics.ToSummary();
            return new DixonColesBacktestResult(
                matches.Count,
                summary.Count,
                summary.BrierScore,
                summary.LogLoss,
                summary.RankedProbabilityScore,
                summary.Accuracy,
                summary.ActualDrawRate,
                summary.PredictedDrawRate,
                model.CurrentRho,
                model.RatedTeamCount);
        }
    }

    public sealed class DixonColesState
    {
        private const int MinimumPriorMatches = 3;
        private const int MaximumGoals = 10;
        private const double TeamDecay = 0.985;
        private const double CompetitionDecay = 0.997;
        private const double TeamPriorWeight = 5.0;
        private const double VenuePriorWeight = 3.0;
        private const double CompetitionPriorWeight = 20.0;
        private const double DefaultHomeGoals = 1.45;
        private const double DefaultAwayGoals = 1.15;

        private readonly Dictionary<int, TeamGoalStats> _teamStats = [];
        private readonly Dictionary<int, CompetitionGoalStats> _competitionStats = [];
        private readonly List<DixonColesCalibrationSample> _calibrationSamples = [];

        public int RatedTeamCount => _teamStats.Count;
        public double CurrentRho { get; private set; } = -0.08;

        public bool CanPredict(NesineCompletedMatch match)
        {
            return _teamStats.GetValueOrDefault(match.HomeTeamId)?.MatchCount >= MinimumPriorMatches &&
                   _teamStats.GetValueOrDefault(match.AwayTeamId)?.MatchCount >= MinimumPriorMatches;
        }

        public DixonColesPrediction Predict(NesineCompletedMatch match)
        {
            var expected = GetExpectedGoals(match);
            var probabilities = BuildOutcomeProbabilities(
                expected.HomeGoals,
                expected.AwayGoals,
                CurrentRho);

            return new DixonColesPrediction(
                probabilities,
                expected.HomeGoals,
                expected.AwayGoals,
                CurrentRho);
        }

        public void Observe(NesineCompletedMatch match)
        {
            var expected = GetExpectedGoals(match);
            var homeStats = GetOrCreateTeam(match.HomeTeamId);
            var awayStats = GetOrCreateTeam(match.AwayTeamId);

            if (homeStats.MatchCount >= 1 && awayStats.MatchCount >= 1)
            {
                _calibrationSamples.Add(new DixonColesCalibrationSample(
                    expected.HomeGoals,
                    expected.AwayGoals,
                    match.HomeScore,
                    match.AwayScore));

                if (_calibrationSamples.Count > 400)
                {
                    _calibrationSamples.RemoveRange(0, _calibrationSamples.Count - 400);
                }

                if (_calibrationSamples.Count >= 12 &&
                    _calibrationSamples.Count % 5 == 0)
                {
                    CurrentRho = EstimateRho(_calibrationSamples);
                }
            }

            homeStats.Decay(TeamDecay);
            awayStats.Decay(TeamDecay);
            homeStats.AddHomeMatch(match.HomeScore, match.AwayScore);
            awayStats.AddAwayMatch(match.AwayScore, match.HomeScore);

            if (match.CompetitionId.HasValue)
            {
                var competition = GetOrCreateCompetition(match.CompetitionId.Value);
                competition.Decay(CompetitionDecay);
                competition.Add(match.HomeScore, match.AwayScore);
            }
        }

        private ExpectedGoals GetExpectedGoals(NesineCompletedMatch match)
        {
            var competition = match.CompetitionId.HasValue
                ? _competitionStats.GetValueOrDefault(match.CompetitionId.Value)
                : null;
            var baseHomeGoals = SmoothedRate(
                competition?.HomeGoals ?? 0.0,
                competition?.MatchWeight ?? 0.0,
                DefaultHomeGoals,
                CompetitionPriorWeight);
            var baseAwayGoals = SmoothedRate(
                competition?.AwayGoals ?? 0.0,
                competition?.MatchWeight ?? 0.0,
                DefaultAwayGoals,
                CompetitionPriorWeight);

            if (match.IsNeutral)
            {
                var neutralMean = (baseHomeGoals + baseAwayGoals) / 2.0;
                baseHomeGoals = neutralMean;
                baseAwayGoals = neutralMean;
            }

            var leagueTeamMean = Math.Max((baseHomeGoals + baseAwayGoals) / 2.0, 0.2);
            var home = _teamStats.GetValueOrDefault(match.HomeTeamId);
            var away = _teamStats.GetValueOrDefault(match.AwayTeamId);

            var homeAttackOverall = RelativeRate(
                home?.GoalsFor ?? 0.0,
                home?.MatchWeight ?? 0.0,
                leagueTeamMean,
                TeamPriorWeight);
            var awayDefenseOverall = RelativeRate(
                away?.GoalsAgainst ?? 0.0,
                away?.MatchWeight ?? 0.0,
                leagueTeamMean,
                TeamPriorWeight);
            var awayAttackOverall = RelativeRate(
                away?.GoalsFor ?? 0.0,
                away?.MatchWeight ?? 0.0,
                leagueTeamMean,
                TeamPriorWeight);
            var homeDefenseOverall = RelativeRate(
                home?.GoalsAgainst ?? 0.0,
                home?.MatchWeight ?? 0.0,
                leagueTeamMean,
                TeamPriorWeight);

            var homeVenueAttack = match.IsNeutral
                ? homeAttackOverall
                : RelativeRate(
                    home?.HomeGoalsFor ?? 0.0,
                    home?.HomeMatchWeight ?? 0.0,
                    baseHomeGoals,
                    VenuePriorWeight);
            var awayVenueDefense = match.IsNeutral
                ? awayDefenseOverall
                : RelativeRate(
                    away?.AwayGoalsAgainst ?? 0.0,
                    away?.AwayMatchWeight ?? 0.0,
                    baseHomeGoals,
                    VenuePriorWeight);
            var awayVenueAttack = match.IsNeutral
                ? awayAttackOverall
                : RelativeRate(
                    away?.AwayGoalsFor ?? 0.0,
                    away?.AwayMatchWeight ?? 0.0,
                    baseAwayGoals,
                    VenuePriorWeight);
            var homeVenueDefense = match.IsNeutral
                ? homeDefenseOverall
                : RelativeRate(
                    home?.HomeGoalsAgainst ?? 0.0,
                    home?.HomeMatchWeight ?? 0.0,
                    baseAwayGoals,
                    VenuePriorWeight);

            var homeGoals = baseHomeGoals *
                            GeometricMean(homeAttackOverall, homeVenueAttack) *
                            GeometricMean(awayDefenseOverall, awayVenueDefense);
            var awayGoals = baseAwayGoals *
                            GeometricMean(awayAttackOverall, awayVenueAttack) *
                            GeometricMean(homeDefenseOverall, homeVenueDefense);

            return new ExpectedGoals(
                Math.Clamp(homeGoals, 0.15, 4.5),
                Math.Clamp(awayGoals, 0.15, 4.5));
        }

        private static SymbolProbabilities BuildOutcomeProbabilities(
            double homeGoals,
            double awayGoals,
            double rho)
        {
            var homeProbabilities = PoissonProbabilities(homeGoals);
            var awayProbabilities = PoissonProbabilities(awayGoals);
            var homeWin = 0.0;
            var draw = 0.0;
            var awayWin = 0.0;

            for (var homeScore = 0; homeScore <= MaximumGoals; homeScore++)
            {
                for (var awayScore = 0; awayScore <= MaximumGoals; awayScore++)
                {
                    var probability = homeProbabilities[homeScore] *
                                      awayProbabilities[awayScore] *
                                      Tau(homeScore, awayScore, homeGoals, awayGoals, rho);

                    if (homeScore > awayScore)
                    {
                        homeWin += probability;
                    }
                    else if (homeScore == awayScore)
                    {
                        draw += probability;
                    }
                    else
                    {
                        awayWin += probability;
                    }
                }
            }

            return SymbolProbabilities.Normalize(homeWin, draw, awayWin);
        }

        private static double[] PoissonProbabilities(double mean)
        {
            var result = new double[MaximumGoals + 1];
            result[0] = Math.Exp(-mean);

            for (var goals = 1; goals <= MaximumGoals; goals++)
            {
                result[goals] = result[goals - 1] * mean / goals;
            }

            return result;
        }

        private static double Tau(
            int homeScore,
            int awayScore,
            double homeGoals,
            double awayGoals,
            double rho)
        {
            return Math.Max(
                RawTau(homeScore, awayScore, homeGoals, awayGoals, rho),
                0.01);
        }

        private static double RawTau(
            int homeScore,
            int awayScore,
            double homeGoals,
            double awayGoals,
            double rho)
        {
            return (homeScore, awayScore) switch
            {
                (0, 0) => 1.0 - (homeGoals * awayGoals * rho),
                (0, 1) => 1.0 + (homeGoals * rho),
                (1, 0) => 1.0 + (awayGoals * rho),
                (1, 1) => 1.0 - rho,
                _ => 1.0
            };
        }

        private static double EstimateRho(
            IReadOnlyList<DixonColesCalibrationSample> samples)
        {
            var bestRho = -0.08;
            var bestLoss = double.MaxValue;

            for (var step = -20; step <= 20; step++)
            {
                var rho = step / 100.0;
                var loss = 0.0;
                var valid = true;

                foreach (var sample in samples)
                {
                    var tau = RawTau(
                        sample.HomeScore,
                        sample.AwayScore,
                        sample.HomeGoals,
                        sample.AwayGoals,
                        rho);

                    if (tau <= 0)
                    {
                        valid = false;
                        break;
                    }

                    loss -= Math.Log(tau);
                }

                if (valid && loss < bestLoss)
                {
                    bestLoss = loss;
                    bestRho = rho;
                }
            }

            return bestRho;
        }

        private TeamGoalStats GetOrCreateTeam(int teamId)
        {
            if (!_teamStats.TryGetValue(teamId, out var stats))
            {
                stats = new TeamGoalStats();
                _teamStats[teamId] = stats;
            }

            return stats;
        }

        private CompetitionGoalStats GetOrCreateCompetition(int competitionId)
        {
            if (!_competitionStats.TryGetValue(competitionId, out var stats))
            {
                stats = new CompetitionGoalStats();
                _competitionStats[competitionId] = stats;
            }

            return stats;
        }

        private static double SmoothedRate(
            double total,
            double count,
            double priorMean,
            double priorWeight)
        {
            return (total + (priorMean * priorWeight)) / (count + priorWeight);
        }

        private static double RelativeRate(
            double total,
            double count,
            double baseline,
            double priorWeight)
        {
            var rate = SmoothedRate(total, count, baseline, priorWeight);
            return Math.Clamp(rate / Math.Max(baseline, 0.1), 0.45, 2.2);
        }

        private static double GeometricMean(double left, double right)
        {
            return Math.Sqrt(Math.Max(left * right, 0.01));
        }
    }

    internal sealed class TeamGoalStats
    {
        public int MatchCount { get; private set; }
        public double MatchWeight { get; private set; }
        public double GoalsFor { get; private set; }
        public double GoalsAgainst { get; private set; }
        public double HomeMatchWeight { get; private set; }
        public double HomeGoalsFor { get; private set; }
        public double HomeGoalsAgainst { get; private set; }
        public double AwayMatchWeight { get; private set; }
        public double AwayGoalsFor { get; private set; }
        public double AwayGoalsAgainst { get; private set; }

        public void Decay(double factor)
        {
            MatchWeight *= factor;
            GoalsFor *= factor;
            GoalsAgainst *= factor;
            HomeMatchWeight *= factor;
            HomeGoalsFor *= factor;
            HomeGoalsAgainst *= factor;
            AwayMatchWeight *= factor;
            AwayGoalsFor *= factor;
            AwayGoalsAgainst *= factor;
        }

        public void AddHomeMatch(int goalsFor, int goalsAgainst)
        {
            MatchCount++;
            MatchWeight += 1.0;
            GoalsFor += goalsFor;
            GoalsAgainst += goalsAgainst;
            HomeMatchWeight += 1.0;
            HomeGoalsFor += goalsFor;
            HomeGoalsAgainst += goalsAgainst;
        }

        public void AddAwayMatch(int goalsFor, int goalsAgainst)
        {
            MatchCount++;
            MatchWeight += 1.0;
            GoalsFor += goalsFor;
            GoalsAgainst += goalsAgainst;
            AwayMatchWeight += 1.0;
            AwayGoalsFor += goalsFor;
            AwayGoalsAgainst += goalsAgainst;
        }
    }

    internal sealed class CompetitionGoalStats
    {
        public double MatchWeight { get; private set; }
        public double HomeGoals { get; private set; }
        public double AwayGoals { get; private set; }

        public void Decay(double factor)
        {
            MatchWeight *= factor;
            HomeGoals *= factor;
            AwayGoals *= factor;
        }

        public void Add(int homeGoals, int awayGoals)
        {
            MatchWeight += 1.0;
            HomeGoals += homeGoals;
            AwayGoals += awayGoals;
        }
    }

    internal sealed record ExpectedGoals(double HomeGoals, double AwayGoals);

    internal sealed record DixonColesCalibrationSample(
        double HomeGoals,
        double AwayGoals,
        int HomeScore,
        int AwayScore);

    public sealed record DixonColesPrediction(
        SymbolProbabilities Probabilities,
        double ExpectedHomeGoals,
        double ExpectedAwayGoals,
        double Rho);

    public sealed record DixonColesBacktestResult(
        int TotalCompletedMatches,
        int EvaluatedMatches,
        double? BrierScore,
        double? LogLoss,
        double? RankedProbabilityScore,
        double? Accuracy,
        double? ActualDrawRate,
        double? PredictedDrawRate,
        double Rho,
        int RatedTeamCount);
}
