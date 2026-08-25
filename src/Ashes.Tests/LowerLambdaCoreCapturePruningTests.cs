using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

/// <summary>
/// A closure capture the lowered body never reads via <see cref="IrInst.LoadEnv"/> is
/// deleted at lowering time, with the environment's remaining captures renumbered to a compact
/// range and the allocation shrunk to match.
/// </summary>
public sealed class LowerLambdaCoreCapturePruningTests
{
    [Test]
    public void Mutual_recursion_dispatch_closure_drops_its_unused_capture()
    {
        // The mutual-recursion group dispatch helper syntactically closes over both its own
        // recreated dispatch closure and the loop's starting label, but the merged TCO loop body
        // (lambda_3) only ever reads the starting-label capture via LoadEnv — the dispatch-closure
        // capture is never read back, so it should be pruned to a single-capture, 8-byte env.
        var ir = LowerProgram(
            """
            let recursive isEven n =
                if n == 0
                then true
                else isOdd(n - 1)
            and isOdd n =
                if n == 0
                then false
                else isEven(n - 1)

            isEven(4)
            """);

        IrFunction dispatch = ir.Functions.Single(f => string.Equals(f.Label, "__recgroup_dispatch_2", StringComparison.Ordinal));
        IrInst.Alloc alloc = dispatch.Instructions.OfType<IrInst.Alloc>().ShouldHaveSingleItem();
        alloc.SizeBytes.ShouldBe(8, "the dispatch-closure capture is never read by the loop body, so only one capture should survive.");

        IrInst.MakeClosure makeClosure = dispatch.Instructions.OfType<IrInst.MakeClosure>().ShouldHaveSingleItem();
        makeClosure.EnvSizeBytes.ShouldBe(8);

        IrFunction loopBody = ir.Functions.Single(f => string.Equals(f.Label, "lambda_3", StringComparison.Ordinal));
        loopBody.Instructions.OfType<IrInst.LoadEnv>()
            .Select(load => load.Index)
            .ShouldAllBe(index => index == 0, "every surviving LoadEnv must be renumbered into the compact 0..0 range.");
    }

    [Test]
    public async Task Self_referential_lambda_with_an_outer_capture_still_runs_correctly()
    {
        // A named recursive lambda reconstructs its own closure from inside its body via
        // Binding.Self, reusing the *same* environment at a size recorded before pruning could
        // run — pruning must decline for a self-referential lambda so that reconstruction never
        // desynchronizes from the environment's actual size. There is no surface-syntax way to
        // force a *dead* capture here directly (any textual reference emits a real LoadEnv, so
        // it is never a pruning candidate to begin with), so this is a correctness guard: a
        // self-referential closure that legitimately captures and uses an outer value must still
        // produce the right answer after this change.
        string stdout = await CompileAndRunAsync(
            """
            let recursive outer step =
                let recursive selfRef n =
                    if n == 0
                    then 0
                    else step + selfRef(n - 1)
                in
                    Ashes.IO.print(Ashes.Text.fromInt(selfRef(3)))

            outer(10)
            """);

        stdout.Trim().ShouldBe("30");
    }

    private static IrProgram LowerProgram(string source)
    {
        Diagnostics diagnostics = new();
        Program program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        IrProgram ir = new Lowering(diagnostics).Lower(program);
        diagnostics.ThrowIfAny();
        return ir;
    }

    private static async Task<string> CompileAndRunAsync(string source)
    {
        var ir = IrOptimizer.Optimize(LowerProgram(source));
        var elfBytes = new Ashes.Backend.Backends.LinuxX64LlvmBackend().Compile(ir);

        var tmpDir = Path.Combine(Path.GetTempPath(), "ashes-tests");
        Directory.CreateDirectory(tmpDir);

        var exePath = Path.Combine(tmpDir, $"capprune_{Guid.NewGuid():N}");
        TestProcessHelper.WriteExecutable(exePath, elfBytes);

        var psi = new System.Diagnostics.ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var proc = await TestProcessHelper.StartProcessAsync(psi).ConfigureAwait(false);
        string stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await proc.WaitForExitAsync().ConfigureAwait(false);
        return stdout;
    }
}
