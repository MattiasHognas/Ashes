using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Coverage;

internal sealed class FuzzCoverage
{
    private readonly SortedDictionary<string, long> _counts = new(StringComparer.Ordinal);
    internal int MaximumDepth { get; private set; }
    internal int MaximumNodeCount { get; private set; }
    internal int MaximumCombinationCount { get; private set; }
    internal int CoveredRuleCount => CountKeys("rule:");
    internal int CoveredCombinationCount => CountKeys("combination:");

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
        return $"coverage: types={types}, rules={rules}, combinations={combinations}, pairs={pairs}, triples={triples}, oracles={oracles}, max-nodes={MaximumNodeCount}, max-depth={MaximumDepth}, max-combinations={MaximumCombinationCount}; features: {top}";
    }

    private int CountKeys(string prefix) => _counts.Keys.Count(key => key.StartsWith(prefix, StringComparison.Ordinal));

    private void Increment(string key) => _counts[key] = _counts.GetValueOrDefault(key) + 1;
}
