namespace Ashes.Frontend;

public sealed class Parser
{
    private readonly Lexer _lexer;
    private readonly Diagnostics _diag;
    private Token _current;
    private Token _previous;

    public Parser(string text, Diagnostics diag)
    {
        _diag = diag;
        _lexer = new Lexer(text, diag);
        _previous = new Token(TokenKind.EOF, "", 0, 0, 0);
        _current = _lexer.Next();
    }

    public Expr ParseExpression()
    {
        var expr = ParseExpressionCore();
        EnsureEndOfInput();
        return expr;
    }

    public Program ParseProgram()
    {
        var typeDecls = new List<TypeDecl>();
        while (_current.Kind == TokenKind.Type)
        {
            typeDecls.Add(ParseTypeDecl());
        }
        var body = ParseExpressionCore();
        EnsureEndOfInput();
        return new Program(typeDecls, body);
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

        var constructors = new List<TypeConstructor>();
        while (_current.Kind == TokenKind.Pipe)
        {
            Consume(TokenKind.Pipe);
            var ctorStart = _previous.Position;
            var ctorName = Consume(TokenKind.Ident).Text;
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
            constructors.Add(RegisterTypeConstructor(new TypeConstructor(ctorName, parameters), ctorStart, LastConsumedEnd));
        }

        if (constructors.Count == 0)
        {
            _diag.Error(CurrentErrorSpan(), $"Type '{name}' must have at least one constructor.");
        }

        return RegisterTypeDecl(new TypeDecl(name, typeParameters, constructors), start, LastConsumedEnd);
    }

    private Expr ParseMatch()
    {
        if (_current.Kind != TokenKind.Match)
        {
            return ParseIf();
        }

        var matchPos = _current.Position;
        Consume(TokenKind.Match);
        var value = ParseExpressionCore();
        Consume(TokenKind.With);

        var cases = new List<MatchCase>();
        if (_current.Kind == TokenKind.Pipe)
        {
            Consume(TokenKind.Pipe);
        }

        var pattern = ParsePattern();
        Consume(TokenKind.Arrow);
        var body = ParseExpressionCore();
        cases.Add(new MatchCase(pattern, body));

        while (_current.Kind == TokenKind.Pipe)
        {
            Consume(TokenKind.Pipe);
            pattern = ParsePattern();
            Consume(TokenKind.Arrow);
            body = ParseExpressionCore();
            cases.Add(new MatchCase(pattern, body));
        }

        return RegisterExpr(new Expr.Match(value, cases, matchPos), matchPos, LastConsumedEnd);
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

            Consume(TokenKind.Let);
            var isRec = _current.Kind == TokenKind.Rec;
            if (isRec)
            {
                Consume(TokenKind.Rec);
            }

            var nameToken = Consume(TokenKind.Ident);
            var name = nameToken.Text;

            // ML-style function sugar: let f x y = body => let f = fun (x) -> fun (y) -> body
            var sugarParams = new List<string>();
            var sugarParamTokens = new List<Token>();
            while (_current.Kind == TokenKind.Ident)
            {
                var paramToken = Consume(TokenKind.Ident);
                sugarParams.Add(paramToken.Text);
                sugarParamTokens.Add(paramToken);
            }

            Consume(TokenKind.Equals);
            var value = ParseExpressionCore();

            // Desugar ML-style parameters into nested lambdas
            for (int i = sugarParams.Count - 1; i >= 0; i--)
            {
                var lambda = RegisterExpr(new Expr.Lambda(sugarParams[i], value), start, AstSpans.GetOrDefault(value).End);
                AstSpans.SetLambdaParameter(lambda, sugarParamTokens[i].Span);
                value = lambda;
            }

            Consume(TokenKind.In);
            var body = ParseExpressionCore();
            if (isRec)
            {
                var letRec = RegisterExpr(new Expr.LetRec(name, value, body) { SugarParams = sugarParams }, start, LastConsumedEnd);
                AstSpans.SetLetRecName(letRec, nameToken.Span);
                return letRec;
            }

            var letExpr = RegisterExpr(new Expr.Let(name, value, body) { SugarParams = sugarParams }, start, LastConsumedEnd);
            AstSpans.SetLetName(letExpr, nameToken.Span);
            return letExpr;
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

        return ParseLambda();
    }

