using System.Diagnostics;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

/// <summary>
/// Covers binding/type selector imports (<c>import M.name [as x]</c>, <c>import M.Type [as T]</c>),
/// validated end-to-end against the same combined-source pipeline the CLI/TestRunner use: built-in
/// and user modules resolve through one path, two selectors colliding on a name raise ASH016, and a
/// module's selector imports never leak into its own consumers.
/// </summary>
public sealed class ImportSelectorTests
{
    private static AshesProject WriteProject(string mainSource, IReadOnlyDictionary<string, string>? modules = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ashes-import-selectors", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (name, src) in modules ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            var path = Path.Combine(dir, name.Replace('.', Path.DirectorySeparatorChar) + ".ash");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, src);
        }

        File.WriteAllText(Path.Combine(dir, "Main.ash"), mainSource);
        return new AshesProject(
            ProjectFilePath: Path.Combine(dir, "ashes.json"),
            ProjectDirectory: dir,
            EntryPath: Path.Combine(dir, "Main.ash"),
            EntryModuleName: "Main",
            Name: null,
            SourceRoots: [dir],
            Include: [],
            OutDir: Path.Combine(dir, "out"),
            Target: null);
    }

    private static async Task<string> RunAsync(string mainSource, IReadOnlyDictionary<string, string>? modules = null)
    {
        var project = WriteProject(mainSource, modules);
        var plan = ProjectSupport.BuildCompilationPlan(project);
        var source = ProjectSupport.BuildCompilationSource(plan);
        return await CompileRunCaptureAsync(source, plan.ImportedStdModules, plan.MergedAliases).ConfigureAwait(false);
    }

    [Test]
    public async Task BuiltinBindingSelector_resolves_identically_to_qualified()
    {
        var viaSelector = await RunAsync("import Ashes.IO.print\nprint(\"hi\")\n").ConfigureAwait(false);
        var viaQualified = await RunAsync("Ashes.IO.print(\"hi\")\n").ConfigureAwait(false);
        viaSelector.TrimEnd().ShouldBe("hi");
        viaSelector.ShouldBe(viaQualified);
    }

    [Test]
    public async Task BuiltinBindingSelector_with_alias_resolves()
    {
        var stdout = await RunAsync("import Ashes.IO.print as p\np(\"hi\")\n").ConfigureAwait(false);
        stdout.TrimEnd().ShouldBe("hi");
    }

    [Test]
    public async Task UserBindingSelector_resolves_unqualified()
    {
        var stdout = await RunAsync(
            "import Util.double\nAshes.IO.print(double(21))\n",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Util"] = "let double = given (x) -> x + x\n" }).ConfigureAwait(false);
        stdout.TrimEnd().ShouldBe("42");
    }

    [Test]
    public async Task UserBindingSelector_with_alias_resolves_unqualified()
    {
        var stdout = await RunAsync(
            "import Util.double as d\nAshes.IO.print(d(21))\n",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Util"] = "let double = given (x) -> x + x\n" }).ConfigureAwait(false);
        stdout.TrimEnd().ShouldBe("42");
    }

    private static readonly Dictionary<string, string> ShapesModule = new(StringComparer.Ordinal)
    {
        ["Shapes"] =
            "type Color = | Red | Green\n" +
            "let name = given (c) -> match c with | Red -> \"red\" | Green -> \"green\"\n",
    };

    [Test]
    public async Task TypeSelectorWithAlias_binds_type_under_alias()
    {
        // `import Shapes.Color as C` must make `C` usable as the type in an annotation. The combined
        // source hoists `type Color` globally under its real name, so the alias `C` is realized by
        // rewriting it back to `Color`; without that the annotation references an unknown type.
        var stdout = await RunAsync(
            "import Shapes.Color as C\nimport Shapes.name\n" +
            "let c : C = Green in Ashes.IO.print(name(c))\n",
            ShapesModule).ConfigureAwait(false);
        stdout.TrimEnd().ShouldBe("green");
    }

    [Test]
    public async Task TypeSelectorAlias_is_required_for_the_alias_to_resolve()
    {
        // The same program without the `as C` selector must fail: `C` is otherwise an unknown type.
        // This isolates the type-selector behavior so the positive test above is not tautological
        // (global type hoisting alone never introduces the alias name `C`).
        var ex = await Should.ThrowAsync<Exception>(() => RunAsync(
            "import Shapes.name\nlet c : C = Green in Ashes.IO.print(name(c))\n",
            ShapesModule)).ConfigureAwait(false);
        ex.Message.ShouldContain("Unknown type name 'C'");
    }

    [Test]
    public async Task BareTypeSelector_brings_type_into_scope()
    {
        var stdout = await RunAsync(
            "import Shapes.Color\nimport Shapes.name\n" +
            "let red : Color = Red in Ashes.IO.print(name(red))\n",
            ShapesModule).ConfigureAwait(false);
        stdout.TrimEnd().ShouldBe("red");
    }

    [Test]
    public void UnknownTypeExport_is_a_compile_error()
    {
        // The bare `import M.Type` path is reclassified from a folded module path into a type selector
        // and validated against M's real export set — a non-existent type is a clear compile error,
        // proving the bare type-selector path is wired (not silently dropped).
        var ex = Should.Throw<InvalidOperationException>(() =>
        {
            var plan = ProjectSupport.BuildCompilationPlan(WriteProject(
                "import Shapes.Missing\nAshes.IO.print(0)\n",
                ShapesModule));
            ProjectSupport.BuildCompilationSource(plan);
        });
        ex.Message.ShouldContain("does not export 'Missing'");
    }

    [Test]
    public void ConflictingUnqualifiedSelectors_emit_ASH016()
    {
        var ex = Should.Throw<InvalidOperationException>(() => ProjectSupport.BuildCompilationPlan(WriteProject(
            "import A.x\nimport B.x\nAshes.IO.print(0)\n",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["A"] = "let x = 1\n",
                ["B"] = "let x = 2\n",
            })));
        ex.Message.ShouldBe("Conflicting unqualified import selectors for 'x'.");
    }

    [Test]
    public async Task SameExportSelectedTwice_is_allowed()
    {
        var stdout = await RunAsync(
            "import Util.double\nimport Util.double\nAshes.IO.print(double(21))\n",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Util"] = "let double = given (x) -> x + x\n" }).ConfigureAwait(false);
        stdout.TrimEnd().ShouldBe("42");
    }

    [Test]
    public async Task SelectorWithDifferentAliases_avoids_conflict()
    {
        var stdout = await RunAsync(
            "import A.x as a\nimport B.x as b\nAshes.IO.print(a + b)\n",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["A"] = "let x = 40\n",
                ["B"] = "let x = 2\n",
            }).ConfigureAwait(false);
        stdout.TrimEnd().ShouldBe("42");
    }

    [Test]
    public void UnknownExport_is_a_compile_error()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
        {
            var plan = ProjectSupport.BuildCompilationPlan(WriteProject(
                "import Util.missing\nAshes.IO.print(0)\n",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["Util"] = "let double = given (x) -> x + x\n" }));
            ProjectSupport.BuildCompilationSource(plan);
        });
        ex.Message.ShouldContain("does not export 'missing'");
    }

    [Test]
    public async Task SelectorImports_are_not_re_exported_to_consumers()
    {
        // A selector-imports B.secret and uses it; Main selector-imports A.helper. Main must see
        // helper but never B's `secret`, which A pulled in only for its own use.
        var modules = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["B"] = "let secret = 40\n",
            ["A"] = "import B.secret\nlet helper = given (x) -> secret + x\n",
        };

        var stdout = await RunAsync("import A.helper\nAshes.IO.print(helper(2))\n", modules).ConfigureAwait(false);
        stdout.TrimEnd().ShouldBe("42");

        await Should.ThrowAsync<Exception>(() =>
            RunAsync("import A.helper\nAshes.IO.print(secret)\n", modules)).ConfigureAwait(false);
    }

    [Test]
    public async Task StandaloneBuiltinSelector_takes_effect_in_entry_expression()
    {
        // Single-file (non-project) mode resolves built-in selectors through the same selector-rename
        // machinery, applied to the entry expression. `import Ashes.IO.print as p` must rewrite `p` to
        // the qualified intrinsic call in the standalone-stitched source, just like in project mode.
        var parsed = ProjectSupport.ParseImportHeader("import Ashes.IO.print as p\np(\"hi\")\n", "<mem>");
        var layout = ProjectSupport.BuildStandaloneCompilationLayout(
            parsed.SourceWithoutImports, parsed.ImportNames, "<mem>", parsed.ImportSelectors);
        layout.Source.ShouldContain("Ashes.IO.print");
        layout.Source.ShouldNotContain("p(");

        var stdout = await CompileRunCaptureAsync(layout.Source, parsed.ImportNames.ToHashSet(StringComparer.Ordinal), null).ConfigureAwait(false);
        stdout.TrimEnd().ShouldBe("hi");
    }

    [Test]
    public void CircularImports_are_still_detected()
    {
        var ex = Should.Throw<InvalidOperationException>(() => ProjectSupport.BuildCompilationPlan(WriteProject(
            "import A.x\nAshes.IO.print(x)\n",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["A"] = "import B.y\nlet x = y\n",
                ["B"] = "import A.x\nlet y = x\n",
            })));
        ex.Message.ShouldContain("Import cycle detected");
    }

    private static async Task<string> CompileRunCaptureAsync(
        string source,
        IReadOnlySet<string>? importedStdModules,
        IReadOnlyDictionary<string, string>? moduleAliases)
    {
        var diag = new Diagnostics();
        var program = new Parser(source, diag).ParseProgram();
        diag.ThrowIfAny();

        var ir = new Lowering(diag, importedStdModules, moduleAliases).Lower(program);
        diag.ThrowIfAny();

        var tmpDir = Path.Combine(Path.GetTempPath(), "ashes-import-selector-tests");
        Directory.CreateDirectory(tmpDir);

        string exePath;
        if (OperatingSystem.IsWindows())
        {
            var exeBytes = new Ashes.Backend.Backends.WindowsX64LlvmBackend().Compile(ir);
            exePath = Path.Combine(tmpDir, $"sel_{Guid.NewGuid():N}.exe");
            TestProcessHelper.WriteExecutable(exePath, exeBytes);
        }
        else
        {
            var elfBytes = new Ashes.Backend.Backends.LinuxX64LlvmBackend().Compile(ir);
            exePath = Path.Combine(tmpDir, $"sel_{Guid.NewGuid():N}");
            TestProcessHelper.WriteExecutable(exePath, elfBytes);
        }

        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var proc = await TestProcessHelper.StartProcessAsync(psi).ConfigureAwait(false);
        var stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await proc.WaitForExitAsync().ConfigureAwait(false);

        proc.ExitCode.ShouldBe(0, $"stderr: {stderr}");
        return stdout;
    }
}
