using SporTotoFormApp.Object;

namespace SporTotoFormApp.Services
{
    public sealed class MonteCarloPortfolioOptimizer
    {
        private readonly HistoricalOutcomeModel _model;
        private readonly int _scenarioCount;
        private readonly Random _random;
        private readonly double _thirdChoiceMinRatio;
        private readonly double _probabilityUniformBlend;

        public MonteCarloPortfolioOptimizer(
            HistoricalOutcomeModel model,
            int scenarioCount,
            int randomSeed = 42,
            double thirdChoiceMinRatio = 1.01,
            double probabilityUniformBlend = 0.0)
        {
            _model = model;
            _scenarioCount = Math.Max(scenarioCount, 500);
            _random = new Random(randomSeed);
            _thirdChoiceMinRatio = Math.Clamp(thirdChoiceMinRatio, 0.0, 1.01);
            _probabilityUniformBlend = Math.Clamp(probabilityUniformBlend, 0.0, 0.35);
        }

        public List<Coupon> SelectPortfolio(List<Coupon> candidates, int desiredCount, int minDistance)
        {
            if (candidates.Count == 0 || desiredCount <= 0)
            {
                return new List<Coupon>();
            }

            var maxSameSymbolPerMatch = desiredCount >= 10
                ? Math.Max((int)Math.Ceiling(desiredCount * 0.74), 1)
                : desiredCount;

            var selectedIndices = new List<int>(desiredCount);
            var selected = new List<Coupon>(desiredCount);
            var used = new bool[candidates.Count];
            var symbolCountsByMatch = CreateSymbolCounts();
            var minimumCoverageTargets = CreateMinimumCoverageTargets(desiredCount);
            var pairCoverage = new Dictionary<int, int>();
            var candidateIndexByPrediction = candidates
                .Select((coupon, index) => new { coupon.prediction, index })
                .GroupBy(x => x.prediction, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First().index, StringComparer.OrdinalIgnoreCase);

            foreach (var target in CoverageScenarioGenerator.Generate(
                         _model,
                         Math.Min(desiredCount, 2500),
                         _thirdChoiceMinRatio,
                         _probabilityUniformBlend))
            {
                var targetIndex = candidateIndexByPrediction.GetValueOrDefault(target, -1);
                if (targetIndex < 0 || used[targetIndex])
                {
                    targetIndex = FindClosestTargetCandidate(target, candidates, used);
                }

                if (targetIndex < 0)
                {
                    continue;
                }

                AddSelectedCandidate(
                    targetIndex,
                    candidates,
                    used,
                    selectedIndices,
                    selected,
                    symbolCountsByMatch,
                    pairCoverage);

                if (selected.Count >= desiredCount)
                {
                    return selected;
                }
            }

            var outcomes = SimulateOutcomes();
            var scenarioScores = BuildScenarioScores(candidates, outcomes);
            var currentBest = new byte[_scenarioCount];
            foreach (var selectedIndex in selectedIndices)
            {
                UpdateCurrentBest(currentBest, scenarioScores[selectedIndex]);
            }

            for (var slot = selected.Count; slot < desiredCount; slot++)
            {
                var bestIndex = FindBestCandidate(
                    candidates,
                    scenarioScores,
                    currentBest,
                    used,
                    symbolCountsByMatch,
                    minimumCoverageTargets,
                    pairCoverage,
                    selected,
                    desiredCount,
                    minDistance,
                    maxSameSymbolPerMatch,
                    enforceDistance: true,
                    enforceSymbolCap: true);

                if (bestIndex == -1)
                {
                    bestIndex = FindBestCandidate(
                        candidates,
                        scenarioScores,
                        currentBest,
                        used,
                        symbolCountsByMatch,
                        minimumCoverageTargets,
                        pairCoverage,
                        selected,
                        desiredCount,
                        Math.Max(1, minDistance - 1),
                        maxSameSymbolPerMatch,
                        enforceDistance: true,
                        enforceSymbolCap: false);
                }

                if (bestIndex == -1)
                {
                    bestIndex = FindBestCandidate(
                        candidates,
                        scenarioScores,
                        currentBest,
                        used,
                        symbolCountsByMatch,
                        minimumCoverageTargets,
                        pairCoverage,
                        selected,
                        desiredCount,
                        minDistance: 1,
                        maxSameSymbolPerMatch,
                        enforceDistance: false,
                        enforceSymbolCap: false);
                }

                if (bestIndex == -1)
                {
                    break;
                }

                AddSelectedCandidate(
                    bestIndex,
                    candidates,
                    used,
                    selectedIndices,
                    selected,
                    symbolCountsByMatch,
                    pairCoverage);
                UpdateCurrentBest(currentBest, scenarioScores[bestIndex]);
            }

            return selected;
        }

