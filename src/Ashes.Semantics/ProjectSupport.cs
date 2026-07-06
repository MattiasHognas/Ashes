using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ashes.Frontend;

namespace Ashes.Semantics;

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
);

public sealed record ProjectModule(
    string ModuleName,
    string FilePath,
    string Source,
    IReadOnlyList<string> Imports,
    IReadOnlyDictionary<string, string> Aliases,
    IReadOnlyList<ImportSelector> Selectors
);

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

public sealed record ProjectCompilationPlan(
    AshesProject Project,
    IReadOnlyList<ProjectModule> OrderedModules,
    ProjectModule EntryModule,
    IReadOnlySet<string> ImportedStdModules,
    IReadOnlyDictionary<string, string> MergedAliases
);

public readonly record struct ParsedImportHeader(
    IReadOnlyList<string> ImportNames,
    string SourceWithoutImports,
    IReadOnlyDictionary<string, string> ImportAliases,
    IReadOnlyList<ImportSelector> ImportSelectors
);

public readonly record struct CombinedCompilationLayout(
    string Source,
    int EntryOffset,
    int BodyStart,
    IReadOnlyList<(string FilePath, int StartOffset, int EndOffset)> ModuleOffsets
);

public static class ProjectSupport
{
    // ── Inline modules (LANGUAGE_SPEC §13.1) ────────────────────────────────────────────────────
    // An inline `module Name = <indented block>` is lifted, before shaping/combination, into a
    // synthetic module whose name is the file-composed path (`File.Name`). Within the defining file
    // a bare qualifier `Name.member` is rewritten to the composed path `File.Name.member`, which the
    // rest of the pipeline (mangling, qualified-reference resolution, cross-file imports) then treats
    // exactly like a separate `File/Name.ash` file — so inline ↔ file promotion is transparent.

    /// <summary>A header line <c>module Name =</c> at column <see cref="Indent"/>; the block body is the run of lines indented past it.</summary>
    private static readonly Regex InlineModuleHeader = new(
        @"^(?<indent>[ \t]*)module[ \t]+(?<name>[A-Z][A-Za-z0-9_]*)[ \t]*=[ \t]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
        bool HasTrailingExpression = true);

    public const string ImportModulePattern = @"^\s*import\s+([A-Z][A-Za-z0-9_]*(?:\.[A-Z][A-Za-z0-9_]*)*)(?:\.([a-z_][A-Za-z0-9_]*))?(?:\s+as\s+([A-Za-z][A-Za-z0-9_]*))?\s*$";

    /// <summary>Message for <c>ASH016</c>: two unqualified selectors collide on the same name.</summary>
    private static string ConflictingSelectorMessage(string name) =>
        $"Conflicting unqualified import selectors for '{name}'.";

    private static readonly Regex ImportLine = new(
        ImportModulePattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant
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
        "effect", "uses", "perform", "handle"
    };

    public static IReadOnlyCollection<string> KnownStandardLibraryModules => KnownStdModules;

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

        var sourceRoots = ReadStringArray(root, "sourceRoots");
        if (sourceRoots.Count == 0)
        {
            sourceRoots.Add(".");
        }

        var include = ReadStringArray(root, "include");

        var outDirValue = ReadString(root, "outDir");
        var outDir = ResolvePath(projectDirectory, string.IsNullOrWhiteSpace(outDirValue) ? "out" : outDirValue!);

