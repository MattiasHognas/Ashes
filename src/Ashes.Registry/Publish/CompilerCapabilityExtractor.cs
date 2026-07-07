using System.Text;
using Ashes.Frontend;
using Ashes.Semantics;

namespace Ashes.Registry.Publish;

/// <summary>
/// Extracts a package's public-API capability rows by reusing the compiler front end
/// (REGISTRY_API §6): parse + lower the uploaded source, then read the inferred capabilities off the
/// exported bindings via <see cref="Lowering.PublicApiCapabilities"/>. Authoritative because it runs the
/// real inference over the uploaded bytes, not a heuristic scan.
///
/// Best-effort by design: any compiler failure yields "no capabilities" rather than failing the publish.
/// The current cut covers single-module packages (the common small-library case); multi-module stitching
/// arrives with the project-loader integration and is silent until then.
/// </summary>
public sealed class CompilerCapabilityExtractor : ICapabilityExtractor
{
    public IReadOnlyList<string> PublicCapabilities(IReadOnlyList<SourceFile> files, string ns)
    {
        ArgumentNullException.ThrowIfNull(files);

        var ashFiles = files.Where(f => f.Path.EndsWith(".ash", StringComparison.OrdinalIgnoreCase)).ToList();
        if (ashFiles.Count != 1)
        {
            return [];
        }

        try
        {
            var source = Encoding.UTF8.GetString(ashFiles[0].Bytes);
            var diag = new Diagnostics();
            var parsed = ProjectSupport.ParseImportHeader(source, ashFiles[0].Path);
            var layout = ProjectSupport.BuildStandaloneCompilationLayout(
                parsed.SourceWithoutImports, parsed.ImportNames, ashFiles[0].Path);
            var importedStd = parsed.ImportNames.Where(ProjectSupport.IsStdModule).ToHashSet(StringComparer.Ordinal);

            var program = new Parser(layout.Source, diag).ParseProgram();
            if (diag.StructuredErrors.Count > 0)
            {
                return [];
            }

            var lowering = new Lowering(
                diag,
                importedStd.Count == 0 ? null : importedStd,
                parsed.ImportAliases.Count == 0 ? null : parsed.ImportAliases);
            lowering.SetSourceContext(layout);
            lowering.Lower(program);

            return diag.StructuredErrors.Count > 0 ? [] : lowering.PublicApiCapabilities();
        }
#pragma warning disable CA1031 // Intentional: the audit must never turn a compiler hiccup into a publish failure.
        catch (Exception)
#pragma warning restore CA1031
        {
            return [];
        }
    }
}
