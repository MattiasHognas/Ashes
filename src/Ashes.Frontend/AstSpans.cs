using System.Runtime.CompilerServices;

namespace Ashes.Frontend;

/// <summary>
/// A side table mapping AST nodes to their originating source spans. Spans are kept out of the AST
/// records (so structural equality stays value-based) and stored here in
/// <see cref="System.Runtime.CompilerServices.ConditionalWeakTable{TKey, TValue}"/> instances keyed by
/// node identity; a lookup for an unregistered node returns the default span. The parser populates the
/// table; consumers such as diagnostics and the formatter read it back.
/// </summary>
public static class AstSpans
{
    private sealed class SpanBox(TextSpan span)
    {
        public TextSpan Span { get; } = span;
    }

    private static readonly ConditionalWeakTable<Expr, SpanBox> ExprSpans = new();
    private static readonly ConditionalWeakTable<Expr.Let, SpanBox> LetNameSpans = new();
    private static readonly ConditionalWeakTable<Expr.LetResult, SpanBox> LetResultNameSpans = new();
    private static readonly ConditionalWeakTable<Expr.LetRecursive, SpanBox> LetRecursiveNameSpans = new();
    private static readonly ConditionalWeakTable<Expr.Lambda, SpanBox> LambdaParameterSpans = new();
    private static readonly ConditionalWeakTable<Pattern, SpanBox> PatternSpans = new();
    private static readonly ConditionalWeakTable<TypeDecl, SpanBox> TypeDeclSpans = new();
    private static readonly ConditionalWeakTable<TypeConstructor, SpanBox> TypeConstructorSpans = new();
    private static readonly ConditionalWeakTable<ExternalDecl, SpanBox> ExternalDeclSpans = new();
    private static readonly ConditionalWeakTable<CapabilityDecl, SpanBox> CapabilityDeclSpans = new();
    private static readonly ConditionalWeakTable<ProvideDecl, SpanBox> ProvideDeclSpans = new();
    private static readonly ConditionalWeakTable<TopLevelItem.LetDecl, SpanBox> LetDeclSpans = new();
    private static readonly ConditionalWeakTable<TopLevelItem.RecursiveGroup, SpanBox> RecursiveGroupSpans = new();

    /// <summary>Records the source span of an expression node.</summary>
    public static void Set(Expr expr, TextSpan span)
    {
        ExprSpans.Remove(expr);
        ExprSpans.Add(expr, new SpanBox(span));
    }

    /// <summary>Records the source span of a pattern node.</summary>
    public static void Set(Pattern pattern, TextSpan span)
    {
        PatternSpans.Remove(pattern);
        PatternSpans.Add(pattern, new SpanBox(span));
    }

    /// <summary>Records the span of the bound name in a <see cref="Expr.Let"/> (the identifier alone,
    /// not the whole binding).</summary>
    public static void SetLetName(Expr.Let letExpr, TextSpan span)
    {
        LetNameSpans.Remove(letExpr);
        LetNameSpans.Add(letExpr, new SpanBox(span));
    }

    /// <summary>Records the span of the bound name in a <see cref="Expr.LetResult"/> binding.</summary>
    public static void SetLetResultName(Expr.LetResult letResultExpr, TextSpan span)
    {
        LetResultNameSpans.Remove(letResultExpr);
        LetResultNameSpans.Add(letResultExpr, new SpanBox(span));
    }

    /// <summary>Records the span of the bound name in a <see cref="Expr.LetRecursive"/> binding.</summary>
    public static void SetLetRecursiveName(Expr.LetRecursive letRecursiveExpr, TextSpan span)
    {
        LetRecursiveNameSpans.Remove(letRecursiveExpr);
        LetRecursiveNameSpans.Add(letRecursiveExpr, new SpanBox(span));
    }

    /// <summary>Records the span of the parameter name in a <see cref="Expr.Lambda"/>.</summary>
    public static void SetLambdaParameter(Expr.Lambda lambdaExpr, TextSpan span)
    {
        LambdaParameterSpans.Remove(lambdaExpr);
        LambdaParameterSpans.Add(lambdaExpr, new SpanBox(span));
    }

    /// <summary>Records the source span of a <c>type</c> declaration.</summary>
    public static void Set(TypeDecl typeDecl, TextSpan span)
    {
        TypeDeclSpans.Remove(typeDecl);
        TypeDeclSpans.Add(typeDecl, new SpanBox(span));
    }

    /// <summary>Records the source span of a type constructor.</summary>
    public static void Set(TypeConstructor typeConstructor, TextSpan span)
    {
        TypeConstructorSpans.Remove(typeConstructor);
        TypeConstructorSpans.Add(typeConstructor, new SpanBox(span));
    }

    /// <summary>Records the source span of an <c>external</c> declaration.</summary>
    public static void Set(ExternalDecl externalDecl, TextSpan span)
    {
        ExternalDeclSpans.Remove(externalDecl);
        ExternalDeclSpans.Add(externalDecl, new SpanBox(span));
    }

