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

        public int GetApiBudget()
        {
            var desired = Math.Max(DesiredCouponCount, 1);
            return Math.Max(desired * ApiBudgetMultiplier, 1000);
        }

        public static OptimizationOptions Create(int desiredCouponCount)
        {
            return new OptimizationOptions
            {
                DesiredCouponCount = Math.Max(desiredCouponCount, 1)
            };
        }
    }
}
