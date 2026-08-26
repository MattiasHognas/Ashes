using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

/// <summary>
/// Source locations of IR emitted for a dependency module of a multi-file project. The stitcher
/// re-renders such a module (export block gone, declarations hoisted, identifiers renamed), so its
/// region of the combined source is not line-for-line the user's file; the layout's line anchors
/// map every rendered fragment back to where it starts in that file.
/// </summary>
public sealed class StitchedSourceLocationTests
{
    private const string TokSource = """
        // A dependency module with a header comment, an export block, a type, and two bindings.
        //
        // The comment and export lines are exactly what the stitcher strips, so every line number
        // below differs from the rendered region's own line count.
        export (
            type Kind(..),
            value describe,
            value classify,
        )

        type Kind =
            | Alpha
            | Beta
            | Gamma

        let describe (kind: Kind) =
            match kind with
                | Alpha -> "alpha"
                | Beta -> "beta"
                | Gamma -> "gamma"

        let classify (text: Str) =
            match text with
                | "abc" -> Alpha
                | "def" -> Beta
                | _ -> Gamma
        """;

    private const string MainSource = """
        import Repro.Tok

        let render (text: Str) =
            let kind = classify(text)
            in describe(kind)

        Ashes.IO.print(render("abc") + render("xyz"))
        """;

    [Test]
    public void Layout_records_a_line_anchor_for_every_stitched_fragment()
    {
        string root = CreateProject();
        try
        {
            CombinedCompilationLayout layout = BuildLayout(root);

            IReadOnlyList<SourceLineAnchor> anchors = layout.SourceLineAnchors.ShouldNotBeNull();
            List<SourceLineAnchor> tokAnchors = anchors
                .Where(anchor => anchor.FilePath.EndsWith("Tok.ash", StringComparison.Ordinal))
                .OrderBy(anchor => anchor.CombinedStart)
                .ToList();

            // One anchor per hoisted declaration and per rendered binding value, each starting on
            // the line where that text starts in the file.
            tokAnchors.Select(anchor => anchor.Line).ShouldBe(
            [
                LineOf(TokSource, "type Kind ="),
                LineOf(TokSource, "match kind with"),
                LineOf(TokSource, "match text with"),
            ]);
            foreach (SourceLineAnchor anchor in tokAnchors)
            {
                anchor.CombinedEnd.ShouldBeGreaterThan(anchor.CombinedStart);
                string fragment = layout.Source.Substring(anchor.CombinedStart, anchor.CombinedEnd - anchor.CombinedStart);
                string expectedStart = anchor.Line == LineOf(TokSource, "type Kind =") ? "type Kind" : "match";
                fragment.ShouldContain(expectedStart);
            }

            // The entry body region is line-preserving and needs no anchors.
            anchors.ShouldAllBe(anchor => !anchor.FilePath.EndsWith("Main.ash", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void Lowered_instructions_of_a_dependency_module_carry_its_own_file_lines()
    {
        string root = CreateProject();
        try
        {
            CombinedCompilationLayout layout = BuildLayout(root);
            Diagnostics diagnostics = new();
            Frontend.Program program = new Parser(layout.Source, diagnostics).ParseProgram();
            diagnostics.ThrowIfAny();
            AshesProject project = ProjectSupport.LoadProject(Path.Combine(root, "ashes.json"));
            ProjectCompilationPlan plan = ProjectSupport.BuildCompilationPlan(project);
            Lowering lowering = new(diagnostics, plan.ImportedStdModules, plan.MergedAliases, layout.ConstructorModules);
            lowering.SetSourceContext(layout);
            IrProgram ir = lowering.Lower(program);
            diagnostics.ThrowIfAny();

            AssertLocatedWithin(ir, "classify", "Tok.ash", LineOf(TokSource, "let classify"), LineOf(TokSource, "| _ -> Gamma"));
            AssertLocatedWithin(ir, "describe", "Tok.ash", LineOf(TokSource, "let describe"), LineOf(TokSource, "| Gamma -> \"gamma\""));
            AssertLocatedWithin(ir, "render", "Main.ash", LineOf(MainSource, "let render"), LineOf(MainSource, "in describe(kind)"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // Every located instruction of every function lowered from the named source declaration lies
    // in that declaration's file and line range, and at least one instruction is located at all.
    private static void AssertLocatedWithin(IrProgram ir, string sourceName, string fileName, int firstLine, int lastLine)
    {
        List<SourceLocation> locations = ir.Functions
            .Where(function => string.Equals(function.Origin?.Source?.SourceName, sourceName, StringComparison.Ordinal))
            .SelectMany(function => function.Instructions)
            .Select(instruction => instruction.Location)
            .OfType<SourceLocation>()
            .ToList();
        locations.ShouldNotBeEmpty($"{sourceName} must carry source locations");
        locations.ShouldAllBe(location => location.FilePath.EndsWith(fileName, StringComparison.Ordinal));
        locations.ShouldAllBe(location => location.Line >= firstLine && location.Line <= lastLine);
        locations.Select(location => location.Line).Distinct().Count().ShouldBeGreaterThan(1);
    }

    private static int LineOf(string source, string text)
    {
        int index = source.IndexOf(text, StringComparison.Ordinal);
        index.ShouldBeGreaterThanOrEqualTo(0, text);
        return source[..index].Count(c => c == '\n') + 1;
    }

    private static string CreateProject()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ashes-stitched-lines-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "src", "Repro"));
        File.WriteAllText(
            Path.Combine(root, "ashes.json"),
            """{"name":"stitched-lines-app","entry":"Main.ash","sourceRoots":["src"]}""");
        File.WriteAllText(Path.Combine(root, "src", "Repro", "Tok.ash"), TokSource + "\n");
        File.WriteAllText(Path.Combine(root, "Main.ash"), MainSource + "\n");
        return root;
    }

    private static CombinedCompilationLayout BuildLayout(string root)
    {
        AshesProject project = ProjectSupport.LoadProject(Path.Combine(root, "ashes.json"));
        ProjectCompilationPlan plan = ProjectSupport.BuildCompilationPlan(project);
        return ProjectSupport.BuildCompilationLayout(plan);
    }
}
