namespace Ashes.Frontend;

public sealed class Parser
{
    private readonly Lexer _lexer;
    private readonly Diagnostics _diag;
    private readonly string _source;
    private Token _current;
    private Token _previous;
    private int _matchCasePipeSuppressionDepth;

    // While parsing a match scrutinee, the trailing `with` belongs to the match expression
    // (`match value with | ...`), not to a record-update expression. This depth suppresses
    // brace-free `with`-update parsing so the scrutinee stops before the match's `with`. A
    // parenthesised sub-expression resets it (see ParseParen), so `match (p with x = 1) with ...`
    // still reads the inner `with` as a record update.
    private int _withSuppressionDepth;

    // While parsing the value of a flat top-level declaration, a following `let` starts the next
    // declaration rather than being absorbed as a whitespace-application argument (per LANGUAGE_SPEC:
    // a flat top-level `let` value is terminated by EOF or the start of the next `type`/`extern`/`let`).
    private bool _suppressLetWhitespaceArgument;

    // The source column at which the current flat top-level declaration began, or -1 when not parsing
    // such a declaration's value. A whitespace-application argument that opens a new source line at
    // this column or further left starts the next top-level item — the next declaration, or the
    // trailing expression (e.g. `let a = 1` <newline> `print(a)`) — and therefore terminates the
    // value instead of being absorbed (the grammar is `declaration* expr?`). Multi-line whitespace
    // application *within* an expression body is indented past this column and is preserved.
    private int _topLevelDeclColumn = -1;

    public Parser(string text, Diagnostics diag)
    {
        _diag = diag;
        _source = text;
        _lexer = new Lexer(text, diag);
        _previous = new Token(TokenKind.EOF, "", 0, 0, 0);
        _current = _lexer.Next();
    }

    // Returns the 0-based column of the character at `pos` in _source.
    private int GetColumn(int pos)
    {
        if (pos <= 0) return 0;
        var lineStart = _source.LastIndexOf('\n', pos - 1);
        return lineStart < 0 ? pos : pos - lineStart - 1;
    }

    public Expr ParseExpression()
    {
        var expr = ParseExpressionCore();
        EnsureEndOfInput();
        return expr;
    }

    public Program ParseProgram()
    {
        // A file is `declaration* expr?` (imports are stripped upstream). Declarations are `type`,
        // `extern`, flat `let [rec] name = value` (no `in`), and `let rec ... and ...` groups,
        // freely interleaved and ordered. A `let ... in ...` is an ordinary expression, not a
        // declaration, so it terminates the loop and becomes the (optional) trailing body.
        var items = new List<TopLevelItem>();
        Expr? body = null;

        while (_current.Kind is TokenKind.Type or TokenKind.Extern or TokenKind.Let or TokenKind.Effect)
        {
            if (_current.Kind == TokenKind.Type)
            {
                items.Add(new TopLevelItem.Type(ParseTypeDecl()));
                continue;
            }

            if (_current.Kind == TokenKind.Extern)
            {
                items.Add(new TopLevelItem.Extern(ParseExternDecl()));
                continue;
            }

            if (_current.Kind == TokenKind.Effect)
            {
                items.Add(new TopLevelItem.Effect(ParseEffectDecl()));
                continue;
            }

            // A let-pattern binding (`let (a, b) = e in body`) is always an expression.
            if (IsLetPatternBinding())
            {
                break;
            }

            var header = ParseLetHeaderAndValue(topLevel: true);
            if (_current.Kind == TokenKind.In)
            {
                // `let ... in ...` — a nested-let expression, which is the trailing body.
                body = FinishLetExpression(header);
                break;
            }

            if (_current.Kind == TokenKind.And)
            {
                items.Add(ParseRecGroup(header));
                continue;
            }

            if (header.ValueLeadsWithLet && _current.Kind == TokenKind.EOF)
            {
                // `let x = let y = ... in ...` (a bare `let`-introduced value) at EOF is genuinely
                // ambiguous with the nested `let ... in ...` pyramid, which needs an outer `in`: the
                // value's `let..in` is byte-for-byte identical whether the author meant a complete
                // flat declaration or the start of a pyramid still awaiting its outer `in`. The REPL
                // resolves the ambiguity toward "keep reading": its `Repl_should_support_multiline_
                // nested_let_input` feeds `let x =` / `let y = 2 in y` / `in x + 1` line by line, so
                // the intermediate `let x = let y = 2 in y`<EOF> MUST surface a need-more-input
                // diagnostic rather than commit to a flat decl (treating it as a complete decl here
                // makes the REPL stop early and that test fail — verified empirically). Finishing the
                // expression emits the expected-`in` diagnostic that `IsLikelyNeedMoreInput` keys on.
                // This carve-out is ONLY for EOF: a bare `let..in` value followed by the next
                // top-level declaration or a trailing expression is unambiguous and falls through to
                // a flat `LetDecl` below. A flat declaration whose `let..in` value must end the file
                // is written with parentheses, which escapes the pyramid: `let x = (let y = 2 in y)`.
                body = FinishLetExpression(header);
                break;
            }

            // A flat top-level binding, terminated by EOF or the next declaration.
            items.Add(new TopLevelItem.LetDecl(header.Name, header.Value, header.IsRecursive) { SugarParams = header.SugarParams });
        }

        // The trailing expression is optional once the file has declarations: a file may end after
        // its last declaration (Body = null). But a file with no declarations at all must be a
        // single expression — parsing one here surfaces the "expected expression" diagnostic for an
        // empty or comment-only file rather than silently yielding an empty program.
        if (body is null && (_current.Kind != TokenKind.EOF || items.Count == 0))
        {
            body = ParseExpressionCore();
        }

        EnsureEndOfInput();
        return new Program(items, body);
    }

    /// <summary>
    /// Parses the remainder of a mutual-recursion group given its first binding's header: while the
    /// current token is <c>and</c>, consumes <c>and name = value</c> bindings. Reports a parse error
    /// if the group is not introduced by <c>let rec</c>.
    /// </summary>
    private TopLevelItem.RecGroup ParseRecGroup(LetHeader header)
    {
        if (!header.IsRecursive)
        {
            _diag.Error(CurrentErrorSpan(), "'and' is only allowed in a 'let rec' binding group.", DiagnosticCodes.ParseError);
        }

        var bindings = new List<(string Name, Expr Value)> { (header.Name, header.Value) };
        var sugarParams = new List<IReadOnlyList<string>> { header.SugarParams };
        while (_current.Kind == TokenKind.And)
        {
            var andStart = _current.Position;
            Consume(TokenKind.And);
            var (_, name, value, andSugarParams, _, _) = ParseLetBinding(andStart, topLevel: true);
            bindings.Add((name, value));
            sugarParams.Add(andSugarParams);
        }

        return new TopLevelItem.RecGroup(bindings) { SugarParams = sugarParams };
    }

    private ExternDecl ParseExternDecl()
    {
        var start = _current.Position;
        Consume(TokenKind.Extern);

        if (_current.Kind == TokenKind.Type)
        {
            Consume(TokenKind.Type);
            var typeName = Consume(TokenKind.Ident).Text;
            return RegisterExternDecl(new ExternDecl.OpaqueType(typeName), start, LastConsumedEnd);
        }

        var name = Consume(TokenKind.Ident).Text;
        Consume(TokenKind.LParen);
        var parameterTypes = new List<ParsedType>();
        if (_current.Kind != TokenKind.RParen)
        {
            parameterTypes.Add(ParseFfiType());
            while (_current.Kind == TokenKind.Comma)
            {
                Consume(TokenKind.Comma);
                parameterTypes.Add(ParseFfiType());
            }
        }

        Consume(TokenKind.RParen);
        Consume(TokenKind.Arrow);
        var returnType = ParseFfiType();

        string? symbolName = null;
        if (_current.Kind == TokenKind.Equals)
        {
            Consume(TokenKind.Equals);
            symbolName = Consume(TokenKind.String).Text;
        }

        return RegisterExternDecl(new ExternDecl.Function(name, parameterTypes, returnType, symbolName), start, LastConsumedEnd);
    }

    private ParsedType ParseFfiType()
    {
        if (_current.Kind == TokenKind.Star)
        {
            Consume(TokenKind.Star);
            return new ParsedType.Pointer(ParseFfiType());
        }

        return ParseTypeName();
    }

    private ParsedType ParseTypeName()
    {
        return new ParsedType.Named(Consume(TokenKind.Ident).Text);
    }

    private Expr ParseExpressionCore()
    {
        return ParseMatch();
    }

