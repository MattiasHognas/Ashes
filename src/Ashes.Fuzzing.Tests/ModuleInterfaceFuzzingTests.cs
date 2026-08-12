using Ashes.Frontend;
using Ashes.Fuzzing.Generation;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Fuzzing.Tests;

public sealed class ModuleInterfaceFuzzingTests
{
    [Test]
    public void Generated_module_interfaces_hide_every_non_exported_value()
    {
        for (int caseIndex = 0; caseIndex < 24; caseIndex++)
        {
            FuzzRandom random = new((ulong)(20260812 + caseIndex));
            bool selectPublic = random.NextBool();
            string suffix = caseIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string publicName = "publicValue" + suffix;
            string hiddenName = "hiddenValue" + suffix;
            string selectedName = selectPublic ? publicName : hiddenName;
            string directory = CreateProject(
                $$"""
                export (value {{publicName}})
                let {{hiddenName}} = {{caseIndex}}
                let {{publicName}} = {{hiddenName}} + 1
                """,
                $"import Library\nLibrary.{selectedName}\n");
            try
            {
                CombinedCompilationLayout layout = BuildLayout(directory);
                if (selectPublic)
                {
                    Lower(directory, layout);
                }
                else
                {
                    CompileDiagnosticException exception = Should.Throw<CompileDiagnosticException>(() =>
                        Lower(directory, layout));
                    exception.Message.ShouldContain($"does not export '{hiddenName}'");
                }
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static string CreateProject(string library, string main)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "ashes-module-fuzz",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "ashes.json"),
            """{"entry":"Main.ash","sourceRoots":["."]}""");
        File.WriteAllText(Path.Combine(directory, "Library.ash"), library);
        File.WriteAllText(Path.Combine(directory, "Main.ash"), main);
        return directory;
    }

    private static CombinedCompilationLayout BuildLayout(string directory)
    {
        AshesProject project = ProjectSupport.LoadProject(Path.Combine(directory, "ashes.json"));
        return ProjectSupport.BuildCompilationLayout(ProjectSupport.BuildCompilationPlan(project));
    }

    private static void Lower(string directory, CombinedCompilationLayout layout)
    {
        AshesProject project = ProjectSupport.LoadProject(Path.Combine(directory, "ashes.json"));
        ProjectCompilationPlan plan = ProjectSupport.BuildCompilationPlan(project);
        Diagnostics diagnostics = new();
        Ashes.Frontend.Program program = new Parser(layout.Source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        _ = new Lowering(
            diagnostics,
            plan.ImportedStdModules,
            plan.MergedAliases,
            layout.ConstructorModules).Lower(program);
        diagnostics.ThrowIfAny();
    }
}
