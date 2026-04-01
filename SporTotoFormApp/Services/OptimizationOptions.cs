namespace SporTotoFormApp.Services
{
    public sealed class OptimizationOptions
    {
        public int DesiredCouponCount { get; init; }
        public int InitialTopCandidateLimit { get; init; } = 500000;
        public int DiversePrePoolLimit { get; init; } = 120000;
        public int ApiBudgetMultiplier { get; init; } = 120;
        public int ApiConcurrency { get; init; } = 6;
        public int MinHammingDistance { get; init; } = 5;
        public int MinHammingDistanceFinal { get; init; } = 4;
        public int MonteCarloScenarioCount { get; init; } = 15000;
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
                MinI15WinnerCount = normalizedMinI15,
                MaxI15WinnerCount = normalizedMaxI15
            };
        }
    }
}