    /// <summary>
    /// Detects whether the current position is a let-pattern binding.
    /// Returns true for <c>let (</c> which indicates a tuple destructuring let.
    /// </summary>
    private bool IsLetPatternBinding()
    {
        // The Lexer has already consumed tokens up to _current.
        // We need to peek past 'let' to see if a pattern follows.
        // 'let (' → tuple pattern. 'let rec' or 'let ident' → normal let.
        if (_current.Kind != TokenKind.Let) return false;

        // Save state — the lexer is forward-only, so we use a simple
        // single-token lookahead by examining the lexer's next output.
        var savedCurrent = _current;
        var savedPrevious = _previous;
        var savedLexer = _lexer.SavePosition();
        Advance(); // consume 'let'

        var isPattern = _current.Kind == TokenKind.LParen;

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
    /// Per LANGUAGE_SPEC.md §11.8 the irrefutable patterns are:
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

        return ParsePipe();
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
        var left = ParseCons();

        while (_current.Kind == TokenKind.GreaterEquals || _current.Kind == TokenKind.LessEquals || _current.Kind == TokenKind.EqualsEquals || _current.Kind == TokenKind.BangEquals)
        {
            var start = AstSpans.GetOrDefault(left).Start;
            var op = _current.Kind;
            Consume(op);
            var right = ParseCons();
            left = op switch
            {
                TokenKind.GreaterEquals => RegisterExpr(new Expr.GreaterOrEqual(left, right), start, AstSpans.GetOrDefault(right).End),
                TokenKind.LessEquals => RegisterExpr(new Expr.LessOrEqual(left, right), start, AstSpans.GetOrDefault(right).End),
                TokenKind.EqualsEquals => RegisterExpr(new Expr.Equal(left, right), start, AstSpans.GetOrDefault(right).End),
                TokenKind.BangEquals => RegisterExpr(new Expr.NotEqual(left, right), start, AstSpans.GetOrDefault(right).End),
                _ => throw new InvalidOperationException()
            };
        }

        return left;
    }

    private Expr ParseCons()
    {
        var left = ParseAdditive();
        if (_current.Kind != TokenKind.ColonColon)
        {
            return left;
        }

        var start = AstSpans.GetOrDefault(left).Start;
        Consume(TokenKind.ColonColon);
        var right = ParseCons();
        return RegisterExpr(new Expr.Cons(left, right), start, AstSpans.GetOrDefault(right).End);
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

            if (IsWhitespaceArgStarter(_current.Kind))
            {
                var start = AstSpans.GetOrDefault(expr).Start;
                var arg = ParsePrimary();
                expr = RegisterExpr(new Expr.Call(expr, arg) { IsWhitespaceApplication = true }, start, AstSpans.GetOrDefault(arg).End);
                continue;
            }

            break;
        }

        return expr;
    }

    private static bool IsWhitespaceArgStarter(TokenKind kind)
    {
        return kind is TokenKind.Ident or TokenKind.Int or TokenKind.Float or TokenKind.String
            or TokenKind.True or TokenKind.False or TokenKind.LBracket;
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
            _ => BadPrimary(),
        };
    }

    private Expr ParseInt()
    {
        var t = Consume(TokenKind.Int);
        return RegisterExpr(new Expr.IntLit(t.IntValue), t.Position, t.End);
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
        return RegisterExpr(new Expr.Var(t.Text), t.Position, t.End);
    }

    private Expr ParseParen()
    {
        var start = _current.Position;
        Consume(TokenKind.LParen);
        var e = ParseExpressionCore();
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
}
