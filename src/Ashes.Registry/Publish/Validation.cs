using System.Text;
using System.Text.Json;
using Ashes.Semantics;

namespace Ashes.Registry.Publish;

/// <summary>Outcome of the namespace lint: valid, or invalid with a reason.</summary>
public sealed record ValidationResult(bool Ok, string? Message)
{
    public static ValidationResult Valid { get; } = new(true, null);

    public static ValidationResult Invalid(string message) => new(false, message);
}

/// <summary>Namespace lint: every exported module must live under the package's
/// namespace. Exposed as an interface so the pipeline is mockable and the implementation is swappable
/// (structural check now; compiler-front-end reuse later).</summary>
public interface IManifestValidator
{
    ValidationResult Validate(IReadOnlyList<SourceFile> files, string ns);
}

/// <summary>Public API capability extraction. Interface-first for the same reasons
/// as <see cref="IManifestValidator"/>.</summary>
public interface ICapabilityExtractor
{
    IReadOnlyList<string> PublicCapabilities(IReadOnlyList<SourceFile> files, string ns);
}

/// <summary>
/// Namespace lint over the uploaded source. It reads the package's declared <c>sourceRoots</c> (not a
/// hardcoded <c>src/</c>) to derive each <c>.ash</c> file's module name, and requires every module to be
/// <c>&lt;Namespace&gt;</c> or live under <c>&lt;Namespace&gt;.…</c>. It also inspects inline
/// <c>module X = …</c> declarations via the compiler front end — a file's inline modules compose under
/// its own module name, so checking the declarations is belt-and-suspenders over the file-path check.
/// Non-source metadata (ashes.json, README, LICENSE) and files outside the source roots are exempt.
/// </summary>
public sealed class SemanticManifestValidator : IManifestValidator
{
    public ValidationResult Validate(IReadOnlyList<SourceFile> files, string ns)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);

        var sourceRoots = ReadSourceRoots(files);

        foreach (var file in files)
        {
            var path = file.Path.Replace('\\', '/').TrimStart('/');
            if (!path.EndsWith(".ash", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var moduleName = ModuleName(path, sourceRoots);
            if (moduleName is null)
            {
                continue; // not under a source root — not an exported module
            }

            if (!IsUnderNamespace(moduleName, ns))
            {
                return ValidationResult.Invalid(
                    $"Module '{moduleName}' ({file.Path}) is outside the '{ns}' namespace; a library's modules must live under it.");
            }

            foreach (var inlineModule in InlineModuleNames(file, moduleName))
            {
                if (!IsUnderNamespace(inlineModule, ns))
                {
                    return ValidationResult.Invalid(
                        $"Inline module '{inlineModule}' ({file.Path}) is outside the '{ns}' namespace.");
                }
            }
        }

        return ValidationResult.Valid;
    }

    private static bool IsUnderNamespace(string module, string ns) =>
        string.Equals(module, ns, StringComparison.Ordinal) || module.StartsWith(ns + ".", StringComparison.Ordinal);

    private static string? ModuleName(string path, IReadOnlyList<string> sourceRoots)
    {
        foreach (var root in sourceRoots)
        {
            var prefix = root is "" or "." ? "" : root.TrimEnd('/') + "/";
            if (prefix.Length == 0 || path.StartsWith(prefix, StringComparison.Ordinal))
            {
                return path[prefix.Length..][..^".ash".Length].Replace('/', '.');
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ReadSourceRoots(IReadOnlyList<SourceFile> files)
    {
        var manifest = files.FirstOrDefault(f =>
            string.Equals(f.Path.Replace('\\', '/').TrimStart('/'), "ashes.json", StringComparison.OrdinalIgnoreCase));
        if (manifest is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(manifest.Bytes);
                if (doc.RootElement.TryGetProperty("sourceRoots", out var roots) && roots.ValueKind == JsonValueKind.Array)
                {
                    var list = roots.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()!.Replace('\\', '/'))
                        .ToList();
                    if (list.Count > 0)
                    {
                        return list;
                    }
                }
            }
            catch (JsonException)
            {
                // A malformed manifest is caught elsewhere; fall back to the default layout.
            }
        }

        return ["src"];
    }

    private static IEnumerable<string> InlineModuleNames(SourceFile file, string moduleName)
    {
        var source = Encoding.UTF8.GetString(file.Bytes);
        if (!ProjectSupport.ContainsInlineModule(source))
        {
            return [];
        }

        try
        {
            var parsed = ProjectSupport.ParseImportHeader(source, file.Path);
            var (_, inlineModules) = ProjectSupport.ExpandInlineModules(parsed.SourceWithoutImports, moduleName, file.Path);
            return inlineModules.Select(m => m.ModuleName).ToList();
        }
        catch (InvalidOperationException)
        {
            // Malformed inline modules (e.g. ASH023/ASH024) are reported by the compile step.
            return [];
        }
    }
}

/// <summary>
/// Default capability extractor. Real extraction reuses the compiler front end to read the inferred
/// <c>needs {...}</c> rows on the public API; until that integration lands it reports
/// no capabilities, so the field is present and forward-compatible but not yet authoritative.
/// </summary>
public sealed class EmptyCapabilityExtractor : ICapabilityExtractor
{
    public IReadOnlyList<string> PublicCapabilities(IReadOnlyList<SourceFile> files, string ns) => [];
}
