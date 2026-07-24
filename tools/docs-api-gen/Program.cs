using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DocsApiGen;

/// <summary>
/// Generates the "Compiler Internals API Reference" pages consumed by the VitePress docs site,
/// by parsing the pipeline projects' C# source directly with Roslyn (no prior compiler build
/// required) and rendering their documented public surface to Markdown.
/// </summary>
internal static class Program
{
    private static readonly ProjectSpec[] Projects =
    [
        new ProjectSpec("Ashes.Frontend", "frontend", "Frontend", ["Parser", "Lexer"]),
        new ProjectSpec("Ashes.Semantics", "semantics", "Semantics", ["Lowering", "ProjectSupport", "BuiltinRegistry"]),
        new ProjectSpec("Ashes.Backend", "backend", "Backend", ["IBackend", "BackendFactory"]),
        new ProjectSpec("Ashes.Formatter", "formatter", "Formatter", ["Formatter", "FormattingOptions"]),
    ];

    private static int Main(string[] args)
    {
        string repoRoot = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
        string srcDir = Path.Combine(repoRoot, "src");
        string outDir = Path.Combine(repoRoot, "docs", "md", "internals", "api");
        Directory.CreateDirectory(outDir);

        Dictionary<string, string> fileToProjectDir = new(StringComparer.Ordinal);
        List<SyntaxTree> syntaxTrees = [];
        CollectSyntaxTrees(srcDir, fileToProjectDir, syntaxTrees);

        if (syntaxTrees.Count == 0)
        {
            Console.Error.WriteLine($"error: no source files found for any target project under {srcDir}");
            return 1;
        }

        CSharpCompilation compilation = CSharpCompilation.Create(
            "DocsApiGenTemp",
            syntaxTrees,
            GetTrustedPlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        Dictionary<string, List<INamedTypeSymbol>> typesByProjectDir = CollectPublicTypesByProject(compilation, fileToProjectDir);
        WritePages(typesByProjectDir, outDir, repoRoot);
        return 0;
    }

    private static void WritePages(Dictionary<string, List<INamedTypeSymbol>> typesByProjectDir, string outDir, string repoRoot)
    {
        foreach (ProjectSpec project in Projects)
        {
            if (!typesByProjectDir.TryGetValue(project.DirectoryName, out List<INamedTypeSymbol>? types) || types.Count == 0)
            {
                Console.Error.WriteLine($"warning: no public types found for {project.DirectoryName}");
                continue;
            }

            string page = MarkdownPage.Render(project, types, repoRoot);
            string outPath = Path.Combine(outDir, project.Slug + ".md");
            File.WriteAllText(outPath, page);
            Console.WriteLine($"wrote {outPath} ({types.Count} types)");
        }
    }

    private static void CollectSyntaxTrees(string srcDir, Dictionary<string, string> fileToProjectDir, List<SyntaxTree> syntaxTrees)
    {
        CSharpParseOptions parseOptions = new(LanguageVersion.Latest);
        foreach (ProjectSpec project in Projects)
        {
            string projectDir = Path.Combine(srcDir, project.DirectoryName);
            if (!Directory.Exists(projectDir))
            {
                Console.Error.WriteLine($"warning: project directory not found: {projectDir}");
                continue;
            }

            foreach (string file in EnumerateProjectSourceFiles(projectDir))
            {
                string text = File.ReadAllText(file);
                syntaxTrees.Add(CSharpSyntaxTree.ParseText(text, parseOptions, path: file));
                fileToProjectDir[file] = project.DirectoryName;
            }
        }
    }

    private static IEnumerable<string> EnumerateProjectSourceFiles(string projectDir)
    {
        string objSegment = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";
        string binSegment = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";
        foreach (string file in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
        {
            if (!file.Contains(objSegment, StringComparison.Ordinal) && !file.Contains(binSegment, StringComparison.Ordinal))
            {
                yield return file;
            }
        }
    }

    private static Dictionary<string, List<INamedTypeSymbol>> CollectPublicTypesByProject(
        CSharpCompilation compilation,
        Dictionary<string, string> fileToProjectDir)
    {
        Dictionary<string, List<INamedTypeSymbol>> result = new(StringComparer.Ordinal);
        foreach (INamedTypeSymbol type in EnumerateNamedTypes(compilation.GlobalNamespace))
        {
            if (!IsEffectivelyPublic(type))
            {
                continue;
            }

            string? projectDir = ResolveProjectDir(type, fileToProjectDir);
            if (projectDir is null)
            {
                continue;
            }

            if (!result.TryGetValue(projectDir, out List<INamedTypeSymbol>? list))
            {
                list = [];
                result[projectDir] = list;
            }

            list.Add(type);
        }

        return result;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateNamedTypes(INamespaceSymbol ns)
    {
        foreach (INamespaceOrTypeSymbol member in ns.GetMembers())
        {
            IEnumerable<INamedTypeSymbol> types = member switch
            {
                INamespaceSymbol nestedNamespace => EnumerateNamedTypes(nestedNamespace),
                INamedTypeSymbol type => EnumerateNamedTypesInType(type),
                _ => [],
            };
            foreach (INamedTypeSymbol type in types)
            {
                yield return type;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateNamedTypesInType(INamedTypeSymbol type)
    {
        yield return type;
        foreach (INamedTypeSymbol nested in type.GetTypeMembers())
        {
            foreach (INamedTypeSymbol descendant in EnumerateNamedTypesInType(nested))
            {
                yield return descendant;
            }
        }
    }

    private static bool IsEffectivelyPublic(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility != Accessibility.Public)
            {
                return false;
            }
        }

        return true;
    }

    private static string? ResolveProjectDir(INamedTypeSymbol type, Dictionary<string, string> fileToProjectDir)
    {
        foreach (SyntaxReference syntaxReference in type.DeclaringSyntaxReferences)
        {
            if (fileToProjectDir.TryGetValue(syntaxReference.SyntaxTree.FilePath, out string? projectDir))
            {
                return projectDir;
            }
        }

        return null;
    }

    private static List<MetadataReference> GetTrustedPlatformReferences()
    {
        string? trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrEmpty(trustedAssemblies))
        {
            throw new InvalidOperationException("TRUSTED_PLATFORM_ASSEMBLIES was not provided by the runtime.");
        }

        List<MetadataReference> references = [];
        foreach (string path in trustedAssemblies.Split(Path.PathSeparator))
        {
            if (path.Length > 0)
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
        }

        return references;
    }
}
