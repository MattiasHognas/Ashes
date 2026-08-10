using Ashes.Frontend;
using Ashes.Semantics;

namespace Ashes.Fuzzing.Oracles;

internal sealed record FuzzSemanticCompilation(
    Diagnostics Diagnostics,
    Ashes.Frontend.Program Program,
    IrProgram Ir);

internal static class FuzzSemanticCompiler
{
    internal static FuzzSemanticCompilation Lower(
        string source,
        LoweringConfiguration? configuration = null)
    {
        ParsedImportHeader imports = ProjectSupport.ParseImportHeader(source, "<fuzz>");
        CombinedCompilationLayout layout = ProjectSupport.BuildStandaloneCompilationLayout(
            imports.SourceWithoutImports,
            imports.ImportNames,
            "<fuzz>",
            imports.ImportSelectors);
        Diagnostics diagnostics = new();
        Ashes.Frontend.Program program = new Parser(layout.Source, diagnostics).ParseProgram();
        HashSet<string> importedModules = imports.ImportNames
            .Where(ProjectSupport.IsStdModule)
            .ToHashSet(StringComparer.Ordinal);
        importedModules.Add("Ashes.Trait");
        if (layout.ModuleProvenanceByPath is not null)
        {
            importedModules.UnionWith(layout.ModuleProvenanceByPath.Values
                .Select(provenance => provenance.ModuleName)
                .Where(ProjectSupport.IsStdModule));
        }
        Lowering lowering = new(
            diagnostics,
            importedModules,
            imports.ImportAliases,
            layout.ConstructorModules,
            configuration);
        lowering.SetSourceContext(layout);
        IrProgram ir = lowering.Lower(program);
        return new FuzzSemanticCompilation(diagnostics, program, ir);
    }
}
