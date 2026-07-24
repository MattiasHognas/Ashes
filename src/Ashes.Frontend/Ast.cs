namespace Ashes.Frontend;

/// <summary>
/// Base of the expression AST. Every syntactic expression form is a nested sealed record; the parser
/// builds a tree of these, and later phases pattern-match over the concrete cases.
/// </summary>
public abstract record Expr
{
    /// <summary>A signed integer literal.</summary>
    /// <param name="Value">The literal's value.</param>
    public sealed record IntLit(long Value) : Expr;
    /// <summary>An arbitrary-precision integer literal (an <c>N</c>-suffixed number).</summary>
    /// <param name="Digits">The decimal digits as written in source.</param>
    public sealed record BigIntLit(string Digits) : Expr;
    /// <summary>An unsigned fixed-width integer literal (e.g. <c>255u8</c>).</summary>
    /// <param name="Value">The literal's value.</param>
    /// <param name="Bits">The declared width in bits (8, 16, 32, or 64).</param>
    public sealed record UIntLit(ulong Value, int Bits) : Expr;
    /// <summary>A floating-point literal, carrying both its numeric value and the source text so the
    /// formatter can round-trip the exact written form.</summary>
    /// <param name="Value">The parsed numeric value.</param>
    /// <param name="Text">The original source text, or empty when synthesized.</param>
    public sealed record FloatLit(double Value, string Text) : Expr
    {
        /// <summary>Creates a float literal with no preserved source text.</summary>
        public FloatLit(double value) : this(value, "")
        {
        }
    }

    /// <summary>A string literal.</summary>
    /// <param name="Value">The decoded string value (escape sequences already resolved).</param>
    public sealed record StrLit(string Value) : Expr;
    /// <summary>A boolean literal.</summary>
    /// <param name="Value">The literal's value.</param>
    public sealed record BoolLit(bool Value) : Expr;

