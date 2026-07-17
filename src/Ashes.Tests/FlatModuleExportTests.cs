using System.Diagnostics;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;
using TUnit.Core;

namespace Ashes.Tests;

/// <summary>
/// Covers module export computation for the flat top-level declaration form (a sequence of
/// <c>let</c> / <c>let recursive ... and ...</c> / <c>type</c> / <c>external</c> declarations with no
/// <c>let ... in</c> pyramid). Exports must be exactly the top-level let/recgroup/type names;
/// <c>external</c> and the trailing expression are never exported, and importers must compile and run.
/// </summary>
public sealed class FlatModuleExportTests
{
    [Test]
    public void Flat_module_exports_top_level_let_names()
    {
        var dir = WriteModules(
            ("B", "let inc = given (x) -> x + 1\nlet double = given (x) -> x + x\n"),
            ("Main", "import B\nAshes.IO.print(B.inc(6))\n"));

        var source = BuildProjectSource(dir, "Main");

        // Each exported binding is stitched under its module-qualified generated name.
        source.ShouldContain("B_inc = ");
        source.ShouldContain("B_double = ");
    }

    [Test]
    public void Flat_module_drops_trailing_expression_and_excludes_external_from_exports()
    {
        var dir = WriteModules(
            ("B", "external getpid() -> Int = \"getpid\"\nlet inc = given (x) -> x + 1\ninc(41)\n"),
            ("Main", "import B\nAshes.IO.print(B.inc(41))\n"));

        var source = BuildProjectSource(dir, "Main");

        // The exported let binding survives, but the external is not exported as a value and the
        // trailing expression is dropped. The external declaration itself may be hoisted because
        // imported flat bindings can depend on FFI declarations.
        source.ShouldContain("B_inc = ");
        source.ShouldContain("external getpid() -> Int");
        source.ShouldNotContain("B_getpid");
        // A flat module binds no whole-module value, so there is no `let B = (...)` stitch.
        source.ShouldNotContain("let B = ");
    }

    [Test]
    public async Task Flat_module_qualified_use_compiles_and_runs()
    {
        var dir = WriteModules(
            ("B", "let inc = given (x) -> x + 1\nlet double = given (x) -> x + x\n"),
            ("Main", "import B\nAshes.IO.print(B.inc(6))\n"));

        var stdout = await BuildAndRunAsync(dir, "Main").ConfigureAwait(false);

        stdout.TrimEnd().ShouldBe("7");
    }

    [Test]
    public async Task Flat_module_unqualified_use_compiles_and_runs()
    {
        var dir = WriteModules(
            ("B", "let inc = given (x) -> x + 1\nlet double = given (x) -> x + x\n"),
            ("Main", "import B\nAshes.IO.print(double(6))\n"));

        var stdout = await BuildAndRunAsync(dir, "Main").ConfigureAwait(false);

        stdout.TrimEnd().ShouldBe("12");
    }

    [Test]
    public async Task Flat_module_mutual_recursion_group_compiles_and_runs()
    {
        var dir = WriteModules(
            ("B",
                "let recursive isEven = given (n) -> if n == 0 then true else isOdd(n - 1)\n" +
                "and isOdd = given (n) -> if n == 0 then false else isEven(n - 1)\n"),
            ("Main", "import B\nAshes.IO.print(B.isEven(10))\n"));

        var stdout = await BuildAndRunAsync(dir, "Main").ConfigureAwait(false);

        stdout.TrimEnd().ShouldBe("true");
    }

    [Test]
    public async Task Flat_module_type_declaration_is_exported_globally()
    {
        var dir = WriteModules(
            ("B", "type Color =\n    | Red\n    | Green\nlet favorite = Red\n"),
            ("Main", "import B\nAshes.IO.print(match favorite with | Red -> 1 | Green -> 2)\n"));

        var stdout = await BuildAndRunAsync(dir, "Main").ConfigureAwait(false);

        stdout.TrimEnd().ShouldBe("1");
    }

    private static string BuildProjectSource(string dir, string entryModule)
    {
        var plan = ProjectSupport.BuildCompilationPlan(BuildProject(dir, entryModule));
        return ProjectSupport.BuildCompilationSource(plan);
    }

    private static AshesProject BuildProject(string dir, string entryModule)
    {
        return new AshesProject(
            ProjectFilePath: Path.Combine(dir, "ashes.json"),
            ProjectDirectory: dir,
            EntryPath: Path.Combine(dir, entryModule + ".ash"),
            EntryModuleName: entryModule,
            Name: null,
            SourceRoots: [dir],
            Include: [],
            OutDir: Path.Combine(dir, "out"),
            Target: null);
    }

    private static async Task<string> BuildAndRunAsync(string dir, string entryModule)
    {
        var plan = ProjectSupport.BuildCompilationPlan(BuildProject(dir, entryModule));
        var combinedSource = ProjectSupport.BuildCompilationSource(plan);
        return await CompileRunCaptureAsync(combinedSource, plan.ImportedStdModules).ConfigureAwait(false);
    }

    private static string WriteModules(params (string Name, string Source)[] modules)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ashes-flat-export-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (name, src) in modules)
        {
            File.WriteAllText(Path.Combine(dir, name + ".ash"), src);
        }

        return dir;
    }

    private static async Task<string> CompileRunCaptureAsync(string source, IReadOnlySet<string>? importedStdModules)
    {
        var diag = new Diagnostics();
        var program = new Parser(source, diag).ParseProgram();
        diag.ThrowIfAny();

        var ir = new Lowering(diag, importedStdModules).Lower(program);
        diag.ThrowIfAny();

        var tmpDir = Path.Combine(Path.GetTempPath(), "ashes-flat-export-tests");
        Directory.CreateDirectory(tmpDir);

        string exePath;
        if (OperatingSystem.IsWindows())
        {
            var exeBytes = new Ashes.Backend.Backends.WindowsX64LlvmBackend().Compile(ir);
            exePath = Path.Combine(tmpDir, $"flat_{Guid.NewGuid():N}.exe");
            TestProcessHelper.WriteExecutable(exePath, exeBytes);
        }
        else
        {
            var elfBytes = new Ashes.Backend.Backends.LinuxX64LlvmBackend().Compile(ir);
            exePath = Path.Combine(tmpDir, $"flat_{Guid.NewGuid():N}");
            TestProcessHelper.WriteExecutable(exePath, elfBytes);
        }

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

        proc.ExitCode.ShouldBe(0, $"stderr: {stderr}");
        return stdout;
    }
}
