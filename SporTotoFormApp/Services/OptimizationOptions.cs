namespace SporTotoFormApp.Services
{
    public sealed class OptimizationOptions
    {
        public int DesiredCouponCount { get; init; }
        public int InitialTopCandidateLimit { get; init; } = 350000;
        public int DiversePrePoolLimit { get; init; } = 70000;
        public int ApiBudgetMultiplier { get; init; } = 80;
        public int ApiConcurrency { get; init; } = 4;
        public int MinHammingDistance { get; init; } = 5;
        public int MinHammingDistanceFinal { get; init; } = 4;
        public int MonteCarloScenarioCount { get; init; } = 6000;

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
