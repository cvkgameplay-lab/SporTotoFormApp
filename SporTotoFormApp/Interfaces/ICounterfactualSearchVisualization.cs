namespace SporTotoFormApp.Interfaces
{
    public interface ICounterfactualSearchVisualization
    {
        void ResetCounterfactualSearchChart(int roundId, string actualResultLine);

        void ReportCounterfactualSearchPoint(
            int roundId,
            double thirdChoiceMinRatio,
            double probabilityUniformBlend,
            int couponCount,
            int bestHitCount,
            decimal netProfitAmount,
            double roi,
            bool foundExact);
    }
}
