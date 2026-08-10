using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Oracles;

internal sealed class ExecutionOracle : IFuzzOracle
{
    public string Id => "execution";
    public async ValueTask<FuzzOracleResult> EvaluateAsync(GeneratedFuzzCase testCase, FuzzExecutionContext context, CancellationToken cancellationToken)
    {
        string observableSource = Execution.ObservableValueRenderer.RenderProgram(testCase);
        (Execution.ProcessResult compile, Execution.ProcessResult? run) = await context.Compiler.CompileAndRunAsync(observableSource, context.RepositoryRoot, context.Target, "-O2", context.CompilerTimeout, context.ProgramTimeout, context.MaximumOutputBytes, cancellationToken).ConfigureAwait(false);
        if (NativeOutcomeValidator.Failure("compiler", compile) is string compileFailure)
        {
            return FuzzOracleResult.Failed(Id, compileFailure, compile.StandardOutput, compile.StandardError);
        }
        if (run is null)
        {
            return FuzzOracleResult.Failed(Id, "Compilation succeeded but the selected target could not be executed.", compile.StandardOutput, compile.StandardError);
        }
        string? runFailure = NativeOutcomeValidator.Failure("generated program", run);
        return runFailure is not null
            ? FuzzOracleResult.Failed(Id, runFailure, run.StandardOutput, run.StandardError)
            : FuzzOracleResult.Passed(Id);
    }
}

internal sealed class DifferentialOptimizationOracle : IFuzzOracle
{
    public string Id => "differential-optimization";
    public async ValueTask<FuzzOracleResult> EvaluateAsync(GeneratedFuzzCase testCase, FuzzExecutionContext context, CancellationToken cancellationToken)
    {
        string observableSource = Execution.ObservableValueRenderer.RenderProgram(testCase);
        (Execution.ProcessResult compile0, Execution.ProcessResult? run0) = await context.Compiler.CompileAndRunAsync(observableSource, context.RepositoryRoot, context.Target, "-O0", context.CompilerTimeout, context.ProgramTimeout, context.MaximumOutputBytes, cancellationToken).ConfigureAwait(false);
        (Execution.ProcessResult compile2, Execution.ProcessResult? run2) = await context.Compiler.CompileAndRunAsync(observableSource, context.RepositoryRoot, context.Target, "-O2", context.CompilerTimeout, context.ProgramTimeout, context.MaximumOutputBytes, cancellationToken).ConfigureAwait(false);
        string? compile0Failure = NativeOutcomeValidator.Failure("-O0 compiler", compile0);
        string? compile2Failure = NativeOutcomeValidator.Failure("-O2 compiler", compile2);
        if (compile0Failure is not null || compile2Failure is not null)
        {
            string message = $"Compiler configuration failure: {compile0Failure ?? "-O0 passed"}; {compile2Failure ?? "-O2 passed"}.";
            return FuzzOracleResult.Failed(Id, message, compile0.StandardOutput + compile2.StandardOutput, compile0.StandardError + compile2.StandardError);
        }
        if (run0 is null || run2 is null)
        {
            return FuzzOracleResult.Failed(Id, "Compilation succeeded but both optimization configurations could not be executed.");
        }
        string? run0Failure = NativeOutcomeValidator.Failure("-O0 program", run0);
        string? run2Failure = NativeOutcomeValidator.Failure("-O2 program", run2);
        if (run0Failure is not null || run2Failure is not null)
        {
            return FuzzOracleResult.Failed(
                Id,
                $"Native configuration failure: {run0Failure ?? "-O0 passed"}; {run2Failure ?? "-O2 passed"}.",
                run0.StandardOutput + run2.StandardOutput,
                run0.StandardError + run2.StandardError);
        }
        bool equal = run0.ExitCode == run2.ExitCode && string.Equals(run0.StandardOutput, run2.StandardOutput, StringComparison.Ordinal) && string.Equals(run0.StandardError, run2.StandardError, StringComparison.Ordinal);
        return equal ? FuzzOracleResult.Passed(Id) : FuzzOracleResult.Failed(Id, "-O0 and -O2 produced different observable behavior.", run0.StandardOutput + run2.StandardOutput, run0.StandardError + run2.StandardError);
    }
}

