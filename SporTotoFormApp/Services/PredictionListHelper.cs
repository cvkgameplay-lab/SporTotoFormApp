namespace SporTotoFormApp.Services
{
    public sealed class PredictionListHelper
    {
        private readonly PredictionGenerationRules _rules;

        public PredictionListHelper(PredictionGenerationRules? rules = null)
        {
            _rules = rules ?? PredictionGenerationRules.Default;
        }

        public IEnumerable<string> FiltreliUret()
        {
            var buffer = new char[_rules.MatchCount];

            foreach (var result in Generate(buffer, 0))
            {
                yield return result;
            }
        }

        private IEnumerable<string> Generate(char[] buffer, int depth)
        {
            if (depth == _rules.MatchCount)
            {
                var candidate = new string(buffer);
                if (MatchesDistribution(candidate))
                {
                    yield return candidate;
                }
                yield break;
            }

            foreach (var option in PredictionGenerationRules.ValidSymbols)
            {
                if (IsStreakLimitExceeded(buffer, depth, option))
                {
                    continue;
                }

                buffer[depth] = option;

                foreach (var generated in Generate(buffer, depth + 1))
                {
                    yield return generated;
                }
            }
        }

        private bool IsStreakLimitExceeded(char[] buffer, int depth, char newOption)
        {
            if (depth < _rules.MaxConsecutiveSame)
            {
                return false;
            }

            for (var i = 1; i <= _rules.MaxConsecutiveSame; i++)
            {
                if (buffer[depth - i] != newOption)
                {
                    return false;
                }
            }

            return true;
        }

        private bool MatchesDistribution(string candidate)
        {
            var count1 = candidate.Count(c => c == '1');
            var countX = candidate.Count(c => c == 'X');
            var count2 = candidate.Count(c => c == '2');

            return count1 >= _rules.MinOne && count1 <= _rules.MaxOne
                && countX >= _rules.MinDraw && countX <= _rules.MaxDraw
                && count2 >= _rules.MinTwo && count2 <= _rules.MaxTwo;
        }
    }

    public sealed class PredictionGenerationRules
    {
        public static readonly char[] ValidSymbols = ['1', 'X', '2'];

        public static PredictionGenerationRules Default => new();

        public int MatchCount { get; init; } = 15;
        public int MaxConsecutiveSame { get; init; } = 3;
        public int MinOne { get; init; } = 5;
        public int MaxOne { get; init; } = 9;
        public int MinDraw { get; init; } = 2;
        public int MaxDraw { get; init; } = 6;
        public int MinTwo { get; init; } = 2;
        public int MaxTwo { get; init; } = 6;
    }
}
