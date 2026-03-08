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
    IReadOnlyList<string> Imports
);

public sealed record ProjectCompilationPlan(
    AshesProject Project,
    IReadOnlyList<ProjectModule> OrderedModules,
    ProjectModule EntryModule,
    IReadOnlySet<string> ImportedStdModules
);

public readonly record struct ParsedImportHeader(
    IReadOnlyList<string> ImportNames,
    string SourceWithoutImports
);

public readonly record struct CombinedCompilationLayout(
    string Source,
    int EntryOffset,
    int BodyStart
);

public static class ProjectSupport
{
    private sealed record StandardLibraryModuleDescriptor(string ModuleName, string? ResourceName);

    private sealed record ModuleBindingFragment(string Name, string ValueSource, bool IsRecursive);

    private sealed record ModuleSourceShape(
        string TypeDeclarationsSource,
        string RawExpressionSource,
        string ExpressionBodySource,
        IReadOnlyList<ModuleBindingFragment> TopLevelBindings,
        string? LegacyExportName);

    public const string ImportModulePattern = @"^\s*import\s+([A-Z][A-Za-z0-9_]*(?:\.[A-Z][A-Za-z0-9_]*)*)\s*$";

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
                imports.Add(match.Groups[1].Value);
                continue;
            }

            if (inHeader && trimmed.StartsWith("import ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Invalid import syntax in {displayPath}:{lineIndex}. Expected 'import Foo' or 'import Foo.Bar'.");
            }

            if (inHeader && (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal)))
            {
                continue;
            }

            inHeader = false;
            sourceLines.Add(line);
        }

        return new ParsedImportHeader(imports, string.Join('\n', sourceLines));
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

        return new ProjectCompilationPlan(project, ordered, entryModule, importedStdModules);

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

            var (imports, source) = ParseImports(fullPath);
            var module = new ProjectModule(moduleName, fullPath, source, imports);
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

    public static CombinedCompilationLayout BuildStandaloneCompilationLayout(string sourceWithoutImports, IReadOnlyList<string> importNames)
    {
        var orderedModules = new List<ProjectModule>();
        var seenModules = new HashSet<string>(StringComparer.Ordinal);
        var states = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var traversal = new Stack<ProjectModule>();

        var entryModule = new ProjectModule("Main", "<memory>", sourceWithoutImports, importNames.ToList());
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
        prefix.Append(entryShape.TypeDeclarationsSource);
        foreach (var module in nonEntryModules)
        {
            prefix.Append(shapes[module.ModuleName].TypeDeclarationsSource);
        }

        var usedBindingNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var module in nonEntryModules)
        {
            prefix.Append(BuildModuleBindingPrefix(module, shapes[module.ModuleName], exportedNames, usedBindingNames));
        }

        var entryExpression = BuildEntryExpression(entryModule, entryShape, exportedNames);
        var hasPrefixBeforeBody = prefix.Length > entryShape.TypeDeclarationsSource.Length;
        if (entryShape.TypeDeclarationsSource.Length == 0 && hasPrefixBeforeBody)
        {
            prefix.Append('(');
            var entryOffset = prefix.Length;
            prefix.Append(entryExpression);
            prefix.Append(')');
            return new CombinedCompilationLayout(prefix.ToString(), entryOffset, 0);
        }

        if (entryShape.TypeDeclarationsSource.Length == 0 && prefix.Length == 0)
        {
            return new CombinedCompilationLayout(entryExpression, 0, 0);
        }

        var offset = prefix.Length;
        prefix.Append(entryExpression);
        return new CombinedCompilationLayout(prefix.ToString(), offset, entryShape.TypeDeclarationsSource.Length);
    }

    private static string BuildEntryExpression(
        ProjectModule entryModule,
        ModuleSourceShape entryShape,
        IReadOnlyDictionary<string, IReadOnlyList<string>> exportedNames)
    {
        var referencedVariables = CollectReferencedVariables(entryShape.RawExpressionSource);
        var aliases = BuildVisibleAliases(
            entryModule,
            [],
            null,
            referencedVariables,
            exportedNames);

        return ApplyAliases(entryShape.RawExpressionSource, aliases);
    }

    private static string BuildModuleBindingPrefix(
        ProjectModule module,
        ModuleSourceShape shape,
        IReadOnlyDictionary<string, IReadOnlyList<string>> exportedNames,
        IDictionary<string, string> usedBindingNames)
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

        foreach (var binding in shape.TopLevelBindings)
        {
            var generatedBindingName = $"{moduleBindingName}_{binding.Name}";
            if (usedBindingNames.TryGetValue(generatedBindingName, out existingModuleName))
            {
                throw new InvalidOperationException(
                    $"Generated export binding collision for '{generatedBindingName}': '{existingModuleName}' and '{module.ModuleName}'.");
            }

            usedBindingNames[generatedBindingName] = module.ModuleName;

            var referencedVariables = CollectReferencedVariables(binding.ValueSource);
            var aliases = BuildVisibleAliases(
                module,
                availableLocalBindings,
                binding.IsRecursive ? binding.Name : null,
                referencedVariables,
                exportedNames);

            prefix.Append("let ");
            if (binding.IsRecursive)
            {
                prefix.Append("rec ");
            }

            prefix.Append(generatedBindingName)
                .Append(" = (")
                .Append(ApplyAliases(binding.ValueSource, aliases))
                .Append(") in ");

            availableLocalBindings.Add(binding.Name);
        }

        var bodyReferencedVariables = CollectReferencedVariables(shape.ExpressionBodySource);
        var bodyAliases = BuildVisibleAliases(
            module,
            availableLocalBindings,
            null,
            bodyReferencedVariables,
            exportedNames);

        prefix.Append("let ")
            .Append(moduleBindingName)
            .Append(" = (")
            .Append(ApplyAliases(shape.ExpressionBodySource, bodyAliases))
            .Append(") in ");

        return prefix.ToString();
    }

    private static List<KeyValuePair<string, string>> BuildVisibleAliases(
        ProjectModule module,
        IReadOnlyList<string> availableLocalBindings,
        string? currentRecursiveBinding,
        IReadOnlySet<string> referencedVariables,
        IReadOnlyDictionary<string, IReadOnlyList<string>> exportedNames)
    {
        var aliases = new List<KeyValuePair<string, string>>();
        var localNames = new HashSet<string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(currentRecursiveBinding) && referencedVariables.Contains(currentRecursiveBinding))
        {
            aliases.Add(new KeyValuePair<string, string>(
                currentRecursiveBinding!,
                $"{SanitizeModuleBindingName(module.ModuleName)}_{currentRecursiveBinding}"));
            localNames.Add(currentRecursiveBinding!);
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
        foreach (var import in module.Imports.Reverse())
        {
            if (!exportedNames.TryGetValue(import, out var exportNames) || exportNames.Count == 0)
            {
                continue;
            }

            foreach (var exportName in exportNames)
            {
                if (!referencedVariables.Contains(exportName) || localNames.Contains(exportName))
                {
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
            }
        }

        return aliases;
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

    private static IReadOnlyList<string> GetExportNames(ModuleSourceShape shape)
    {
        if (shape.TopLevelBindings.Count > 0)
        {
            return shape.TopLevelBindings.Select(binding => binding.Name).ToArray();
        }

        return string.IsNullOrWhiteSpace(shape.LegacyExportName)
            ? []
            : [shape.LegacyExportName!];
    }

    private static ModuleSourceShape ShapeModuleSource(string source)
    {
        var bodyStart = FindExpressionBodyStart(source);
        var typeDeclarationsSource = bodyStart > 0 ? source[..bodyStart] : string.Empty;
        var rawExpressionSource = bodyStart < source.Length ? source[bodyStart..].Trim() : string.Empty;
        var topLevelBindings = ExtractTopLevelBindings(rawExpressionSource, out var remainingBody);
        var legacyExportName = topLevelBindings.Count == 0 ? TryInferExportName(source) : null;

        return new ModuleSourceShape(typeDeclarationsSource, rawExpressionSource, remainingBody, topLevelBindings, legacyExportName);
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
        module = new ProjectModule(moduleName, $"<std:{moduleName}>", parsed.SourceWithoutImports, parsed.ImportNames);
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

    private static HashSet<string> CollectReferencedVariables(string source)
    {
        var diag = new Diagnostics();
        var program = new Parser(source, diag).ParseProgram();
        if (diag.StructuredErrors.Count > 0)
        {
            return [];
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        Visit(program.Body);
        return names;

        void Visit(Expr expr)
        {
            switch (expr)
            {
                case Expr.Var varExpr:
                    names.Add(varExpr.Name);
                    break;
                case Expr.QualifiedVar:
                case Expr.IntLit:
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
                case Expr.GreaterOrEqual ge:
                    Visit(ge.Left);
                    Visit(ge.Right);
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
                        Visit(matchCase.Body);
                    }
                    break;
                default:
                    throw new NotSupportedException(expr.GetType().Name);
            }
        }
    }

    private static (IReadOnlyList<string> Imports, string SourceWithoutImports) ParseImports(string filePath)
    {
        var parsed = ParseImportHeader(File.ReadAllText(filePath), filePath);
        return (parsed.ImportNames, parsed.SourceWithoutImports);
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