        private int FindClosestTargetCandidate(
            string target,
            IReadOnlyList<Coupon> candidates,
            IReadOnlyList<bool> used)
        {
            var bestIndex = -1;
            var bestDistance = double.MaxValue;
            var bestUtility = double.MinValue;

            for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
            {
                if (used[candidateIndex])
                {
                    continue;
                }

                var prediction = candidates[candidateIndex].prediction;
                var distance = 0.0;
                for (var matchIndex = 0; matchIndex < target.Length; matchIndex++)
                {
                    if (prediction[matchIndex] != target[matchIndex])
                    {
                        distance += 1.0 +
                            GetAdjustedProbabilities(matchIndex).ForSymbol(target[matchIndex]);
                    }
                }

                if (distance < bestDistance ||
                    (Math.Abs(distance - bestDistance) < 1e-9 &&
                     candidates[candidateIndex].Utility > bestUtility))
                {
                    bestDistance = distance;
                    bestUtility = candidates[candidateIndex].Utility;
                    bestIndex = candidateIndex;
                }
            }

            return bestIndex;
        }

        private static void AddSelectedCandidate(
            int candidateIndex,
            IReadOnlyList<Coupon> candidates,
            bool[] used,
            List<int> selectedIndices,
            List<Coupon> selected,
            Dictionary<char, int>[] symbolCountsByMatch,
            Dictionary<int, int> pairCoverage)
        {
            used[candidateIndex] = true;
            selectedIndices.Add(candidateIndex);
            selected.Add(candidates[candidateIndex]);
            AddToSymbolCounts(candidates[candidateIndex].prediction, symbolCountsByMatch);
            AddToPairCoverage(candidates[candidateIndex].prediction, pairCoverage);
        }

