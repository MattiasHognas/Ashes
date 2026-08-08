using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Oracles;

internal sealed class ExecutionOracle : IFuzzOracle
{
    public string Id => "execution";
    public async ValueTask<FuzzOracleResult> EvaluateAsync(GeneratedFuzzCase testCase, FuzzExecutionContext context, CancellationToken cancellationToken)
    {
        string observableSource = Execution.ObservableValueRenderer.RenderProgram(testCase);
        (Execution.ProcessResult compile, Execution.ProcessResult? run) = await context.Compiler.CompileAndRunAsync(observableSource, context.RepositoryRoot, context.Target, "-O2", context.CompilerTimeout, context.ProgramTimeout, context.MaximumOutputBytes, cancellationToken).ConfigureAwait(false);
        if (compile.TimedOut || compile.ExitCode != 0)
        {
            return FuzzOracleResult.Failed(Id, compile.TimedOut ? "Compiler timed out." : $"Compiler exited with {compile.ExitCode}.", compile.StandardOutput, compile.StandardError);
        }
        if (run is null)
        {
            return FuzzOracleResult.Failed(Id, "Compilation succeeded but the selected target could not be executed.", compile.StandardOutput, compile.StandardError);
        }
        return run.TimedOut || run.ExitCode != 0 || run.OutputTruncated
            ? FuzzOracleResult.Failed(Id, run.TimedOut ? "Generated program timed out." : $"Generated program exited with {run.ExitCode}.", run.StandardOutput, run.StandardError)
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
        if (compile0.ExitCode != 0 || compile2.ExitCode != 0 || compile0.TimedOut || compile2.TimedOut)
        {
            string message = $"Compiler configuration failure: -O0 {Describe(compile0)}; -O2 {Describe(compile2)}.";
            return FuzzOracleResult.Failed(Id, message, compile0.StandardOutput + compile2.StandardOutput, compile0.StandardError + compile2.StandardError);
        }
        if (run0 is null || run2 is null)
        {
            return FuzzOracleResult.Failed(Id, "Compilation succeeded but both optimization configurations could not be executed.");
        }
        bool equal = run0.ExitCode == run2.ExitCode && string.Equals(run0.StandardOutput, run2.StandardOutput, StringComparison.Ordinal) && string.Equals(run0.StandardError, run2.StandardError, StringComparison.Ordinal);
        return equal ? FuzzOracleResult.Passed(Id) : FuzzOracleResult.Failed(Id, "-O0 and -O2 produced different observable behavior.", run0.StandardOutput + run2.StandardOutput, run0.StandardError + run2.StandardError);
    }

    private static string Describe(Execution.ProcessResult result) => result.TimedOut
        ? "timed out"
        : $"exited with {result.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
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
        if (normalCompile.ExitCode != 0 || noReuseCompile.ExitCode != 0 || normalCompile.TimedOut || noReuseCompile.TimedOut)
        {
            return FuzzOracleResult.Failed(Id, "Normal or reuse-disabled compilation failed.", normalCompile.StandardOutput + noReuseCompile.StandardOutput, normalCompile.StandardError + noReuseCompile.StandardError);
        }
        if (normalRun is null || noReuseRun is null)
        {
            return FuzzOracleResult.Failed(Id, "Compilation succeeded but the reuse configurations could not be executed.");
        }
        bool equal = normalRun.ExitCode == noReuseRun.ExitCode &&
            string.Equals(normalRun.StandardOutput, noReuseRun.StandardOutput, StringComparison.Ordinal) &&
            string.Equals(normalRun.StandardError, noReuseRun.StandardError, StringComparison.Ordinal);
        return equal
            ? FuzzOracleResult.Passed(Id)
            : FuzzOracleResult.Failed(Id, "Normal and reuse-disabled lowering produced different observable behavior.", normalRun.StandardOutput + noReuseRun.StandardOutput, normalRun.StandardError + noReuseRun.StandardError);
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
        return compile.TimedOut || compile.ExitCode != 0
            ? FuzzOracleResult.Failed(Id, compile.TimedOut ? $"Compilation for '{context.Target}' timed out." : $"Compilation for '{context.Target}' exited with {compile.ExitCode}.", compile.StandardOutput, compile.StandardError)
            : FuzzOracleResult.Passed(Id);
    }
}