    /// <summary>A reference to an unqualified name.</summary>
    /// <param name="Name">The referenced identifier.</param>
    public sealed record Var(string Name) : Expr;
    /// <summary>A module-qualified name reference, <c>Module.Name</c>.</summary>
    /// <param name="Module">The module qualifier.</param>
    /// <param name="Name">The member name within the module.</param>
    public sealed record QualifiedVar(string Module, string Name) : Expr;
    /// <summary>Integer/float addition, <c>Left + Right</c>.</summary>
    /// <param name="Left">The left operand.</param>
    /// <param name="Right">The right operand.</param>
    public sealed record Add(Expr Left, Expr Right) : Expr;
    /// <summary>Subtraction, <c>Left - Right</c> (also the desugared form of unary minus).</summary>
    /// <param name="Left">The left operand.</param>
    /// <param name="Right">The right operand.</param>
    public sealed record Subtract(Expr Left, Expr Right) : Expr;
    /// <summary>Multiplication, <c>Left * Right</c>.</summary>
    /// <param name="Left">The left operand.</param>
    /// <param name="Right">The right operand.</param>
    public sealed record Multiply(Expr Left, Expr Right) : Expr;
    /// <summary>Division, <c>Left / Right</c>.</summary>
    /// <param name="Left">The left operand.</param>
    /// <param name="Right">The right operand.</param>
    public sealed record Divide(Expr Left, Expr Right) : Expr;
    /// <summary>Modulo, <c>Left % Right</c>.</summary>
    /// <param name="Left">The left operand.</param>
    /// <param name="Right">The right operand.</param>
    public sealed record Modulo(Expr Left, Expr Right) : Expr;
    /// <summary>Bitwise and, <c>Left &amp; Right</c>.</summary>
    /// <param name="Left">The left operand.</param>
    /// <param name="Right">The right operand.</param>
    public sealed record BitwiseAnd(Expr Left, Expr Right) : Expr;
    /// <summary>Bitwise or, <c>Left | Right</c>.</summary>
    /// <param name="Left">The left operand.</param>
    /// <param name="Right">The right operand.</param>
    public sealed record BitwiseOr(Expr Left, Expr Right) : Expr;
    /// <summary>Bitwise xor, <c>Left ^ Right</c>.</summary>
    /// <param name="Left">The left operand.</param>
    /// <param name="Right">The right operand.</param>
    public sealed record BitwiseXor(Expr Left, Expr Right) : Expr;
    /// <summary>Left shift, <c>Left &lt;&lt; Right</c>.</summary>
    /// <param name="Left">The value being shifted.</param>
    /// <param name="Right">The shift amount.</param>
    public sealed record ShiftLeft(Expr Left, Expr Right) : Expr;
    /// <summary>Right shift, <c>Left &gt;&gt; Right</c>.</summary>
    /// <param name="Left">The value being shifted.</param>
    /// <param name="Right">The shift amount.</param>
    public sealed record ShiftRight(Expr Left, Expr Right) : Expr;
    /// <summary>Bitwise complement, <c>~Operand</c>.</summary>
    /// <param name="Operand">The value to complement.</param>
    public sealed record BitwiseNot(Expr Operand) : Expr;
    /// <summary>Greater-than comparison, <c>Left &gt; Right</c>.</summary>
    /// <param name="Left">The left operand.</param>
    /// <param name="Right">The right operand.</param>
    public sealed record GreaterThan(Expr Left, Expr Right) : Expr;
    /// <summary>Less-than comparison, <c>Left &lt; Right</c>.</summary>
    /// <param name="Left">The left operand.</param>
    /// <param name="Right">The right operand.</param>
    public sealed record LessThan(Expr Left, Expr Right) : Expr;
    /// <summary>Greater-or-equal comparison, <c>Left &gt;= Right</c>.</summary>
    /// <param name="Left">The left operand.</param>
    /// <param name="Right">The right operand.</param>
    public sealed record GreaterOrEqual(Expr Left, Expr Right) : Expr;
    /// <summary>Less-or-equal comparison, <c>Left &lt;= Right</c>.</summary>
    /// <param name="Left">The left operand.</param>
    /// <param name="Right">The right operand.</param>
    public sealed record LessOrEqual(Expr Left, Expr Right) : Expr;
    /// <summary>Equality comparison, <c>Left == Right</c>.</summary>
    /// <param name="Left">The left operand.</param>
    /// <param name="Right">The right operand.</param>
    public sealed record Equal(Expr Left, Expr Right) : Expr;
    /// <summary>Inequality comparison, <c>Left != Right</c>.</summary>
    /// <param name="Left">The left operand.</param>
    /// <param name="Right">The right operand.</param>
    public sealed record NotEqual(Expr Left, Expr Right) : Expr;
    /// <summary>Result-pipe, <c>Left |?&gt; Right</c>: threads the ok value of <paramref name="Left"/>
    /// into <paramref name="Right"/>, short-circuiting on error.</summary>
    /// <param name="Left">The result-producing left side.</param>
    /// <param name="Right">The function applied to the ok value.</param>
    public sealed record ResultPipe(Expr Left, Expr Right) : Expr;
    /// <summary>Result-map-error pipe, <c>Left |!&gt; Right</c>: maps the error value of
    /// <paramref name="Left"/> through <paramref name="Right"/>, passing ok values through.</summary>
    /// <param name="Left">The result-producing left side.</param>
    /// <param name="Right">The function applied to the error value.</param>
    public sealed record ResultMapErrorPipe(Expr Left, Expr Right) : Expr;

