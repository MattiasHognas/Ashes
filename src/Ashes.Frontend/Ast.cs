namespace Ashes.Frontend;

public abstract record Expr
{
    public sealed record IntLit(long Value) : Expr;
    public sealed record UIntLit(ulong Value, int Bits) : Expr;
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
    public sealed record BitwiseAnd(Expr Left, Expr Right) : Expr;
    public sealed record BitwiseOr(Expr Left, Expr Right) : Expr;
    public sealed record BitwiseXor(Expr Left, Expr Right) : Expr;
    public sealed record ShiftLeft(Expr Left, Expr Right) : Expr;
    public sealed record ShiftRight(Expr Left, Expr Right) : Expr;
    public sealed record BitwiseNot(Expr Operand) : Expr;
    public sealed record GreaterThan(Expr Left, Expr Right) : Expr;
    public sealed record LessThan(Expr Left, Expr Right) : Expr;
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
        /// <summary>Optional user-supplied type annotation, e.g. <c>let x : Int = 42</c> or <c>let f : Int -> Int = ...</c>.</summary>
        public TypeExpr? TypeAnnotation { get; init; }
    }
    public sealed record LetResult(string Name, Expr Value, Expr Body) : Expr;
    public sealed record LetRec(string Name, Expr Value, Expr Body) : Expr
    {
        /// <summary>ML-style sugar parameters. When non-empty, the formatter prints <c>let rec f x y = ...</c> instead of <c>let rec f = fun (x) -> fun (y) -> ...</c>.</summary>
        public IReadOnlyList<string> SugarParams { get; init; } = [];
        /// <summary>Optional user-supplied type annotation: <c>let rec f : Int -> Int = ...</c>.</summary>
        public TypeExpr? TypeAnnotation { get; init; }
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

    /// <summary>Record literal: <c>TypeName { field1 = e1, field2 = e2 }</c>.</summary>
    public sealed record RecordLit(string TypeName, IReadOnlyList<(string Name, Expr Value)> Fields) : Expr;

    /// <summary>Record update: <c>{ expr with field1 = e1, field2 = e2 }</c>.</summary>
    public sealed record RecordUpdate(Expr Target, IReadOnlyList<(string Name, Expr Value)> Updates) : Expr;
}

public readonly record struct MatchCase(Pattern Pattern, Expr Body, Expr? Guard = null);

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

public sealed record TypeConstructor(string Name, IReadOnlyList<string> Parameters)
{
    /// <summary>For record types: the named field identifiers corresponding to each parameter position. Empty for regular ADT constructors.</summary>
    public IReadOnlyList<string> FieldNames { get; init; } = [];
}

public sealed record TypeDecl(string Name, IReadOnlyList<TypeParameter> TypeParameters, IReadOnlyList<TypeConstructor> Constructors)
{
    /// <summary>True when this was declared with record syntax: <c>type T = { field: Type, ... }</c>.</summary>
    public bool IsRecord { get; init; }
}

public abstract record ParsedType
{
    public sealed record Named(string Name) : ParsedType;
    public sealed record Pointer(ParsedType Pointee) : ParsedType;
}

/// <summary>A type expression written by the user in an annotation, e.g. <c>Int -> Str</c>.</summary>
public abstract record TypeExpr
{
    /// <summary>A simple named type or type parameter: <c>Int</c>, <c>Str</c>, <c>T</c>.</summary>
    public sealed record Named(string Name) : TypeExpr;
    /// <summary>A parameterised type application: <c>List(Int)</c>, <c>Result(Str, Int)</c>.</summary>
    public sealed record Applied(string Name, IReadOnlyList<TypeExpr> Args) : TypeExpr;
    /// <summary>A function type: <c>Int -> Str</c>.</summary>
    public sealed record Arrow(TypeExpr From, TypeExpr To) : TypeExpr;
    /// <summary>A tuple type: <c>(Int, Str)</c>.</summary>
    public sealed record TupleType(IReadOnlyList<TypeExpr> Elements) : TypeExpr;
    /// <summary>Unit written as an empty tuple: <c>()</c>.</summary>
    public sealed record UnitType : TypeExpr;
}

public abstract record ExternDecl
{
    public sealed record OpaqueType(string Name) : ExternDecl;

    public sealed record Function(
        string Name,
        IReadOnlyList<ParsedType> ParameterTypes,
        ParsedType ReturnType,
        string? SymbolName = null) : ExternDecl;
}

