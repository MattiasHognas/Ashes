namespace Ashes.Frontend;

public abstract record Expr
{
    public sealed record IntLit(long Value) : Expr;
    public sealed record FloatLit(double Value, string Text) : Expr
    {
        public FloatLit(double value) : this(value, "")
        {
        }
    }

    public sealed record StrLit(string Value) : Expr;
    public sealed record BoolLit(bool Value) : Expr;

    public sealed record Var(string Name) : Expr;
    public sealed record QualifiedVar(string Module, string Name) : Expr;
    public sealed record Add(Expr Left, Expr Right) : Expr;
    public sealed record Subtract(Expr Left, Expr Right) : Expr;
    public sealed record Multiply(Expr Left, Expr Right) : Expr;
    public sealed record Divide(Expr Left, Expr Right) : Expr;
    public sealed record GreaterOrEqual(Expr Left, Expr Right) : Expr;
    public sealed record LessOrEqual(Expr Left, Expr Right) : Expr;
    public sealed record Equal(Expr Left, Expr Right) : Expr;
    public sealed record NotEqual(Expr Left, Expr Right) : Expr;
    public sealed record ResultPipe(Expr Left, Expr Right) : Expr;
    public sealed record ResultMapErrorPipe(Expr Left, Expr Right) : Expr;

    public sealed record Let(string Name, Expr Value, Expr Body) : Expr
    {
        /// <summary>ML-style sugar parameters. When non-empty, the formatter prints <c>let f x y = ...</c> instead of <c>let f = fun (x) -> fun (y) -> ...</c>.</summary>
        public IReadOnlyList<string> SugarParams { get; init; } = [];
    }
    public sealed record LetResult(string Name, Expr Value, Expr Body) : Expr;
    public sealed record LetRec(string Name, Expr Value, Expr Body) : Expr
    {
        /// <summary>ML-style sugar parameters. When non-empty, the formatter prints <c>let rec f x y = ...</c> instead of <c>let rec f = fun (x) -> fun (y) -> ...</c>.</summary>
        public IReadOnlyList<string> SugarParams { get; init; } = [];
    }

    public sealed record If(Expr Cond, Expr Then, Expr Else) : Expr;

    public sealed record Lambda(string ParamName, Expr Body) : Expr; // fun (x) -> expr
    public sealed record Call(Expr Func, Expr Arg) : Expr           // f(x) or f x
    {
        /// <summary>When true, the formatter prints <c>f x</c> instead of <c>f(x)</c>.</summary>
        public bool IsWhitespaceApplication { get; init; }
    }
    public sealed record TupleLit(IReadOnlyList<Expr> Elements) : Expr;
    public sealed record ListLit(IReadOnlyList<Expr> Elements) : Expr;
    public sealed record Cons(Expr Head, Expr Tail) : Expr;
    public sealed record Match(Expr Value, IReadOnlyList<MatchCase> Cases, int? Pos = null) : Expr;

    public sealed record Async(Expr Body) : Expr;
    public sealed record Await(Expr Task) : Expr;

}

public readonly record struct MatchCase(Pattern Pattern, Expr Body);

public abstract record Pattern
{
    public sealed record EmptyList : Pattern;
    public sealed record Var(string Name) : Pattern;
    public sealed record Wildcard : Pattern;
    public sealed record Cons(Pattern Head, Pattern Tail) : Pattern;
    public sealed record Tuple(IReadOnlyList<Pattern> Elements) : Pattern;
    public sealed record Constructor(string Name, IReadOnlyList<Pattern> Patterns) : Pattern;
    public sealed record IntLit(long Value) : Pattern;
    public sealed record StrLit(string Value) : Pattern;
    public sealed record BoolLit(bool Value) : Pattern;
}

public sealed record TypeParameter(string Name);

public sealed record TypeConstructor(string Name, IReadOnlyList<string> Parameters);

public sealed record TypeDecl(string Name, IReadOnlyList<TypeParameter> TypeParameters, IReadOnlyList<TypeConstructor> Constructors);

public sealed record Program(IReadOnlyList<TypeDecl> TypeDecls, Expr Body);
