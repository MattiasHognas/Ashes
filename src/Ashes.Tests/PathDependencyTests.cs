using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

/// <summary>Phase 1 package manager: a project depending on another on disk via a <c>path</c> dependency,
/// imported under the dependency's namespace.</summary>
public sealed class PathDependencyTests
{
    [Test]
    public void LoadProject_resolves_a_path_dependency_to_its_source_roots()
    {
        var appManifest = WriteWorkspace();
        var project = ProjectSupport.LoadProject(appManifest);

        var dep = project.Dependencies.ShouldHaveSingleItem();
        dep.Name.ShouldBe("greet");
        dep.Namespace.ShouldBe("Greet");
        dep.IsDev.ShouldBeFalse();
        var root = dep.SourceRoots.ShouldHaveSingleItem();
        File.Exists(Path.Combine(root, "Greet.ash")).ShouldBeTrue();
    }

    [Test]
    public void A_path_dependency_module_is_stitched_into_the_build()
    {
        var appManifest = WriteWorkspace();
        var project = ProjectSupport.LoadProject(appManifest);

        var plan = ProjectSupport.BuildCompilationPlan(project);
        var source = ProjectSupport.BuildCompilationSource(plan);

        // The dependency's exported binding is stitched in under its module-qualified name.
        source.ShouldContain("Greet_hello");
    }

