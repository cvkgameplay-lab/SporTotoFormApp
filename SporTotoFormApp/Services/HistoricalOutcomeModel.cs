namespace SporTotoFormApp.Services
{
    public sealed class HistoricalOutcomeModel
    {
        private const int MatchCount = 15;
        private readonly Dictionary<int, SymbolProbabilities> _positionProbabilities;

        private HistoricalOutcomeModel(Dictionary<int, SymbolProbabilities> positionProbabilities)
        {
            _positionProbabilities = positionProbabilities;
        }

        public SymbolProbabilities GetForPosition(int index)
        {
            if (_positionProbabilities.TryGetValue(index, out var p))
            {
                return p;
            }

            return SymbolProbabilities.Default;
        }

        public static HistoricalOutcomeModel Create(string baseDirectory)
        {
            var defaultModel = CreateDefault();
            var dataPath = FindHistoricalFile(baseDirectory);

            if (dataPath == null || !File.Exists(dataPath))
            {
                return defaultModel;
            }

            var lines = File.ReadAllLines(dataPath)
                .Select(x => x.Trim().ToUpperInvariant())
                .Where(x => x.Length == MatchCount && x.All(c => c is '1' or 'X' or '2'))
                .ToList();

            if (lines.Count < 20)
            {
                return defaultModel;
            }

            var result = new Dictionary<int, SymbolProbabilities>();

            for (var i = 0; i < MatchCount; i++)
            {
                var count1 = 1.0;
                var countX = 1.0;
                var count2 = 1.0;

                foreach (var line in lines)
                {
                    switch (line[i])
                    {
                        case '1': count1++; break;
                        case 'X': countX++; break;
                        case '2': count2++; break;
                    }
                }

                result[i] = SymbolProbabilities.FromCounts(count1, countX, count2);
            }

            return new HistoricalOutcomeModel(result);
        }

        private static string? FindHistoricalFile(string baseDirectory)
        {
            const string relativePath = "Data/historical_results.txt";
            var current = new DirectoryInfo(baseDirectory);

            for (var i = 0; i < 6 && current != null; i++)
            {
                var candidate = Path.Combine(current.FullName, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                current = current.Parent;
            }

            return null;
        }

        public static HistoricalOutcomeModel CreateDefault()
        {
            var map = new Dictionary<int, SymbolProbabilities>();

            for (var i = 0; i < MatchCount; i++)
            {
                var p1 = 0.45 + ((i % 3) - 1) * 0.01;
                var pX = 0.28 + ((i % 2 == 0) ? 0.01 : -0.01);
                var p2 = 1.0 - p1 - pX;

                map[i] = SymbolProbabilities.Normalize(p1, pX, p2);
            }

            return new HistoricalOutcomeModel(map);
        }
    }

    public readonly struct SymbolProbabilities
    {
        public SymbolProbabilities(double one, double draw, double two)
        {
            One = one;
            Draw = draw;
            Two = two;
        }

        public double One { get; }
        public double Draw { get; }
        public double Two { get; }

        public static SymbolProbabilities Default => new(0.46, 0.28, 0.26);

        public static SymbolProbabilities FromCounts(double one, double draw, double two)
        {
            return Normalize(one, draw, two);
        }

        public static SymbolProbabilities Normalize(double one, double draw, double two)
        {
            var sum = one + draw + two;
            if (sum <= 0)
            {
                return Default;
            }

            return new SymbolProbabilities(one / sum, draw / sum, two / sum);
        }

        public double ForSymbol(char symbol)
        {
            return symbol switch
            {
                '1' => One,
                'X' => Draw,
                '2' => Two,
                _ => 1e-6
            };
        }
    }
}
