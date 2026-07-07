namespace Ashes.Registry.Publish;

/// <summary>Outcome of the namespace lint: valid, or invalid with a reason.</summary>
public sealed record ValidationResult(bool Ok, string? Message)
{
    public static ValidationResult Valid { get; } = new(true, null);

    public static ValidationResult Invalid(string message) => new(false, message);
}

/// <summary>Namespace lint (PACKAGE_MANAGER §2): every exported module must live under the package's
/// namespace. Exposed as an interface so the pipeline is mockable and the implementation is swappable
/// (structural check now; compiler-front-end reuse later, REGISTRY_API §6).</summary>
public interface IManifestValidator
{
    ValidationResult Validate(IReadOnlyList<SourceFile> files, string ns);
}

/// <summary>Public API capability extraction (PACKAGE_MANAGER §8). Interface-first for the same reasons
/// as <see cref="IManifestValidator"/>.</summary>
public interface ICapabilityExtractor
{
    IReadOnlyList<string> PublicCapabilities(IReadOnlyList<SourceFile> files, string ns);
}

/// <summary>
/// Structural namespace lint over file paths: every <c>.ash</c> module (ignoring a leading <c>src/</c>
/// source root) must be <c>&lt;Namespace&gt;.ash</c> or live under <c>&lt;Namespace&gt;/</c>. Non-source
/// metadata (ashes.json, README, LICENSE) is exempt. This catches the path-level violations without the
/// type checker; the semantic lint over *exported* modules arrives with capability extraction.
/// </summary>
public sealed class StructuralManifestValidator : IManifestValidator
{
    public ValidationResult Validate(IReadOnlyList<SourceFile> files, string ns)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);

        foreach (var file in files)
        {
            var path = StripSourceRoot(file.Path.Replace('\\', '/'));
            if (!path.EndsWith(".ash", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var underNamespace = string.Equals(path, ns + ".ash", StringComparison.Ordinal)
                || path.StartsWith(ns + "/", StringComparison.Ordinal);
            if (!underNamespace)
            {
                return ValidationResult.Invalid(
                    $"Module '{file.Path}' is outside the '{ns}' namespace; a library's modules must live under it.");
            }
        }

        return ValidationResult.Valid;
    }

    private static string StripSourceRoot(string path)
    {
        const string root = "src/";
        return path.StartsWith(root, StringComparison.Ordinal) ? path[root.Length..] : path;
    }
}

/// <summary>
/// Default capability extractor. Real extraction reuses the compiler front end to read the inferred
/// <c>needs {...}</c> rows on the public API (REGISTRY_API §6); until that integration lands it reports
/// no capabilities, so the field is present and forward-compatible but not yet authoritative.
/// </summary>
public sealed class EmptyCapabilityExtractor : ICapabilityExtractor
{
    public IReadOnlyList<string> PublicCapabilities(IReadOnlyList<SourceFile> files, string ns) => [];
}