    private TypeDecl ParseTypeDecl()
    {
        var start = _current.Position;
        Consume(TokenKind.Type);
        var name = Consume(TokenKind.Ident).Text;
        var typeParameters = new List<TypeParameter>();
        if (_current.Kind == TokenKind.LParen)
        {
            Consume(TokenKind.LParen);
            if (_current.Kind != TokenKind.RParen)
            {
                typeParameters.Add(new TypeParameter(Consume(TokenKind.Ident).Text));
                while (_current.Kind == TokenKind.Comma)
                {
                    Consume(TokenKind.Comma);
                    typeParameters.Add(new TypeParameter(Consume(TokenKind.Ident).Text));
                }
            }

            Consume(TokenKind.RParen);
        }
        Consume(TokenKind.Equals);

        // The brace record form (`type Point = { x: Int, y: Int }`) has been removed in favour of
        // brace-free field alternatives (`type Point = | x: Int | y: Int`).
        if (_current.Kind == TokenKind.LBrace)
        {
            _diag.Error(CurrentErrorSpan(), "Brace record declarations have been removed; use '| field: Type' alternatives.");
            SkipBalancedBraces();
            return RegisterTypeDecl(new TypeDecl(name, typeParameters, []), start, LastConsumedEnd);
        }

        // A `type ... = | ...` declaration is either a record (all `| field: Type` branches) or an
        // ordinary ADT (all `| Constructor(...)` branches). A branch is a record field when its name
        // is immediately followed by `:`; otherwise it is a constructor.
        var constructors = new List<TypeConstructor>();
        var fieldNames = new List<string>();
        var fieldTypeNames = new List<string>();
        var sawField = false;
        var sawConstructor = false;
        while (_current.Kind == TokenKind.Pipe)
        {
            Consume(TokenKind.Pipe);
            var branchStart = _previous.Position;
            var branchName = Consume(TokenKind.Ident).Text;

            if (_current.Kind == TokenKind.Colon)
            {
                // Record field branch: | name: Type
                sawField = true;
                Consume(TokenKind.Colon);
                var fieldType = ParseTypeExprAtomName();
                fieldNames.Add(branchName);
                fieldTypeNames.Add(fieldType);
                continue;
            }

            // Constructor branch: | Name | Name(p1, p2, ...)
            sawConstructor = true;
            var parameters = new List<string>();
            if (_current.Kind == TokenKind.LParen)
            {
                Consume(TokenKind.LParen);
                if (_current.Kind != TokenKind.RParen)
                {
                    parameters.Add(Consume(TokenKind.Ident).Text);
                    while (_current.Kind == TokenKind.Comma)
                    {
                        Consume(TokenKind.Comma);
                        parameters.Add(Consume(TokenKind.Ident).Text);
                    }
                }
                Consume(TokenKind.RParen);
            }
            constructors.Add(RegisterTypeConstructor(new TypeConstructor(branchName, parameters), branchStart, LastConsumedEnd));
        }

        if (sawField && sawConstructor)
        {
            _diag.Error(CurrentErrorSpan(), "Record field alternatives cannot be mixed with constructor alternatives.");
        }

        if (sawField)
        {
            var recordCtor = RegisterTypeConstructor(
                new TypeConstructor(name, fieldTypeNames) { FieldNames = fieldNames },
                start, LastConsumedEnd);
            return RegisterTypeDecl(
                new TypeDecl(name, typeParameters, [recordCtor]) { IsRecord = true },
                start, LastConsumedEnd);
        }

        if (constructors.Count == 0)
        {
            _diag.Error(CurrentErrorSpan(), $"Type '{name}' must have at least one constructor.");
        }

        return RegisterTypeDecl(new TypeDecl(name, typeParameters, constructors), start, LastConsumedEnd);
    }

    /// <summary>
    /// Best-effort recovery for the removed brace record forms: consumes a balanced <c>{ ... }</c>
    /// block starting at the current <c>{</c> token so parsing can continue after the diagnostic.
    /// </summary>
    private void SkipBalancedBraces()
    {
        if (_current.Kind != TokenKind.LBrace)
        {
            return;
        }

        Consume(TokenKind.LBrace);
        var depth = 1;
        while (depth > 0 && _current.Kind != TokenKind.EOF)
        {
            if (_current.Kind == TokenKind.LBrace) depth++;
            else if (_current.Kind == TokenKind.RBrace) depth--;
            Advance();
        }
    }

    /// <summary>
    /// Parses a simple type name used inside record field declarations.
    /// Supports parameterised types like <c>List(Int)</c> and <c>Maybe(T)</c>.
    /// Returns the type name as a string token (for compatibility with existing <see cref="TypeConstructor.Parameters"/>).
    /// </summary>
    private string ParseTypeExprAtomName()
    {
        var typeName = Consume(TokenKind.Ident).Text;
        // Parameterised types in field positions are not yet representable as plain strings.
        // Consume the argument list to keep parsing, but report an error instead of silently dropping it.
        if (_current.Kind == TokenKind.LParen)
        {
            _diag.Error(CurrentErrorSpan(), $"Parameterized type arguments are not supported in record field declarations yet (found '{typeName}(...)').");
            Consume(TokenKind.LParen);
            var depth = 1;
            while (depth > 0 && _current.Kind != TokenKind.EOF)
            {
                if (_current.Kind == TokenKind.LParen) depth++;
                else if (_current.Kind == TokenKind.RParen) depth--;
                Advance();
            }

            if (depth > 0)
            {
                _diag.Error(CurrentErrorSpan(), "Unterminated type argument list in record field declaration.");
            }
        }

        return typeName;
    }

    /// <summary>
    /// Parses an <c>effect</c> declaration:
    /// <c>effect Name [(a, b)] = | op [: TypeExpr] | op2 ...</c>.
    /// </summary>
    private EffectDecl ParseEffectDecl()
    {
        var start = _current.Position;
        Consume(TokenKind.Effect);
        var name = Consume(TokenKind.Ident).Text;
        var typeParameters = new List<TypeParameter>();
        if (_current.Kind == TokenKind.LParen)
        {
            Consume(TokenKind.LParen);
            if (_current.Kind != TokenKind.RParen)
            {
                typeParameters.Add(new TypeParameter(Consume(TokenKind.Ident).Text));
                while (_current.Kind == TokenKind.Comma)
                {
                    Consume(TokenKind.Comma);
                    typeParameters.Add(new TypeParameter(Consume(TokenKind.Ident).Text));
                }
            }

            Consume(TokenKind.RParen);
        }

        Consume(TokenKind.Equals);

        var operations = new List<EffectOperation>();
        while (_current.Kind == TokenKind.Pipe)
        {
            Consume(TokenKind.Pipe);
            var opName = Consume(TokenKind.Ident).Text;
            TypeExpr? signature = null;
            if (_current.Kind == TokenKind.Colon)
            {
                Consume(TokenKind.Colon);
                signature = ParseTypeExpr();
            }

            operations.Add(new EffectOperation(opName, signature));
        }

        if (operations.Count == 0)
        {
            _diag.Error(CurrentErrorSpan(), $"Effect '{name}' must declare at least one operation.");
        }

        var decl = new EffectDecl(name, typeParameters, operations);
        AstSpans.Set(decl, TextSpan.FromBounds(start, LastConsumedEnd));
        return decl;
    }

    /// <summary>
    /// Parses a full type expression: <c>Int</c>, <c>Int -> Str</c>, <c>List(Int)</c>,
    /// <c>(Int, Str)</c>, <c>()</c> (Unit), and effect rows: <c>Str -> Int uses {Prices}</c>.
    /// </summary>
    private TypeExpr ParseTypeExpr()
    {
        var (type, pendingUses) = ParseTypeExprWithUses();
        if (pendingUses is not null)
        {
            // A `uses` row can only attach to a function arrow; e.g. `let x : Int uses {E}`.
            _diag.Error(CurrentErrorSpan(), "'uses' requires a function type to attach to.", DiagnosticCodes.ParseError);
        }

        return type;
    }

    /// <summary>
    /// Parses a type expression, returning a trailing <c>uses</c> row separately when the row
    /// follows a non-arrow type. The row attaches to the innermost arrow whose result it follows
    /// (<c>A -> B -> C uses {E}</c> is <c>A -> (B -> C uses {E})</c>), so a row parsed after a
    /// non-arrow result bubbles up exactly one level to the arrow that encloses it.
    /// </summary>
    private (TypeExpr Type, UsesRowSyntax? PendingUses) ParseTypeExprWithUses()
    {
        var atom = ParseTypeExprPrimary();
        if (_current.Kind == TokenKind.Arrow)
        {
            Consume(TokenKind.Arrow);
            var (returnType, pendingUses) = ParseTypeExprWithUses();
            return (new TypeExpr.Arrow(atom, returnType) { Uses = pendingUses }, null);
        }

        if (_current.Kind == TokenKind.Uses)
        {
            return (atom, ParseUsesRow());
        }

        return (atom, null);
    }