    /// <summary>A non-recursive binding, <c>let Name = Value in Body</c>.</summary>
    /// <param name="Name">The bound name.</param>
    /// <param name="Value">The bound value expression.</param>
    /// <param name="Body">The expression in which the binding is visible.</param>
    public sealed record Let(string Name, Expr Value, Expr Body) : Expr
    {
        /// <summary>ML-style sugar parameters. When non-empty, the formatter prints <c>let f x y = ...</c> instead of <c>let f = given (x) -> given (y) -> ...</c>.</summary>
        public IReadOnlyList<string> SugarParams { get; init; } = [];
        /// <summary>Optional user-supplied type annotation, e.g. <c>let x : Int = 42</c> or <c>let f : Int -> Int = ...</c>.</summary>
        public TypeExpr? TypeAnnotation { get; init; }
    }
    /// <summary>A result-unwrapping binding, <c>let? Name = Value in Body</c>: binds the ok value of
    /// <paramref name="Value"/>, short-circuiting the whole expression on error.</summary>
    /// <param name="Name">The bound name.</param>
    /// <param name="Value">The result-producing value expression.</param>
    /// <param name="Body">The expression in which the binding is visible.</param>
    public sealed record LetResult(string Name, Expr Value, Expr Body) : Expr;
    /// <summary>A self-recursive binding, <c>let recursive Name = Value in Body</c>, where
    /// <paramref name="Name"/> is in scope within its own <paramref name="Value"/>.</summary>
    /// <param name="Name">The bound name, visible inside <paramref name="Value"/>.</param>
    /// <param name="Value">The bound value expression.</param>
    /// <param name="Body">The expression in which the binding is visible.</param>
    public sealed record LetRecursive(string Name, Expr Value, Expr Body) : Expr
    {
        /// <summary>ML-style sugar parameters. When non-empty, the formatter prints <c>let rec f x y = ...</c> instead of <c>let rec f = given (x) -> given (y) -> ...</c>.</summary>
        public IReadOnlyList<string> SugarParams { get; init; } = [];
        /// <summary>Optional user-supplied type annotation: <c>let rec f : Int -> Int = ...</c>.</summary>
        public TypeExpr? TypeAnnotation { get; init; }
    }

    /// <summary>A conditional, <c>if Cond then Then else Else</c>.</summary>
    /// <param name="Cond">The boolean condition.</param>
    /// <param name="Then">The branch taken when <paramref name="Cond"/> is true.</param>
    /// <param name="Else">The branch taken when <paramref name="Cond"/> is false.</param>
    public sealed record If(Expr Cond, Expr Then, Expr Else) : Expr;

    /// <summary>A single-parameter lambda, <c>given (x) -> expr</c>. Multi-parameter sugar is desugared
    /// into nested lambdas during parsing.</summary>
    /// <param name="ParamName">The bound parameter name.</param>
    /// <param name="Body">The lambda body.</param>
    public sealed record Lambda(string ParamName, Expr Body) : Expr // given (x) -> expr
    {
        /// <summary>Inline parameter type annotation: <c>given (x: Type) -> expr</c> (also carried by
        /// the lambda a parenthesized annotated sugar parameter desugars to). Unifies with the
        /// parameter's inferred type during lowering.</summary>
        public TypeExpr? ParamAnnotation { get; init; }
    }
    /// <summary>Function application, <c>f(x)</c> or <c>f x</c>. Multi-argument calls are curried into
    /// nested <see cref="Call"/> nodes.</summary>
    /// <param name="Func">The function being applied.</param>
    /// <param name="Arg">The single argument.</param>
    public sealed record Call(Expr Func, Expr Arg) : Expr           // f(x) or f x
    {
        /// <summary>When true, the formatter prints <c>f x</c> instead of <c>f(x)</c>.</summary>
        public bool IsWhitespaceApplication { get; init; }
    }
    /// <summary>A tuple literal, <c>(a, b, ...)</c>.</summary>
    /// <param name="Elements">The tuple's element expressions in order.</param>
    public sealed record TupleLit(IReadOnlyList<Expr> Elements) : Expr;
    /// <summary>A list literal, <c>[a, b, ...]</c>.</summary>
    /// <param name="Elements">The list's element expressions in order.</param>
    public sealed record ListLit(IReadOnlyList<Expr> Elements) : Expr;
    /// <summary>A cons cell, <c>Head :: Tail</c>, prepending an element onto a list.</summary>
    /// <param name="Head">The element prepended.</param>
    /// <param name="Tail">The rest of the list.</param>
    public sealed record Cons(Expr Head, Expr Tail) : Expr;
    /// <summary>A pattern match, <c>match Value with | ... -> ...</c>.</summary>
    /// <param name="Value">The scrutinee being matched.</param>
    /// <param name="Cases">The match arms in source order.</param>
    /// <param name="Pos">The source offset of the <c>match</c> keyword, or null when synthesized.</param>
    public sealed record Match(Expr Value, IReadOnlyList<MatchCase> Cases, int? Pos = null) : Expr;