internal sealed class DifferentialReuseOracle : IFuzzOracle
{
    public string Id => "differential-reuse";
    public async ValueTask<FuzzOracleResult> EvaluateAsync(GeneratedFuzzCase testCase, FuzzExecutionContext context, CancellationToken cancellationToken)
    {
        string observableSource = Execution.ObservableValueRenderer.RenderProgram(testCase);
        (Execution.ProcessResult normalCompile, Execution.ProcessResult? normalRun) = await context.Compiler.CompileAndRunAsync(
            observableSource,
            context.RepositoryRoot,
            context.Target,
            "-O2",
            context.CompilerTimeout,
            context.ProgramTimeout,
            context.MaximumOutputBytes,
            cancellationToken).ConfigureAwait(false);
        (Execution.ProcessResult noReuseCompile, Execution.ProcessResult? noReuseRun) = await context.Compiler.CompileAndRunAsync(
            observableSource,
            context.RepositoryRoot,
            context.Target,
            "-O2",
            context.CompilerTimeout,
            context.ProgramTimeout,
            context.MaximumOutputBytes,
            cancellationToken,
            disableReuse: true).ConfigureAwait(false);
        string? normalCompileFailure = NativeOutcomeValidator.Failure("normal compiler", normalCompile);
        string? noReuseCompileFailure = NativeOutcomeValidator.Failure("reuse-disabled compiler", noReuseCompile);
        if (normalCompileFailure is not null || noReuseCompileFailure is not null)
        {
            return FuzzOracleResult.Failed(Id, $"Compiler configuration failure: {normalCompileFailure ?? "normal passed"}; {noReuseCompileFailure ?? "reuse-disabled passed"}.", normalCompile.StandardOutput + noReuseCompile.StandardOutput, normalCompile.StandardError + noReuseCompile.StandardError);
        }
        if (normalRun is null || noReuseRun is null)
        {
            return FuzzOracleResult.Failed(Id, "Compilation succeeded but the reuse configurations could not be executed.");
        }
        string? normalRunFailure = NativeOutcomeValidator.Failure("normal program", normalRun);
        string? noReuseRunFailure = NativeOutcomeValidator.Failure("reuse-disabled program", noReuseRun);
        if (normalRunFailure is not null || noReuseRunFailure is not null)
        {
            return FuzzOracleResult.Failed(
                Id,
                $"Native configuration failure: {normalRunFailure ?? "normal passed"}; {noReuseRunFailure ?? "reuse-disabled passed"}.",
                normalRun.StandardOutput + noReuseRun.StandardOutput,
                normalRun.StandardError + noReuseRun.StandardError);
        }
        bool equal = normalRun.ExitCode == noReuseRun.ExitCode &&
            string.Equals(normalRun.StandardOutput, noReuseRun.StandardOutput, StringComparison.Ordinal) &&
            string.Equals(normalRun.StandardError, noReuseRun.StandardError, StringComparison.Ordinal);
        return equal
            ? FuzzOracleResult.Passed(Id)
            : FuzzOracleResult.Failed(Id, "Normal and reuse-disabled lowering produced different observable behavior.", normalRun.StandardOutput + noReuseRun.StandardOutput, normalRun.StandardError + noReuseRun.StandardError);
    }
}

internal sealed class DifferentialTraitEvidenceOracle : IFuzzOracle
{
    public string Id => "differential-trait-evidence";