    /// <summary>
    /// Parses a <c>uses</c> row: <c>uses {A, B}</c>, <c>uses {A, B | e}</c>, <c>uses {State(Int)}</c>,
    /// or the bare row variable form <c>uses e</c>.
    /// </summary>
    private UsesRowSyntax ParseUsesRow()
    {
        Consume(TokenKind.Uses);

        // Bare row variable: `uses e`.
        if (_current.Kind == TokenKind.Ident)
        {
            return new UsesRowSyntax([], Consume(TokenKind.Ident).Text);
        }

        Consume(TokenKind.LBrace);
        var effects = new List<EffectRefSyntax>();
        string? tailVar = null;
        if (_current.Kind != TokenKind.RBrace)
        {
            effects.Add(ParseEffectRef());
            while (_current.Kind == TokenKind.Comma)
            {
                Consume(TokenKind.Comma);
                effects.Add(ParseEffectRef());
            }

            if (_current.Kind == TokenKind.Pipe)
            {
                Consume(TokenKind.Pipe);
                tailVar = Consume(TokenKind.Ident).Text;
            }
        }

        Consume(TokenKind.RBrace);
        return new UsesRowSyntax(effects, tailVar);
    }

    /// <summary>Parses one effect reference in a <c>uses</c> row: <c>Clock</c> or <c>State(Int)</c>.</summary>
    private EffectRefSyntax ParseEffectRef()
    {
        var name = Consume(TokenKind.Ident).Text;
        var args = new List<TypeExpr>();
        if (_current.Kind == TokenKind.LParen)
        {
            Consume(TokenKind.LParen);
            if (_current.Kind != TokenKind.RParen)
            {
                args.Add(ParseTypeExpr());
                while (_current.Kind == TokenKind.Comma)
                {
                    Consume(TokenKind.Comma);
                    args.Add(ParseTypeExpr());
                }
            }

            Consume(TokenKind.RParen);
        }

        return new EffectRefSyntax(name, args);
    }

    private TypeExpr ParseTypeExprPrimary()
    {
        if (_current.Kind == TokenKind.LParen)
        {
            Consume(TokenKind.LParen);
            if (_current.Kind == TokenKind.RParen)
            {
                Consume(TokenKind.RParen);
                return new TypeExpr.UnitType();
            }

            var first = ParseTypeExpr();
            if (_current.Kind == TokenKind.Comma)
            {
                var elements = new List<TypeExpr> { first };
                while (_current.Kind == TokenKind.Comma)
                {
                    Consume(TokenKind.Comma);
                    elements.Add(ParseTypeExpr());
                }
                Consume(TokenKind.RParen);
                return new TypeExpr.TupleType(elements);
            }

            Consume(TokenKind.RParen);
            return first;
        }

        var name = Consume(TokenKind.Ident).Text;
        if (_current.Kind == TokenKind.LParen)
        {
            Consume(TokenKind.LParen);
            var args = new List<TypeExpr>();
            if (_current.Kind != TokenKind.RParen)
            {
                args.Add(ParseTypeExpr());
                while (_current.Kind == TokenKind.Comma)
                {
                    Consume(TokenKind.Comma);
                    args.Add(ParseTypeExpr());
                }
            }
            Consume(TokenKind.RParen);
            return new TypeExpr.Applied(name, args);
        }

        return new TypeExpr.Named(name);
    }

    private Expr ParseMatch()
    {
        if (_current.Kind == TokenKind.Handle)
        {
            return ParseHandle();
        }

        if (_current.Kind != TokenKind.Match)
        {
            return ParseIf();
        }

        var matchPos = _current.Position;
        Consume(TokenKind.Match);
        _withSuppressionDepth++;
        Expr value;
        try
        {
            value = ParseExpressionCore();
        }
        finally
        {
            _withSuppressionDepth--;
        }
        Consume(TokenKind.With);

        var cases = new List<MatchCase>();
        int firstPipeColumn = _current.Kind == TokenKind.Pipe ? GetColumn(_current.Position) : -1;
        if (_current.Kind == TokenKind.Pipe)
        {
            Consume(TokenKind.Pipe);
        }

        var pattern = ParsePattern();
        Expr? guard = null;
        if (_current.Kind == TokenKind.When)
        {
            Consume(TokenKind.When);
            guard = ParseExpressionCore();
        }
        Consume(TokenKind.Arrow);
        var body = ParseMatchCaseBody();
        cases.Add(new MatchCase(pattern, body, guard));

        while (_current.Kind == TokenKind.Pipe && (firstPipeColumn < 0 || GetColumn(_current.Position) >= firstPipeColumn))
        {
            Consume(TokenKind.Pipe);
            pattern = ParsePattern();
            guard = null;
            if (_current.Kind == TokenKind.When)
            {
                Consume(TokenKind.When);
                guard = ParseExpressionCore();
            }
            Consume(TokenKind.Arrow);
            body = ParseMatchCaseBody();
            cases.Add(new MatchCase(pattern, body, guard));
        }

        return RegisterExpr(new Expr.Match(value, cases, matchPos), matchPos, LastConsumedEnd);
    }

    private Expr ParseMatchCaseBody()
    {
        _matchCasePipeSuppressionDepth++;
        try
        {
            return ParseExpressionCore();
        }
        finally
        {
            _matchCasePipeSuppressionDepth--;
        }
    }

    /// <summary>
    /// Parses <c>handle body with | Effect.op(args) -> arm | return(r) -> arm</c>. The body is
    /// parsed like a match scrutinee (the trailing <c>with</c> belongs to the handle), and arms
    /// follow the same pipe/column rules as match cases.
    /// </summary>
    private Expr ParseHandle()
    {
        var start = _current.Position;
        Consume(TokenKind.Handle);
        _withSuppressionDepth++;
        Expr body;
        try
        {
            body = ParseExpressionCore();
        }
        finally
        {
            _withSuppressionDepth--;
        }

        Consume(TokenKind.With);

        var arms = new List<HandlerArm>();
        int firstPipeColumn = _current.Kind == TokenKind.Pipe ? GetColumn(_current.Position) : -1;
        if (_current.Kind == TokenKind.Pipe)
        {
            Consume(TokenKind.Pipe);
        }

        arms.Add(ParseHandlerArm());
        while (_current.Kind == TokenKind.Pipe && (firstPipeColumn < 0 || GetColumn(_current.Position) >= firstPipeColumn))
        {
            Consume(TokenKind.Pipe);
            arms.Add(ParseHandlerArm());
        }

        return RegisterExpr(new Expr.Handle(body, arms), start, LastConsumedEnd);
    }

    /// <summary>
    /// Parses one handler arm: <c>Effect.op(p1, p2) -> body</c> or <c>return(r) -> body</c>.
    /// </summary>
    private HandlerArm ParseHandlerArm()
    {
        var head = Consume(TokenKind.Ident);
        string? effectName = null;
        var operationName = head.Text;
        if (_current.Kind == TokenKind.Dot)
        {
            Consume(TokenKind.Dot);
            effectName = head.Text;
            operationName = Consume(TokenKind.Ident).Text;
        }
        else if (!string.Equals(head.Text, "return", StringComparison.Ordinal))
        {
            _diag.Error(head.Span, "Handler arm must be 'Effect.op(args)' or 'return(value)'.", DiagnosticCodes.ParseError);
        }

        var parameters = new List<Pattern>();
        Consume(TokenKind.LParen);
        if (_current.Kind != TokenKind.RParen)
        {
            parameters.Add(ParsePattern());
            while (_current.Kind == TokenKind.Comma)
            {
                Consume(TokenKind.Comma);
                parameters.Add(ParsePattern());
            }
        }

        Consume(TokenKind.RParen);
        Consume(TokenKind.Arrow);
        var body = ParseMatchCaseBody();
        return new HandlerArm(effectName, operationName, parameters, body);
    }

    private Expr ParseIf()
    {
        if (_current.Kind == TokenKind.If)
        {
            var start = _current.Position;
            Consume(TokenKind.If);
            var cond = ParseExpressionCore();
            Consume(TokenKind.Then);
            var thenExpr = ParseExpressionCore();
            var elseToken = Consume(TokenKind.Else);
            var elseExpr = elseToken.Length == 0
                ? RegisterExpr(new Expr.IntLit(0), elseToken.Position, elseToken.End)
                : ParseExpressionCore();
            return RegisterExpr(new Expr.If(cond, thenExpr, elseExpr), start, LastConsumedEnd);
        }

        return ParseLet();
    }

