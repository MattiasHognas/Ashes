using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ashes.Frontend;
using Ashes.Semantics;

namespace Ashes.Lsp;

/// <summary>
/// The compiler-backed core of the language server: it runs the frontend and semantic phases over an
/// editor's source and projects the results into the plain, protocol-agnostic items the LSP layer
/// serves (diagnostics, hover, go-to-definition, semantic tokens, formatting). It never performs
/// codegen — the Lsp project is a consumer of compiler logic, not an implementer.
/// </summary>
public static partial class DocumentService
{
    /// <summary>A diagnostic to surface in the editor, spanning <paramref name="Start"/> to
    /// <paramref name="End"/> in the document.</summary>
    /// <param name="Start">Inclusive start offset of the diagnostic span.</param>
    /// <param name="End">Exclusive end offset of the diagnostic span.</param>
    /// <param name="Message">Human-readable diagnostic text.</param>
    /// <param name="Code">Optional diagnostic code (e.g. <c>ASH016</c>), or null when uncoded.</param>
    public readonly record struct DiagnosticItem(int Start, int End, string Message, string? Code = null)
    {
        /// <summary>The document offset where the diagnostic begins, an alias for <see cref="Start"/>.</summary>
        public int Position => Start;

        /// <summary>The diagnostic's span as a <see cref="TextSpan"/> from <see cref="Start"/> to <see cref="End"/>.</summary>
        public TextSpan Span => TextSpan.FromBounds(Start, End);
    }

    /// <summary>Hover content to show for the range <paramref name="Start"/> to <paramref name="End"/>.</summary>
    /// <param name="Start">Inclusive start offset of the hovered range.</param>
    /// <param name="End">Exclusive end offset of the hovered range.</param>
    /// <param name="Contents">The hover text (typically the inferred type or signature).</param>
    public readonly record struct HoverItem(int Start, int End, string Contents)
    {
        /// <summary>The hovered range as a <see cref="TextSpan"/> from <see cref="Start"/> to <see cref="End"/>.</summary>
        public TextSpan Span => TextSpan.FromBounds(Start, End);
    }

    /// <summary>The resolved location a go-to-definition request points at.</summary>
    /// <param name="FilePath">The file containing the definition, or null when it is the current document.</param>
    /// <param name="Start">Inclusive start offset of the definition span.</param>
    /// <param name="End">Exclusive end offset of the definition span.</param>
    public readonly record struct DefinitionItem(string? FilePath, int Start, int End)
    {
        /// <summary>The definition's span as a <see cref="TextSpan"/> from <see cref="Start"/> to <see cref="End"/>.</summary>
        public TextSpan Span => TextSpan.FromBounds(Start, End);
    }

    /// <summary>One source reference to a resolved declaration.</summary>
    public readonly record struct ReferenceItem(string? FilePath, int Start, int End)
    {
        /// <summary>The referenced identifier's source span.</summary>
        public TextSpan Span => TextSpan.FromBounds(Start, End);
    }

    /// <summary>One semantic-highlighting token, positioned by zero-based <paramref name="Line"/> and
    /// <paramref name="Character"/> and classified by <paramref name="TokenType"/>.</summary>
    /// <param name="Line">Zero-based line of the token.</param>
    /// <param name="Character">Zero-based start column of the token.</param>
    /// <param name="Length">Length of the token in characters.</param>
    /// <param name="TokenType">Token-type index into <see cref="SemanticTokenTypes"/>.</param>
    /// <param name="TokenModifiers">Bitset of token modifiers applied to the token.</param>
    public readonly record struct SemanticTokenItem(int Line, int Character, int Length, int TokenType, int TokenModifiers);

    internal readonly record struct ImportItem(TextSpan Span, string ModuleName, string? Selector, string? Alias)
    {
        /// <summary>
        /// The unqualified name this import binds: the alias when present, otherwise the selected
        /// binding/type. Only meaningful for selector imports (<see cref="Selector"/> is non-null).
        /// </summary>
        public string LocalName => Alias ?? Selector ?? ModuleName;
    }

    private readonly record struct HeaderLineItem(string Text, string? ModuleName, string? Selector, string? Alias);

    private readonly record struct ImportHeaderInfo(
        string StrippedSource,
        int HeaderOffset,
        IReadOnlyList<HeaderLineItem> HeaderLines,
        IReadOnlyList<ImportItem> Imports,
        IReadOnlyList<DiagnosticItem> Diagnostics);

    private readonly record struct AnalysisContext(
        string StrippedSource,
        string AnalysisSource,
        int HeaderOffset,
        int EntryOffset,
        int BodyStart,
        IReadOnlySet<string>? ImportedStdModules,
        IReadOnlyDictionary<string, string>? ModuleAliases,
        IReadOnlyList<DiagnosticItem> Diagnostics,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? ConstructorModules = null);

    private readonly record struct ProjectAnalysisContext(
        string CombinedSource,
        int EntryOffset,
        int BodyStart,
        IReadOnlySet<string> ImportedStdModules,
        IReadOnlyDictionary<string, string>? ModuleAliases,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? ConstructorModules = null);

    private readonly record struct DefinitionLocation(string? FilePath, TextSpan Span);

    // Diagnostic code for conflicting unqualified import selectors (mirrors the compiler's ASH016).
    private const string ConflictingImportSelectorsCode = "ASH016";

    // Token type indices matching SemanticTokenTypes legend order
    /// <summary>Semantic token type index for a type name; indexes <see cref="SemanticTokenTypes"/>.</summary>
    public const int TokenTypeType = 0;
    /// <summary>Semantic token type index for a type parameter; indexes <see cref="SemanticTokenTypes"/>.</summary>
    public const int TokenTypeTypeParameter = 1;
    /// <summary>Semantic token type index for an enum/constructor member; indexes <see cref="SemanticTokenTypes"/>.</summary>
    public const int TokenTypeEnumMember = 2;
    /// <summary>Semantic token type index for a trait name; indexes <see cref="SemanticTokenTypes"/>.</summary>
    public const int TokenTypeTrait = 3;
    /// <summary>Semantic token type index for a trait method; indexes <see cref="SemanticTokenTypes"/>.</summary>
    public const int TokenTypeTraitMethod = 4;

    /// <summary>The semantic-token type legend, in the index order the <c>TokenType*</c> constants
    /// reference and the client is registered with.</summary>
    public static IReadOnlyList<string> SemanticTokenTypes { get; } = ["type", "typeParameter", "enumMember", "interface", "method"];

    [GeneratedRegex(@"'([^']+)'", RegexOptions.Compiled)]
    private static partial Regex QuotedValueRegex();

    private static readonly Regex ImportLineRegex = new(
        ProjectSupport.ImportModulePattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Strips the import header (import lines and leading blank/comment lines) from the source.
    /// Returns the stripped source, the character offset where it starts in the original, and the import module names.
    /// </summary>
    private static ImportHeaderInfo StripImportHeader(string source)
    {
        SourceTextIndex sourceIndex = new(source);
        var imports = new List<ImportItem>();
        var headerLines = new List<HeaderLineItem>();
        var diagnostics = new List<DiagnosticItem>();
        int pos = 0;

        while (pos < source.Length)
        {
            int lineStart = pos;
            var (lineContent, nextPos) = ReadHeaderLine(source, pos);

            var trimmed = lineContent.TrimStart();

            var match = ImportLineRegex.Match(lineContent);
            if (match.Success)
            {
                // Group 1 is the module path, group 2 an optional lowercase binding selector, group 3
                // an optional alias (matching ProjectSupport.ImportModulePattern).
                var selector = match.Groups[2].Success ? match.Groups[2].Value : null;
                var alias = match.Groups[3].Success ? match.Groups[3].Value : null;
                imports.Add(new ImportItem(ToByteSpan(sourceIndex, lineStart, lineStart + lineContent.Length), match.Groups[1].Value, selector, alias));
                headerLines.Add(new HeaderLineItem(lineContent, match.Groups[1].Value, selector, alias));
                pos = nextPos;
                continue;
            }

            if (trimmed.StartsWith("import ", StringComparison.Ordinal))
            {
                diagnostics.Add(new DiagnosticItem(
                    sourceIndex.ToUtf8Offset(lineStart),
                    sourceIndex.ToUtf8Offset(lineStart + lineContent.Length),
                    "Invalid import syntax. Expected 'import Foo' or 'import Foo.Bar' or 'import Foo.Bar as Alias'.",
                    DiagnosticCodes.ParseError));
                return new ImportHeaderInfo(source[nextPos..], sourceIndex.ToUtf8Offset(nextPos), headerLines, imports, diagnostics);
            }

            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                headerLines.Add(new HeaderLineItem(lineContent, null, null, null));
                pos = nextPos;
                continue;
            }

            break; // First non-header content line
        }

        if (pos > source.Length)
        {
            pos = source.Length;
        }

        return new ImportHeaderInfo(source[pos..], sourceIndex.ToUtf8Offset(pos), headerLines, imports, diagnostics);
    }

    private static TextSpan ToByteSpan(SourceTextIndex index, int start, int end) =>
        TextSpan.FromBounds(index.ToUtf8Offset(start), index.ToUtf8Offset(end));

    /// <summary>
    /// Reads the header line starting at <paramref name="pos"/>, returning its content and the
    /// position where the next line begins.
    /// </summary>
    private static (string LineContent, int NextPos) ReadHeaderLine(string source, int pos)
    {
        int nlIdx = source.IndexOf('\n', pos);
        int lineEnd = nlIdx < 0 ? source.Length : nlIdx;
        int nextPos = nlIdx < 0 ? source.Length : nlIdx + 1;

        // Line content without the newline (and without trailing \r)
        var lineContent = source[pos..lineEnd];
        if (lineContent.EndsWith('\r'))
        {
            lineContent = lineContent[..^1];
        }

        return (lineContent, nextPos);
    }

    private static string FormatImportLine(string moduleName, string? selector, string? alias)
    {
        var target = selector is null ? moduleName : $"{moduleName}.{selector}";
        return alias is null ? $"import {target}" : $"import {target} as {alias}";
    }

    /// <summary>
    /// Detects the <c>ASH016</c> condition for the standalone (non-project) path: two unqualified
    /// import selectors that bind the same local name to different exports. Mirrors the compiler's
    /// <c>ProjectSupport.ValidateSelectorConflicts</c> — importing the same export twice is allowed.
    /// </summary>
    private static DiagnosticItem? DetectSelectorConflict(IReadOnlyList<ImportItem> imports)
    {
        var byLocalName = new Dictionary<string, ImportItem>(StringComparer.Ordinal);
        foreach (var import in imports)
        {
            if (import.Selector is null)
            {
                continue;
            }

            var localName = import.LocalName;
            if (byLocalName.TryGetValue(localName, out var existing)
                && (!string.Equals(existing.ModuleName, import.ModuleName, StringComparison.Ordinal)
                    || !string.Equals(existing.Selector, import.Selector, StringComparison.Ordinal)))
            {
                return new DiagnosticItem(
                    import.Span.Start,
                    import.Span.End,
                    $"Conflicting unqualified import selectors for '{localName}'.",
                    ConflictingImportSelectorsCode);
            }

            byLocalName[localName] = import;
        }

        return null;
    }

    /// <summary>
    /// Finds the character position in <paramref name="source"/> where the expression body begins –
    /// i.e. after all leading <c>type</c> declarations. Returns 0 if there are no type declarations.
    /// </summary>
    private static int FindExpressionBodyStart(string source)
    {
        var diag = new Diagnostics();
        var lexer = new Lexer(source, diag);
        var tok = lexer.Next();

        while (tok.Kind == TokenKind.Type)
        {
            // Skip: type <Name> = | Ctor [(Params)] ...
            tok = lexer.Next(); // Name (Ident)
            tok = lexer.Next(); // either '(' or '='
            if (tok.Kind == TokenKind.LParen)
            {
                while (tok.Kind != TokenKind.RParen && tok.Kind != TokenKind.EOF)
                {
                    tok = lexer.Next();
                }

                tok = lexer.Next(); // =
            }

            tok = lexer.Next(); // first | or body-start token

            while (tok.Kind == TokenKind.Pipe)
            {
                tok = lexer.Next(); // CtorName
                tok = lexer.Next(); // either '(' or next '|' or body

                if (tok.Kind == TokenKind.LParen)
                {
                    // Skip parameter list: Ident, Comma, Ident, ... RParen
                    while (tok.Kind != TokenKind.RParen && tok.Kind != TokenKind.EOF)
                    {
                        tok = lexer.Next();
                    }
                    tok = lexer.Next(); // token after ')'
                }
                // tok is now '|' or the first token of the body
            }
            // tok is the first token after this type declaration (either another 'type' or the body)
        }

        return tok.Kind == TokenKind.EOF ? source.Length : tok.Position;
    }

    /// <summary>
    /// Tries to build a combined project source for the given file, substituting
    /// <paramref name="strippedSource"/> (import-stripped in-memory content) as the entry module.
    /// Returns (CombinedSource, EntryOffset, BodyStart) or null if no project is found.<br/>
    /// <list type="bullet">
    ///   <item><b>EntryOffset</b>: char index where the entry expression body begins in CombinedSource.</item>
    ///   <item><b>BodyStart</b>: char index where the expression body begins in strippedSource
    ///     (non-zero when the entry has leading type declarations).</item>
    /// </list>
    /// </summary>
    private static ProjectAnalysisContext? TryBuildCombinedProjectSource(
        string filePath, string strippedSource)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (dir is null)
        {
            return null;
        }

        var projectPath = ProjectSupport.DiscoverProjectFile(dir);
        if (projectPath is null)
        {
            return null;
        }

        var project = ProjectSupport.LoadProject(projectPath);
        var fileFullPath = Path.GetFullPath(filePath);

        // Treat the current file as the entry for analysis so all its imports are resolved.
        var pseudoProject = project with
        {
            EntryPath = fileFullPath,
            EntryModuleName = Path.GetFileNameWithoutExtension(fileFullPath)
        };

        var plan = ProjectSupport.BuildCompilationPlan(pseudoProject);
        var layout = ProjectSupport.BuildCompilationLayout(plan, strippedSource);

