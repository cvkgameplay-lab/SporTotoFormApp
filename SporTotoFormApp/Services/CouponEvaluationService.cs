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
            var expected1 = 0.0;
            var expectedX = 0.0;
            var expected2 = 0.0;

            for (var i = 0; i < prediction.Length; i++)
            {
                var symbol = prediction[i];
                var p = _model.GetForPosition(i);
                var probability = Math.Max(RegularizeProbability(p.ForSymbol(symbol)), 1e-6);
                logLikelihood += Math.Log(probability);
                expected1 += p.One;
                expectedX += p.Draw;
                expected2 += p.Two;

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
            structurePenalty += Math.Pow(c1 - expected1, 2) * 0.035;
            structurePenalty += Math.Pow(cX - expectedX, 2) * 0.030;
            structurePenalty += Math.Pow(c2 - expected2, 2) * 0.030;
            structurePenalty += transitions < 4 ? (4 - transitions) * 0.12 : 0.0;
            structurePenalty += transitions > 14 ? (transitions - 14) * 0.10 : 0.0;

            return logLikelihood - structurePenalty;
        }

        public CouponAnalysis Analyze(string prediction, Bonus bonus)
        {
            var hitProbs = new double[prediction.Length];
            for (var i = 0; i < prediction.Length; i++)
            {
                hitProbs[i] = Math.Max(
                    RegularizeProbability(_model.GetForPosition(i).ForSymbol(prediction[i])),
                    1e-6);
            }

            var distribution = CorrectCountDistribution(hitProbs);

            var p15 = distribution[15];
            var p14 = distribution[14];
            var p13 = distribution[13];

            // The objective is exact accuracy. Estimated winner counts describe
            // prize sharing, not the probability that this prediction is correct.
            var utility = p15 + (0.040 * p14) + (0.004 * p13);

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

        private static double RegularizeProbability(double probability)
        {
            const double uniformBlend = 0.08;
            return Math.Clamp(
                (probability * (1.0 - uniformBlend)) + ((1.0 / 3.0) * uniformBlend),
                0.03,
                0.94);
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