    /// <summary>An <c>await</c> expression that suspends until a task completes.</summary>
    /// <param name="Task">The task expression being awaited.</param>
    public sealed record Await(Expr Task) : Expr;

    /// <summary>Record literal: <c>TypeName { field1 = e1, field2 = e2 }</c>.</summary>
    public sealed record RecordLit(string TypeName, IReadOnlyList<(string Name, Expr Value)> Fields) : Expr;

    /// <summary>Record update: <c>{ expr with field1 = e1, field2 = e2 }</c>.</summary>
    public sealed record RecordUpdate(Expr Target, IReadOnlyList<(string Name, Expr Value)> Updates) : Expr;

    /// <summary>
    /// Explicit capability-operation marker: <c>perform Clock.now(x)</c>. The keyword is optional and
    /// changes nothing about the program — <see cref="Operation"/> is the operation call itself
    /// (typically a <see cref="Call"/> rooted at a <see cref="QualifiedVar"/>); the node exists so
    /// the formatter can preserve the written form.
    /// </summary>
    public sealed record Perform(Expr Operation) : Expr;

    /// <summary>
    /// Handler installation: <c>handle body with | Capability.op(args) -> arm | return(r) -> arm</c>.
    /// </summary>
    public sealed record Handle(Expr Body, IReadOnlyList<HandlerArm> Arms) : Expr;
}

/// <summary>
/// One arm of a <c>handle ... with</c> expression. An operation arm has a non-null
/// <see cref="CapabilityName"/> (<c>| Clock.now(_) -> ...</c>); the <c>return</c> arm has a null
/// <see cref="CapabilityName"/> and <see cref="OperationName"/> <c>"return"</c>.
/// </summary>
public sealed record HandlerArm(string? CapabilityName, string OperationName, IReadOnlyList<Pattern> Parameters, Expr Body);

/// <summary>One arm of a <see cref="Expr.Match"/>: a pattern, its result expression, and an optional
/// <c>when</c> guard.</summary>
/// <param name="Pattern">The pattern the scrutinee is tested against.</param>
/// <param name="Body">The expression evaluated when the pattern (and guard) match.</param>
/// <param name="Guard">An optional boolean guard that must also hold for the arm to be taken.</param>
public readonly record struct MatchCase(Pattern Pattern, Expr Body, Expr? Guard = null);

