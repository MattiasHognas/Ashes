using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;
using Ashes.Frontend;

namespace Ashes.Semantics;

/// <summary>
/// A loaded <c>ashes.json</c> project: its entry point, source roots, output settings, and resolved
/// dependencies. Produced by <see cref="ProjectSupport.LoadProject(string)"/> and consumed by
/// <see cref="ProjectSupport.BuildCompilationPlan(AshesProject)"/>.
/// </summary>
/// <param name="ProjectFilePath">Absolute path to the <c>ashes.json</c> file.</param>
/// <param name="ProjectDirectory">Absolute path to the directory containing the project file.</param>
/// <param name="EntryPath">Absolute path to the project's entry <c>.ash</c> source file.</param>
/// <param name="EntryModuleName">The module name derived from the entry file's path.</param>
/// <param name="Name">The project's declared name, or null when unspecified.</param>
/// <param name="SourceRoots">Directories searched for module sources, in priority order.</param>
/// <param name="Include">Explicit source-file include globs from the project file.</param>
/// <param name="OutDir">Directory into which build artifacts are written.</param>
/// <param name="Target">The declared build target RID, or null to use the host default.</param>
public sealed record AshesProject(
    string ProjectFilePath,
    string ProjectDirectory,
    string EntryPath,
    string EntryModuleName,
    string? Name,
    IReadOnlyList<string> SourceRoots,
    IReadOnlyList<string> Include,
    string OutDir,
    string? Target
)
{
    /// <summary>
    /// Resolved dependencies whose source roots are appended to the compilation search roots. Path
    /// dependencies resolve directly from disk; registry/git dependencies are materialized by the CLI
    /// (lock + cache) and surfaced here through the same list. Kept as an init property with an empty
    /// default so existing constructor call sites are unaffected.
    /// </summary>
    public IReadOnlyList<ResolvedDependency> Dependencies { get; init; } = [];
}

/// <summary>A dependency resolved to concrete source roots on disk, imported under its namespace.</summary>
public sealed record ResolvedDependency(
    string Name,
    string Namespace,
    IReadOnlyList<string> SourceRoots,
    string ProjectDirectory,
    bool IsDev)
{
    /// <summary>The dependency's own entry file, if any — exempt from the namespace lint because a
    /// project's entry is never one of its exports.</summary>
    public string? EntryFile { get; init; }
}

/// <summary>
/// One source module participating in a compilation plan, with its imports already parsed out of the
/// source. Both file-backed modules and lifted inline modules are represented uniformly here.
/// </summary>
/// <param name="ModuleName">The module's fully qualified path-derived name.</param>
/// <param name="FilePath">The source file this module was loaded from (a synthetic <c>path#Name</c> for inline modules).</param>
/// <param name="Source">The module's source text with its import header stripped.</param>
/// <param name="Imports">The whole-module names this module imports.</param>
/// <param name="Aliases">Import aliases (<c>import M as X</c>) mapping alias to module name.</param>
/// <param name="Selectors">Selector imports (<c>import M.name</c>) that bring single exports in unqualified.</param>
public sealed record ProjectModule(
    string ModuleName,
    string FilePath,
    string Source,
    IReadOnlyList<string> Imports,
    IReadOnlyDictionary<string, string> Aliases,
    IReadOnlyList<ImportSelector> Selectors
)
{
    /// <summary>The manifest-derived package identity used by coherence and orphan checks.</summary>
    public string PackageId { get; init; } = "standalone";
}

/// <summary>
/// A selector import (<c>import M.name</c> / <c>import M.Type</c>, optionally <c>as alias</c>) that
/// brings a single exported binding or type into the importing module's scope <em>unqualified</em>.
/// <see cref="ModuleName"/> is the resolved source module, <see cref="ExportName"/> the selected
/// export, and <see cref="LocalName"/> the unqualified name introduced (the alias, or the export
/// name when no <c>as</c> clause is present).
/// </summary>
public readonly record struct ImportSelector(
    string ModuleName,
    string ExportName,
    string LocalName
);

/// <summary>
/// A fully resolved build plan: the modules to compile in dependency order, the designated entry
/// module, and the merged import surface. Produced by
/// <see cref="ProjectSupport.BuildCompilationPlan(AshesProject)"/>.
/// </summary>
/// <param name="Project">The project this plan was built for.</param>
/// <param name="OrderedModules">All modules to compile, topologically ordered so dependencies precede dependents.</param>
/// <param name="EntryModule">The module holding the program's entry point.</param>
/// <param name="ImportedStdModules">The set of standard-library modules transitively imported by the plan.</param>
/// <param name="MergedAliases">Import aliases visible across the combined compilation, mapping alias to module name.</param>
public sealed record ProjectCompilationPlan(
    AshesProject Project,
    IReadOnlyList<ProjectModule> OrderedModules,
    ProjectModule EntryModule,
    IReadOnlySet<string> ImportedStdModules,
    IReadOnlyDictionary<string, string> MergedAliases
);

/// <summary>
/// The result of splitting a module's import header from its body:
/// <see cref="ProjectSupport.ParseImportHeader(string, string)"/> returns the imported names, the
/// body source with the header removed, and the alias and selector import tables.
/// </summary>
/// <param name="ImportNames">The whole-module names imported by the header.</param>
/// <param name="SourceWithoutImports">The module source with the import header lines removed.</param>
/// <param name="ImportAliases">Alias imports mapping alias to module name.</param>
/// <param name="ImportSelectors">Selector imports parsed from the header.</param>
public readonly record struct ParsedImportHeader(
    IReadOnlyList<string> ImportNames,
    string SourceWithoutImports,
    IReadOnlyDictionary<string, string> ImportAliases,
    IReadOnlyList<ImportSelector> ImportSelectors
);

/// <summary>
/// The combined single-source view stitched together from all plan modules, together with the offset
/// maps needed to translate diagnostics on the combined text back to their originating files.
/// </summary>
/// <param name="Source">The concatenated source text handed to the frontend as one unit.</param>
/// <param name="EntryOffset">Host-string index in <paramref name="Source"/> where the entry module's content begins.</param>
/// <param name="BodyStart">Host-string index where the entry module's trailing body expression begins.</param>
/// <param name="ModuleOffsets">Per-file host-string spans within the combined source.</param>
/// <param name="EntryTypeDeclFragments">Host-string spans of entry-module type declarations hoisted into the combined preamble, mapping combined index to original index and length; null when none were hoisted.</param>
/// <param name="ConstructorModules">Maps each module name to the ADT constructor names its own <c>type</c> declarations introduce, so a qualified reference (<c>alias.Ctor</c>) can be scoped to the module the alias actually names.</param>
/// <param name="FunctionSourceNames">Maps compiler binding names in the stitched source back to the
/// original source and module-qualified declaration names.</param>
/// <param name="ModuleProvenanceByPath">Maps stitched source paths to manifest package and module identities.</param>
public readonly record struct CombinedCompilationLayout(
    string Source,
    int EntryOffset,
    int BodyStart,
    IReadOnlyList<(string FilePath, int StartOffset, int EndOffset)> ModuleOffsets,
    IReadOnlyList<(int CombinedStart, int OriginalStart, int Length)>? EntryTypeDeclFragments = null,
    IReadOnlyDictionary<string, IReadOnlySet<string>>? ConstructorModules = null,
    IReadOnlyDictionary<string, SourceFunctionName>? FunctionSourceNames = null,
    IReadOnlyDictionary<string, ModuleProvenance>? ModuleProvenanceByPath = null
);

/// <summary>Manifest package and module identity for one stitched source path.</summary>
public sealed record ModuleProvenance(string PackageId, string ModuleName);

/// <summary>
/// Original declaration names retained while project modules are stitched into compiler bindings.
/// </summary>
/// <param name="SourceName">The declaration name in its source module.</param>
/// <param name="QualifiedName">The canonical module-qualified declaration name.</param>
public sealed record SourceFunctionName(string SourceName, string QualifiedName);

/// <summary>
/// Multi-file project support: discovers and loads <c>ashes.json</c> projects, parses import
/// headers, resolves the standard library and inline modules, orders modules by dependency, and
/// stitches them into a single combined compilation unit for the frontend.
/// </summary>
public static class ProjectSupport
{
    /// <summary>Compiler-only prefix used when stitching a constructor hidden by an explicit interface.</summary>
    public const string PrivateConstructorPrefix = "AshesPrivateConstructor_";
    /// <summary>Compiler-only prefix used when stitching a type hidden by an explicit interface.</summary>
    public const string PrivateTypePrefix = "AshesPrivateType_";

    // Inline modules (LANGUAGE_SPEC §13.1)
    // An inline `module Name = <indented block>` is lifted, before shaping/combination, into a
    // synthetic module whose name is the file-composed path (`File.Name`). Within the defining file
    // a bare qualifier `Name.member` is rewritten to the composed path `File.Name.member`, which the
    // rest of the pipeline (mangling, qualified-reference resolution, cross-file imports) then treats
    // exactly like a separate `File/Name.ash` file — so inline ↔ file promotion is transparent.

    /// <summary>A header line <c>module Name =</c> at the column captured by the <c>indent</c> group; the block body is the run of lines indented past it. A trailing line comment after the <c>=</c> is permitted (and dropped), matching where comments are ignored elsewhere.</summary>
    private static readonly Regex InlineModuleHeader = new(
        @"^(?<indent>[ \t]*)module[ \t]+(?<name>[A-Z][A-Za-z0-9_]*)[ \t]*=[ \t]*(//[^\n]*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>A lifted inline module: its file-composed <see cref="ModuleName"/> and its (rewritten) block source.</summary>
    private sealed record InlineModule(string ModuleName, string Source);

    /// <summary>Whether a source contains an inline <c>module Name = ...</c> declaration header.</summary>
    public static bool ContainsInlineModule(string source)
    {
        foreach (var line in source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (InlineModuleHeader.IsMatch(line))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record StandardLibraryModuleDescriptor(string ModuleName, string? ResourceName);

    private sealed record ModuleBindingFragment(string Name, string ValueSource, bool IsRecursive, string? Annotation = null);

    private sealed record ModuleBindingGroup(IReadOnlyList<ModuleBindingFragment> Bindings, bool IsRecursiveGroup);

    private sealed record ModuleExportPolicy(
        bool IsExplicit,
        IReadOnlySet<string> Values,
        IReadOnlySet<string> Types,
        IReadOnlySet<string> Constructors,
        IReadOnlySet<string> Modules)
    {
        public static ModuleExportPolicy Compatibility { get; } = new(
            false,
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));
    }

    private readonly record struct QualifiedReference(string ModuleName, string ExportName);

    private readonly record struct ReferencedNames(
        IReadOnlySet<string> Variables,
        IReadOnlySet<QualifiedReference> QualifiedReferences);

    private sealed record ModuleSourceShape(
        string TypeDeclarationsSource,
        string RawExpressionSource,
        string ExpressionBodySource,
        IReadOnlyList<ModuleBindingGroup> TopLevelBindings,
        string? LegacyExportName,
        bool IsFlat,
        ModuleExportPolicy? ExportPolicy = null,
        bool HasTrailingExpression = true,
        // Original position of each hoisted declaration fragment inside TypeDeclarationsSource:
        // (offset within TypeDeclarationsSource, offset in the module's imports-stripped source,
        // fragment length). Lets diagnostics that land in the hoisted-declaration region of the
        // combined source map back to exact original offsets.
        IReadOnlyList<(int FragmentStart, int OriginalStart, int Length)>? TypeDeclFragments = null);

    /// <summary>
    /// Regex matching a single <c>import</c> line: a dotted module path, an optional selector leaf,
    /// and an optional <c>as</c> alias. Exposed so other phases match import syntax identically.
    /// </summary>
    public const string ImportModulePattern = @"^\s*import\s+([A-Z][A-Za-z0-9_]*(?:\.[A-Z][A-Za-z0-9_]*)*)(?:\.([a-z_][A-Za-z0-9_]*))?(?:\s+as\s+([A-Za-z][A-Za-z0-9_]*))?\s*$";

    /// <summary>Message for <c>ASH016</c>: two unqualified selectors collide on the same name.</summary>
    private static string ConflictingSelectorMessage(string name) =>
        $"Conflicting unqualified import selectors for '{name}'.";

    private static readonly Regex ImportLine = new(
        ImportModulePattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1)
    );

    private static readonly IReadOnlyDictionary<string, StandardLibraryModuleDescriptor> StdModules =
        BuiltinRegistry.StandardModuleNames
            .Select(moduleName =>
            {
                BuiltinRegistry.TryGetModule(moduleName, out var module);
                return new StandardLibraryModuleDescriptor(module.Name, module.ResourceName);
            })
            .ToDictionary(x => x.ModuleName, x => x, StringComparer.Ordinal);

    private static readonly Lazy<IReadOnlyDictionary<string, string>> EmbeddedStdSources = new(LoadEmbeddedStandardLibrarySources);

    private static readonly Lazy<string?> ShippedLibraryRoot = new(DiscoverShippedLibraryRoot);

    private static readonly HashSet<string> KnownStdModules = [.. StdModules.Keys];

    private static readonly HashSet<string> ReservedKeywords = new(StringComparer.Ordinal)
    {
        "let", "recursive", "in", "if", "then", "else", "match", "with",
        "given", "true", "false", "type", "await", "external",
        "capability", "needs", "perform", "handle", "trait", "implement", "requires"
    };

    /// <summary>The names of every standard-library module recognized when resolving imports.</summary>
    public static IReadOnlyCollection<string> KnownStandardLibraryModules => KnownStdModules;

    /// <summary>
    /// Walks upward from <paramref name="startDirectory"/> looking for an <c>ashes.json</c> file,
    /// returning its path when found or null when no project file exists in any ancestor directory.
    /// </summary>
    public static string? DiscoverProjectFile(string startDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "ashes.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }

    /// <summary>
    /// Parses the <c>ashes.json</c> at <paramref name="projectPath"/> into an <see cref="AshesProject"/>,
    /// resolving its entry file, source roots, and output settings. Throws when the file is missing or
    /// not a valid project object.
    /// </summary>
    public static AshesProject LoadProject(string projectPath)
    {
        var fullProjectPath = Path.GetFullPath(projectPath);
        if (!File.Exists(fullProjectPath))
        {
            throw new InvalidOperationException($"Project file not found: {projectPath}");
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(fullProjectPath));
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Project file must contain a JSON object.");
        }

        var root = doc.RootElement;
        var projectDirectory = Path.GetDirectoryName(fullProjectPath)!;

        var entryPath = ResolveEntryPath(root, projectDirectory);

        var sourceRoots = ReadStringArray(root, "sourceRoots");
        if (sourceRoots.Count == 0)
        {
            sourceRoots.Add(".");
        }

        var include = ReadStringArray(root, "include");

        var outDirValue = ReadString(root, "outDir");
        var outDir = ResolvePath(projectDirectory, string.IsNullOrWhiteSpace(outDirValue) ? "out" : outDirValue);

        var name = ReadString(root, "name");
        var target = ReadString(root, "target");

        var dependencies = ResolveDependencies(root, fullProjectPath, projectDirectory);
        ValidateDependencyNamespaces(dependencies);

        return new AshesProject(
            ProjectFilePath: fullProjectPath,
            ProjectDirectory: projectDirectory,
            EntryPath: entryPath,
            EntryModuleName: Path.GetFileNameWithoutExtension(entryPath),
            Name: name,
            SourceRoots: sourceRoots.Select(x => ResolvePath(projectDirectory, x)).ToList(),
            Include: include.Select(x => ResolvePath(projectDirectory, x)).ToList(),
            OutDir: outDir,
            Target: target
        )
        {
            Dependencies = dependencies,
        };
    }

    private static string ResolveEntryPath(JsonElement root, string projectDirectory)
    {
        var entryValue = ReadString(root, "entry")
            ?? throw new InvalidOperationException("Project file is missing required string field 'entry'.");

        var entryPath = ResolvePath(projectDirectory, entryValue);
        if (!entryPath.EndsWith(".ash", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Project entry must be a .ash file: {entryValue}");
        }

        if (!File.Exists(entryPath))
        {
            throw new InvalidOperationException($"Project entry file not found: {entryPath}");
        }

        return entryPath;
    }

    /// <summary>
    /// Resolve the manifest's <c>dependencies</c> and <c>devDependencies</c>. Only path dependencies
    /// resolve locally here (deterministic, from disk); registry/git dependencies are materialized by the
    /// CLI restore step into the lock and cache. The compiler is never the dependency solver.
    /// </summary>
    private static IReadOnlyList<ResolvedDependency> ResolveDependencies(
        JsonElement root,
        string projectFilePath,
        string projectDirectory)
    {
        var result = new List<ResolvedDependency>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Overrides are a root-project policy. Resolve them before ordinary path dependencies so a
        // checkout named in both places is only loaded once, and never inspect overrides recursively.
        var overriddenNamespaces = CollectRootOverrides(
            root, projectFilePath, projectDirectory, result, visited);

        // A dependency's own `dependencies` are pulled transitively; its `devDependencies` are not
        // (they build/test that package only). So the root follows both maps, recursion follows only
        // `dependencies`, and dev-ness is inherited down each chain.
        CollectPathDependencies(root, projectDirectory, "dependencies", isDev: false, result, visited, []);
        CollectPathDependencies(root, projectDirectory, "devDependencies", isDev: true, result, visited, []);
        AddLockedDependencies(projectFilePath, result, overriddenNamespaces);
        return result;
    }

    private static HashSet<string> CollectRootOverrides(
        JsonElement manifest,
        string projectFilePath,
        string projectDirectory,
        List<ResolvedDependency> accumulator,
        HashSet<string> visited)
    {
        var overridden = new HashSet<string>(StringComparer.Ordinal);
        if (!manifest.TryGetProperty("overrides", out var overrides) ||
            overrides.ValueKind != JsonValueKind.Object)
        {
            return overridden;
        }

        var lockedVersions = ReadLockedVersions(projectFilePath);
        foreach (var entry in overrides.EnumerateObject())
        {
            var expectedNamespace = lockedVersions.ContainsKey(entry.Name)
                ? entry.Name
                : PascalCase(entry.Name);
            if (!lockedVersions.TryGetValue(expectedNamespace, out var lockedVersion))
            {
                throw new InvalidOperationException(
                    $"ASH033: override '{entry.Name}' does not name a package in {Path.GetFileName(GetLockFilePath(projectFilePath))}. Run 'ashes restore'.");
            }

            var (dependency, dependencyManifest) = ResolveRootOverride(
                entry, projectDirectory, expectedNamespace, lockedVersion);
            overridden.Add(expectedNamespace);
            if (!visited.Add(dependency.ProjectDirectory))
            {
                continue;
            }

            accumulator.Add(dependency);

            using var dependencyDocument = JsonDocument.Parse(File.ReadAllText(dependencyManifest));
            CollectPathDependencies(
                dependencyDocument.RootElement,
                dependency.ProjectDirectory,
                "dependencies",
                isDev: false,
                accumulator,
                visited,
                [dependency.ProjectDirectory]);
        }

        return overridden;
    }

    private static (ResolvedDependency Dependency, string ManifestPath) ResolveRootOverride(
        JsonProperty entry, string projectDirectory, string expectedNamespace, string lockedVersion)
    {
        if (entry.Value.ValueKind != JsonValueKind.Object ||
            !entry.Value.TryGetProperty("path", out var pathElement) ||
            pathElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                $"ASH047: override '{entry.Name}' must be an object with a string 'path'.");
        }

        var dependencyDirectory = Path.GetFullPath(ResolvePath(projectDirectory, pathElement.GetString()!));
        var dependencyManifest = Path.Combine(dependencyDirectory, "ashes.json");
        if (!Directory.Exists(dependencyDirectory))
        {
            throw new InvalidOperationException(
                $"ASH030: override '{entry.Name}' path not found: {pathElement.GetString()}");
        }

        if (!File.Exists(dependencyManifest))
        {
            throw new InvalidOperationException(
                $"ASH031: override '{entry.Name}' at '{pathElement.GetString()}' is not an Ashes project (no ashes.json).");
        }

        var (actualNamespace, version, roots, entryFile) = ReadDependencyManifest(
            dependencyManifest, dependencyDirectory, namespaceOverride: null, entry.Name);
        if (!string.Equals(actualNamespace, expectedNamespace, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"ASH047: override '{entry.Name}' declares namespace '{actualNamespace}', expected '{expectedNamespace}'.");
        }

        if (version is null || !string.Equals(version, lockedVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"ASH047: override '{entry.Name}' must declare version '{lockedVersion}', but declares '{version ?? "<missing>"}'.");
        }

        return (new ResolvedDependency(entry.Name, actualNamespace, roots, dependencyDirectory, IsDev: false)
        {
            EntryFile = entryFile,
        }, dependencyManifest);
    }

    private static void CollectPathDependencies(
        JsonElement manifest, string manifestDir, string field, bool isDev,
        List<ResolvedDependency> accumulator, HashSet<string> visited, HashSet<string> chain)
    {
        if (!manifest.TryGetProperty(field, out var deps) || deps.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var entry in deps.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object ||
                !entry.Value.TryGetProperty("path", out var pathEl) ||
                pathEl.ValueKind != JsonValueKind.String)
            {
                // Non-path (registry/git) dependencies are resolved by the CLI via the lock/cache.
                continue;
            }

            var depDir = Path.GetFullPath(ResolvePath(manifestDir, pathEl.GetString()!));
            var depManifest = Path.Combine(depDir, "ashes.json");
            if (!Directory.Exists(depDir))
            {
                throw new InvalidOperationException(
                    $"ASH030: dependency '{entry.Name}' path not found: {pathEl.GetString()}");
            }

            if (!File.Exists(depManifest))
            {
                throw new InvalidOperationException(
                    $"ASH031: dependency '{entry.Name}' at '{pathEl.GetString()}' is not an Ashes project (no ashes.json).");
            }

            if (chain.Contains(depDir))
            {
                throw new InvalidOperationException(
                    $"ASH035: dependency cycle through '{entry.Name}' ({depDir}).");
            }

            if (!visited.Add(depDir))
            {
                continue; // already resolved via another route (diamond) — resolve once
            }

            var nsOverride = entry.Value.TryGetProperty("namespace", out var entryNs) && entryNs.ValueKind == JsonValueKind.String
                ? entryNs.GetString()
                : null;
            var (ns, _, roots, entryFile) = ReadDependencyManifest(depManifest, depDir, nsOverride, entry.Name);
            accumulator.Add(new ResolvedDependency(entry.Name, ns, roots, depDir, isDev) { EntryFile = entryFile });

            using var depDoc = JsonDocument.Parse(File.ReadAllText(depManifest));
            chain.Add(depDir);
            CollectPathDependencies(depDoc.RootElement, depDir, "dependencies", isDev, accumulator, visited, chain);
            chain.Remove(depDir);
        }
    }

