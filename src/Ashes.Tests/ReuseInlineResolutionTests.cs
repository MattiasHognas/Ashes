using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class ReuseInlineResolutionTests
{
    [Test]
    public void StitchedHelperCallingAStdlibHelperThroughItsModuleAliasIsNotAForwardReference()
    {
        // Inside a match arm a saturated call to an inlinable helper is inlined so its constructor can
        // reuse the dead cell. The helper below calls the stitched stdlib `Ashes.Text.trimEnd`, whose
        // own body reaches a module sibling through the stitcher's alias name; that alias resolves
        // nowhere outside the stdlib module, so `trimEnd` cannot be inlined in turn and, capturing the
        // alias, has no label to be called by either. The inline gate must therefore decline the outer
        // helper, leaving an ordinary call, instead of reporting the stdlib name as not yet declared.
        string root = Path.Combine(Path.GetTempPath(), $"ashes-inline-resolution-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(
                Path.Combine(root, "ashes.json"),
                """{"name":"inline-resolution-app","entry":"Main.ash","sourceRoots":["."]}""");
            File.WriteAllText(Path.Combine(root, "Render.ash"), """
                type Description =
                    | Description(Str, Str)

                type Wrapped =
                    | Wrapped(Description, Str)

                let formatDescription location description =
                    match description with
                        | Description(opcode, operands) ->
                            let line = "    " + opcode + operands + location
                            in Ashes.Text.trimEnd(line)

                let formatWrapped wrapped =
                    match wrapped with
                        | Wrapped(description, location) ->
                            description
                            |> formatDescription(location)
                """);
            File.WriteAllText(Path.Combine(root, "Main.ash"), """
                import Ashes.Text
                import Render
                Ashes.IO.print(Render.formatWrapped(Render.Wrapped(Render.Description("Add", " a b   "), " (here)")))
                """);

            AshesProject project = ProjectSupport.LoadProject(Path.Combine(root, "ashes.json"));
            ProjectCompilationPlan plan = ProjectSupport.BuildCompilationPlan(project);
            CombinedCompilationLayout layout = ProjectSupport.BuildCompilationLayout(plan);
            Diagnostics diagnostics = new();
            Ashes.Frontend.Program program = new Parser(layout.Source, diagnostics).ParseProgram();
            Lowering lowering = new(diagnostics, plan.ImportedStdModules, plan.MergedAliases, layout.ConstructorModules);
            lowering.SetSourceContext(layout);
            _ = lowering.Lower(program);

            diagnostics.StructuredErrors.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
