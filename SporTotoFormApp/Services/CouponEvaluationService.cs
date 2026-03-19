using SporTotoFormApp.Object;

namespace SporTotoFormApp.Services
{
    public sealed class CouponEvaluationService
    {
        private readonly HistoricalOutcomeModel _model;

        public CouponEvaluationService(HistoricalOutcomeModel model)
        {
            _model = model;
        }

        public double PreScore(string prediction)
        {
            var logLikelihood = 0.0;
            var c1 = 0;
            var cX = 0;
            var c2 = 0;
            var transitions = 0;

            for (var i = 0; i < prediction.Length; i++)
            {
                var symbol = prediction[i];
                var probability = Math.Max(_model.GetForPosition(i).ForSymbol(symbol), 1e-6);
                logLikelihood += Math.Log(probability);

                switch (symbol)
                {
                    case '1': c1++; break;
                    case 'X': cX++; break;
                    case '2': c2++; break;
                }

                if (i > 0 && prediction[i - 1] != symbol)
                {
                    transitions++;
                }
            }

            // Keep a slight rarity preference, but avoid ultra-random picks.
            var structurePenalty = 0.0;
            structurePenalty += Math.Pow(c1 - 7.0, 2) * 0.08;
            structurePenalty += Math.Pow(cX - 4.0, 2) * 0.07;
            structurePenalty += Math.Pow(c2 - 4.0, 2) * 0.07;
            structurePenalty += transitions < 6 ? (6 - transitions) * 0.20 : 0.0;
            structurePenalty += transitions > 13 ? (transitions - 13) * 0.15 : 0.0;

            return logLikelihood - structurePenalty;
        }

        public CouponAnalysis Analyze(string prediction, Bonus bonus)
        {
            var hitProbs = new double[prediction.Length];
            for (var i = 0; i < prediction.Length; i++)
            {
                hitProbs[i] = Math.Max(_model.GetForPosition(i).ForSymbol(prediction[i]), 1e-6);
            }

            var distribution = CorrectCountDistribution(hitProbs);

            var p15 = distribution[15];
            var p14 = distribution[14];
            var p13 = distribution[13];

            var k15 = ParsePositiveInt(bonus.i15);
            var k14 = ParsePositiveInt(bonus.i14);
            var k13 = ParsePositiveInt(bonus.i13);

            // Core objective: maximize win probability while minimizing split risk.
            var utility = (p15 / Math.Pow(k15 + 0.8, 1.12))
                        + (0.34 * p14 / Math.Pow(k14 + 1.0, 1.08))
                        + (0.16 * p13 / Math.Pow(k13 + 1.0, 1.04));

            return new CouponAnalysis
            {
                Prediction = prediction,
                P15 = p15,
                P14 = p14,
                P13 = p13,
                Utility = utility
            };
        }

        private static double[] CorrectCountDistribution(double[] hitProbabilities)
        {
            var n = hitProbabilities.Length;
            var dp = new double[n + 1];
            dp[0] = 1.0;

            foreach (var q in hitProbabilities)
            {
                for (var k = n; k >= 0; k--)
                {
                    var keepMiss = dp[k] * (1.0 - q);
                    var moveHit = k > 0 ? dp[k - 1] * q : 0.0;
                    dp[k] = keepMiss + moveHit;
                }
            }

            return dp;
        }

        private static int ParsePositiveInt(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return 0;
            }

            var cleaned = new string(raw.Where(char.IsDigit).ToArray());
            if (int.TryParse(cleaned, out var value) && value >= 0)
            {
                return value;
            }

            return 0;
        }
    }

    public sealed class CouponAnalysis
    {
        public string Prediction { get; init; } = string.Empty;
        public double P15 { get; init; }
        public double P14 { get; init; }
        public double P13 { get; init; }
        public double Utility { get; init; }
    }
}
