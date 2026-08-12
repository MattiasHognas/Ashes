using Ashes.Fuzzing.Generation;
using Ashes.Fuzzing.Oracles;

namespace Ashes.Fuzzing.Persistence;

internal static class FuzzFailureReport
{
    internal static IReadOnlyList<string> Lines(
        GeneratedFuzzCase testCase,
        FuzzOracleResult result,
        string artifactPath,
        string replayCommand)
    {
        string[] rules = Selected(testCase.Trace, "rule:");
        string[] combinations = Selected(testCase.Trace, "combination:");
        GenerationBudget budget = testCase.Budget;
        return
        [
            $"fuzz failure: seed={testCase.MasterSeed} case-seed={testCase.CaseSeed} case={testCase.CaseIndex} profile={testCase.Profile} oracle={result.Oracle}",
            $"budget: nodes={budget.RemainingNodes} depth={budget.RemainingDepth} declarations={budget.RemainingDeclarations} functions={budget.RemainingFunctions} adts={budget.RemainingAdts} match-cases={budget.MaximumMatchCases} collection={budget.MaximumCollectionLength} recursion={budget.RemainingRecursion} combinations={budget.RemainingCombinations} source={budget.MaximumSourceLength}",
            "selected rules: " + (rules.Length == 0 ? "none" : string.Join(",", rules)),
            "selected combinations: " + (combinations.Length == 0 ? "none" : string.Join(",", combinations)),
            result.Message,
            testCase.Source,
            $"artifact: {artifactPath}",
            $"replay: {replayCommand}",
        ];
    }

    internal static string CompilerConfiguration(string oracle) => oracle switch
    {
        "differential-optimization" => "Release; -O0 vs -O2",
        "differential-reuse" => "Release; -O2 reuse enabled vs disabled",
        "execution" or "cross-target" or "memory-growth" => "Release; -O2",
        _ => "in-process compiler defaults",
    };

    private static string[] Selected(GenerationTrace trace, string prefix) => trace.Entries
        .Where(entry => entry.StartsWith(prefix, StringComparison.Ordinal))
        .Select(entry => entry[prefix.Length..])
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();
}
