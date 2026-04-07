using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ashes.Frontend;
using Ashes.Semantics;

namespace Ashes.Lsp;

public static partial class DocumentService
{
    public readonly record struct DiagnosticItem(int Start, int End, string Message, string? Code = null)
    {
        public int Position => Start;

        public TextSpan Span => TextSpan.FromBounds(Start, End);
    }

    public readonly record struct HoverItem(int Start, int End, string Contents)
    {
        public TextSpan Span => TextSpan.FromBounds(Start, End);
    }

    public readonly record struct DefinitionItem(string? FilePath, int Start, int End)
    {
        public TextSpan Span => TextSpan.FromBounds(Start, End);
    }

    public readonly record struct SemanticTokenItem(int Line, int Character, int Length, int TokenType, int TokenModifiers);

    private readonly record struct ImportItem(TextSpan Span, string ModuleName, string? Alias);

    private readonly record struct HeaderLineItem(string Text, string? ModuleName, string? Alias);

    private readonly record struct LineAnchor(string Signature, int Occurrence);

    private readonly record struct SignificantLine(int Index, LineAnchor Anchor);

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
        IReadOnlyList<DiagnosticItem> Diagnostics);

    private readonly record struct ProjectAnalysisContext(
        string CombinedSource,
        int EntryOffset,
        int BodyStart,
        IReadOnlySet<string> ImportedStdModules);

    private readonly record struct DefinitionLocation(string? FilePath, TextSpan Span);

    // Token type indices matching SemanticTokenTypes legend order
    public const int TokenTypeType = 0;
    public const int TokenTypeTypeParameter = 1;
    public const int TokenTypeEnumMember = 2;

    public static IReadOnlyList<string> SemanticTokenTypes { get; } = ["type", "typeParameter", "enumMember"];

    [GeneratedRegex(@"'([^']+)'", RegexOptions.Compiled)]
    private static partial Regex QuotedValueRegex();

    private static readonly Regex ImportLineRegex = new(
        ProjectSupport.ImportModulePattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
            int nlIdx = source.IndexOf('\n', pos);
            int lineEnd = nlIdx < 0 ? source.Length : nlIdx;
            int nextPos = nlIdx < 0 ? source.Length : nlIdx + 1;

            // Line content without the newline (and without trailing \r)
            var lineContent = source[lineStart..lineEnd];
            if (lineContent.EndsWith('\r'))
            {
                lineContent = lineContent[..^1];
            }

            var trimmed = lineContent.TrimStart();

            var match = ImportLineRegex.Match(lineContent);
            if (match.Success)
            {
                var alias = match.Groups[2].Success ? match.Groups[2].Value : null;
                imports.Add(new ImportItem(TextSpan.FromBounds(lineStart, lineStart + lineContent.Length), match.Groups[1].Value, alias));
                headerLines.Add(new HeaderLineItem(lineContent, match.Groups[1].Value, alias));
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
                headerLines.Add(new HeaderLineItem(lineContent, null, null));
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

    private static string ReinsertStandaloneCommentLines(string originalSource, string formattedSource, string lineEnding)
    {
        var originalLines = SplitLines(originalSource);
        if (originalLines.Count == 0)
        {
            return formattedSource;
        }

        var commentInsertions = CollectStandaloneCommentInsertions(originalLines);
        if (commentInsertions.Count == 0)
        {
            return formattedSource;
        }

        var formattedLines = SplitLines(formattedSource);
        var formattedSignificantLines = CollectSignificantLines(formattedLines);
        var formattedAnchorIndices = BuildAnchorIndexMap(formattedSignificantLines);
        var insertionsByPosition = new Dictionary<int, List<string>>();

        foreach (var insertion in commentInsertions)
        {
            var position = ResolveInsertionPosition(insertion.PreviousAnchor, insertion.NextAnchor, formattedAnchorIndices, formattedLines.Count);
            if (!insertionsByPosition.TryGetValue(position, out var linesAtPosition))
            {
                linesAtPosition = [];
                insertionsByPosition[position] = linesAtPosition;
            }

            linesAtPosition.Add(insertion.Text);
        }

        var mergedLines = new List<string>(formattedLines.Count + commentInsertions.Count);
        for (var i = 0; i < formattedLines.Count; i++)
        {
            if (insertionsByPosition.TryGetValue(i, out var linesBefore))
            {
                mergedLines.AddRange(linesBefore);
            }

            mergedLines.Add(formattedLines[i]);
        }

        if (insertionsByPosition.TryGetValue(formattedLines.Count, out var trailingLines))
        {
            mergedLines.AddRange(trailingLines);
        }

        var endsWithNewline = formattedSource.EndsWith("\n", StringComparison.Ordinal);
        var merged = string.Join(lineEnding, mergedLines);
        return endsWithNewline ? merged + lineEnding : merged;
    }

    private static IReadOnlyList<string> SplitLines(string source)
    {
        var lines = new List<string>();
        using var reader = new StringReader(source);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lines.Add(line);
        }

        return lines;
    }

    private static IReadOnlyList<SignificantLine> CollectSignificantLines(IReadOnlyList<string> lines)
    {
        var significantLines = new List<SignificantLine>();
        var occurrenceCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < lines.Count; i++)
        {
            if (IsStandaloneCommentLine(lines[i]))
            {
                continue;
            }

            var signature = GetLineSignature(lines[i]);
            if (signature.Length == 0)
            {
                continue;
            }

            occurrenceCounts.TryGetValue(signature, out var count);
            count++;
            occurrenceCounts[signature] = count;
            significantLines.Add(new SignificantLine(i, new LineAnchor(signature, count)));
        }

        return significantLines;
    }

    private static Dictionary<string, List<int>> BuildAnchorIndexMap(IReadOnlyList<SignificantLine> lines)
    {
        var map = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (!map.TryGetValue(line.Anchor.Signature, out var indices))
            {
                indices = [];
                map[line.Anchor.Signature] = indices;
            }

            indices.Add(line.Index);
        }

        return map;
    }

    private static List<(string Text, LineAnchor? PreviousAnchor, LineAnchor? NextAnchor)> CollectStandaloneCommentInsertions(IReadOnlyList<string> originalLines)
    {
        var significantLines = CollectSignificantLines(originalLines);
        var insertions = new List<(string Text, LineAnchor? PreviousAnchor, LineAnchor? NextAnchor)>();
        var significantIndex = 0;

        for (var i = 0; i < originalLines.Count; i++)
        {
            if (!IsStandaloneCommentLine(originalLines[i]))
            {
                if (significantIndex < significantLines.Count && significantLines[significantIndex].Index == i)
                {
                    significantIndex++;
                }

                continue;
            }

            LineAnchor? previousAnchor = significantIndex > 0 ? significantLines[significantIndex - 1].Anchor : null;
            LineAnchor? nextAnchor = significantIndex < significantLines.Count ? significantLines[significantIndex].Anchor : null;
            insertions.Add((originalLines[i], previousAnchor, nextAnchor));
        }

        return insertions;
    }

    private static int ResolveInsertionPosition(
        LineAnchor? previousAnchor,
        LineAnchor? nextAnchor,
        IReadOnlyDictionary<string, List<int>> formattedAnchorIndices,
        int formattedLineCount)
    {
        if (nextAnchor is not null && TryFindAnchorIndex(nextAnchor.Value, formattedAnchorIndices, out var nextIndex))
        {
            return nextIndex;
        }

        if (previousAnchor is not null && TryFindAnchorIndex(previousAnchor.Value, formattedAnchorIndices, out var previousIndex))
        {
            return previousIndex + 1;
        }

        return 0;
    }

    private static bool TryFindAnchorIndex(LineAnchor anchor, IReadOnlyDictionary<string, List<int>> formattedAnchorIndices, out int index)
    {
        if (formattedAnchorIndices.TryGetValue(anchor.Signature, out var indices)
            && anchor.Occurrence > 0
            && anchor.Occurrence <= indices.Count)
        {
            index = indices[anchor.Occurrence - 1];
            return true;
        }

        index = -1;
        return false;
    }

    private static bool IsStandaloneCommentLine(string line)
    {
        return line.TrimStart().StartsWith("//", StringComparison.Ordinal);
    }

    private static string GetLineSignature(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return string.Empty;
        }

        var diag = new Diagnostics();
        var lexer = new Lexer(line, diag);
        var sb = new StringBuilder();
        while (true)
        {
            var token = lexer.Next();
            if (token.Kind is TokenKind.EOF or TokenKind.Bad)
            {
                break;
            }

            if (sb.Length > 0)
            {
                sb.Append('|');
            }

            sb.Append((int)token.Kind);
            sb.Append(':');
            sb.Append(token.Text);
        }

        return sb.ToString();
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

        return new ProjectAnalysisContext(layout.Source, layout.EntryOffset, layout.BodyStart, plan.ImportedStdModules);
    }

    private static AnalysisContext PrepareAnalysisContext(string source, string? filePath)
    {
        var header = StripImportHeader(source);
        if (header.Diagnostics.Count > 0)
        {
            return new AnalysisContext(header.StrippedSource, header.StrippedSource, header.HeaderOffset, 0, 0, null, null, header.Diagnostics);
        }

        if (filePath is not null)
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
                        null,
                        []);
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
        }

        var standaloneImportDiagnostics = ValidateStandaloneImports(header.Imports);
        var importedStdModules = standaloneImportDiagnostics.Count == 0
            ? BuildImportedStdModules(header.Imports)
            : null;
        var analysisSource = header.StrippedSource;
        var entryOffset = 0;
        var bodyStart = 0;

        if (standaloneImportDiagnostics.Count == 0 && header.Imports.Count > 0)
        {
            var layout = ProjectSupport.BuildStandaloneCompilationLayout(
                header.StrippedSource,
                header.Imports.Select(x => x.ModuleName).ToArray());
            analysisSource = layout.Source;
            entryOffset = layout.EntryOffset;
            bodyStart = layout.BodyStart;
        }

        return new AnalysisContext(
            header.StrippedSource,
            analysisSource,
            header.HeaderOffset,
            entryOffset,
            bodyStart,
            importedStdModules,
            BuildModuleAliases(header.Imports),
            standaloneImportDiagnostics);
    }

    private static IReadOnlyDictionary<string, string>? BuildModuleAliases(IReadOnlyList<ImportItem> imports)
    {
        Dictionary<string, string>? aliases = null;
        foreach (var import in imports)
        {
            if (import.Alias is not null)
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

    public static IReadOnlyList<DiagnosticItem> Analyze(string source, string? filePath = null)
    {
        var context = PrepareAnalysisContext(source, filePath);
        if (context.Diagnostics.Count > 0)
        {
            return context.Diagnostics;
        }

        var diag = new Diagnostics();
        var program = new Parser(context.AnalysisSource, diag).ParseProgram();
        _ = new Lowering(diag, context.ImportedStdModules, context.ModuleAliases).Lower(program);

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

        formattedBody = ReinsertStandaloneCommentLines(strippedSource, formattedBody, formattingOptions.NewLine);

        if (header.HeaderLines.Count == 0)
        {
            return formattedBody;
        }

        var headerLines = string.Join(
            formattingOptions.NewLine,
            header.HeaderLines.Select(line => line.ModuleName is null
                ? line.Text
                : line.Alias is not null
                    ? $"import {line.ModuleName} as {line.Alias}"
                    : $"import {line.ModuleName}"));

        return headerLines + formattingOptions.NewLine + formattedBody;
    }

    public static IReadOnlyList<SemanticTokenItem> GetSemanticTokens(string source, string? filePath = null)
    {
        var context = PrepareAnalysisContext(source, filePath);
        if (context.Diagnostics.Count > 0)
        {
            return Array.Empty<SemanticTokenItem>();
        }

        var strippedSource = context.StrippedSource;
        var headerOffset = context.HeaderOffset;

        var diag = new Diagnostics();
        var program = new Parser(context.AnalysisSource, diag).ParseProgram();
        var lowering = new Lowering(diag, context.ImportedStdModules, context.ModuleAliases);
        lowering.Lower(program);

        var typeNames = lowering.TypeSymbols.Keys.ToHashSet(StringComparer.Ordinal);
        var ctorNames = lowering.ConstructorSymbols.Keys.ToHashSet(StringComparer.Ordinal);
        // Collect unique type-parameter names used in constructor parameter lists
        var typeParamNames = program.TypeDecls
            .SelectMany(d => d.TypeParameters.Select(tp => tp.Name)
                .Concat(d.Constructors.SelectMany(c => c.Parameters)))
            .ToHashSet(StringComparer.Ordinal);

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

    public static IReadOnlyList<string> GetCompletions(string source, string? filePath = null)
    {
        return GetCompletions(source, position: null, filePath);
    }

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
        var lowering = new Lowering(diag, context.ImportedStdModules, context.ModuleAliases);
        lowering.Lower(program);

        var completionNames = new HashSet<string>(lowering.ConstructorSymbols.Keys, StringComparer.Ordinal);

        if (position is not null)
        {
            var strippedPosition = position.Value - header.HeaderOffset;
            if (strippedPosition >= 0 && strippedPosition <= header.StrippedSource.Length)
            {
                var strippedDiag = new Diagnostics();
                var strippedProgram = new Parser(header.StrippedSource, strippedDiag).ParseProgram();
                if (strippedDiag.StructuredErrors.Count == 0)
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

    private static IReadOnlyCollection<string> CollectVisibleBindingsInProgram(Frontend.Program program, int position)
    {
        return CollectVisibleBindingsInExpr(program.Body, position, new Dictionary<string, byte>(StringComparer.Ordinal));
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

        switch (expr)
        {
            case Expr.IntLit:
            case Expr.FloatLit:
            case Expr.StrLit:
            case Expr.BoolLit:
            case Expr.Var:
            case Expr.QualifiedVar:
                return scope.Keys.ToArray();

            case Expr.Add add:
                return CollectVisibleBindingsInBinary(add.Left, add.Right, position, scope);

            case Expr.Subtract sub:
                return CollectVisibleBindingsInBinary(sub.Left, sub.Right, position, scope);

            case Expr.Multiply mul:
                return CollectVisibleBindingsInBinary(mul.Left, mul.Right, position, scope);

            case Expr.Divide div:
                return CollectVisibleBindingsInBinary(div.Left, div.Right, position, scope);

            case Expr.GreaterOrEqual ge:
                return CollectVisibleBindingsInBinary(ge.Left, ge.Right, position, scope);

            case Expr.LessOrEqual le:
                return CollectVisibleBindingsInBinary(le.Left, le.Right, position, scope);

            case Expr.Equal eq:
                return CollectVisibleBindingsInBinary(eq.Left, eq.Right, position, scope);

            case Expr.NotEqual ne:
                return CollectVisibleBindingsInBinary(ne.Left, ne.Right, position, scope);

            case Expr.ResultPipe pipe:
                return CollectVisibleBindingsInBinary(pipe.Left, pipe.Right, position, scope);

            case Expr.ResultMapErrorPipe pipe:
                return CollectVisibleBindingsInBinary(pipe.Left, pipe.Right, position, scope);

            case Expr.Let letExpr:
                {
                    var inValue = CollectVisibleBindingsInExpr(letExpr.Value, position, scope);
                    if (inValue.Count > 0)
                    {
                        return inValue;
                    }

                    var bodyScope = CloneCompletionScope(scope);
                    bodyScope[letExpr.Name] = 0;
                    return CollectVisibleBindingsInExpr(letExpr.Body, position, bodyScope);
                }

            case Expr.LetResult letResultExpr:
                {
                    var inValue = CollectVisibleBindingsInExpr(letResultExpr.Value, position, scope);
                    if (inValue.Count > 0)
                    {
                        return inValue;
                    }

                    var bodyScope = CloneCompletionScope(scope);
                    bodyScope[letResultExpr.Name] = 0;
                    return CollectVisibleBindingsInExpr(letResultExpr.Body, position, bodyScope);
                }

            case Expr.LetRec letRecExpr:
                {
                    var recursiveScope = CloneCompletionScope(scope);
                    recursiveScope[letRecExpr.Name] = 0;

                    var inValue = CollectVisibleBindingsInExpr(letRecExpr.Value, position, recursiveScope);
                    if (inValue.Count > 0)
                    {
                        return inValue;
                    }

                    return CollectVisibleBindingsInExpr(letRecExpr.Body, position, recursiveScope);
                }

            case Expr.If ifExpr:
                return CollectVisibleBindingsInExpr(ifExpr.Cond, position, scope)
                    .Concat(CollectVisibleBindingsInExpr(ifExpr.Then, position, scope))
                    .Concat(CollectVisibleBindingsInExpr(ifExpr.Else, position, scope))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

            case Expr.Lambda lambda:
                {
                    var lambdaScope = CloneCompletionScope(scope);
                    lambdaScope[lambda.ParamName] = 0;
                    return CollectVisibleBindingsInExpr(lambda.Body, position, lambdaScope);
                }

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

                        var inBody = CollectVisibleBindingsInExpr(matchCase.Body, position, caseScope);
                        if (inBody.Count > 0)
                        {
                            return inBody;
                        }
                    }

                    return Array.Empty<string>();
                }

            default:
                return Array.Empty<string>();
        }
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
        // Check alias matches first
        foreach (var import in imports)
        {
            if (import.Alias is not null && string.Equals(import.Alias, qualifier, StringComparison.Ordinal))
            {
                return import.ModuleName;
            }
        }

        if (qualifier == "Ashes")
        {
            return "Ashes";
        }

        if (qualifier == "Ashes.Net")
        {
            return qualifier;
        }

        if (BuiltinRegistry.TryGetModule(qualifier, out _))
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

    private static IReadOnlyList<string> GetModuleCompletionItems(string moduleName)
    {
        return moduleName switch
        {
            "Ashes" => ["File", "Http", "IO", "List", "Maybe", "Net", "Result", "Test"],
            "Ashes.Net" => ["Tcp"],
            "Ashes.List" => ["append", "filter", "fold", "foldLeft", "head", "isEmpty", "length", "map", "reverse", "tail"],
            "Ashes.Maybe" => ["default", "flatMap", "getOrElse", "isNone", "isSome", "map", "unwrapOr"],
            "Ashes.Result" => ["bind", "default", "flatMap", "getOrElse", "isError", "isOk", "map", "mapError"],
            "Ashes.Test" => ["assertEqual", "fail"],
            _ when BuiltinRegistry.TryGetModule(moduleName, out var module) => module.Members.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            _ => Array.Empty<string>()
        };
    }

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
        var lowering = new Lowering(diag, context.ImportedStdModules, context.ModuleAliases);
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
        return ResolveDefinitionInExpr(program.Body, position, currentFilePath, imports, new Dictionary<string, DefinitionLocation>(StringComparer.Ordinal));
    }

    private static DefinitionLocation? ResolveDefinitionInExpr(
        Expr expr,
        int position,
        string? currentFilePath,
        IReadOnlyList<ImportItem> imports,
        IReadOnlyDictionary<string, DefinitionLocation> scope)
    {
        switch (expr)
        {
            case Expr.IntLit:
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

            case Expr.Add add:
                return ResolveDefinitionInBinary(add.Left, add.Right, position, currentFilePath, imports, scope);

            case Expr.Subtract sub:
                return ResolveDefinitionInBinary(sub.Left, sub.Right, position, currentFilePath, imports, scope);

            case Expr.Multiply mul:
                return ResolveDefinitionInBinary(mul.Left, mul.Right, position, currentFilePath, imports, scope);

            case Expr.Divide div:
                return ResolveDefinitionInBinary(div.Left, div.Right, position, currentFilePath, imports, scope);

            case Expr.GreaterOrEqual ge:
                return ResolveDefinitionInBinary(ge.Left, ge.Right, position, currentFilePath, imports, scope);

            case Expr.LessOrEqual le:
                return ResolveDefinitionInBinary(le.Left, le.Right, position, currentFilePath, imports, scope);

            case Expr.Equal eq:
                return ResolveDefinitionInBinary(eq.Left, eq.Right, position, currentFilePath, imports, scope);

            case Expr.NotEqual ne:
                return ResolveDefinitionInBinary(ne.Left, ne.Right, position, currentFilePath, imports, scope);

            case Expr.ResultPipe pipe:
                return ResolveDefinitionInBinary(pipe.Left, pipe.Right, position, currentFilePath, imports, scope);

            case Expr.ResultMapErrorPipe pipe:
                return ResolveDefinitionInBinary(pipe.Left, pipe.Right, position, currentFilePath, imports, scope);

            case Expr.Let letExpr:
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

            case Expr.LetResult letResultExpr:
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

            case Expr.LetRec letRecExpr:
                {
                    var bindingDefinition = new DefinitionLocation(currentFilePath, AstSpans.GetLetRecNameOrDefault(letRecExpr));
                    if (ContainsPosition(bindingDefinition.Span, position))
                    {
                        return bindingDefinition;
                    }

                    var recursiveScope = CloneScope(scope);
                    recursiveScope[letRecExpr.Name] = bindingDefinition;

                    var inValue = ResolveDefinitionInExpr(letRecExpr.Value, position, currentFilePath, imports, recursiveScope);
                    if (inValue is not null)
                    {
                        return inValue;
                    }

                    return ResolveDefinitionInExpr(letRecExpr.Body, position, currentFilePath, imports, recursiveScope);
                }

            case Expr.If ifExpr:
                return ResolveDefinitionInExpr(ifExpr.Cond, position, currentFilePath, imports, scope)
                    ?? ResolveDefinitionInExpr(ifExpr.Then, position, currentFilePath, imports, scope)
                    ?? ResolveDefinitionInExpr(ifExpr.Else, position, currentFilePath, imports, scope);

            case Expr.Lambda lambda:
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

                        var inBody = ResolveDefinitionInExpr(matchCase.Body, position, currentFilePath, imports, caseScope);
                        if (inBody is not null)
                        {
                            return inBody;
                        }
                    }

                    return null;
                }

            default:
                return null;
        }
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
        // Check alias matches first
        foreach (var import in imports)
        {
            if (import.Alias is not null && string.Equals(import.Alias, moduleName, StringComparison.Ordinal))
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

        if (TryFindBindingDefinition(program.Body, exportName, module.FilePath, out var bindingDefinition))
        {
            return new DefinitionLocation(module.FilePath, TextSpan.FromBounds(bindingDefinition.Span.Start + header.HeaderOffset, bindingDefinition.Span.End + header.HeaderOffset));
        }

        if (string.Equals(exportName, module.ModuleName, StringComparison.Ordinal))
        {
            var bodySpan = AstSpans.GetOrDefault(program.Body);
            return new DefinitionLocation(module.FilePath, TextSpan.FromBounds(bodySpan.Start + header.HeaderOffset, bodySpan.End + header.HeaderOffset));
        }

        return null;
    }

    private static bool TryFindBindingDefinition(Expr expr, string name, string? filePath, out DefinitionLocation definition)
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

            case Expr.LetRec letRecExpr:
                if (string.Equals(letRecExpr.Name, name, StringComparison.Ordinal))
                {
                    definition = new DefinitionLocation(filePath, AstSpans.GetLetRecNameOrDefault(letRecExpr));
                    return true;
                }

                if (TryFindBindingDefinition(letRecExpr.Value, name, filePath, out definition)
                    || TryFindBindingDefinition(letRecExpr.Body, name, filePath, out definition))
                {
                    return true;
                }

                break;

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
                if (TryFindBindingDefinition(match.Value, name, filePath, out definition))
                {
                    return true;
                }

                foreach (var matchCase in match.Cases)
                {
                    if (TryFindPatternBindingDefinition(matchCase.Pattern, name, filePath, out definition)
                        || TryFindBindingDefinition(matchCase.Body, name, filePath, out definition))
                    {
                        return true;
                    }
                }

                break;
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