/// <summary>
/// A single top-level item, kept in source order. Model-A sequential scoping makes the order
/// significant: a binding is visible only to the items that follow it (and the trailing body).
/// </summary>
public abstract record TopLevelItem
{
    /// <summary>A top-level <c>type</c> declaration.</summary>
    public sealed record Type(TypeDecl Decl) : TopLevelItem;

    /// <summary>A top-level <c>extern</c> declaration.</summary>
    public sealed record Extern(ExternDecl Decl) : TopLevelItem;

    /// <summary>A top-level value binding: <c>let Name = Value</c>, or <c>let rec</c> when <see cref="IsRecursive"/>.</summary>
    public sealed record LetDecl(string Name, Expr Value, bool IsRecursive) : TopLevelItem
    {
        /// <summary>
        /// ML-style sugar parameters. When non-empty, the formatter prints <c>let f x y = ...</c>
        /// instead of <c>let f = fun (x) -> fun (y) -> ...</c>. Codegen is unaffected: the value is
        /// already the desugared nested-lambda form regardless of this list.
        /// </summary>
        public IReadOnlyList<string> SugarParams { get; init; } = [];
    }

    /// <summary>
    /// A mutual-recursion group: <c>let rec A = ... and B = ...</c>. Every binding is implicitly
    /// recursive within the group and visible to the others regardless of order.
    /// </summary>
    public sealed record RecGroup(IReadOnlyList<(string Name, Expr Value)> Bindings) : TopLevelItem
    {
        /// <summary>
        /// ML-style sugar parameters per binding, parallel to <see cref="Bindings"/> by index. An
        /// entry that is absent or empty renders without sugar. Kept as a side list (rather than
        /// widening the binding tuple) so the desugared value stays the single source of truth for
        /// lowering, which consumes <see cref="Bindings"/> directly.
        /// </summary>
        public IReadOnlyList<IReadOnlyList<string>> SugarParams { get; init; } = [];
    }
}

/// <summary>
/// A whole compilation unit: an ordered sequence of top-level items followed by an optional
/// trailing expression.
/// </summary>
public sealed record Program
{
    /// <summary>The top-level items in source order (significant under Model-A scoping).</summary>
    public IReadOnlyList<TopLevelItem> Items { get; init; }

    /// <summary>
    /// The trailing expression, if any. A program may omit it (e.g. a module that only declares
    /// bindings), represented by a null backing value. It is surfaced with a non-null type so the
    /// current consumers — all of which run on programs that do have a trailing expression — keep
    /// compiling without change; teaching them to tolerate an absent body is follow-up work that
    /// flips this to <c>Expr?</c>.
    /// </summary>
    public Expr Body { get; init; }

    public Program(IReadOnlyList<TopLevelItem> Items, Expr? Body)
    {
        this.Items = Items;
        this.Body = Body!;
    }

    /// <summary>Back-compat constructor: separated type/extern declarations plus a trailing expression.</summary>
    public Program(IReadOnlyList<TypeDecl> TypeDecls, IReadOnlyList<ExternDecl> ExternDecls, Expr Body)
        : this(BuildItems(TypeDecls, ExternDecls), Body)
    {
    }

    /// <summary>Back-compat constructor: type declarations plus a trailing expression.</summary>
    public Program(IReadOnlyList<TypeDecl> TypeDecls, Expr Body)
        : this(TypeDecls, [], Body)
    {
    }

    /// <summary>The <c>type</c> declarations in source order, derived from <see cref="Items"/>.</summary>
    public IReadOnlyList<TypeDecl> TypeDecls =>
        Items.OfType<TopLevelItem.Type>().Select(item => item.Decl).ToList();

    /// <summary>The <c>extern</c> declarations in source order, derived from <see cref="Items"/>.</summary>
    public IReadOnlyList<ExternDecl> ExternDecls =>
        Items.OfType<TopLevelItem.Extern>().Select(item => item.Decl).ToList();

    private static IReadOnlyList<TopLevelItem> BuildItems(
        IReadOnlyList<TypeDecl> typeDecls,
        IReadOnlyList<ExternDecl> externDecls)
    {
        var items = new List<TopLevelItem>(typeDecls.Count + externDecls.Count);
        foreach (var typeDecl in typeDecls)
        {
            items.Add(new TopLevelItem.Type(typeDecl));
        }

        foreach (var externDecl in externDecls)
        {
            items.Add(new TopLevelItem.Extern(externDecl));
        }

        return items;
    }
}