    private Expr ParseLet()
    {
        if (_current.Kind == TokenKind.Let)
        {
            var start = _current.Position;

            // Check for let-pattern bindings: let (a, b) = expr in body
            // Desugar to: match expr with | (a, b) -> body
            if (IsLetPatternBinding())
            {
                return ParseLetPattern(start);
            }

            var header = ParseLetHeaderAndValue(topLevel: false);
            return FinishLetExpression(header);
        }

        if (_current.Kind == TokenKind.LetQuestion)
        {
            var start = _current.Position;
            Consume(TokenKind.LetQuestion);
            var nameToken = Consume(TokenKind.Ident);
            Consume(TokenKind.Equals);
            var value = ParseExpressionCore();
            Consume(TokenKind.In);
            var body = ParseExpressionCore();

            var letResultExpr = RegisterExpr(new Expr.LetResult(nameToken.Text, value, body), start, LastConsumedEnd);
            AstSpans.SetLetResultName(letResultExpr, nameToken.Span);
            return letResultExpr;
        }

        if (_current.Kind == TokenKind.LetBang)
        {
            // let! x = expr in body  ⟶  let x = await expr in body
            // let! x = expr1 let! y = expr2 in body  ⟶  let x = await expr1 in let y = await expr2 in body
            return ParseLetBangChain();
        }

        return ParseLambda();
    }

    /// <summary>
    /// The parsed prefix of a <c>let</c> binding up to and including its value, but *not* the
    /// terminating <c>in</c>. Shared between the nested <c>let ... in ...</c> expression form and
    /// the flat top-level <c>let</c> declaration form, which differ only in what follows the value.
    /// </summary>
    private readonly record struct LetHeader(
        int Start,
        Token NameToken,
        string Name,
        bool IsRecursive,
        Expr Value,
        List<string> SugarParams,
        TypeExpr? TypeAnnotation,
        bool ValueLeadsWithLet);

    /// <summary>
    /// Parses <c>let [rec] name [params] [: type] = value</c>, stopping before <c>in</c>. When
    /// <paramref name="topLevel"/> is set, a following <c>let</c> terminates the value (it begins the
    /// next declaration) rather than being absorbed as a whitespace-application argument.
    /// </summary>
    private LetHeader ParseLetHeaderAndValue(bool topLevel)
    {
        var start = _current.Position;
        Consume(TokenKind.Let);
        var isRec = _current.Kind == TokenKind.Rec;
        if (isRec)
        {
            Consume(TokenKind.Rec);
        }

        var (nameToken, name, value, sugarParams, typeAnnotation, valueLeadsWithLet) = ParseLetBinding(start, topLevel);
        return new LetHeader(start, nameToken, name, isRec, value, sugarParams, typeAnnotation, valueLeadsWithLet);
    }

    /// <summary>
    /// Parses the <c>name [params] [: type] = value</c> portion of a binding (the part after
    /// <c>let [rec]</c> or after <c>and</c>), desugaring ML-style parameters into nested lambdas.
    /// </summary>
    private (Token NameToken, string Name, Expr Value, List<string> SugarParams, TypeExpr? TypeAnnotation, bool ValueLeadsWithLet) ParseLetBinding(int start, bool topLevel)
    {
        var nameToken = Consume(TokenKind.Ident);
        var name = nameToken.Text;

        // Optional type annotation: let name : TypeExpr = value
        TypeExpr? typeAnnotation = null;
        if (_current.Kind == TokenKind.Colon)
        {
            Consume(TokenKind.Colon);
            typeAnnotation = ParseTypeExpr();
        }

        // ML-style function sugar: let f x y = body => let f = fun (x) -> fun (y) -> body
        // (Only collected when no annotation is present, since annotated let uses `let f : T -> T = fun x -> ...`)
        var sugarParams = new List<string>();
        var sugarParamTokens = new List<Token>();
        if (typeAnnotation is null)
        {
            while (_current.Kind == TokenKind.Ident)
            {
                var paramToken = Consume(TokenKind.Ident);
                sugarParams.Add(paramToken.Text);
                sugarParamTokens.Add(paramToken);
            }
        }

        Consume(TokenKind.Equals);

        // Whether the value is a bare (unparenthesized) `let`-introduced expression. Such a value
        // makes the whole construct a nested `let ... in ...` expression that requires an outer
        // `in`; it is never a flat top-level declaration (see ParseProgram). Parenthesizing the
        // value escapes this — `let x = (let y = 2 in y)` is a flat declaration.
        //
        // ML-style parameter sugar also escapes it: `let f x = let g = ... in g` has sugar params,
        // so the desugared value is a `fun (x) -> (let ... in ...)` Lambda, not a bare `let`. The
        // function's body merely *happens* to lead with `let..in`, which is unambiguous (the `in`
        // belongs to the body) — it is a flat function declaration, never a pyramid head awaiting an
        // outer `in`. Only a paramless binding leading with `let` is the ambiguous pyramid case.
        var valueLeadsWithLet = sugarParams.Count == 0
            && _current.Kind is TokenKind.Let or TokenKind.LetBang or TokenKind.LetQuestion;

        var previousSuppression = _suppressLetWhitespaceArgument;
        var previousDeclColumn = _topLevelDeclColumn;
        _suppressLetWhitespaceArgument = topLevel;
        if (topLevel)
        {
            _topLevelDeclColumn = GetColumn(start);
        }
        var value = ParseExpressionCore();
        _suppressLetWhitespaceArgument = previousSuppression;
        _topLevelDeclColumn = previousDeclColumn;

        // Desugar ML-style parameters into nested lambdas
        for (int i = sugarParams.Count - 1; i >= 0; i--)
        {
            var lambda = RegisterExpr(new Expr.Lambda(sugarParams[i], value), start, AstSpans.GetOrDefault(value).End);
            AstSpans.SetLambdaParameter(lambda, sugarParamTokens[i].Span);
            value = lambda;
        }

        return (nameToken, name, value, sugarParams, typeAnnotation, valueLeadsWithLet);
    }

    /// <summary>Completes a nested <c>let ... in body</c> expression from an already-parsed header.</summary>
    private Expr FinishLetExpression(LetHeader header)
    {
        Consume(TokenKind.In);
        var body = ParseExpressionCore();
        if (header.IsRecursive)
        {
            var letRec = RegisterExpr(new Expr.LetRec(header.Name, header.Value, body) { SugarParams = header.SugarParams, TypeAnnotation = header.TypeAnnotation }, header.Start, LastConsumedEnd);
            AstSpans.SetLetRecName(letRec, header.NameToken.Span);
            return letRec;
        }

        var letExpr = RegisterExpr(new Expr.Let(header.Name, header.Value, body) { SugarParams = header.SugarParams, TypeAnnotation = header.TypeAnnotation }, header.Start, LastConsumedEnd);
        AstSpans.SetLetName(letExpr, header.NameToken.Span);
        return letExpr;
    }

    /// <summary>
    /// Parses a chain of <c>let!</c> bindings, desugaring each into
    /// <c>let name = await value in body</c>. Consecutive <c>let!</c>
    /// bindings chain without explicit <c>in</c> between them — only the
    /// final binding requires <c>in</c> before the result expression.
    /// </summary>
    private Expr ParseLetBangChain()
    {
        var start = _current.Position;
        Consume(TokenKind.LetBang);
        var nameToken = Consume(TokenKind.Ident);
        Consume(TokenKind.Equals);
        var value = ParseExpressionCore();
        var awaitValue = RegisterExpr(new Expr.Await(value), AstSpans.GetOrDefault(value).Start, AstSpans.GetOrDefault(value).End);

        Expr body;
        if (_current.Kind == TokenKind.LetBang)
        {
            // Chain: the body is the next let! binding (no 'in' required between them)
            body = ParseLetBangChain();
        }
        else
        {
            Consume(TokenKind.In);
            body = ParseExpressionCore();
        }

        var letExpr = RegisterExpr(new Expr.Let(nameToken.Text, awaitValue, body), start, LastConsumedEnd);
        AstSpans.SetLetName(letExpr, nameToken.Span);
        return letExpr;
    }