    public async ValueTask<FuzzOracleResult> EvaluateAsync(
        GeneratedFuzzCase testCase,
        FuzzExecutionContext context,
        CancellationToken cancellationToken)
    {
        string observableSource = Execution.ObservableValueRenderer.RenderProgram(testCase);
        (Execution.ProcessResult specializedCompile, Execution.ProcessResult? specializedRun) =
            await context.Compiler.CompileAndRunAsync(
                observableSource,
                context.RepositoryRoot,
                context.Target,
                "-O2",
                context.CompilerTimeout,
                context.ProgramTimeout,
                context.MaximumOutputBytes,
                cancellationToken).ConfigureAwait(false);
        (Execution.ProcessResult dictionaryCompile, Execution.ProcessResult? dictionaryRun) =
            await context.Compiler.CompileAndRunAsync(
                observableSource,
                context.RepositoryRoot,
                context.Target,
                "-O2",
                context.CompilerTimeout,
                context.ProgramTimeout,
                context.MaximumOutputBytes,
                cancellationToken,
                disableTraitSpecialization: true).ConfigureAwait(false);
        string? specializedCompileFailure = NativeOutcomeValidator.Failure(
            "trait-specialized compiler",
            specializedCompile);
        string? dictionaryCompileFailure = NativeOutcomeValidator.Failure(
            "dictionary-only compiler",
            dictionaryCompile);
        if (specializedCompileFailure is not null || dictionaryCompileFailure is not null)
        {
            return FuzzOracleResult.Failed(
                Id,
                $"Compiler configuration failure: {specializedCompileFailure ?? "specialized passed"}; {dictionaryCompileFailure ?? "dictionary-only passed"}.",
                specializedCompile.StandardOutput + dictionaryCompile.StandardOutput,
                specializedCompile.StandardError + dictionaryCompile.StandardError);
        }
        if (specializedRun is null || dictionaryRun is null)
        {
            return FuzzOracleResult.Failed(
                Id,
                "Compilation succeeded but both trait evidence configurations could not be executed.");
        }
        string? specializedRunFailure = NativeOutcomeValidator.Failure(
            "trait-specialized program",
            specializedRun);
        string? dictionaryRunFailure = NativeOutcomeValidator.Failure(
            "dictionary-only program",
            dictionaryRun);
        if (specializedRunFailure is not null || dictionaryRunFailure is not null)
        {
            return FuzzOracleResult.Failed(
                Id,
                $"Native configuration failure: {specializedRunFailure ?? "specialized passed"}; {dictionaryRunFailure ?? "dictionary-only passed"}.",
                specializedRun.StandardOutput + dictionaryRun.StandardOutput,
                specializedRun.StandardError + dictionaryRun.StandardError);
        }
        bool equal = specializedRun.ExitCode == dictionaryRun.ExitCode
            && string.Equals(
                specializedRun.StandardOutput,
                dictionaryRun.StandardOutput,
                StringComparison.Ordinal)
            && string.Equals(
                specializedRun.StandardError,
                dictionaryRun.StandardError,
                StringComparison.Ordinal);
        return equal
            ? FuzzOracleResult.Passed(Id)
            : FuzzOracleResult.Failed(
                Id,
                "Trait-specialized and dictionary-only lowering produced different observable behavior.",
                specializedRun.StandardOutput + dictionaryRun.StandardOutput,
                specializedRun.StandardError + dictionaryRun.StandardError);
    }
}

internal sealed class CrossTargetOracle : IFuzzOracle
{
    public string Id => "cross-target";
    public async ValueTask<FuzzOracleResult> EvaluateAsync(GeneratedFuzzCase testCase, FuzzExecutionContext context, CancellationToken cancellationToken)
    {
        string observableSource = Execution.ObservableValueRenderer.RenderProgram(testCase);
        (Execution.ProcessResult compile, Execution.ProcessResult? _) = await context.Compiler.CompileAndRunAsync(
            observableSource,
            context.RepositoryRoot,
            context.Target,
            "-O2",
            context.CompilerTimeout,
            context.ProgramTimeout,
            context.MaximumOutputBytes,
            cancellationToken).ConfigureAwait(false);
        string? failure = NativeOutcomeValidator.Failure($"compiler for '{context.Target}'", compile);
        return failure is not null
            ? FuzzOracleResult.Failed(Id, failure, compile.StandardOutput, compile.StandardError)
            : FuzzOracleResult.Passed(Id);
    }
}

internal static class NativeOutcomeValidator
{
    internal static string? Failure(string stage, Execution.ProcessResult result)
    {
        if (result.TimedOut)
        {
            return $"{stage} timed out";
        }
        if (result.OutputTruncated)
        {
            return $"{stage} exceeded its output limit";
        }
        if (result.ExitCode != 0)
        {
            return $"{stage} exited with {result.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        }
        return null;
    }
}
