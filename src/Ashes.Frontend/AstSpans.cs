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
    private static readonly ConditionalWeakTable<Expr.LetRec, SpanBox> LetRecNameSpans = new();
    private static readonly ConditionalWeakTable<Expr.Lambda, SpanBox> LambdaParameterSpans = new();
    private static readonly ConditionalWeakTable<Pattern, SpanBox> PatternSpans = new();
    private static readonly ConditionalWeakTable<TypeDecl, SpanBox> TypeDeclSpans = new();
    private static readonly ConditionalWeakTable<TypeConstructor, SpanBox> TypeConstructorSpans = new();

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

    public static void SetLetRecName(Expr.LetRec letRecExpr, TextSpan span)
    {
        LetRecNameSpans.Remove(letRecExpr);
        LetRecNameSpans.Add(letRecExpr, new SpanBox(span));
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

    public static TextSpan GetLetRecNameOrDefault(Expr.LetRec letRecExpr)
    {
        return LetRecNameSpans.TryGetValue(letRecExpr, out var spanBox) ? spanBox.Span : default;
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
}