    /// <summary>Records the source span of a top-level <c>let</c> declaration.</summary>
    public static void Set(TopLevelItem.LetDecl letDecl, TextSpan span)
    {
        LetDeclSpans.Remove(letDecl);
        LetDeclSpans.Add(letDecl, new SpanBox(span));
    }

    /// <summary>Records the source span of a top-level mutual-recursion group.</summary>
    public static void Set(TopLevelItem.RecursiveGroup recursiveGroup, TextSpan span)
    {
        RecursiveGroupSpans.Remove(recursiveGroup);
        RecursiveGroupSpans.Add(recursiveGroup, new SpanBox(span));
    }

    /// <summary>Returns the recorded span of an expression node, or the default span if none was set.</summary>
    public static TextSpan GetOrDefault(Expr expr)
    {
        return ExprSpans.TryGetValue(expr, out var spanBox) ? spanBox.Span : default;
    }

    /// <summary>Returns the recorded span of a pattern node, or the default span if none was set.</summary>
    public static TextSpan GetOrDefault(Pattern pattern)
    {
        return PatternSpans.TryGetValue(pattern, out var spanBox) ? spanBox.Span : default;
    }

    /// <summary>Returns the recorded name span of a <see cref="Expr.Let"/>, or the default if unset.</summary>
    public static TextSpan GetLetNameOrDefault(Expr.Let letExpr)
    {
        return LetNameSpans.TryGetValue(letExpr, out var spanBox) ? spanBox.Span : default;
    }

    /// <summary>Returns the recorded name span of a <see cref="Expr.LetResult"/>, or the default if unset.</summary>
    public static TextSpan GetLetResultNameOrDefault(Expr.LetResult letResultExpr)
    {
        return LetResultNameSpans.TryGetValue(letResultExpr, out var spanBox) ? spanBox.Span : default;
    }

    /// <summary>Returns the recorded name span of a <see cref="Expr.LetRecursive"/>, or the default if unset.</summary>
    public static TextSpan GetLetRecursiveNameOrDefault(Expr.LetRecursive letRecursiveExpr)
    {
        return LetRecursiveNameSpans.TryGetValue(letRecursiveExpr, out var spanBox) ? spanBox.Span : default;
    }

    /// <summary>Returns the recorded parameter span of a <see cref="Expr.Lambda"/>, or the default if unset.</summary>
    public static TextSpan GetLambdaParameterOrDefault(Expr.Lambda lambdaExpr)
    {
        return LambdaParameterSpans.TryGetValue(lambdaExpr, out var spanBox) ? spanBox.Span : default;
    }

    /// <summary>Returns the recorded span of a <c>type</c> declaration, or the default if unset.</summary>
    public static TextSpan GetOrDefault(TypeDecl typeDecl)
    {
        return TypeDeclSpans.TryGetValue(typeDecl, out var spanBox) ? spanBox.Span : default;
    }

    /// <summary>Returns the recorded span of a type constructor, or the default if unset.</summary>
    public static TextSpan GetOrDefault(TypeConstructor typeConstructor)
    {
        return TypeConstructorSpans.TryGetValue(typeConstructor, out var spanBox) ? spanBox.Span : default;
    }

    /// <summary>Returns the recorded span of an <c>external</c> declaration, or the default if unset.</summary>
    public static TextSpan GetOrDefault(ExternalDecl externalDecl)
    {
        return ExternalDeclSpans.TryGetValue(externalDecl, out var spanBox) ? spanBox.Span : default;
    }

    /// <summary>Records the source span of a <c>capability</c> declaration.</summary>
    public static void Set(CapabilityDecl capabilityDecl, TextSpan span)
    {
        CapabilityDeclSpans.Remove(capabilityDecl);
        CapabilityDeclSpans.Add(capabilityDecl, new SpanBox(span));
    }

    /// <summary>Returns the recorded span of a <c>capability</c> declaration, or the default if unset.</summary>
    public static TextSpan GetOrDefault(CapabilityDecl capabilityDecl)
    {
        return CapabilityDeclSpans.TryGetValue(capabilityDecl, out var spanBox) ? spanBox.Span : default;
    }

    /// <summary>Records the source span of a <c>provide</c> declaration.</summary>
    public static void Set(ProvideDecl provideDecl, TextSpan span)
    {
        ProvideDeclSpans.Remove(provideDecl);
        ProvideDeclSpans.Add(provideDecl, new SpanBox(span));
    }

    /// <summary>Returns the recorded span of a <c>provide</c> declaration, or the default if unset.</summary>
    public static TextSpan GetOrDefault(ProvideDecl provideDecl)
    {
        return ProvideDeclSpans.TryGetValue(provideDecl, out var spanBox) ? spanBox.Span : default;
    }
}
