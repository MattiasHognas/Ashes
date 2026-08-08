using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Coverage;

internal sealed class FuzzCoverage
{
    private readonly SortedDictionary<string, long> _counts = new(StringComparer.Ordinal);
    internal FuzzCoverage(IEnumerable<string>? rules = null, IEnumerable<string>? combinations = null)
    {
        foreach (string rule in rules ?? [])
        {
            _counts.TryAdd("rule:" + rule, 0);
        }
        foreach (string combination in combinations ?? [])
        {
            _counts.TryAdd("combination:" + combination, 0);
        }
    }

    internal int MaximumDepth { get; private set; }
    internal int MaximumNodeCount { get; private set; }
    internal int MaximumCombinationCount { get; private set; }
    internal int CoveredRuleCount => CountKeys("rule:");
    internal int CoveredCombinationCount => CountKeys("combination:");
    internal IReadOnlyList<string> MissingRules => Missing("rule:");
    internal IReadOnlyList<string> MissingCombinations => Missing("combination:");

    internal void Record(GeneratedFuzzCase testCase, IEnumerable<string> oracles)
    {
        Increment("type:" + testCase.Type);
        foreach (GeneratedFeature feature in testCase.Features) Increment("feature:" + feature);
        string[] features = testCase.Features.Select(feature => feature.ToString()).Order(StringComparer.Ordinal).ToArray();
        for (int i = 0; i < features.Length; i++)
        {
            for (int j = i + 1; j < features.Length; j++) Increment($"pair:{features[i]}+{features[j]}");
        }
        for (int i = 0; i < features.Length; i++)
        {
            for (int j = i + 1; j < features.Length; j++)
            {
                for (int k = j + 1; k < features.Length; k++) Increment($"triple:{features[i]}+{features[j]}+{features[k]}");
            }
        }
        int combinationCount = 0;
        foreach (string entry in testCase.Trace.Entries)
        {
            if (entry.StartsWith("combination:", StringComparison.Ordinal))
            {
                Increment(entry);
                combinationCount++;
            }
            else if (entry.StartsWith("rule:", StringComparison.Ordinal))
            {
                Increment(entry);
            }
        }
        foreach (string oracle in oracles) Increment("oracle:" + oracle);
        AstCoverageMetrics ast = AstCoverageMetrics.Measure(testCase.Program);
        MaximumNodeCount = Math.Max(MaximumNodeCount, ast.Nodes);
        MaximumDepth = Math.Max(MaximumDepth, ast.Depth);
        MaximumCombinationCount = Math.Max(MaximumCombinationCount, combinationCount);
    }

    internal string Summary()
    {
        string top = string.Join(", ", _counts.Where(pair => pair.Key.StartsWith("feature:", StringComparison.Ordinal)).OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Take(12).Select(pair => $"{pair.Key[8..]}={pair.Value}"));
        int types = CountKeys("type:");
        int rules = CountKeys("rule:");
        int combinations = CountKeys("combination:");
        int pairs = CountKeys("pair:");
        int triples = CountKeys("triple:");
        int oracles = CountKeys("oracle:");
        return $"coverage: types={types}, rules={rules}, combinations={combinations}, missing-rules={MissingRules.Count}, missing-combinations={MissingCombinations.Count}, pairs={pairs}, triples={triples}, oracles={oracles}, max-nodes={MaximumNodeCount}, max-depth={MaximumDepth}, max-combinations={MaximumCombinationCount}; features: {top}";
    }

    private int CountKeys(string prefix) => _counts.Count(pair =>
        pair.Value > 0 && pair.Key.StartsWith(prefix, StringComparison.Ordinal));

    private IReadOnlyList<string> Missing(string prefix) => _counts
        .Where(pair => pair.Value == 0 && pair.Key.StartsWith(prefix, StringComparison.Ordinal))
        .Select(pair => pair.Key[prefix.Length..])
        .ToArray();

    private void Increment(string key) => _counts[key] = _counts.GetValueOrDefault(key) + 1;
}
