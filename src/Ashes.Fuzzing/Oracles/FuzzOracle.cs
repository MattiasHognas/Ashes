using Ashes.Fuzzing.Execution;
using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Oracles;

internal sealed record FuzzOracleResult(string Oracle, bool Success, string Message, string StandardOutput = "", string StandardError = "")
{
    internal static FuzzOracleResult Passed(string oracle) => new(oracle, true, "passed");
    internal static FuzzOracleResult Failed(string oracle, string message, string stdout = "", string stderr = "") => new(oracle, false, message, stdout, stderr);
}

internal sealed record FuzzExecutionContext(
    string RepositoryRoot,
    string Target,
    TimeSpan CompilerTimeout,
    TimeSpan ProgramTimeout,
    int MaximumOutputBytes,
    CompilerExecution Compiler);

internal interface IFuzzOracle
{
    string Id { get; }
    ValueTask<FuzzOracleResult> EvaluateAsync(GeneratedFuzzCase testCase, FuzzExecutionContext context, CancellationToken cancellationToken);
}

internal sealed class FuzzOracleRegistry
{
    private readonly SortedDictionary<string, IFuzzOracle> _oracles = new(StringComparer.Ordinal);
    internal IReadOnlyCollection<IFuzzOracle> Oracles => _oracles.Values;
    internal void Register(IFuzzOracle oracle)
    {
        if (string.IsNullOrWhiteSpace(oracle.Id) || !_oracles.TryAdd(oracle.Id, oracle))
        {
            throw new ArgumentException($"Duplicate or invalid oracle '{oracle.Id}'.");
        }
    }
    internal IFuzzOracle Get(string id) => _oracles.TryGetValue(id, out IFuzzOracle? oracle) ? oracle : throw new ArgumentException($"Unknown oracle '{id}'.");
    internal static FuzzOracleRegistry CreateDefault()
    {
        FuzzOracleRegistry registry = new();
        registry.Register(new ParseOracle());
        registry.Register(new FormatOracle());
        registry.Register(new SemanticOracle());
        registry.Register(new IrVerificationOracle());
        registry.Register(new InvalidSourceOracle());
        registry.Register(new InvalidSemanticOracle());
        registry.Register(new ExecutionOracle());
        registry.Register(new DifferentialOptimizationOracle());
        registry.Register(new DifferentialReuseOracle());
        registry.Register(new DifferentialTraitEvidenceOracle());
        registry.Register(new CrossTargetOracle());
        return registry;
    }
}