/// <summary>
/// Base of the pattern AST used in <c>match</c> arms and irrefutable <c>let</c> bindings. Each
/// syntactic pattern form is a nested sealed record.
/// </summary>
public abstract record Pattern
{
    /// <summary>The empty-list pattern, <c>[]</c>.</summary>
    public sealed record EmptyList : Pattern;
    /// <summary>A variable pattern that always matches and binds the value to a name.</summary>
    /// <param name="Name">The name bound to the matched value.</param>
    public sealed record Var(string Name) : Pattern;
    /// <summary>The wildcard pattern, <c>_</c>, that matches anything without binding.</summary>
    public sealed record Wildcard : Pattern;
    /// <summary>A cons pattern, <c>Head :: Tail</c>, matching a non-empty list.</summary>
    /// <param name="Head">Pattern for the first element.</param>
    /// <param name="Tail">Pattern for the remainder of the list.</param>
    public sealed record Cons(Pattern Head, Pattern Tail) : Pattern;
    /// <summary>A tuple pattern, <c>(p1, p2, ...)</c>.</summary>
    /// <param name="Elements">Patterns for each tuple position.</param>
    public sealed record Tuple(IReadOnlyList<Pattern> Elements) : Pattern;
    /// <summary>A constructor pattern, <c>Name(p1, ...)</c>, matching an ADT variant.</summary>
    /// <param name="Name">The constructor name.</param>
    /// <param name="Patterns">Patterns for the constructor's fields.</param>
    public sealed record Constructor(string Name, IReadOnlyList<Pattern> Patterns) : Pattern;
    /// <summary>An integer-literal pattern.</summary>
    /// <param name="Value">The value to match.</param>
    public sealed record IntLit(long Value) : Pattern;
    /// <summary>A string-literal pattern.</summary>
    /// <param name="Value">The value to match.</param>
    public sealed record StrLit(string Value) : Pattern;
    /// <summary>A boolean-literal pattern.</summary>
    /// <param name="Value">The value to match.</param>
    public sealed record BoolLit(bool Value) : Pattern;
}

/// <summary>A declared type parameter, e.g. the <c>a</c> in <c>type List(a) = ...</c>.</summary>
/// <param name="Name">The parameter's name.</param>
public sealed record TypeParameter(string Name);

/// <summary>One constructor of a <c>type</c> declaration (also the single synthetic constructor of a
/// record type).</summary>
/// <param name="Name">The constructor name.</param>
/// <param name="Parameters">The field type expressions, positionally.</param>
public sealed record TypeConstructor(string Name, IReadOnlyList<TypeExpr> Parameters)
{
    /// <summary>For record types: the named field identifiers corresponding to each parameter position. Empty for regular ADT constructors.</summary>
    public IReadOnlyList<string> FieldNames { get; init; } = [];
}

/// <summary>A <c>type</c> declaration: an algebraic data type or (when <see cref="IsRecord"/>) a
/// record type.</summary>
/// <param name="Name">The declared type's name.</param>
/// <param name="TypeParameters">The type's parameters, if any.</param>
/// <param name="Constructors">The type's constructors (a single one for a record type).</param>
public sealed record TypeDecl(string Name, IReadOnlyList<TypeParameter> TypeParameters, IReadOnlyList<TypeConstructor> Constructors)
{
    /// <summary>True when this was declared with record syntax: <c>type T = { field: Type, ... }</c>.</summary>
    public bool IsRecord { get; init; }
}

/// <summary>One operation of a <c>capability</c> declaration: <c>| now : Unit -> Int</c> or a bare <c>| log</c>.</summary>
public sealed record CapabilityOperation(string Name, TypeExpr? Signature);

/// <summary>A <c>capability</c> declaration: <c>capability Clock = | now : Unit -> Int</c>.</summary>
public sealed record CapabilityDecl(string Name, IReadOnlyList<TypeParameter> TypeParameters, IReadOnlyList<CapabilityOperation> Operations);

/// <summary>One operation implementation in a <c>provide</c>: <c>| compare = Ashes.Text.compare</c>.</summary>
public sealed record ProvideBinding(string OperationName, Expr Implementation);

/// <summary>
/// A static provider: <c>provide Ord(Str) = | compare = ...</c>. Supplies type-directed evidence for a
/// concrete capability instance (<see cref="CapabilityName"/> applied to <see cref="TypeArgs"/>).
/// </summary>
public sealed record ProvideDecl(string CapabilityName, IReadOnlyList<TypeExpr> TypeArgs, IReadOnlyList<ProvideBinding> Bindings);

/// <summary>A single capability reference inside a <c>uses</c> row: <c>Clock</c> or <c>State(Int)</c>.</summary>
public sealed record CapabilityRefSyntax(string Name, IReadOnlyList<TypeExpr> Args);

