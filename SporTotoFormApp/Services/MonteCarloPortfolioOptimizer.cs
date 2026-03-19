using SporTotoFormApp.Object;

namespace SporTotoFormApp.Services
{
    public sealed class MonteCarloPortfolioOptimizer
    {
        private readonly HistoricalOutcomeModel _model;
        private readonly int _scenarioCount;
        private readonly Random _random;

        public MonteCarloPortfolioOptimizer(HistoricalOutcomeModel model, int scenarioCount, int randomSeed = 42)
        {
            _model = model;
            _scenarioCount = Math.Max(scenarioCount, 500);
            _random = new Random(randomSeed);
        }

        public List<Coupon> SelectPortfolio(List<Coupon> candidates, int desiredCount, int minDistance)
        {
            if (candidates.Count == 0 || desiredCount <= 0)
            {
                return new List<Coupon>();
            }

            var outcomes = SimulateOutcomes();
            var scenarioScores = BuildScenarioScores(candidates, outcomes);

            var selectedIndices = new List<int>(desiredCount);
            var selected = new List<Coupon>(desiredCount);
            var currentBest = new double[_scenarioCount];
            var used = new bool[candidates.Count];

            for (var slot = 0; slot < desiredCount; slot++)
            {
                var bestIndex = -1;
                var bestGain = double.MinValue;

                for (var i = 0; i < candidates.Count; i++)
                {
                    if (used[i])
                    {
                        continue;
                    }

                    if (selected.Count > 0 && selected.Any(x => Distance(x.prediction, candidates[i].prediction) < minDistance))
                    {
                        continue;
                    }

                    var gain = 0.0;
                    var candidateScores = scenarioScores[i];

                    for (var s = 0; s < _scenarioCount; s++)
                    {
                        var improved = Math.Max(currentBest[s], candidateScores[s]);
                        gain += improved - currentBest[s];
                    }

                    gain += candidates[i].Utility * 0.02;

                    if (gain > bestGain)
                    {
                        bestGain = gain;
                        bestIndex = i;
                    }
                }

                if (bestIndex == -1)
                {
                    break;
                }

                used[bestIndex] = true;
                selectedIndices.Add(bestIndex);
                selected.Add(candidates[bestIndex]);

                var chosenScores = scenarioScores[bestIndex];
                for (var s = 0; s < _scenarioCount; s++)
                {
                    currentBest[s] = Math.Max(currentBest[s], chosenScores[s]);
                }
            }

            return selected;
        }

        private List<char[]> SimulateOutcomes()
        {
            var scenarios = new List<char[]>(_scenarioCount);
            for (var i = 0; i < _scenarioCount; i++)
            {
                var row = new char[15];
                for (var m = 0; m < 15; m++)
                {
                    var p = _model.GetForPosition(m);
                    var r = _random.NextDouble();
                    row[m] = r < p.One ? '1' : r < p.One + p.Draw ? 'X' : '2';
                }

                scenarios.Add(row);
            }

            return scenarios;
        }

        private List<double[]> BuildScenarioScores(List<Coupon> candidates, List<char[]> outcomes)
        {
            var result = new List<double[]>(candidates.Count);

            foreach (var coupon in candidates)
            {
                var scores = new double[_scenarioCount];

                for (var s = 0; s < _scenarioCount; s++)
                {
                    var correct = 0;
                    var outcome = outcomes[s];

                    for (var i = 0; i < 15; i++)
                    {
                        if (coupon.prediction[i] == outcome[i])
                        {
                            correct++;
                        }
                    }

                    scores[s] = correct switch
                    {
                        15 => 1.00,
                        14 => 0.33,
                        13 => 0.10,
                        12 => 0.03,
                        _ => 0.0
                    };
                }

                result.Add(scores);
            }

            return result;
        }

        private static int Distance(string left, string right)
        {
            var diff = 0;
            for (var i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    diff++;
                }
            }

            return diff;
        }
    }
}