    /// <summary>
    /// Add registry/git dependencies recorded in the selected manifest's lock file: each is materialized
    /// in the shared content-addressed cache and consumed exactly like a path dependency (its cached tree
    /// is the source root). A locked package missing from the cache means the project has not been restored.
    /// </summary>
    private static void AddLockedDependencies(
        string projectFilePath,
        List<ResolvedDependency> accumulator,
        IReadOnlySet<string> overriddenNamespaces)
    {
        string lockPath = GetLockFilePath(projectFilePath);
        if (!File.Exists(lockPath))
        {
            return;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(lockPath));
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("package", out var packages) ||
            packages.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var package in packages.EnumerateArray())
        {
            var ns = ReadString(package, "namespace");
            var version = ReadString(package, "version");
            var hash = ReadString(package, "hash");
            if (ns is null || version is null || hash is null)
            {
                continue;
            }

            if (overriddenNamespaces.Contains(ns))
            {
                continue;
            }

            var cacheDir = CachePathFor(ns, version, hash);
            var manifest = Path.Combine(cacheDir, "ashes.json");
            if (!File.Exists(manifest))
            {
                throw new InvalidOperationException(
                    $"ASH033: locked package '{ns}@{version}' is not in the cache. Run 'ashes restore'.");
            }

            var (_, _, roots, entryFile) = ReadDependencyManifest(manifest, cacheDir, ns, ns);
            accumulator.Add(new ResolvedDependency(ns, ns, roots, cacheDir, IsDev: false) { EntryFile = entryFile });
        }
    }

    private static IReadOnlyDictionary<string, string> ReadLockedVersions(string projectFilePath)
    {
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        var lockPath = GetLockFilePath(projectFilePath);
        if (!File.Exists(lockPath))
        {
            return versions;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(lockPath));
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("package", out var packages) ||
            packages.ValueKind != JsonValueKind.Array)
        {
            return versions;
        }

        foreach (var package in packages.EnumerateArray())
        {
            var packageNamespace = ReadString(package, "namespace");
            var packageVersion = ReadString(package, "version");
            if (packageNamespace is not null && packageVersion is not null)
            {
                versions[packageNamespace] = packageVersion;
            }
        }

        return versions;
    }

    /// <summary>
    /// Returns the lock file paired with <paramref name="projectFilePath"/>. Replacing the manifest's
    /// final extension preserves the default <c>ashes.json</c> → <c>ashes.lock</c> convention while
    /// keeping colocated manifests independent, for example
    /// <c>ashes-test.json</c> → <c>ashes-test.lock</c>.
    /// </summary>
    public static string GetLockFilePath(string projectFilePath)
    {
        return Path.ChangeExtension(Path.GetFullPath(projectFilePath), ".lock");
    }

    private static (string Namespace, string? Version, IReadOnlyList<string> Roots, string? EntryFile) ReadDependencyManifest(
        string manifestPath, string depDir, string? namespaceOverride, string depKey)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var manifest = doc.RootElement;

        var sourceRoots = ReadStringArray(manifest, "sourceRoots");
        if (sourceRoots.Count == 0)
        {
            sourceRoots.Add(".");
        }

        var ns = namespaceOverride
                 ?? ReadString(manifest, "namespace")
                 ?? PascalCase(ReadString(manifest, "name") ?? depKey);

        var entryValue = ReadString(manifest, "entry");
        var entryFile = entryValue is null ? null : ResolvePath(depDir, entryValue);

        return (ns, ReadString(manifest, "version"), sourceRoots.Select(x => ResolvePath(depDir, x)).ToList(), entryFile);
    }

    /// <summary>Root of the shared content-addressed package cache (<c>$XDG_CACHE_HOME/ashes</c>, else
    /// <c>~/.cache/ashes</c>). Both the CLI (writing) and the compiler (reading) compute paths the same way.</summary>
    public static string PackageCacheRoot()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        var baseDir = string.IsNullOrEmpty(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache")
            : xdg;
        return Path.Combine(baseDir, "ashes");
    }

    /// <summary>The cache directory for a specific package version: <c>cache/pkg/&lt;ns&gt;/&lt;version&gt;/&lt;hashkey&gt;</c>.</summary>
    public static string CachePathFor(string ns, string version, string hash)
    {
        var colon = hash.IndexOf(':', StringComparison.Ordinal);
        var key = colon >= 0 ? hash[(colon + 1)..] : hash;
        return Path.Combine(PackageCacheRoot(), "pkg", ns, version, key);
    }

    /// <summary>Map a package name to its default namespace (e.g. <c>json-parser</c> → <c>JsonParser</c>).</summary>
    public static string PascalCase(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length);
        var capitalize = true;
        foreach (var c in name)
        {
            if (c == '.')
            {
                builder.Append(c);
                capitalize = true;
                continue;
            }

            if (!char.IsLetterOrDigit(c))
            {
                capitalize = true;
                continue;
            }

            builder.Append(capitalize ? char.ToUpperInvariant(c) : c);
            capitalize = false;
        }

        return builder.Length == 0 ? name : builder.ToString();
    }

    /// <summary>
    /// Enforce the namespace discipline over resolved dependencies: no two dependencies may claim the
    /// same namespace (ASH009), and every module a dependency exports must live under its namespace —
    /// its own entry file excepted, since an entry is never an export (ASH008).
    /// </summary>
    private static void ValidateDependencyNamespaces(IReadOnlyList<ResolvedDependency> dependencies)
    {
        foreach (var group in dependencies.GroupBy(d => d.Namespace, StringComparer.Ordinal))
        {
            var owners = group.ToList();
            if (owners.Count > 1)
            {
                throw new InvalidOperationException(
                    $"ASH029: dependencies '{string.Join("', '", owners.Select(d => d.Name))}' both declare " +
                    $"namespace '{group.Key}'. A namespace may be owned by only one dependency.");
            }
        }

        foreach (var dep in dependencies)
        {
            foreach (var root in dep.SourceRoots)
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(root, "*.ash", SearchOption.AllDirectories))
                {
                    if (dep.EntryFile is not null &&
                        string.Equals(Path.GetFullPath(file), Path.GetFullPath(dep.EntryFile), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var module = ModuleNameFromPath(root, file);
                    var underNamespace = string.Equals(module, dep.Namespace, StringComparison.Ordinal)
                        || module.StartsWith(dep.Namespace + ".", StringComparison.Ordinal);
                    if (!underNamespace)
                    {
                        throw new InvalidOperationException(
                            $"ASH028: dependency '{dep.Name}' exports module '{module}' outside its namespace " +
                            $"'{dep.Namespace}'. A library's modules must live under its namespace directory.");
                    }
                }
            }
        }
    }

    private static string ModuleNameFromPath(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file);
        var withoutExtension = relative[..^".ash".Length];
        return withoutExtension.Replace(Path.DirectorySeparatorChar, '.').Replace('/', '.');
    }

    /// <summary>
    /// Splits <paramref name="source"/> into its leading import header and remaining body, returning
    /// the imported module names, alias table, selector imports, and the body with the header removed.
    /// <paramref name="displayPath"/> names the file in any diagnostics raised for malformed imports.
    /// </summary>
    public static ParsedImportHeader ParseImportHeader(string source, string displayPath)
    {
        var imports = new List<string>();
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var selectors = new List<ImportSelector>();
        var selectorLocalNames = new Dictionary<string, ImportSelector>(StringComparer.Ordinal);
        var sourceLines = new List<string>();
        using var reader = new StringReader(source);
        string? line;
        var lineIndex = 0;
        var inHeader = true;

        while ((line = reader.ReadLine()) is not null)
        {
            lineIndex++;
            var trimmed = line.TrimStart();
            var match = ImportLine.Match(line);
            if (inHeader && match.Success)
            {
                ProcessImportLine(match, displayPath, lineIndex, imports, aliases, selectors, selectorLocalNames);
                sourceLines.Add(string.Empty);
                continue;
            }

            if (inHeader && trimmed.StartsWith("import ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Invalid import syntax in {displayPath}:{lineIndex}. Expected 'import Foo', 'import Foo.Bar', 'import Foo.name [as alias]', or 'import Foo.Bar as Alias'.");
            }

            if (inHeader && (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal)))
            {
                sourceLines.Add(line);
                continue;
            }

            inHeader = false;
            sourceLines.Add(line);
        }

        // Realize the rename-based selectors (intrinsic value members like `Ashes.IO.print`, and
        // aliased type selectors) directly in the imports-stripped source. Single-file consumers (the
        // CLI/LSP standalone path) build their layout from `SourceWithoutImports` alone and do not
        // thread the selector list through, so without this an aliased intrinsic selector
        // (`import Ashes.IO.print as say`) would leave `say` undefined. Project mode and the
        // explicit-selector standalone path re-apply the same rewrite downstream, where it is a no-op.
        // Import and header lines are kept as blank lines rather than removed, so line numbers in the
        // stripped source (and everything derived from it: DWARF line info, span mapping) match the
        // original file.
        var sourceWithoutImports = ApplySelectorRenames(string.Join('\n', sourceLines), selectors);
        return new ParsedImportHeader(imports, sourceWithoutImports, aliases, selectors);
    }

    private static void ProcessImportLine(
        Match match,
        string displayPath,
        int lineIndex,
        List<string> imports,
        Dictionary<string, string> aliases,
        List<ImportSelector> selectors,
        Dictionary<string, ImportSelector> selectorLocalNames)
    {
        var modulePath = match.Groups[1].Value;
        var hasSelectorGroup = match.Groups[2].Success;
        var aliasGroup = match.Groups[3].Success ? match.Groups[3].Value : null;

        // A lowercase trailing segment (`import M.name`) is unambiguously a binding selector;
        // the module-path group only matches uppercase segments, so the parser leaves it here.
        // An uppercase trailing segment is consumed into the module path and is split into a
        // type selector later, against the known built-in/user module set (longest-path-wins).
        var selector = hasSelectorGroup
            ? new ImportSelector(modulePath, match.Groups[2].Value, aliasGroup ?? match.Groups[2].Value)
            : TrySplitBuiltinTypeSelector(modulePath, aliasGroup);

        if (selector is { } sel)
        {
            var local = sel.LocalName;
            if (ReservedKeywords.Contains(local))
            {
                throw new InvalidOperationException(
                    $"Invalid alias '{local}' in {displayPath}:{lineIndex}. Reserved keywords cannot be used as import aliases.");
            }

            if (selectorLocalNames.TryGetValue(local, out var existing)
                && (!string.Equals(existing.ModuleName, sel.ModuleName, StringComparison.Ordinal)
                    || !string.Equals(existing.ExportName, sel.ExportName, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(ConflictingSelectorMessage(local));
            }

            selectorLocalNames[local] = sel;
            selectors.Add(sel);
            imports.Add(sel.ModuleName);
            return;
        }

        imports.Add(modulePath);
        if (aliasGroup is not null)
        {
            AddWholeModuleAlias(aliasGroup, modulePath, displayPath, lineIndex, aliases);
        }
    }

    private static void AddWholeModuleAlias(
        string alias,
        string modulePath,
        string displayPath,
        int lineIndex,
        Dictionary<string, string> aliases)
    {
        if (ReservedKeywords.Contains(alias))
        {
            throw new InvalidOperationException(
                $"Invalid alias '{alias}' in {displayPath}:{lineIndex}. Reserved keywords cannot be used as import aliases.");
        }

        if (aliases.ContainsKey(alias))
        {
            throw new InvalidOperationException(
                $"Duplicate alias '{alias}' in {displayPath}:{lineIndex}. The alias '{alias}' is already mapped to '{aliases[alias]}'.");
        }

        aliases[alias] = modulePath;
    }

    /// <summary>
    /// Splits an uppercase-final import path (e.g. <c>Ashes.Some.Type</c>) into a built-in type
    /// selector when the path itself is not a built-in module but its parent is and exports the final
    /// segment. Returns <see langword="null"/> when the path should stay a whole-module import (it is a
    /// built-in module, or its parent is not a known built-in — the user-module case is split later in
    /// <see cref="BuildCompilationPlan"/> where user modules are resolvable).
    /// </summary>
    private static ImportSelector? TrySplitBuiltinTypeSelector(string modulePath, string? alias)
    {
        var lastDot = modulePath.LastIndexOf('.');
        if (lastDot < 0 || BuiltinRegistry.IsBuiltinModule(modulePath))
        {
            return null;
        }

        var parent = modulePath[..lastDot];
        var leaf = modulePath[(lastDot + 1)..];
        if (BuiltinRegistry.TryGetModuleExports(parent, out var exports) && exports.Contains(leaf))
        {
            return new ImportSelector(parent, leaf, alias ?? leaf);
        }

        return null;
    }

    /// <summary>
    /// Resolves all modules reachable from <paramref name="project"/>'s entry file, validates their
    /// imports, orders them so dependencies precede dependents, and returns the
    /// <see cref="ProjectCompilationPlan"/> the combined-source builders consume.
    /// </summary>
    public static ProjectCompilationPlan BuildCompilationPlan(AshesProject project)
    {
        if (BuiltinRegistry.IsReservedModuleNamespace(project.EntryModuleName))
        {
            throw new InvalidOperationException(
                "Module name 'Ashes' is reserved for the standard library.");
        }

        var searchRoots = project.SourceRoots
            .Concat(project.Include)
            .Concat(project.Dependencies.SelectMany(d => d.SourceRoots))
            .ToArray();
        var resolvedByModuleName = new Dictionary<string, ProjectModule>(StringComparer.Ordinal);
        var resolvedByPath = new Dictionary<string, ProjectModule>(StringComparer.OrdinalIgnoreCase);
        var states = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var traversal = new Stack<ProjectModule>();
        var ordered = new List<ProjectModule>();
        var importedStdModules = new HashSet<string>(StringComparer.Ordinal);
        // Inline submodules lifted from each file, keyed by the file's full path, ordered before the file.
        var inlineChildrenByPath = new Dictionary<string, List<ProjectModule>>(StringComparer.OrdinalIgnoreCase);

        var entryModule = LoadProjectModule(
            project.EntryModuleName, project.EntryPath, project, searchRoots,
            resolvedByModuleName, resolvedByPath, inlineChildrenByPath);
        VisitPlanImport(
            "Ashes.Trait", project, searchRoots, resolvedByModuleName, resolvedByPath,
            states, traversal, ordered, importedStdModules, inlineChildrenByPath);
        VisitModuleForPlan(
            entryModule, project, searchRoots, resolvedByModuleName, resolvedByPath,
            states, traversal, ordered, importedStdModules, inlineChildrenByPath);

        var mergedAliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var module in ordered)
        {
            foreach (var (alias, moduleName) in module.Aliases)
            {
                if (mergedAliases.TryGetValue(alias, out var existing) && !string.Equals(existing, moduleName, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Conflicting alias '{alias}' in module '{module.ModuleName}': maps to '{moduleName}', but already mapped to '{existing}'.");
                }

                mergedAliases.TryAdd(alias, moduleName);
            }
        }

        ProjectModule[] identifiedModules = ordered.Select(module => WithPackageIdentity(project, module)).ToArray();
        ProjectModule identifiedEntry = identifiedModules.Single(module =>
            string.Equals(module.FilePath, entryModule.FilePath, StringComparison.OrdinalIgnoreCase));
        return new ProjectCompilationPlan(project, identifiedModules, identifiedEntry, importedStdModules, mergedAliases);
    }

    private static ProjectModule WithPackageIdentity(AshesProject project, ProjectModule module)
    {
        if (module.FilePath.StartsWith("<std:", StringComparison.Ordinal))
        {
            return module with { PackageId = "ashes-core" };
        }

        string fullPath = module.FilePath.Split('#', 2)[0];
        foreach (ResolvedDependency dependency in project.Dependencies.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            if (dependency.SourceRoots.Any(root => IsPathWithin(fullPath, root)))
            {
                return module with { PackageId = dependency.Name };
            }
        }

        string projectIdentity = string.IsNullOrWhiteSpace(project.Name)
            ? Path.GetFullPath(project.ProjectFilePath)
            : project.Name;
        return module with { PackageId = projectIdentity };
    }

    private static bool IsPathWithin(string path, string directory)
    {
        string normalizedPath = Path.GetFullPath(path);
        string normalizedDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static void VisitModuleForPlan(
        ProjectModule module,
        AshesProject project,
        string[] searchRoots,
        Dictionary<string, ProjectModule> resolvedByModuleName,
        Dictionary<string, ProjectModule> resolvedByPath,
        Dictionary<string, int> states,
        Stack<ProjectModule> traversal,
        List<ProjectModule> ordered,
        HashSet<string> importedStdModules,
        Dictionary<string, List<ProjectModule>> inlineChildrenByPath)
    {
        if (IsModuleAlreadyPlanned(module, states, traversal))
        {
            return;
        }

        states[module.FilePath] = 1;
        traversal.Push(module);
        foreach (var import in module.Imports)
        {
            VisitPlanImport(
                import, project, searchRoots, resolvedByModuleName, resolvedByPath,
                states, traversal, ordered, importedStdModules, inlineChildrenByPath);
        }

        traversal.Pop();
        states[module.FilePath] = 2;
        AppendPlannedModule(module, states, ordered, inlineChildrenByPath);
    }

    private static bool IsModuleAlreadyPlanned(
        ProjectModule module, Dictionary<string, int> states, Stack<ProjectModule> traversal)
    {
        if (states.TryGetValue(module.FilePath, out var state))
        {
            if (state == 2)
            {
                return true;
            }

            if (state == 1)
            {
                var chain = traversal.Reverse()
                    .SkipWhile(x => !string.Equals(x.FilePath, module.FilePath, StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.ModuleName)
                    .Concat(new[] { module.ModuleName });
                throw new InvalidOperationException($"Import cycle detected: {string.Join(" -> ", chain)}");
            }
        }

        return false;
    }

    private static void VisitPlanImport(
        string import,
        AshesProject project,
        string[] searchRoots,
        Dictionary<string, ProjectModule> resolvedByModuleName,
        Dictionary<string, ProjectModule> resolvedByPath,
        Dictionary<string, int> states,
        Stack<ProjectModule> traversal,
        List<ProjectModule> ordered,
        HashSet<string> importedStdModules,
        Dictionary<string, List<ProjectModule>> inlineChildrenByPath)
    {
        if (string.Equals(import, "Ashes", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Module name 'Ashes' is reserved for the standard library.");
        }

        if (IsStdModule(import))
        {
            importedStdModules.Add(import);
            if (TryLoadStandardLibraryModule(import, out var stdModule))
            {
                VisitModuleForPlan(
                    stdModule, project, searchRoots, resolvedByModuleName, resolvedByPath,
                    states, traversal, ordered, importedStdModules, inlineChildrenByPath);
            }

            return;
        }

        if (import.StartsWith("Ashes.", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unknown standard library module '{import}'. Known modules: {string.Join(", ", KnownStdModules)}.");
        }

        var dependency = ResolveImportForPlan(
            import, project, searchRoots, resolvedByModuleName, resolvedByPath, inlineChildrenByPath);
        ValidateInlineImportSurface(import, traversal.Peek().ModuleName, resolvedByModuleName);
        VisitModuleForPlan(
            dependency, project, searchRoots, resolvedByModuleName, resolvedByPath,
            states, traversal, ordered, importedStdModules, inlineChildrenByPath);
    }

    private static void ValidateInlineImportSurface(
        string importedModule,
        string importingModule,
        IReadOnlyDictionary<string, ProjectModule> modules)
    {
        for (int separator = importedModule.LastIndexOf('.'); separator > 0; separator = importedModule.LastIndexOf('.', separator - 1))
        {
            string parentName = importedModule[..separator];
            string childPath = importedModule[(separator + 1)..];
            string childName = childPath.Split('.')[0];
            if (!modules.TryGetValue(parentName, out ProjectModule? parent)
                || string.Equals(importingModule, parentName, StringComparison.Ordinal)
                || importingModule.StartsWith(parentName + ".", StringComparison.Ordinal))
            {
                continue;
            }

            var diagnostics = new Diagnostics();
            Program program = new Parser(parent.Source, diagnostics).ParseProgram();
            ExportDecl? declaration = program.Items.OfType<TopLevelItem.Export>().Select(item => item.Decl).SingleOrDefault();
            if (declaration is not null
                && !declaration.Items.OfType<ExportItem.Module>().Any(module =>
                    string.Equals(module.Name, childName, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"Module '{parentName}' does not export '{childName}'.");
            }
        }
    }

    private static void AppendPlannedModule(
        ProjectModule module,
        Dictionary<string, int> states,
        List<ProjectModule> ordered,
        Dictionary<string, List<ProjectModule>> inlineChildrenByPath)
    {
        // Lifted inline submodules are declared before their file so the file's qualified
        // references to them resolve in the combined source.
        if (inlineChildrenByPath.TryGetValue(module.FilePath, out var inlineChildren))
        {
            foreach (var child in inlineChildren)
            {
                if (states.TryGetValue(child.FilePath, out var childState) && childState == 2)
                {
                    continue;
                }

                states[child.FilePath] = 2;
                ordered.Add(child);
            }
        }

        ordered.Add(module);
    }

    private static ProjectModule ResolveImportForPlan(
        string moduleName,
        AshesProject project,
        string[] searchRoots,
        Dictionary<string, ProjectModule> resolvedByModuleName,
        Dictionary<string, ProjectModule> resolvedByPath,
        Dictionary<string, List<ProjectModule>> inlineChildrenByPath)
    {
        if (BuiltinRegistry.IsReservedModuleNamespace(moduleName))
        {
            throw new InvalidOperationException(
                "Module name 'Ashes' is reserved for the standard library.");
        }

        if (resolvedByModuleName.TryGetValue(moduleName, out var existing))
        {
            return existing;
        }

        var moduleRelativePath = GetModuleRelativePath(moduleName);
        var projectMatches = GetExistingModuleCandidates(searchRoots, moduleRelativePath);
        if (projectMatches.Count > 1)
        {
            throw new InvalidOperationException(
                $"Ambiguous module resolution for '{moduleName}'. Found multiple project modules: {string.Join(", ", projectMatches)}");
        }

        if (projectMatches.Count == 1)
        {
            var module = LoadProjectModule(
                moduleName, projectMatches[0], project, searchRoots,
                resolvedByModuleName, resolvedByPath, inlineChildrenByPath);
            resolvedByModuleName[moduleName] = module;
            return module;
        }

        var shippedLibraryPath = GetShippedLibraryModulePath(moduleRelativePath);
        if (shippedLibraryPath is not null)
        {
            var module = LoadProjectModule(
                moduleName, shippedLibraryPath, project, searchRoots,
                resolvedByModuleName, resolvedByPath, inlineChildrenByPath);
            resolvedByModuleName[moduleName] = module;
            return module;
        }

        // The path may name an inline submodule of an enclosing file (`import Geom.Vec` where
        // `Vec` is `module Vec` inside `Geom.ash`). Load the nearest enclosing file, which lifts
        // and registers its inline submodules, then retry.
        if (TryResolveInlineSubmodule(
                moduleName, project, searchRoots, resolvedByModuleName, resolvedByPath, inlineChildrenByPath) is { } inlineResolved)
        {
            return inlineResolved;
        }

        throw new InvalidOperationException(BuildMissingModuleMessage(moduleName, searchRoots, moduleRelativePath));
    }

    private static ProjectModule? TryResolveInlineSubmodule(
        string moduleName,
        AshesProject project,
        string[] searchRoots,
        Dictionary<string, ProjectModule> resolvedByModuleName,
        Dictionary<string, ProjectModule> resolvedByPath,
        Dictionary<string, List<ProjectModule>> inlineChildrenByPath)
    {
        for (var dot = moduleName.LastIndexOf('.'); dot > 0; dot = moduleName.LastIndexOf('.', dot - 1))
        {
            var enclosing = moduleName[..dot];
            var enclosingMatches = GetExistingModuleCandidates(searchRoots, GetModuleRelativePath(enclosing));
            if (enclosingMatches.Count == 1)
            {
                LoadProjectModule(
                    enclosing, enclosingMatches[0], project, searchRoots,
                    resolvedByModuleName, resolvedByPath, inlineChildrenByPath);
                if (resolvedByModuleName.TryGetValue(moduleName, out var inlineResolved))
                {
                    return inlineResolved;
                }
            }
        }

        return null;
    }

    private static ProjectModule LoadProjectModule(
        string moduleName,
        string filePath,
        AshesProject project,
        string[] searchRoots,
        Dictionary<string, ProjectModule> resolvedByModuleName,
        Dictionary<string, ProjectModule> resolvedByPath,
        Dictionary<string, List<ProjectModule>> inlineChildrenByPath)
    {
        var fullPath = Path.GetFullPath(filePath);
        if (resolvedByPath.TryGetValue(fullPath, out var existing))
        {
            return existing;
        }

        var (imports, source, aliases, selectors) = ParseImports(fullPath);

        // Lift inline `module Name = ...` blocks. The entry file's inline modules keep bare names
        // (nothing imports the entry across files); every other file prefixes them with its own
        // module name so `File.Inner` is cross-file addressable and promotion is transparent.
        var inlineScope = string.Equals(moduleName, project.EntryModuleName, StringComparison.Ordinal) ? "" : moduleName;
        var (outerSource, inlineModules) = ExpandInlineModules(source, inlineScope, fullPath);
        source = outerSource;
        var children = LiftInlineModules(inlineModules, fullPath, searchRoots, resolvedByModuleName);

        var aliasMap = new Dictionary<string, string>(aliases, StringComparer.Ordinal);
        (imports, selectors) = NormalizeTypeSelectors(
            imports, selectors, aliasMap,
            name => IsResolvableProjectModule(name, searchRoots, resolvedByModuleName));
        source = ApplySelectorRenames(source, selectors);
        var module = new ProjectModule(moduleName, fullPath, source, imports, aliasMap, selectors);
        resolvedByPath[fullPath] = module;
        inlineChildrenByPath[fullPath] = children;
        if (!resolvedByModuleName.ContainsKey(moduleName))
        {
            resolvedByModuleName[moduleName] = module;
        }

        return module;
    }

    private static List<ProjectModule> LiftInlineModules(
        IReadOnlyList<InlineModuleInfo> inlineModules,
        string fullPath,
        string[] searchRoots,
        Dictionary<string, ProjectModule> resolvedByModuleName)
    {
        var children = new List<ProjectModule>();
        foreach (var inline in inlineModules)
        {
            if (BuiltinRegistry.IsReservedModuleNamespace(inline.ModuleName)
                || inline.ModuleName.StartsWith("Ashes.", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"[ASH023] Inline module '{inline.ModuleName}' shadows a reserved 'Ashes.*' path ({fullPath}).");
            }

            var candidates = GetExistingModuleCandidates(searchRoots, GetModuleRelativePath(inline.ModuleName));
            if (candidates.Count > 0)
            {
                throw new InvalidOperationException(
                    $"[ASH022] Module path '{inline.ModuleName}' is defined by both an inline module and a file ({candidates[0]}).");
            }

            var childModule = new ProjectModule(
                inline.ModuleName,
                $"{fullPath}#{inline.ModuleName}",
                inline.Source,
                [],
                new Dictionary<string, string>(StringComparer.Ordinal),
                []);
            children.Add(childModule);
            resolvedByModuleName[inline.ModuleName] = childModule;
        }

        return children;
    }

    private static bool IsResolvableProjectModule(
        string name,
        string[] searchRoots,
        Dictionary<string, ProjectModule> resolvedByModuleName)
    {
        if (IsStdModule(name) || BuiltinRegistry.IsBuiltinModule(name))
        {
            return true;
        }

        if (BuiltinRegistry.IsReservedModuleNamespace(name))
        {
            return false;
        }

        var relativePath = GetModuleRelativePath(name);
        if (GetExistingModuleCandidates(searchRoots, relativePath).Count > 0
            || GetShippedLibraryModulePath(relativePath) is not null)
        {
            return true;
        }

        // A dotted path may name an inline submodule of an enclosing file. Already-registered
        // inline modules resolve directly; otherwise scan the nearest enclosing file's inline
        // blocks so `import Geom.Vec` reads as a whole-module import rather than a type selector.
        return resolvedByModuleName.ContainsKey(name) || IsInlineSubmodulePath(name, searchRoots);
    }

    /// <summary>
    /// Whether <paramref name="name"/> is an inline submodule declared in some enclosing project
    /// file — i.e. the nearest file whose path prefixes <paramref name="name"/> lifts a module of
    /// exactly this composed path. A lightweight text scan (no full compile).
    /// </summary>
    private static bool IsInlineSubmodulePath(string name, IReadOnlyList<string> searchRoots)
    {
        for (var dot = name.LastIndexOf('.'); dot > 0; dot = name.LastIndexOf('.', dot - 1))
        {
            var enclosing = name[..dot];
            var matches = GetExistingModuleCandidates(searchRoots, GetModuleRelativePath(enclosing));
            if (matches.Count != 1)
            {
                continue;
            }

            var parsed = ParseImportHeader(SourceTextIndex.ReadUtf8File(matches[0]), matches[0]);
            var (_, inlineModules) = ExpandInlineModules(parsed.SourceWithoutImports, enclosing, matches[0]);
            if (inlineModules.Any(m => string.Equals(m.ModuleName, name, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves the module-path-vs-type-selector ambiguity for uppercase-final imports that the regex
    /// folded into the module path (e.g. <c>import M.Type</c>). Using the longest-matching-module rule:
    /// a whole-module import whose path is not itself a resolvable module but whose parent is becomes a
    /// type selector of that parent. Lowercase binding selectors were already split during parsing.
    /// Re-validates the combined selector set for unqualified-name conflicts (ASH016).
    /// </summary>
    private static (IReadOnlyList<string> Imports, IReadOnlyList<ImportSelector> Selectors) NormalizeTypeSelectors(
        IReadOnlyList<string> imports,
        IReadOnlyList<ImportSelector> selectors,
        IDictionary<string, string> aliases,
        Func<string, bool> isResolvableModule)
    {
        var newImports = new List<string>();
        var newSelectors = new List<ImportSelector>(selectors);

        foreach (var import in imports)
        {
            var lastDot = import.LastIndexOf('.');
            if (lastDot > 0 && !isResolvableModule(import))
            {
                var parent = import[..lastDot];
                var leaf = import[(lastDot + 1)..];
                if (isResolvableModule(parent))
                {
                    // An uppercase final segment the regex folded into the module path is a type selector
                    // when the full path is not itself a resolvable module but its parent is. For the
                    // aliased form (`import Parent.Type as T`) the parser could not tell selector from
                    // module qualifier, so it tentatively recorded a whole-module alias `T -> Parent.Type`.
                    // Claim that alias here as the selector's unqualified local name and drop it from the
                    // module-qualifier set, so it drives the aliased-type rename rather than dangling as a
                    // bogus qualifier (which never resolves).
                    var localName = leaf;
                    var aliasKey = aliases
                        .FirstOrDefault(entry => string.Equals(entry.Value, import, StringComparison.Ordinal))
                        .Key;
                    if (aliasKey is not null)
                    {
                        localName = aliasKey;
                        aliases.Remove(aliasKey);
                    }

                    newSelectors.Add(new ImportSelector(parent, leaf, localName));
                    if (!newImports.Contains(parent, StringComparer.Ordinal))
                    {
                        newImports.Add(parent);
                    }

                    continue;
                }
            }

            if (!newImports.Contains(import, StringComparer.Ordinal))
            {
                newImports.Add(import);
            }
        }

        ValidateSelectorConflicts(newSelectors);
        return (newImports, newSelectors);
    }

    /// <summary>
    /// Emits the <c>ASH016</c> diagnostic when two selectors bring different exports into scope under
    /// the same unqualified name. Importing the same export twice is harmless and allowed.
    /// </summary>
    private static void ValidateSelectorConflicts(IReadOnlyList<ImportSelector> selectors)
    {
        var byLocalName = new Dictionary<string, ImportSelector>(StringComparer.Ordinal);
        foreach (var selector in selectors)
        {
            if (byLocalName.TryGetValue(selector.LocalName, out var existing)
                && (!string.Equals(existing.ModuleName, selector.ModuleName, StringComparison.Ordinal)
                    || !string.Equals(existing.ExportName, selector.ExportName, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(ConflictingSelectorMessage(selector.LocalName));
            }

            byLocalName[selector.LocalName] = selector;
        }
    }

    /// <summary>Returns true when <paramref name="moduleName"/> names a standard-library module.</summary>
    public static bool IsStdModule(string moduleName)
    {
        return KnownStdModules.Contains(moduleName);
    }

    /// <summary>
    /// Convenience wrapper over <see cref="BuildCompilationLayout(ProjectCompilationPlan, string)"/>
    /// that returns only the stitched combined source text for <paramref name="plan"/>.
    /// </summary>
    public static string BuildCompilationSource(ProjectCompilationPlan plan)
    {
        return BuildCompilationLayout(plan).Source;
    }

    /// <summary>
    /// Stitches every module of <paramref name="plan"/> into one combined compilation unit, lifting any
    /// inline modules in the entry source, and returns the source together with the offset maps that
    /// translate diagnostics back to their originating files. <paramref name="entrySourceOverride"/>
    /// substitutes replacement text for the entry module when provided (e.g. for the language server).
    /// </summary>
    public static CombinedCompilationLayout BuildCompilationLayout(ProjectCompilationPlan plan, string? entrySourceOverride = null)
    {
        if (entrySourceOverride is null || !ContainsInlineModule(entrySourceOverride))
        {
            return BuildCompilationLayoutCore(plan.OrderedModules, plan.EntryModule, entrySourceOverride);
        }

        var (entryOuter, entryInlineModules) = ExpandInlineModules(entrySourceOverride, "", plan.EntryModule.FilePath);
        var entryInlinePathPrefix = Path.GetFullPath(plan.EntryModule.FilePath) + "#";
        var orderedModules = new List<ProjectModule>(plan.OrderedModules.Count + entryInlineModules.Count);
        var entryModule = plan.EntryModule with
        {
            Source = ApplySelectorRenames(entryOuter, plan.EntryModule.Selectors)
        };

        foreach (var module in plan.OrderedModules)
        {
            if (module.FilePath.StartsWith(entryInlinePathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(module.FilePath, plan.EntryModule.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var inline in entryInlineModules)
                {
                    if (orderedModules.Any(existing => string.Equals(existing.ModuleName, inline.ModuleName, StringComparison.Ordinal)))
                    {
                        throw new InvalidOperationException(
                            $"[ASH022] Module path '{inline.ModuleName}' is defined by both an inline module and a file ({plan.EntryModule.FilePath}).");
                    }

                    orderedModules.Add(new ProjectModule(
                        inline.ModuleName,
                        $"{Path.GetFullPath(plan.EntryModule.FilePath)}#{inline.ModuleName}",
                        inline.Source,
                        [],
                        new Dictionary<string, string>(StringComparer.Ordinal),
                        [])
                    { PackageId = plan.EntryModule.PackageId });
                }

                orderedModules.Add(entryModule);
                continue;
            }

            orderedModules.Add(module);
        }

        return BuildCompilationLayoutCore(orderedModules, entryModule, entryModule.Source);
    }

    /// <summary>
    /// Builds a combined compilation layout for a single standalone file (no <c>ashes.json</c>),
    /// pulling in only the standard-library modules named by <paramref name="importNames"/>.
    /// <paramref name="sourceWithoutImports"/> is the file body with its import header already removed,
    /// <paramref name="entryFilePath"/> labels it in diagnostics, and <paramref name="selectors"/>
    /// carries any selector imports to apply.
    /// </summary>
    public static CombinedCompilationLayout BuildStandaloneCompilationLayout(
        string sourceWithoutImports,
        IReadOnlyList<string> importNames,
        string entryFilePath = "<memory>",
        IReadOnlyList<ImportSelector>? selectors = null)
    {
        var orderedModules = new List<ProjectModule>();
        var seenModules = new HashSet<string>(StringComparer.Ordinal);
        var states = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var traversal = new Stack<ProjectModule>();

        var entrySelectors = selectors ?? [];

        // Lift inline `module Name = ...` blocks out of the entry source into synthetic submodules,
        // ordered before the entry, and use the rewritten outer as the entry source. The entry file's
        // own inline modules keep their bare names (nothing imports the entry across files), so
        // same-file imports/aliases/selectors of them need no path rewriting.
        var (entryOuter, entryInlineModules) = ExpandInlineModules(sourceWithoutImports, "", entryFilePath);
        var inlineModuleNames = new HashSet<string>(entryInlineModules.Select(m => m.ModuleName), StringComparer.Ordinal);
        foreach (var inline in entryInlineModules)
        {
            orderedModules.Add(new ProjectModule(
                inline.ModuleName,
                $"{entryFilePath}#{inline.ModuleName}",
                inline.Source,
                [],
                new Dictionary<string, string>(StringComparer.Ordinal),
                [])
            { PackageId = "standalone" });
        }

        var entryModule = new ProjectModule(
            "Main",
            entryFilePath,
            ApplySelectorRenames(entryOuter, entrySelectors),
            importNames.ToList(),
            new Dictionary<string, string>(StringComparer.Ordinal),
            entrySelectors)
        { PackageId = "standalone" };
        orderedModules.Add(entryModule);

        if (TryLoadStandardLibraryModule("Ashes.Trait", out ProjectModule traitModule))
        {
            VisitStandaloneModule(traitModule, states, traversal, seenModules, orderedModules);
        }
        ResolveStandaloneImports(importNames, inlineModuleNames, states, traversal, seenModules, orderedModules);

        // Use the entry module's selector-rewritten source (intrinsic and aliased-type selectors are
        // realized by in-place renaming) rather than the raw imports-stripped source, so single-file
        // selector imports take effect in the entry expression just as they do in project mode.
        return BuildCompilationLayoutCore(orderedModules, entryModule, entryModule.Source);
    }

    private static void ResolveStandaloneImports(
        IReadOnlyList<string> importNames,
        HashSet<string> inlineModuleNames,
        Dictionary<string, int> states,
        Stack<ProjectModule> traversal,
        HashSet<string> seenModules,
        List<ProjectModule> orderedModules)
    {
        foreach (var importName in importNames)
        {
            if (IsStdModule(importName))
            {
                if (TryLoadStandardLibraryModule(importName, out var stdModule))
                {
                    VisitStandaloneModule(stdModule, states, traversal, seenModules, orderedModules);
                }

                continue;
            }

            // Same-file inline modules are already in orderedModules; a same-file import of one is
            // satisfied here rather than requiring project mode.
            if (inlineModuleNames.Contains(importName))
            {
                continue;
            }

            if (importName.StartsWith("Ashes.", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Unknown standard library module '{importName}'. Known modules: {string.Join(", ", KnownStdModules)}.");
            }

            throw new InvalidOperationException(
                $"Could not resolve module '{importName}'. User-defined module imports require project mode via ashes.json.");
        }
    }

    private static void VisitStandaloneModule(
        ProjectModule module,
        Dictionary<string, int> states,
        Stack<ProjectModule> traversal,
        HashSet<string> seenModules,
        List<ProjectModule> orderedModules)
    {
        if (IsModuleAlreadyPlanned(module, states, traversal))
        {
            return;
        }

        states[module.FilePath] = 1;
        traversal.Push(module);
        foreach (var import in module.Imports)
        {
            if (!IsStdModule(import))
            {
                throw new InvalidOperationException(
                    $"Could not resolve module '{import}'. User-defined module imports require project mode via ashes.json.");
            }

            if (TryLoadStandardLibraryModule(import, out var dependency))
            {
                VisitStandaloneModule(dependency, states, traversal, seenModules, orderedModules);
            }
        }

        traversal.Pop();
        states[module.FilePath] = 2;
        if (seenModules.Add(module.ModuleName))
        {
            orderedModules.Insert(orderedModules.Count - 1, module);
        }
    }

    /// <summary>A diagnostic mapped from combined-source coordinates back to a user-visible location:
    /// the owning file, and — when the region is position-preserving — a span rebased into the entry
    /// file's original text. <see cref="HasPosition"/> is false for spans inside stitched
    /// (reconstructed) module regions, where only the owning file is recoverable.</summary>
    public readonly record struct MappedDiagnostic(DiagnosticEntry Entry, string FilePath, bool HasPosition);

    /// <summary>
    /// Maps diagnostic spans from combined-source offsets (what compilation ran on) back to the entry
    /// file's original text, so errors render at the coordinates the user sees. The entry region of
    /// the combined source is line/column-preserving with respect to the original file (imports and
    /// hoisted declarations are blanked keeping newlines; alias preludes overwrite blank lines), so
    /// entry-region spans map by line/column rather than direct offset. Hoisted entry declarations map
    /// exactly via <see cref="CombinedCompilationLayout.EntryTypeDeclFragments"/>. Spans inside a
    /// stitched (reconstructed) module region cannot be positioned — they are attributed to the
    /// owning file with <c>HasPosition = false</c>.
    /// </summary>
    public static IReadOnlyList<MappedDiagnostic> MapDiagnosticsToOriginal(
        CombinedCompilationLayout layout,
        IReadOnlyList<DiagnosticEntry> entries,
        string entryFilePath,
        string entryOriginalSource,
        string entryStrippedSource)
    {
        var combined = layout.Source;
        var originalLineStarts = BuildLineStarts(entryOriginalSource);
        SourceTextIndex combinedIndex = new(combined);
        SourceTextIndex originalIndex = new(entryOriginalSource);
        var results = new List<MappedDiagnostic>(entries.Count);

        foreach (var entry in entries)
        {
            var start = combinedIndex.ToUtf16Offset(entry.Span.Start);
            var length = Math.Max(entry.Span.End - entry.Span.Start, 0);

            if (start >= layout.EntryOffset)
            {
                var (line, column) = OffsetToLineColumn(combined, layout.EntryOffset, start);
                var mappedStringStart = LineColumnToOffset(entryOriginalSource, originalLineStarts, line, column);
                var mappedStart = originalIndex.ToUtf8Offset(mappedStringStart);
                results.Add(new MappedDiagnostic(
                    entry with { Span = TextSpan.FromBounds(mappedStart, Math.Min(mappedStart + length, originalIndex.Utf8Length)) },
                    entryFilePath,
                    HasPosition: true));
                continue;
            }

            if (layout.EntryTypeDeclFragments is { } fragments
                && TryMapTypeDeclFragmentDiagnostic(
                    entry, start, length, fragments, entryStrippedSource, entryOriginalSource,
                    originalLineStarts, entryFilePath, results))
            {
                continue;
            }

            var filePath = entryFilePath;
            foreach (var (path, regionStart, regionEnd) in layout.ModuleOffsets)
            {
                if (start >= regionStart && start < regionEnd)
                {
                    filePath = path;
                    break;
                }
            }

            results.Add(new MappedDiagnostic(entry, filePath, HasPosition: false));
        }

        return results;
    }

    private static bool TryMapTypeDeclFragmentDiagnostic(
        DiagnosticEntry entry,
        int start,
        int length,
        IReadOnlyList<(int CombinedStart, int OriginalStart, int Length)> fragments,
        string entryStrippedSource,
        string entryOriginalSource,
        int[] originalLineStarts,
        string entryFilePath,
        List<MappedDiagnostic> results)
    {
        foreach (var (fragmentStart, originalStart, fragmentLength) in fragments)
        {
            if (start >= fragmentStart && start < fragmentStart + fragmentLength)
            {
                // Fragment offsets are relative to the imports-stripped source, which shares
                // the original file's line structure but not its direct offsets — hop via
                // line/column.
                var strippedOffset = Math.Min(originalStart + (start - fragmentStart), entryStrippedSource.Length);
                var (line, column) = OffsetToLineColumn(entryStrippedSource, 0, strippedOffset);
                var mappedStringStart = LineColumnToOffset(entryOriginalSource, originalLineStarts, line, column);
                SourceTextIndex originalIndex = new(entryOriginalSource);
                var mappedStart = originalIndex.ToUtf8Offset(mappedStringStart);
                results.Add(new MappedDiagnostic(
                    entry with { Span = TextSpan.FromBounds(mappedStart, Math.Min(mappedStart + length, originalIndex.Utf8Length)) },
                    entryFilePath,
                    HasPosition: true));
                return true;
            }
        }

        return false;
    }

    private static int[] BuildLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return [.. starts];
    }

    /// <summary>1-based line/column of <paramref name="offset"/> within <paramref name="text"/>,
    /// counting from <paramref name="regionStart"/> (the region's first character is line 1, column 1).</summary>
    private static (int Line, int Column) OffsetToLineColumn(string text, int regionStart, int offset)
    {
        var line = 1;
        var lineStart = regionStart;
        var limit = Math.Min(offset, text.Length);
        for (var i = regionStart; i < limit; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }

        return (line, limit - lineStart + 1);
    }

    /// <summary>Offset of 1-based (<paramref name="line"/>, <paramref name="column"/>) in
    /// <paramref name="text"/>, clamping the line to the available range and the column to the
    /// line's length (so a mapped span never points past the rendered line).</summary>
    private static int LineColumnToOffset(string text, int[] lineStarts, int line, int column)
    {
        var lineIndex = Math.Clamp(line - 1, 0, lineStarts.Length - 1);
        var lineStart = lineStarts[lineIndex];
        var lineEnd = lineIndex + 1 < lineStarts.Length ? lineStarts[lineIndex + 1] - 1 : text.Length;
        return Math.Clamp(lineStart + column - 1, lineStart, Math.Max(lineEnd, lineStart));
    }

    /// <summary>
    /// Turns a dotted module name into an identifier-safe binding name by replacing each <c>.</c> with
    /// an underscore, so a module can be referenced as a single mangled symbol.
    /// </summary>
    public static string SanitizeModuleBindingName(string moduleName)
    {
        return moduleName.Replace('.', '_');
    }

    /// <summary>
    /// Infers the single name a module exports by inspecting its trailing expression: a
    /// <c>let x = ... in x</c> / <c>let recursive x = ... in x</c> spine or a bare <c>x</c> yields
    /// <c>x</c>. Returns null when the source fails to parse or has no single inferable export.
    /// </summary>
    public static string? TryInferExportName(string source)
    {
        var diag = new Diagnostics();
        var program = new Parser(source, diag).ParseProgram();
        if (diag.StructuredErrors.Count > 0)
        {
            return null;
        }

        return program.Body switch
        {
            Expr.Let letExpr when letExpr.Body is Expr.Var bodyVar && string.Equals(letExpr.Name, bodyVar.Name, StringComparison.Ordinal) => letExpr.Name,
            Expr.LetRecursive letRecursiveExpr when letRecursiveExpr.Body is Expr.Var bodyVar && string.Equals(letRecursiveExpr.Name, bodyVar.Name, StringComparison.Ordinal) => letRecursiveExpr.Name,
            Expr.Var varExpr => varExpr.Name,
            _ => null
        };
    }

    private static CombinedCompilationLayout BuildCompilationLayoutCore(
        IReadOnlyList<ProjectModule> orderedModules,
        ProjectModule entryModule,
        string? entrySourceOverride)
    {
        Dictionary<string, ModuleSourceShape> shapes = BuildModuleShapes(orderedModules, entryModule, entrySourceOverride);

        (IReadOnlyDictionary<string, IReadOnlyList<string>> exportedNames,
            IReadOnlyDictionary<string, string> exportAnnotations) = BuildExportMetadata(orderedModules, shapes);

        var entryShape = shapes[entryModule.ModuleName];
        var nonEntryModules = orderedModules.Where(module =>
            !string.Equals(module.FilePath, entryModule.FilePath, StringComparison.OrdinalIgnoreCase)).ToList();

        var prefix = new StringBuilder();

        // Track module offset regions as content is appended
        var moduleOffsets = new List<(string FilePath, int StartOffset, int EndOffset)>();

        int entryTypeDeclarationsStart = AppendHoistedTypeDeclarations(
            prefix, moduleOffsets, entryModule, entryShape, nonEntryModules, shapes);
        IReadOnlyList<(int CombinedStart, int OriginalStart, int Length)>? entryTypeDeclFragments =
            OffsetEntryTypeDeclFragments(entryShape, entryTypeDeclarationsStart);

        var constructorModules = BuildConstructorModuleNames(orderedModules, entryModule, entrySourceOverride);
        var functionSourceNames = BuildFunctionSourceNames(orderedModules, entryModule, shapes);
        IReadOnlyDictionary<string, ModuleProvenance> moduleProvenanceByPath =
            BuildModuleProvenance(orderedModules);

        var legacyBindingEmitted = AppendModuleBindingPrefixes(
            prefix,
            moduleOffsets,
            nonEntryModules,
            shapes,
            exportedNames,
            exportAnnotations);

        // A hoisted flat declaration (a flat `let` value, or a `provide` whose implementation is an
        // expression) ends with a newline, not an `in`. Without a following legacy nested-let binding
        // to open the trailing body, the parenthesized entry expression would be absorbed as an
        // application argument of that last value (e.g. `given (y) -> x - y` swallowing `(entry)` as
        // `y(entry)`), so introduce a boundary binding whose `in` makes the entry a proper let body.
        // The `let` boundary is safe because flat-value/provider parsing suppresses `let` as an
        // argument. This is needed whenever any module content precedes the entry with no legacy `in`.
        bool appendFlatEntryBare = ApplyEntryBoundary(prefix, entryShape, legacyBindingEmitted);

        var entryExpression = BuildEntryExpression(
            entryModule,
            entryShape,
            exportedNames,
            exportAnnotations);
        return ComposeEntryLayout(
            entryModule,
            entryShape,
            entryExpression,
            prefix,
            moduleOffsets,
            constructorModules,
            functionSourceNames,
            moduleProvenanceByPath,
            entryTypeDeclFragments,
            appendFlatEntryBare);
    }

    private static IReadOnlyList<(int CombinedStart, int OriginalStart, int Length)>?
        OffsetEntryTypeDeclFragments(ModuleSourceShape entryShape, int combinedStart)
        => entryShape.TypeDeclFragments?
            .Select(fragment => (
                combinedStart + fragment.FragmentStart,
                fragment.OriginalStart,
                fragment.Length))
            .ToArray();

    private static Dictionary<string, ModuleSourceShape> BuildModuleShapes(
        IReadOnlyList<ProjectModule> modules,
        ProjectModule entryModule,
        string? entrySourceOverride)
    {
        var shapes = new Dictionary<string, ModuleSourceShape>(StringComparer.Ordinal);
        foreach (ProjectModule module in modules)
        {
            string source = string.Equals(module.FilePath, entryModule.FilePath, StringComparison.OrdinalIgnoreCase)
                ? entrySourceOverride ?? module.Source
                : module.Source;
            IReadOnlySet<string> directModules = modules
                .Select(candidate => candidate.ModuleName)
                .Where(name => IsDirectChildModule(module.ModuleName, name))
                .Select(name => name[(module.ModuleName.Length + 1)..])
                .ToHashSet(StringComparer.Ordinal);
            shapes[module.ModuleName] = ShapeModuleSource(source, module.ModuleName, directModules);
        }

        return shapes;
    }

    private static bool IsDirectChildModule(string parent, string candidate)
    {
        if (!candidate.StartsWith(parent + ".", StringComparison.Ordinal))
        {
            return false;
        }

        return candidate.AsSpan(parent.Length + 1).IndexOf('.') < 0;
    }

    private static (IReadOnlyDictionary<string, IReadOnlyList<string>> Names,
        IReadOnlyDictionary<string, string> Annotations) BuildExportMetadata(
        IReadOnlyList<ProjectModule> modules,
        IReadOnlyDictionary<string, ModuleSourceShape> shapes)
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> names = modules.ToDictionary(
            module => module.ModuleName,
            module => GetExportNames(shapes[module.ModuleName]),
            StringComparer.Ordinal);
        ValidateNestedModuleExports(modules, shapes);
        ValidateSelectorExports(modules, names);
        return (names, BuildExportAnnotationLookup(modules, shapes));
    }

    private static void ValidateNestedModuleExports(
        IReadOnlyList<ProjectModule> modules,
        IReadOnlyDictionary<string, ModuleSourceShape> shapes)
    {
        foreach (ProjectModule importer in modules)
        {
            IEnumerable<string> importedNames = importer.Imports.Concat(importer.Selectors.Select(selector => selector.ModuleName));
            foreach (string importedName in importedNames.Distinct(StringComparer.Ordinal))
            {
                int separator = importedName.LastIndexOf('.');
                if (separator < 0)
                {
                    continue;
                }

                string parent = importedName[..separator];
                string child = importedName[(separator + 1)..];
                if (!shapes.TryGetValue(parent, out ModuleSourceShape? parentShape)
                    || parentShape.ExportPolicy is not { IsExplicit: true } policy
                    || policy.Modules.Contains(child)
                    || string.Equals(importer.ModuleName, parent, StringComparison.Ordinal)
                    || importer.ModuleName.StartsWith(parent + ".", StringComparison.Ordinal))
                {
                    continue;
                }

                throw new InvalidOperationException(
                    $"Module '{parent}' does not export '{child}'.");
            }
        }
    }

    private static bool ApplyEntryBoundary(
        StringBuilder prefix,
        ModuleSourceShape entryShape,
        bool legacyBindingEmitted)
    {
        bool moduleContentPrecedesEntry = prefix.Length > entryShape.TypeDeclarationsSource.Length;
        bool appendFlatEntryBare = moduleContentPrecedesEntry
            && !legacyBindingEmitted
            && entryShape.IsFlat
            && entryShape.TopLevelBindings.Count > 0;
        if (moduleContentPrecedesEntry && !legacyBindingEmitted && !appendFlatEntryBare)
        {
            prefix.Append("let __ashes_module_boundary = 0 in ");
        }

        return appendFlatEntryBare;
    }

    private static IReadOnlyDictionary<string, ModuleProvenance> BuildModuleProvenance(
        IReadOnlyList<ProjectModule> modules)
    {
        return modules
            .OrderBy(module => module.ModuleName, StringComparer.Ordinal)
            .ToDictionary(
                module => module.FilePath,
                module => new ModuleProvenance(module.PackageId, module.ModuleName),
                StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, SourceFunctionName> BuildFunctionSourceNames(
        IReadOnlyList<ProjectModule> orderedModules,
        ProjectModule entryModule,
        IReadOnlyDictionary<string, ModuleSourceShape> shapes)
    {
        var result = new Dictionary<string, SourceFunctionName>(StringComparer.Ordinal);
        foreach (ProjectModule module in orderedModules)
        {
            bool isEntry = string.Equals(
                module.FilePath,
                entryModule.FilePath,
                StringComparison.OrdinalIgnoreCase);
            string moduleBindingName = SanitizeModuleBindingName(module.ModuleName);
            foreach (ModuleBindingGroup group in shapes[module.ModuleName].TopLevelBindings)
            {
                foreach (ModuleBindingFragment binding in group.Bindings)
                {
                    string compilerName = isEntry
                        ? binding.Name
                        : $"{moduleBindingName}_{binding.Name}";
                    result[compilerName] = new SourceFunctionName(
                        binding.Name,
                        $"{module.ModuleName}.{binding.Name}");
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Maps each module in <paramref name="orderedModules"/> to the ADT constructor names its own
    /// <c>type</c> declarations introduce. Lowering uses this to scope a qualified constructor
    /// reference (<c>alias.Ctor</c>) to the module the alias actually resolves to, rather than
    /// resolving to any same-named constructor declared elsewhere — constructors themselves stay
    /// globally registered (types are hoisted unqualified, per <see cref="AppendHoistedTypeDeclarations"/>),
    /// this only records which module a name may be qualified through.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlySet<string>> BuildConstructorModuleNames(
        IReadOnlyList<ProjectModule> orderedModules,
        ProjectModule entryModule,
        string? entrySourceOverride)
    {
        var result = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var module in orderedModules)
        {
            var source = string.Equals(module.FilePath, entryModule.FilePath, StringComparison.OrdinalIgnoreCase)
                ? entrySourceOverride ?? module.Source
                : module.Source;
            CollectAllExportNames(source, out var ctorNames);
            result[module.ModuleName] = ctorNames;
        }

        return result;
    }

    private static int AppendHoistedTypeDeclarations(
        StringBuilder prefix,
        List<(string FilePath, int StartOffset, int EndOffset)> moduleOffsets,
        ProjectModule entryModule,
        ModuleSourceShape entryShape,
        IReadOnlyList<ProjectModule> nonEntryModules,
        Dictionary<string, ModuleSourceShape> shapes)
    {
        // The plan is dependency-first, and Ashes type declarations are sequential. Hoist imported
        // module types in that same order so entry and dependent declarations can resolve them.
        foreach (var module in nonEntryModules)
        {
            var typeDecls = shapes[module.ModuleName].TypeDeclarationsSource;
            if (typeDecls.Length > 0)
            {
                var start = prefix.Length;
                prefix.Append(typeDecls);
                EnsureEndsWithNewline(prefix);
                moduleOffsets.Add((module.FilePath, start, prefix.Length));
            }
        }

        int entryTypeDeclarationsStart = prefix.Length;
        if (entryShape.TypeDeclarationsSource.Length > 0)
        {
            int start = prefix.Length;
            prefix.Append(entryShape.TypeDeclarationsSource);
            EnsureEndsWithNewline(prefix);
            moduleOffsets.Add((entryModule.FilePath, start, prefix.Length));
        }

        return entryTypeDeclarationsStart;
    }

    private static void EnsureEndsWithNewline(StringBuilder source)
    {
        if (source.Length > 0 && source[^1] != '\n')
        {
            source.Append('\n');
        }
    }

    private static bool AppendModuleBindingPrefixes(
        StringBuilder prefix,
        List<(string FilePath, int StartOffset, int EndOffset)> moduleOffsets,
        IReadOnlyList<ProjectModule> nonEntryModules,
        Dictionary<string, ModuleSourceShape> shapes,
        IReadOnlyDictionary<string, IReadOnlyList<string>> exportedNames,
        IReadOnlyDictionary<string, string> exportAnnotations)
    {
        var usedBindingNames = new Dictionary<string, string>(StringComparer.Ordinal);

        // Flat modules contribute genuine top-level declarations, which must precede the legacy
        // nested-let pyramid (everything after the first pyramid `let ... in` is the trailing body).
        foreach (var module in nonEntryModules.Where(module => shapes[module.ModuleName].IsFlat))
        {
            var start = prefix.Length;
            prefix.Append(BuildModuleBindingPrefix(
                module,
                shapes[module.ModuleName],
                exportedNames,
                exportAnnotations,
                usedBindingNames,
                flat: true));
            if (prefix.Length > start)
            {
                moduleOffsets.Add((module.FilePath, start, prefix.Length));
            }
        }

        var legacyBindingEmitted = false;
        foreach (var module in nonEntryModules.Where(module => !shapes[module.ModuleName].IsFlat))
        {
            var start = prefix.Length;
            prefix.Append(BuildModuleBindingPrefix(
                module,
                shapes[module.ModuleName],
                exportedNames,
                exportAnnotations,
                usedBindingNames));
            if (prefix.Length > start)
            {
                moduleOffsets.Add((module.FilePath, start, prefix.Length));
                legacyBindingEmitted = true;
            }
        }

        return legacyBindingEmitted;
    }

    private static CombinedCompilationLayout ComposeEntryLayout(
        ProjectModule entryModule,
        ModuleSourceShape entryShape,
        string entryExpression,
        StringBuilder prefix,
        List<(string FilePath, int StartOffset, int EndOffset)> moduleOffsets,
        IReadOnlyDictionary<string, IReadOnlySet<string>> constructorModules,
        IReadOnlyDictionary<string, SourceFunctionName> functionSourceNames,
        IReadOnlyDictionary<string, ModuleProvenance> moduleProvenanceByPath,
        IReadOnlyList<(int CombinedStart, int OriginalStart, int Length)>? entryTypeDeclFragments,
        bool appendFlatEntryBare)
    {
        if (appendFlatEntryBare)
        {
            return AppendBareEntryLayout(
                entryModule,
                entryShape,
                entryExpression,
                prefix,
                moduleOffsets,
                constructorModules,
                functionSourceNames,
                moduleProvenanceByPath,
                entryTypeDeclFragments);
        }

        if (prefix.Length > entryShape.TypeDeclarationsSource.Length)
        {
            return AppendParenthesizedEntryLayout(
                entryModule,
                entryShape,
                entryExpression,
                prefix,
                moduleOffsets,
                constructorModules,
                functionSourceNames,
                moduleProvenanceByPath,
                entryTypeDeclFragments);
        }

        if (entryShape.TypeDeclarationsSource.Length == 0 && prefix.Length == 0)
        {
            moduleOffsets.Add((entryModule.FilePath, 0, entryExpression.Length));
            return CreateCombinedCompilationLayout(
                entryExpression,
                0,
                0,
                moduleOffsets,
                entryTypeDeclFragments: null,
                constructorModules,
                functionSourceNames,
                moduleProvenanceByPath);
        }

        // Only the entry's own (hoisted) type declarations precede the body: append it bare — the
        // combined source is then an ordinary flat program (type declarations, then the body), and
        // the type-declaration region keeps its exact original offsets for span mapping.
        return AppendBareEntryLayout(
            entryModule,
            entryShape,
            entryExpression,
            prefix,
            moduleOffsets,
            constructorModules,
            functionSourceNames,
            moduleProvenanceByPath,
            entryTypeDeclFragments);
    }

    private static CombinedCompilationLayout AppendParenthesizedEntryLayout(
        ProjectModule entryModule,
        ModuleSourceShape entryShape,
        string entryExpression,
        StringBuilder prefix,
        List<(string FilePath, int StartOffset, int EndOffset)> moduleOffsets,
        IReadOnlyDictionary<string, IReadOnlySet<string>> constructorModules,
        IReadOnlyDictionary<string, SourceFunctionName> functionSourceNames,
        IReadOnlyDictionary<string, ModuleProvenance> moduleProvenanceByPath,
        IReadOnlyList<(int CombinedStart, int OriginalStart, int Length)>? entryTypeDeclFragments)
    {
        // A declarations-only entry needs an inert trailing value because its result is discarded.
        entryExpression = EnsureTrailingEntryExpression(entryShape, entryExpression);
        prefix.Append('(');
        int entryOffset = prefix.Length;
        prefix.Append(entryExpression);
        prefix.Append(')');
        moduleOffsets.Add((entryModule.FilePath, entryOffset, entryOffset + entryExpression.Length));
        return CreateCombinedCompilationLayout(
            prefix.ToString(),
            entryOffset,
            entryShape.TypeDeclarationsSource.Length,
            moduleOffsets,
            entryTypeDeclFragments,
            constructorModules,
            functionSourceNames,
            moduleProvenanceByPath);
    }

    private static CombinedCompilationLayout AppendBareEntryLayout(
        ProjectModule entryModule,
        ModuleSourceShape entryShape,
        string entryExpression,
        StringBuilder prefix,
        List<(string FilePath, int StartOffset, int EndOffset)> moduleOffsets,
        IReadOnlyDictionary<string, IReadOnlySet<string>> constructorModules,
        IReadOnlyDictionary<string, SourceFunctionName> functionSourceNames,
        IReadOnlyDictionary<string, ModuleProvenance> moduleProvenanceByPath,
        IReadOnlyList<(int CombinedStart, int OriginalStart, int Length)>? entryTypeDeclFragments)
    {
        int offset = prefix.Length;
        prefix.Append(entryExpression);
        moduleOffsets.Add((entryModule.FilePath, offset, prefix.Length));
        return CreateCombinedCompilationLayout(
            prefix.ToString(),
            offset,
            entryShape.TypeDeclarationsSource.Length,
            moduleOffsets,
            entryTypeDeclFragments,
            constructorModules,
            functionSourceNames,
            moduleProvenanceByPath);
    }

    private static string EnsureTrailingEntryExpression(
        ModuleSourceShape entryShape,
        string entryExpression)
    {
        if (entryShape.HasTrailingExpression && !string.IsNullOrWhiteSpace(entryExpression))
        {
            return entryExpression;
        }

        return string.IsNullOrWhiteSpace(entryExpression) ? "0" : entryExpression + "\n0";
    }

    private static CombinedCompilationLayout CreateCombinedCompilationLayout(
        string source,
        int entryOffset,
        int bodyStart,
        IReadOnlyList<(string FilePath, int StartOffset, int EndOffset)> moduleOffsets,
        IReadOnlyList<(int CombinedStart, int OriginalStart, int Length)>? entryTypeDeclFragments,
        IReadOnlyDictionary<string, IReadOnlySet<string>> constructorModules,
        IReadOnlyDictionary<string, SourceFunctionName> functionSourceNames,
        IReadOnlyDictionary<string, ModuleProvenance> moduleProvenanceByPath)
        => new(
            source,
            entryOffset,
            bodyStart,
            moduleOffsets,
            entryTypeDeclFragments,
            constructorModules,
            functionSourceNames,
            moduleProvenanceByPath);

    /// <summary>
    /// Verifies every selector import names an export the target module actually provides, resolving
    /// built-in modules (via the built-in export tables) and user/std modules (via the flat export set
    /// computed from the real parser) through the same check. A selector to an unknown export is a
    /// clear compile error rather than a silent dangling reference at lowering time.
    /// </summary>
    private static void ValidateSelectorExports(
        IReadOnlyList<ProjectModule> orderedModules,
        IReadOnlyDictionary<string, IReadOnlyList<string>> exportedNames)
    {
        var modulesByName = new Dictionary<string, ProjectModule>(StringComparer.Ordinal);
        foreach (var module in orderedModules)
        {
            modulesByName.TryAdd(module.ModuleName, module);
        }

        // The flat export set used by the value stitcher (`exportedNames`) covers `let` bindings only;
        // type exports are validated against the full export set (lets + types) computed per module.
        var fullExports = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

        foreach (var module in orderedModules)
        {
            foreach (var selector in module.Selectors)
            {
                if (BuiltinRegistry.TryGetModuleExports(selector.ModuleName, out var builtinExports)
                    && builtinExports.Contains(selector.ExportName))
                {
                    continue;
                }

                if (!fullExports.TryGetValue(selector.ModuleName, out var moduleExports))
                {
                    moduleExports = modulesByName.TryGetValue(selector.ModuleName, out var target)
                        ? CollectAllExportNames(target.Source)
                        : new HashSet<string>(StringComparer.Ordinal);
                    fullExports[selector.ModuleName] = moduleExports;
                }

                if (!moduleExports.Contains(selector.ExportName))
                {
                    throw new InvalidOperationException(
                        $"Import selector 'import {selector.ModuleName}.{selector.ExportName}' refers to an unknown export; module '{selector.ModuleName}' does not export '{selector.ExportName}'.");
                }
            }
        }
    }

    /// <summary>
    /// Whether an export name is a value binding (lowercase or underscore lead) as opposed to a type
    /// or constructor (uppercase). Drives whether a selector needs a value-level alias.
    /// </summary>
    private static bool IsValueExportName(string name) =>
        name.Length > 0 && (char.IsLower(name[0]) || name[0] == '_');

    /// <summary>
    /// Whether a selector targets a built-in <em>intrinsic</em> member (e.g. <c>Ashes.IO.print</c>).
    /// Intrinsics must be called directly and cannot be bound as first-class values, so an intrinsic
    /// selector is realized by renaming its unqualified occurrences to the qualified call form rather
    /// than by injecting a <c>let</c> alias (which the lowering would reject).
    /// </summary>
    private static bool IsBuiltinIntrinsicMember(string moduleName, string exportName) =>
        BuiltinRegistry.TryGetModule(moduleName, out var module) && module.Members.ContainsKey(exportName);

    /// <summary>
    /// Rewrites a module's source to realize the selectors that are implemented by in-place identifier
    /// renaming rather than by injected value aliases:
    /// <list type="bullet">
    /// <item>Intrinsic value selectors (e.g. <c>Ashes.IO.print</c>) — the unqualified name becomes the
    /// qualified reference <c>Module.member</c>, which the lowering resolves as a direct intrinsic call
    /// (an intrinsic cannot be bound as a first-class <c>let</c> value).</item>
    /// <item>Type selectors with an alias (<c>import M.Type as T</c>) — type declarations are hoisted to
    /// the top of the combined source under their <em>real</em> name, so the unqualified alias is
    /// rewritten back to the real type name <c>Type</c>. The bare form (<c>import M.Type</c>) needs no
    /// rewrite: the unqualified name already <em>is</em> the real name.</item>
    /// </list>
    /// Non-intrinsic value selectors are untouched here (they are handled as injected value aliases
    /// during stitching by <see cref="BuildVisibleAliases"/>).
    /// </summary>
    private static string ApplySelectorRenames(string source, IReadOnlyList<ImportSelector> selectors)
    {
        var renames = new List<KeyValuePair<string, string>>();
        foreach (var selector in selectors)
        {
            if (IsBuiltinIntrinsicMember(selector.ModuleName, selector.ExportName))
            {
                renames.Add(new KeyValuePair<string, string>(
                    selector.LocalName,
                    $"{selector.ModuleName}.{selector.ExportName}"));
            }
            else if (!IsValueExportName(selector.ExportName)
                && !string.Equals(selector.LocalName, selector.ExportName, StringComparison.Ordinal))
            {
                renames.Add(new KeyValuePair<string, string>(
                    selector.LocalName,
                    selector.ExportName));
            }
        }

        return renames.Count == 0 ? source : ApplyAliasesByRenaming(source, renames);
    }

    /// <summary>
    /// The full export set of a module — top-level <c>let</c>/<c>let rec</c> bindings <em>and</em>
    /// <c>type</c> and <c>trait</c> declarations, plus legacy pyramid bindings — used to validate selector imports
    /// (mirrors the built-in export tables). Returns an empty set for sources the parser rejects.
    /// </summary>
    private static IReadOnlySet<string> CollectAllExportNames(string source) => CollectAllExportNames(source, out _);

    /// <summary>
    /// As <see cref="CollectAllExportNames(string)"/>, additionally returning the ADT constructor
    /// names introduced by the module's own <c>type</c> declarations (e.g. <c>Wrap</c> for
    /// <c>type Box = | Wrap(Int)</c>) — used to scope a qualified constructor reference
    /// (<c>alias.Ctor</c>) to the module that actually declares it.
    /// </summary>
    private static IReadOnlySet<string> CollectAllExportNames(string source, out IReadOnlySet<string> constructorNames)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var ctorNames = new HashSet<string>(StringComparer.Ordinal);
        constructorNames = ctorNames;
        var diag = new Diagnostics();
        var program = new Parser(source, diag).ParseProgram();
        if (diag.StructuredErrors.Count > 0)
        {
            return names;
        }

        if (TryCollectExplicitExportNames(program, out IReadOnlySet<string> explicitNames, out IReadOnlySet<string> explicitConstructors))
        {
            constructorNames = explicitConstructors;
            return explicitNames;
        }

        foreach (var item in program.Items)
        {
            CollectCompatibilityTopLevelExport(item, names, ctorNames);
        }

        for (var expr = program.Body; expr is not null;)
        {
            switch (expr)
            {
                case Expr.Let letExpr:
                    names.Add(letExpr.Name);
                    expr = letExpr.Body;
                    break;
                case Expr.LetRecursive letRecursiveExpr:
                    names.Add(letRecursiveExpr.Name);
                    expr = letRecursiveExpr.Body;
                    break;
                default:
                    expr = null;
                    break;
            }
        }

        return names;
    }

    private static void CollectCompatibilityTopLevelExport(
        TopLevelItem item,
        HashSet<string> names,
        HashSet<string> constructors)
    {
        switch (item)
        {
            case TopLevelItem.LetDecl letDecl:
                names.Add(letDecl.Name);
                break;
            case TopLevelItem.RecursiveGroup recursiveGroup:
                names.UnionWith(recursiveGroup.Bindings.Select(binding => binding.Name));
                break;
            case TopLevelItem.Type typeDecl:
                names.Add(typeDecl.Decl.Name);
                constructors.UnionWith(typeDecl.Decl.Constructors.Select(constructor => constructor.Name));
                break;
            case TopLevelItem.TypeAlias aliasDecl:
                names.Add(aliasDecl.Decl.Name);
                break;
            case TopLevelItem.ZeroCostType zeroCostType:
                names.Add(zeroCostType.Decl.Name);
                constructors.Add(zeroCostType.Decl.Constructor.Name);
                break;
            case TopLevelItem.Trait traitDecl:
                names.Add(traitDecl.Decl.Name);
                break;
        }
    }

    private static bool TryCollectExplicitExportNames(
        Program program,
        out IReadOnlySet<string> names,
        out IReadOnlySet<string> constructors)
    {
        ExportDecl? explicitInterface = program.Items
            .OfType<TopLevelItem.Export>()
            .Select(item => item.Decl)
            .SingleOrDefault();
        if (explicitInterface is null)
        {
            names = new HashSet<string>(StringComparer.Ordinal);
            constructors = new HashSet<string>(StringComparer.Ordinal);
            return false;
        }

        var exportedNames = new HashSet<string>(StringComparer.Ordinal);
        var exportedConstructors = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, TypeDecl> declaredTypes = CollectDeclaredTypeShapes(program);
        foreach (ExportItem export in explicitInterface.Items)
        {
            CollectExplicitExport(export, declaredTypes, exportedNames, exportedConstructors);
        }

        names = exportedNames;
        constructors = exportedConstructors;
        return true;
    }

    private static IReadOnlyDictionary<string, TypeDecl> CollectDeclaredTypeShapes(Program program)
    {
        var declarations = new Dictionary<string, TypeDecl>(StringComparer.Ordinal);
        foreach (TopLevelItem item in program.Items)
        {
            switch (item)
            {
                case TopLevelItem.Type type:
                    declarations[type.Decl.Name] = type.Decl;
                    break;
                case TopLevelItem.TypeAlias alias:
                    declarations[alias.Decl.Name] = new TypeDecl(
                        alias.Decl.Name,
                        alias.Decl.TypeParameters,
                        []);
                    break;
                case TopLevelItem.ZeroCostType zeroCostType:
                    declarations[zeroCostType.Decl.Name] = new TypeDecl(
                        zeroCostType.Decl.Name,
                        zeroCostType.Decl.TypeParameters,
                        [zeroCostType.Decl.Constructor]);
                    break;
            }
        }

        return declarations;
    }

    private static void CollectExplicitExport(
        ExportItem export,
        IReadOnlyDictionary<string, TypeDecl> declaredTypes,
        HashSet<string> names,
        HashSet<string> constructors)
    {
        if (export is ExportItem.Value value)
        {
            names.Add(value.Name);
            return;
        }

        if (export is not ExportItem.Type type)
        {
            return;
        }

        names.Add(type.Name);
        if (!declaredTypes.TryGetValue(type.Name, out TypeDecl? declaration))
        {
            return;
        }

        IEnumerable<string> selected = type.Constructors switch
        {
            ExportConstructors.All => declaration.Constructors.Select(constructor => constructor.Name),
            ExportConstructors.Selected constructorSelection => constructorSelection.Names,
            _ => [],
        };
        constructors.UnionWith(selected);
    }

    private static string BuildEntryExpression(
        ProjectModule entryModule,
        ModuleSourceShape entryShape,
        IReadOnlyDictionary<string, IReadOnlyList<string>> exportedNames,
        IReadOnlyDictionary<string, string> exportAnnotations)
    {
        var referencedNames = CollectReferencedNames(entryShape.RawExpressionSource);
        HashSet<string> entryBindings = entryShape.TopLevelBindings
            .SelectMany(group => group.Bindings)
            .Select(binding => binding.Name)
            .ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<KeyValuePair<string, string>> aliases = BuildVisibleAliases(
                entryModule,
                [],
                [],
                referencedNames,
                exportedNames)
            .Where(alias => !entryBindings.Contains(alias.Key))
            .ToArray();

        // A flat top-level entry parses as a flat-declaration block (`decl decl ... trailingExpr`,
        // folded into nested lets by the parser inside the stitched parentheses). Wrapping it in the
        // ordinary `let alias = value in <body>` prelude would make the alias body an ordinary
        // expression, so the following flat declarations would no longer be folded and parsing fails.
        // Emit the alias prelude as flat declarations instead so the whole entry stays one flat block.
        return ApplyEntryAliases(
            entryShape.RawExpressionSource,
            aliases,
            entryShape.IsFlat,
            exportAnnotations);
    }

    /// <summary>
    /// Applies the alias prelude to the entry source while keeping its line numbers aligned with
    /// the original file: the import header upstream is blanked rather than removed, so the alias
    /// declarations are written into those leading blank lines instead of being prepended. Debug
    /// line info and span mapping for the entry file stay 1:1 with what the user sees. Falls back
    /// to the prepending strategies when there are not enough blank lines to fill.
    /// </summary>
    private static string ApplyEntryAliases(
        string source,
        IReadOnlyList<KeyValuePair<string, string>> aliases,
        bool flat,
        IReadOnlyDictionary<string, string> exportAnnotations)
    {
        if (aliases.Count == 0)
        {
            return source.TrimEnd();
        }

        var lines = source.Split('\n');
        var blankCount = 0;
        while (blankCount < lines.Length && string.IsNullOrWhiteSpace(lines[blankCount]))
        {
            blankCount++;
        }

        if (aliases.Count > blankCount)
        {
            return flat
                ? ApplyAliasesAsFlatDeclarations(source, aliases, exportAnnotations)
                : ApplyAliases(source, aliases, exportAnnotations);
        }

        for (var index = 0; index < aliases.Count; index++)
        {
            // A flat entry block folds bare `let` declarations; a nested-let entry needs the
            // `in` so the chain wraps the trailing expression.
            string declaration = FormatAliasBinding(aliases[index], exportAnnotations);
            lines[index] = flat ? declaration : declaration + " in";
        }

        return string.Join('\n', lines).TrimEnd();
    }

    /// <summary>
    /// Prepends the alias prelude as flat top-level declarations (no <c>in</c>) rather than wrapping
    /// the source in nested <c>let ... in</c> bindings. Used for the flat top-level entry, whose body
    /// is itself a flat-declaration block: the synthetic alias bindings join that block so the parser
    /// folds them together with the entry's own declarations and trailing expression.
    /// </summary>
    private static string ApplyAliasesAsFlatDeclarations(
        string source,
        IReadOnlyList<KeyValuePair<string, string>> aliases,
        IReadOnlyDictionary<string, string> exportAnnotations)
    {
        var trimmed = source.Trim();
        if (aliases.Count == 0)
        {
            return trimmed;
        }

        var builder = new StringBuilder();
        foreach (var alias in aliases)
        {
            builder.Append(FormatAliasBinding(alias, exportAnnotations)).Append('\n');
        }

        builder.Append(trimmed);
        return builder.ToString();
    }

    private static string BuildModuleBindingPrefix(
        ProjectModule module,
        ModuleSourceShape shape,
        IReadOnlyDictionary<string, IReadOnlyList<string>> exportedNames,
        IReadOnlyDictionary<string, string> exportAnnotations,
        IDictionary<string, string> usedBindingNames,
        bool flat = false)
    {
        var moduleBindingName = SanitizeModuleBindingName(module.ModuleName);
        if (usedBindingNames.TryGetValue(moduleBindingName, out var existingModuleName))
        {
            throw new InvalidOperationException(
                $"Module name collision for generated binding '{moduleBindingName}': '{existingModuleName}' and '{module.ModuleName}'.");
        }

        usedBindingNames[moduleBindingName] = module.ModuleName;

        var prefix = new StringBuilder();
        var availableLocalBindings = new List<string>();

        foreach (var group in shape.TopLevelBindings)
        {
            AppendModuleBindingGroup(
                prefix, module, group, moduleBindingName, flat,
                exportedNames, exportAnnotations, usedBindingNames, availableLocalBindings);
        }

        // Flat modules drop their trailing expression and bind no whole-module value, so the
        // module-value binding is emitted only when the legacy nested form left an expression body.
        if (!string.IsNullOrWhiteSpace(shape.ExpressionBodySource))
        {
            var bodyReferencedNames = CollectReferencedNames(shape.ExpressionBodySource);
            var bodyAliases = BuildVisibleAliases(
                module,
                availableLocalBindings,
                [],
                bodyReferencedNames,
                exportedNames);

            prefix.Append("let ")
                .Append(moduleBindingName)
                .Append(" = (")
                .Append(ApplyAliases(shape.ExpressionBodySource, bodyAliases, exportAnnotations))
                .Append(") in ");
        }

        return prefix.ToString();
    }

    private static void AppendModuleBindingGroup(
        StringBuilder prefix,
        ProjectModule module,
        ModuleBindingGroup group,
        string moduleBindingName,
        bool flat,
        IReadOnlyDictionary<string, IReadOnlyList<string>> exportedNames,
        IReadOnlyDictionary<string, string> exportAnnotations,
        IDictionary<string, string> usedBindingNames,
        List<string> availableLocalBindings)
    {
        foreach (var binding in group.Bindings)
        {
            var generatedBindingName = $"{moduleBindingName}_{binding.Name}";
            if (usedBindingNames.TryGetValue(generatedBindingName, out var existingModuleName))
            {
                throw new InvalidOperationException(
                    $"Generated export binding collision for '{generatedBindingName}': '{existingModuleName}' and '{module.ModuleName}'.");
            }

            usedBindingNames[generatedBindingName] = module.ModuleName;
        }

        // Within a `let rec ... and ...` group every member is visible to the others, so each
        // member value resolves sibling references to the group's generated names.
        string[] recursiveBindings = group.Bindings
            .Where(binding => group.IsRecursiveGroup || binding.IsRecursive)
            .Select(binding => binding.Name)
            .ToArray();

        prefix.Append("let ");
        if (group.IsRecursiveGroup)
        {
            prefix.Append("recursive ");
        }

        for (var i = 0; i < group.Bindings.Count; i++)
        {
            AppendModuleGroupBinding(
                prefix, module, group.Bindings[i], moduleBindingName, flat,
                recursiveBindings, availableLocalBindings, exportedNames, exportAnnotations, appendSeparator: i > 0);
        }

        // Flat modules are stitched as genuine top-level declarations (no `in`): a `let rec ... and
        // ...` group cannot be expressed in the nested expression pyramid the legacy form uses, and
        // top-level declarations keep mutual recursion intact while staying visible to the body.
        prefix.Append(flat ? "\n" : " in ");

        foreach (var binding in group.Bindings)
        {
            availableLocalBindings.Add(binding.Name);
        }
    }

    private static void AppendModuleGroupBinding(
        StringBuilder prefix,
        ProjectModule module,
        ModuleBindingFragment binding,
        string moduleBindingName,
        bool flat,
        IReadOnlyList<string> recursiveBindings,
        IReadOnlyList<string> availableLocalBindings,
        IReadOnlyDictionary<string, IReadOnlyList<string>> exportedNames,
        IReadOnlyDictionary<string, string> exportAnnotations,
        bool appendSeparator)
    {
        var referencedNames = CollectReferencedNames(binding.ValueSource);
        var aliases = BuildVisibleAliases(
            module,
            availableLocalBindings,
            recursiveBindings,
            referencedNames,
            exportedNames);

        if (appendSeparator)
        {
            prefix.Append(" and ");
        }

        // Flat modules are stitched as genuine top-level declarations, so resolve every visible
        // alias by renaming identifiers in place. Wrapping a value in `let alias = generated in ...`
        // changes the identity of exported functions and, for inferred constrained functions, can
        // detach the alias scheme from the hidden trait-evidence ABI. The legacy nested form keeps
        // its wrapping path because it is already represented as an expression pyramid.
        var renderedValue = flat
            ? ApplyAliasesByRenaming(binding.ValueSource, aliases)
            : ApplyAliases(binding.ValueSource, aliases, exportAnnotations);

        prefix.Append($"{moduleBindingName}_{binding.Name}");
        // Keep a `needs {Cap(a)}` annotation on the stitched binding: it is what marks a generic
        // dictionary-passing function, so dropping it would leave an exported generic capability
        // function unresolved. Capability and type-variable names in the row are program-global,
        // so they need no alias rewriting.
        if (binding.Annotation is { Length: > 0 } annotation)
        {
            prefix.Append(" : ").Append(annotation);
        }

        prefix.Append(" = (")
            .Append(renderedValue)
            .Append(')');
    }

    private static List<KeyValuePair<string, string>> BuildVisibleAliases(
        ProjectModule module,
        IReadOnlyList<string> availableLocalBindings,
        IReadOnlyList<string> currentRecursiveBindings,
        ReferencedNames referencedNames,
        IReadOnlyDictionary<string, IReadOnlyList<string>> exportedNames)
    {
        var aliases = new List<KeyValuePair<string, string>>();
        var localNames = new HashSet<string>(StringComparer.Ordinal);
        var referencedVariables = referencedNames.Variables;
        var referencedQualifiedReferences = referencedNames.QualifiedReferences;

        AddLocalBindingAliases(module, availableLocalBindings, currentRecursiveBindings, referencedVariables, localNames, aliases);
        AddSelectorAliases(module, referencedVariables, localNames, aliases);
        AddImportedExportAliases(module, referencedVariables, referencedQualifiedReferences, exportedNames, localNames, aliases);

        return aliases;
    }

    private static void AddLocalBindingAliases(
        ProjectModule module,
        IReadOnlyList<string> availableLocalBindings,
        IReadOnlyList<string> currentRecursiveBindings,
        IReadOnlySet<string> referencedVariables,
        HashSet<string> localNames,
        List<KeyValuePair<string, string>> aliases)
    {
        foreach (var recursiveBinding in currentRecursiveBindings)
        {
            if (referencedVariables.Contains(recursiveBinding) && localNames.Add(recursiveBinding))
            {
                aliases.Add(new KeyValuePair<string, string>(
                    recursiveBinding,
                    $"{SanitizeModuleBindingName(module.ModuleName)}_{recursiveBinding}"));
            }
        }

        foreach (var bindingName in availableLocalBindings.Reverse())
        {
            if (!referencedVariables.Contains(bindingName) || !localNames.Add(bindingName))
            {
                continue;
            }

            aliases.Add(new KeyValuePair<string, string>(
                bindingName,
                $"{SanitizeModuleBindingName(module.ModuleName)}_{bindingName}"));
        }
    }

    private static void AddSelectorAliases(
        ProjectModule module,
        IReadOnlySet<string> referencedVariables,
        HashSet<string> localNames,
        List<KeyValuePair<string, string>> aliases)
    {
        // Binding selectors (`import M.name [as x]`) introduce the selected value export unqualified.
        // They are resolved before whole-module imports so an explicitly selected name wins over the
        // same name being brought in by a whole-module import. The alias target is the qualified
        // reference `Module.name`, which the lowering resolves uniformly for built-in members and
        // stitched user-module bindings (`Ashes_Module_name`) alike — so no implicit re-export occurs:
        // the alias lives only in this module's stitched source. Type selectors (`import M.Type`) need
        // no value alias: type declarations are hoisted to the top of the combined source and are
        // already globally visible, so an unqualified type reference resolves with nothing to inject.
        foreach (var selector in module.Selectors)
        {
            if (!IsValueExportName(selector.ExportName)
                || !referencedVariables.Contains(selector.LocalName)
                || !localNames.Add(selector.LocalName))
            {
                continue;
            }

            aliases.Add(new KeyValuePair<string, string>(
                selector.LocalName,
                $"{selector.ModuleName}.{selector.ExportName}"));
        }
    }

    private static void AddImportedExportAliases(
        ProjectModule module,
        IReadOnlySet<string> referencedVariables,
        IReadOnlySet<QualifiedReference> referencedQualifiedReferences,
        IReadOnlyDictionary<string, IReadOnlyList<string>> exportedNames,
        HashSet<string> localNames,
        List<KeyValuePair<string, string>> aliases)
    {
        var importedOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        var shortQualifierOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var import in module.Imports.Reverse())
        {
            if (!exportedNames.TryGetValue(import, out var exportNames) || exportNames.Count == 0)
            {
                continue;
            }

            var shortQualifier = GetLeafModuleQualifier(import);
            var hasReferencedShortQualifier = !string.Equals(shortQualifier, import, StringComparison.Ordinal)
                && referencedQualifiedReferences.Any(reference => string.Equals(reference.ModuleName, shortQualifier, StringComparison.Ordinal));

            if (hasReferencedShortQualifier)
            {
                if (shortQualifierOwners.TryGetValue(shortQualifier, out var existingModule))
                {
                    throw new InvalidOperationException(
                        $"Import module qualifier collision for '{shortQualifier}': '{existingModule}' and '{import}'. Use full qualification.");
                }

                shortQualifierOwners[shortQualifier] = import;
            }

            foreach (var exportName in exportNames)
            {
                AddImportExportAlias(
                    import, exportName, shortQualifier, hasReferencedShortQualifier,
                    referencedVariables, referencedQualifiedReferences, localNames, importedOwners, aliases);
            }
        }
    }

    private static void AddImportExportAlias(
        string import,
        string exportName,
        string shortQualifier,
        bool hasReferencedShortQualifier,
        IReadOnlySet<string> referencedVariables,
        IReadOnlySet<QualifiedReference> referencedQualifiedReferences,
        HashSet<string> localNames,
        Dictionary<string, string> importedOwners,
        List<KeyValuePair<string, string>> aliases)
    {
        if (!referencedVariables.Contains(exportName) || localNames.Contains(exportName))
        {
            if (hasReferencedShortQualifier
                && referencedQualifiedReferences.Contains(new QualifiedReference(shortQualifier, exportName)))
            {
                aliases.Add(new KeyValuePair<string, string>(
                    $"{SanitizeModuleBindingName(shortQualifier)}_{exportName}",
                    $"{SanitizeModuleBindingName(import)}_{exportName}"));
            }

            return;
        }

        if (importedOwners.TryGetValue(exportName, out var existingModule))
        {
            throw new InvalidOperationException(
                $"Import name collision for imported binding '{exportName}': '{existingModule}' and '{import}'.");
        }

        importedOwners[exportName] = import;
        aliases.Add(new KeyValuePair<string, string>(
            exportName,
            $"{SanitizeModuleBindingName(import)}_{exportName}"));

        if (hasReferencedShortQualifier
            && referencedQualifiedReferences.Contains(new QualifiedReference(shortQualifier, exportName)))
        {
            aliases.Add(new KeyValuePair<string, string>(
                $"{SanitizeModuleBindingName(shortQualifier)}_{exportName}",
                $"{SanitizeModuleBindingName(import)}_{exportName}"));
        }
    }

    private static string GetLeafModuleQualifier(string moduleName)
    {
        var lastDot = moduleName.LastIndexOf('.');
        return lastDot >= 0 ? moduleName[(lastDot + 1)..] : moduleName;
    }

    private static string ApplyAliases(
        string source,
        IReadOnlyList<KeyValuePair<string, string>> aliases,
        IReadOnlyDictionary<string, string> exportAnnotations)
    {
        var wrapped = source.Trim();
        foreach (var alias in aliases)
        {
            wrapped = $"{FormatAliasBinding(alias, exportAnnotations)} in {wrapped}";
        }

        return wrapped;
    }

    private static string FormatAliasBinding(
        KeyValuePair<string, string> alias,
        IReadOnlyDictionary<string, string> exportAnnotations)
    {
        string annotation = exportAnnotations.TryGetValue(alias.Value, out string? value)
            ? " : " + value
            : string.Empty;
        return $"let {alias.Key}{annotation} = {alias.Value}";
    }

    /// <summary>
    /// Applies aliases by renaming matching identifier tokens in place rather than wrapping the
    /// expression in <c>let ... in</c> bindings. Used for <c>let rec</c> values, which must remain
    /// bare function literals so mutual/self references stay deferred to call time.
    /// </summary>
    private static string ApplyAliasesByRenaming(string source, IReadOnlyList<KeyValuePair<string, string>> aliases)
    {
        var trimmed = source.Trim();
        if (aliases.Count == 0)
        {
            return trimmed;
        }

        var valueNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var alias in aliases)
        {
            valueNames.Add(alias.Key);
        }

        // Alias keys inhabit the value/type namespace. Preserve same-spelled record-field tokens;
        // those labels are not references to the binding being qualified.
        return RenameIdentifiers(trimmed, aliases, valueNames);
    }

    private static IReadOnlyList<string> GetExportNames(ModuleSourceShape shape)
    {
        if (shape.ExportPolicy is { IsExplicit: true } policy)
        {
            return shape.TopLevelBindings
                .SelectMany(group => group.Bindings)
                .Select(binding => binding.Name)
                .Where(policy.Values.Contains)
                .ToArray();
        }

        if (shape.TopLevelBindings.Count > 0)
        {
            return shape.TopLevelBindings
                .SelectMany(group => group.Bindings)
                .Select(binding => binding.Name)
                .ToArray();
        }

        return string.IsNullOrWhiteSpace(shape.LegacyExportName)
            ? []
            : [shape.LegacyExportName];
    }

    private static IReadOnlyDictionary<string, string> BuildExportAnnotationLookup(
        IReadOnlyList<ProjectModule> modules,
        IReadOnlyDictionary<string, ModuleSourceShape> shapes)
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ProjectModule module in modules)
        {
            foreach (ModuleBindingFragment binding in shapes[module.ModuleName].TopLevelBindings.SelectMany(group => group.Bindings))
            {
                if (!string.IsNullOrWhiteSpace(binding.Annotation))
                {
                    annotations[$"{SanitizeModuleBindingName(module.ModuleName)}_{binding.Name}"] = binding.Annotation;
                    annotations[$"{module.ModuleName}.{binding.Name}"] = binding.Annotation;
                }
            }
        }

        return annotations;
    }

    private static ModuleSourceShape ShapeModuleSource(
        string source,
        string moduleName = "Main",
        IReadOnlySet<string>? directModules = null)
    {
        (source, ModuleExportPolicy policy) = ApplyExplicitExportInterface(
            source,
            moduleName,
            directModules ?? new HashSet<string>(StringComparer.Ordinal));
        if (TryShapeFlatModule(source, out var flatShape))
        {
            return flatShape with { ExportPolicy = policy };
        }

        var bodyStart = FindExpressionBodyStart(source);
        var typeDeclarationsSource = bodyStart > 0 ? source[..bodyStart] : string.Empty;

        // Keep the body's line numbers aligned with the original file: the hoisted type-declaration
        // prefix is replaced by an equivalent run of blank lines rather than dropped outright.
        var rawExpressionSource = bodyStart < source.Length
            ? new string('\n', typeDeclarationsSource.Count(c => c == '\n')) + source[bodyStart..].TrimEnd()
            : string.Empty;
        var fragments = ExtractTopLevelBindings(rawExpressionSource, out var remainingBody);
        var topLevelBindings = fragments
            .Select(fragment => new ModuleBindingGroup([fragment], fragment.IsRecursive))
            .ToArray();
        var legacyExportName = topLevelBindings.Length == 0 ? TryInferExportName(source) : null;

        // The legacy path hoists the contiguous leading type-declaration prefix verbatim, so the
        // whole region is one identity fragment.
        var legacyFragments = typeDeclarationsSource.Length > 0
            ? new[] { (0, 0, typeDeclarationsSource.Length) }
            : null;
        return new ModuleSourceShape(typeDeclarationsSource, rawExpressionSource, remainingBody, topLevelBindings, legacyExportName, IsFlat: false, policy, TypeDeclFragments: legacyFragments);
    }

    private static (string Source, ModuleExportPolicy Policy) ApplyExplicitExportInterface(
        string source,
        string moduleName,
        IReadOnlySet<string> directModules)
    {
        var diagnostics = new Diagnostics();
        Program program = new Parser(source, diagnostics).ParseProgram();
        if (diagnostics.StructuredErrors.Count > 0)
        {
            return (source, ModuleExportPolicy.Compatibility);
        }

        List<(int Index, ExportDecl Decl)> declarations = program.Items
            .Select((item, index) => (item, index))
            .Where(pair => pair.item is TopLevelItem.Export)
            .Select(pair => (pair.index, ((TopLevelItem.Export)pair.item).Decl))
            .ToList();
        if (declarations.Count == 0)
        {
            return (source, ModuleExportPolicy.Compatibility);
        }

        if (declarations.Count != 1 || declarations[0].Index != 0)
        {
            throw new InvalidOperationException(
                $"[ASH038] Module '{moduleName}' must contain exactly one export declaration before all other declarations.");
        }

        ExportDecl declaration = declarations[0].Decl;
        CollectExportableDeclarations(
            program,
            out HashSet<string> declaredValues,
            out Dictionary<string, TypeDecl> declaredTypes,
            out HashSet<string> zeroCostTypeNames);
        ModuleExportPolicy policy = BuildExportPolicy(
            moduleName, declaration, declaredValues, declaredTypes, directModules);
        TextSpan span = AstSpans.GetOrDefault(declaration);
        TextSpan stringSpan = ToStringSpan(source, span);
        string withoutDeclaration = BlankSpans(source, [(stringSpan.Start, stringSpan.End)]);
        return (RenamePrivateModuleMembers(
            withoutDeclaration,
            moduleName,
            declaredValues,
            declaredTypes,
            zeroCostTypeNames,
            policy), policy);
    }

    private static void CollectExportableDeclarations(
        Program program,
        out HashSet<string> declaredValues,
        out Dictionary<string, TypeDecl> declaredTypes,
        out HashSet<string> zeroCostTypeNames)
    {
        declaredValues = new HashSet<string>(StringComparer.Ordinal);
        declaredTypes = new Dictionary<string, TypeDecl>(StringComparer.Ordinal);
        zeroCostTypeNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (TopLevelItem item in program.Items)
        {
            switch (item)
            {
                case TopLevelItem.LetDecl letDecl:
                    declaredValues.Add(letDecl.Name);
                    break;
                case TopLevelItem.RecursiveGroup group:
                    foreach ((string name, _) in group.Bindings)
                    {
                        declaredValues.Add(name);
                    }
                    break;
                case TopLevelItem.Type type:
                    declaredTypes[type.Decl.Name] = type.Decl;
                    break;
                case TopLevelItem.TypeAlias alias:
                    declaredTypes[alias.Decl.Name] = new TypeDecl(
                        alias.Decl.Name,
                        alias.Decl.TypeParameters,
                        []);
                    break;
                case TopLevelItem.ZeroCostType zeroCostType:
                    zeroCostTypeNames.Add(zeroCostType.Decl.Name);
                    declaredTypes[zeroCostType.Decl.Name] = new TypeDecl(
                        zeroCostType.Decl.Name,
                        zeroCostType.Decl.TypeParameters,
                        [zeroCostType.Decl.Constructor]);
                    break;
            }
        }
    }

    private static ModuleExportPolicy BuildExportPolicy(
        string moduleName,
        ExportDecl declaration,
        IReadOnlySet<string> declaredValues,
        IReadOnlyDictionary<string, TypeDecl> declaredTypes,
        IReadOnlySet<string> directModules)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        var types = new HashSet<string>(StringComparer.Ordinal);
        var constructors = new HashSet<string>(StringComparer.Ordinal);
        var modules = new HashSet<string>(StringComparer.Ordinal);
        var entries = new HashSet<string>(StringComparer.Ordinal);
        foreach (ExportItem item in declaration.Items)
        {
            ValidateExportItem(
                moduleName, item, declaredValues, declaredTypes, directModules,
                values, types, constructors, modules, entries);
        }

        return new ModuleExportPolicy(true, values, types, constructors, modules);
    }

    private static string RenamePrivateModuleMembers(
        string source,
        string moduleName,
        IReadOnlySet<string> declaredValues,
        IReadOnlyDictionary<string, TypeDecl> declaredTypes,
        IReadOnlySet<string> zeroCostTypeNames,
        ModuleExportPolicy policy)
    {
        var renames = new List<KeyValuePair<string, string>>();
        var privateValueNames = new HashSet<string>(StringComparer.Ordinal);
        var publicAbstractAliases = new List<string>();
        string sanitized = SanitizeModuleBindingName(moduleName);
        foreach (string value in declaredValues.Where(name => !policy.Values.Contains(name)))
        {
            renames.Add(new(value, $"__ashes_private_value_{sanitized}_{value}"));
            privateValueNames.Add(value);
        }

        foreach ((string typeName, TypeDecl typeDecl) in declaredTypes)
        {
            string privateTypeName = $"{PrivateTypePrefix}{sanitized}_{typeName}";
            if (!policy.Types.Contains(typeName))
            {
                renames.Add(new(typeName, privateTypeName));
            }
            else if (zeroCostTypeNames.Contains(typeName)
                && typeDecl.Constructors.Any(constructor =>
                string.Equals(constructor.Name, typeName, StringComparison.Ordinal)
                && !policy.Constructors.Contains(constructor.Name)))
            {
                // A type and its sole constructor may deliberately share one spelling (records and
                // zero-cost nominal types). Identifier-wide private-constructor renaming cannot
                // separate those namespaces in stitched source, so rename the nominal declaration
                // and publish a transparent alias back to its exported type name. The constructor
                // stays private while annotations retain the public nominal identity.
                renames.Add(new(typeName, privateTypeName));
                string parameters = typeDecl.TypeParameters.Count == 0
                    ? string.Empty
                    : $"({string.Join(", ", typeDecl.TypeParameters.Select(parameter => parameter.Name))})";
                string arguments = typeDecl.TypeParameters.Count == 0
                    ? string.Empty
                    : $"({string.Join(", ", typeDecl.TypeParameters.Select(parameter => parameter.Name))})";
                publicAbstractAliases.Add(
                    $"type alias {typeName}{parameters} = {privateTypeName}{arguments}");
            }

            foreach (TypeConstructor constructor in typeDecl.Constructors)
            {
                if (!policy.Constructors.Contains(constructor.Name)
                    && !string.Equals(typeName, constructor.Name, StringComparison.Ordinal))
                {
                    renames.Add(new(constructor.Name, $"{PrivateConstructorPrefix}{sanitized}_{constructor.Name}"));
                }
            }
        }

        string renamed = RenameIdentifiers(source, renames, privateValueNames);
        return publicAbstractAliases.Count == 0
            ? renamed
            : renamed.TrimEnd() + "\n" + string.Join("\n", publicAbstractAliases) + "\n";
    }

    private static void ValidateExportItem(
        string moduleName,
        ExportItem item,
        IReadOnlySet<string> declaredValues,
        IReadOnlyDictionary<string, TypeDecl> declaredTypes,
        IReadOnlySet<string> directModules,
        HashSet<string> values,
        HashSet<string> types,
        HashSet<string> constructors,
        HashSet<string> modules,
        HashSet<string> entries)
    {
        string key = item switch
        {
            ExportItem.Value value => $"value:{value.Name}",
            ExportItem.Type type => $"type:{type.Name}",
            ExportItem.Module module => $"module:{module.Name}",
            _ => throw new UnreachableException(),
        };
        if (!entries.Add(key))
        {
            throw new InvalidOperationException($"[ASH037] Duplicate export '{key}' in module '{moduleName}'.");
        }

        switch (item)
        {
            case ExportItem.Value value when !declaredValues.Contains(value.Name):
                throw UnknownExport(moduleName, value.Name);
            case ExportItem.Value value:
                values.Add(value.Name);
                break;
            case ExportItem.Module module when !directModules.Contains(module.Name):
                throw UnknownExport(moduleName, module.Name);
            case ExportItem.Module module:
                modules.Add(module.Name);
                break;
            case ExportItem.Type type:
                if (!declaredTypes.TryGetValue(type.Name, out TypeDecl? declaration))
                {
                    throw UnknownExport(moduleName, type.Name);
                }

                types.Add(type.Name);
                AddExportedConstructors(moduleName, type, declaration, constructors);
                break;
        }
    }

    private static void AddExportedConstructors(
        string moduleName,
        ExportItem.Type export,
        TypeDecl declaration,
        HashSet<string> constructors)
    {
        IReadOnlyList<string> names = export.Constructors switch
        {
            ExportConstructors.Hidden => [],
            ExportConstructors.All => declaration.Constructors.Select(constructor => constructor.Name).ToArray(),
            ExportConstructors.Selected constructorSelection => constructorSelection.Names,
            _ => throw new UnreachableException(),
        };
        var uniqueNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in names)
        {
            if (!uniqueNames.Add(name) || !constructors.Add(name))
            {
                throw new InvalidOperationException($"[ASH037] Duplicate constructor export '{name}' in module '{moduleName}'.");
            }

            if (!declaration.Constructors.Any(constructor => string.Equals(constructor.Name, name, StringComparison.Ordinal)))
            {
                throw UnknownExport(moduleName, name);
            }
        }
    }

    private static InvalidOperationException UnknownExport(string moduleName, string name) =>
        new($"[ASH038] Module '{moduleName}' does not export unknown local declaration '{name}'.");

    private static string RenameIdentifiers(
        string source,
        IReadOnlyList<KeyValuePair<string, string>> replacements,
        IReadOnlySet<string> privateValueNames)
    {
        if (replacements.Count == 0)
        {
            return source;
        }

        var byName = replacements.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var diagnostics = new Diagnostics();
        var lexer = new StringOffsetLexer(source, diagnostics);
        var tokens = new List<Token>();
        while (true)
        {
            Token token = lexer.Next();
            if (token.Kind == TokenKind.EOF)
            {
                break;
            }

            tokens.Add(token);
        }

        var builder = new StringBuilder();
        int copied = 0;
        for (int index = 0; index < tokens.Count; index++)
        {
            Token token = tokens[index];
            if (token.Kind == TokenKind.Ident
                && byName.TryGetValue(token.Text, out string? replacement)
                && (!privateValueNames.Contains(token.Text) || !IsRecordFieldIdentifier(tokens, index)))
            {
                builder.Append(source[copied..token.Position]).Append(replacement);
                copied = token.End;
            }
        }

        builder.Append(source[copied..]);
        return builder.ToString();
    }

    private static bool IsRecordFieldIdentifier(IReadOnlyList<Token> tokens, int index)
    {
        TokenKind previous = index > 0 ? tokens[index - 1].Kind : TokenKind.EOF;
        TokenKind next = index + 1 < tokens.Count ? tokens[index + 1].Kind : TokenKind.EOF;

        // Field access uses the same dot spelling as module qualification. A private top-level value
        // is referenced bare inside its own module, so a name after a dot is always the field/member
        // namespace rather than the value binding being hidden.
        if (previous == TokenKind.Dot)
        {
            return true;
        }

        // Named construction, update, and record-pattern fields are followed by '='. Their first
        // field follows '(', 'with', or '{'; later fields follow a comma.
        if (next == TokenKind.Equals
            && previous is TokenKind.LParen or TokenKind.Comma or TokenKind.With or TokenKind.LBrace)
        {
            return true;
        }

        // Record declarations spell each field as '| name: Type'.
        return next == TokenKind.Colon && previous == TokenKind.Pipe;
    }

    /// <summary>
    /// Shapes a module written in the flat top-level declaration form (a sequence of
    /// <c>let</c> / <c>let rec ... and ...</c> / <c>type</c> / <c>external</c> declarations followed by
    /// an optional trailing expression). The export set is exactly the top-level <c>let</c>/recgroup
    /// names; <c>external</c> declarations and the trailing expression are dropped. Returns
    /// <see langword="false"/> for the legacy nested <c>let ... in</c> pyramid (no top-level items)
    /// or any source the real parser rejects, so the caller falls back to text-based shaping.
    /// </summary>
    private static bool TryShapeFlatModule(string source, out ModuleSourceShape shape)
    {
        shape = null!;
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        var diag = new Diagnostics();
        var program = new Parser(source, diag).ParseProgram();

        // A file with no top-level items is either a single expression or the legacy nested
        // `let ... in` pyramid (its declarations form an expression chain, not Program.Items); both
        // keep the legacy text-based shaping path.
        if (diag.StructuredErrors.Count > 0 || program.Items.Count == 0)
        {
            return false;
        }

        var typeDeclarations = new StringBuilder();
        var typeDeclFragments = new List<(int FragmentStart, int OriginalStart, int Length)>();
        var groups = new List<ModuleBindingGroup>();
        // Spans of declarations hoisted out of the entry expression (type decls, which the stitcher
        // emits up front, and `external`, which is never part of the body). Removing exactly these from
        // the source leaves the flat `let` declarations and the trailing expression for the entry path.
        var hoistedSpans = new List<(int Start, int End)>();
        var hasFlatBinding = false;
        var hoistedCapabilityOrProvide = false;
        var cursor = 0;

        foreach (var item in program.Items)
        {
            if (!TryShapeFlatItem(
                    source, item, typeDeclarations, typeDeclFragments, groups, hoistedSpans,
                    ref hasFlatBinding, ref hoistedCapabilityOrProvide, ref cursor))
            {
                return false;
            }
        }

        // Without a genuine top-level `let`/`let rec` declaration there is normally nothing flat to
        // export, so a module keeps the legacy text-based shaping (which extracts pyramid bindings from
        // Program.Body and preserves the entry expression). The one exception: a module that hoists a
        // capability or provider but has no value bindings is still genuinely flat — shape it here (with
        // empty binding groups) so those declarations hoist, rather than falling back to a text path
        // that mis-parses `provide`/`capability`. A pyramid body still forces legacy (its bindings must
        // be extracted).
        if (!hasFlatBinding && (!hoistedCapabilityOrProvide || program.Body is Expr.Let or Expr.LetRecursive))
        {
            return false;
        }

        shape = BuildFlatModuleShape(source, typeDeclarations, typeDeclFragments, groups, hoistedSpans, program.Body is not null);
        return true;
    }

    private static ModuleSourceShape BuildFlatModuleShape(
        string source,
        StringBuilder typeDeclarations,
        List<(int FragmentStart, int OriginalStart, int Length)> typeDeclFragments,
        List<ModuleBindingGroup> groups,
        List<(int Start, int End)> hoistedSpans,
        bool hasTrailingExpression)
    {
        // For an imported (non-entry) module the trailing expression is dropped and the stitcher uses
        // the binding groups, so RawExpressionSource is unused. For an entry module it is the program
        // body, so preserve the flat `let` declarations and trailing expression here (with the hoisted
        // type/external declarations removed, since those are emitted up front) rather than discarding it.
        return new ModuleSourceShape(
            TypeDeclarationsSource: typeDeclarations.ToString(),
            RawExpressionSource: BlankSpans(source, hoistedSpans).TrimEnd(),
            ExpressionBodySource: string.Empty,
            TopLevelBindings: groups,
            LegacyExportName: null,
            IsFlat: true,
            HasTrailingExpression: hasTrailingExpression,
            TypeDeclFragments: typeDeclFragments);
    }

    private static bool TryShapeFlatItem(
        string source,
        TopLevelItem item,
        StringBuilder typeDeclarations,
        List<(int FragmentStart, int OriginalStart, int Length)> typeDeclFragments,
        List<ModuleBindingGroup> groups,
        List<(int Start, int End)> hoistedSpans,
        ref bool hasFlatBinding,
        ref bool hoistedCapabilityOrProvide,
        ref int cursor)
    {
        switch (item)
        {
            case TopLevelItem.Type typeItem:
                return TryHoistDeclarationSpan(
                    source, AstSpans.GetOrDefault(typeItem.Decl),
                    typeDeclarations, typeDeclFragments, hoistedSpans, ref cursor);

            case TopLevelItem.TypeAlias aliasItem:
                return TryHoistDeclarationSpan(
                    source, AstSpans.GetOrDefault(aliasItem.Decl),
                    typeDeclarations, typeDeclFragments, hoistedSpans, ref cursor);

            case TopLevelItem.ZeroCostType zeroCostTypeItem:
                return TryHoistDeclarationSpan(
                    source, AstSpans.GetOrDefault(zeroCostTypeItem.Decl),
                    typeDeclarations, typeDeclFragments, hoistedSpans, ref cursor);

            case TopLevelItem.Capability capabilityItem:
                return TryHoistGlobalDeclaration(
                    source, AstSpans.GetOrDefault(capabilityItem.Decl), typeDeclarations,
                    typeDeclFragments, hoistedSpans, ref hoistedCapabilityOrProvide, ref cursor);

            case TopLevelItem.Provide provideItem:
                return TryHoistGlobalDeclaration(
                    source, AstSpans.GetOrDefault(provideItem.Decl), typeDeclarations,
                    typeDeclFragments, hoistedSpans, ref hoistedCapabilityOrProvide, ref cursor);

            case TopLevelItem.Trait traitItem:
                return TryHoistGlobalDeclaration(
                    source, AstSpans.GetOrDefault(traitItem.Decl), typeDeclarations,
                    typeDeclFragments, hoistedSpans, ref hoistedCapabilityOrProvide, ref cursor);

            case TopLevelItem.Implementation instanceItem:
                return TryHoistGlobalDeclaration(
                    source, AstSpans.GetOrDefault(instanceItem.Decl), typeDeclarations,
                    typeDeclFragments, hoistedSpans, ref hoistedCapabilityOrProvide, ref cursor);

            case TopLevelItem.External externalItem:
                // `external` is never exported, but it is program-wide and must be visible to
                // lowering so direct FFI calls bind. Hoist it with the other global declarations
                // while still removing it from the entry expression.
                return TryHoistGlobalDeclaration(
                    source, AstSpans.GetOrDefault(externalItem.Decl),
                    typeDeclarations, typeDeclFragments, hoistedSpans,
                    ref hoistedCapabilityOrProvide, ref cursor);

            case TopLevelItem.LetDecl letDecl:
                return TryShapeFlatLetDecl(source, letDecl, groups, ref hasFlatBinding, ref cursor);

            case TopLevelItem.RecursiveGroup recursiveGroup:
                return TryShapeFlatRecursiveGroup(source, recursiveGroup, groups, ref hasFlatBinding, ref cursor);

            default:
                return false;
        }
    }

    private static bool TryHoistGlobalDeclaration(
        string source,
        TextSpan span,
        StringBuilder declarations,
        List<(int FragmentStart, int OriginalStart, int Length)> fragments,
        List<(int Start, int End)> hoistedSpans,
        ref bool hoistedGlobalDeclaration,
        ref int cursor)
    {
        if (!TryHoistDeclarationSpan(source, span, declarations, fragments, hoistedSpans, ref cursor))
        {
            return false;
        }

        hoistedGlobalDeclaration = true;
        return true;
    }

    private static bool TryHoistDeclarationSpan(
        string source,
        TextSpan span,
        StringBuilder typeDeclarations,
        List<(int FragmentStart, int OriginalStart, int Length)> typeDeclFragments,
        List<(int Start, int End)> hoistedSpans,
        ref int cursor)
    {
        span = ToStringSpan(source, span);
        if (span.End <= span.Start || span.End > source.Length)
        {
            return false;
        }

        // Each declaration carries a trailing newline so concatenated type sources (and the
        // bindings that follow them in the combined source) stay lexically separated.
        typeDeclFragments.Add((typeDeclarations.Length, span.Start, span.End - span.Start));
        typeDeclarations.Append(source[span.Start..span.End]).Append('\n');
        hoistedSpans.Add((span.Start, span.End));
        cursor = span.End;
        return true;
    }

    private static bool TryShapeFlatLetDecl(
        string source,
        TopLevelItem.LetDecl letDecl,
        List<ModuleBindingGroup> groups,
        ref bool hasFlatBinding,
        ref int cursor)
    {
        if (!TryExtractFlatBindingValue(source, letDecl.Value, ref cursor, out var valueSource, out var annotation))
        {
            return false;
        }

        groups.Add(new ModuleBindingGroup(
            [new ModuleBindingFragment(letDecl.Name, valueSource, letDecl.IsRecursive, annotation)],
            letDecl.IsRecursive));
        hasFlatBinding = true;
        return true;
    }

    private static bool TryShapeFlatRecursiveGroup(
        string source,
        TopLevelItem.RecursiveGroup recursiveGroup,
        List<ModuleBindingGroup> groups,
        ref bool hasFlatBinding,
        ref int cursor)
    {
        var members = new List<ModuleBindingFragment>();
        for (int index = 0; index < recursiveGroup.Bindings.Count; index++)
        {
            (string name, Expr value) = recursiveGroup.Bindings[index];
            if (!TryExtractFlatBindingValue(
                    source,
                    value,
                    ref cursor,
                    out string valueSource,
                    out string? annotation))
            {
                return false;
            }

            members.Add(new ModuleBindingFragment(name, valueSource, IsRecursive: true, annotation));
        }

        groups.Add(new ModuleBindingGroup(members, IsRecursiveGroup: true));
        hasFlatBinding = true;
        return true;
    }

    /// <summary>
    /// Returns <paramref name="source"/> with the given (already source-ordered, non-overlapping)
    /// spans removed, preserving everything in between. Used to strip hoisted type/external declarations
    /// out of the flat entry expression while keeping the flat <c>let</c> declarations and trailing
    /// expression intact.
    /// </summary>
    private static string BlankSpans(string source, IReadOnlyList<(int Start, int End)> spans)
    {
        if (spans.Count == 0)
        {
            return source;
        }

        // Blank the spans instead of removing them: newlines are kept and every other character
        // becomes a space, so the remaining text keeps its original line and column positions
        // (debug line info and span mapping stay 1:1 with the user's file).
        var builder = new StringBuilder(source);
        foreach (var (start, end) in spans)
        {
            for (var index = start; index < end && index < builder.Length; index++)
            {
                if (builder[index] != '\n')
                {
                    builder[index] = ' ';
                }
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Extracts the source text of a flat top-level binding's value, advancing <paramref name="cursor"/>
    /// past it. The value end comes from the parsed AST span (authoritative for arbitrarily nested
    /// expressions); the value start is the position just after the binding's <c>=</c>. ML-style
    /// function sugar (<c>let f x y = body</c>) is reconstructed into an explicit lambda chain so the
    /// stitched binding stays a plain value expression.
    /// </summary>
    private static bool TryExtractFlatBindingValue(string source, Expr value, ref int cursor, out string valueSource)
    {
        return TryExtractFlatBindingValue(source, value, ref cursor, out valueSource, out _);
    }

    private static bool TryExtractFlatBindingValue(string source, Expr value, ref int cursor, out string valueSource, out string? annotation)
    {
        valueSource = string.Empty;
        if (!TryScanFlatLetHeader(source, cursor, out var parameters, out var valueStart, out annotation))
        {
            return false;
        }

        var valueEnd = new SourceTextIndex(source).ToUtf16Offset(AstSpans.GetOrDefault(value).End);
        if (valueEnd <= valueStart || valueEnd > source.Length)
        {
            return false;
        }

        // `fmt -w` parenthesizes a sugared binding whose folded body leads with `let..in`
        // (`let f a b = (let..in ...)`). A parenthesized expression's AST span excludes the wrapping
        // parentheses — `ParseParen` returns the inner expression carrying its own span — so the
        // recorded value end stops before the closing `)`. Extending past the closing brackets that
        // balance opens within the extracted value keeps the stitched binding syntactically complete;
        // for an already-balanced value (the common case) this is a no-op.
        valueEnd = ExtendToBalancedEnd(source, valueStart, valueEnd);
        if (valueEnd > source.Length)
        {
            return false;
        }

        var body = source[valueStart..valueEnd].Trim();
        for (var i = parameters.Count - 1; i >= 0; i--)
        {
            body = $"given ({parameters[i]}) -> {body}";
        }

        valueSource = body;
        cursor = valueEnd;
        return true;
    }

    /// <summary>
    /// Extends a value's AST-derived end position past any closing parens/brackets that balance opens
    /// occurring within the extracted value text. A parenthesized expression's span excludes its
    /// wrapping parentheses (the parser strips them), so the recorded end can fall short of the real
    /// value end by the dropped closers; re-balancing recovers them. Returns <paramref name="astEnd"/>
    /// unchanged when the value is already balanced.
    /// </summary>
    private static int ExtendToBalancedEnd(string source, int from, int astEnd)
    {
        var diag = new Diagnostics();
        var lexer = new StringOffsetLexer(source[from..], diag);
        var depth = 0;
        var reachedAstEnd = false;

        while (true)
        {
            var token = lexer.Next();
            if (token.Kind == TokenKind.EOF)
            {
                return astEnd;
            }

            switch (token.Kind)
            {
                case TokenKind.LParen:
                case TokenKind.LBracket:
                    depth++;
                    break;
                case TokenKind.RParen:
                case TokenKind.RBracket:
                    depth--;
                    break;
            }

            var absoluteEnd = from + token.Position + token.Length;
            if (absoluteEnd >= astEnd)
            {
                reachedAstEnd = true;
            }

            // Once the AST span is covered and every opened bracket is closed, the value is complete:
            // its true end is whichever is later — the AST end (balanced case) or the closer that
            // brought the depth back to zero (the dropped wrapping `)` case).
            if (reachedAstEnd && depth <= 0)
            {
                return Math.Max(astEnd, absoluteEnd);
            }
        }
    }

    /// <summary>
    /// Scans a flat <c>let [rec] name [params] [: type] =</c> (or <c>and name [params] =</c>) header
    /// starting at <paramref name="from"/>, returning any ML-style sugar parameters and the source
    /// position immediately after the value-introducing <c>=</c>.
    /// </summary>
    private static bool TryScanFlatLetHeader(string source, int from, out IReadOnlyList<string> parameters, out int valueStart)
    {
        return TryScanFlatLetHeader(source, from, out parameters, out valueStart, out _);
    }

    private static bool TryScanFlatLetHeader(string source, int from, out IReadOnlyList<string> parameters, out int valueStart, out string? annotation)
    {
        parameters = [];
        valueStart = from;
        annotation = null;
        if (from < 0 || from >= source.Length)
        {
            return false;
        }

        var diag = new Diagnostics();
        var lexer = new StringOffsetLexer(source[from..], diag);

        var token = lexer.Next();
        if (token.Kind is TokenKind.Let or TokenKind.And)
        {
            token = lexer.Next();
        }

        if (token.Kind == TokenKind.Recursive)
        {
            token = lexer.Next();
        }

        if (token.Kind != TokenKind.Ident)
        {
            return false;
        }

        token = lexer.Next();

        if (token.Kind == TokenKind.Colon)
        {
            return TryScanAnnotatedLetHeader(source, from, lexer, token, out valueStart, out annotation);
        }

        var collected = new List<string>();
        if (!TryScanLetParameters(source, from, lexer, ref token, collected))
        {
            return false;
        }

        if (token.Kind != TokenKind.Equals)
        {
            return false;
        }

        parameters = collected;
        valueStart = from + token.Position + token.Text.Length;
        return true;
    }

    private static bool TryScanLetParameters(string source, int from, StringOffsetLexer lexer, ref Token token, List<string> collected)
    {
        while (token.Kind is TokenKind.Ident or TokenKind.LParen)
        {
            if (token.Kind == TokenKind.LParen)
            {
                if (!TryScanParenthesizedParameter(source, from, lexer, ref token, collected))
                {
                    return false;
                }

                continue;
            }

            collected.Add(token.Text);
            token = lexer.Next();
        }

        return true;
    }

    private static bool TryScanAnnotatedLetHeader(
        string source, int from, StringOffsetLexer lexer, Token token, out int valueStart, out string? annotation)
    {
        valueStart = from;
        annotation = null;

        // Annotated binding: skip the type expression up to the value-introducing `=`, capturing
        // the annotation source so a stitched module binding can keep it (a `needs {Cap(a)}` row is
        // what marks a generic dictionary-passing function).
        var annotationStart = from + token.Position + token.Text.Length;
        var depth = 0;
        while (token.Kind != TokenKind.EOF)
        {
            token = lexer.Next();
            switch (token.Kind)
            {
                case TokenKind.LParen:
                case TokenKind.LBracket:
                    depth++;
                    break;
                case TokenKind.RParen:
                case TokenKind.RBracket:
                    depth--;
                    break;
                case TokenKind.Equals when depth == 0:
                    annotation = source[annotationStart..(from + token.Position)].Trim();
                    valueStart = from + token.Position + token.Text.Length;
                    return true;
            }
        }

        return false;
    }

    private static bool TryScanParenthesizedParameter(
        string source, int from, StringOffsetLexer lexer, ref Token token, List<string> collected)
    {
        // Parenthesized annotated parameter: `(name: Type)` — capture the inner text
        // verbatim so the `given ({param}) ->` reconstruction keeps the annotation.
        token = lexer.Next();
        if (token.Kind != TokenKind.Ident)
        {
            return false;
        }

        var innerStart = from + token.Position;
        token = lexer.Next();
        if (token.Kind != TokenKind.Colon)
        {
            return false;
        }

        var parenDepth = 1;
        while (parenDepth > 0)
        {
            token = lexer.Next();
            if (token.Kind == TokenKind.EOF)
            {
                return false;
            }

            if (token.Kind is TokenKind.LParen or TokenKind.LBracket)
            {
                parenDepth++;
            }
            else if (token.Kind is TokenKind.RParen or TokenKind.RBracket)
            {
                parenDepth--;
            }
        }

        collected.Add(source[innerStart..(from + token.Position)].Trim());
        token = lexer.Next();
        return true;
    }

    private static IReadOnlyList<ModuleBindingFragment> ExtractTopLevelBindings(string expressionSource, out string remainingBody)
    {
        var bindings = new List<ModuleBindingFragment>();
        var remaining = expressionSource.Trim();

        while (TrySplitLeadingTopLevelBinding(remaining, out var binding, out var rest))
        {
            bindings.Add(binding);
            remaining = rest;
        }

        remainingBody = remaining;
        return bindings;
    }

    private static bool TrySplitLeadingTopLevelBinding(
        string source,
        out ModuleBindingFragment binding,
        out string remaining)
    {
        binding = null!;
        remaining = source;

        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        var diag = new Diagnostics();
        var lexer = new StringOffsetLexer(source, diag);
        var token = lexer.Next();
        if (token.Kind != TokenKind.Let)
        {
            return false;
        }

        var next = lexer.Next();
        var isRecursive = false;
        if (next.Kind == TokenKind.Recursive)
        {
            isRecursive = true;
            next = lexer.Next();
        }

        if (next.Kind != TokenKind.Ident)
        {
            return false;
        }

        var name = next.Text;
        var equals = lexer.Next();

        // ML-style function sugar: `let f x y = body` binds `f` to nested lambdas over `x`, `y`.
        // The parameters appear as bare identifiers between the name and `=` (a `let f : T = ...`
        // type annotation uses a leading colon and never carries sugar params), so collect them
        // here and re-wrap the value as explicit `given` lambdas — otherwise the binding would keep
        // only the body and drop the parameters (binding `f` to an open expression referencing the
        // undefined parameter names).
        var sugarParams = new List<string>();
        if (!TryScanSugarParams(source, lexer, ref equals, sugarParams))
        {
            return false;
        }

        if (equals.Kind != TokenKind.Equals)
        {
            return false;
        }

        var valueStart = equals.Position + equals.Text.Length;
        return TrySplitBindingValue(source, lexer, valueStart, name, isRecursive, sugarParams, out binding, out remaining);
    }

    private static bool TryScanSugarParams(string source, StringOffsetLexer lexer, ref Token equals, List<string> sugarParams)
    {
        if (equals.Kind is TokenKind.Ident or TokenKind.LParen)
        {
            while (equals.Kind is TokenKind.Ident or TokenKind.LParen)
            {
                if (equals.Kind == TokenKind.LParen)
                {
                    // Parenthesized annotated parameter: `(name: Type)` — capture the inner text
                    // verbatim so the `given ({param}) ->` re-wrap keeps the annotation.
                    if (!TryScanParenthesizedParameter(source, 0, lexer, ref equals, sugarParams))
                    {
                        return false;
                    }

                    continue;
                }

                sugarParams.Add(equals.Text);
                equals = lexer.Next();
            }
        }
        else
        {
            while (equals.Kind != TokenKind.Equals && equals.Kind != TokenKind.EOF)
            {
                equals = lexer.Next();
            }
        }

        return true;
    }

    private static bool TrySplitBindingValue(
        string source,
        StringOffsetLexer lexer,
        int valueStart,
        string name,
        bool isRecursive,
        List<string> sugarParams,
        out ModuleBindingFragment binding,
        out string remaining)
    {
        binding = null!;
        remaining = source;
        var nestedLetDepth = 0;

        while (true)
        {
            var current = lexer.Next();
            switch (current.Kind)
            {
                case TokenKind.EOF:
                    return false;
                case TokenKind.Let:
                    nestedLetDepth++;
                    break;
                case TokenKind.In when nestedLetDepth == 0:
                    var valueSource = source[valueStart..current.Position].Trim();
                    for (var i = sugarParams.Count - 1; i >= 0; i--)
                    {
                        valueSource = $"given ({sugarParams[i]}) -> {valueSource}";
                    }

                    remaining = source[(current.Position + current.Text.Length)..].TrimStart();
                    binding = new ModuleBindingFragment(name, valueSource, isRecursive);
                    return true;
                case TokenKind.In:
                    nestedLetDepth--;
                    break;
            }
        }
    }

    private static int FindExpressionBodyStart(string source)
    {
        int parsedPrefixEnd = FindParsedTypePrefixEnd(source);
        if (parsedPrefixEnd > 0)
        {
            var remainderDiagnostics = new Diagnostics();
            Token firstBodyToken = new StringOffsetLexer(source[parsedPrefixEnd..], remainderDiagnostics).Next();
            return firstBodyToken.Kind == TokenKind.EOF
                ? source.Length
                : parsedPrefixEnd + firstBodyToken.Position;
        }

        var diag = new Diagnostics();
        var lexer = new StringOffsetLexer(source, diag);
        var tok = lexer.Next();

        while (tok.Kind == TokenKind.Type)
        {
            tok = lexer.Next();
            tok = lexer.Next();
            if (tok.Kind == TokenKind.LParen)
            {
                tok = AdvancePastBalancedParentheses(lexer);
            }

            tok = lexer.Next();

            while (tok.Kind == TokenKind.Pipe)
            {
                tok = lexer.Next();
                tok = lexer.Next();

                if (tok.Kind == TokenKind.LParen)
                {
                    tok = AdvancePastBalancedParentheses(lexer);
                }
                else if (tok.Kind == TokenKind.Colon)
                {
                    tok = AdvancePastRecordFieldType(lexer);
                }
            }

            if (tok.Kind == TokenKind.Deriving)
            {
                tok = AdvancePastDerivingClause(lexer);
            }
        }

        return tok.Kind == TokenKind.EOF ? source.Length : tok.Position;
    }

    private static int FindParsedTypePrefixEnd(string source)
    {
        var parserDiagnostics = new Diagnostics();
        Program parsed = new Parser(source, parserDiagnostics).ParseProgram();
        int parsedPrefixEnd = 0;
        if (parserDiagnostics.StructuredErrors.Count == 0)
        {
            foreach (TopLevelItem item in parsed.Items)
            {
                TextSpan span = item switch
                {
                    TopLevelItem.Type type => AstSpans.GetOrDefault(type.Decl),
                    TopLevelItem.TypeAlias alias => AstSpans.GetOrDefault(alias.Decl),
                    TopLevelItem.ZeroCostType zeroCostType => AstSpans.GetOrDefault(zeroCostType.Decl),
                    _ => default,
                };
                if (span.Length == 0)
                {
                    break;
                }
                parsedPrefixEnd = new SourceTextIndex(source).ToUtf16Offset(span.End);
            }
        }
        return parsedPrefixEnd;
    }

    private static TextSpan ToStringSpan(string source, TextSpan byteSpan)
    {
        SourceTextIndex index = new(source);
        return TextSpan.FromBounds(
            index.ToUtf16Offset(byteSpan.Start),
            index.ToUtf16Offset(byteSpan.End));
    }

    private static Token AdvancePastBalancedParentheses(StringOffsetLexer lexer)
    {
        int depth = 1;
        while (depth > 0)
        {
            Token token = lexer.Next();
            if (token.Kind == TokenKind.EOF)
            {
                return token;
            }
            if (token.Kind == TokenKind.LParen)
            {
                depth++;
            }
            else if (token.Kind == TokenKind.RParen)
            {
                depth--;
            }
        }
        return lexer.Next();
    }

    private static Token AdvancePastRecordFieldType(StringOffsetLexer lexer)
    {
        // Type expressions contain neither a bare branch pipe nor a deriving keyword, so either one
        // safely terminates this field in the legacy declaration-prefix scanner.
        Token token;
        do
        {
            token = lexer.Next();
        }
        while (token.Kind is not TokenKind.Pipe
            and not TokenKind.Deriving
            and not TokenKind.EOF
            and not TokenKind.Type
            and not TokenKind.External
            and not TokenKind.Let
            and not TokenKind.Capability
            and not TokenKind.Provide
            and not TokenKind.Trait
            and not TokenKind.Implement);
        return token;
    }

    private static Token AdvancePastDerivingClause(StringOffsetLexer lexer)
    {
        Token token = lexer.Next();
        if (token.Kind != TokenKind.LBrace)
        {
            return token;
        }
        do
        {
            token = lexer.Next();
        }
        while (token.Kind != TokenKind.RBrace && token.Kind != TokenKind.EOF);
        return token.Kind == TokenKind.RBrace ? lexer.Next() : token;
    }

    private static bool TryLoadStandardLibraryModule(string moduleName, out ProjectModule module)
    {
        module = null!;
        if (!StdModules.TryGetValue(moduleName, out var descriptor) || string.IsNullOrWhiteSpace(descriptor.ResourceName))
        {
            return false;
        }

        if (!EmbeddedStdSources.Value.TryGetValue(descriptor.ResourceName, out var source))
        {
            throw new InvalidOperationException($"Could not load standard library module '{moduleName}'.");
        }

        var parsed = ParseImportHeader(source, moduleName);
        module = new ProjectModule(
            moduleName,
            $"<std:{moduleName}>",
            ApplySelectorRenames(parsed.SourceWithoutImports, parsed.ImportSelectors),
            parsed.ImportNames,
            parsed.ImportAliases,
            parsed.ImportSelectors)
        { PackageId = "ashes-core" };
        return true;
    }

    private static string GetModuleRelativePath(string moduleName)
    {
        return moduleName.Replace('.', Path.DirectorySeparatorChar) + ".ash";
    }

    private static List<string> GetExistingModuleCandidates(IReadOnlyList<string> roots, string moduleRelativePath)
    {
        var matches = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            var candidate = Path.GetFullPath(Path.Combine(root, moduleRelativePath));
            if (File.Exists(candidate) && seenPaths.Add(candidate))
            {
                matches.Add(candidate);
            }
        }

        return matches;
    }

    private static string? GetShippedLibraryModulePath(string moduleRelativePath)
    {
        var shippedLibraryRoot = ShippedLibraryRoot.Value;
        if (string.IsNullOrWhiteSpace(shippedLibraryRoot))
        {
            return null;
        }

        var candidate = Path.GetFullPath(Path.Combine(shippedLibraryRoot, moduleRelativePath));
        return File.Exists(candidate) ? candidate : null;
    }

    private static string BuildMissingModuleMessage(string moduleName, IReadOnlyList<string> projectRoots, string moduleRelativePath)
    {
        var attemptedProjectPaths = projectRoots
            .Select(root => Path.GetFullPath(Path.Combine(root, moduleRelativePath)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var message = new StringBuilder()
            .Append($"Could not resolve module '{moduleName}'. Attempted project modules: {string.Join(", ", attemptedProjectPaths)}");

        var shippedLibraryRoot = ShippedLibraryRoot.Value;
        if (string.IsNullOrWhiteSpace(shippedLibraryRoot))
        {
            message.Append($"; shipped library root not found from compiler base '{AppContext.BaseDirectory}'.");
        }
        else
        {
            message.Append($"; attempted shipped library: {Path.GetFullPath(Path.Combine(shippedLibraryRoot, moduleRelativePath))}");
        }

        return message.ToString();
    }

    private static string? DiscoverShippedLibraryRoot()
    {
        var current = new DirectoryInfo(Path.GetFullPath(AppContext.BaseDirectory));
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "lib");
            if (Directory.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }

            current = current.Parent;
        }

        return null;
    }

    private static IReadOnlyDictionary<string, string> LoadEmbeddedStandardLibrarySources()
    {
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        var assembly = typeof(ProjectSupport).Assembly;

        foreach (var descriptor in StdModules.Values)
        {
            if (string.IsNullOrWhiteSpace(descriptor.ResourceName))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(descriptor.ResourceName);
            if (stream is null)
            {
                throw new InvalidOperationException($"Missing embedded standard library resource '{descriptor.ResourceName}'.");
            }

            using var reader = new StreamReader(stream);
            sources[descriptor.ResourceName] = reader.ReadToEnd();
        }

        return sources;
    }

    private static ReferencedNames CollectReferencedNames(string source)
    {
        var diag = new Diagnostics();
        var program = new Parser(source, diag).ParseProgram();
        if (diag.StructuredErrors.Count > 0)
        {
            return new ReferencedNames(
                new HashSet<string>(StringComparer.Ordinal),
                new HashSet<QualifiedReference>());
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var qualifiedReferences = new HashSet<QualifiedReference>();

        // A flat top-level entry parses as a sequence of declarations (Items) followed by an
        // optional trailing expression (Body). Short qualified references can live inside a flat
        // decl's value (e.g. `let n = List.length(xs)`), so the alias prelude must cover those
        // values in addition to the trailing expression — otherwise they reach lowering as an
        // unresolved QualifiedVar and fail with 'Unknown module'.
        foreach (var item in program.Items)
        {
            switch (item)
            {
                case TopLevelItem.LetDecl letDecl:
                    VisitReferencedNames(letDecl.Value, names, qualifiedReferences);
                    break;
                case TopLevelItem.RecursiveGroup recursiveGroup:
                    foreach (var binding in recursiveGroup.Bindings)
                    {
                        VisitReferencedNames(binding.Value, names, qualifiedReferences);
                    }

                    break;
                default:
                    break;
            }
        }

        if (program.Body is not null)
        {
            VisitReferencedNames(program.Body, names, qualifiedReferences);
        }

        return new ReferencedNames(names, qualifiedReferences);
    }

    private static void VisitReferencedNames(Expr expr, HashSet<string> names, HashSet<QualifiedReference> qualifiedReferences)
    {
        switch (expr)
        {
            case Expr.Var varExpr:
                names.Add(varExpr.Name);
                break;
            case Expr.QualifiedVar qualifiedVar:
                qualifiedReferences.Add(new QualifiedReference(qualifiedVar.Module, qualifiedVar.Name));
                break;
            case Expr.IntLit:
            case Expr.UIntLit:
            case Expr.BigIntLit:
            case Expr.FloatLit:
            case Expr.StrLit or Expr.RuneLit:
            case Expr.BoolLit:
                break;
            default:
                VisitReferencedNamesOperators(expr, names, qualifiedReferences);
                break;
        }
    }

    private static void VisitReferencedNamesOperators(Expr expr, HashSet<string> names, HashSet<QualifiedReference> qualifiedReferences)
    {
        switch (expr)
        {
            case Expr.Add add:
                VisitReferencedNames(add.Left, names, qualifiedReferences);
                VisitReferencedNames(add.Right, names, qualifiedReferences);
                break;
            case Expr.Subtract sub:
                VisitReferencedNames(sub.Left, names, qualifiedReferences);
                VisitReferencedNames(sub.Right, names, qualifiedReferences);
                break;
            case Expr.Multiply mul:
                VisitReferencedNames(mul.Left, names, qualifiedReferences);
                VisitReferencedNames(mul.Right, names, qualifiedReferences);
                break;
            case Expr.Modulo modExpr:
                VisitReferencedNames(modExpr.Left, names, qualifiedReferences);
                VisitReferencedNames(modExpr.Right, names, qualifiedReferences);
                break;
            case Expr.Divide div:
                VisitReferencedNames(div.Left, names, qualifiedReferences);
                VisitReferencedNames(div.Right, names, qualifiedReferences);
                break;
            case Expr.BitwiseAnd bitAnd:
                VisitReferencedNames(bitAnd.Left, names, qualifiedReferences);
                VisitReferencedNames(bitAnd.Right, names, qualifiedReferences);
                break;
            case Expr.BitwiseOr bitOr:
                VisitReferencedNames(bitOr.Left, names, qualifiedReferences);
                VisitReferencedNames(bitOr.Right, names, qualifiedReferences);
                break;
            case Expr.BitwiseXor bitXor:
                VisitReferencedNames(bitXor.Left, names, qualifiedReferences);
                VisitReferencedNames(bitXor.Right, names, qualifiedReferences);
                break;
            case Expr.ShiftLeft shiftLeft:
                VisitReferencedNames(shiftLeft.Left, names, qualifiedReferences);
                VisitReferencedNames(shiftLeft.Right, names, qualifiedReferences);
                break;
            case Expr.ShiftRight shiftRight:
                VisitReferencedNames(shiftRight.Left, names, qualifiedReferences);
                VisitReferencedNames(shiftRight.Right, names, qualifiedReferences);
                break;
            case Expr.BitwiseNot bitwiseNot:
                VisitReferencedNames(bitwiseNot.Operand, names, qualifiedReferences);
                break;
            case Expr.LogicalNot logicalNot:
                VisitReferencedNames(logicalNot.Operand, names, qualifiedReferences);
                break;
            default:
                VisitReferencedNamesComparisons(expr, names, qualifiedReferences);
                break;
        }
    }

    private static void VisitReferencedNamesComparisons(Expr expr, HashSet<string> names, HashSet<QualifiedReference> qualifiedReferences)
    {
        switch (expr)
        {
            case Expr.GreaterThan gt:
                VisitReferencedNames(gt.Left, names, qualifiedReferences);
                VisitReferencedNames(gt.Right, names, qualifiedReferences);
                break;
            case Expr.GreaterOrEqual ge:
                VisitReferencedNames(ge.Left, names, qualifiedReferences);
                VisitReferencedNames(ge.Right, names, qualifiedReferences);
                break;
            case Expr.LessThan lt:
                VisitReferencedNames(lt.Left, names, qualifiedReferences);
                VisitReferencedNames(lt.Right, names, qualifiedReferences);
                break;
            case Expr.LessOrEqual le:
                VisitReferencedNames(le.Left, names, qualifiedReferences);
                VisitReferencedNames(le.Right, names, qualifiedReferences);
                break;
            case Expr.Equal eq:
                VisitReferencedNames(eq.Left, names, qualifiedReferences);
                VisitReferencedNames(eq.Right, names, qualifiedReferences);
                break;
            case Expr.NotEqual ne:
                VisitReferencedNames(ne.Left, names, qualifiedReferences);
                VisitReferencedNames(ne.Right, names, qualifiedReferences);
                break;
            case Expr.ResultPipe pipe:
                VisitReferencedNames(pipe.Left, names, qualifiedReferences);
                VisitReferencedNames(pipe.Right, names, qualifiedReferences);
                break;
            case Expr.ResultMapErrorPipe pipe:
                VisitReferencedNames(pipe.Left, names, qualifiedReferences);
                VisitReferencedNames(pipe.Right, names, qualifiedReferences);
                break;
            default:
                VisitReferencedNamesBindings(expr, names, qualifiedReferences);
                break;
        }
    }

    private static void VisitReferencedNamesBindings(Expr expr, HashSet<string> names, HashSet<QualifiedReference> qualifiedReferences)
    {
        switch (expr)
        {
            case Expr.Let letExpr:
                VisitReferencedNames(letExpr.Value, names, qualifiedReferences);
                VisitReferencedNames(letExpr.Body, names, qualifiedReferences);
                break;
            case Expr.LetResult letResultExpr:
                VisitReferencedNames(letResultExpr.Value, names, qualifiedReferences);
                VisitReferencedNames(letResultExpr.Body, names, qualifiedReferences);
                break;
            case Expr.LetRecursive letRecursiveExpr:
                VisitReferencedNames(letRecursiveExpr.Value, names, qualifiedReferences);
                VisitReferencedNames(letRecursiveExpr.Body, names, qualifiedReferences);
                break;
            case Expr.If ifExpr:
                VisitReferencedNames(ifExpr.Cond, names, qualifiedReferences);
                VisitReferencedNames(ifExpr.Then, names, qualifiedReferences);
                VisitReferencedNames(ifExpr.Else, names, qualifiedReferences);
                break;
            case Expr.Lambda lambda:
                VisitReferencedNames(lambda.Body, names, qualifiedReferences);
                break;
            case Expr.Call call:
                VisitReferencedNames(call.Func, names, qualifiedReferences);
                VisitReferencedNames(call.Arg, names, qualifiedReferences);
                break;
            case Expr.TupleLit tuple:
                foreach (var element in tuple.Elements)
                {
                    VisitReferencedNames(element, names, qualifiedReferences);
                }
                break;
            case Expr.ListLit list:
                foreach (var element in list.Elements)
                {
                    VisitReferencedNames(element, names, qualifiedReferences);
                }
                break;
            case Expr.Cons cons:
                VisitReferencedNames(cons.Head, names, qualifiedReferences);
                VisitReferencedNames(cons.Tail, names, qualifiedReferences);
                break;
            default:
                VisitReferencedNamesAggregates(expr, names, qualifiedReferences);
                break;
        }
    }

    private static void VisitReferencedNamesAggregates(Expr expr, HashSet<string> names, HashSet<QualifiedReference> qualifiedReferences)
    {
        switch (expr)
        {
            case Expr.Match match:
                VisitReferencedNames(match.Value, names, qualifiedReferences);
                foreach (var matchCase in match.Cases)
                {
                    if (matchCase.Guard is not null)
                    {
                        VisitReferencedNames(matchCase.Guard, names, qualifiedReferences);
                    }
                    VisitReferencedNames(matchCase.Body, names, qualifiedReferences);
                }
                break;
            case Expr.Await awaitExpr:
                VisitReferencedNames(awaitExpr.Task, names, qualifiedReferences);
                break;
            case Expr.RecordLit rl:
                foreach (var field in rl.Fields)
                    VisitReferencedNames(field.Value, names, qualifiedReferences);
                break;
            case Expr.RecordUpdate ru:
                VisitReferencedNames(ru.Target, names, qualifiedReferences);
                foreach (var update in ru.Updates)
                    VisitReferencedNames(update.Value, names, qualifiedReferences);
                break;
            case Expr.Perform perform:
                VisitReferencedNames(perform.Operation, names, qualifiedReferences);
                break;
            case Expr.Handle handleExpr:
                VisitReferencedNames(handleExpr.Body, names, qualifiedReferences);
                foreach (var arm in handleExpr.Arms)
                    VisitReferencedNames(arm.Body, names, qualifiedReferences);
                break;
            default:
                throw new NotSupportedException(expr.GetType().Name);
        }
    }

    private static (IReadOnlyList<string> Imports, string SourceWithoutImports, IReadOnlyDictionary<string, string> Aliases, IReadOnlyList<ImportSelector> Selectors) ParseImports(string filePath)
    {
        var parsed = ParseImportHeader(SourceTextIndex.ReadUtf8File(filePath), filePath);
        return (parsed.ImportNames, parsed.SourceWithoutImports, parsed.ImportAliases, parsed.ImportSelectors);
    }

    /// <summary>
    /// Lifts inline <c>module Name = ...</c> blocks out of an (imports-stripped) module source. The
    /// enclosing source is returned with each block removed and every bare inline qualifier rewritten
    /// to its file-composed path; each block is returned as an <see cref="InlineModule"/> with the
    /// same rewrites applied recursively for nesting. <paramref name="scopeModuleName"/> is the
    /// composed path of the scope being expanded (the file module name at the top level).
    /// </summary>
    public static (string OuterSource, IReadOnlyList<InlineModuleInfo> InlineModules) ExpandInlineModules(
        string source, string scopeModuleName, string displayPath)
    {
        var lifted = new List<InlineModule>();
        var outer = ExpandInlineModulesCore(source, scopeModuleName, displayPath, lifted);
        return (outer, lifted.Select(m => new InlineModuleInfo(m.ModuleName, m.Source)).ToList());
    }

    /// <summary>Public view of a lifted inline module for consumers outside this class (kept minimal).</summary>
    public sealed record InlineModuleInfo(string ModuleName, string Source);

    private static string ExpandInlineModulesCore(
        string source, string scopeModuleName, string displayPath, List<InlineModule> lifted)
    {
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var outer = new List<string>();
        var directChildren = new List<(string Name, List<string> Body)>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        var i = 0;
        while (i < lines.Length)
        {
            var header = InlineModuleHeader.Match(lines[i]);
            if (!header.Success)
            {
                outer.Add(lines[i]);
                i++;
                continue;
            }

            i = CollectInlineModuleBlock(lines, i, header, displayPath, seenNames, directChildren);
        }

        if (directChildren.Count == 0)
        {
            return source;
        }

        // Rewrite a bare inline qualifier `Name.` to the composed path `<scope>.Name.` so a same-scope
        // reference resolves to the lifted module exactly as a cross-file `import <scope>.Name` would.
        // At an empty scope (the entry file) the children keep their bare names — nothing imports the
        // entry across files — so no rewrite is needed and imports/aliases stay bare.
        var childNames = directChildren.Select(c => c.Name).ToList();
        string Compose(string name) => scopeModuleName.Length == 0 ? name : $"{scopeModuleName}.{name}";
        string Rewrite(string s) => scopeModuleName.Length == 0 ? s : RewriteInlineQualifiers(s, childNames, scopeModuleName);

        foreach (var (name, body) in directChildren)
        {
            var composed = Compose(name);
            var blockSource = Rewrite(Dedent(body));
            ValidateInlineModuleBody(blockSource, composed, displayPath);
            var childOuter = ExpandInlineModulesCore(blockSource, composed, displayPath, lifted);
            lifted.Add(new InlineModule(composed, childOuter));
        }

        return Rewrite(string.Join('\n', outer));
    }

    private static int CollectInlineModuleBlock(
        string[] lines,
        int i,
        Match header,
        string displayPath,
        HashSet<string> seenNames,
        List<(string Name, List<string> Body)> directChildren)
    {
        var headerIndent = header.Groups["indent"].Value.Length;
        var name = header.Groups["name"].Value;
        if (!seenNames.Add(name))
        {
            throw new InvalidOperationException(
                $"[ASH024] Duplicate inline module '{name}' in this scope ({displayPath}).");
        }

        if (string.Equals(name, "Ashes", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"[ASH023] Inline module may not be named 'Ashes' (reserved for the standard library) ({displayPath}).");
        }

        // Body: subsequent lines that are blank or indented past the header column.
        var body = new List<string>();
        var j = i + 1;
        while (j < lines.Length)
        {
            var line = lines[j];
            if (line.Trim().Length == 0)
            {
                body.Add(line);
                j++;
                continue;
            }

            if (LeadingWhitespaceWidth(line) <= headerIndent)
            {
                break;
            }

            body.Add(line);
            j++;
        }

        directChildren.Add((name, body));
        return j;
    }

    private static int LeadingWhitespaceWidth(string line)
    {
        var width = 0;
        foreach (var ch in line)
        {
            if (ch == ' ' || ch == '\t')
            {
                width++;
            }
            else
            {
                break;
            }
        }

        return width;
    }

    /// <summary>Removes the minimal common leading indentation from a block's non-blank lines so it parses as a top-level module.</summary>
    private static string Dedent(IReadOnlyList<string> lines)
    {
        var minIndent = int.MaxValue;
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            minIndent = Math.Min(minIndent, LeadingWhitespaceWidth(line));
        }

        if (minIndent is int.MaxValue or 0)
        {
            return string.Join('\n', lines);
        }

        return string.Join('\n', lines.Select(line => line.Length >= minIndent ? line[minIndent..] : line));
    }

    /// <summary>
    /// Prefixes each bare inline-module qualifier (<c>Name.</c>) with the scope's composed path
    /// (<c>Scope.Name.</c>), when the head segment is one of <paramref name="childNames"/> and is not
    /// already part of a longer qualifier. String literals are skipped so an occurrence inside a
    /// string is never rewritten.
    /// </summary>
    private static string RewriteInlineQualifiers(string source, IReadOnlyList<string> childNames, string scopePrefix)
    {
        if (childNames.Count == 0)
        {
            return source;
        }

        var alternation = string.Join('|', childNames.Select(Regex.Escape));
        // Head segment must not be preceded by a word char or a dot (so it is a genuine head, not a
        // deeper segment), and must be followed by `.` + an identifier start (a qualified reference).
        var pattern = new Regex($@"(?<![A-Za-z0-9_.])(?<head>{alternation})\.(?=[A-Za-z_])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var sb = new StringBuilder(source.Length + 16);
        var inString = false;
        var i = 0;
        while (i < source.Length)
        {
            var ch = source[i];
            if (ch == '"')
            {
                inString = !inString;
                sb.Append(ch);
                i++;
                continue;
            }

            if (inString)
            {
                if (ch == '\\' && i + 1 < source.Length)
                {
                    sb.Append(ch).Append(source[i + 1]);
                    i += 2;
                    continue;
                }

                sb.Append(ch);
                i++;
                continue;
            }

            var m = pattern.Match(source, i);
            if (m.Success && m.Index == i)
            {
                sb.Append(scopePrefix).Append('.').Append(m.Groups["head"].Value).Append('.');
                i += m.Length;
                continue;
            }

            sb.Append(ch);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>Rejects a trailing expression or an <c>external</c>/<c>import</c> in a lifted inline module (ASH021).</summary>
    private static void ValidateInlineModuleBody(string blockSource, string composedName, string displayPath)
    {
        foreach (var raw in blockSource.Split('\n'))
        {
            var trimmed = raw.Trim();
            if (trimmed.StartsWith("import ", StringComparison.Ordinal) || trimmed.StartsWith("import\t", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"[ASH021] Inline module '{composedName}' may not contain an 'import' ({displayPath}).");
            }
        }

        var diag = new Diagnostics();
        var program = new Parser(blockSource, diag).ParseProgram();
        if (diag.StructuredErrors.Count > 0)
        {
            // Let the normal compile surface the syntax error against the combined source.
            return;
        }

        foreach (var item in program.Items)
        {
            if (item is TopLevelItem.External)
            {
                throw new InvalidOperationException(
                    $"[ASH021] Inline module '{composedName}' may not contain an 'external' declaration ({displayPath}).");
            }
        }

        if (program.Body is not null)
        {
            throw new InvalidOperationException(
                $"[ASH021] Inline module '{composedName}' may not contain a trailing expression ({displayPath}).");
        }
    }

    private static string? ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    private static List<string> ReadStringArray(JsonElement root, string name)
    {
        var result = new List<string>();
        if (!root.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in prop.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            {
                result.Add(item.GetString()!);
            }
        }

        return result;
    }

    private static string ResolvePath(string projectDirectory, string pathValue)
    {
        return Path.GetFullPath(Path.IsPathRooted(pathValue)
            ? pathValue
            : Path.Combine(projectDirectory, pathValue));
    }

    // Project stitching slices the host string while it constructs a new source file. Keep that
    // implementation detail explicit at this boundary; Lexer itself always exposes canonical byte
    // spans to compiler consumers.
    private sealed class StringOffsetLexer
    {
        private readonly Lexer _lexer;
        private readonly SourceTextIndex _index;

        public StringOffsetLexer(string source, Diagnostics diagnostics)
        {
            _lexer = new Lexer(source, diagnostics);
            _index = new SourceTextIndex(source);
        }

        public Token Next()
        {
            Token token = _lexer.Next();
            int start = _index.ToUtf16Offset(token.Position);
            int end = _index.ToUtf16Offset(token.End);
            return token with { Position = start, Length = end - start };
        }
    }
}
