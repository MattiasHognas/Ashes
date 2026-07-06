using System.Diagnostics;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

/// <summary>
/// Exercises Model-A sequential scoping over <see cref="Program.Items"/> plus the optional trailing
/// body: a binding is visible to later declarations and the trailing expression, never to earlier
/// ones. Covers the documented forward-reference (ASH014) and duplicate-binding (ASH013)
/// diagnostics, decl-only programs, and that the legacy single-expression / nested let..in forms
/// keep lowering unchanged.
/// </summary>
public sealed class TopLevelScopingTests
{
    // ASH013/ASH014: the shared DiagnosticCodes table is in
    // Ashes.Frontend and does not surface these yet, so reference the codes by literal here.
    private const string DuplicateTopLevelBinding = "ASH013";
    private const string ForwardReference = "ASH014";

    [Test]
    public async Task Sequential_bindings_are_visible_to_later_declarations_and_body()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // `a` and `b` are flat top-level declarations (no `in`); the final `let c = ... in expr`
        // supplies the trailing body. Under Model-A scoping `a` is visible to `b` and both to `c`.
        var src = """
            let a = 1
            let b = a + 1
            let c = b + a in Ashes.IO.print(c)
            """;

        (await CompileRunCaptureProgramAsync(src).ConfigureAwait(false)).ShouldBe("3\n");
    }

    [Test]
    public void Forward_reference_to_a_later_binding_emits_ash014_and_fails()
    {
        // `c` refers to `d`, which is declared later. Under Model-A sequential scoping `d` is not yet
        // in scope, so this must fail with the forward-reference diagnostic rather than resolving.
        var src = """
            let c = d + 1
            let d = 2
            """;

        var diag = LowerProgram(src);

        diag.StructuredErrors.ShouldNotBeEmpty();
        diag.StructuredErrors.ShouldContain(x => x.Code == ForwardReference);
    }

    [Test]
    public void Duplicate_top_level_binding_emits_ash013()
    {
        var src = """
            let a = 1
            let a = 2
            """;

        var diag = LowerProgram(src);

        diag.StructuredErrors.ShouldNotBeEmpty();
        diag.StructuredErrors.ShouldContain(x => x.Code == DuplicateTopLevelBinding);
    }

    [Test]
    public void Top_level_mutual_recursion_type_checks_as_one_binding_group()
    {
        // `let recursive ... and ...` is now implemented (semantics-rec-and-groups): the group's members
        // see one another, so this type-checks and lowers cleanly with no diagnostic. End-to-end
        // behaviour (running the compiled program) is covered by MutualRecursionTests.
        var src = """
            let recursive isEven = given (n) -> if n == 0 then true else isOdd(n - 1)
            and isOdd = given (n) -> if n == 0 then false else isEven(n - 1)
            """;

        var diag = LowerProgram(src);

        diag.StructuredErrors.ShouldBeEmpty();
    }

    [Test]
    public async Task Declarations_only_with_no_trailing_expression_compiles_and_runs_silently()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var src = """
            let a = 1
            let b = 2
            """;

        var (stdout, exitCode) = await CompileRunCaptureProgramWithExitAsync(src).ConfigureAwait(false);
        stdout.ShouldBe("");
        exitCode.ShouldBe(0);
    }

    [Test]
    public async Task Plain_single_expression_file_still_compiles_unchanged()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        (await CompileRunCaptureProgramAsync("Ashes.IO.print(42)").ConfigureAwait(false)).ShouldBe("42\n");
    }

    [Test]
    public async Task Nested_let_in_pyramid_still_compiles_unchanged()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var src = """
            let x = 1
            in let y = 2
            in Ashes.IO.print(x + y)
            """;

        (await CompileRunCaptureProgramAsync(src).ConfigureAwait(false)).ShouldBe("3\n");
    }

    private static Diagnostics LowerProgram(string source)
    {
        var diag = new Diagnostics();
        var program = new Parser(source, diag).ParseProgram();
        _ = new Lowering(diag).Lower(program);
        return diag;
    }

    private static async Task<string> CompileRunCaptureProgramAsync(string source)
    {
        var (stdout, _) = await CompileRunCaptureProgramWithExitAsync(source).ConfigureAwait(false);
        return stdout;
    }

    private static async Task<(string Stdout, int ExitCode)> CompileRunCaptureProgramWithExitAsync(string source)
    {
        var diag = new Diagnostics();
        var program = new Parser(source, diag).ParseProgram();
        diag.ThrowIfAny();

        var ir = new Lowering(diag).Lower(program);
        diag.ThrowIfAny();

        return await RunElfAsync(ir).ConfigureAwait(false);
    }

    private static async Task<(string Stdout, int ExitCode)> RunElfAsync(IrProgram ir)
    {
        var elfBytes = new Ashes.Backend.Backends.LinuxX64LlvmBackend().Compile(ir);

        var tmpDir = Path.Combine(Path.GetTempPath(), "ashes-tests");
        Directory.CreateDirectory(tmpDir);

        var exePath = Path.Combine(tmpDir, $"tls_{Guid.NewGuid():N}");
        TestProcessHelper.WriteExecutable(exePath, elfBytes);

        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var proc = await TestProcessHelper.StartProcessAsync(psi).ConfigureAwait(false);
        var stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await proc.WaitForExitAsync().ConfigureAwait(false);

        return (stdout, proc.ExitCode);
    }
}