        return new ProjectAnalysisContext(
            layout.Source,
            new SourceTextIndex(layout.Source).ToUtf8Offset(layout.EntryOffset),
            new SourceTextIndex(layout.Source).ToUtf8Offset(layout.BodyStart),
            plan.ImportedStdModules,
            plan.MergedAliases.Count == 0 ? null : plan.MergedAliases,
            layout.ConstructorModules);
    }

    private static AnalysisContext PrepareAnalysisContext(string source, string? filePath)
    {
        var header = StripImportHeader(source);
        if (header.Diagnostics.Count > 0)
        {
            return new AnalysisContext(header.StrippedSource, header.StrippedSource, header.HeaderOffset, 0, 0, null, null, header.Diagnostics);
        }

        if (filePath is not null && TryPrepareProjectAnalysisContext(filePath, header) is { } projectContext)
        {
            return projectContext;
        }

        return PrepareStandaloneAnalysisContext(header);
    }

    private static AnalysisContext? TryPrepareProjectAnalysisContext(string filePath, ImportHeaderInfo header)
    {
        try
        {
            var combined = TryBuildCombinedProjectSource(filePath, header.StrippedSource);
            if (combined is not null)
            {
                return new AnalysisContext(
                    header.StrippedSource,
                    combined.Value.CombinedSource,
                    header.HeaderOffset,
                    combined.Value.EntryOffset,
                    combined.Value.BodyStart,
                    combined.Value.ImportedStdModules,
                    combined.Value.ModuleAliases,
                    [],
                    combined.Value.ConstructorModules);
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return new AnalysisContext(
                header.StrippedSource,
                header.StrippedSource,
                header.HeaderOffset,
                0,
                0,
                null,
                null,
                [CreateProjectDiagnostic(ex, header.Imports)]);
        }

        return null;
    }

    private static AnalysisContext PrepareStandaloneAnalysisContext(ImportHeaderInfo header)
    {
        var standaloneImportDiagnostics = ValidateStandaloneImports(header.Imports);
        if (standaloneImportDiagnostics.Count == 0 && DetectSelectorConflict(header.Imports) is { } selectorConflict)
        {
            return new AnalysisContext(
                header.StrippedSource,
                header.StrippedSource,
                header.HeaderOffset,
                0,
                0,
                null,
                null,
                [selectorConflict]);
        }

        var importedStdModules = standaloneImportDiagnostics.Count == 0
            ? BuildImportedStdModules(header.Imports)
            : null;
        var analysisSource = header.StrippedSource;
        var entryOffset = 0;
        var bodyStart = 0;
        IReadOnlyDictionary<string, IReadOnlySet<string>>? constructorModules = null;

        if (standaloneImportDiagnostics.Count == 0
            && (header.Imports.Count > 0
                || ProjectSupport.ContainsInlineModule(header.StrippedSource)
                || ContainsTraitSurface(header.StrippedSource)))
        {
            var layout = ProjectSupport.BuildStandaloneCompilationLayout(
                header.StrippedSource,
                header.Imports.Select(x => x.ModuleName).ToArray());
            analysisSource = layout.Source;
            SourceTextIndex analysisIndex = new(analysisSource);
            entryOffset = analysisIndex.ToUtf8Offset(layout.EntryOffset);
            bodyStart = analysisIndex.ToUtf8Offset(layout.BodyStart);
            constructorModules = layout.ConstructorModules;
            if (layout.ModuleProvenanceByPath is not null)
            {
                importedStdModules!.UnionWith(layout.ModuleProvenanceByPath.Values
                    .Select(provenance => provenance.ModuleName)
                    .Where(ProjectSupport.IsStdModule));
            }
        }

        return new AnalysisContext(
            header.StrippedSource,
            analysisSource,
            header.HeaderOffset,
            entryOffset,
            bodyStart,
            importedStdModules,
            BuildModuleAliases(header.Imports),
            standaloneImportDiagnostics,
            constructorModules);
    }

    private static bool ContainsTraitSurface(string source)
    {
        var diagnostics = new Diagnostics();
        var lexer = new Lexer(source, diagnostics);
        while (true)
        {
            Token token = lexer.Next();
            if (token.Kind is TokenKind.Trait or TokenKind.Implement or TokenKind.Requires or TokenKind.Deriving)
            {
                return true;
            }
            if (token.Kind == TokenKind.EOF)
            {
                return false;
            }
        }
    }

    private static IReadOnlyDictionary<string, string>? BuildModuleAliases(IReadOnlyList<ImportItem> imports)
    {
        Dictionary<string, string>? aliases = null;
        foreach (var import in imports)
        {
            // Only whole-module imports introduce a module alias; on a selector import the alias
            // renames the imported binding/type, not the module qualifier.
            if (import.Selector is null && import.Alias is not null)
            {
                aliases ??= new Dictionary<string, string>(StringComparer.Ordinal);
                aliases[import.Alias] = import.ModuleName;
            }
        }
        return aliases;
    }

    private static IReadOnlyList<DiagnosticItem> ValidateStandaloneImports(IReadOnlyList<ImportItem> imports)
    {
        var diagnostics = new List<DiagnosticItem>();

        foreach (var import in imports)
        {
            if (ProjectSupport.IsStdModule(import.ModuleName))
            {
                continue;
            }

            if (import.ModuleName.StartsWith("Ashes.", StringComparison.Ordinal))
            {
                diagnostics.Add(new DiagnosticItem(
                    import.Span.Start,
                    import.Span.End,
                    $"Unknown standard library module '{import.ModuleName}'. Known modules: {string.Join(", ", ProjectSupport.KnownStandardLibraryModules)}."));
                continue;
            }

            diagnostics.Add(new DiagnosticItem(
                import.Span.Start,
                import.Span.End,
                $"Could not resolve module '{import.ModuleName}'. User-defined module imports require project mode via ashes.json."));
        }

        return diagnostics;
    }

    private static HashSet<string> BuildImportedStdModules(IReadOnlyList<ImportItem> imports)
    {
        var importedStdModules = imports
            .Select(x => x.ModuleName)
            .Where(ProjectSupport.IsStdModule)
            .ToHashSet(StringComparer.Ordinal);
        return importedStdModules;
    }

    private static DiagnosticItem CreateProjectDiagnostic(Exception ex, IReadOnlyList<ImportItem> imports)
    {
        var match = QuotedValueRegex().Match(ex.Message);
        if (match.Success)
        {
            var import = imports.FirstOrDefault(x => string.Equals(x.ModuleName, match.Groups[1].Value, StringComparison.Ordinal));
            if (!string.IsNullOrEmpty(import.ModuleName))
            {
                return new DiagnosticItem(import.Span.Start, import.Span.End, ex.Message);
            }
        }

        return new DiagnosticItem(0, 0, ex.Message);
    }

    /// <summary>
    /// Parses and lowers <paramref name="source"/> and returns the resulting compiler diagnostics as
    /// <see cref="DiagnosticItem"/>s, with spans mapped back onto the original document (accounting for
    /// the stripped import header). <paramref name="filePath"/> supplies project context when present.
    /// </summary>
    public static IReadOnlyList<DiagnosticItem> Analyze(string source, string? filePath = null)
    {
        var context = PrepareAnalysisContext(source, filePath);
        if (context.Diagnostics.Count > 0)
        {
            return context.Diagnostics;
        }

        var diag = new Diagnostics();
        var program = new Parser(context.AnalysisSource, diag).ParseProgram();
        _ = new Lowering(diag, context.ImportedStdModules, context.ModuleAliases, context.ConstructorModules).Lower(program);

        return diag.StructuredErrors
            .Select(d => (Diagnostic: d, MappedSpan: MapToOriginalSpan(d.Start, d.End, context)))
            .Where(x => x.MappedSpan is not null)
            .Select(x => new DiagnosticItem(
                x.MappedSpan!.Value.Start + context.HeaderOffset,
                x.MappedSpan.Value.End + context.HeaderOffset,
                x.Diagnostic.Message,
                x.Diagnostic.Code))
            .ToArray();
    }

    /// <summary>
    /// Canonically formats <paramref name="source"/> and returns the result, or null when the source
    /// has a syntax error and cannot be formatted. The import header is preserved and normalized around
    /// the formatted body, and standalone comments are reinserted. <paramref name="options"/> overrides
    /// the whitespace conventions; otherwise they are resolved from <paramref name="filePath"/>'s
    /// <c>.editorconfig</c> chain when a path is given.
    /// </summary>
    public static string? Format(string source, string? filePath = null, global::Ashes.Formatter.FormattingOptions? options = null)
    {
        var header = StripImportHeader(source);
        if (header.Diagnostics.Count > 0)
        {
            return null;
        }

        var strippedSource = header.StrippedSource;
        var formattingOptions = options
            ?? (filePath is not null
                ? global::Ashes.Formatter.EditorConfigFormattingOptionsResolver.ResolveForPath(filePath)
                : new global::Ashes.Formatter.FormattingOptions { NewLine = "\n" });

        var diag = new Diagnostics();
        var program = new Parser(strippedSource, diag).ParseProgram();
        if (diag.Errors.Count > 0)
        {
            return null;
        }

        var formattedBody = global::Ashes.Formatter.Formatter.Format(
            program,
            preferPipelines: strippedSource.Contains("|>", StringComparison.Ordinal)
                || strippedSource.Contains("|?>", StringComparison.Ordinal)
                || strippedSource.Contains("|!>", StringComparison.Ordinal),
            options: formattingOptions);

        formattedBody = global::Ashes.Formatter.CommentReinserter.ReinsertStandaloneCommentLines(strippedSource, formattedBody, formattingOptions.NewLine);

        if (header.HeaderLines.Count == 0)
        {
            return formattedBody;
        }

        var headerLines = string.Join(
            formattingOptions.NewLine,
            header.HeaderLines.Select(line => line.ModuleName is null
                ? line.Text
                : FormatImportLine(line.ModuleName, line.Selector, line.Alias)));

        return headerLines + formattingOptions.NewLine + formattedBody;
    }

    /// <summary>
    /// Returns the semantic-highlighting tokens for <paramref name="source"/> (types, type parameters,
    /// and enum/constructor members), or an empty list when the source cannot be analyzed.
    /// <paramref name="filePath"/> supplies project context when present.
    /// </summary>
    public static IReadOnlyList<SemanticTokenItem> GetSemanticTokens(string source, string? filePath = null)
    {
        var context = PrepareAnalysisContext(source, filePath);
        if (context.Diagnostics.Count > 0)
        {
            return Array.Empty<SemanticTokenItem>();
        }

        var diag = new Diagnostics();
        var program = new Parser(context.AnalysisSource, diag).ParseProgram();
        var lowering = new Lowering(diag, context.ImportedStdModules, context.ModuleAliases, context.ConstructorModules);
        lowering.Lower(program);

        var typeNames = lowering.TypeSymbols.Keys.ToHashSet(StringComparer.Ordinal);
        typeNames.UnionWith(program.TypeAliasDecls.Select(declaration => declaration.Name));
        typeNames.UnionWith(program.ExternalDecls.OfType<ExternalDecl.OpaqueType>()
            .Select(declaration => declaration.Name));
        typeNames.Add("FfiBuffer");
        typeNames.Add("FfiStr");
        var ctorNames = lowering.ConstructorSymbols.Keys.ToHashSet(StringComparer.Ordinal);
        var traitNames = lowering.TraitSymbols.Values
            .SelectMany(trait => new[] { trait.Name, trait.QualifiedName })
            .ToHashSet(StringComparer.Ordinal);
        var traitMethodNames = lowering.TraitSymbols.Values
            .SelectMany(trait => trait.Methods.Keys)
            .ToHashSet(StringComparer.Ordinal);
        // Collect unique type-parameter names used in constructor parameter lists
        var typeParamNames = program.TypeDecls
            .SelectMany(d => d.TypeParameters.Select(tp => tp.Name)
                .Concat(d.Constructors.SelectMany(c => c.Parameters).SelectMany(fieldType => fieldType.MentionedNames())))
            .Concat(program.TypeAliasDecls.SelectMany(declaration =>
                declaration.TypeParameters.Select(parameter => parameter.Name)))
            .Concat(program.ZeroCostTypeDecls.SelectMany(declaration =>
                declaration.TypeParameters.Select(parameter => parameter.Name)
                    .Concat(declaration.Constructor.Parameters.SelectMany(type => type.MentionedNames()))))
            .Concat(program.Items.OfType<TopLevelItem.Trait>()
                .SelectMany(item => item.Decl.TypeParameters.Select(parameter => parameter.Name)))
            .ToHashSet(StringComparer.Ordinal);

        return ScanSemanticTokens(
            source,
            context.StrippedSource,
            context.HeaderOffset,
            typeNames,
            ctorNames,
            typeParamNames,
            traitNames,
            traitMethodNames);
    }

    private static List<SemanticTokenItem> ScanSemanticTokens(
        string source,
        string strippedSource,
        int headerOffset,
        HashSet<string> typeNames,
        HashSet<string> ctorNames,
        HashSet<string> typeParamNames,
        HashSet<string> traitNames,
        HashSet<string> traitMethodNames)
    {
        // Scan the stripped source (user's code without import header) for tokens.
        // Positions are adjusted by headerOffset to match original file positions.
        var originalLineStarts = LspTextUtils.GetLineStarts(source);
        var tokens = new List<SemanticTokenItem>();
        var scanDiag = new Diagnostics();
        var lexer = new Lexer(strippedSource, scanDiag);

        while (true)
        {
            var tok = lexer.Next();
            if (tok.Kind == TokenKind.EOF)
            {
                break;
            }

            if (tok.Kind != TokenKind.Ident)
            {
                continue;
            }

            int tokenType;
            if (traitNames.Contains(tok.Text))
            {
                tokenType = TokenTypeTrait;
            }
            else if (traitMethodNames.Contains(tok.Text))
            {
                tokenType = TokenTypeTraitMethod;
            }
            else if (typeNames.Contains(tok.Text))
            {
                tokenType = TokenTypeType;
            }
            else if (ctorNames.Contains(tok.Text))
            {
                tokenType = TokenTypeEnumMember;
            }
            else if (typeParamNames.Contains(tok.Text))
            {
                tokenType = TokenTypeTypeParameter;
            }
            else
            {
                continue;
            }

            // Map position in stripped source back to position in the original source.
            var originalPos = tok.Position + headerOffset;
            AddSemanticToken(tokens, originalLineStarts, source.Length, originalPos, tok.Length, tokenType);
        }

        return tokens;
    }

    private static void AddSemanticToken(
        List<SemanticTokenItem> tokens,
        SourceTextIndex index,
        int sourceLength,
        int start,
        int length,
        int tokenType)
    {
        (int line, int character) = LspTextUtils.ToLineCharacter(index, sourceLength, start);
        (int endLine, int endCharacter) = LspTextUtils.ToLineCharacter(index, sourceLength, start + length);
        if (endLine == line)
        {
            tokens.Add(new SemanticTokenItem(line, character, endCharacter - character, tokenType, 0));
        }
    }

    /// <summary>Returns completion candidates for <paramref name="source"/> without a cursor position,
    /// yielding the full in-scope name set. <paramref name="filePath"/> supplies project context.</summary>
    public static IReadOnlyList<string> GetCompletions(string source, string? filePath = null)
    {
        return GetCompletions(source, position: null, filePath);
    }

    /// <summary>
    /// Returns completion candidates for <paramref name="source"/> at <paramref name="position"/>. When
    /// the position sits after a module-qualifying dot, the candidates are that module's exports;
    /// otherwise the in-scope names are returned. A null position yields the full in-scope set.
    /// <paramref name="filePath"/> supplies project context when present.
    /// </summary>
    public static IReadOnlyList<string> GetCompletions(string source, int? position, string? filePath = null)
    {
        var header = StripImportHeader(source);
        if (position is not null
            && TryGetQualifiedCompletions(source, position.Value, filePath, header.Imports, out var moduleCompletions))
        {
            return moduleCompletions;
        }

        var context = PrepareAnalysisContext(source, filePath);
        if (context.Diagnostics.Count > 0)
        {
            return Array.Empty<string>();
        }

        var diag = new Diagnostics();
        var program = new Parser(context.AnalysisSource, diag).ParseProgram();
        var lowering = new Lowering(diag, context.ImportedStdModules, context.ModuleAliases, context.ConstructorModules);
        lowering.Lower(program);

        var completionNames = new HashSet<string>(lowering.ConstructorSymbols.Keys, StringComparer.Ordinal);
        completionNames.RemoveWhere(name =>
            name.StartsWith(ProjectSupport.PrivateConstructorPrefix, StringComparison.Ordinal)
            || name.StartsWith(ProjectSupport.PrivateTypePrefix, StringComparison.Ordinal));
        completionNames.UnionWith(lowering.TraitSymbols.Values.Select(trait => trait.Name));
        AddExternalCompletions(completionNames, program.ExternalDecls);

        var strippedDiag = new Diagnostics();
        var strippedProgram = new Parser(header.StrippedSource, strippedDiag).ParseProgram();
        if (strippedDiag.StructuredErrors.Count == 0)
        {
            // Top-level let/type names are file-scope symbols (Model-A): expose them all regardless of
            // the cursor position, since each is visible to everything that follows it.
            foreach (var name in CollectTopLevelDeclNames(strippedProgram))
            {
                completionNames.Add(name);
            }

            if (position is not null)
            {
                var strippedPosition = position.Value - header.HeaderOffset;
                if (strippedPosition >= 0 && strippedPosition <= new SourceTextIndex(header.StrippedSource).Utf8Length)
                {
                    foreach (var name in CollectVisibleBindingsInProgram(strippedProgram, strippedPosition))
                    {
                        completionNames.Add(name);
                    }
                }
            }
        }

        return completionNames
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddExternalCompletions(
        HashSet<string> completionNames,
        IReadOnlyList<ExternalDecl> declarations)
    {
        if (declarations.Count > 0)
        {
            completionNames.Add("FfiBuffer");
            completionNames.Add("FfiStr");
            completionNames.Add("out");
        }
        completionNames.UnionWith(declarations.Select(declaration => declaration switch
        {
            ExternalDecl.OpaqueType opaque => opaque.Name,
            ExternalDecl.Function function => function.Name,
            _ => string.Empty,
        }).Where(name => name.Length > 0));
    }

    /// <summary>
    /// Enumerates the names a program's top-level declarations bind: <c>let</c>/<c>let rec</c>
    /// bindings, every member of a mutual-recursion group, and <c>type</c> names.
    /// </summary>
    private static IEnumerable<string> CollectTopLevelDeclNames(Frontend.Program program)
    {
        foreach (var item in program.Items)
        {
            switch (item)
            {
                case TopLevelItem.LetDecl letDecl:
                    yield return letDecl.Name;
                    break;

                case TopLevelItem.RecursiveGroup group:
                    foreach (var (name, _) in group.Bindings)
                    {
                        yield return name;
                    }

                    break;

                case TopLevelItem.Type type:
                    yield return type.Decl.Name;
                    break;
                case TopLevelItem.TypeAlias alias:
                    yield return alias.Decl.Name;
                    break;
                case TopLevelItem.ZeroCostType zeroCostType:
                    yield return zeroCostType.Decl.Name;
                    break;
            }
        }
    }

    private static IReadOnlyCollection<string> CollectVisibleBindingsInProgram(Frontend.Program program, int position)
    {
        // Walk the top-level items in source order (Model-A): each binding becomes visible to the
        // values of subsequent declarations and to the trailing expression, never to earlier ones.
        var scope = new Dictionary<string, byte>(StringComparer.Ordinal);
        foreach (var item in program.Items)
        {
            switch (item)
            {
                case TopLevelItem.LetDecl letDecl:
                    if (letDecl.IsRecursive)
                    {
                        scope[letDecl.Name] = 0;
                    }

                    var inLetValue = CollectVisibleBindingsInExpr(letDecl.Value, position, scope);
                    if (inLetValue.Count > 0)
                    {
                        return inLetValue;
                    }

                    scope[letDecl.Name] = 0;
                    break;

                case TopLevelItem.RecursiveGroup group:
                    foreach (var (name, _) in group.Bindings)
                    {
                        scope[name] = 0;
                    }

                    foreach (var (_, value) in group.Bindings)
                    {
                        var inBinding = CollectVisibleBindingsInExpr(value, position, scope);
                        if (inBinding.Count > 0)
                        {
                            return inBinding;
                        }
                    }

                    break;
            }
        }

        // A flat top-level file may have no trailing expression (the parser folds a bare trailing
        // expression into the preceding declaration's value), so the body can be absent.
        Expr? body = program.Body;
        if (body is null)
        {
            return scope.Keys.ToArray();
        }

        var inBody = CollectVisibleBindingsInExpr(body, position, scope);
        return inBody.Count > 0 ? inBody : scope.Keys.ToArray();
    }

    private static IReadOnlyCollection<string> CollectVisibleBindingsInExpr(
        Expr expr,
        int position,
        IReadOnlyDictionary<string, byte> scope)
    {
        if (!ContainsCompletionPosition(AstSpans.GetOrDefault(expr), position))
        {
            return Array.Empty<string>();
        }

        if (GetBinaryOperands(expr) is { } operands)
        {
            return CollectVisibleBindingsInBinary(operands.Left, operands.Right, position, scope);
        }

        switch (expr)
        {
            case Expr.IntLit:
            case Expr.UIntLit:
            case Expr.BigIntLit:
            case Expr.FloatLit:
            case Expr.StrLit or Expr.RuneLit:
            case Expr.BoolLit:
            case Expr.Var:
            case Expr.QualifiedVar:
                return scope.Keys.ToArray();

            case Expr.BitwiseNot bitwiseNot:
                return CollectVisibleBindingsInExpr(bitwiseNot.Operand, position, scope);

            case Expr.LogicalNot logicalNot:
                return CollectVisibleBindingsInExpr(logicalNot.Operand, position, scope);

            default:
                return CollectVisibleBindingsInNestedExpr(expr, position, scope);
        }
    }

    private static IReadOnlyCollection<string> CollectVisibleBindingsInNestedExpr(
        Expr expr,
        int position,
        IReadOnlyDictionary<string, byte> scope)
    {
        switch (expr)
        {
            case Expr.Let letExpr:
                return CollectVisibleBindingsInLetBinding(letExpr.Name, letExpr.Value, letExpr.Body, position, scope);

            case Expr.LetResult letResultExpr:
                return CollectVisibleBindingsInLetBinding(letResultExpr.Name, letResultExpr.Value, letResultExpr.Body, position, scope);

            case Expr.LetRecursive letRecursiveExpr:
                return CollectVisibleBindingsInLetRecursive(letRecursiveExpr, position, scope);

            case Expr.If ifExpr:
                return CollectVisibleBindingsInExpr(ifExpr.Cond, position, scope)
                    .Concat(CollectVisibleBindingsInExpr(ifExpr.Then, position, scope))
                    .Concat(CollectVisibleBindingsInExpr(ifExpr.Else, position, scope))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

            case Expr.Lambda lambda:
                return CollectVisibleBindingsInLambda(lambda, position, scope);

            case Expr.Call call:
                return CollectVisibleBindingsInExpr(call.Func, position, scope)
                    .Concat(CollectVisibleBindingsInExpr(call.Arg, position, scope))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

            case Expr.TupleLit tuple:
                return tuple.Elements
                    .SelectMany(element => CollectVisibleBindingsInExpr(element, position, scope))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

            case Expr.ListLit list:
                return list.Elements
                    .SelectMany(element => CollectVisibleBindingsInExpr(element, position, scope))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

            case Expr.Cons cons:
                return CollectVisibleBindingsInExpr(cons.Head, position, scope)
                    .Concat(CollectVisibleBindingsInExpr(cons.Tail, position, scope))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

            case Expr.Match match:
                return CollectVisibleBindingsInMatch(match, position, scope);

            default:
                return Array.Empty<string>();
        }
    }

    private static IReadOnlyCollection<string> CollectVisibleBindingsInLetBinding(
        string name,
        Expr value,
        Expr body,
        int position,
        IReadOnlyDictionary<string, byte> scope)
    {
        var inValue = CollectVisibleBindingsInExpr(value, position, scope);
        if (inValue.Count > 0)
        {
            return inValue;
        }

        var bodyScope = CloneCompletionScope(scope);
        bodyScope[name] = 0;
        return CollectVisibleBindingsInExpr(body, position, bodyScope);
    }

    private static IReadOnlyCollection<string> CollectVisibleBindingsInLetRecursive(
        Expr.LetRecursive letRecursiveExpr,
        int position,
        IReadOnlyDictionary<string, byte> scope)
    {
        var recursiveScope = CloneCompletionScope(scope);
        recursiveScope[letRecursiveExpr.Name] = 0;

        var inValue = CollectVisibleBindingsInExpr(letRecursiveExpr.Value, position, recursiveScope);
        if (inValue.Count > 0)
        {
            return inValue;
        }

        return CollectVisibleBindingsInExpr(letRecursiveExpr.Body, position, recursiveScope);
    }

    private static IReadOnlyCollection<string> CollectVisibleBindingsInLambda(
        Expr.Lambda lambda,
        int position,
        IReadOnlyDictionary<string, byte> scope)
    {
        var lambdaScope = CloneCompletionScope(scope);
        lambdaScope[lambda.ParamName] = 0;
        return CollectVisibleBindingsInExpr(lambda.Body, position, lambdaScope);
    }

    private static IReadOnlyCollection<string> CollectVisibleBindingsInMatch(
        Expr.Match match,
        int position,
        IReadOnlyDictionary<string, byte> scope)
    {
        var inValue = CollectVisibleBindingsInExpr(match.Value, position, scope);
        if (inValue.Count > 0)
        {
            return inValue;
        }

        foreach (var matchCase in match.Cases)
        {
            var caseScope = CloneCompletionScope(scope);
            foreach (var binding in CollectPatternBindings(matchCase.Pattern, currentFilePath: null))
            {
                caseScope[binding.Key] = 0;
            }

            if (matchCase.Guard is not null)
            {
                var inGuard = CollectVisibleBindingsInExpr(matchCase.Guard, position, caseScope);
                if (inGuard.Count > 0)
                {
                    return inGuard;
                }
            }

            var inBody = CollectVisibleBindingsInExpr(matchCase.Body, position, caseScope);
            if (inBody.Count > 0)
            {
                return inBody;
            }
        }

        return Array.Empty<string>();
    }

    private static IReadOnlyCollection<string> CollectVisibleBindingsInBinary(
        Expr left,
        Expr right,
        int position,
        IReadOnlyDictionary<string, byte> scope)
    {
        return CollectVisibleBindingsInExpr(left, position, scope)
            .Concat(CollectVisibleBindingsInExpr(right, position, scope))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Extracts the operands of a two-operand operator expression (arithmetic, bitwise, comparison,
    /// and pipe forms); returns null for every other expression shape.
    /// </summary>
    private static (Expr Left, Expr Right)? GetBinaryOperands(Expr expr)
    {
        return expr switch
        {
            Expr.Add add => (add.Left, add.Right),
            Expr.Subtract sub => (sub.Left, sub.Right),
            Expr.Multiply mul => (mul.Left, mul.Right),
            Expr.Divide div => (div.Left, div.Right),
            Expr.Modulo modExpr => (modExpr.Left, modExpr.Right),
            Expr.BitwiseAnd bitAnd => (bitAnd.Left, bitAnd.Right),
            Expr.BitwiseOr bitOr => (bitOr.Left, bitOr.Right),
            Expr.BitwiseXor bitXor => (bitXor.Left, bitXor.Right),
            Expr.ShiftLeft shiftLeft => (shiftLeft.Left, shiftLeft.Right),
            Expr.ShiftRight shiftRight => (shiftRight.Left, shiftRight.Right),
            Expr.GreaterOrEqual ge => (ge.Left, ge.Right),
            Expr.LessOrEqual le => (le.Left, le.Right),
            Expr.Equal eq => (eq.Left, eq.Right),
            Expr.NotEqual ne => (ne.Left, ne.Right),
            Expr.ResultPipe pipe => (pipe.Left, pipe.Right),
            Expr.ResultMapErrorPipe pipe => (pipe.Left, pipe.Right),
            _ => null,
        };
    }

    private static Dictionary<string, byte> CloneCompletionScope(IReadOnlyDictionary<string, byte> scope)
    {
        return new Dictionary<string, byte>(scope, StringComparer.Ordinal);
    }

    private static bool ContainsCompletionPosition(TextSpan span, int position)
    {
        if (ContainsPosition(span, position))
        {
            return true;
        }

        return position == span.End;
    }

    private static bool TryGetQualifiedCompletions(
        string source,
        int position,
        string? filePath,
        IReadOnlyList<ImportItem> imports,
        out IReadOnlyList<string> completions)
    {
        completions = Array.Empty<string>();

        SourceTextIndex sourceIndex = new(source);
        if (position < 0 || position > sourceIndex.Utf8Length)
        {
            return false;
        }

        var prefix = ExtractCompletionPrefix(source, position);
        if (string.IsNullOrEmpty(prefix) || !prefix.EndsWith(".", StringComparison.Ordinal))
        {
            return false;
        }

        var qualifier = prefix[..^1];
        if (TryGetModuleExportCompletions(qualifier, filePath, imports, out completions))
        {
            return true;
        }

        int stringPosition = sourceIndex.ToUtf16Offset(position);
        int prefixStart = stringPosition - prefix.Length;
        string sourceWithoutIncompleteReference = source.Remove(prefixStart, prefix.Length);
        AnalysisContext context = PrepareAnalysisContext(sourceWithoutIncompleteReference, filePath);
        if (context.Diagnostics.Count > 0)
        {
            return false;
        }

        var diagnostics = new Diagnostics();
        Frontend.Program program = new Parser(context.AnalysisSource, diagnostics).ParseProgram();
        var lowering = new Lowering(
            diagnostics,
            context.ImportedStdModules,
            context.ModuleAliases,
            context.ConstructorModules);
        lowering.Lower(program);
        if (diagnostics.StructuredErrors.Count > 0)
        {
            return false;
        }

        TraitSymbol[] traits = lowering.TraitSymbols.Values
            .Where(trait => string.Equals(trait.Name, qualifier, StringComparison.Ordinal)
                || string.Equals(trait.QualifiedName, qualifier, StringComparison.Ordinal)
                || trait.QualifiedName.EndsWith($".{qualifier}", StringComparison.Ordinal))
            .DistinctBy(trait => trait.QualifiedName)
            .ToArray();
        if (traits.Length != 1)
        {
            return false;
        }

        completions = traits[0].Methods.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        return completions.Count > 0;
    }

    private static bool TryGetModuleExportCompletions(
        string qualifier,
        string? filePath,
        IReadOnlyList<ImportItem> imports,
        out IReadOnlyList<string> completions)
    {
        string? moduleName = ResolveCompletionModuleName(qualifier, imports);
        if (moduleName is null)
        {
            completions = [];
            return false;
        }

        completions = GetModuleCompletionItems(moduleName);
        if (completions.Count == 0 && filePath is not null)
        {
            completions = GetProjectModuleCompletionItems(moduleName, filePath);
        }

        return completions.Count > 0;
    }

    private static string ExtractCompletionPrefix(string source, int position)
    {
        int stringPosition = new SourceTextIndex(source).ToUtf16Offset(position);
        var start = stringPosition;
        while (start > 0)
        {
            var ch = source[start - 1];
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '.')
            {
                start--;
                continue;
            }

            break;
        }

        return source[start..stringPosition];
    }

    private static string? ResolveCompletionModuleName(string qualifier, IReadOnlyList<ImportItem> imports)
    {
        // Check whole-module alias matches first (selector imports rename a binding, not a module).
        foreach (var import in imports)
        {
            if (import.Selector is null && import.Alias is not null && string.Equals(import.Alias, qualifier, StringComparison.Ordinal))
            {
                return import.ModuleName;
            }
        }

        // A real module, or a pure namespace prefix of one (Ashes, Ashes.Net, Ashes.Number, ...):
        // both complete — modules with their exports, prefixes with their child segments.
        if (BuiltinRegistry.TryGetModule(qualifier, out _) || IsModuleNamespacePrefix(qualifier))
        {
            return qualifier;
        }

        var matches = imports
            .Where(import => string.Equals(GetLeafQualifier(import.ModuleName), qualifier, StringComparison.Ordinal))
            .Select(import => import.ModuleName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return matches.Length == 1 ? matches[0] : null;
    }

    private static string GetLeafQualifier(string moduleName)
    {
        var lastDot = moduleName.LastIndexOf('.');
        return lastDot < 0 ? moduleName : moduleName[(lastDot + 1)..];
    }

    private static bool IsModuleNamespacePrefix(string qualifier)
    {
        var prefix = qualifier + ".";
        return BuiltinRegistry.StandardModuleNames.Any(name => name.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> GetModuleCompletionItems(string moduleName)
    {
        // Everything is registry-derived: a module completes to its exports (intrinsic members
        // plus shipped-overlay bindings), and any name that prefixes deeper modules also offers
        // the next path segment (so Ashes.Text suggests Json/Regex alongside its functions).
        var items = new SortedSet<string>(StringComparer.Ordinal);
        if (BuiltinRegistry.TryGetModuleExports(moduleName, out var exports))
        {
            items.UnionWith(exports);
        }

        var prefix = moduleName + ".";
        foreach (var name in BuiltinRegistry.StandardModuleNames)
        {
            if (string.Equals(name, "Ashes.Internal", StringComparison.Ordinal)
                || name.StartsWith("Ashes.Internal.", StringComparison.Ordinal))
            {
                continue;
            }

            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                var rest = name[prefix.Length..];
                var dot = rest.IndexOf('.', StringComparison.Ordinal);
                items.Add(dot < 0 ? rest : rest[..dot]);
            }
        }

        return items.ToArray();
    }

    private static IReadOnlyList<string> GetProjectModuleCompletionItems(string moduleName, string filePath)
    {
        try
        {
            string? projectPath = ProjectSupport.DiscoverProjectFile(Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? filePath);
            if (projectPath is null)
            {
                return [];
            }

            AshesProject project = ProjectSupport.LoadProject(projectPath);
            ProjectCompilationPlan plan = ProjectSupport.BuildCompilationPlan(project with
            {
                EntryPath = Path.GetFullPath(filePath),
                EntryModuleName = Path.GetFileNameWithoutExtension(filePath),
            });
            ProjectModule? module = plan.OrderedModules.FirstOrDefault(candidate =>
                string.Equals(candidate.ModuleName, moduleName, StringComparison.Ordinal));
            if (module is null)
            {
                return [];
            }

            var diagnostics = new Diagnostics();
            Frontend.Program program = new Parser(module.Source, diagnostics).ParseProgram();
            if (diagnostics.StructuredErrors.Count > 0)
            {
                return [];
            }

            return CollectModuleCompletionExports(program);
        }
        catch (IOException)
        {
            return [];
        }
        catch (InvalidOperationException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> CollectModuleCompletionExports(Frontend.Program program)
    {
        ExportDecl? declaration = program.Items
            .OfType<TopLevelItem.Export>()
            .Select(item => item.Decl)
            .SingleOrDefault();
        if (declaration is null)
        {
            return program.Items.SelectMany(item => item switch
            {
                TopLevelItem.LetDecl letDecl => [letDecl.Name],
                TopLevelItem.RecursiveGroup group => group.Bindings.Select(binding => binding.Name),
                TopLevelItem.Type type => new[] { type.Decl.Name }.Concat(type.Decl.Constructors.Select(constructor => constructor.Name)),
                TopLevelItem.TypeAlias alias => [alias.Decl.Name],
                TopLevelItem.ZeroCostType zeroCostType => [zeroCostType.Decl.Name, zeroCostType.Decl.Constructor.Name],
                _ => [],
            }).Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, TypeDecl> types = CollectLspTypeShapes(program);
        foreach (ExportItem item in declaration.Items)
        {
            switch (item)
            {
                case ExportItem.Value value:
                    names.Add(value.Name);
                    break;
                case ExportItem.Module module:
                    names.Add(module.Name);
                    break;
                case ExportItem.Type type:
                    names.Add(type.Name);
                    AddCompletionConstructors(type, types, names);
                    break;
            }
        }

        return names.OrderBy(name => name, StringComparer.Ordinal).ToArray();
    }

    private static void AddCompletionConstructors(
        ExportItem.Type export,
        IReadOnlyDictionary<string, TypeDecl> types,
        HashSet<string> names)
    {
        if (export.Constructors is ExportConstructors.Selected selected)
        {
            names.UnionWith(selected.Names);
        }
        else if (export.Constructors is ExportConstructors.All
            && types.TryGetValue(export.Name, out TypeDecl? declaration))
        {
            names.UnionWith(declaration.Constructors.Select(constructor => constructor.Name));
        }
    }

    /// <summary>
    /// Returns the hover for the token at <paramref name="position"/> in <paramref name="source"/> — the
    /// inferred type, prefixed with the name when available — or null when nothing resolves there.
    /// <paramref name="filePath"/> supplies project context when present.
    /// </summary>
    public static HoverItem? GetHover(string source, int position, string? filePath = null)
    {
        ImportHeaderInfo hoverHeader = StripImportHeader(source);
        var context = PrepareAnalysisContext(source, filePath);
        if (context.Diagnostics.Count > 0)
        {
            return null;
        }

        var analysisPosition = MapOriginalPositionToAnalysis(position, context);
        if (analysisPosition is null)
        {
            return null;
        }

        var diag = new Diagnostics();
        var program = new Parser(context.AnalysisSource, diag).ParseProgram();
        var lowering = new Lowering(diag, context.ImportedStdModules, context.ModuleAliases, context.ConstructorModules);
        lowering.Lower(program);

        HoverItem? declarationHover = GetMappedTypeDeclarationHover(context, position, program)
            ?? GetMappedExternalDeclarationHover(context, position, program, lowering)
            ?? GetMappedTraitHover(context, position, lowering);
        if (declarationHover is not null)
        {
            return declarationHover;
        }

        var hover = lowering.GetTypeAtPosition(analysisPosition.Value);
        if (hover is null)
        {
            return null;
        }

        var mappedSpan = MapToOriginalSpan(
            hover.Value.Span.Start,
            hover.Value.Span.End,
            context);
        if (mappedSpan is null)
        {
            return null;
        }

        TypeScheme hoverScheme = new([], hover.Value.Type, hover.Value.Constraints ?? []);
        string formattedType = lowering.FormatTypeScheme(hoverScheme);
        string? canonicalName = ResolveCanonicalHoverName(hover.Value.Name, hoverHeader.Imports);
        string displayText = FormatHoverMarkdown(lowering, hover.Value, formattedType, canonicalName);

        return new HoverItem(
            mappedSpan.Value.Start + context.HeaderOffset,
            mappedSpan.Value.End + context.HeaderOffset,
            displayText);
    }

    private static HoverItem? GetMappedTypeDeclarationHover(
        AnalysisContext context,
        int originalPosition,
        Frontend.Program program)
    {
        int? analysisPosition = MapOriginalPositionToAnalysis(originalPosition, context);
        if (analysisPosition is null)
        {
            return null;
        }
        (Token Token, TextSpan Span)? identifier = FindIdentifierAtPosition(
            context.AnalysisSource,
            analysisPosition.Value);
        if (identifier is null)
        {
            return null;
        }

        string? declaration = null;
        string? kind = null;
        TypeAliasDecl? alias = program.TypeAliasDecls.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, identifier.Value.Token.Text, StringComparison.Ordinal));
        if (alias is not null)
        {
            declaration = Ashes.Formatter.Formatter.Format(
                new Frontend.Program([new TopLevelItem.TypeAlias(alias)], null)).Trim();
            kind = "type alias";
        }
        else
        {
            ZeroCostTypeDecl? zeroCostType = program.ZeroCostTypeDecls.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, identifier.Value.Token.Text, StringComparison.Ordinal)
                || string.Equals(candidate.Constructor.Name, identifier.Value.Token.Text, StringComparison.Ordinal));
            if (zeroCostType is not null)
            {
                declaration = Ashes.Formatter.Formatter.Format(
                    new Frontend.Program([new TopLevelItem.ZeroCostType(zeroCostType)], null)).Trim();
                kind = "zero-cost nominal type";
            }
        }

        TextSpan? mapped = declaration is null
            ? null
            : MapToOriginalSpan(identifier.Value.Span.Start, identifier.Value.Span.End, context);
        return mapped is null
            ? null
            : new HoverItem(
                mapped.Value.Start + context.HeaderOffset,
                mapped.Value.End + context.HeaderOffset,
                $"```ashes\n{declaration}\n```\n\n*{kind}*");
    }

    private static HoverItem? GetMappedExternalDeclarationHover(
        AnalysisContext context,
        int originalPosition,
        Frontend.Program program,
        Lowering lowering)
    {
        int? analysisPosition = MapOriginalPositionToAnalysis(originalPosition, context);
        if (analysisPosition is null)
        {
            return null;
        }

        (Token Token, TextSpan Span)? identifier = FindIdentifierAtPosition(
            context.AnalysisSource,
            analysisPosition.Value);
        if (identifier is null)
        {
            return null;
        }

        ExternalDecl? declaration = program.ExternalDecls.FirstOrDefault(candidate =>
            string.Equals(candidate switch
            {
                ExternalDecl.OpaqueType opaque => opaque.Name,
                ExternalDecl.Function function => function.Name,
                _ => string.Empty,
            }, identifier.Value.Token.Text, StringComparison.Ordinal));
        if (declaration is null)
        {
            return null;
        }

        Lowering.ExternalOwnershipInfo? ownership = lowering.GetExternalOwnershipInfo(
            identifier.Value.Token.Text);
        bool hasSpecialFfiParameter = declaration is ExternalDecl.Function function
            && (function.ReturnType is ParsedType.NativeString
                || function.ParameterTypes.Any(type => type is ParsedType.Buffer or ParsedType.Out));
        if (ownership is null && !hasSpecialFfiParameter)
        {
            return null;
        }

        string formatted = Ashes.Formatter.Formatter.Format(new Frontend.Program(
            [new TopLevelItem.External(declaration)], null)).Trim();
        string markdown = FormatExternalDeclarationHover(formatted, declaration, ownership);
        TextSpan? mapped = MapToOriginalSpan(identifier.Value.Span.Start, identifier.Value.Span.End, context);
        return mapped is null
            ? null
            : new HoverItem(mapped.Value.Start + context.HeaderOffset, mapped.Value.End + context.HeaderOffset, markdown);
    }

    private static string FormatExternalDeclarationHover(
        string formatted,
        ExternalDecl declaration,
        Lowering.ExternalOwnershipInfo? ownership)
    {
        var markdown = new StringBuilder($"```ashes\n{formatted}\n```\n\n");
        if (ownership is { IsResourceType: true } resourceOwnership)
        {
            markdown.Append("*affine external resource*\n\n**Destructor:** `");
            markdown.Append(resourceOwnership.Destructor);
            markdown.Append('`');
        }
        else
        {
            markdown.Append("*external function*");
            if (ownership is { } functionOwnership
                && (functionOwnership.ParameterOwnerships.Count > 0 || functionOwnership.ReturnsOwnedResource))
            {
                markdown.Append("\n\n**Resource ownership**");
                foreach (string parameter in functionOwnership.ParameterOwnerships)
                {
                    markdown.Append("\n\n- `");
                    markdown.Append(parameter);
                    markdown.Append('`');
                }
                if (functionOwnership.ReturnsOwnedResource)
                {
                    markdown.Append("\n\n- return: `owned`");
                }
            }

            if (declaration is ExternalDecl.Function function)
            {
                AppendFfiBufferHover(markdown, function);
                AppendFfiOutHover(markdown, function);
                AppendFfiStringHover(markdown, function);
            }
        }

        return markdown.ToString();
    }

    private static void AppendFfiBufferHover(StringBuilder markdown, ExternalDecl.Function function)
    {
        IReadOnlyList<(int Index, ParsedType.Buffer Buffer)> buffers = [.. function.ParameterTypes
            .Select((type, index) => (Index: index, Type: type))
            .Where(parameter => parameter.Type is ParsedType.Buffer)
            .Select(parameter => (parameter.Index, (ParsedType.Buffer)parameter.Type))];
        if (buffers.Count == 0)
        {
            return;
        }

        markdown.Append("\n\n**Call-scoped buffers**");
        foreach ((int index, ParsedType.Buffer buffer) in buffers)
        {
            string elementName = buffer.Element is ParsedType.Named named ? named.Name : "T";
            markdown.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"\n\n- `#{index + 1} FfiBuffer({elementName}): List({elementName})`");
        }
    }

    private static void AppendFfiOutHover(StringBuilder markdown, ExternalDecl.Function function)
    {
        IReadOnlyList<(int Index, ParsedType.Out Out)> outputs = [.. function.ParameterTypes
            .Select((type, index) => (Index: index, Type: type))
            .Where(parameter => parameter.Type is ParsedType.Out { Element: not ParsedType.NativeString })
            .Select(parameter => (parameter.Index, (ParsedType.Out)parameter.Type))];
        if (outputs.Count == 0)
        {
            return;
        }

        markdown.Append("\n\n**Compiler-owned outputs**");
        foreach ((int index, ParsedType.Out output) in outputs)
        {
            string elementName = FormatExternalHoverType(output.Element);
            markdown.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"\n\n- `#{index + 1} out {elementName}: Maybe({elementName})`");
        }
    }

    private static string FormatExternalHoverType(ParsedType type) => type switch
    {
        ParsedType.Named named => named.Name,
        ParsedType.Pointer pointer => "*" + FormatExternalHoverType(pointer.Pointee),
        ParsedType.NativeString native => FormatFfiStringHoverType(native),
        _ => "T",
    };

    private static void AppendFfiStringHover(StringBuilder markdown, ExternalDecl.Function function)
    {
        List<string> contracts = [];
        if (function.ReturnType is ParsedType.NativeString returned)
        {
            contracts.Add($"return `{FormatFfiStringHoverType(returned)}`: copied to `Result(Str, "
                + (returned.Nullable ? "Maybe(Str))`" : "Str)`"));
        }

        foreach ((ParsedType type, int index) in function.ParameterTypes.Select((type, index) => (type, index)))
        {
            if (type is ParsedType.Out { Element: ParsedType.NativeString output })
            {
                contracts.Add($"#{index + 1} `out {FormatFfiStringHoverType(output)}`: copied to `Result(Str, Maybe(Str))`");
            }
        }

        if (contracts.Count == 0)
        {
            return;
        }

        markdown.Append("\n\n**Native UTF-8 strings**");
        foreach (string contract in contracts)
        {
            markdown.Append("\n\n- ");
            markdown.Append(contract);
        }
    }

    private static string FormatFfiStringHoverType(ParsedType.NativeString native)
    {
        string nullable = native.Nullable ? "nullable " : string.Empty;
        string ownership = native.Ownership == FfiStringOwnership.Borrowed
            ? "borrowed"
            : "owned " + native.DestructorName;
        return $"FfiStr({nullable}{ownership})";
    }

    private static string? ResolveCanonicalHoverName(
        string? name,
        IReadOnlyList<ImportItem> imports)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }
        if (name.StartsWith("Ashes.", StringComparison.Ordinal))
        {
            return name;
        }

        foreach (ImportItem import in imports)
        {
            if (import.Selector is not null
                && string.Equals(import.LocalName, name, StringComparison.Ordinal))
            {
                return $"{import.ModuleName}.{import.Selector}";
            }
        }

        return StandardLibraryDocumentation.ResolveUnqualified(name, imports) ?? name;
    }

    private static string FormatHoverMarkdown(
        Lowering lowering,
        Lowering.HoverTypeInfo hover,
        string formattedType,
        string? canonicalName)
    {
        string? displayName = canonicalName ?? hover.Name;
        bool isFunction = hover.Type is TypeRef.TFun;
        string signature = string.IsNullOrEmpty(displayName)
            ? formattedType
            : $"{displayName} : {formattedType}";
        var markdown = new StringBuilder();
        markdown.Append("```ashes\n");
        markdown.Append(signature);
        markdown.Append("\n```");
        string kind = hover.IsParameter
            ? "parameter"
            : isFunction
                ? "function"
                : string.IsNullOrEmpty(displayName) ? "expression" : "value";
        markdown.Append($"\n\n*{kind}*");

        AppendParameterDetails(markdown, lowering, hover);

        if (displayName is not null
            && StandardLibraryDocumentation.TryGet(displayName, out StandardLibraryDocumentation.Entry documentation))
        {
            markdown.Append("\n\n---\n\n");
            markdown.Append(documentation.Summary);
            markdown.Append("\n\n[Open standard-library documentation](");
            markdown.Append(documentation.Url);
            markdown.Append(')');
        }

        return markdown.ToString();
    }

    private static void AppendParameterDetails(
        StringBuilder markdown,
        Lowering lowering,
        Lowering.HoverTypeInfo hover)
    {
        if (hover.ParameterNames is not { Count: > 0 })
        {
            return;
        }

        var parameterTypes = new List<TypeRef>();
        TypeRef returnType = hover.Type;
        foreach (string _ in hover.ParameterNames)
        {
            if (returnType is not TypeRef.TFun function)
            {
                return;
            }
            parameterTypes.Add(function.Arg);
            returnType = function.Ret;
        }

        IReadOnlyList<string> formattedParts = lowering.FormatTypes([.. parameterTypes, returnType]);
        markdown.Append("\n\n**Parameters**\n");
        for (int index = 0; index < hover.ParameterNames.Count; index++)
        {
            markdown.Append("\n- `");
            markdown.Append(hover.ParameterNames[index]);
            markdown.Append("` : `");
            markdown.Append(formattedParts[index]);
            markdown.Append('`');
        }
        markdown.Append("\n\n**Returns:** `");
        markdown.Append(formattedParts[^1]);
        markdown.Append('`');
    }

    private static HoverItem? GetMappedTraitHover(
        AnalysisContext context,
        int originalPosition,
        Lowering lowering)
    {
        int strippedPosition = originalPosition - context.HeaderOffset;
        var diagnostics = new Diagnostics();
        Frontend.Program program = new Parser(context.StrippedSource, diagnostics).ParseProgram();
        Lowering.HoverTypeInfo? hover = TryGetTraitHover(
            context.StrippedSource,
            strippedPosition,
            program,
            lowering);
        if (hover is null)
        {
            return null;
        }
        return new HoverItem(
            hover.Value.Span.Start + context.HeaderOffset,
            hover.Value.Span.End + context.HeaderOffset,
            FormatNamedDeclarationHover(hover.Value.Name ?? string.Empty));
    }

    private static string FormatNamedDeclarationHover(string declaration)
    {
        string kind = declaration.StartsWith("trait ", StringComparison.Ordinal)
            ? "trait"
            : "trait method";
        return $"```ashes\n{declaration}\n```\n\n*{kind}*";
    }

    private static Lowering.HoverTypeInfo? TryGetTraitHover(
        string analysisSource,
        int position,
        Frontend.Program program,
        Lowering lowering)
    {
        (Token Token, TextSpan Span)? identifier = FindIdentifierAtPosition(analysisSource, position);
        if (identifier is null)
        {
            return null;
        }

        TraitSymbol[] traits = lowering.TraitSymbols.Values
            .DistinctBy(trait => trait.QualifiedName)
            .ToArray();
        if (TryCreateNamedTraitHover(identifier.Value, traits, lowering) is { } namedHover)
        {
            return namedHover;
        }

        TraitSymbol[] methodTraits = traits
            .Where(trait => trait.Methods.ContainsKey(identifier.Value.Token.Text))
            .ToArray();
        string? qualifier = FindQualifierBeforeIdentifier(analysisSource, identifier.Value.Span.Start);
        if (qualifier is not null)
        {
            methodTraits = methodTraits
                .Where(trait => string.Equals(trait.Name, qualifier, StringComparison.Ordinal)
                    || string.Equals(trait.QualifiedName, qualifier, StringComparison.Ordinal)
                    || trait.QualifiedName.EndsWith($".{qualifier}", StringComparison.Ordinal))
                .ToArray();
        }
        else
        {
            string? declarationTrait = FindContainingTraitMethodOwner(program, position, identifier.Value.Token.Text);
            if (declarationTrait is not null)
            {
                methodTraits = methodTraits
                    .Where(trait => string.Equals(trait.Name, declarationTrait, StringComparison.Ordinal)
                        || string.Equals(trait.QualifiedName, declarationTrait, StringComparison.Ordinal)
                        || trait.QualifiedName.EndsWith($".{declarationTrait}", StringComparison.Ordinal))
                    .ToArray();
            }
        }
        return TryCreateTraitMethodHover(identifier.Value, methodTraits, lowering);
    }

    private static Lowering.HoverTypeInfo? TryCreateNamedTraitHover(
        (Token Token, TextSpan Span) identifier,
        IReadOnlyList<TraitSymbol> traits,
        Lowering lowering)
    {
        TraitSymbol[] matches = traits
            .Where(trait => string.Equals(trait.Name, identifier.Token.Text, StringComparison.Ordinal)
                || string.Equals(trait.QualifiedName, identifier.Token.Text, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            return null;
        }
        TraitSymbol trait = matches[0];
        string parameters = string.Join(", ", trait.TypeParameters.Select(parameter => parameter.Name));
        string requirements = trait.Supertraits.Count == 0
            ? string.Empty
            : $" requires {{{string.Join(", ", trait.Supertraits.Select(requirement => FormatTraitConstraintForTooling(lowering, requirement)))}}}";
        return new Lowering.HoverTypeInfo(
            identifier.Span,
            $"trait {trait.QualifiedName}({parameters}){requirements}",
            new TypeRef.TNever());
    }

    private static Lowering.HoverTypeInfo? TryCreateTraitMethodHover(
        (Token Token, TextSpan Span) identifier,
        IReadOnlyList<TraitSymbol> traits,
        Lowering lowering)
    {
        if (traits.Count != 1)
        {
            return null;
        }
        TraitSymbol owner = traits[0];
        TraitMethodSymbol method = owner.Methods[identifier.Token.Text];
        TypeRef[] traitArguments = owner.TypeParameters
            .Select(parameter => (TypeRef)new TypeRef.TTypeParam(parameter))
            .ToArray();
        var scheme = new TypeScheme(
            method.Scheme.Quantified,
            method.Scheme.Body,
            [.. method.Scheme.Constraints, new TraitConstraint(owner, traitArguments)]);
        return new Lowering.HoverTypeInfo(
            identifier.Span,
            $"{owner.QualifiedName}.{method.Name} : {lowering.FormatTypeScheme(scheme)}",
            method.Scheme.Body,
            scheme.Constraints);
    }

    private static string FormatTraitConstraintForTooling(Lowering lowering, TraitConstraint constraint) =>
        $"{constraint.Trait.QualifiedName}({string.Join(", ", constraint.TypeArgs.Select(lowering.FormatType))})";

    private static string? FindContainingTraitMethodOwner(
        Frontend.Program program,
        int position,
        string methodName)
    {
        foreach (TopLevelItem.Trait item in program.Items.OfType<TopLevelItem.Trait>())
        {
            if (item.Decl.Methods.Any(method => string.Equals(method.Name, methodName, StringComparison.Ordinal)
                    && ContainsPosition(AstSpans.GetOrDefault(method), position)))
            {
                return item.Decl.Name;
            }
        }
        foreach (TopLevelItem.Implementation item in program.Items.OfType<TopLevelItem.Implementation>())
        {
            if (item.Decl.Bindings.Any(binding => string.Equals(binding.MethodName, methodName, StringComparison.Ordinal)
                    && ContainsPosition(AstSpans.GetOrDefault(binding), position)))
            {
                return item.Decl.TraitName;
            }
        }
        return null;
    }

    private static (Token Token, TextSpan Span)? FindIdentifierAtPosition(string source, int position)
    {
        var diagnostics = new Diagnostics();
        var lexer = new Lexer(source, diagnostics);
        while (true)
        {
            Token token = lexer.Next();
            if (token.Kind == TokenKind.EOF)
            {
                return null;
            }
            TextSpan span = token.Span;
            if (token.Kind == TokenKind.Ident && ContainsPosition(span, position))
            {
                return (token, span);
            }
        }
    }

    private static string? FindQualifierBeforeIdentifier(string source, int identifierStart)
    {
        int end = new SourceTextIndex(source).ToUtf16Offset(identifierStart);
        if (end == 0 || source[end - 1] != '.')
        {
            return null;
        }
        int start = end - 1;
        while (start > 0)
        {
            char character = source[start - 1];
            if (!char.IsLetterOrDigit(character) && character != '_' && character != '.')
            {
                break;
            }
            start--;
        }
        return source[start..(end - 1)];
    }

    /// <summary>
    /// Resolves the definition of the symbol at <paramref name="position"/> in <paramref name="source"/>
    /// and returns its location, or null when nothing resolves there. The location may point into
    /// another file (an imported module). <paramref name="filePath"/> supplies project context.
    /// </summary>
    public static DefinitionItem? GetDefinition(string source, int position, string? filePath = null)
    {
        var header = StripImportHeader(source);
        if (header.Diagnostics.Count > 0)
        {
            return null;
        }

        var strippedPosition = position - header.HeaderOffset;
        if (strippedPosition < 0 || strippedPosition > new SourceTextIndex(header.StrippedSource).Utf8Length)
        {
            return null;
        }

        var diag = new Diagnostics();
        var program = new Parser(header.StrippedSource, diag).ParseProgram();
        if (diag.StructuredErrors.Count > 0)
        {
            return null;
        }

        var definition = ResolveDefinitionInProgram(
            program,
            header.StrippedSource,
            strippedPosition,
            filePath,
            header.Imports);
        if (definition is null)
        {
            return null;
        }

        if (string.Equals(definition.Value.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
        {
            return new DefinitionItem(filePath, definition.Value.Span.Start + header.HeaderOffset, definition.Value.Span.End + header.HeaderOffset);
        }

        return new DefinitionItem(definition.Value.FilePath, definition.Value.Span.Start, definition.Value.Span.End);
    }

    /// <summary>
    /// Returns references in the current document that resolve to the declaration at
    /// <paramref name="position"/>. Trait names and methods use the same identity-aware resolver as
    /// go-to-definition, so unrelated traits with identically named methods are not mixed.
    /// </summary>
    public static IReadOnlyList<ReferenceItem> GetReferences(
        string source,
        int position,
        string? filePath = null,
        bool includeDeclaration = true)
    {
        ImportHeaderInfo header = StripImportHeader(source);
        int strippedPosition = position - header.HeaderOffset;
        if (header.Diagnostics.Count > 0
            || strippedPosition < 0
            || strippedPosition > new SourceTextIndex(header.StrippedSource).Utf8Length)
        {
            return [];
        }

        var diagnostics = new Diagnostics();
        Frontend.Program program = new Parser(header.StrippedSource, diagnostics).ParseProgram();
        DefinitionLocation? target = ResolveDefinitionInProgram(
            program,
            header.StrippedSource,
            strippedPosition,
            filePath,
            header.Imports);
        (Token Token, TextSpan Span)? selected = FindIdentifierAtPosition(header.StrippedSource, strippedPosition);
        if (target is null || selected is null || diagnostics.StructuredErrors.Count > 0)
        {
            return [];
        }

        var references = new List<ReferenceItem>();
        var lexer = new Lexer(header.StrippedSource, new Diagnostics());
        while (true)
        {
            Token token = lexer.Next();
            if (token.Kind == TokenKind.EOF)
            {
                break;
            }
            if (token.Kind != TokenKind.Ident
                || !string.Equals(token.Text, selected.Value.Token.Text, StringComparison.Ordinal))
            {
                continue;
            }
            DefinitionLocation? candidate = ResolveDefinitionInProgram(
                program,
                header.StrippedSource,
                token.Position,
                filePath,
                header.Imports);
            if (!DefinitionLocationsEqual(candidate, target))
            {
                continue;
            }
            TextSpan span = token.Span;
            if (!includeDeclaration && IsDeclarationIdentifier(header.StrippedSource, span, target.Value))
            {
                continue;
            }
            references.Add(new ReferenceItem(
                filePath,
                span.Start + header.HeaderOffset,
                span.End + header.HeaderOffset));
        }
        return references;
    }

    private static bool DefinitionLocationsEqual(
        DefinitionLocation? left,
        DefinitionLocation? right) =>
        left is not null
        && right is not null
        && string.Equals(left.Value.FilePath, right.Value.FilePath, StringComparison.OrdinalIgnoreCase)
        && left.Value.Span == right.Value.Span;

    private static bool IsDeclarationIdentifier(
        string source,
        TextSpan candidate,
        DefinitionLocation definition)
    {
        if (candidate.Start < definition.Span.Start || candidate.End > definition.Span.End)
        {
            return false;
        }
        (Token Token, TextSpan Span)? first = FindFirstIdentifierInSpan(source, definition.Span);
        return first is not null && first.Value.Span == candidate;
    }

    private static (Token Token, TextSpan Span)? FindFirstIdentifierInSpan(string source, TextSpan outer)
    {
        var lexer = new Lexer(source, new Diagnostics());
        while (true)
        {
            Token token = lexer.Next();
            if (token.Kind == TokenKind.EOF || token.Position >= outer.End)
            {
                return null;
            }
            if (token.Kind == TokenKind.Ident && token.Position >= outer.Start)
            {
                return (token, token.Span);
            }
        }
    }

    /// <summary>
    /// Maps a span in the combined source back to the import-stripped entry source, or returns null
    /// for stitched-module content. The entry region is line/column preserving even when its type
    /// declarations were replaced by blank lines or import aliases, so body spans map by line and
    /// character rather than by subtracting the hoisted declaration length.
    /// </summary>
    private static TextSpan? MapToOriginalSpan(int start, int end, AnalysisContext context)
    {
        if (context.EntryOffset == 0)
        {
            return TextSpan.FromBounds(start, end);
        }

        if (context.BodyStart > 0 && start <= context.BodyStart && end <= context.BodyStart)
        {
            return TextSpan.FromBounds(start, end);
        }

        SourceTextIndex analysisIndex = new(context.AnalysisSource);
        if (start >= context.EntryOffset && end <= analysisIndex.Utf8Length)
        {
            string entrySource = context.AnalysisSource[analysisIndex.ToUtf16Offset(context.EntryOffset)..];
            return TextSpan.FromBounds(
                MapPositionByLineAndCharacter(
                    entrySource,
                    start - context.EntryOffset,
                    context.StrippedSource),
                MapPositionByLineAndCharacter(
                    entrySource,
                    end - context.EntryOffset,
                    context.StrippedSource));
        }

        return null;
    }

    private static bool ExplicitInterfaceExportsName(Frontend.Program program, string name)
    {
        ExportDecl? declaration = program.Items
            .OfType<TopLevelItem.Export>()
            .Select(item => item.Decl)
            .SingleOrDefault();
        if (declaration is null)
        {
            return true;
        }

        IReadOnlyDictionary<string, TypeDecl> types = CollectLspTypeShapes(program);
        foreach (ExportItem item in declaration.Items)
        {
            if (item is ExportItem.Value value && string.Equals(value.Name, name, StringComparison.Ordinal)
                || item is ExportItem.Module module && string.Equals(module.Name, name, StringComparison.Ordinal)
                || item is ExportItem.Type type && ExportedTypeContainsName(type, name, types))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ExportedTypeContainsName(
        ExportItem.Type export,
        string name,
        IReadOnlyDictionary<string, TypeDecl> types)
    {
        if (string.Equals(export.Name, name, StringComparison.Ordinal))
        {
            return true;
        }

        return export.Constructors switch
        {
            ExportConstructors.Selected selected => selected.Names.Contains(name, StringComparer.Ordinal),
            ExportConstructors.All when types.TryGetValue(export.Name, out TypeDecl? declaration) =>
                declaration.Constructors.Any(constructor => string.Equals(constructor.Name, name, StringComparison.Ordinal)),
            _ => false,
        };
    }

    private static int? MapOriginalPositionToAnalysis(int position, AnalysisContext context)
    {
        var strippedPosition = position - context.HeaderOffset;
        if (strippedPosition < 0 || strippedPosition > new SourceTextIndex(context.StrippedSource).Utf8Length)
        {
            return null;
        }

        if (context.EntryOffset == 0)
        {
            return strippedPosition;
        }

        if (context.BodyStart > 0 && strippedPosition <= context.BodyStart)
        {
            return strippedPosition;
        }

        SourceTextIndex analysisIndex = new(context.AnalysisSource);
        string entrySource = context.AnalysisSource[analysisIndex.ToUtf16Offset(context.EntryOffset)..];
        return context.EntryOffset + MapPositionByLineAndCharacter(
            context.StrippedSource,
            strippedPosition,
            entrySource);
    }

    private static int MapPositionByLineAndCharacter(
        string source,
        int sourcePosition,
        string target)
    {
        SourceTextIndex sourceLineStarts = LspTextUtils.GetLineStarts(source);
        (int line, int character) = LspTextUtils.ToLineCharacter(
            sourceLineStarts,
            source.Length,
            sourcePosition,
            SourcePositionEncoding.UnicodeScalar);
        SourceTextIndex targetLineStarts = LspTextUtils.GetLineStarts(target);
        return LspTextUtils.FromLineCharacter(
            targetLineStarts,
            target.Length,
            line,
            character,
            SourcePositionEncoding.UnicodeScalar);
    }

    private static DefinitionLocation? ResolveDefinitionInProgram(
        Frontend.Program program,
        string source,
        int position,
        string? currentFilePath,
        IReadOnlyList<ImportItem> imports)
    {
        DefinitionLocation? namedDefinition = ResolveTraitDefinitionInProgram(
                program, source, position, currentFilePath, imports)
            ?? ResolveTypeDefinitionInProgram(program, source, position, currentFilePath);
        if (namedDefinition is not null)
        {
            return namedDefinition;
        }

        // Resolve through the top-level items first (Model-A): a binding declared earlier is visible
        // to the values of later declarations and to the trailing expression. Top-level binding names
        // have no dedicated span, so a reference resolves to the bound value.
        var scope = new Dictionary<string, DefinitionLocation>(StringComparer.Ordinal);
        foreach (var item in program.Items)
        {
            switch (item)
            {
                case TopLevelItem.LetDecl letDecl:
                    var letDefinition = new DefinitionLocation(currentFilePath, AstSpans.GetOrDefault(letDecl.Value));
                    if (letDecl.IsRecursive)
                    {
                        scope[letDecl.Name] = letDefinition;
                    }

                    var inLetValue = ResolveDefinitionInExpr(letDecl.Value, position, currentFilePath, imports, scope);
                    if (inLetValue is not null)
                    {
                        return inLetValue;
                    }

                    scope[letDecl.Name] = letDefinition;
                    break;

                case TopLevelItem.RecursiveGroup group:
                    foreach (var (name, value) in group.Bindings)
                    {
                        scope[name] = new DefinitionLocation(currentFilePath, AstSpans.GetOrDefault(value));
                    }

                    foreach (var (_, value) in group.Bindings)
                    {
                        var inBinding = ResolveDefinitionInExpr(value, position, currentFilePath, imports, scope);
                        if (inBinding is not null)
                        {
                            return inBinding;
                        }
                    }

                    break;
            }
        }

        Expr? body = program.Body;
        return body is null ? null : ResolveDefinitionInExpr(body, position, currentFilePath, imports, scope);
    }

    private static DefinitionLocation? ResolveTypeDefinitionInProgram(
        Frontend.Program program,
        string source,
        int position,
        string? currentFilePath)
    {
        (Token Token, TextSpan Span)? identifier = FindIdentifierAtPosition(source, position);
        if (identifier is null)
        {
            return null;
        }
        string name = identifier.Value.Token.Text;
        foreach (TopLevelItem item in program.Items)
        {
            switch (item)
            {
                case TopLevelItem.Type type when string.Equals(type.Decl.Name, name, StringComparison.Ordinal):
                    return new DefinitionLocation(currentFilePath, AstSpans.GetOrDefault(type.Decl));
                case TopLevelItem.Type type:
                    TypeConstructor? constructor = type.Decl.Constructors.FirstOrDefault(candidate =>
                        string.Equals(candidate.Name, name, StringComparison.Ordinal));
                    if (constructor is not null)
                    {
                        return new DefinitionLocation(currentFilePath, AstSpans.GetOrDefault(constructor));
                    }
                    break;
                case TopLevelItem.TypeAlias alias when string.Equals(alias.Decl.Name, name, StringComparison.Ordinal):
                    return new DefinitionLocation(currentFilePath, AstSpans.GetOrDefault(alias.Decl));
                case TopLevelItem.ZeroCostType zeroCostType
                    when string.Equals(zeroCostType.Decl.Name, name, StringComparison.Ordinal):
                    return new DefinitionLocation(currentFilePath, AstSpans.GetOrDefault(zeroCostType.Decl));
                case TopLevelItem.ZeroCostType zeroCostType
                    when string.Equals(zeroCostType.Decl.Constructor.Name, name, StringComparison.Ordinal):
                    return new DefinitionLocation(currentFilePath, AstSpans.GetOrDefault(zeroCostType.Decl.Constructor));
            }
        }

        return null;
    }

    private static DefinitionLocation? ResolveTraitDefinitionInProgram(
        Frontend.Program program,
        string source,
        int position,
        string? currentFilePath,
        IReadOnlyList<ImportItem> imports)
    {
        (Token Token, TextSpan Span)? identifier = FindIdentifierAtPosition(source, position);
        if (identifier is null)
        {
            return null;
        }

        TopLevelItem.Trait? localTrait = program.Items
            .OfType<TopLevelItem.Trait>()
            .SingleOrDefault(item => string.Equals(item.Decl.Name, identifier.Value.Token.Text, StringComparison.Ordinal));
        if (localTrait is not null)
        {
            return new DefinitionLocation(currentFilePath, AstSpans.GetOrDefault(localTrait.Decl));
        }

        if (char.IsUpper(identifier.Value.Token.Text[0])
            && ResolveImportedTraitDefinition(
                imports,
                identifier.Value.Token.Text,
                methodName: null,
                currentFilePath) is { } importedTrait)
        {
            return importedTrait;
        }

        string? ownerName = FindQualifierBeforeIdentifier(source, identifier.Value.Span.Start)
            ?? FindContainingTraitMethodOwner(program, position, identifier.Value.Token.Text);
        if (ownerName is null)
        {
            return null;
        }
        TopLevelItem.Trait? owner = program.Items
            .OfType<TopLevelItem.Trait>()
            .SingleOrDefault(item => string.Equals(item.Decl.Name, ownerName, StringComparison.Ordinal)
                || ownerName.EndsWith($".{item.Decl.Name}", StringComparison.Ordinal));
        TraitMethodDecl? method = owner?.Decl.Methods
            .SingleOrDefault(candidate => string.Equals(candidate.Name, identifier.Value.Token.Text, StringComparison.Ordinal));
        if (method is not null)
        {
            return new DefinitionLocation(currentFilePath, AstSpans.GetOrDefault(method));
        }

        return ResolveImportedTraitDefinition(
            imports,
            ownerName,
            identifier.Value.Token.Text,
            currentFilePath);
    }

    private static DefinitionLocation? ResolveImportedTraitDefinition(
        IReadOnlyList<ImportItem> imports,
        string traitReference,
        string? methodName,
        string? currentFilePath)
    {
        if (currentFilePath is null)
        {
            return null;
        }

        string? projectPath = ProjectSupport.DiscoverProjectFile(
            Path.GetDirectoryName(Path.GetFullPath(currentFilePath)) ?? currentFilePath);
        if (projectPath is null)
        {
            return null;
        }

        try
        {
            return ResolveImportedTraitDefinitionInProject(
                imports,
                traitReference,
                methodName,
                currentFilePath,
                projectPath);
        }
        catch (IOException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DefinitionLocation? ResolveImportedTraitDefinitionInProject(
        IReadOnlyList<ImportItem> imports,
        string traitReference,
        string? methodName,
        string currentFilePath,
        string projectPath)
    {
        AshesProject project = ProjectSupport.LoadProject(projectPath);
        string currentFullPath = Path.GetFullPath(currentFilePath);
        ProjectCompilationPlan plan = ProjectSupport.BuildCompilationPlan(project with
        {
            EntryPath = currentFullPath,
            EntryModuleName = Path.GetFileNameWithoutExtension(currentFullPath),
        });
        DefinitionLocation? match = null;
        foreach (ImportItem import in imports)
        {
            ProjectModule? module = plan.OrderedModules
                .Where(candidate => string.Equals(candidate.ModuleName, import.ModuleName, StringComparison.Ordinal)
                    || import.ModuleName.StartsWith(candidate.ModuleName + ".", StringComparison.Ordinal))
                .OrderByDescending(candidate => candidate.ModuleName.Length)
                .FirstOrDefault();
            if (module is null || !File.Exists(module.FilePath))
            {
                continue;
            }

            string selectedTrait = import.ModuleName.Length == module.ModuleName.Length
                ? string.Empty
                : import.ModuleName[(module.ModuleName.Length + 1)..];
            DefinitionLocation? candidate = FindModuleTraitDefinition(
                module,
                import,
                selectedTrait,
                traitReference,
                methodName);
            if (candidate is null)
            {
                continue;
            }
            if (match is not null && !DefinitionLocationsEqual(match, candidate))
            {
                return null;
            }
            match = candidate;
        }
        return match;
    }

    private static DefinitionLocation? FindModuleTraitDefinition(
        ProjectModule module,
        ImportItem import,
        string selectedTrait,
        string traitReference,
        string? methodName)
    {
        string originalSource = SourceTextIndex.ReadUtf8File(module.FilePath);
        ImportHeaderInfo header = StripImportHeader(originalSource);
        if (header.Diagnostics.Count > 0)
        {
            return null;
        }

        var diagnostics = new Diagnostics();
        Frontend.Program program = new Parser(header.StrippedSource, diagnostics).ParseProgram();
        if (diagnostics.StructuredErrors.Count > 0)
        {
            return null;
        }

        foreach (TopLevelItem.Trait item in program.Items.OfType<TopLevelItem.Trait>())
        {
            if (selectedTrait.Length > 0
                && !string.Equals(selectedTrait, item.Decl.Name, StringComparison.Ordinal))
            {
                continue;
            }

            string leaf = GetLeafQualifier(module.ModuleName);
            string[] references = selectedTrait.Length > 0
                ? [import.Alias ?? item.Decl.Name, item.Decl.Name]
                : [
                    item.Decl.Name,
                    $"{module.ModuleName}.{item.Decl.Name}",
                    $"{leaf}.{item.Decl.Name}",
                    import.Alias is null ? string.Empty : $"{import.Alias}.{item.Decl.Name}",
                ];
            if (!references.Contains(traitReference, StringComparer.Ordinal))
            {
                continue;
            }

            TextSpan span;
            if (methodName is null)
            {
                span = AstSpans.GetOrDefault(item.Decl);
            }
            else
            {
                TraitMethodDecl? method = item.Decl.Methods.SingleOrDefault(candidate =>
                    string.Equals(candidate.Name, methodName, StringComparison.Ordinal));
                if (method is null)
                {
                    continue;
                }
                span = AstSpans.GetOrDefault(method);
            }
            return new DefinitionLocation(
                module.FilePath,
                TextSpan.FromBounds(span.Start + header.HeaderOffset, span.End + header.HeaderOffset));
        }
        return null;
    }

    private static DefinitionLocation? ResolveDefinitionInExpr(
        Expr expr,
        int position,
        string? currentFilePath,
        IReadOnlyList<ImportItem> imports,
        IReadOnlyDictionary<string, DefinitionLocation> scope)
    {
        if (GetBinaryOperands(expr) is { } operands)
        {
            return ResolveDefinitionInBinary(operands.Left, operands.Right, position, currentFilePath, imports, scope);
        }

        switch (expr)
        {
            case Expr.IntLit:
            case Expr.UIntLit:
            case Expr.BigIntLit:
            case Expr.FloatLit:
            case Expr.StrLit or Expr.RuneLit:
            case Expr.BoolLit:
                return null;

            case Expr.Var varExpr:
                if (!ContainsPosition(AstSpans.GetOrDefault(varExpr), position))
                {
                    return null;
                }

                if (scope.TryGetValue(varExpr.Name, out var localDefinition))
                {
                    return localDefinition;
                }

                return ResolveImportedDefinition(imports, varExpr.Name, currentFilePath);

            case Expr.QualifiedVar qualifiedVar:
                return ContainsPosition(AstSpans.GetOrDefault(qualifiedVar), position)
                    ? ResolveQualifiedDefinition(qualifiedVar.Module, qualifiedVar.Name, currentFilePath, imports)
                    : null;

            case Expr.BitwiseNot bitwiseNot:
                return ResolveDefinitionInExpr(bitwiseNot.Operand, position, currentFilePath, imports, scope);

            case Expr.LogicalNot logicalNot:
                return ResolveDefinitionInExpr(logicalNot.Operand, position, currentFilePath, imports, scope);

            default:
                return ResolveDefinitionInNestedExpr(expr, position, currentFilePath, imports, scope);
        }
    }

    private static DefinitionLocation? ResolveDefinitionInNestedExpr(
        Expr expr,
        int position,
        string? currentFilePath,
        IReadOnlyList<ImportItem> imports,
        IReadOnlyDictionary<string, DefinitionLocation> scope)
    {
        switch (expr)
        {
            case Expr.Let letExpr:
                return ResolveDefinitionInLet(letExpr, position, currentFilePath, imports, scope);

            case Expr.LetResult letResultExpr:
                return ResolveDefinitionInLetResult(letResultExpr, position, currentFilePath, imports, scope);

            case Expr.LetRecursive letRecursiveExpr:
                return ResolveDefinitionInLetRecursive(letRecursiveExpr, position, currentFilePath, imports, scope);

            case Expr.If ifExpr:
                return ResolveDefinitionInExpr(ifExpr.Cond, position, currentFilePath, imports, scope)
                    ?? ResolveDefinitionInExpr(ifExpr.Then, position, currentFilePath, imports, scope)
                    ?? ResolveDefinitionInExpr(ifExpr.Else, position, currentFilePath, imports, scope);

            case Expr.Lambda lambda:
                return ResolveDefinitionInLambda(lambda, position, currentFilePath, imports, scope);

            case Expr.Call call:
                return ResolveDefinitionInExpr(call.Func, position, currentFilePath, imports, scope)
                    ?? ResolveDefinitionInExpr(call.Arg, position, currentFilePath, imports, scope);

            case Expr.TupleLit tuple:
                return tuple.Elements
                    .Select(element => ResolveDefinitionInExpr(element, position, currentFilePath, imports, scope))
                    .FirstOrDefault(result => result is not null);

            case Expr.ListLit list:
                return list.Elements
                    .Select(element => ResolveDefinitionInExpr(element, position, currentFilePath, imports, scope))
                    .FirstOrDefault(result => result is not null);

            case Expr.Cons cons:
                return ResolveDefinitionInExpr(cons.Head, position, currentFilePath, imports, scope)
                    ?? ResolveDefinitionInExpr(cons.Tail, position, currentFilePath, imports, scope);

            case Expr.Match match:
                return ResolveDefinitionInMatch(match, position, currentFilePath, imports, scope);

            default:
                return null;
        }
    }

    private static DefinitionLocation? ResolveDefinitionInLet(
        Expr.Let letExpr,
        int position,
        string? currentFilePath,
        IReadOnlyList<ImportItem> imports,
        IReadOnlyDictionary<string, DefinitionLocation> scope)
    {
        var bindingDefinition = new DefinitionLocation(currentFilePath, AstSpans.GetLetNameOrDefault(letExpr));
        if (ContainsPosition(bindingDefinition.Span, position))
        {
            return bindingDefinition;
        }

        var inValue = ResolveDefinitionInExpr(letExpr.Value, position, currentFilePath, imports, scope);
        if (inValue is not null)
        {
            return inValue;
        }

        var bodyScope = CloneScope(scope);
        bodyScope[letExpr.Name] = bindingDefinition;
        return ResolveDefinitionInExpr(letExpr.Body, position, currentFilePath, imports, bodyScope);
    }

    private static DefinitionLocation? ResolveDefinitionInLetResult(
        Expr.LetResult letResultExpr,
        int position,
        string? currentFilePath,
        IReadOnlyList<ImportItem> imports,
        IReadOnlyDictionary<string, DefinitionLocation> scope)
    {
        var bindingDefinition = new DefinitionLocation(currentFilePath, AstSpans.GetLetResultNameOrDefault(letResultExpr));
        if (ContainsPosition(bindingDefinition.Span, position))
        {
            return bindingDefinition;
        }

        var inValue = ResolveDefinitionInExpr(letResultExpr.Value, position, currentFilePath, imports, scope);
        if (inValue is not null)
        {
            return inValue;
        }

        var bodyScope = CloneScope(scope);
        bodyScope[letResultExpr.Name] = bindingDefinition;
        return ResolveDefinitionInExpr(letResultExpr.Body, position, currentFilePath, imports, bodyScope);
    }

    private static DefinitionLocation? ResolveDefinitionInLetRecursive(
        Expr.LetRecursive letRecursiveExpr,
        int position,
        string? currentFilePath,
        IReadOnlyList<ImportItem> imports,
        IReadOnlyDictionary<string, DefinitionLocation> scope)
    {
        var bindingDefinition = new DefinitionLocation(currentFilePath, AstSpans.GetLetRecursiveNameOrDefault(letRecursiveExpr));
        if (ContainsPosition(bindingDefinition.Span, position))
        {
            return bindingDefinition;
        }

        var recursiveScope = CloneScope(scope);
        recursiveScope[letRecursiveExpr.Name] = bindingDefinition;

        var inValue = ResolveDefinitionInExpr(letRecursiveExpr.Value, position, currentFilePath, imports, recursiveScope);
        if (inValue is not null)
        {
            return inValue;
        }

        return ResolveDefinitionInExpr(letRecursiveExpr.Body, position, currentFilePath, imports, recursiveScope);
    }

    private static DefinitionLocation? ResolveDefinitionInLambda(
        Expr.Lambda lambda,
        int position,
        string? currentFilePath,
        IReadOnlyList<ImportItem> imports,
        IReadOnlyDictionary<string, DefinitionLocation> scope)
    {
        var parameterDefinition = new DefinitionLocation(currentFilePath, AstSpans.GetLambdaParameterOrDefault(lambda));
        if (ContainsPosition(parameterDefinition.Span, position))
        {
            return parameterDefinition;
        }

        var lambdaScope = CloneScope(scope);
        lambdaScope[lambda.ParamName] = parameterDefinition;
        return ResolveDefinitionInExpr(lambda.Body, position, currentFilePath, imports, lambdaScope);
    }

    private static DefinitionLocation? ResolveDefinitionInMatch(
        Expr.Match match,
        int position,
        string? currentFilePath,
        IReadOnlyList<ImportItem> imports,
        IReadOnlyDictionary<string, DefinitionLocation> scope)
    {
        var inValue = ResolveDefinitionInExpr(match.Value, position, currentFilePath, imports, scope);
        if (inValue is not null)
        {
            return inValue;
        }

        foreach (var matchCase in match.Cases)
        {
            var inPattern = ResolveDefinitionInPattern(matchCase.Pattern, position, currentFilePath);
            if (inPattern is not null)
            {
                return inPattern;
            }

            var caseScope = CloneScope(scope);
            foreach (var binding in CollectPatternBindings(matchCase.Pattern, currentFilePath))
            {
                caseScope[binding.Key] = binding.Value;
            }

            if (matchCase.Guard is not null)
            {
                var inGuard = ResolveDefinitionInExpr(matchCase.Guard, position, currentFilePath, imports, caseScope);
                if (inGuard is not null)
                {
                    return inGuard;
                }
            }

            var inBody = ResolveDefinitionInExpr(matchCase.Body, position, currentFilePath, imports, caseScope);
            if (inBody is not null)
            {
                return inBody;
            }
        }

        return null;
    }

    private static DefinitionLocation? ResolveDefinitionInBinary(
        Expr left,
        Expr right,
        int position,
        string? currentFilePath,
        IReadOnlyList<ImportItem> imports,
        IReadOnlyDictionary<string, DefinitionLocation> scope)
    {
        return ResolveDefinitionInExpr(left, position, currentFilePath, imports, scope)
            ?? ResolveDefinitionInExpr(right, position, currentFilePath, imports, scope);
    }

    private static DefinitionLocation? ResolveDefinitionInPattern(Pattern pattern, int position, string? currentFilePath)
    {
        switch (pattern)
        {
            case Pattern.Var varPattern when IsPatternVariable(varPattern):
                {
                    var definition = new DefinitionLocation(currentFilePath, AstSpans.GetOrDefault(varPattern));
                    return ContainsPosition(definition.Span, position) ? definition : null;
                }

            case Pattern.Cons cons:
                return ResolveDefinitionInPattern(cons.Head, position, currentFilePath)
                    ?? ResolveDefinitionInPattern(cons.Tail, position, currentFilePath);

            case Pattern.Tuple tuple:
                return tuple.Elements
                    .Select(element => ResolveDefinitionInPattern(element, position, currentFilePath))
                    .FirstOrDefault(result => result is not null);

            case Pattern.Constructor ctor:
                return ctor.Patterns
                    .Select(element => ResolveDefinitionInPattern(element, position, currentFilePath))
                    .FirstOrDefault(result => result is not null);

            case Pattern.Record record:
                return record.Fields
                    .Select(field => ResolveDefinitionInPattern(field.Pattern, position, currentFilePath))
                    .FirstOrDefault(result => result is not null);

            case Pattern.As asPattern:
                return ResolveDefinitionInPattern(asPattern.Inner, position, currentFilePath)
                    ?? (ContainsPosition(AstSpans.GetAsPatternNameOrDefault(asPattern), position)
                        ? new DefinitionLocation(currentFilePath, AstSpans.GetAsPatternNameOrDefault(asPattern))
                        : null);

            case Pattern.Or orPattern:
                return orPattern.Alternatives
                    .Select(alternative => ResolveDefinitionInPattern(alternative, position, currentFilePath))
                    .FirstOrDefault(result => result is not null);

            default:
                return null;
        }
    }

    private static Dictionary<string, DefinitionLocation> CollectPatternBindings(Pattern pattern, string? currentFilePath)
    {
        var bindings = new Dictionary<string, DefinitionLocation>(StringComparer.Ordinal);
        Visit(pattern);
        return bindings;

        void Visit(Pattern current)
        {
            switch (current)
            {
                case Pattern.Var varPattern when IsPatternVariable(varPattern):
                    bindings[varPattern.Name] = new DefinitionLocation(currentFilePath, AstSpans.GetOrDefault(varPattern));
                    break;

                case Pattern.Cons cons:
                    Visit(cons.Head);
                    Visit(cons.Tail);
                    break;

                case Pattern.Tuple tuple:
                    foreach (var element in tuple.Elements)
                    {
                        Visit(element);
                    }
                    break;

                case Pattern.Constructor ctor:
                    foreach (var element in ctor.Patterns)
                    {
                        Visit(element);
                    }
                    break;

                case Pattern.Record record:
                    foreach ((string _, Pattern fieldPattern) in record.Fields)
                    {
                        Visit(fieldPattern);
                    }
                    break;

                case Pattern.As asPattern:
                    Visit(asPattern.Inner);
                    bindings[asPattern.Name] = new DefinitionLocation(currentFilePath, AstSpans.GetAsPatternNameOrDefault(asPattern));
                    break;

                case Pattern.Or { Alternatives.Count: > 0 } orPattern:
                    Visit(orPattern.Alternatives[0]);
                    break;
            }
        }
    }

    private static bool IsPatternVariable(Pattern.Var varPattern)
    {
        return !string.IsNullOrEmpty(varPattern.Name)
               && !char.IsUpper(varPattern.Name[0]);
    }

    private static Dictionary<string, DefinitionLocation> CloneScope(IReadOnlyDictionary<string, DefinitionLocation> scope)
    {
        return new Dictionary<string, DefinitionLocation>(scope, StringComparer.Ordinal);
    }

    private static bool ContainsPosition(TextSpan span, int position)
    {
        if (span.Start == span.End)
        {
            return position == span.Start;
        }

        return position >= span.Start && position < span.End;
    }

    private static DefinitionLocation? ResolveImportedDefinition(IReadOnlyList<ImportItem> imports, string name, string? currentFilePath)
    {
        DefinitionLocation? match = null;

        foreach (var import in imports)
        {
            var definition = ResolveModuleExportDefinition(import.ModuleName, name, currentFilePath);
            if (definition is null)
            {
                continue;
            }

            if (match is not null)
            {
                return null;
            }

            match = definition;
        }

        return match;
    }

    private static DefinitionLocation? ResolveQualifiedDefinition(
        string moduleName,
        string exportName,
        string? currentFilePath,
        IReadOnlyList<ImportItem> imports)
    {
        // Check whole-module alias matches first (selector imports rename a binding, not a module).
        foreach (var import in imports)
        {
            if (import.Selector is null && import.Alias is not null && string.Equals(import.Alias, moduleName, StringComparison.Ordinal))
            {
                return ResolveModuleExportDefinition(import.ModuleName, exportName, currentFilePath);
            }
        }

        var exactMatch = ResolveModuleExportDefinition(moduleName, exportName, currentFilePath);
        if (exactMatch is not null)
        {
            return exactMatch;
        }

        if (moduleName.Contains('.', StringComparison.Ordinal))
        {
            return null;
        }

        DefinitionLocation? shortQualifiedMatch = null;
        foreach (var import in imports)
        {
            var lastDot = import.ModuleName.LastIndexOf('.');
            if (lastDot < 0 || !string.Equals(import.ModuleName[(lastDot + 1)..], moduleName, StringComparison.Ordinal))
            {
                continue;
            }

            var definition = ResolveModuleExportDefinition(import.ModuleName, exportName, currentFilePath);
            if (definition is null)
            {
                continue;
            }

            if (shortQualifiedMatch is not null)
            {
                return null;
            }

            shortQualifiedMatch = definition;
        }

        return shortQualifiedMatch;
    }

    private static DefinitionLocation? ResolveModuleExportDefinition(string moduleName, string exportName, string? currentFilePath)
    {
        if (currentFilePath is null)
        {
            return null;
        }

        var projectPath = ProjectSupport.DiscoverProjectFile(Path.GetDirectoryName(Path.GetFullPath(currentFilePath)) ?? currentFilePath);
        if (projectPath is null)
        {
            return null;
        }

        try
        {
            var project = ProjectSupport.LoadProject(projectPath);
            var currentFullPath = Path.GetFullPath(currentFilePath);
            var pseudoProject = project with
            {
                EntryPath = currentFullPath,
                EntryModuleName = Path.GetFileNameWithoutExtension(currentFullPath)
            };

            var plan = ProjectSupport.BuildCompilationPlan(pseudoProject);
            var module = plan.OrderedModules.FirstOrDefault(x => string.Equals(x.ModuleName, moduleName, StringComparison.Ordinal));
            if (module is null || !File.Exists(module.FilePath))
            {
                return null;
            }

            return FindModuleDefinition(module, exportName);
        }
        catch (IOException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DefinitionLocation? FindModuleDefinition(ProjectModule module, string exportName)
    {
        var originalSource = SourceTextIndex.ReadUtf8File(module.FilePath);
        var header = StripImportHeader(originalSource);
        if (header.Diagnostics.Count > 0)
        {
            return null;
        }

        var diag = new Diagnostics();
        var program = new Parser(header.StrippedSource, diag).ParseProgram();
        if (diag.StructuredErrors.Count > 0)
        {
            return null;
        }

        if (!ExplicitInterfaceExportsName(program, exportName))
        {
            return null;
        }

        // A module's exports are its top-level let/type declarations; search those first.
        if (TryFindTopLevelExport(program, exportName, module.FilePath, out var topLevelDefinition))
        {
            return new DefinitionLocation(module.FilePath, TextSpan.FromBounds(topLevelDefinition.Span.Start + header.HeaderOffset, topLevelDefinition.Span.End + header.HeaderOffset));
        }

        // Fall back to the nested let ... in (pyramid) style for modules that still use it.
        Expr? body = program.Body;
        if (body is not null && TryFindBindingDefinition(body, exportName, module.FilePath, out var bindingDefinition))
        {
            return new DefinitionLocation(module.FilePath, TextSpan.FromBounds(bindingDefinition.Span.Start + header.HeaderOffset, bindingDefinition.Span.End + header.HeaderOffset));
        }

        if (body is not null && string.Equals(exportName, module.ModuleName, StringComparison.Ordinal))
        {
            var bodySpan = AstSpans.GetOrDefault(body);
            return new DefinitionLocation(module.FilePath, TextSpan.FromBounds(bodySpan.Start + header.HeaderOffset, bodySpan.End + header.HeaderOffset));
        }

        return null;
    }

    private static bool TryFindTopLevelExport(Frontend.Program program, string exportName, string? filePath, out DefinitionLocation definition)
    {
        foreach (var item in program.Items)
        {
            switch (item)
            {
                case TopLevelItem.LetDecl letDecl when string.Equals(letDecl.Name, exportName, StringComparison.Ordinal):
                    definition = new DefinitionLocation(filePath, AstSpans.GetOrDefault(letDecl.Value));
                    return true;

                case TopLevelItem.RecursiveGroup group:
                    foreach (var (name, value) in group.Bindings)
                    {
                        if (string.Equals(name, exportName, StringComparison.Ordinal))
                        {
                            definition = new DefinitionLocation(filePath, AstSpans.GetOrDefault(value));
                            return true;
                        }
                    }

                    break;

                case TopLevelItem.Type type when string.Equals(type.Decl.Name, exportName, StringComparison.Ordinal):
                    definition = new DefinitionLocation(filePath, AstSpans.GetOrDefault(type.Decl));
                    return true;

                case TopLevelItem.Type type:
                    TypeConstructor? constructor = type.Decl.Constructors.FirstOrDefault(candidate =>
                        string.Equals(candidate.Name, exportName, StringComparison.Ordinal));
                    if (constructor is not null)
                    {
                        definition = new DefinitionLocation(filePath, AstSpans.GetOrDefault(constructor));
                        return true;
                    }
                    break;

                case TopLevelItem.TypeAlias alias when string.Equals(alias.Decl.Name, exportName, StringComparison.Ordinal):
                    definition = new DefinitionLocation(filePath, AstSpans.GetOrDefault(alias.Decl));
                    return true;

                case TopLevelItem.ZeroCostType zeroCostType
                    when string.Equals(zeroCostType.Decl.Name, exportName, StringComparison.Ordinal):
                    definition = new DefinitionLocation(filePath, AstSpans.GetOrDefault(zeroCostType.Decl));
                    return true;

                case TopLevelItem.ZeroCostType zeroCostType
                    when string.Equals(zeroCostType.Decl.Constructor.Name, exportName, StringComparison.Ordinal):
                    definition = new DefinitionLocation(filePath, AstSpans.GetOrDefault(zeroCostType.Decl.Constructor));
                    return true;

                case TopLevelItem.Trait trait when string.Equals(trait.Decl.Name, exportName, StringComparison.Ordinal):
                    definition = new DefinitionLocation(filePath, AstSpans.GetOrDefault(trait.Decl));
                    return true;
            }
        }

        definition = default;
        return false;
    }

    private static IReadOnlyDictionary<string, TypeDecl> CollectLspTypeShapes(Frontend.Program program)
    {
        var types = new Dictionary<string, TypeDecl>(StringComparer.Ordinal);
        foreach (TopLevelItem item in program.Items)
        {
            switch (item)
            {
                case TopLevelItem.Type type:
                    types[type.Decl.Name] = type.Decl;
                    break;
                case TopLevelItem.TypeAlias alias:
                    types[alias.Decl.Name] = new TypeDecl(alias.Decl.Name, alias.Decl.TypeParameters, []);
                    break;
                case TopLevelItem.ZeroCostType zeroCostType:
                    types[zeroCostType.Decl.Name] = new TypeDecl(
                        zeroCostType.Decl.Name,
                        zeroCostType.Decl.TypeParameters,
                        [zeroCostType.Decl.Constructor]);
                    break;
            }
        }

        return types;
    }

    private static bool TryFindBindingDefinition(Expr expr, string name, string? filePath, out DefinitionLocation definition)
    {
        switch (expr)
        {
            case Expr.Let or Expr.LetResult or Expr.LetRecursive:
                return TryFindLetBindingDefinition(expr, name, filePath, out definition);

            case Expr.Lambda lambda:
                if (string.Equals(lambda.ParamName, name, StringComparison.Ordinal))
                {
                    definition = new DefinitionLocation(filePath, AstSpans.GetLambdaParameterOrDefault(lambda));
                    return true;
                }

                if (TryFindBindingDefinition(lambda.Body, name, filePath, out definition))
                {
                    return true;
                }

                break;

            case Expr.If ifExpr:
                if (TryFindBindingDefinition(ifExpr.Cond, name, filePath, out definition)
                    || TryFindBindingDefinition(ifExpr.Then, name, filePath, out definition)
                    || TryFindBindingDefinition(ifExpr.Else, name, filePath, out definition))
                {
                    return true;
                }

                break;

            case Expr.Call call:
                if (TryFindBindingDefinition(call.Func, name, filePath, out definition)
                    || TryFindBindingDefinition(call.Arg, name, filePath, out definition))
                {
                    return true;
                }

                break;

            case Expr.Match match:
                return TryFindMatchBindingDefinition(match, name, filePath, out definition);
        }

        definition = default;
        return false;
    }

    private static bool TryFindLetBindingDefinition(Expr expr, string name, string? filePath, out DefinitionLocation definition)
    {
        switch (expr)
        {
            case Expr.Let letExpr:
                if (string.Equals(letExpr.Name, name, StringComparison.Ordinal))
                {
                    definition = new DefinitionLocation(filePath, AstSpans.GetLetNameOrDefault(letExpr));
                    return true;
                }

                if (TryFindBindingDefinition(letExpr.Value, name, filePath, out definition)
                    || TryFindBindingDefinition(letExpr.Body, name, filePath, out definition))
                {
                    return true;
                }

                break;

            case Expr.LetResult letResultExpr:
                if (string.Equals(letResultExpr.Name, name, StringComparison.Ordinal))
                {
                    definition = new DefinitionLocation(filePath, AstSpans.GetLetResultNameOrDefault(letResultExpr));
                    return true;
                }

                if (TryFindBindingDefinition(letResultExpr.Value, name, filePath, out definition)
                    || TryFindBindingDefinition(letResultExpr.Body, name, filePath, out definition))
                {
                    return true;
                }

                break;

            case Expr.LetRecursive letRecursiveExpr:
                if (string.Equals(letRecursiveExpr.Name, name, StringComparison.Ordinal))
                {
                    definition = new DefinitionLocation(filePath, AstSpans.GetLetRecursiveNameOrDefault(letRecursiveExpr));
                    return true;
                }

                if (TryFindBindingDefinition(letRecursiveExpr.Value, name, filePath, out definition)
                    || TryFindBindingDefinition(letRecursiveExpr.Body, name, filePath, out definition))
                {
                    return true;
                }

                break;
        }

        definition = default;
        return false;
    }

    private static bool TryFindMatchBindingDefinition(Expr.Match match, string name, string? filePath, out DefinitionLocation definition)
    {
        if (TryFindBindingDefinition(match.Value, name, filePath, out definition))
        {
            return true;
        }

        foreach (var matchCase in match.Cases)
        {
            if (TryFindPatternBindingDefinition(matchCase.Pattern, name, filePath, out definition)
                || (matchCase.Guard is not null && TryFindBindingDefinition(matchCase.Guard, name, filePath, out definition))
                || TryFindBindingDefinition(matchCase.Body, name, filePath, out definition))
            {
                return true;
            }
        }

        definition = default;
        return false;
    }

    private static bool TryFindPatternBindingDefinition(Pattern pattern, string name, string? filePath, out DefinitionLocation definition)
    {
        if (pattern is Pattern.Var varPattern
            && IsPatternVariable(varPattern)
            && string.Equals(varPattern.Name, name, StringComparison.Ordinal))
        {
            definition = new DefinitionLocation(filePath, AstSpans.GetOrDefault(varPattern));
            return true;
        }

        if (pattern is Pattern.As asPattern && string.Equals(asPattern.Name, name, StringComparison.Ordinal))
        {
            definition = new DefinitionLocation(filePath, AstSpans.GetAsPatternNameOrDefault(asPattern));
            return true;
        }

        IEnumerable<Pattern> children = pattern switch
        {
            Pattern.Cons cons => [cons.Head, cons.Tail],
            Pattern.Tuple tuple => tuple.Elements,
            Pattern.Constructor constructor => constructor.Patterns,
            Pattern.Record record => record.Fields.Select(field => field.Pattern),
            Pattern.As nestedAs => [nestedAs.Inner],
            Pattern.Or orPattern => orPattern.Alternatives,
            _ => [],
        };
        foreach (Pattern child in children)
        {
            if (TryFindPatternBindingDefinition(child, name, filePath, out definition))
            {
                return true;
            }
        }

        definition = default;
        return false;
    }
}
