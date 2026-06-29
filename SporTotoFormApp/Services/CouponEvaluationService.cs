using SporTotoFormApp.Object;

namespace SporTotoFormApp.Services
{
    public sealed class CouponEvaluationService
    {
        private readonly HistoricalOutcomeModel _model;
        private readonly WeekPatternModel? _weekPatternModel;
        private readonly OptimizationOptions _options;

        public CouponEvaluationService(
            HistoricalOutcomeModel model,
            WeekPatternModel? weekPatternModel = null,
            OptimizationOptions? options = null)
        {
            _model = model;
            _weekPatternModel = weekPatternModel;
            _options = options ?? new OptimizationOptions();
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

            var patternAdjustment = _weekPatternModel?.GetPreScoreAdjustment(
                prediction,
                _model,
                _options) ?? 0.0;

            return logLikelihood - structurePenalty + patternAdjustment;
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

            // A single-column utility that only chases 15/15 becomes too brittle:
            // one bad probability can push the whole portfolio away from 14+.
            // Keep 15/15 as the main objective, but give 14/15 and 13/15 enough
            // weight so the final portfolio prefers robust near-miss coverage.
            var utility = p15 + (0.160 * p14) + (0.020 * p13);
            utility *= _weekPatternModel?.GetUtilityMultiplier(
                prediction,
                _model,
                _options) ?? 1.0;

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