    [Test]
    public void A_missing_path_dependency_is_a_clear_error()
    {
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "app", "src"));
        File.WriteAllText(Path.Combine(root, "app", "ashes.json"),
            """{ "name": "app", "entry": "src/Main.ash", "sourceRoots": ["src"], "dependencies": { "ghost": { "path": "../ghost" } } }""");
        File.WriteAllText(Path.Combine(root, "app", "src", "Main.ash"), "Ashes.IO.print(\"hi\")\n");

        var ex = Should.Throw<InvalidOperationException>(
            () => ProjectSupport.LoadProject(Path.Combine(root, "app", "ashes.json")));
        ex.Message.ShouldContain("ghost");
    }

    [Test]
    public void A_dependency_module_outside_its_namespace_is_rejected()
    {
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "dep", "src"));
        Directory.CreateDirectory(Path.Combine(root, "app", "src"));
        File.WriteAllText(Path.Combine(root, "dep", "ashes.json"),
            """{ "name": "greet", "entry": "src/Greet.ash", "sourceRoots": ["src"] }""");
        File.WriteAllText(Path.Combine(root, "dep", "src", "Greet.ash"), "let hello = given (n) -> n\n");
        File.WriteAllText(Path.Combine(root, "dep", "src", "Stray.ash"), "let x = 1\n");
        File.WriteAllText(Path.Combine(root, "app", "ashes.json"),
            """{ "name": "app", "entry": "src/Main.ash", "sourceRoots": ["src"], "dependencies": { "greet": { "path": "../dep" } } }""");
        File.WriteAllText(Path.Combine(root, "app", "src", "Main.ash"), "Ashes.IO.print(\"hi\")\n");

        var ex = Should.Throw<InvalidOperationException>(
            () => ProjectSupport.LoadProject(Path.Combine(root, "app", "ashes.json")));
        ex.Message.ShouldContain("ASH028");
        ex.Message.ShouldContain("Stray");
    }

    [Test]
    public void Two_dependencies_claiming_the_same_namespace_are_rejected()
    {
        var root = NewRoot();
        foreach (var (dir, name) in new[] { ("dep1", "greet"), ("dep2", "greeting") })
        {
            Directory.CreateDirectory(Path.Combine(root, dir, "src"));
            File.WriteAllText(Path.Combine(root, dir, "ashes.json"),
                $$"""{ "name": "{{name}}", "namespace": "Greet", "entry": "src/Greet.ash", "sourceRoots": ["src"] }""");
            File.WriteAllText(Path.Combine(root, dir, "src", "Greet.ash"), "let hello = given (n) -> n\n");
        }

        Directory.CreateDirectory(Path.Combine(root, "app", "src"));
        File.WriteAllText(Path.Combine(root, "app", "ashes.json"),
            """{ "name": "app", "entry": "src/Main.ash", "sourceRoots": ["src"], "dependencies": { "greet": { "path": "../dep1" }, "greeting": { "path": "../dep2" } } }""");
        File.WriteAllText(Path.Combine(root, "app", "src", "Main.ash"), "Ashes.IO.print(\"hi\")\n");

        var ex = Should.Throw<InvalidOperationException>(
            () => ProjectSupport.LoadProject(Path.Combine(root, "app", "ashes.json")));
        ex.Message.ShouldContain("ASH029");
    }

    [Test]
    public void Path_dependencies_are_resolved_transitively()
    {
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "base", "src"));
        Directory.CreateDirectory(Path.Combine(root, "mid", "src"));
        Directory.CreateDirectory(Path.Combine(root, "app", "src"));

        File.WriteAllText(Path.Combine(root, "base", "ashes.json"),
            """{ "name": "base", "entry": "src/Base.ash", "sourceRoots": ["src"] }""");
        File.WriteAllText(Path.Combine(root, "base", "src", "Base.ash"), "let greeting = given (n) -> n\n");

        File.WriteAllText(Path.Combine(root, "mid", "ashes.json"),
            """{ "name": "mid", "entry": "src/Mid.ash", "sourceRoots": ["src"], "dependencies": { "base": { "path": "../base" } } }""");
        File.WriteAllText(Path.Combine(root, "mid", "src", "Mid.ash"),
            "import Base\nlet shout = given (n) -> Base.greeting(n)\n");

        File.WriteAllText(Path.Combine(root, "app", "ashes.json"),
            """{ "name": "app", "entry": "src/Main.ash", "sourceRoots": ["src"], "dependencies": { "mid": { "path": "../mid" } } }""");
        File.WriteAllText(Path.Combine(root, "app", "src", "Main.ash"),
            "import Mid\nAshes.IO.print(Mid.shout(\"hi\"))\n");

        var project = ProjectSupport.LoadProject(Path.Combine(root, "app", "ashes.json"));

        // 'base' is only a dependency of 'mid', yet it is pulled into the app's build.
        project.Dependencies.Select(d => d.Namespace).ShouldBe(["Mid", "Base"], ignoreOrder: true);
        var source = ProjectSupport.BuildCompilationSource(ProjectSupport.BuildCompilationPlan(project));
        source.ShouldContain("Base_greeting");
    }

    [Test]
    public void A_dependency_cycle_is_rejected()
    {
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "a", "src"));
        Directory.CreateDirectory(Path.Combine(root, "b", "src"));

        File.WriteAllText(Path.Combine(root, "a", "ashes.json"),
            """{ "name": "a", "namespace": "A", "entry": "src/A.ash", "sourceRoots": ["src"], "dependencies": { "b": { "path": "../b" } } }""");
        File.WriteAllText(Path.Combine(root, "a", "src", "A.ash"), "let x = given (n) -> n\n");
        File.WriteAllText(Path.Combine(root, "b", "ashes.json"),
            """{ "name": "b", "namespace": "B", "entry": "src/B.ash", "sourceRoots": ["src"], "dependencies": { "a": { "path": "../a" } } }""");
        File.WriteAllText(Path.Combine(root, "b", "src", "B.ash"), "let y = given (n) -> n\n");

        var ex = Should.Throw<InvalidOperationException>(
            () => ProjectSupport.LoadProject(Path.Combine(root, "a", "ashes.json")));
        ex.Message.ShouldContain("ASH035");
    }

    private static string WriteWorkspace()
    {
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "dep", "src"));
        Directory.CreateDirectory(Path.Combine(root, "app", "src"));

        File.WriteAllText(Path.Combine(root, "dep", "ashes.json"),
            """{ "name": "greet", "entry": "src/Greet.ash", "sourceRoots": ["src"] }""");
        File.WriteAllText(Path.Combine(root, "dep", "src", "Greet.ash"),
            "let hello = given (name) -> name\n");

        File.WriteAllText(Path.Combine(root, "app", "ashes.json"),
            """{ "name": "app", "entry": "src/Main.ash", "sourceRoots": ["src"], "dependencies": { "greet": { "path": "../dep" } } }""");
        File.WriteAllText(Path.Combine(root, "app", "src", "Main.ash"),
            "import Greet\nAshes.IO.print(Greet.hello(\"hi\"))\n");

        return Path.Combine(root, "app", "ashes.json");
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ashes-pathdep-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