        var name = ReadString(root, "name");
        var target = ReadString(root, "target");

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
        );
    }

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
                    continue;
                }

                imports.Add(modulePath);
                if (aliasGroup is not null)
                {
                    var alias = aliasGroup;
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
                continue;
            }

            if (inHeader && trimmed.StartsWith("import ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Invalid import syntax in {displayPath}:{lineIndex}. Expected 'import Foo', 'import Foo.Bar', 'import Foo.name [as alias]', or 'import Foo.Bar as Alias'.");
            }

            if (inHeader && (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal)))
            {
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
        var sourceWithoutImports = ApplySelectorRenames(string.Join('\n', sourceLines), selectors);
        return new ParsedImportHeader(imports, sourceWithoutImports, aliases, selectors);
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

    public static ProjectCompilationPlan BuildCompilationPlan(AshesProject project)
    {
        if (BuiltinRegistry.IsReservedModuleNamespace(project.EntryModuleName))
        {
            throw new InvalidOperationException(
                "Module name 'Ashes' is reserved for the standard library.");
        }

        var searchRoots = project.SourceRoots.Concat(project.Include).ToArray();
        var resolvedByModuleName = new Dictionary<string, ProjectModule>(StringComparer.Ordinal);
        var resolvedByPath = new Dictionary<string, ProjectModule>(StringComparer.OrdinalIgnoreCase);
        var states = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var traversal = new Stack<ProjectModule>();
        var ordered = new List<ProjectModule>();
        var importedStdModules = new HashSet<string>(StringComparer.Ordinal);
        // Inline submodules lifted from each file, keyed by the file's full path, ordered before the file.
        var inlineChildrenByPath = new Dictionary<string, List<ProjectModule>>(StringComparer.OrdinalIgnoreCase);

        var entryModule = LoadModule(project.EntryModuleName, project.EntryPath);
        Visit(entryModule);

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

        return new ProjectCompilationPlan(project, ordered, entryModule, importedStdModules, mergedAliases);

        void Visit(ProjectModule module)
        {
            if (states.TryGetValue(module.FilePath, out var state))
            {
                if (state == 2)
                {
                    return;
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

            states[module.FilePath] = 1;
            traversal.Push(module);
            foreach (var import in module.Imports)
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
                        Visit(stdModule);
                    }

                    continue;
                }

                if (import.StartsWith("Ashes.", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Unknown standard library module '{import}'. Known modules: {string.Join(", ", KnownStdModules)}.");
                }

                var dependency = ResolveImport(import);
                Visit(dependency);
            }

            traversal.Pop();
            states[module.FilePath] = 2;
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

        ProjectModule ResolveImport(string moduleName)
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
                var module = LoadModule(moduleName, projectMatches[0]);
                resolvedByModuleName[moduleName] = module;
                return module;
            }

            var shippedLibraryPath = GetShippedLibraryModulePath(moduleRelativePath);
            if (shippedLibraryPath is not null)
            {
                var module = LoadModule(moduleName, shippedLibraryPath);
                resolvedByModuleName[moduleName] = module;
                return module;
            }

            // The path may name an inline submodule of an enclosing file (`import Geom.Vec` where
            // `Vec` is `module Vec` inside `Geom.ash`). Load the nearest enclosing file, which lifts
            // and registers its inline submodules, then retry.
            for (var dot = moduleName.LastIndexOf('.'); dot > 0; dot = moduleName.LastIndexOf('.', dot - 1))
            {
                var enclosing = moduleName[..dot];
                var enclosingMatches = GetExistingModuleCandidates(searchRoots, GetModuleRelativePath(enclosing));
                if (enclosingMatches.Count == 1)
                {
                    LoadModule(enclosing, enclosingMatches[0]);
                    if (resolvedByModuleName.TryGetValue(moduleName, out var inlineResolved))
                    {
                        return inlineResolved;
                    }
                }
            }

            throw new InvalidOperationException(BuildMissingModuleMessage(moduleName, searchRoots, moduleRelativePath));
        }

        ProjectModule LoadModule(string moduleName, string filePath)
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

            var aliasMap = new Dictionary<string, string>(aliases, StringComparer.Ordinal);
            (imports, selectors) = NormalizeTypeSelectors(imports, selectors, aliasMap, IsResolvableModule);
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

        bool IsResolvableModule(string name)
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

            var parsed = ParseImportHeader(File.ReadAllText(matches[0]), matches[0]);
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

    public static bool IsStdModule(string moduleName)
    {
        return KnownStdModules.Contains(moduleName);
    }

    public static string BuildCompilationSource(ProjectCompilationPlan plan)
    {
        return BuildCompilationLayout(plan).Source;
    }

    public static CombinedCompilationLayout BuildCompilationLayout(ProjectCompilationPlan plan, string? entrySourceOverride = null)
    {
        return BuildCompilationLayoutCore(plan.OrderedModules, plan.EntryModule, entrySourceOverride);
    }

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
                []));
        }

        var entryModule = new ProjectModule(
            "Main",
            entryFilePath,
            ApplySelectorRenames(entryOuter, entrySelectors),
            importNames.ToList(),
            new Dictionary<string, string>(StringComparer.Ordinal),
            entrySelectors);
        orderedModules.Add(entryModule);

        foreach (var importName in importNames)
        {
            if (IsStdModule(importName))
            {
                if (TryLoadStandardLibraryModule(importName, out var stdModule))
                {
                    Visit(stdModule);
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

        // Use the entry module's selector-rewritten source (intrinsic and aliased-type selectors are
        // realized by in-place renaming) rather than the raw imports-stripped source, so single-file
        // selector imports take effect in the entry expression just as they do in project mode.
        return BuildCompilationLayoutCore(orderedModules, entryModule, entryModule.Source);

        void Visit(ProjectModule module)
        {
            if (states.TryGetValue(module.FilePath, out var state))
            {
                if (state == 2)
                {
                    return;
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
                    Visit(dependency);
                }
            }

            traversal.Pop();
            states[module.FilePath] = 2;
            if (seenModules.Add(module.ModuleName))
            {
                orderedModules.Insert(orderedModules.Count - 1, module);
            }
        }
    }

    public static string SanitizeModuleBindingName(string moduleName)
    {
        return moduleName.Replace('.', '_');
    }

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
        var shapes = new Dictionary<string, ModuleSourceShape>(StringComparer.Ordinal);
        foreach (var module in orderedModules)
        {
            var source = string.Equals(module.FilePath, entryModule.FilePath, StringComparison.OrdinalIgnoreCase)
                ? entrySourceOverride ?? module.Source
                : module.Source;
            shapes[module.ModuleName] = ShapeModuleSource(source);
        }

        var exportedNames = orderedModules.ToDictionary(
            module => module.ModuleName,
            module => GetExportNames(shapes[module.ModuleName]),
            StringComparer.Ordinal);

        ValidateSelectorExports(orderedModules, exportedNames);

        var entryShape = shapes[entryModule.ModuleName];
        var nonEntryModules = orderedModules
            .Where(module => !string.Equals(module.FilePath, entryModule.FilePath, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var prefix = new StringBuilder();

        // Track module offset regions as content is appended
        var moduleOffsets = new List<(string FilePath, int StartOffset, int EndOffset)>();

        // Entry module type declarations
        if (entryShape.TypeDeclarationsSource.Length > 0)
        {
            var start = prefix.Length;
            prefix.Append(entryShape.TypeDeclarationsSource);
            moduleOffsets.Add((entryModule.FilePath, start, prefix.Length));
        }

        // Non-entry module type declarations
        foreach (var module in nonEntryModules)
        {
            var typeDecls = shapes[module.ModuleName].TypeDeclarationsSource;
            if (typeDecls.Length > 0)
            {
                var start = prefix.Length;
                prefix.Append(typeDecls);
                moduleOffsets.Add((module.FilePath, start, prefix.Length));
            }
        }

        var usedBindingNames = new Dictionary<string, string>(StringComparer.Ordinal);

        // Flat modules contribute genuine top-level declarations, which must precede the legacy
        // nested-let pyramid (everything after the first pyramid `let ... in` is the trailing body).
        foreach (var module in nonEntryModules.Where(module => shapes[module.ModuleName].IsFlat))
        {
            var start = prefix.Length;
            prefix.Append(BuildModuleBindingPrefix(module, shapes[module.ModuleName], exportedNames, usedBindingNames, flat: true));
            if (prefix.Length > start)
            {
                moduleOffsets.Add((module.FilePath, start, prefix.Length));
            }
        }

        var legacyBindingEmitted = false;
        foreach (var module in nonEntryModules.Where(module => !shapes[module.ModuleName].IsFlat))
        {
            var start = prefix.Length;
            prefix.Append(BuildModuleBindingPrefix(module, shapes[module.ModuleName], exportedNames, usedBindingNames));
            if (prefix.Length > start)
            {
                moduleOffsets.Add((module.FilePath, start, prefix.Length));
                legacyBindingEmitted = true;
            }
        }

        // A hoisted flat declaration (a flat `let` value, or a `provide` whose implementation is an
        // expression) ends with a newline, not an `in`. Without a following legacy nested-let binding
        // to open the trailing body, the parenthesized entry expression would be absorbed as an
        // application argument of that last value (e.g. `given (y) -> x - y` swallowing `(entry)` as
        // `y(entry)`), so introduce a boundary binding whose `in` makes the entry a proper let body.
        // The `let` boundary is safe because flat-value/provider parsing suppresses `let` as an
        // argument. This is needed whenever any module content precedes the entry with no legacy `in`.
        bool moduleContentPrecedesEntry = prefix.Length > entryShape.TypeDeclarationsSource.Length;
        if (moduleContentPrecedesEntry && !legacyBindingEmitted)
        {
            prefix.Append("let __ashes_module_boundary = 0 in ");
        }

        var entryExpression = BuildEntryExpression(entryModule, entryShape, exportedNames);
        var hasPrefixBeforeBody = prefix.Length > entryShape.TypeDeclarationsSource.Length;
        if (hasPrefixBeforeBody)
        {
            // A declarations-only entry (e.g. a module file compiled directly) has no trailing
            // expression, but the parenthesized flat entry block below must end in one — otherwise
            // the parser reports ASH003 at the closing paren. Synthesize an inert trailing value;
            // a program's trailing value is discarded, so this is unobservable.
            if (!entryShape.HasTrailingExpression || string.IsNullOrWhiteSpace(entryExpression))
            {
                entryExpression = string.IsNullOrWhiteSpace(entryExpression)
                    ? "0"
                    : entryExpression + "\n0";
            }

            // Module bindings precede the entry body, so the body must be parenthesized: a flat
            // entry block (declarations + trailing expression) is only recognized inside parens
            // (ParseParenthesizedBody), and after a legacy binding chain's trailing `in` a bare
            // flat block would not parse at all. This holds whether or not the entry contributed
            // hoisted type declarations (they sit at the very top, before every module binding).
            prefix.Append('(');
            var entryOffset = prefix.Length;
            prefix.Append(entryExpression);
            prefix.Append(')');
            moduleOffsets.Add((entryModule.FilePath, entryOffset, entryOffset + entryExpression.Length));
            return new CombinedCompilationLayout(prefix.ToString(), entryOffset, entryShape.TypeDeclarationsSource.Length, moduleOffsets);
        }

        if (entryShape.TypeDeclarationsSource.Length == 0 && prefix.Length == 0)
        {
            moduleOffsets.Add((entryModule.FilePath, 0, entryExpression.Length));
            return new CombinedCompilationLayout(entryExpression, 0, 0, moduleOffsets);
        }

        // Only the entry's own (hoisted) type declarations precede the body: append it bare — the
        // combined source is then an ordinary flat program (type declarations, then the body), and
        // the type-declaration region keeps its exact original offsets for span mapping.
        var offset = prefix.Length;
        prefix.Append(entryExpression);
        moduleOffsets.Add((entryModule.FilePath, offset, prefix.Length));
        return new CombinedCompilationLayout(prefix.ToString(), offset, entryShape.TypeDeclarationsSource.Length, moduleOffsets);
    }

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
    /// <c>type</c> declarations, plus legacy pyramid bindings — used to validate selector imports
    /// (mirrors the built-in export tables). Returns an empty set for sources the parser rejects.
    /// </summary>
    private static IReadOnlySet<string> CollectAllExportNames(string source)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var diag = new Diagnostics();
        var program = new Parser(source, diag).ParseProgram();
        if (diag.StructuredErrors.Count > 0)
        {
            return names;
        }

        foreach (var item in program.Items)
        {
            switch (item)
            {
                case TopLevelItem.LetDecl letDecl:
                    names.Add(letDecl.Name);
                    break;
                case TopLevelItem.RecursiveGroup recursiveGroup:
                    foreach (var (name, _) in recursiveGroup.Bindings)
                    {
                        names.Add(name);
                    }

                    break;
                case TopLevelItem.Type typeDecl:
                    names.Add(typeDecl.Decl.Name);
                    break;
            }
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

    private static string BuildEntryExpression(
        ProjectModule entryModule,
        ModuleSourceShape entryShape,
        IReadOnlyDictionary<string, IReadOnlyList<string>> exportedNames)
    {
        var referencedNames = CollectReferencedNames(entryShape.RawExpressionSource);
        var aliases = BuildVisibleAliases(
            entryModule,
            [],
            [],
            referencedNames,
            exportedNames);

        // A flat top-level entry parses as a flat-declaration block (`decl decl ... trailingExpr`,
        // folded into nested lets by the parser inside the stitched parentheses). Wrapping it in the
        // ordinary `let alias = value in <body>` prelude would make the alias body an ordinary
        // expression, so the following flat declarations would no longer be folded and parsing fails.
        // Emit the alias prelude as flat declarations instead so the whole entry stays one flat block.
        return entryShape.IsFlat
            ? ApplyAliasesAsFlatDeclarations(entryShape.RawExpressionSource, aliases)
            : ApplyAliases(entryShape.RawExpressionSource, aliases);
    }

    /// <summary>
    /// Prepends the alias prelude as flat top-level declarations (no <c>in</c>) rather than wrapping
    /// the source in nested <c>let ... in</c> bindings. Used for the flat top-level entry, whose body
    /// is itself a flat-declaration block: the synthetic alias bindings join that block so the parser
    /// folds them together with the entry's own declarations and trailing expression.
    /// </summary>
    private static string ApplyAliasesAsFlatDeclarations(
        string source,
        IReadOnlyList<KeyValuePair<string, string>> aliases)
    {
        var trimmed = source.Trim();
        if (aliases.Count == 0)
        {
            return trimmed;
        }

        var builder = new StringBuilder();
        foreach (var alias in aliases)
        {
            builder.Append("let ").Append(alias.Key).Append(" = ").Append(alias.Value).Append('\n');
        }

        builder.Append(trimmed);
        return builder.ToString();
    }

    private static string BuildModuleBindingPrefix(
        ProjectModule module,
        ModuleSourceShape shape,
        IReadOnlyDictionary<string, IReadOnlyList<string>> exportedNames,
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
            foreach (var binding in group.Bindings)
            {
                var generatedBindingName = $"{moduleBindingName}_{binding.Name}";
                if (usedBindingNames.TryGetValue(generatedBindingName, out existingModuleName))
                {
                    throw new InvalidOperationException(
                        $"Generated export binding collision for '{generatedBindingName}': '{existingModuleName}' and '{module.ModuleName}'.");
                }

                usedBindingNames[generatedBindingName] = module.ModuleName;
            }

            // Within a `let rec ... and ...` group every member is visible to the others, so each
            // member value resolves sibling references to the group's generated names.
            var recursiveBindings = group.IsRecursiveGroup
                ? group.Bindings.Select(binding => binding.Name).ToArray()
                : [];

            prefix.Append("let ");
            if (group.IsRecursiveGroup)
            {
                prefix.Append("recursive ");
            }

            for (var i = 0; i < group.Bindings.Count; i++)
            {
                var binding = group.Bindings[i];
                var referencedNames = CollectReferencedNames(binding.ValueSource);
                var aliases = BuildVisibleAliases(
                    module,
                    availableLocalBindings,
                    recursiveBindings,
                    referencedNames,
                    exportedNames);

                if (i > 0)
                {
                    prefix.Append(" and ");
                }

                // A flat `let rec ... and ...` group is stitched as a genuine top-level declaration, so
                // each member value must stay a bare function literal; its aliases are resolved by
                // renaming identifiers in place rather than wrapping them in `let ... in` (which would
                // make the value a non-function expression and force eager sibling evaluation). The
                // legacy nested form keeps the original wrapping path so its codegen is unchanged — in
                // particular wrapping is what resolves synthetic cross-module/qualified alias names that
                // never appear as bare identifier tokens.
                var renderedValue = flat && group.IsRecursiveGroup
                    ? ApplyAliasesByRenaming(binding.ValueSource, aliases)
                    : ApplyAliases(binding.ValueSource, aliases);

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

            // Flat modules are stitched as genuine top-level declarations (no `in`): a `let rec ... and
            // ...` group cannot be expressed in the nested expression pyramid the legacy form uses, and
            // top-level declarations keep mutual recursion intact while staying visible to the body.
            prefix.Append(flat ? "\n" : " in ");

            foreach (var binding in group.Bindings)
            {
                availableLocalBindings.Add(binding.Name);
            }
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
                .Append(ApplyAliases(shape.ExpressionBodySource, bodyAliases))
                .Append(") in ");
        }

        return prefix.ToString();
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
                if (!referencedVariables.Contains(exportName) || localNames.Contains(exportName))
                {
                    if (hasReferencedShortQualifier
                        && referencedQualifiedReferences.Contains(new QualifiedReference(shortQualifier, exportName)))
                    {
                        aliases.Add(new KeyValuePair<string, string>(
                            $"{SanitizeModuleBindingName(shortQualifier)}_{exportName}",
                            $"{SanitizeModuleBindingName(import)}_{exportName}"));
                    }

                    continue;
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
        }

        return aliases;
    }

    private static string GetLeafModuleQualifier(string moduleName)
    {
        var lastDot = moduleName.LastIndexOf('.');
        return lastDot >= 0 ? moduleName[(lastDot + 1)..] : moduleName;
    }

    private static string ApplyAliases(string source, IReadOnlyList<KeyValuePair<string, string>> aliases)
    {
        var wrapped = source.Trim();
        foreach (var alias in aliases)
        {
            wrapped = $"let {alias.Key} = {alias.Value} in {wrapped}";
        }

        return wrapped;
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

        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var alias in aliases)
        {
            renames[alias.Key] = alias.Value;
        }

        var diag = new Diagnostics();
        var lexer = new Lexer(trimmed, diag);
        var builder = new StringBuilder();
        var copiedUpTo = 0;
        var previousKind = TokenKind.EOF;

        while (true)
        {
            var token = lexer.Next();
            if (token.Kind == TokenKind.EOF)
            {
                break;
            }

            // Only rewrite a bare unqualified occurrence of the alias. An identifier that follows a
            // `.` is a qualified-member suffix (e.g. the `print` in an already-realized
            // `Ashes.IO.print`), never the imported binding, so leaving it untouched keeps the rewrite
            // idempotent when it is applied more than once over the same source.
            if (token.Kind == TokenKind.Ident
                && previousKind != TokenKind.Dot
                && renames.TryGetValue(token.Text, out var replacement))
            {
                builder.Append(trimmed[copiedUpTo..token.Position]).Append(replacement);
                copiedUpTo = token.Position + token.Length;
            }

            previousKind = token.Kind;
        }

        builder.Append(trimmed[copiedUpTo..]);
        return builder.ToString();
    }

    private static IReadOnlyList<string> GetExportNames(ModuleSourceShape shape)
    {
        if (shape.TopLevelBindings.Count > 0)
        {
            return shape.TopLevelBindings
                .SelectMany(group => group.Bindings)
                .Select(binding => binding.Name)
                .ToArray();
        }

        return string.IsNullOrWhiteSpace(shape.LegacyExportName)
            ? []
            : [shape.LegacyExportName!];
    }

    private static ModuleSourceShape ShapeModuleSource(string source)
    {
        if (TryShapeFlatModule(source, out var flatShape))
        {
            return flatShape;
        }

        var bodyStart = FindExpressionBodyStart(source);
        var typeDeclarationsSource = bodyStart > 0 ? source[..bodyStart] : string.Empty;
        var rawExpressionSource = bodyStart < source.Length ? source[bodyStart..].Trim() : string.Empty;
        var fragments = ExtractTopLevelBindings(rawExpressionSource, out var remainingBody);
        var topLevelBindings = fragments
            .Select(fragment => new ModuleBindingGroup([fragment], fragment.IsRecursive))
            .ToArray();
        var legacyExportName = topLevelBindings.Length == 0 ? TryInferExportName(source) : null;

        return new ModuleSourceShape(typeDeclarationsSource, rawExpressionSource, remainingBody, topLevelBindings, legacyExportName, IsFlat: false);
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
            switch (item)
            {
                case TopLevelItem.Type typeItem:
                    {
                        var span = AstSpans.GetOrDefault(typeItem.Decl);
                        if (span.End <= span.Start || span.End > source.Length)
                        {
                            return false;
                        }

                        // Each declaration carries a trailing newline so concatenated type sources (and the
                        // bindings that follow them in the combined source) stay lexically separated.
                        typeDeclarations.Append(source[span.Start..span.End]).Append('\n');
                        hoistedSpans.Add((span.Start, span.End));
                        cursor = span.End;
                        break;
                    }

                case TopLevelItem.Capability capabilityItem:
                    {
                        // Capability declarations hoist exactly like type declarations: they are
                        // program-wide (operations resolve as qualified Capability.op from any module)
                        // and carry no value binding.
                        var span = AstSpans.GetOrDefault(capabilityItem.Decl);
                        if (span.End <= span.Start || span.End > source.Length)
                        {
                            return false;
                        }

                        typeDeclarations.Append(source[span.Start..span.End]).Append('\n');
                        hoistedSpans.Add((span.Start, span.End));
                        hoistedCapabilityOrProvide = true;
                        cursor = span.End;
                        break;
                    }

                case TopLevelItem.Provide provideItem:
                    {
                        // Providers are program-wide static evidence, hoisted like capabilities.
                        var span = AstSpans.GetOrDefault(provideItem.Decl);
                        if (span.End <= span.Start || span.End > source.Length)
                        {
                            return false;
                        }

                        typeDeclarations.Append(source[span.Start..span.End]).Append('\n');
                        hoistedSpans.Add((span.Start, span.End));
                        hoistedCapabilityOrProvide = true;
                        cursor = span.End;
                        break;
                    }

                case TopLevelItem.External externalItem:
                    {
                        // `external` is never exported and carries no value the stitcher needs; skip it.
                        var span = AstSpans.GetOrDefault(externalItem.Decl);
                        if (span.End <= span.Start || span.End > source.Length)
                        {
                            return false;
                        }

                        hoistedSpans.Add((span.Start, span.End));
                        cursor = span.End;
                        break;
                    }

                case TopLevelItem.LetDecl letDecl:
                    {
                        if (!TryExtractFlatBindingValue(source, letDecl.Value, ref cursor, out var valueSource, out var annotation))
                        {
                            return false;
                        }

                        groups.Add(new ModuleBindingGroup(
                            [new ModuleBindingFragment(letDecl.Name, valueSource, letDecl.IsRecursive, annotation)],
                            letDecl.IsRecursive));
                        hasFlatBinding = true;
                        break;
                    }

                case TopLevelItem.RecursiveGroup recursiveGroup:
                    {
                        var members = new List<ModuleBindingFragment>();
                        foreach (var (name, value) in recursiveGroup.Bindings)
                        {
                            if (!TryExtractFlatBindingValue(source, value, ref cursor, out var valueSource))
                            {
                                return false;
                            }

                            members.Add(new ModuleBindingFragment(name, valueSource, IsRecursive: true));
                        }

                        groups.Add(new ModuleBindingGroup(members, IsRecursiveGroup: true));
                        hasFlatBinding = true;
                        break;
                    }

                default:
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

        // For an imported (non-entry) module the trailing expression is dropped and the stitcher uses
        // the binding groups, so RawExpressionSource is unused. For an entry module it is the program
        // body, so preserve the flat `let` declarations and trailing expression here (with the hoisted
        // type/external declarations removed, since those are emitted up front) rather than discarding it.
        shape = new ModuleSourceShape(
            TypeDeclarationsSource: typeDeclarations.ToString(),
            RawExpressionSource: RemoveSpans(source, hoistedSpans).Trim(),
            ExpressionBodySource: string.Empty,
            TopLevelBindings: groups,
            LegacyExportName: null,
            IsFlat: true,
            HasTrailingExpression: program.Body is not null);
        return true;
    }

    /// <summary>
    /// Returns <paramref name="source"/> with the given (already source-ordered, non-overlapping)
    /// spans removed, preserving everything in between. Used to strip hoisted type/external declarations
    /// out of the flat entry expression while keeping the flat <c>let</c> declarations and trailing
    /// expression intact.
    /// </summary>
    private static string RemoveSpans(string source, IReadOnlyList<(int Start, int End)> spans)
    {
        if (spans.Count == 0)
        {
            return source;
        }

        var builder = new StringBuilder();
        var copiedUpTo = 0;
        foreach (var (start, end) in spans)
        {
            if (start > copiedUpTo)
            {
                builder.Append(source[copiedUpTo..start]);
            }

            copiedUpTo = Math.Max(copiedUpTo, end);
        }

        builder.Append(source[copiedUpTo..]);
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

        var valueEnd = AstSpans.GetOrDefault(value).End;
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
        var lexer = new Lexer(source[from..], diag);
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
        var lexer = new Lexer(source[from..], diag);

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

        var collected = new List<string>();
        while (token.Kind == TokenKind.Ident)
        {
            collected.Add(token.Text);
            token = lexer.Next();
        }

        if (token.Kind != TokenKind.Equals)
        {
            return false;
        }

        parameters = collected;
        valueStart = from + token.Position + token.Text.Length;
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
        var lexer = new Lexer(source, diag);
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
        if (equals.Kind == TokenKind.Ident)
        {
            while (equals.Kind == TokenKind.Ident)
            {
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

        if (equals.Kind != TokenKind.Equals)
        {
            return false;
        }

        var valueStart = equals.Position + equals.Text.Length;
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
        var diag = new Diagnostics();
        var lexer = new Lexer(source, diag);
        var tok = lexer.Next();

        while (tok.Kind == TokenKind.Type)
        {
            tok = lexer.Next();
            tok = lexer.Next();
            if (tok.Kind == TokenKind.LParen)
            {
                while (tok.Kind != TokenKind.RParen && tok.Kind != TokenKind.EOF)
                {
                    tok = lexer.Next();
                }

                tok = lexer.Next();
            }

            tok = lexer.Next();

            while (tok.Kind == TokenKind.Pipe)
            {
                tok = lexer.Next();
                tok = lexer.Next();

                if (tok.Kind == TokenKind.LParen)
                {
                    while (tok.Kind != TokenKind.RParen && tok.Kind != TokenKind.EOF)
                    {
                        tok = lexer.Next();
                    }

                    tok = lexer.Next();
                }
            }
        }

        return tok.Kind == TokenKind.EOF ? source.Length : tok.Position;
    }

    private static bool TryLoadStandardLibraryModule(string moduleName, out ProjectModule module)
    {
        module = null!;
        if (!StdModules.TryGetValue(moduleName, out var descriptor) || string.IsNullOrWhiteSpace(descriptor.ResourceName))
        {
            return false;
        }

        if (!EmbeddedStdSources.Value.TryGetValue(descriptor.ResourceName!, out var source))
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
            parsed.ImportSelectors);
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

            using var stream = assembly.GetManifestResourceStream(descriptor.ResourceName!);
            if (stream is null)
            {
                throw new InvalidOperationException($"Missing embedded standard library resource '{descriptor.ResourceName}'.");
            }

            using var reader = new StreamReader(stream);
            sources[descriptor.ResourceName!] = reader.ReadToEnd();
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
                    Visit(letDecl.Value);
                    break;
                case TopLevelItem.RecursiveGroup recursiveGroup:
                    foreach (var binding in recursiveGroup.Bindings)
                    {
                        Visit(binding.Value);
                    }

                    break;
                default:
                    break;
            }
        }

        if (program.Body is not null)
        {
            Visit(program.Body);
        }

        return new ReferencedNames(names, qualifiedReferences);

        void Visit(Expr expr)
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
                case Expr.FloatLit:
                case Expr.StrLit:
                case Expr.BoolLit:
                    break;
                case Expr.Add add:
                    Visit(add.Left);
                    Visit(add.Right);
                    break;
                case Expr.Subtract sub:
                    Visit(sub.Left);
                    Visit(sub.Right);
                    break;
                case Expr.Multiply mul:
                    Visit(mul.Left);
                    Visit(mul.Right);
                    break;
                case Expr.Divide div:
                    Visit(div.Left);
                    Visit(div.Right);
                    break;
                case Expr.BitwiseAnd bitAnd:
                    Visit(bitAnd.Left);
                    Visit(bitAnd.Right);
                    break;
                case Expr.BitwiseOr bitOr:
                    Visit(bitOr.Left);
                    Visit(bitOr.Right);
                    break;
                case Expr.BitwiseXor bitXor:
                    Visit(bitXor.Left);
                    Visit(bitXor.Right);
                    break;
                case Expr.ShiftLeft shiftLeft:
                    Visit(shiftLeft.Left);
                    Visit(shiftLeft.Right);
                    break;
                case Expr.ShiftRight shiftRight:
                    Visit(shiftRight.Left);
                    Visit(shiftRight.Right);
                    break;
                case Expr.BitwiseNot bitwiseNot:
                    Visit(bitwiseNot.Operand);
                    break;
                case Expr.GreaterThan gt:
                    Visit(gt.Left);
                    Visit(gt.Right);
                    break;
                case Expr.GreaterOrEqual ge:
                    Visit(ge.Left);
                    Visit(ge.Right);
                    break;
                case Expr.LessThan lt:
                    Visit(lt.Left);
                    Visit(lt.Right);
                    break;
                case Expr.LessOrEqual le:
                    Visit(le.Left);
                    Visit(le.Right);
                    break;
                case Expr.Equal eq:
                    Visit(eq.Left);
                    Visit(eq.Right);
                    break;
                case Expr.NotEqual ne:
                    Visit(ne.Left);
                    Visit(ne.Right);
                    break;
                case Expr.ResultPipe pipe:
                    Visit(pipe.Left);
                    Visit(pipe.Right);
                    break;
                case Expr.ResultMapErrorPipe pipe:
                    Visit(pipe.Left);
                    Visit(pipe.Right);
                    break;
                case Expr.Let letExpr:
                    Visit(letExpr.Value);
                    Visit(letExpr.Body);
                    break;
                case Expr.LetResult letResultExpr:
                    Visit(letResultExpr.Value);
                    Visit(letResultExpr.Body);
                    break;
                case Expr.LetRecursive letRecursiveExpr:
                    Visit(letRecursiveExpr.Value);
                    Visit(letRecursiveExpr.Body);
                    break;
                case Expr.If ifExpr:
                    Visit(ifExpr.Cond);
                    Visit(ifExpr.Then);
                    Visit(ifExpr.Else);
                    break;
                case Expr.Lambda lambda:
                    Visit(lambda.Body);
                    break;
                case Expr.Call call:
                    Visit(call.Func);
                    Visit(call.Arg);
                    break;
                case Expr.TupleLit tuple:
                    foreach (var element in tuple.Elements)
                    {
                        Visit(element);
                    }
                    break;
                case Expr.ListLit list:
                    foreach (var element in list.Elements)
                    {
                        Visit(element);
                    }
                    break;
                case Expr.Cons cons:
                    Visit(cons.Head);
                    Visit(cons.Tail);
                    break;
                case Expr.Match match:
                    Visit(match.Value);
                    foreach (var matchCase in match.Cases)
                    {
                        if (matchCase.Guard is not null)
                        {
                            Visit(matchCase.Guard);
                        }
                        Visit(matchCase.Body);
                    }
                    break;
                case Expr.Await awaitExpr:
                    Visit(awaitExpr.Task);
                    break;
                case Expr.RecordLit rl:
                    foreach (var field in rl.Fields)
                        Visit(field.Value);
                    break;
                case Expr.RecordUpdate ru:
                    Visit(ru.Target);
                    foreach (var update in ru.Updates)
                        Visit(update.Value);
                    break;
                case Expr.Perform perform:
                    Visit(perform.Operation);
                    break;
                case Expr.Handle handleExpr:
                    Visit(handleExpr.Body);
                    foreach (var arm in handleExpr.Arms)
                        Visit(arm.Body);
                    break;
                default:
                    throw new NotSupportedException(expr.GetType().Name);
            }
        }
    }

    private static (IReadOnlyList<string> Imports, string SourceWithoutImports, IReadOnlyDictionary<string, string> Aliases, IReadOnlyList<ImportSelector> Selectors) ParseImports(string filePath)
    {
        var parsed = ParseImportHeader(File.ReadAllText(filePath), filePath);
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
            i = j;
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
        var pattern = new Regex($@"(?<![A-Za-z0-9_.])(?<head>{alternation})\.(?=[A-Za-z_])", RegexOptions.CultureInvariant);
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
}
