using Ashes.Frontend;
using Ashes.Fuzzing.Generation;
using Ashes.Semantics;

namespace Ashes.Fuzzing.Oracles;

using AshesFormatter = Ashes.Formatter.Formatter;
using FrontendProgram = Ashes.Frontend.Program;

internal sealed class ParseOracle : IFuzzOracle
{
    public string Id => "parse";
    public ValueTask<FuzzOracleResult> EvaluateAsync(GeneratedFuzzCase testCase, FuzzExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            Diagnostics diagnostics = new();
            _ = new Parser(testCase.Source, diagnostics).ParseProgram();
            return ValueTask.FromResult(diagnostics.Errors.Count == 0 ? FuzzOracleResult.Passed(Id) : FuzzOracleResult.Failed(Id, string.Join(Environment.NewLine, diagnostics.Errors)));
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(FuzzOracleResult.Failed(Id, exception.ToString()));
        }
    }
}

internal sealed class FormatOracle : IFuzzOracle
{
    public string Id => "format";
    public ValueTask<FuzzOracleResult> EvaluateAsync(GeneratedFuzzCase testCase, FuzzExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            string astFormatted = AshesFormatter.Format(testCase.Program);
            if (!string.Equals(testCase.Source, astFormatted, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(FuzzOracleResult.Failed(
                    Id,
                    "Generated source differs from formatting its original AST."));
            }
            Diagnostics firstDiagnostics = new();
            FrontendProgram first = new Parser(testCase.Source, firstDiagnostics).ParseProgram();
            string firstFormatted = AshesFormatter.Format(first);
            Diagnostics secondDiagnostics = new();
            FrontendProgram second = new Parser(firstFormatted, secondDiagnostics).ParseProgram();
            string secondFormatted = AshesFormatter.Format(second);
            if (firstDiagnostics.Errors.Count != 0 || secondDiagnostics.Errors.Count != 0)
            {
                return ValueTask.FromResult(FuzzOracleResult.Failed(Id, "A formatter round-trip parse produced diagnostics."));
            }
            if (!string.Equals(astFormatted, firstFormatted, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(FuzzOracleResult.Failed(
                    Id,
                    "AST formatting changed after parsing and formatting again."));
            }
            return ValueTask.FromResult(string.Equals(firstFormatted, secondFormatted, StringComparison.Ordinal)
                ? FuzzOracleResult.Passed(Id)
                : FuzzOracleResult.Failed(Id, "Formatting is not idempotent."));
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(FuzzOracleResult.Failed(Id, exception.ToString()));
        }
    }
}

internal sealed class SemanticOracle : IFuzzOracle
{
    public string Id => "semantic";
    public ValueTask<FuzzOracleResult> EvaluateAsync(GeneratedFuzzCase testCase, FuzzExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            Diagnostics diagnostics = new();
            FrontendProgram parsed = new Parser(testCase.Source, diagnostics).ParseProgram();
            _ = new Lowering(diagnostics).Lower(parsed);
            diagnostics.ThrowIfAny();
            return ValueTask.FromResult(FuzzOracleResult.Passed(Id));
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(FuzzOracleResult.Failed(Id, exception.ToString()));
        }
    }
}

internal sealed class IrVerificationOracle : IFuzzOracle
{
    public string Id => "ir";
    public ValueTask<FuzzOracleResult> EvaluateAsync(GeneratedFuzzCase testCase, FuzzExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            Diagnostics diagnostics = new();
            FrontendProgram parsed = new Parser(testCase.Source, diagnostics).ParseProgram();
            IrProgram ir = new Lowering(diagnostics).Lower(parsed);
            diagnostics.ThrowIfAny();
            IrInvariantVerifier verifier = new();
            IReadOnlyList<string> loweringErrors = verifier.Verify(ir);
            if (loweringErrors.Count != 0)
            {
                return ValueTask.FromResult(FuzzOracleResult.Failed(Id, string.Join(Environment.NewLine, loweringErrors)));
            }
            IrProgram optimized = IrOptimizer.Optimize(ir);
            IReadOnlyList<string> optimizationErrors = verifier.Verify(optimized);
            if (optimizationErrors.Count != 0)
            {
                return ValueTask.FromResult(FuzzOracleResult.Failed(Id, string.Join(Environment.NewLine, optimizationErrors)));
            }
            return ValueTask.FromResult(FuzzOracleResult.Passed(Id));
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(FuzzOracleResult.Failed(Id, exception.ToString()));
        }
    }
}