        private int FindBestCandidate(
            List<Coupon> candidates,
            List<byte[]> scenarioScores,
            byte[] currentBest,
            bool[] used,
            Dictionary<char, int>[] symbolCountsByMatch,
            Dictionary<char, int>[] minimumCoverageTargets,
            Dictionary<int, int> pairCoverage,
            List<Coupon> selected,
            int desiredCount,
            int minDistance,
            int maxSameSymbolPerMatch,
            bool enforceDistance,
            bool enforceSymbolCap)
        {
            var bestIndex = -1;
            var bestGain = double.MinValue;

            for (var i = 0; i < candidates.Count; i++)
            {
                if (used[i])
                {
                    continue;
                }

                if (enforceDistance &&
                    selected.Count > 0 &&
                    selected.Any(x => Distance(x.prediction, candidates[i].prediction) < minDistance))
                {
                    continue;
                }

                if (enforceSymbolCap &&
                    ExceedsPerMatchSymbolCap(
                        candidates[i].prediction,
                        symbolCountsByMatch,
                        desiredCount,
                        maxSameSymbolPerMatch))
                {
                    continue;
                }

                var gain = 0.0;
                var candidateScores = scenarioScores[i];

                for (var s = 0; s < _scenarioCount; s++)
                {
                    var improved = Math.Max(currentBest[s], candidateScores[s]);
                    gain += ScoreForHits(improved) - ScoreForHits(currentBest[s]);
                }

                gain /= _scenarioCount;
                gain += candidates[i].Utility * 0.02;
                gain += CoverageAdjustment(
                    candidates[i].prediction,
                    symbolCountsByMatch,
                    minimumCoverageTargets,
                    pairCoverage,
                    selected.Count,
                    desiredCount);

                if (gain > bestGain)
                {
                    bestGain = gain;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        private static Dictionary<char, int>[] CreateSymbolCounts()
        {
            var result = new Dictionary<char, int>[15];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = new Dictionary<char, int>
                {
                    ['1'] = 0,
                    ['X'] = 0,
                    ['2'] = 0
                };
            }

            return result;
        }

        private bool ExceedsPerMatchSymbolCap(
            string prediction,
            Dictionary<char, int>[] symbolCountsByMatch,
            int desiredCount,
            int maxSameSymbolPerMatch)
        {
            for (var i = 0; i < prediction.Length; i++)
            {
                var probability = GetAdjustedProbabilities(i).ForSymbol(prediction[i]);
                var probabilityCap = Math.Max(
                    2,
                    (int)Math.Ceiling((probability + 0.14) * desiredCount));
                var allowedCount = Math.Min(maxSameSymbolPerMatch, probabilityCap);

                if (symbolCountsByMatch[i][prediction[i]] >= allowedCount)
                {
                    return true;
                }
            }

            return false;
        }

        private double CoverageAdjustment(
            string prediction,
            Dictionary<char, int>[] symbolCountsByMatch,
            Dictionary<char, int>[] minimumCoverageTargets,
            Dictionary<int, int> pairCoverage,
            int selectedCount,
            int desiredCount)
        {
            if (desiredCount < 10)
            {
                return 0.0;
            }

            var remainingAfterThis = desiredCount - selectedCount - 1;
            var adjustment = 0.0;

            for (var i = 0; i < prediction.Length; i++)
            {
                var counts = symbolCountsByMatch[i];
                var chosen = prediction[i];
                var probabilities = GetAdjustedProbabilities(i);
                var chosenProbability = probabilities.ForSymbol(chosen);
                var targetCount = chosenProbability * desiredCount;
                var minimumTarget = minimumCoverageTargets[i][chosen];
                var minimumDeficit = minimumTarget - counts[chosen];

                adjustment += (targetCount - counts[chosen]) / desiredCount;
                if (minimumDeficit > 0)
                {
                    adjustment += 2.5 * minimumDeficit / Math.Max(minimumTarget, 1);
                }

                foreach (var symbol in new[] { '1', 'X', '2' })
                {
                    var projectedCount = counts[symbol] + (chosen == symbol ? 1 : 0);
                    var minimumCoverage = minimumCoverageTargets[i][symbol];

                    if (minimumCoverage > 0 &&
                        projectedCount + remainingAfterThis < minimumCoverage)
                    {
                        adjustment -= 2.0;
                    }
                }
            }

            var pairNovelty = PairCoverageAdjustment(prediction, pairCoverage);
            return (0.22 * adjustment / prediction.Length) + pairNovelty;
        }

        private double PairCoverageAdjustment(
            string prediction,
            IReadOnlyDictionary<int, int> pairCoverage)
        {
            var eligiblePairs = 0;
            var novelty = 0.0;

            for (var left = 0; left < prediction.Length; left++)
            {
                var leftProbability = GetAdjustedProbabilities(left).ForSymbol(prediction[left]);
                if (leftProbability < 0.08)
                {
                    continue;
                }

                for (var right = left + 1; right < prediction.Length; right++)
                {
                    var rightProbability = GetAdjustedProbabilities(right).ForSymbol(prediction[right]);
                    if (rightProbability < 0.08)
                    {
                        continue;
                    }

                    eligiblePairs++;
                    var key = PairKey(left, prediction[left], right, prediction[right]);
                    var count = pairCoverage.GetValueOrDefault(key);
                    novelty += 1.0 / (1.0 + count);
                }
            }

            return eligiblePairs == 0
                ? 0.0
                : 0.06 * novelty / eligiblePairs;
        }

        private Dictionary<char, int>[] CreateMinimumCoverageTargets(int desiredCount)
        {
            var result = CreateSymbolCounts();
            if (desiredCount < 10)
            {
                return result;
            }

            for (var i = 0; i < result.Length; i++)
            {
                var probabilities = GetAdjustedProbabilities(i);
                foreach (var symbol in new[] { '1', 'X', '2' })
                {
                    var probability = probabilities.ForSymbol(symbol);
                    if (probability < 0.08)
                    {
                        continue;
                    }

                    var target = probability >= 0.18
                        ? (int)Math.Floor(probability * desiredCount * 0.58)
                        : 1;
                    result[i][symbol] = Math.Clamp(target, 1, Math.Max(1, desiredCount / 2));
                }
            }

            return result;
        }

        private static void AddToSymbolCounts(string prediction, Dictionary<char, int>[] symbolCountsByMatch)
        {
            for (var i = 0; i < prediction.Length; i++)
            {
                symbolCountsByMatch[i][prediction[i]]++;
            }
        }

        private static void AddToPairCoverage(
            string prediction,
            Dictionary<int, int> pairCoverage)
        {
            for (var left = 0; left < prediction.Length; left++)
            {
                for (var right = left + 1; right < prediction.Length; right++)
                {
                    var key = PairKey(left, prediction[left], right, prediction[right]);
                    pairCoverage[key] = pairCoverage.GetValueOrDefault(key) + 1;
                }
            }
        }

        private static int PairKey(int left, char leftSymbol, int right, char rightSymbol)
        {
            return ((((left * 15) + right) * 3) + SymbolIndex(leftSymbol)) * 3 +
                   SymbolIndex(rightSymbol);
        }

        private static int SymbolIndex(char symbol)
        {
            return symbol switch
            {
                '1' => 0,
                'X' => 1,
                _ => 2
            };
        }

        private SymbolProbabilities GetAdjustedProbabilities(int matchIndex)
        {
            var probabilities = _model.GetForPosition(matchIndex);
            if (_probabilityUniformBlend <= 0.0)
            {
                return probabilities;
            }

            return SymbolProbabilities.Normalize(
                BlendUniform(probabilities.One),
                BlendUniform(probabilities.Draw),
                BlendUniform(probabilities.Two));
        }

        private double BlendUniform(double probability)
        {
            return (probability * (1.0 - _probabilityUniformBlend)) +
                   ((1.0 / 3.0) * _probabilityUniformBlend);
        }

        private List<char[]> SimulateOutcomes()
        {
            var scenarios = new List<char[]>(_scenarioCount);
            for (var i = 0; i < _scenarioCount; i++)
            {
                var row = new char[15];
                for (var m = 0; m < 15; m++)
                {
                    var p = GetAdjustedProbabilities(m);
                    var r = _random.NextDouble();
                    row[m] = r < p.One ? '1' : r < p.One + p.Draw ? 'X' : '2';
                }

                scenarios.Add(row);
            }

            return scenarios;
        }

        private List<byte[]> BuildScenarioScores(List<Coupon> candidates, List<char[]> outcomes)
        {
            var result = new List<byte[]>(candidates.Count);

            foreach (var coupon in candidates)
            {
                var scores = new byte[_scenarioCount];

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

                    scores[s] = (byte)correct;
                }

                result.Add(scores);
            }

            return result;
        }

        private static void UpdateCurrentBest(byte[] currentBest, byte[] candidateScores)
        {
            for (var scenarioIndex = 0; scenarioIndex < currentBest.Length; scenarioIndex++)
            {
                currentBest[scenarioIndex] = Math.Max(
                    currentBest[scenarioIndex],
                    candidateScores[scenarioIndex]);
            }
        }

        private static double ScoreForHits(int correct)
        {
            return correct switch
            {
                15 => 1.00,
                14 => 0.40,
                13 => 0.16,
                12 => 0.06,
                11 => 0.020,
                10 => 0.006,
                9 => 0.0015,
                _ => 0.0
            };
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

    public static class CoverageScenarioGenerator
    {
        public static IReadOnlyList<string> Generate(
            HistoricalOutcomeModel model,
            int maximumCount,
            double thirdChoiceMinRatio = 1.01,
            double probabilityUniformBlend = 0.0,
            double portfolioJitter = 0.0)
        {
            var targetCount = Math.Clamp(maximumCount, 1, 32768);
            var thirdChoiceThreshold = Math.Clamp(thirdChoiceMinRatio, 0.0, 1.01);
            var uniformBlend = Math.Clamp(probabilityUniformBlend, 0.0, 0.35);
            var jitter = Math.Clamp(portfolioJitter, 0.0, 2.0);
            var states = new List<CoverageScenarioState>
            {
                new(string.Empty, 0.0)
            };

            for (var matchIndex = 0; matchIndex < 15; matchIndex++)
            {
                var probabilities = model.GetForPosition(matchIndex);
                var choicesByProbability = new[]
                    {
                        new CoverageChoice('1', BlendUniform(probabilities.One, uniformBlend)),
                        new CoverageChoice('X', BlendUniform(probabilities.Draw, uniformBlend)),
                        new CoverageChoice('2', BlendUniform(probabilities.Two, uniformBlend))
                    }
                    .OrderByDescending(x => x.Probability)
                    .ToArray();
                var topProbability = choicesByProbability[0].Probability;
                var thirdProbability = choicesByProbability[2].Probability;
                var thirdToTopRatio = thirdProbability / Math.Max(topProbability, 1e-12);
                var includeThirdChoice =
                    thirdToTopRatio >= thirdChoiceThreshold ||
                    topProbability <= 0.48 ||
                    thirdProbability >= 0.18;
                var choiceCount = includeThirdChoice ? 3 : 2;
                var choices = choicesByProbability.Take(choiceCount).ToArray();

                states = states
                    .SelectMany(state => choices.Select(choice =>
                    {
                        var logScore = state.LogProbability +
                                       Math.Log(Math.Max(choice.Probability, 1e-12));
                        if (jitter > 0.000001)
                        {
                            logScore += BuildDeterministicJitter(
                                state.Prediction,
                                matchIndex,
                                choice.Symbol,
                                jitter);
                        }

                        return new CoverageScenarioState(
                            state.Prediction + choice.Symbol,
                            logScore);
                    }))
                    .OrderByDescending(x => x.LogProbability)
                    .Take(targetCount)
                    .ToList();
            }

            return states.Select(x => x.Prediction).ToList();
        }

        private static double BlendUniform(double probability, double uniformBlend)
        {
            return (probability * (1.0 - uniformBlend)) +
                   ((1.0 / 3.0) * uniformBlend);
        }

        private static double BuildDeterministicJitter(
            string prefix,
            int matchIndex,
            char symbol,
            double jitter)
        {
            var unit = BuildDeterministicUnit(prefix, matchIndex, symbol, jitter);
            return ((unit * 2.0) - 1.0) * jitter;
        }

        private static double BuildDeterministicUnit(
            string prefix,
            int matchIndex,
            char symbol,
            double salt)
        {
            unchecked
            {
                var hash = 14695981039346656037UL;

                void Mix(ulong value)
                {
                    hash ^= value;
                    hash *= 1099511628211UL;
                }

                foreach (var c in prefix)
                {
                    Mix(c);
                }

                Mix((ulong)(matchIndex + 1));
                Mix(symbol);
                Mix((ulong)Math.Round(salt * 10000.0));

                hash ^= hash >> 33;
                hash *= 0xff51afd7ed558ccdUL;
                hash ^= hash >> 33;
                hash *= 0xc4ceb9fe1a85ec53UL;
                hash ^= hash >> 33;

                return (hash >> 11) * (1.0 / 9007199254740992.0);
            }
        }

        private sealed record CoverageChoice(char Symbol, double Probability);
        private sealed record CoverageScenarioState(
            string Prediction,
            double LogProbability);
    }
}
