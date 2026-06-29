namespace SporTotoFormApp.Services
{
    public sealed class OptimizationOptions
    {
        public int DesiredCouponCount { get; init; }
        public int InitialTopCandidateLimit { get; init; } = 3200000;
        public int DiversePrePoolLimit { get; init; } = 750000;
        public int ApiBudgetMultiplier { get; init; } = 2000;
        public int ApiConcurrency { get; init; } = 6;
        public int MinHammingDistance { get; init; } = 3;
        public int MinHammingDistanceFinal { get; init; } = 3;
        public int MonteCarloScenarioCount { get; init; } = 400000;
        public double ThirdChoiceMinRatio { get; init; } = 0.55;
        public double ProbabilityUniformBlend { get; init; } = 0.08;
        public double PatternScoreWeight { get; init; } = 0.35;
        public double WinnerPatternWeight { get; init; } = 0.45;
        public double RecentPatternWeight { get; init; } = 0.20;
        public double PreviousWeekPatternWeight { get; init; } = 0.12;
        public double SurpriseBalanceWeight { get; init; } = 0.30;
        public int MinI15WinnerCount { get; init; } = 10;
        public int MaxI15WinnerCount { get; init; } = 20;

        public int GetApiBudget()
        {
            var desired = Math.Max(DesiredCouponCount, 1);
            return Math.Max(desired * ApiBudgetMultiplier, 1000);
        }

        public static OptimizationOptions Create(int desiredCouponCount, OptimizationOptions? uiOverrides = null)
        {
            var source = uiOverrides ?? new OptimizationOptions();
            var normalizedMinI15 = Math.Max(source.MinI15WinnerCount, 0);
            var normalizedMaxI15 = Math.Max(source.MaxI15WinnerCount, normalizedMinI15);

            return new OptimizationOptions
            {
                DesiredCouponCount = Math.Max(desiredCouponCount, 1),
                InitialTopCandidateLimit = Math.Max(source.InitialTopCandidateLimit, 1000),
                DiversePrePoolLimit = Math.Max(source.DiversePrePoolLimit, 1000),
                ApiBudgetMultiplier = Math.Max(source.ApiBudgetMultiplier, 1),
                ApiConcurrency = Math.Max(source.ApiConcurrency, 1),
                MinHammingDistance = Math.Max(source.MinHammingDistance, 1),
                MinHammingDistanceFinal = Math.Max(source.MinHammingDistanceFinal, 1),
                MonteCarloScenarioCount = Math.Max(source.MonteCarloScenarioCount, 500),
                ThirdChoiceMinRatio = Math.Clamp(source.ThirdChoiceMinRatio, 0.0, 1.01),
                ProbabilityUniformBlend = Math.Clamp(source.ProbabilityUniformBlend, 0.0, 0.35),
                PatternScoreWeight = Math.Clamp(source.PatternScoreWeight, 0.0, 2.0),
                WinnerPatternWeight = Math.Clamp(source.WinnerPatternWeight, 0.0, 2.0),
                RecentPatternWeight = Math.Clamp(source.RecentPatternWeight, 0.0, 2.0),
                PreviousWeekPatternWeight = Math.Clamp(source.PreviousWeekPatternWeight, 0.0, 2.0),
                SurpriseBalanceWeight = Math.Clamp(source.SurpriseBalanceWeight, 0.0, 2.0),
                MinI15WinnerCount = normalizedMinI15,
                MaxI15WinnerCount = normalizedMaxI15
            };
        }
    }
}
