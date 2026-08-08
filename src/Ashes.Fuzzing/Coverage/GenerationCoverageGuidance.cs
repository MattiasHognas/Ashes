namespace Ashes.Fuzzing.Coverage;

internal sealed class GenerationCoverageGuidance
{
    private readonly SortedDictionary<string, int> _ruleCounts = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, int> _combinationCounts = new(StringComparer.Ordinal);

    internal GenerationCoverageGuidance(IEnumerable<string> rules, IEnumerable<string> combinations)
    {
        foreach (string id in rules.Order(StringComparer.Ordinal))
        {
            _ruleCounts.TryAdd(id, 0);
        }
        foreach (string id in combinations.Order(StringComparer.Ordinal))
        {
            _combinationCounts.TryAdd(id, 0);
        }
    }

    internal int RuleWeight(string id, int baseWeight, bool preferred)
        => EffectiveWeight(_ruleCounts, id, baseWeight, preferred);

    internal int CombinationWeight(string id, bool preferred)
        => EffectiveWeight(_combinationCounts, id, 4, preferred);

    internal void RecordRule(string id) => Increment(_ruleCounts, id);

    internal void RecordCombination(string id) => Increment(_combinationCounts, id);

    internal int RuleCount(string id) => _ruleCounts.GetValueOrDefault(id);

    internal int CombinationCount(string id) => _combinationCounts.GetValueOrDefault(id);

    private static int EffectiveWeight(IReadOnlyDictionary<string, int> counts, string id, int baseWeight, bool preferred)
    {
        int maximum = counts.Count == 0 ? 0 : counts.Values.Max();
        int debt = maximum - counts.GetValueOrDefault(id);
        return baseWeight + (debt * 6) + (preferred ? 24 : 0);
    }

    private static void Increment(IDictionary<string, int> counts, string id)
    {
        if (!counts.ContainsKey(id))
        {
            throw new InvalidOperationException($"Coverage guidance received unknown entry '{id}'.");
        }
        counts[id]++;
    }
}
