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
            FuzzSemanticCompilation compilation = FuzzSemanticCompiler.Lower(testCase.Source);
            compilation.Diagnostics.ThrowIfAny();
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
            FuzzSemanticCompilation compilation = FuzzSemanticCompiler.Lower(testCase.Source);
            compilation.Diagnostics.ThrowIfAny();
            IrProgram ir = compilation.Ir;
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

internal sealed class InvalidSemanticOracle : IFuzzOracle
{
    public string Id => "invalid-semantic";

    public ValueTask<FuzzOracleResult> EvaluateAsync(
        GeneratedFuzzCase testCase,
        FuzzExecutionContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            if (testCase.ExpectedDiagnosticCodes.Count == 0)
            {
                return ValueTask.FromResult(FuzzOracleResult.Failed(
                    Id,
                    "Invalid-semantic case did not declare an expected diagnostic code."));
            }
            FuzzSemanticCompilation compilation = FuzzSemanticCompiler.Lower(testCase.Source);
            IReadOnlyList<DiagnosticEntry> errors = compilation.Diagnostics.StructuredErrors;
            if (errors.Count == 0)
            {
                return ValueTask.FromResult(FuzzOracleResult.Failed(
                    Id,
                    "Invalid-semantic case was unexpectedly accepted."));
            }
            if (errors.Count > 64)
            {
                return ValueTask.FromResult(FuzzOracleResult.Failed(
                    Id,
                    $"Invalid-semantic case produced an unbounded diagnostic cascade ({errors.Count})."));
            }
            string[] actualCodes = errors
                .Select(error => error.Code ?? "<uncoded>")
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            string? missing = testCase.ExpectedDiagnosticCodes.FirstOrDefault(expected =>
                !actualCodes.Contains(expected, StringComparer.Ordinal));
            return ValueTask.FromResult(missing is null
                ? FuzzOracleResult.Passed(Id)
                : FuzzOracleResult.Failed(
                    Id,
                    $"Expected diagnostic '{missing}', got: {string.Join(", ", actualCodes)}."));
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(FuzzOracleResult.Failed(Id, exception.ToString()));
        }
    }
}
