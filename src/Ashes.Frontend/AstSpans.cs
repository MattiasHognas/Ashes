using System.Runtime.CompilerServices;

namespace Ashes.Frontend;

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
    private static readonly ConditionalWeakTable<CapabilityDecl, SpanBox> EffectDeclSpans = new();
    private static readonly ConditionalWeakTable<TopLevelItem.LetDecl, SpanBox> LetDeclSpans = new();
    private static readonly ConditionalWeakTable<TopLevelItem.RecursiveGroup, SpanBox> RecursiveGroupSpans = new();

    public static void Set(Expr expr, TextSpan span)
    {
        ExprSpans.Remove(expr);
        ExprSpans.Add(expr, new SpanBox(span));
    }

    public static void Set(Pattern pattern, TextSpan span)
    {
        PatternSpans.Remove(pattern);
        PatternSpans.Add(pattern, new SpanBox(span));
    }

    public static void SetLetName(Expr.Let letExpr, TextSpan span)
    {
        LetNameSpans.Remove(letExpr);
        LetNameSpans.Add(letExpr, new SpanBox(span));
    }

    public static void SetLetResultName(Expr.LetResult letResultExpr, TextSpan span)
    {
        LetResultNameSpans.Remove(letResultExpr);
        LetResultNameSpans.Add(letResultExpr, new SpanBox(span));
    }

    public static void SetLetRecursiveName(Expr.LetRecursive letRecursiveExpr, TextSpan span)
    {
        LetRecursiveNameSpans.Remove(letRecursiveExpr);
        LetRecursiveNameSpans.Add(letRecursiveExpr, new SpanBox(span));
    }

    public static void SetLambdaParameter(Expr.Lambda lambdaExpr, TextSpan span)
    {
        LambdaParameterSpans.Remove(lambdaExpr);
        LambdaParameterSpans.Add(lambdaExpr, new SpanBox(span));
    }

    public static void Set(TypeDecl typeDecl, TextSpan span)
    {
        TypeDeclSpans.Remove(typeDecl);
        TypeDeclSpans.Add(typeDecl, new SpanBox(span));
    }

    public static void Set(TypeConstructor typeConstructor, TextSpan span)
    {
        TypeConstructorSpans.Remove(typeConstructor);
        TypeConstructorSpans.Add(typeConstructor, new SpanBox(span));
    }

    public static void Set(ExternalDecl externalDecl, TextSpan span)
    {
        ExternalDeclSpans.Remove(externalDecl);
        ExternalDeclSpans.Add(externalDecl, new SpanBox(span));
    }

    public static void Set(TopLevelItem.LetDecl letDecl, TextSpan span)
    {
        LetDeclSpans.Remove(letDecl);
        LetDeclSpans.Add(letDecl, new SpanBox(span));
    }

    public static void Set(TopLevelItem.RecursiveGroup recursiveGroup, TextSpan span)
    {
        RecursiveGroupSpans.Remove(recursiveGroup);
        RecursiveGroupSpans.Add(recursiveGroup, new SpanBox(span));
    }

    public static TextSpan GetOrDefault(Expr expr)
    {
        return ExprSpans.TryGetValue(expr, out var spanBox) ? spanBox.Span : default;
    }

    public static TextSpan GetOrDefault(Pattern pattern)
    {
        return PatternSpans.TryGetValue(pattern, out var spanBox) ? spanBox.Span : default;
    }

    public static TextSpan GetLetNameOrDefault(Expr.Let letExpr)
    {
        return LetNameSpans.TryGetValue(letExpr, out var spanBox) ? spanBox.Span : default;
    }

    public static TextSpan GetLetResultNameOrDefault(Expr.LetResult letResultExpr)
    {
        return LetResultNameSpans.TryGetValue(letResultExpr, out var spanBox) ? spanBox.Span : default;
    }

    public static TextSpan GetLetRecursiveNameOrDefault(Expr.LetRecursive letRecursiveExpr)
    {
        return LetRecursiveNameSpans.TryGetValue(letRecursiveExpr, out var spanBox) ? spanBox.Span : default;
    }

    public static TextSpan GetLambdaParameterOrDefault(Expr.Lambda lambdaExpr)
    {
        return LambdaParameterSpans.TryGetValue(lambdaExpr, out var spanBox) ? spanBox.Span : default;
    }

    public static TextSpan GetOrDefault(TypeDecl typeDecl)
    {
        return TypeDeclSpans.TryGetValue(typeDecl, out var spanBox) ? spanBox.Span : default;
    }

    public static TextSpan GetOrDefault(TypeConstructor typeConstructor)
    {
        return TypeConstructorSpans.TryGetValue(typeConstructor, out var spanBox) ? spanBox.Span : default;
    }

    public static TextSpan GetOrDefault(ExternalDecl externalDecl)
    {
        return ExternalDeclSpans.TryGetValue(externalDecl, out var spanBox) ? spanBox.Span : default;
    }

    public static void Set(CapabilityDecl effectDecl, TextSpan span)
    {
        EffectDeclSpans.Remove(effectDecl);
        EffectDeclSpans.Add(effectDecl, new SpanBox(span));
    }

    public static TextSpan GetOrDefault(CapabilityDecl effectDecl)
    {
        return EffectDeclSpans.TryGetValue(effectDecl, out var spanBox) ? spanBox.Span : default;
    }
}
