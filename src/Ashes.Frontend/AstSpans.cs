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

    private sealed class SpanListBox(IReadOnlyList<TextSpan> spans)
    {
        public IReadOnlyList<TextSpan> Spans { get; } = spans;
    }

    private static readonly ConditionalWeakTable<Expr, SpanBox> ExprSpans = new();
    private static readonly ConditionalWeakTable<Expr.Let, SpanBox> LetNameSpans = new();
    private static readonly ConditionalWeakTable<Expr.LetResult, SpanBox> LetResultNameSpans = new();
    private static readonly ConditionalWeakTable<Expr.LetRecursive, SpanBox> LetRecursiveNameSpans = new();
    private static readonly ConditionalWeakTable<Expr.Lambda, SpanBox> LambdaParameterSpans = new();
    private static readonly ConditionalWeakTable<Pattern, SpanBox> PatternSpans = new();
    private static readonly ConditionalWeakTable<TypeDecl, SpanBox> TypeDeclSpans = new();
    private static readonly ConditionalWeakTable<ExportDecl, SpanBox> ExportDeclSpans = new();
    private static readonly ConditionalWeakTable<TypeConstructor, SpanBox> TypeConstructorSpans = new();
    private static readonly ConditionalWeakTable<ExternalDecl, SpanBox> ExternalDeclSpans = new();
    private static readonly ConditionalWeakTable<CapabilityDecl, SpanBox> CapabilityDeclSpans = new();
    private static readonly ConditionalWeakTable<ProvideDecl, SpanBox> ProvideDeclSpans = new();
    private static readonly ConditionalWeakTable<TraitDecl, SpanBox> TraitDeclSpans = new();
    private static readonly ConditionalWeakTable<TraitMethodDecl, SpanBox> TraitMethodDeclSpans = new();
    private static readonly ConditionalWeakTable<TraitImplementationDecl, SpanBox> TraitImplementationDeclSpans = new();
    private static readonly ConditionalWeakTable<TraitImplementationMethodBinding, SpanBox> TraitImplementationMethodBindingSpans = new();
    private static readonly ConditionalWeakTable<TraitConstraintSyntax, SpanBox> TraitConstraintSpans = new();
    private static readonly ConditionalWeakTable<TopLevelItem.LetDecl, SpanBox> LetDeclSpans = new();
    private static readonly ConditionalWeakTable<TopLevelItem.RecursiveGroup, SpanBox> RecursiveGroupSpans = new();
    private static readonly ConditionalWeakTable<TopLevelItem.RecursiveGroup, SpanListBox>
        RecursiveGroupBindingNameSpans = new();

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

    /// <summary>Records the source span of an <c>export</c> declaration.</summary>
    public static void Set(ExportDecl exportDecl, TextSpan span)
    {
        ExportDeclSpans.Remove(exportDecl);
        ExportDeclSpans.Add(exportDecl, new SpanBox(span));
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

    /// <summary>Records the source span of a top-level <c>let</c> declaration's identifier.</summary>
    public static void Set(TopLevelItem.LetDecl letDecl, TextSpan span)
    {
        LetDeclSpans.Remove(letDecl);
        LetDeclSpans.Add(letDecl, new SpanBox(span));
    }

    /// <summary>Records the source span of a top-level mutual-recursion group's first identifier.</summary>
    public static void Set(TopLevelItem.RecursiveGroup recursiveGroup, TextSpan span)
    {
        RecursiveGroupSpans.Remove(recursiveGroup);
        RecursiveGroupSpans.Add(recursiveGroup, new SpanBox(span));
    }

    /// <summary>Records each binding identifier span in a top-level mutual-recursion group.</summary>
    public static void SetRecursiveGroupBindingNames(
        TopLevelItem.RecursiveGroup recursiveGroup,
        IReadOnlyList<TextSpan> spans)
    {
        RecursiveGroupBindingNameSpans.Remove(recursiveGroup);
        RecursiveGroupBindingNameSpans.Add(recursiveGroup, new SpanListBox(spans));
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

    /// <summary>Returns the recorded identifier span of a top-level <c>let</c> declaration.</summary>
    public static TextSpan GetOrDefault(TopLevelItem.LetDecl letDecl)
    {
        return LetDeclSpans.TryGetValue(letDecl, out var spanBox) ? spanBox.Span : default;
    }

    /// <summary>Returns the recorded first-identifier span of a top-level recursive group.</summary>
    public static TextSpan GetOrDefault(TopLevelItem.RecursiveGroup recursiveGroup)
    {
        return RecursiveGroupSpans.TryGetValue(recursiveGroup, out var spanBox) ? spanBox.Span : default;
    }

    /// <summary>Returns the binding identifier spans of a top-level recursive group.</summary>
    public static IReadOnlyList<TextSpan> GetRecursiveGroupBindingNamesOrDefault(
        TopLevelItem.RecursiveGroup recursiveGroup)
    {
        return RecursiveGroupBindingNameSpans.TryGetValue(recursiveGroup, out var spanBox)
            ? spanBox.Spans
            : [];
    }

    /// <summary>Returns the recorded span of a <c>type</c> declaration, or the default if unset.</summary>
    public static TextSpan GetOrDefault(TypeDecl typeDecl)
    {
        return TypeDeclSpans.TryGetValue(typeDecl, out var spanBox) ? spanBox.Span : default;
    }

    /// <summary>Returns the recorded span of an <c>export</c> declaration, or the default if unset.</summary>
    public static TextSpan GetOrDefault(ExportDecl exportDecl)
    {
        return ExportDeclSpans.TryGetValue(exportDecl, out var spanBox) ? spanBox.Span : default;
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

    /// <summary>Records the source span of a trait declaration.</summary>
    public static void Set(TraitDecl declaration, TextSpan span) => SetSpan(TraitDeclSpans, declaration, span);

    /// <summary>Returns the source span of a trait declaration.</summary>
    public static TextSpan GetOrDefault(TraitDecl declaration) => GetSpan(TraitDeclSpans, declaration);

    /// <summary>Records the source span of a trait method declaration.</summary>
    public static void Set(TraitMethodDecl method, TextSpan span) => SetSpan(TraitMethodDeclSpans, method, span);

    /// <summary>Returns the source span of a trait method declaration.</summary>
    public static TextSpan GetOrDefault(TraitMethodDecl method) => GetSpan(TraitMethodDeclSpans, method);

    /// <summary>Records the source span of a trait implementation declaration.</summary>
    public static void Set(TraitImplementationDecl declaration, TextSpan span) => SetSpan(TraitImplementationDeclSpans, declaration, span);

    /// <summary>Returns the source span of a trait implementation declaration.</summary>
    public static TextSpan GetOrDefault(TraitImplementationDecl declaration) => GetSpan(TraitImplementationDeclSpans, declaration);

    /// <summary>Records the source span of a trait implementation method binding.</summary>
    public static void Set(TraitImplementationMethodBinding binding, TextSpan span) => SetSpan(TraitImplementationMethodBindingSpans, binding, span);

    /// <summary>Returns the source span of a trait implementation method binding.</summary>
    public static TextSpan GetOrDefault(TraitImplementationMethodBinding binding) => GetSpan(TraitImplementationMethodBindingSpans, binding);

    /// <summary>Records the source span of a written trait constraint.</summary>
    public static void Set(TraitConstraintSyntax constraint, TextSpan span) => SetSpan(TraitConstraintSpans, constraint, span);

    /// <summary>Returns the source span of a written trait constraint.</summary>
    public static TextSpan GetOrDefault(TraitConstraintSyntax constraint) => GetSpan(TraitConstraintSpans, constraint);

    private static void SetSpan<T>(ConditionalWeakTable<T, SpanBox> table, T key, TextSpan span)
        where T : class
    {
        table.Remove(key);
        table.Add(key, new SpanBox(span));
    }

    private static TextSpan GetSpan<T>(ConditionalWeakTable<T, SpanBox> table, T key)
        where T : class => table.TryGetValue(key, out SpanBox? spanBox) ? spanBox.Span : default;
}
