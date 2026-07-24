using System.Text;
using Ashes.Frontend;
using Ashes.Semantics;

namespace Ashes.Registry.Publish;

/// <summary>
/// Extracts a package's public-API capability rows by reusing the compiler front end: parse and lower
/// the uploaded source, then read the inferred capabilities off the
/// exported bindings via <see cref="Lowering.PublicApiCapabilities"/>. Authoritative because it runs the
/// real inference over the uploaded bytes, not a heuristic scan.
///
/// Multi-module packages are stitched through the real project loader when the upload carries an
/// <c>ashes.json</c> (as <c>ashes publish</c> produces); a bare single module is compiled standalone.
/// Best-effort by design: any compiler failure (including an unresolved external dependency) yields "no
/// capabilities" rather than failing the publish.
/// </summary>
public sealed class CompilerCapabilityExtractor : ICapabilityExtractor
{
    /// <inheritdoc/>
    public IReadOnlyList<string> PublicCapabilities(IReadOnlyList<SourceFile> files, string ns)
    {
        ArgumentNullException.ThrowIfNull(files);

        try
        {
            var manifest = files.FirstOrDefault(f => IsManifest(f.Path));
            return manifest is not null ? ExtractViaProject(files) : ExtractStandalone(files);
        }
#pragma warning disable CA1031 // Intentional: the audit must never turn a compiler hiccup into a publish failure.
        catch (Exception)
#pragma warning restore CA1031
        {
            return [];
        }
    }

    private static IReadOnlyList<string> ExtractViaProject(IReadOnlyList<SourceFile> files)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ashes-registry-audit", Guid.NewGuid().ToString("N"));
        try
        {
            var manifestPath = MaterializeFiles(files, tempDir);
            if (manifestPath is null)
            {
                return [];
            }

            var diag = new Diagnostics();
            var project = ProjectSupport.LoadProject(manifestPath);
            var plan = ProjectSupport.BuildCompilationPlan(project);
            var layout = ProjectSupport.BuildCompilationLayout(plan);

            var program = new Parser(layout.Source, diag).ParseProgram();
            if (diag.StructuredErrors.Count > 0)
            {
                return [];
            }

            var lowering = new Lowering(
                diag, plan.ImportedStdModules, plan.MergedAliases.Count == 0 ? null : plan.MergedAliases);
            lowering.SetSourceContext(layout);
            lowering.Lower(program);

            return diag.StructuredErrors.Count > 0 ? [] : lowering.PublicApiCapabilities();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static IReadOnlyList<string> ExtractStandalone(IReadOnlyList<SourceFile> files)
    {
        var ashFiles = files.Where(f => f.Path.EndsWith(".ash", StringComparison.OrdinalIgnoreCase)).ToList();
        if (ashFiles.Count != 1)
        {
            return [];
        }

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

    /// <summary>Write the uploaded files under <paramref name="tempDir"/>, returning the manifest path.
    /// Paths are validated (no traversal); the pipeline already enforced this, but the write is defensive.</summary>
    private static string? MaterializeFiles(IReadOnlyList<SourceFile> files, string tempDir)
    {
        var root = Path.GetFullPath(tempDir);
        Directory.CreateDirectory(root);
        string? manifestPath = null;

        foreach (var file in files)
        {
            var target = Path.GetFullPath(Path.Combine(root, file.Path));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return null; // escapes the temp root; refuse
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, file.Bytes);

            if (IsManifest(file.Path))
            {
                manifestPath = target;
            }
        }

        return manifestPath;
    }

    private static bool IsManifest(string path) =>
        string.Equals(path.Replace('\\', '/').TrimStart('/'), "ashes.json", StringComparison.OrdinalIgnoreCase);
}
