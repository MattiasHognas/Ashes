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
    IReadOnlyDictionary<string, string> Aliases
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
    IReadOnlyDictionary<string, string> ImportAliases
);

public readonly record struct CombinedCompilationLayout(
    string Source,
    int EntryOffset,
    int BodyStart,
    IReadOnlyList<(string FilePath, int StartOffset, int EndOffset)> ModuleOffsets
);

public static class ProjectSupport
{
    private sealed record StandardLibraryModuleDescriptor(string ModuleName, string? ResourceName);

    private sealed record ModuleBindingFragment(string Name, string ValueSource, bool IsRecursive);

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
        bool IsFlat);

    public const string ImportModulePattern = @"^\s*import\s+([A-Z][A-Za-z0-9_]*(?:\.[A-Z][A-Za-z0-9_]*)*)(?:\s+as\s+([A-Za-z][A-Za-z0-9_]*))?\s*$";

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
        "let", "rec", "in", "if", "then", "else", "match", "with",
        "fun", "true", "false", "type", "await"
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
                var moduleName = match.Groups[1].Value;
                imports.Add(moduleName);
                if (match.Groups[2].Success)
                {
                    var alias = match.Groups[2].Value;
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

                    aliases[alias] = moduleName;
                }
                continue;
            }

            if (inHeader && trimmed.StartsWith("import ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Invalid import syntax in {displayPath}:{lineIndex}. Expected 'import Foo' or 'import Foo.Bar' or 'import Foo.Bar as Alias'.");
            }

            if (inHeader && (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal)))
            {
                continue;
            }

            inHeader = false;
            sourceLines.Add(line);
        }

        return new ParsedImportHeader(imports, string.Join('\n', sourceLines), aliases);
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

            throw new InvalidOperationException(BuildMissingModuleMessage(moduleName, searchRoots, moduleRelativePath));
        }

        ProjectModule LoadModule(string moduleName, string filePath)
        {
            var fullPath = Path.GetFullPath(filePath);
            if (resolvedByPath.TryGetValue(fullPath, out var existing))
            {
                return existing;
            }

            var (imports, source, aliases) = ParseImports(fullPath);
            var module = new ProjectModule(moduleName, fullPath, source, imports, aliases);
            resolvedByPath[fullPath] = module;
            if (!resolvedByModuleName.ContainsKey(moduleName))
            {
                resolvedByModuleName[moduleName] = module;
            }

            return module;
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
        string entryFilePath = "<memory>")
    {
        var orderedModules = new List<ProjectModule>();
        var seenModules = new HashSet<string>(StringComparer.Ordinal);
        var states = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var traversal = new Stack<ProjectModule>();

        var entryModule = new ProjectModule("Main", entryFilePath, sourceWithoutImports, importNames.ToList(), new Dictionary<string, string>());
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

            if (importName.StartsWith("Ashes.", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Unknown standard library module '{importName}'. Known modules: {string.Join(", ", KnownStdModules)}.");
            }

            throw new InvalidOperationException(
                $"Could not resolve module '{importName}'. User-defined module imports require project mode via ashes.json.");
        }

        return BuildCompilationLayoutCore(orderedModules, entryModule, sourceWithoutImports);

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
            Expr.LetRec letRecExpr when letRecExpr.Body is Expr.Var bodyVar && string.Equals(letRecExpr.Name, bodyVar.Name, StringComparison.Ordinal) => letRecExpr.Name,
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
        var flatDeclarationEmitted = false;
        foreach (var module in nonEntryModules.Where(module => shapes[module.ModuleName].IsFlat))
        {
            var start = prefix.Length;
            prefix.Append(BuildModuleBindingPrefix(module, shapes[module.ModuleName], exportedNames, usedBindingNames, flat: true));
            if (prefix.Length > start)
            {
                moduleOffsets.Add((module.FilePath, start, prefix.Length));
                flatDeclarationEmitted = true;
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

        // A flat top-level declaration ends with a newline, not an `in`. Without a following legacy
        // nested-let binding to open the trailing body, the entry expression would be absorbed as a
        // whitespace/parenthesized-application argument of the last flat value, so introduce a boundary
        // binding whose `in` makes the entry expression a proper let body.
        if (flatDeclarationEmitted && !legacyBindingEmitted)
        {
            prefix.Append("let __ashes_module_boundary = 0 in ");
        }

        var entryExpression = BuildEntryExpression(entryModule, entryShape, exportedNames);
        var hasPrefixBeforeBody = prefix.Length > entryShape.TypeDeclarationsSource.Length;
        if (entryShape.TypeDeclarationsSource.Length == 0 && hasPrefixBeforeBody)
        {
            prefix.Append('(');
            var entryOffset = prefix.Length;
            prefix.Append(entryExpression);
            prefix.Append(')');
            moduleOffsets.Add((entryModule.FilePath, entryOffset, entryOffset + entryExpression.Length));
            return new CombinedCompilationLayout(prefix.ToString(), entryOffset, 0, moduleOffsets);
        }

        if (entryShape.TypeDeclarationsSource.Length == 0 && prefix.Length == 0)
        {
            moduleOffsets.Add((entryModule.FilePath, 0, entryExpression.Length));
            return new CombinedCompilationLayout(entryExpression, 0, 0, moduleOffsets);
        }

        var offset = prefix.Length;
        prefix.Append(entryExpression);
        moduleOffsets.Add((entryModule.FilePath, offset, prefix.Length));
        return new CombinedCompilationLayout(prefix.ToString(), offset, entryShape.TypeDeclarationsSource.Length, moduleOffsets);
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

        return ApplyAliases(entryShape.RawExpressionSource, aliases);
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
                prefix.Append("rec ");
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

                prefix.Append($"{moduleBindingName}_{binding.Name}")
                    .Append(" = (")
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

        while (true)
        {
            var token = lexer.Next();
            if (token.Kind == TokenKind.EOF)
            {
                break;
            }

            if (token.Kind == TokenKind.Ident && renames.TryGetValue(token.Text, out var replacement))
            {
                builder.Append(trimmed[copiedUpTo..token.Position]).Append(replacement);
                copiedUpTo = token.Position + token.Length;
            }
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
    /// <c>let</c> / <c>let rec ... and ...</c> / <c>type</c> / <c>extern</c> declarations followed by
    /// an optional trailing expression). The export set is exactly the top-level <c>let</c>/recgroup
    /// names; <c>extern</c> declarations and the trailing expression are dropped. Returns
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
        // emits up front, and `extern`, which is never part of the body). Removing exactly these from
        // the source leaves the flat `let` declarations and the trailing expression for the entry path.
        var hoistedSpans = new List<(int Start, int End)>();
        var hasFlatBinding = false;
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

                case TopLevelItem.Extern externItem:
                    {
                        // `extern` is never exported and carries no value the stitcher needs; skip it.
                        var span = AstSpans.GetOrDefault(externItem.Decl);
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
                        if (!TryExtractFlatBindingValue(source, letDecl.Value, ref cursor, out var valueSource))
                        {
                            return false;
                        }

                        groups.Add(new ModuleBindingGroup(
                            [new ModuleBindingFragment(letDecl.Name, valueSource, letDecl.IsRecursive)],
                            letDecl.IsRecursive));
                        hasFlatBinding = true;
                        break;
                    }

                case TopLevelItem.RecGroup recGroup:
                    {
                        var members = new List<ModuleBindingFragment>();
                        foreach (var (name, value) in recGroup.Bindings)
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

        // Without a genuine top-level `let`/`let rec` declaration there is nothing flat to export: a
        // module that is only type declarations followed by a nested `let ... in` pyramid (its bindings
        // live in Program.Body, not Program.Items) must keep the legacy text-based shaping, which
        // extracts those bindings and preserves the entry expression. Bailing here is what keeps the
        // legacy `type T = ...` + `let f = ... in f` module form working.
        if (!hasFlatBinding)
        {
            return false;
        }

        // For an imported (non-entry) module the trailing expression is dropped and the stitcher uses
        // the binding groups, so RawExpressionSource is unused. For an entry module it is the program
        // body, so preserve the flat `let` declarations and trailing expression here (with the hoisted
        // type/extern declarations removed, since those are emitted up front) rather than discarding it.
        shape = new ModuleSourceShape(
            TypeDeclarationsSource: typeDeclarations.ToString(),
            RawExpressionSource: RemoveSpans(source, hoistedSpans).Trim(),
            ExpressionBodySource: string.Empty,
            TopLevelBindings: groups,
            LegacyExportName: null,
            IsFlat: true);
        return true;
    }

    /// <summary>
    /// Returns <paramref name="source"/> with the given (already source-ordered, non-overlapping)
    /// spans removed, preserving everything in between. Used to strip hoisted type/extern declarations
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
        valueSource = string.Empty;
        if (!TryScanFlatLetHeader(source, cursor, out var parameters, out var valueStart))
        {
            return false;
        }

        var valueEnd = AstSpans.GetOrDefault(value).End;
        if (valueEnd <= valueStart || valueEnd > source.Length)
        {
            return false;
        }

        var body = source[valueStart..valueEnd].Trim();
        for (var i = parameters.Count - 1; i >= 0; i--)
        {
            body = $"fun ({parameters[i]}) -> {body}";
        }

        valueSource = body;
        cursor = valueEnd;
        return true;
    }

    /// <summary>
    /// Scans a flat <c>let [rec] name [params] [: type] =</c> (or <c>and name [params] =</c>) header
    /// starting at <paramref name="from"/>, returning any ML-style sugar parameters and the source
    /// position immediately after the value-introducing <c>=</c>.
    /// </summary>
    private static bool TryScanFlatLetHeader(string source, int from, out IReadOnlyList<string> parameters, out int valueStart)
    {
        parameters = [];
        valueStart = from;
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

        if (token.Kind == TokenKind.Rec)
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
            // Annotated binding: skip the type expression up to the value-introducing `=`.
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
        if (next.Kind == TokenKind.Rec)
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
        while (equals.Kind != TokenKind.Equals && equals.Kind != TokenKind.EOF)
        {
            equals = lexer.Next();
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
        module = new ProjectModule(moduleName, $"<std:{moduleName}>", parsed.SourceWithoutImports, parsed.ImportNames, parsed.ImportAliases);
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
        Visit(program.Body);
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
                case Expr.LetRec letRecExpr:
                    Visit(letRecExpr.Value);
                    Visit(letRecExpr.Body);
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
                case Expr.Async asyncExpr:
                    Visit(asyncExpr.Body);
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
                default:
                    throw new NotSupportedException(expr.GetType().Name);
            }
        }
    }

    private static (IReadOnlyList<string> Imports, string SourceWithoutImports, IReadOnlyDictionary<string, string> Aliases) ParseImports(string filePath)
    {
        var parsed = ParseImportHeader(File.ReadAllText(filePath), filePath);
        return (parsed.ImportNames, parsed.SourceWithoutImports, parsed.ImportAliases);
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