/// <summary>
/// A written <c>uses</c> row: <c>uses {A, B}</c> (closed), <c>uses {A, B | e}</c> (open), or
/// <c>uses e</c> (open, no required capabilities). The row is closed exactly when <see cref="TailVar"/>
/// is null.
/// </summary>
public sealed record NeedsRowSyntax(IReadOnlyList<CapabilityRefSyntax> Capabilities, string? TailVar);

/// <summary>
/// A type as written in an <c>external</c> FFI signature. Deliberately narrower than
/// <see cref="TypeExpr"/>: only named types and pointer-to types, matching what the C ABI surface needs.
/// </summary>
public abstract record ParsedType
{
    /// <summary>A named FFI type, e.g. <c>Int</c> or an opaque external type name.</summary>
    /// <param name="Name">The type's name.</param>
    public sealed record Named(string Name) : ParsedType;
    /// <summary>A pointer to another FFI type, written <c>*T</c>.</summary>
    /// <param name="Pointee">The pointed-to type.</param>
    public sealed record Pointer(ParsedType Pointee) : ParsedType;
}

/// <summary>A type expression written by the user in an annotation, e.g. <c>Int -> Str</c>.</summary>
public abstract record TypeExpr
{
    /// <summary>A simple named type or type parameter: <c>Int</c>, <c>Str</c>, <c>T</c>.</summary>
    public sealed record Named(string Name) : TypeExpr;
    /// <summary>A parameterised type application: <c>List(Int)</c>, <c>Result(Str, Int)</c>.</summary>
    public sealed record Applied(string Name, IReadOnlyList<TypeExpr> Args) : TypeExpr;
    /// <summary>A function type: <c>Int -> Str</c>, optionally carrying a capability row: <c>Str -> Int uses {Prices}</c>.</summary>
    public sealed record Arrow(TypeExpr From, TypeExpr To) : TypeExpr
    {
        /// <summary>The written <c>uses</c> row, or null when the arrow carries none (pure).</summary>
        public NeedsRowSyntax? Needs { get; init; }
    }
    /// <summary>A tuple type: <c>(Int, Str)</c>.</summary>
    public sealed record TupleType(IReadOnlyList<TypeExpr> Elements) : TypeExpr;
    /// <summary>Unit written as an empty tuple: <c>()</c>.</summary>
    public sealed record UnitType : TypeExpr;
}

/// <summary>Helpers over <see cref="TypeExpr"/> trees.</summary>
public static class TypeExprExtensions
{
    /// <summary>Every <c>Named</c> / <c>Applied</c> head name mentioned anywhere in a type
    /// expression (leaves and application heads), in order. Used to discover the implicit type
    /// parameters of a constructor field — a name that resolves to no known type is a parameter.</summary>
    public static IEnumerable<string> MentionedNames(this TypeExpr typeExpr) => typeExpr switch
    {
        TypeExpr.Named n => [n.Name],
        TypeExpr.Applied a => new[] { a.Name }.Concat(a.Args.SelectMany(arg => arg.MentionedNames())),
        TypeExpr.Arrow arr => arr.From.MentionedNames().Concat(arr.To.MentionedNames()),
        TypeExpr.TupleType t => t.Elements.SelectMany(e => e.MentionedNames()),
        _ => []
    };
}

/// <summary>
/// An <c>external</c> declaration: a foreign function or opaque type imported from native code via the
/// FFI surface.
/// </summary>
public abstract record ExternalDecl
{
    /// <summary>An opaque external type whose representation is unknown to the compiler,
    /// <c>external type Name</c>.</summary>
    /// <param name="Name">The type's name.</param>
    public sealed record OpaqueType(string Name) : ExternalDecl;