    /// <summary>
    /// Detects whether the current position is a let-pattern binding.
    /// Returns true for <c>let (</c> (tuple patterns) or <c>let ident ::</c>
    /// / <c>let _ ::</c> (cons patterns).
    /// </summary>
    private bool IsLetPatternBinding()
    {
        // The Lexer has already consumed tokens up to _current.
        // We need to peek past 'let' to see if a pattern follows.
        // 'let (' → tuple pattern.
        // 'let ident ::' or 'let _ ::' → cons pattern.
        // 'let rec' or 'let ident =' → normal let.
        if (_current.Kind != TokenKind.Let) return false;

        // Save state — the lexer is forward-only, so we use lookahead
        // by examining the lexer's next output.
        var savedCurrent = _current;
        var savedPrevious = _previous;
        var savedLexer = _lexer.SavePosition();
        Advance(); // consume 'let'

        var isPattern = _current.Kind == TokenKind.LParen;

        // Also detect cons patterns: let x :: xs = ...
        // After 'let', if we see an identifier (including _) followed by ::, it's a cons pattern.
        if (!isPattern && _current.Kind == TokenKind.Ident)
        {
            Advance(); // consume the identifier
            isPattern = _current.Kind == TokenKind.ColonColon;
        }

        // Restore state
        _current = savedCurrent;
        _previous = savedPrevious;
        _lexer.RestorePosition(savedLexer);

        return isPattern;
    }

