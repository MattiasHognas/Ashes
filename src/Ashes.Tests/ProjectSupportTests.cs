using Ashes.Semantics;
using Ashes.Frontend;
using Shouldly;
using System.Diagnostics;
using TUnit.Core;

namespace Ashes.Tests;

[NotInParallel]
public sealed class ProjectSupportTests
{
    [Test]
    public void ParseImportHeader_should_strip_header_imports_and_preserve_body()
    {
        var parsed = ProjectSupport.ParseImportHeader("import Ashes.IO\n\ntype Bool = | True | False\nprint(1)", "<memory>");

        parsed.ImportNames.ShouldBe(["Ashes.IO"]);
        parsed.SourceWithoutImports.ShouldBe("type Bool = | True | False\nprint(1)");
    }

    [Test]
    public void ParseImportHeader_should_reject_invalid_import_syntax()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            ProjectSupport.ParseImportHeader("import ashes.io\nprint(1)", "sample.ash"));

        ex.Message.ShouldContain("Invalid import syntax in sample.ash:1");
    }

    [Test]
    public void LoadProject_should_apply_defaults_and_ignore_unknown_fields()
    {
        var root = CreateTempDirectory();
        try
        {
            var entry = Path.Combine(root, "src", "Main.ash");
            Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
            File.WriteAllText(entry, "Ashes.IO.print(1)");
            File.WriteAllText(
                Path.Combine(root, "ashes.json"),
                """
                {
                  "entry": "src/Main.ash",
                  "unknown": 123
                }
                """);

            var project = ProjectSupport.LoadProject(Path.Combine(root, "ashes.json"));

            project.EntryPath.ShouldBe(Path.GetFullPath(entry));
            project.SourceRoots.ShouldBe([Path.GetFullPath(root)]);
            project.Include.ShouldBeEmpty();
            project.OutDir.ShouldBe(Path.Combine(Path.GetFullPath(root), "out"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void BuildCompilationPlan_should_order_dependencies_before_dependents()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "ashes.json"), """{"entry":"src/Main.ash","sourceRoots":["src"]}""");
            Directory.CreateDirectory(Path.Combine(root, "src", "Foo"));
            File.WriteAllText(Path.Combine(root, "src", "Main.ash"), "import Foo.Bar\nimport Foo\nAshes.IO.print(1)");
            File.WriteAllText(Path.Combine(root, "src", "Foo.ash"), "Ashes.IO.print(2)");
            File.WriteAllText(Path.Combine(root, "src", "Foo", "Bar.ash"), "import Foo\nAshes.IO.print(3)");

            var project = ProjectSupport.LoadProject(Path.Combine(root, "ashes.json"));
            var plan = ProjectSupport.BuildCompilationPlan(project);

            plan.OrderedModules.Select(x => x.ModuleName).ShouldBe(["Foo", "Foo.Bar", "Main"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void BuildCompilationPlan_should_report_attempted_paths_for_missing_module()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(root, "ashes.json"),
                """{"entry":"src/Main.ash","sourceRoots":["src"],"include":["lib"]}""");
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "Main.ash"), "import Missing\nAshes.IO.print(1)");

            var project = ProjectSupport.LoadProject(Path.Combine(root, "ashes.json"));
            var ex = Should.Throw<InvalidOperationException>(() => ProjectSupport.BuildCompilationPlan(project));
            ex.Message.ShouldContain("Could not resolve module 'Missing'");
            ex.Message.ShouldContain("Attempted project modules");
            ex.Message.ShouldContain(Path.Combine(root, "src", "Missing.ash"));
            ex.Message.ShouldContain(Path.Combine(root, "lib", "Missing.ash"));
            ex.Message.ShouldContain(Path.Combine("lib", "Missing.ash"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void BuildCompilationPlan_should_fail_when_multiple_project_roots_define_same_module()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(root, "ashes.json"),
                """{"entry":"src/Main.ash","sourceRoots":["src"],"include":["vendor"]}""");
            Directory.CreateDirectory(Path.Combine(root, "src"));
            Directory.CreateDirectory(Path.Combine(root, "vendor"));
            File.WriteAllText(Path.Combine(root, "src", "Main.ash"), "import Ashes.List\nAshes.IO.print(1)");
            File.WriteAllText(Path.Combine(root, "src", "List.ash"), "1");
            File.WriteAllText(Path.Combine(root, "vendor", "List.ash"), "2");

            var project = ProjectSupport.LoadProject(Path.Combine(root, "ashes.json"));
            var plan = ProjectSupport.BuildCompilationPlan(project);
            plan.OrderedModules.Select(x => x.ModuleName).ShouldContain("Ashes.List");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task BuildCompilationSource_should_resolve_shipped_library_modules_after_project_roots()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "ashes.json"), """{"entry":"Main.ash","sourceRoots":["."]}""");
            File.WriteAllText(Path.Combine(root, "Main.ash"), "import Ashes.List\nAshes.IO.print(Ashes.List.length([1, 2, 3, 4]))");

            var plan = ProjectSupport.BuildCompilationPlan(ProjectSupport.LoadProject(Path.Combine(root, "ashes.json")));
            plan.OrderedModules.Select(x => x.ModuleName).ShouldContain("Ashes.List");
            plan.OrderedModules.Single(x => x.ModuleName == "Ashes.List").FilePath.ShouldBe("<std:Ashes.List>");

            var combinedSource = ProjectSupport.BuildCompilationSource(plan);

            (await CompileRunCaptureAsync(combinedSource, plan.ImportedStdModules)).ShouldBe("4\n");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void BuildCompilationPlan_should_detect_import_cycles()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "ashes.json"), """{"entry":"src/Main.ash","sourceRoots":["src"]}""");
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "Main.ash"), "import A\nAshes.IO.print(0)");
            File.WriteAllText(Path.Combine(root, "src", "A.ash"), "import B\nAshes.IO.print(1)");
            File.WriteAllText(Path.Combine(root, "src", "B.ash"), "import A\nAshes.IO.print(2)");

            var project = ProjectSupport.LoadProject(Path.Combine(root, "ashes.json"));
            var ex = Should.Throw<InvalidOperationException>(() => ProjectSupport.BuildCompilationPlan(project));
            ex.Message.ShouldContain("Import cycle detected");
            ex.Message.ShouldContain("A -> B -> A");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void DiscoverProjectFile_should_walk_up_parent_directories()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "ashes.json"), """{"entry":"Main.ash"}""");
            File.WriteAllText(Path.Combine(root, "Main.ash"), "Ashes.IO.print(1)");
            var nested = Path.Combine(root, "a", "b", "c");
            Directory.CreateDirectory(nested);

            var discovered = ProjectSupport.DiscoverProjectFile(nested);

            discovered.ShouldBe(Path.Combine(root, "ashes.json"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task BuildCompilationSource_should_include_imported_modules_in_execution()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "ashes.json"), """{"entry":"src/Main.ash","sourceRoots":["src"]}""");
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "Main.ash"), "import Lib\nAshes.IO.print(2)");
            File.WriteAllText(Path.Combine(root, "src", "Lib.ash"), "Ashes.IO.print(1)");

            var plan = ProjectSupport.BuildCompilationPlan(ProjectSupport.LoadProject(Path.Combine(root, "ashes.json")));
            var combinedSource = ProjectSupport.BuildCompilationSource(plan);

            (await CompileRunCaptureAsync(combinedSource)).ShouldBe("1\n2\n");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task BuildCompilationSource_should_allow_using_imported_module_values()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "ashes.json"), """{"entry":"src/Main.ash","sourceRoots":["src"]}""");
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "Main.ash"), "import AddOne\nimport Meaning\nAshes.IO.print(AddOne(Meaning))");
            File.WriteAllText(Path.Combine(root, "src", "AddOne.ash"), "let add_one = fun (x) -> x + 1 in add_one");
            File.WriteAllText(Path.Combine(root, "src", "Meaning.ash"), "41");

            var plan = ProjectSupport.BuildCompilationPlan(ProjectSupport.LoadProject(Path.Combine(root, "ashes.json")));
            var combinedSource = ProjectSupport.BuildCompilationSource(plan);

            (await CompileRunCaptureAsync(combinedSource)).ShouldBe("42\n");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void BuildCompilationSource_should_fail_on_generated_module_binding_name_collision()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "ashes.json"), """{"entry":"src/Main.ash","sourceRoots":["src"]}""");
            Directory.CreateDirectory(Path.Combine(root, "src"));
            Directory.CreateDirectory(Path.Combine(root, "src", "Foo"));
            File.WriteAllText(Path.Combine(root, "src", "Main.ash"), "import Foo.Bar\nimport Foo_Bar\nAshes.IO.print(0)");
            File.WriteAllText(Path.Combine(root, "src", "Foo", "Bar.ash"), "1");
            File.WriteAllText(Path.Combine(root, "src", "Foo_Bar.ash"), "2");

            var plan = ProjectSupport.BuildCompilationPlan(ProjectSupport.LoadProject(Path.Combine(root, "ashes.json")));
            var ex = Should.Throw<InvalidOperationException>(() => ProjectSupport.BuildCompilationSource(plan));
            ex.Message.ShouldContain("Module name collision for generated binding 'Foo_Bar'");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task BuildCompilationSource_should_allow_unqualified_imported_export_names()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "ashes.json"), """{"entry":"Main.ash","sourceRoots":["."]}""");
            File.WriteAllText(Path.Combine(root, "Main.ash"), "import Foo\nAshes.IO.print(answer)");
            File.WriteAllText(Path.Combine(root, "Foo.ash"), "let answer = 42 in answer");

            var plan = ProjectSupport.BuildCompilationPlan(ProjectSupport.LoadProject(Path.Combine(root, "ashes.json")));
            var combinedSource = ProjectSupport.BuildCompilationSource(plan);

            (await CompileRunCaptureAsync(combinedSource, plan.ImportedStdModules)).ShouldBe("42\n");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task BuildCompilationSource_should_allow_qualified_export_access_for_imported_modules()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "ashes.json"), """{"entry":"Main.ash","sourceRoots":["."]}""");
            File.WriteAllText(Path.Combine(root, "Main.ash"), "import A\nimport B\nAshes.IO.print(A.x + B.x)");
            File.WriteAllText(Path.Combine(root, "A.ash"), "let x = 1 in x");
            File.WriteAllText(Path.Combine(root, "B.ash"), "let x = 2 in x");

            var plan = ProjectSupport.BuildCompilationPlan(ProjectSupport.LoadProject(Path.Combine(root, "ashes.json")));
            var combinedSource = ProjectSupport.BuildCompilationSource(plan);

            (await CompileRunCaptureAsync(combinedSource, plan.ImportedStdModules)).ShouldBe("3\n");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task BuildCompilationSource_should_allow_qualified_export_access_for_multisegment_modules()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "ashes.json"), """{"entry":"Main.ash","sourceRoots":["."]}""");
            Directory.CreateDirectory(Path.Combine(root, "Foo"));
            File.WriteAllText(Path.Combine(root, "Main.ash"), "import Foo.Bar\nAshes.IO.print(Foo.Bar.x)");
            File.WriteAllText(Path.Combine(root, "Foo", "Bar.ash"), "let x = 7 in x");

            var plan = ProjectSupport.BuildCompilationPlan(ProjectSupport.LoadProject(Path.Combine(root, "ashes.json")));
            var combinedSource = ProjectSupport.BuildCompilationSource(plan);

            (await CompileRunCaptureAsync(combinedSource, plan.ImportedStdModules)).ShouldBe("7\n");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task BuildCompilationSource_should_allow_leaf_qualified_export_access_for_multisegment_modules()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "ashes.json"), """{"entry":"Main.ash","sourceRoots":["."]}""");
            Directory.CreateDirectory(Path.Combine(root, "M"));
            File.WriteAllText(Path.Combine(root, "Main.ash"), "import M.X\nimport M.Y\nAshes.IO.print(X.z + Y.z)");
            File.WriteAllText(Path.Combine(root, "M", "X.ash"), "let z = 1 in z");
            File.WriteAllText(Path.Combine(root, "M", "Y.ash"), "let z = 2 in z");

            var plan = ProjectSupport.BuildCompilationPlan(ProjectSupport.LoadProject(Path.Combine(root, "ashes.json")));
            var combinedSource = ProjectSupport.BuildCompilationSource(plan);

            (await CompileRunCaptureAsync(combinedSource, plan.ImportedStdModules)).ShouldBe("3\n");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void BuildCompilationSource_should_fail_on_imported_export_name_collision_for_multisegment_modules()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "ashes.json"), """{"entry":"Main.ash","sourceRoots":["."]}""");
            Directory.CreateDirectory(Path.Combine(root, "M"));
            File.WriteAllText(Path.Combine(root, "Main.ash"), "import M.X\nimport M.Y\nAshes.IO.print(z)");
            File.WriteAllText(Path.Combine(root, "M", "X.ash"), "let z = 1 in z");
            File.WriteAllText(Path.Combine(root, "M", "Y.ash"), "let z = 2 in z");

            var plan = ProjectSupport.BuildCompilationPlan(ProjectSupport.LoadProject(Path.Combine(root, "ashes.json")));
            var ex = Should.Throw<InvalidOperationException>(() => ProjectSupport.BuildCompilationSource(plan));
            ex.Message.ShouldContain("Import name collision for imported binding 'z'");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void BuildCompilationSource_should_fail_on_ambiguous_leaf_module_qualifier()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "ashes.json"), """{"entry":"Main.ash","sourceRoots":["."]}""");
            Directory.CreateDirectory(Path.Combine(root, "A"));
            Directory.CreateDirectory(Path.Combine(root, "B"));
            File.WriteAllText(Path.Combine(root, "Main.ash"), "import A.X\nimport B.X\nAshes.IO.print(X.z)");
            File.WriteAllText(Path.Combine(root, "A", "X.ash"), "let z = 1 in z");
            File.WriteAllText(Path.Combine(root, "B", "X.ash"), "let z = 2 in z");

            var plan = ProjectSupport.BuildCompilationPlan(ProjectSupport.LoadProject(Path.Combine(root, "ashes.json")));
            var ex = Should.Throw<InvalidOperationException>(() => ProjectSupport.BuildCompilationSource(plan));
            ex.Message.ShouldContain("Import module qualifier collision for 'X'");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task BuildCompilationSource_should_allow_full_qualification_when_leaf_module_qualifier_is_ambiguous()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "ashes.json"), """{"entry":"Main.ash","sourceRoots":["."]}""");
            Directory.CreateDirectory(Path.Combine(root, "A"));
            Directory.CreateDirectory(Path.Combine(root, "B"));
            File.WriteAllText(Path.Combine(root, "Main.ash"), "import A.X\nimport B.X\nAshes.IO.print(A.X.z + B.X.z)");
            File.WriteAllText(Path.Combine(root, "A", "X.ash"), "let z = 1 in z");
            File.WriteAllText(Path.Combine(root, "B", "X.ash"), "let z = 2 in z");

            var plan = ProjectSupport.BuildCompilationPlan(ProjectSupport.LoadProject(Path.Combine(root, "ashes.json")));
            var combinedSource = ProjectSupport.BuildCompilationSource(plan);

            (await CompileRunCaptureAsync(combinedSource, plan.ImportedStdModules)).ShouldBe("3\n");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task BuildCompilationSource_should_allow_multiple_exports_from_imported_modules()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "ashes.json"), """{"entry":"Main.ash","sourceRoots":["."]}""");
            File.WriteAllText(Path.Combine(root, "Main.ash"), "import Math\nAshes.IO.print(inc(add(1)(2)))");
            File.WriteAllText(
                Path.Combine(root, "Math.ash"),
                "let add = fun (x) -> fun (y) -> x + y in let inc = fun (x) -> x + 1 in inc");

            var plan = ProjectSupport.BuildCompilationPlan(ProjectSupport.LoadProject(Path.Combine(root, "ashes.json")));
            var combinedSource = ProjectSupport.BuildCompilationSource(plan);

            (await CompileRunCaptureAsync(combinedSource, plan.ImportedStdModules)).ShouldBe("4\n");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task BuildCompilationSource_should_hoist_imported_type_declarations()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "ashes.json"), """{"entry":"Main.ash","sourceRoots":["."]}""");
            File.WriteAllText(Path.Combine(root, "Main.ash"), "import Wraps\nlet value = wrap(41) in match value with | Wrap(n) -> Ashes.IO.print(n)");
            File.WriteAllText(
                Path.Combine(root, "Wraps.ash"),
                "type Wrapper(A) = | Wrap(A)\nlet wrap = Wrap in wrap");

            var plan = ProjectSupport.BuildCompilationPlan(ProjectSupport.LoadProject(Path.Combine(root, "ashes.json")));
            var combinedSource = ProjectSupport.BuildCompilationSource(plan);

            (await CompileRunCaptureAsync(combinedSource, plan.ImportedStdModules)).ShouldBe("41\n");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task BuildCompilationSource_should_resolve_source_backed_standard_library_modules()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "ashes.json"), """{"entry":"Main.ash","sourceRoots":["."]}""");
            File.WriteAllText(
                Path.Combine(root, "Main.ash"),
                "import Ashes.Result\nmatch Ashes.Result.map((fun (x) -> x + 1))(Ok(41)) with | Ok(value) -> Ashes.IO.print(value) | Error(_) -> Ashes.IO.print(0)");

            var plan = ProjectSupport.BuildCompilationPlan(ProjectSupport.LoadProject(Path.Combine(root, "ashes.json")));
            plan.OrderedModules.Select(x => x.ModuleName).ShouldContain("Ashes.Result");

            var combinedSource = ProjectSupport.BuildCompilationSource(plan);

            (await CompileRunCaptureAsync(combinedSource, plan.ImportedStdModules)).ShouldBe("42\n");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task BuildCompilationSource_should_resolve_ashes_test_in_project_mode()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "ashes.json"), """{"entry":"Main.ash","sourceRoots":["."]}""");
            File.WriteAllText(
                Path.Combine(root, "Main.ash"),
                "import Ashes.Test\nlet checked = assertEqual(1, 1)\nin Ashes.IO.print(\"ok\")");

            var plan = ProjectSupport.BuildCompilationPlan(ProjectSupport.LoadProject(Path.Combine(root, "ashes.json")));
            plan.OrderedModules.Select(x => x.ModuleName).ShouldContain("Ashes.Test");

            var combinedSource = ProjectSupport.BuildCompilationSource(plan);

            (await CompileRunCaptureAsync(combinedSource, plan.ImportedStdModules)).ShouldBe("ok\n");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task BuildCompilationSource_should_allow_intrinsic_standard_library_modules_without_source_files()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "ashes.json"), """{"entry":"Main.ash","sourceRoots":["."]}""");
            File.WriteAllText(
                Path.Combine(root, "Main.ash"),
                "import Ashes.File\nmatch Ashes.File.exists(\"file.txt\") with | Ok(found) -> if found then Ashes.IO.print(1) else Ashes.IO.print(0) | Error(_) -> Ashes.IO.print(0)");

            var plan = ProjectSupport.BuildCompilationPlan(ProjectSupport.LoadProject(Path.Combine(root, "ashes.json")));
            plan.ImportedStdModules.ShouldContain("Ashes.File");

            var combinedSource = ProjectSupport.BuildCompilationSource(plan);

            (await CompileRunCaptureAsync(combinedSource, plan.ImportedStdModules)).ShouldBe("0\n");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void BuildCompilationSource_should_fail_on_imported_export_name_collision()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "ashes.json"), """{"entry":"Main.ash","sourceRoots":["."]}""");
            File.WriteAllText(Path.Combine(root, "Main.ash"), "import A\nimport B\nAshes.IO.print(x)");
            File.WriteAllText(Path.Combine(root, "A.ash"), "let x = 1 in x");
            File.WriteAllText(Path.Combine(root, "B.ash"), "let x = 2 in x");

            var plan = ProjectSupport.BuildCompilationPlan(ProjectSupport.LoadProject(Path.Combine(root, "ashes.json")));
            var ex = Should.Throw<InvalidOperationException>(() => ProjectSupport.BuildCompilationSource(plan));
            ex.Message.ShouldContain("Import name collision for imported binding 'x'");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void BuildCompilationPlan_should_reject_reserved_ashes_entry_module()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "ashes.json"), """{"entry":"Ashes.ash","sourceRoots":["."]}""");
            File.WriteAllText(Path.Combine(root, "Ashes.ash"), "Ashes.IO.print(1)");

            var project = ProjectSupport.LoadProject(Path.Combine(root, "ashes.json"));
            var ex = Should.Throw<InvalidOperationException>(() => ProjectSupport.BuildCompilationPlan(project));
            ex.Message.ShouldContain("Module name 'Ashes' is reserved");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ashes-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<string> CompileRunCaptureAsync(string source, IReadOnlySet<string>? importedStdModules = null)
    {
        var diag = new Diagnostics();
        var ast = new Parser(source, diag).ParseProgram();
        diag.ThrowIfAny();

        var ir = new Lowering(diag, importedStdModules).Lower(ast);
        diag.ThrowIfAny();

        byte[] image;
        string exePath;
        if (OperatingSystem.IsWindows())
        {
            image = new Ashes.Backend.Backends.WindowsX64LlvmBackend().Compile(ir);
            exePath = Path.Combine(Path.GetTempPath(), $"ashes-tests-{Guid.NewGuid():N}.exe");
            await File.WriteAllBytesAsync(exePath, image);
        }
        else
        {
            image = new Ashes.Backend.Backends.LinuxX64LlvmBackend().Compile(ir);
            exePath = Path.Combine(Path.GetTempPath(), $"ashes-tests-{Guid.NewGuid():N}");
            await File.WriteAllBytesAsync(exePath, image);
#pragma warning disable CA1416
            File.SetUnixFileMode(exePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416
        }
        try
        {
            var psi = new ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using var proc = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            proc.ExitCode.ShouldBe(0, $"stderr: {stderr}");
            return stdout;
        }
        finally
        {
            if (File.Exists(exePath))
            {
                try
                {
                    File.Delete(exePath);
                }
                catch
                {
                }
            }
        }
    }
}