    /// <summary>An external function binding, <c>external name(params) -> ret [= "symbol"]</c>.</summary>
    /// <param name="Name">The Ashes-visible name of the function.</param>
    /// <param name="ParameterTypes">The parameter types in order.</param>
    /// <param name="ReturnType">The return type.</param>
    /// <param name="SymbolName">The native symbol to link against; null uses <paramref name="Name"/>.</param>
    public sealed record Function(
        string Name,
        IReadOnlyList<ParsedType> ParameterTypes,
        ParsedType ReturnType,
        string? SymbolName = null) : ExternalDecl;
}

/// <summary>
/// A single top-level item, kept in source order. Model-A sequential scoping makes the order
/// significant: a binding is visible only to the items that follow it (and the trailing body).
/// </summary>
public abstract record TopLevelItem
{
    /// <summary>A top-level <c>type</c> declaration.</summary>
    public sealed record Type(TypeDecl Decl) : TopLevelItem;

    /// <summary>A top-level <c>external</c> declaration.</summary>
    public sealed record External(ExternalDecl Decl) : TopLevelItem;

    /// <summary>A top-level <c>capability</c> declaration.</summary>
    public sealed record Capability(CapabilityDecl Decl) : TopLevelItem;

    /// <summary>A top-level <c>provide</c> declaration (static capability satisfaction).</summary>
    public sealed record Provide(ProvideDecl Decl) : TopLevelItem;

    /// <summary>A top-level value binding: <c>let Name = Value</c>, or <c>let rec</c> when <see cref="IsRecursive"/>.</summary>
    public sealed record LetDecl(string Name, Expr Value, bool IsRecursive) : TopLevelItem
    {
        /// <summary>
        /// ML-style sugar parameters. When non-empty, the formatter prints <c>let f x y = ...</c>
        /// instead of <c>let f = given (x) -> given (y) -> ...</c>. Codegen is unaffected: the value is
        /// already the desugared nested-lambda form regardless of this list.
        /// </summary>
        public IReadOnlyList<string> SugarParams { get; init; } = [];

        /// <summary>Optional user-supplied type annotation: <c>let f : Int -> Int = ...</c>.</summary>
        public TypeExpr? TypeAnnotation { get; init; }
    }

    /// <summary>
    /// A mutual-recursion group: <c>let rec A = ... and B = ...</c>. Every binding is implicitly
    /// recursive within the group and visible to the others regardless of order.
    /// </summary>
    public sealed record RecursiveGroup(IReadOnlyList<(string Name, Expr Value)> Bindings) : TopLevelItem
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

    /// <summary>Constructs a program from its ordered <paramref name="Items"/> and an optional trailing
    /// <paramref name="Body"/> expression.</summary>
    public Program(IReadOnlyList<TopLevelItem> Items, Expr? Body)
    {
        this.Items = Items;
        this.Body = Body!;
    }

    /// <summary>Back-compat constructor: separated type/external declarations plus a trailing expression.</summary>
    public Program(IReadOnlyList<TypeDecl> TypeDecls, IReadOnlyList<ExternalDecl> ExternalDecls, Expr Body)
        : this(BuildItems(TypeDecls, ExternalDecls), Body)
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

    /// <summary>The <c>external</c> declarations in source order, derived from <see cref="Items"/>.</summary>
    public IReadOnlyList<ExternalDecl> ExternalDecls =>
        Items.OfType<TopLevelItem.External>().Select(item => item.Decl).ToList();

    private static IReadOnlyList<TopLevelItem> BuildItems(
        IReadOnlyList<TypeDecl> typeDecls,
        IReadOnlyList<ExternalDecl> externalDecls)
    {
        var items = new List<TopLevelItem>(typeDecls.Count + externalDecls.Count);
        foreach (var typeDecl in typeDecls)
        {
            items.Add(new TopLevelItem.Type(typeDecl));
        }

        foreach (var externalDecl in externalDecls)
        {
            items.Add(new TopLevelItem.External(externalDecl));
        }

        return items;
    }
}