    /// <summary>
    /// Parses <c>let pattern = expr in body</c> and desugars to
    /// <c>match expr with | pattern -> body</c>.
    /// Reports a parse error if the pattern is refutable (see §11.8).
    /// </summary>
    private Expr ParseLetPattern(int start)
    {
        Consume(TokenKind.Let);
        var pattern = ParsePattern();

        if (!IsIrrefutableLetPattern(pattern))
        {
            var span = AstSpans.GetOrDefault(pattern);
            _diag.Error(span, "Refutable pattern in let binding. Only irrefutable patterns (variable, wildcard, tuple, cons) are allowed — use 'match' for refutable patterns.", DiagnosticCodes.ParseError);
        }

        Consume(TokenKind.Equals);
        var value = ParseExpressionCore();
        Consume(TokenKind.In);
        var body = ParseExpressionCore();

        // Desugar: let pattern = value in body → match value with | pattern -> body
        var cases = new List<MatchCase> { new MatchCase(pattern, body) };
        return RegisterExpr(new Expr.Match(value, cases, start), start, LastConsumedEnd);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="pattern"/> is irrefutable —
    /// that is, it will always match regardless of the value.
    /// The irrefutable patterns are:
    /// <list type="bullet">
    ///   <item><see cref="Pattern.Var"/> — always matches and binds.</item>
    ///   <item><see cref="Pattern.Wildcard"/> — always matches.</item>
    ///   <item><see cref="Pattern.Tuple"/> — irrefutable when every element is irrefutable.</item>
    ///   <item><see cref="Pattern.Cons"/> — allowed (may fail at runtime on empty list).</item>
    /// </list>
    /// All other patterns (Constructor, IntLit, StrLit, BoolLit, EmptyList)
    /// are refutable and must not appear in let-pattern bindings.
    /// </summary>
    internal static bool IsIrrefutableLetPattern(Pattern pattern) => pattern switch
    {
        Pattern.Var => true,
        Pattern.Wildcard => true,
        Pattern.Tuple t => t.Elements.All(IsIrrefutableLetPattern),
        Pattern.Cons c => IsIrrefutableLetPattern(c.Head) && IsIrrefutableLetPattern(c.Tail),
        _ => false,
    };

    private Expr ParseLambda()
    {
        if (_current.Kind == TokenKind.Fun)
        {
            var start = _current.Position;
            Consume(TokenKind.Fun);
            Consume(TokenKind.LParen);
            var paramToken = Consume(TokenKind.Ident);
            var param = paramToken.Text;
            var extraParamTokens = new List<Token>();
            while (_current.Kind == TokenKind.Comma)
            {
                Consume(TokenKind.Comma);
                extraParamTokens.Add(Consume(TokenKind.Ident));
            }
            Consume(TokenKind.RParen);
            Consume(TokenKind.Arrow);
            var body = ParseExpressionCore();
            // Desugar multi-param lambdas: fun (x, y) -> body => fun (x) -> fun (y) -> body
            for (int i = extraParamTokens.Count - 1; i >= 0; i--)
            {
                var lambda = RegisterExpr(new Expr.Lambda(extraParamTokens[i].Text, body), start, AstSpans.GetOrDefault(body).End);
                AstSpans.SetLambdaParameter(lambda, extraParamTokens[i].Span);
                body = lambda;
            }

            var outerLambda = RegisterExpr(new Expr.Lambda(param, body), start, AstSpans.GetOrDefault(body).End);
            AstSpans.SetLambdaParameter(outerLambda, paramToken.Span);
            return outerLambda;
        }

        return ParseWith();
    }

    /// <summary>
    /// Parses brace-free record updates: <c>target with field = value[, field = value]*</c>. The
    /// target and each field value are parsed at pipe precedence, so <c>with</c> binds looser than
    /// function application and the binary operators. Chained updates
    /// (<c>p with x = 1 with y = 2</c>) are left-associative. Inside a match scrutinee the trailing
    /// <c>with</c> is suppressed (it belongs to the match), unless re-enabled by parentheses.
    /// </summary>
    private Expr ParseWith()
    {
        var target = ParsePipe();

        while (_current.Kind == TokenKind.With && _withSuppressionDepth == 0)
        {
            var start = AstSpans.GetOrDefault(target).Start;
            Consume(TokenKind.With);

            var updates = new List<(string Name, Expr Value)>();
            var fieldName = Consume(TokenKind.Ident).Text;
            Consume(TokenKind.Equals);
            var fieldValue = ParsePipe();
            updates.Add((fieldName, fieldValue));

            while (_current.Kind == TokenKind.Comma)
            {
                Consume(TokenKind.Comma);
                fieldName = Consume(TokenKind.Ident).Text;
                Consume(TokenKind.Equals);
                fieldValue = ParsePipe();
                updates.Add((fieldName, fieldValue));
            }

            target = RegisterExpr(new Expr.RecordUpdate(target, updates), start, LastConsumedEnd);
        }

        return target;
    }

    private Expr ParsePipe()
    {
        var left = ParseComparison();

        while (_current.Kind == TokenKind.PipeGreater || _current.Kind == TokenKind.PipeQuestionGreater || _current.Kind == TokenKind.PipeBangGreater)
        {
            var start = AstSpans.GetOrDefault(left).Start;
            var op = _current.Kind;
            Consume(op);
            var right = ParseComparison();
            left = op switch
            {
                TokenKind.PipeGreater => RegisterExpr(new Expr.Call(right, left), start, AstSpans.GetOrDefault(right).End),
                TokenKind.PipeQuestionGreater => RegisterExpr(new Expr.ResultPipe(left, right), start, AstSpans.GetOrDefault(right).End),
                TokenKind.PipeBangGreater => RegisterExpr(new Expr.ResultMapErrorPipe(left, right), start, AstSpans.GetOrDefault(right).End),
                _ => throw new InvalidOperationException()
            };
        }

        return left;
    }

    private Expr ParseComparison()
    {
        var left = ParseBitwiseOr();

        while (_current.Kind == TokenKind.GreaterThan || _current.Kind == TokenKind.GreaterEquals || _current.Kind == TokenKind.LessThan || _current.Kind == TokenKind.LessEquals || _current.Kind == TokenKind.EqualsEquals || _current.Kind == TokenKind.BangEquals)
        {
            var start = AstSpans.GetOrDefault(left).Start;
            var op = _current.Kind;
            Consume(op);
            var right = ParseBitwiseOr();
            left = op switch
            {
                TokenKind.GreaterThan => RegisterExpr(new Expr.GreaterThan(left, right), start, AstSpans.GetOrDefault(right).End),
                TokenKind.GreaterEquals => RegisterExpr(new Expr.GreaterOrEqual(left, right), start, AstSpans.GetOrDefault(right).End),
                TokenKind.LessThan => RegisterExpr(new Expr.LessThan(left, right), start, AstSpans.GetOrDefault(right).End),
                TokenKind.LessEquals => RegisterExpr(new Expr.LessOrEqual(left, right), start, AstSpans.GetOrDefault(right).End),
                TokenKind.EqualsEquals => RegisterExpr(new Expr.Equal(left, right), start, AstSpans.GetOrDefault(right).End),
                TokenKind.BangEquals => RegisterExpr(new Expr.NotEqual(left, right), start, AstSpans.GetOrDefault(right).End),
                _ => throw new InvalidOperationException()
            };
        }

        return left;
    }

    private Expr ParseBitwiseOr()
    {
        var left = ParseBitwiseXor();

        while (_current.Kind == TokenKind.Pipe && _matchCasePipeSuppressionDepth == 0)
        {
            var start = AstSpans.GetOrDefault(left).Start;
            Consume(TokenKind.Pipe);
            var right = ParseBitwiseXor();
            left = RegisterExpr(new Expr.BitwiseOr(left, right), start, AstSpans.GetOrDefault(right).End);
        }

        return left;
    }

    private Expr ParseBitwiseXor()
    {
        var left = ParseBitwiseAnd();

        while (_current.Kind == TokenKind.Caret)
        {
            var start = AstSpans.GetOrDefault(left).Start;
            Consume(TokenKind.Caret);
            var right = ParseBitwiseAnd();
            left = RegisterExpr(new Expr.BitwiseXor(left, right), start, AstSpans.GetOrDefault(right).End);
        }

        return left;
    }

    private Expr ParseBitwiseAnd()
    {
        var left = ParseCons();

        while (_current.Kind == TokenKind.Ampersand)
        {
            var start = AstSpans.GetOrDefault(left).Start;
            Consume(TokenKind.Ampersand);
            var right = ParseCons();
            left = RegisterExpr(new Expr.BitwiseAnd(left, right), start, AstSpans.GetOrDefault(right).End);
        }

        return left;
    }

    private Expr ParseCons()
    {
        var left = ParseShift();
        if (_current.Kind != TokenKind.ColonColon)
        {
            return left;
        }

        var start = AstSpans.GetOrDefault(left).Start;
        Consume(TokenKind.ColonColon);
        var right = ParseCons();
        return RegisterExpr(new Expr.Cons(left, right), start, AstSpans.GetOrDefault(right).End);
    }

    private Expr ParseShift()
    {
        var left = ParseAdditive();

        while (_current.Kind == TokenKind.LessLess || _current.Kind == TokenKind.GreaterGreater)
        {
            var start = AstSpans.GetOrDefault(left).Start;
            var op = _current.Kind;
            Consume(op);
            var right = ParseAdditive();
            left = op switch
            {
                TokenKind.LessLess => RegisterExpr(new Expr.ShiftLeft(left, right), start, AstSpans.GetOrDefault(right).End),
                TokenKind.GreaterGreater => RegisterExpr(new Expr.ShiftRight(left, right), start, AstSpans.GetOrDefault(right).End),
                _ => throw new InvalidOperationException()
            };
        }

        return left;
    }

    private Expr ParseAdditive()
    {
        var left = ParseMultiplicative();

        while (_current.Kind == TokenKind.Plus || _current.Kind == TokenKind.Minus)
        {
            var start = AstSpans.GetOrDefault(left).Start;
            var op = _current.Kind;
            Consume(op);
            var right = ParseMultiplicative();
            left = op switch
            {
                TokenKind.Plus => RegisterExpr(new Expr.Add(left, right), start, AstSpans.GetOrDefault(right).End),
                TokenKind.Minus => RegisterExpr(new Expr.Subtract(left, right), start, AstSpans.GetOrDefault(right).End),
                _ => throw new InvalidOperationException()
            };
        }

        return left;
    }

    private Expr ParseMultiplicative()
    {
        var left = ParseUnary();

        while (_current.Kind == TokenKind.Star || _current.Kind == TokenKind.Slash)
        {
            var start = AstSpans.GetOrDefault(left).Start;
            var op = _current.Kind;
            Consume(op);
            var right = ParseUnary();
            left = op switch
            {
                TokenKind.Star => RegisterExpr(new Expr.Multiply(left, right), start, AstSpans.GetOrDefault(right).End),
                TokenKind.Slash => RegisterExpr(new Expr.Divide(left, right), start, AstSpans.GetOrDefault(right).End),
                _ => throw new InvalidOperationException()
            };
        }

        return left;
    }

    private Expr ParseUnary()
    {
        if (_current.Kind == TokenKind.Minus)
        {
            var start = _current.Position;
            Consume(TokenKind.Minus);
            var zero = RegisterExpr(new Expr.IntLit(0), start, start + 1);
            var right = ParseUnary();
            return RegisterExpr(new Expr.Subtract(zero, right), start, AstSpans.GetOrDefault(right).End);
        }

        if (_current.Kind == TokenKind.Tilde)
        {
            var start = _current.Position;
            Consume(TokenKind.Tilde);
            var operand = ParseUnary();
            return RegisterExpr(new Expr.BitwiseNot(operand), start, AstSpans.GetOrDefault(operand).End);
        }

        if (_current.Kind == TokenKind.Await)
        {
            var start = _current.Position;
            Consume(TokenKind.Await);
            var operand = ParseCall();
            return RegisterExpr(new Expr.Await(operand), start, AstSpans.GetOrDefault(operand).End);
        }

        if (_current.Kind == TokenKind.Perform)
        {
            var start = _current.Position;
            Consume(TokenKind.Perform);
            var operation = ParseCall();
            return RegisterExpr(new Expr.Perform(operation), start, AstSpans.GetOrDefault(operation).End);
        }

        return ParseCall();
    }

    private Expr ParseCall()
    {
        var expr = ParsePrimary();

        while (true)
        {
            if (_current.Kind == TokenKind.LParen)
            {
                var start = AstSpans.GetOrDefault(expr).Start;
                Consume(TokenKind.LParen);

                // Named-argument call syntax (`Name(field = value, ...)`) is record construction.
                // It is recognised only when the first argument is `ident =` (a bare `=`, not `==`).
                if (NamedArgumentFollows())
                {
                    expr = ParseRecordConstruction(expr, start);
                    continue;
                }

                var args = new List<Expr>();
                if (_current.Kind == TokenKind.RParen)
                {
                    args.Add(RegisterExpr(new Expr.Var("Unit"), start + 1, start + 1));
                }
                else
                {
                    args.Add(ParseExpressionCore());
                    while (_current.Kind == TokenKind.Comma)
                    {
                        Consume(TokenKind.Comma);
                        args.Add(ParseExpressionCore());
                    }
                }
                Consume(TokenKind.RParen);
                // Desugar multi-arg calls: f(a, b) => f(a)(b)
                foreach (var a in args)
                {
                    expr = RegisterExpr(new Expr.Call(expr, a), start, LastConsumedEnd);
                }
                continue;
            }

            if (IsWhitespaceArgStarter(_current.Kind)
                && !(_suppressLetWhitespaceArgument && _current.Kind == TokenKind.Let)
                && !StartsNextTopLevelItem())
            {
                var start = AstSpans.GetOrDefault(expr).Start;
                var arg = ParseWhitespaceArgument();
                expr = RegisterExpr(new Expr.Call(expr, arg) { IsWhitespaceApplication = true }, start, AstSpans.GetOrDefault(arg).End);
                continue;
            }

            break;
        }

        return expr;
    }

    /// <summary>
    /// Returns <c>true</c> when the current token begins a named call argument: an identifier
    /// immediately followed by a single <c>=</c> (not <c>==</c>). Used to distinguish record
    /// construction (<c>Point(x = 1)</c>) from a positional call whose argument happens to start
    /// with an identifier (<c>Some(x)</c>, <c>Some(x == 1)</c>).
    /// </summary>
    private bool NamedArgumentFollows()
    {
        if (_current.Kind != TokenKind.Ident)
        {
            return false;
        }

        var savedCurrent = _current;
        var savedPrevious = _previous;
        var savedLexer = _lexer.SavePosition();
        Advance(); // consume the identifier
        var isNamed = _current.Kind == TokenKind.Equals;
        _current = savedCurrent;
        _previous = savedPrevious;
        _lexer.RestorePosition(savedLexer);
        return isNamed;
    }

    /// <summary>
    /// Parses the named-argument list of a record construction (the opening <c>(</c> has already been
    /// consumed) and produces a <see cref="Expr.RecordLit"/>. Named arguments are only valid for
    /// record construction, so <paramref name="callee"/> must be a simple unqualified type-name
    /// identifier; otherwise a diagnostic is reported and recovery continues.
    /// </summary>
    private Expr ParseRecordConstruction(Expr callee, int start)
    {
        string typeName;
        if (callee is Expr.Var v && v.Name.Length > 0 && char.IsUpper(v.Name[0]))
        {
            typeName = v.Name;
        }
        else
        {
            _diag.Error(CurrentErrorSpan(), "Named arguments are only allowed in record construction.");
            typeName = (callee as Expr.Var)?.Name ?? string.Empty;
        }

        var fields = new List<(string Name, Expr Value)>();
        var fieldName = Consume(TokenKind.Ident).Text;
        Consume(TokenKind.Equals);
        var fieldValue = ParseExpressionCore();
        fields.Add((fieldName, fieldValue));

        while (_current.Kind == TokenKind.Comma)
        {
            Consume(TokenKind.Comma);
            fieldName = Consume(TokenKind.Ident).Text;
            Consume(TokenKind.Equals);
            fieldValue = ParseExpressionCore();
            fields.Add((fieldName, fieldValue));
        }

        Consume(TokenKind.RParen);
        return RegisterExpr(new Expr.RecordLit(typeName, fields), start, LastConsumedEnd);
    }

    /// <summary>
    /// While parsing a flat top-level declaration's value, returns <c>true</c> when the current token
    /// opens a new source line at (or left of) the declaration's column. Such a token begins the next
    /// top-level item — the next declaration or the trailing expression — so it must terminate the
    /// value rather than be absorbed as a whitespace-application argument. Continuation arguments of a
    /// multi-line expression are indented past the declaration's column and so are preserved.
    /// </summary>
    private bool StartsNextTopLevelItem()
    {
        // The rule applies only to the outermost value of a flat top-level declaration (the same
        // scope in which `let` is suppressed). A nested `let ... in` value turns the flag off, so its
        // own multi-line application is governed by the explicit `in`, not by source layout.
        if (!_suppressLetWhitespaceArgument || _topLevelDeclColumn < 0)
        {
            return false;
        }

        return StartsSourceLine(_current.Position) && GetColumn(_current.Position) <= _topLevelDeclColumn;
    }

    // Returns true when only whitespace precedes `pos` on its source line (i.e. `pos` is the line's
    // first token).
    private bool StartsSourceLine(int pos)
    {
        var lineStart = pos <= 0 ? 0 : _source.LastIndexOf('\n', pos - 1) + 1;
        for (var i = lineStart; i < pos; i++)
        {
            if (!char.IsWhiteSpace(_source[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsWhitespaceArgStarter(TokenKind kind)
    {
        return kind is TokenKind.Ident or TokenKind.Int or TokenKind.Float or TokenKind.String
            or TokenKind.True or TokenKind.False or TokenKind.LBracket
            or TokenKind.Await or TokenKind.Let
            or TokenKind.If or TokenKind.Match or TokenKind.Fun;
    }

    private Expr ParseWhitespaceArgument()
    {
        return _current.Kind switch
        {
            TokenKind.Await => ParseUnary(),
            TokenKind.Let => ParseLet(),
            TokenKind.If => ParseIf(),
            TokenKind.Match => ParseMatch(),
            TokenKind.Fun => ParseLambda(),
            _ => ParsePrimary()
        };
    }

    private Expr ParsePrimary()
    {
        return _current.Kind switch
        {
            TokenKind.Int => ParseInt(),
            TokenKind.Float => ParseFloat(),
            TokenKind.String => ParseString(),
            TokenKind.True => ParseBool(true),
            TokenKind.False => ParseBool(false),
            TokenKind.Ident => ParseVar(),
            TokenKind.LParen => ParseParen(),
            TokenKind.LBracket => ParseList(),
            TokenKind.LBrace => ParseRemovedBraceUpdate(),
            _ => BadPrimary(),
        };
    }

    private Expr ParseInt()
    {
        var t = Consume(TokenKind.Int);
        if (TryParseUnsignedLiteral(t.Text, out var value, out var bits))
        {
            return RegisterExpr(new Expr.UIntLit(value, bits), t.Position, t.End);
        }

        return RegisterExpr(new Expr.IntLit(t.IntValue), t.Position, t.End);
    }

    private static bool TryParseUnsignedLiteral(string text, out ulong value, out int bits)
    {
        bits = 0;
        value = 0;

        if (text.EndsWith("u8", StringComparison.Ordinal))
        {
            bits = 8;
            return ulong.TryParse(text[..^2], out value);
        }

        if (text.EndsWith("u16", StringComparison.Ordinal))
        {
            bits = 16;
            return ulong.TryParse(text[..^3], out value);
        }

        if (text.EndsWith("u32", StringComparison.Ordinal))
        {
            bits = 32;
            return ulong.TryParse(text[..^3], out value);
        }

        if (text.EndsWith("u64", StringComparison.Ordinal))
        {
            bits = 64;
            return ulong.TryParse(text[..^3], out value);
        }

        return false;
    }

    private Expr ParseFloat()
    {
        var t = Consume(TokenKind.Float);
        return RegisterExpr(new Expr.FloatLit(t.FloatValue, t.Text), t.Position, t.End);
    }

    private Expr ParseString()
    {
        var t = Consume(TokenKind.String);
        return RegisterExpr(new Expr.StrLit(t.Text), t.Position, t.End);
    }

    private Expr ParseBool(bool value)
    {
        var token = Consume(value ? TokenKind.True : TokenKind.False);
        return RegisterExpr(new Expr.BoolLit(value), token.Position, token.End);
    }

    private Expr ParseVar()
    {
        var t = Consume(TokenKind.Ident);
        // Check for qualified name: Module.name or Module.Sub.name
        if (_current.Kind == TokenKind.Dot)
        {
            var parts = new List<string> { t.Text };
            while (_current.Kind == TokenKind.Dot)
            {
                Consume(TokenKind.Dot);
                parts.Add(Consume(TokenKind.Ident).Text);
            }
            // Last part is the member name, everything before is the module path
            var name = parts[^1];
            var module = string.Join('.', parts.Take(parts.Count - 1));
            return RegisterExpr(new Expr.QualifiedVar(module, name), t.Position, LastConsumedEnd);
        }

        // The brace record construction form (`TypeName { field = expr, ... }`) has been removed in
        // favour of named-argument call syntax (`TypeName(field = expr, ...)`).
        if (_current.Kind == TokenKind.LBrace && t.Text.Length > 0 && char.IsUpper(t.Text[0]))
        {
            _diag.Error(CurrentErrorSpan(), "Brace record construction has been removed; use 'Name(field = value)'.");
            SkipBalancedBraces();
            return RegisterExpr(new Expr.Var(t.Text), t.Position, LastConsumedEnd);
        }

        return RegisterExpr(new Expr.Var(t.Text), t.Position, t.End);
    }

    /// <summary>
    /// Reports the removal of the brace record update form (<c>{ expr with field = e }</c>) and
    /// recovers by skipping the balanced brace block. Use <c>expr with field = e</c> instead.
    /// </summary>
    private Expr ParseRemovedBraceUpdate()
    {
        var start = _current.Position;
        _diag.Error(CurrentErrorSpan(), "Brace record update has been removed; use 'base with field = value'.");
        SkipBalancedBraces();
        return RegisterExpr(new Expr.IntLit(0), start, LastConsumedEnd);
    }

    private Expr ParseParen()
    {
        var start = _current.Position;
        Consume(TokenKind.LParen);
        var suppressedMatchCasePipeDepth = _matchCasePipeSuppressionDepth;
        _matchCasePipeSuppressionDepth = 0;
        var suppressedWithDepth = _withSuppressionDepth;
        _withSuppressionDepth = 0;
        try
        {
            var e = ParseParenthesizedBody();
            if (_current.Kind == TokenKind.Comma)
            {
                var elements = new List<Expr> { e };
                while (_current.Kind == TokenKind.Comma)
                {
                    Consume(TokenKind.Comma);
                    elements.Add(ParseExpressionCore());
                }
                Consume(TokenKind.RParen);
                return RegisterExpr(new Expr.TupleLit(elements), start, LastConsumedEnd);
            }
            Consume(TokenKind.RParen);
            return e;
        }
        finally
        {
            _matchCasePipeSuppressionDepth = suppressedMatchCasePipeDepth;
            _withSuppressionDepth = suppressedWithDepth;
        }
    }

    /// <summary>
    /// Parses the body of a parenthesized expression, which may be a flat-declaration block: a
    /// sequence of <c>let [rec] name = value</c> declarations (no <c>in</c>) followed by a trailing
    /// expression, folded into nested <c>let</c> expressions. The combined-source stitcher emits the
    /// flat top-level entry expression in exactly this form (<c>(decl decl ... trailingExpr)</c>) when
    /// the program imports a flat module, so parentheses must accept it just as the file top level
    /// does. A binding terminated by <c>in</c> remains the ordinary nested <c>let ... in</c>
    /// expression; one terminated by the next declaration or the trailing expression (the same
    /// column-based boundary rule as the top level, via <see cref="StartsNextTopLevelItem"/>) is a
    /// flat declaration whose body is the remainder of the block. Non-<c>let</c> content falls
    /// straight through to the ordinary expression parser, so this is a superset of the previous
    /// behavior.
    /// </summary>
    private Expr ParseParenthesizedBody()
    {
        // Only a plain `let` declaration can begin a flat block. A let-pattern binding
        // (`let (a, b) = e in body`) is always an ordinary expression, as is any non-`let` content.
        if (_current.Kind != TokenKind.Let || IsLetPatternBinding())
        {
            return ParseExpressionCore();
        }

        // Parse the binding's header and value with the flat-declaration boundary active so a value
        // that is a (possibly qualified) application does not absorb the next declaration.
        var header = ParseLetHeaderAndValue(topLevel: true);

        // `let ... in ...` (and a `let rec ... and ...` group, which has no nested-expression form and
        // must report the missing `in`) is the ordinary nested-let expression.
        if (_current.Kind is TokenKind.In or TokenKind.And)
        {
            return FinishLetExpression(header);
        }

        // A flat declaration: its body is the remainder of the block.
        var body = ParseParenthesizedBody();
        if (header.IsRecursive)
        {
            var letRec = RegisterExpr(new Expr.LetRec(header.Name, header.Value, body) { SugarParams = header.SugarParams, TypeAnnotation = header.TypeAnnotation }, header.Start, LastConsumedEnd);
            AstSpans.SetLetRecName(letRec, header.NameToken.Span);
            return letRec;
        }

        var letExpr = RegisterExpr(new Expr.Let(header.Name, header.Value, body) { SugarParams = header.SugarParams, TypeAnnotation = header.TypeAnnotation }, header.Start, LastConsumedEnd);
        AstSpans.SetLetName(letExpr, header.NameToken.Span);
        return letExpr;
    }

    private Expr ParseList()
    {
        var start = _current.Position;
        Consume(TokenKind.LBracket);
        var elements = new List<Expr>();
        if (_current.Kind != TokenKind.RBracket)
        {
            elements.Add(ParseExpressionCore());
            while (_current.Kind == TokenKind.Comma)
            {
                Consume(TokenKind.Comma);
                elements.Add(ParseExpressionCore());
            }
        }
        Consume(TokenKind.RBracket);
        return RegisterExpr(new Expr.ListLit(elements), start, LastConsumedEnd);
    }

    private Pattern ParsePattern()
    {
        return ParsePatternCons();
    }

    private Pattern ParsePatternCons()
    {
        var head = ParsePatternPrimary();
        if (_current.Kind != TokenKind.ColonColon)
        {
            return head;
        }

        var start = AstSpans.GetOrDefault(head).Start;
        Consume(TokenKind.ColonColon);
        var tail = ParsePatternCons();
        return RegisterPattern(new Pattern.Cons(head, tail), start, AstSpans.GetOrDefault(tail).End);
    }

    private Pattern ParsePatternPrimary()
    {
        return _current.Kind switch
        {
            TokenKind.LBracket => ParseEmptyListPattern(),
            TokenKind.Ident => ParseVarPattern(),
            TokenKind.LParen => ParseParenPattern(),
            TokenKind.Int => ParseIntLitPattern(),
            TokenKind.String => ParseStrLitPattern(),
            TokenKind.True => ParseBoolLitPattern(true),
            TokenKind.False => ParseBoolLitPattern(false),
            TokenKind.Minus => ParseNegativeIntLitPattern(),
            _ => BadPattern(),
        };
    }

    private Pattern ParseEmptyListPattern()
    {
        var start = _current.Position;
        Consume(TokenKind.LBracket);
        Consume(TokenKind.RBracket);
        return RegisterPattern(new Pattern.EmptyList(), start, LastConsumedEnd);
    }

    private Pattern ParseVarPattern()
    {
        var token = Consume(TokenKind.Ident);
        var name = token.Text;
        if (name == "_")
        {
            return RegisterPattern(new Pattern.Wildcard(), token.Position, token.End);
        }

        if (_current.Kind == TokenKind.LParen)
        {
            Consume(TokenKind.LParen);
            var patterns = new List<Pattern>();
            if (_current.Kind != TokenKind.RParen)
            {
                patterns.Add(ParsePattern());
                while (_current.Kind == TokenKind.Comma)
                {
                    Consume(TokenKind.Comma);
                    patterns.Add(ParsePattern());
                }
            }
            Consume(TokenKind.RParen);
            return RegisterPattern(new Pattern.Constructor(name, patterns), token.Position, LastConsumedEnd);
        }

        return RegisterPattern(new Pattern.Var(name), token.Position, token.End);
    }

    private Pattern ParseParenPattern()
    {
        var start = _current.Position;
        Consume(TokenKind.LParen);
        var p = ParsePattern();
        if (_current.Kind == TokenKind.Comma)
        {
            var elements = new List<Pattern> { p };
            while (_current.Kind == TokenKind.Comma)
            {
                Consume(TokenKind.Comma);
                elements.Add(ParsePattern());
            }
            Consume(TokenKind.RParen);
            return RegisterPattern(new Pattern.Tuple(elements), start, LastConsumedEnd);
        }
        Consume(TokenKind.RParen);
        return p;
    }

    private Pattern ParseIntLitPattern()
    {
        var token = Consume(TokenKind.Int);
        return RegisterPattern(new Pattern.IntLit(token.IntValue), token.Position, token.End);
    }

    private Pattern ParseNegativeIntLitPattern()
    {
        var start = _current.Position;
        Consume(TokenKind.Minus);
        if (_current.Kind != TokenKind.Int)
        {
            return BadPattern();
        }
        var token = Consume(TokenKind.Int);
        return RegisterPattern(new Pattern.IntLit(-token.IntValue), start, token.End);
    }

    private Pattern ParseStrLitPattern()
    {
        var token = Consume(TokenKind.String);
        return RegisterPattern(new Pattern.StrLit(token.Text), token.Position, token.End);
    }

    private Pattern ParseBoolLitPattern(bool value)
    {
        var token = Consume(value ? TokenKind.True : TokenKind.False);
        return RegisterPattern(new Pattern.BoolLit(value), token.Position, token.End);
    }

    private Pattern BadPattern()
    {
        var span = CurrentErrorSpan();
        _diag.Error(span, $"Expected pattern but found {_current.Kind}.", DiagnosticCodes.ParseError);
        Advance();
        return RegisterPattern(new Pattern.Wildcard(), span.Start, span.End);
    }

    private Expr BadPrimary()
    {
        var span = CurrentErrorSpan();
        _diag.Error(span, $"Expected expression but found {_current.Kind}.", DiagnosticCodes.ParseError);
        Advance();
        return RegisterExpr(new Expr.IntLit(0), span.Start, span.End);
    }

    private Token Consume(TokenKind expected)
    {
        if (_current.Kind == expected)
        {
            return Advance();
        }

        _diag.Error(CurrentErrorSpan(), $"Expected {expected} but found {_current.Kind}.", DiagnosticCodes.ParseError);
        return new Token(expected, "", 0, _current.Position, 0);
    }

    private void EnsureEndOfInput()
    {
        if (_current.Kind != TokenKind.EOF)
        {
            _diag.Error(CurrentErrorSpan(), $"Unexpected token after end of expression: {_current.Kind}.", DiagnosticCodes.ParseError);
        }
    }

    private Token Advance()
    {
        var token = _current;
        _previous = token;
        _current = _lexer.Next();
        return token;
    }

    private int LastConsumedEnd => _previous.End;

    private TextSpan CurrentErrorSpan()
    {
        return _current.Kind == TokenKind.EOF
            ? TextSpan.FromBounds(_current.Position, _current.Position)
            : _current.Span;
    }

    private static T RegisterExpr<T>(T expr, int start, int end)
        where T : Expr
    {
        AstSpans.Set(expr, TextSpan.FromBounds(start, end));
        return expr;
    }

    private static T RegisterPattern<T>(T pattern, int start, int end)
        where T : Pattern
    {
        AstSpans.Set(pattern, TextSpan.FromBounds(start, end));
        return pattern;
    }

    private static TypeDecl RegisterTypeDecl(TypeDecl typeDecl, int start, int end)
    {
        AstSpans.Set(typeDecl, TextSpan.FromBounds(start, end));
        return typeDecl;
    }

    private static TypeConstructor RegisterTypeConstructor(TypeConstructor typeConstructor, int start, int end)
    {
        AstSpans.Set(typeConstructor, TextSpan.FromBounds(start, end));
        return typeConstructor;
    }

    private static ExternDecl RegisterExternDecl(ExternDecl externDecl, int start, int end)
    {
        AstSpans.Set(externDecl, TextSpan.FromBounds(start, end));
        return externDecl;
    }
}
