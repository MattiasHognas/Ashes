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

    /// <summary>One semantic-highlighting token, positioned by zero-based <paramref name="Line"/> and
    /// <paramref name="Character"/> and classified by <paramref name="TokenType"/>.</summary>
    /// <param name="Line">Zero-based line of the token.</param>
    /// <param name="Character">Zero-based start column of the token.</param>
    /// <param name="Length">Length of the token in characters.</param>
    /// <param name="TokenType">Token-type index into <see cref="SemanticTokenTypes"/>.</param>
    /// <param name="TokenModifiers">Bitset of token modifiers applied to the token.</param>
    public readonly record struct SemanticTokenItem(int Line, int Character, int Length, int TokenType, int TokenModifiers);

    private readonly record struct ImportItem(TextSpan Span, string ModuleName, string? Selector, string? Alias)
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

    /// <summary>The semantic-token type legend, in the index order the <c>TokenType*</c> constants
    /// reference and the client is registered with.</summary>
    public static IReadOnlyList<string> SemanticTokenTypes { get; } = ["type", "typeParameter", "enumMember"];

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
                imports.Add(new ImportItem(TextSpan.FromBounds(lineStart, lineStart + lineContent.Length), match.Groups[1].Value, selector, alias));
                headerLines.Add(new HeaderLineItem(lineContent, match.Groups[1].Value, selector, alias));
                pos = nextPos;
                continue;
            }

            if (trimmed.StartsWith("import ", StringComparison.Ordinal))
            {
                diagnostics.Add(new DiagnosticItem(
                    lineStart,
                    lineStart + lineContent.Length,
                    "Invalid import syntax. Expected 'import Foo' or 'import Foo.Bar' or 'import Foo.Bar as Alias'.",
                    DiagnosticCodes.ParseError));
                return new ImportHeaderInfo(source[nextPos..], nextPos, headerLines, imports, diagnostics);
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

        return new ImportHeaderInfo(source[pos..], pos, headerLines, imports, diagnostics);
    }

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
            layout.EntryOffset,
            layout.BodyStart,
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
            && (header.Imports.Count > 0 || ProjectSupport.ContainsInlineModule(header.StrippedSource)))
        {
            var layout = ProjectSupport.BuildStandaloneCompilationLayout(
                header.StrippedSource,
                header.Imports.Select(x => x.ModuleName).ToArray());
            analysisSource = layout.Source;
            entryOffset = layout.EntryOffset;
            bodyStart = layout.BodyStart;
            constructorModules = layout.ConstructorModules;
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

    private static IReadOnlySet<string>? BuildImportedStdModules(IReadOnlyList<ImportItem> imports)
    {
        var importedStdModules = imports
            .Select(x => x.ModuleName)
            .Where(ProjectSupport.IsStdModule)
            .ToHashSet(StringComparer.Ordinal);

        return importedStdModules.Count == 0 ? null : importedStdModules;
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
            .Select(d => (Diagnostic: d, MappedSpan: MapToOriginalSpan(d.Start, d.End, context.EntryOffset, context.BodyStart, context.StrippedSource.Length)))
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
        var ctorNames = lowering.ConstructorSymbols.Keys.ToHashSet(StringComparer.Ordinal);
        // Collect unique type-parameter names used in constructor parameter lists
        var typeParamNames = program.TypeDecls
            .SelectMany(d => d.TypeParameters.Select(tp => tp.Name)
                .Concat(d.Constructors.SelectMany(c => c.Parameters).SelectMany(fieldType => fieldType.MentionedNames())))
            .ToHashSet(StringComparer.Ordinal);

        return ScanSemanticTokens(source, context.StrippedSource, context.HeaderOffset, typeNames, ctorNames, typeParamNames);
    }

    private static List<SemanticTokenItem> ScanSemanticTokens(
        string source,
        string strippedSource,
        int headerOffset,
        HashSet<string> typeNames,
        HashSet<string> ctorNames,
        HashSet<string> typeParamNames)
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
            if (typeNames.Contains(tok.Text))
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
            var (line, character) = LspTextUtils.ToLineCharacter(originalLineStarts, source.Length, originalPos);
            tokens.Add(new SemanticTokenItem(line, character, tok.Text.Length, tokenType, 0));
        }

        return tokens;
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
        if (position is not null && TryGetModuleCompletions(source, position.Value, header.Imports, out var moduleCompletions))
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
                if (strippedPosition >= 0 && strippedPosition <= header.StrippedSource.Length)
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
            case Expr.StrLit:
            case Expr.BoolLit:
            case Expr.Var:
            case Expr.QualifiedVar:
                return scope.Keys.ToArray();

            case Expr.BitwiseNot bitwiseNot:
                return CollectVisibleBindingsInExpr(bitwiseNot.Operand, position, scope);

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

    private static bool TryGetModuleCompletions(string source, int position, IReadOnlyList<ImportItem> imports, out IReadOnlyList<string> completions)
    {
        completions = Array.Empty<string>();

        if (position < 0 || position > source.Length)
        {
            return false;
        }

        var prefix = ExtractCompletionPrefix(source, position);
        if (string.IsNullOrEmpty(prefix) || !prefix.EndsWith(".", StringComparison.Ordinal))
        {
            return false;
        }

        var qualifier = prefix[..^1];
        var moduleName = ResolveCompletionModuleName(qualifier, imports);
        if (moduleName is null)
        {
            return false;
        }

        completions = GetModuleCompletionItems(moduleName);
        return completions.Count > 0;
    }

    private static string ExtractCompletionPrefix(string source, int position)
    {
        var start = position;
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

        return source[start..position];
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
            if (string.Equals(name, "Ashes.Internal", StringComparison.Ordinal))
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

    /// <summary>
    /// Returns the hover for the token at <paramref name="position"/> in <paramref name="source"/> — the
    /// inferred type, prefixed with the name when available — or null when nothing resolves there.
    /// <paramref name="filePath"/> supplies project context when present.
    /// </summary>
    public static HoverItem? GetHover(string source, int position, string? filePath = null)
    {
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

        var hover = lowering.GetTypeAtPosition(analysisPosition.Value);
        if (hover is null)
        {
            return null;
        }

        var mappedSpan = MapToOriginalSpan(
            hover.Value.Span.Start,
            hover.Value.Span.End,
            context.EntryOffset,
            context.BodyStart,
            context.StrippedSource.Length);
        if (mappedSpan is null)
        {
            return null;
        }

        var displayText = string.IsNullOrEmpty(hover.Value.Name)
            ? lowering.FormatType(hover.Value.Type)
            : $"{hover.Value.Name} : {lowering.FormatType(hover.Value.Type)}";

        return new HoverItem(
            mappedSpan.Value.Start + context.HeaderOffset,
            mappedSpan.Value.End + context.HeaderOffset,
            displayText);
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
        if (strippedPosition < 0 || strippedPosition > header.StrippedSource.Length)
        {
            return null;
        }

        var diag = new Diagnostics();
        var program = new Parser(header.StrippedSource, diag).ParseProgram();
        if (diag.StructuredErrors.Count > 0)
        {
            return null;
        }

        var definition = ResolveDefinitionInProgram(program, strippedPosition, filePath, header.Imports);
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
    /// Maps a position in the combined source back to a position within strippedSource, or returns
    /// null if the position falls in the imported-module portion (which should be filtered out).
    /// <list type="bullet">
    ///   <item>When <paramref name="entryOffset"/> is 0 (standalone analysis), all positions are included
    ///     and returned unchanged.</item>
    ///   <item>When type declarations are hoisted (<paramref name="bodyStart"/> &gt; 0), positions in
    ///     [0, bodyStart) map directly (same text), and positions in [entryOffset, …) map to
    ///     bodyStart + (p - entryOffset).</item>
    ///   <item>Without hoisting (<paramref name="bodyStart"/> == 0), positions in [entryOffset, …) map
    ///     to p - entryOffset.</item>
    /// </list>
    /// </summary>
    private static TextSpan? MapToOriginalSpan(int start, int end, int entryOffset, int bodyStart, int strippedLength)
    {
        if (entryOffset == 0)
        {
            return TextSpan.FromBounds(start, end);
        }

        if (bodyStart > 0 && start <= bodyStart && end <= bodyStart)
        {
            return TextSpan.FromBounds(start, end);
        }

        var bodyLength = strippedLength - bodyStart;
        var bodyEnd = entryOffset + bodyLength;
        if (start >= entryOffset && end <= bodyEnd)
        {
            return TextSpan.FromBounds(
                bodyStart + (start - entryOffset),
                bodyStart + (end - entryOffset));
        }

        if (start == bodyEnd && end == bodyEnd)
        {
            return TextSpan.FromBounds(strippedLength, strippedLength);
        }

        return null;
    }

    private static int? MapOriginalPositionToAnalysis(int position, AnalysisContext context)
    {
        var strippedPosition = position - context.HeaderOffset;
        if (strippedPosition < 0 || strippedPosition > context.StrippedSource.Length)
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

        return context.EntryOffset + (strippedPosition - context.BodyStart);
    }

    private static DefinitionLocation? ResolveDefinitionInProgram(
        Frontend.Program program,
        int position,
        string? currentFilePath,
        IReadOnlyList<ImportItem> imports)
    {
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
            case Expr.StrLit:
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
        var originalSource = File.ReadAllText(module.FilePath);
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
            }
        }

        definition = default;
        return false;
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
        switch (pattern)
        {
            case Pattern.Var varPattern when IsPatternVariable(varPattern) && string.Equals(varPattern.Name, name, StringComparison.Ordinal):
                definition = new DefinitionLocation(filePath, AstSpans.GetOrDefault(varPattern));
                return true;

            case Pattern.Cons cons:
                if (TryFindPatternBindingDefinition(cons.Head, name, filePath, out definition)
                    || TryFindPatternBindingDefinition(cons.Tail, name, filePath, out definition))
                {
                    return true;
                }

                break;

            case Pattern.Tuple tuple:
                foreach (var element in tuple.Elements)
                {
                    if (TryFindPatternBindingDefinition(element, name, filePath, out definition))
                    {
                        return true;
                    }
                }

                break;

            case Pattern.Constructor ctor:
                foreach (var element in ctor.Patterns)
                {
                    if (TryFindPatternBindingDefinition(element, name, filePath, out definition))
                    {
                        return true;
                    }
                }

                break;
        }

        definition = default;
        return false;
    }
}
