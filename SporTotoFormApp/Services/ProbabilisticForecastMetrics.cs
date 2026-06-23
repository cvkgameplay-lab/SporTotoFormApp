namespace SporTotoFormApp.Services
{
    public sealed class ProbabilisticForecastAccumulator
    {
        private int _correct;
        private int _actualDraws;
        private int _predictedDraws;
        private double _brierTotal;
        private double _logLossTotal;
        private double _rpsTotal;

        public int Count { get; private set; }

        public void Add(SymbolProbabilities probabilities, char actual)
        {
            var predicted = probabilities.One >= probabilities.Draw &&
                            probabilities.One >= probabilities.Two
                ? '1'
                : probabilities.Draw >= probabilities.Two ? 'X' : '2';

            Count++;
            _correct += predicted == actual ? 1 : 0;
            _actualDraws += actual == 'X' ? 1 : 0;
            _predictedDraws += predicted == 'X' ? 1 : 0;
            _brierTotal += BrierScore(probabilities, actual);
            _logLossTotal += -Math.Log(Math.Max(probabilities.ForSymbol(actual), 1e-12));
            _rpsTotal += RankedProbabilityScore(probabilities, actual);
        }

        public ProbabilisticForecastSummary ToSummary()
        {
            return new ProbabilisticForecastSummary(
                Count,
                Count == 0 ? null : _brierTotal / Count,
                Count == 0 ? null : _logLossTotal / Count,
                Count == 0 ? null : _rpsTotal / Count,
                Count == 0 ? null : (double)_correct / Count,
                Count == 0 ? null : (double)_actualDraws / Count,
                Count == 0 ? null : (double)_predictedDraws / Count);
        }

        public static char GetActual(int homeScore, int awayScore)
        {
            return homeScore > awayScore ? '1' : homeScore == awayScore ? 'X' : '2';
        }

        private static double BrierScore(SymbolProbabilities probabilities, char actual)
        {
            var y1 = actual == '1' ? 1.0 : 0.0;
            var yX = actual == 'X' ? 1.0 : 0.0;
            var y2 = actual == '2' ? 1.0 : 0.0;

            return Math.Pow(probabilities.One - y1, 2) +
                   Math.Pow(probabilities.Draw - yX, 2) +
                   Math.Pow(probabilities.Two - y2, 2);
        }

        private static double RankedProbabilityScore(
            SymbolProbabilities probabilities,
            char actual)
        {
            var y1 = actual == '1' ? 1.0 : 0.0;
            var yX = actual == 'X' ? 1.0 : 0.0;

            var first = Math.Pow(probabilities.One - y1, 2);
            var second = Math.Pow(
                probabilities.One + probabilities.Draw - y1 - yX,
                2);

            return (first + second) / 2.0;
        }
    }

    public sealed record ProbabilisticForecastSummary(
        int Count,
        double? BrierScore,
        double? LogLoss,
        double? RankedProbabilityScore,
        double? Accuracy,
        double? ActualDrawRate,
        double? PredictedDrawRate);
}
