using Ashes.Frontend;

namespace Ashes.Semantics;

/// <summary>
/// A builtin whose result views its argument's bytes (`Ashes.Text.uncons` and `unconsText`, whose
/// tail is a view of the text, `Ashes.Byte.subView` and `Ashes.Byte.fromText`) cannot release a
/// newly produced argument behind itself, as a builtin that only reads its argument does
/// (`ReleaseConsumedOwnedOperand`): the views would point into freed bytes. Such an argument,
/// written in place (`match Ashes.Text.unconsText(trim(text)) with ...`), is bound by an anonymous
/// `let` around the expression that consumes the builtin's result, exactly as the let-bound form
/// (`let trimmed = trim(text) in match Ashes.Text.unconsText(trimmed) with ...`) binds it, so the
/// let's scope owns it: its result views are materialized before the release, and its tail-call
/// rules decide the release around a back edge.
///
/// The consuming expression is the match whose scrutinee is the builtin call, the `let` whose value
/// is the builtin call (the scope is the let's body), or the call one of whose arguments is the
/// builtin call (the scope is that call, a tail self-call included, which the let-bound tail-call
/// argument rules handle). Anywhere else (a function result, a field) the let binds the argument
/// around the builtin call itself, as `let s = trim(text) in Ashes.Byte.fromText(s)` does.
/// </summary>
public sealed partial class Lowering
{
    private int _viewedBuiltinArgumentCount;

    private static bool ViewedBuiltinArgumentRuleEnabled =>
        Environment.GetEnvironmentVariable("GRC_NO_VIEWEDBUILTINARGUMENT") is null;

    private static bool IsViewingBuiltinKind(BuiltinRegistry.BuiltinValueKind kind)
        => kind is BuiltinRegistry.BuiltinValueKind.TextUncons
            or BuiltinRegistry.BuiltinValueKind.TextUnconsText
            or BuiltinRegistry.BuiltinValueKind.BytesSubView
            or BuiltinRegistry.BuiltinValueKind.BytesFromText;

    private static bool IsViewingIntrinsicKind(IntrinsicKind kind)
        => kind is IntrinsicKind.TextUncons
            or IntrinsicKind.TextUnconsText
            or IntrinsicKind.BytesSubView
            or IntrinsicKind.BytesFromText;

    private static int ViewingBuiltinArity(BuiltinRegistry.BuiltinValueKind? kind, IntrinsicKind? intrinsic)
        => kind is BuiltinRegistry.BuiltinValueKind.BytesSubView || intrinsic is IntrinsicKind.BytesSubView ? 3 : 1;

    // The viewed argument of a saturated call to a viewing builtin, when it is an expression that
    // may produce a value of its own (not a name or a literal, which have their owners already).
    private Expr? ViewedBuiltinArgument(Expr expression)
    {
        if (expression is not Expr.Call)
        {
            return null;
        }

        List<Expr> arguments = [];
        Expr root = CollectCallArgs(expression, arguments);
        bool viewing = false;
        int arity = 0;
        switch (root)
        {
            case Expr.QualifiedVar qualified when Lookup(qualified.Module) is null
                && BuiltinRegistry.TryGetModule(ResolveModuleAlias(qualified.Module), out var module)
                && module.Members.TryGetValue(qualified.Name, out var member)
                && member.IsCallable
                && IsViewingBuiltinKind(member.Kind):
                viewing = true;
                arity = ViewingBuiltinArity(member.Kind, null);
                break;
            case Expr.Var variable when Lookup(variable.Name) is Binding.Intrinsic intrinsic
                && IsViewingIntrinsicKind(intrinsic.Kind):
                viewing = true;
                arity = ViewingBuiltinArity(null, intrinsic.Kind);
                break;
        }

        if (!viewing || arguments.Count != arity)
        {
            return null;
        }

        return arguments[0] is Expr.Var or Expr.QualifiedVar or Expr.StrLit or Expr.IntLit
            ? null
            : arguments[0];
    }

    // The builtin call with its viewed argument replaced by the anonymous binding's name.
    private static Expr ReplaceViewedArgument(Expr call, Expr viewed, string name)
    {
        if (call is Expr.Call application)
        {
            Expr arg = ReferenceEquals(application.Arg, viewed)
                ? CopySpan(viewed, new Expr.Var(name))
                : application.Arg;
            return CopySpan(call, new Expr.Call(ReplaceViewedArgument(application.Func, viewed, name), arg));
        }

        return call;
    }

    // `expression` with the viewed argument of the builtin call `builtin` (one of its parts) bound
    // by an anonymous let around the whole expression.
    private Expr BindViewedArgument(Expr expression, Expr builtin, Expr viewed, Func<Expr, Expr> rebuild)
    {
        string name = $"$viewed{_viewedBuiltinArgumentCount++}";
        Expr body = rebuild(ReplaceViewedArgument(builtin, viewed, name));
        CopySpan(expression, body);
        var binding = new Expr.Let(name, viewed, body);
        AstSpans.Set(binding, AstSpans.GetOrDefault(expression));
        return binding;
    }

    // The consuming expression rewritten so a viewing builtin's newly produced argument is bound by
    // an anonymous let of that expression's scope, or null when there is none.
    private Expr? BindViewedBuiltinArgument(Expr expression)
    {
        if (!ViewedBuiltinArgumentRuleEnabled || !GeneralRcEnabled)
        {
            return null;
        }

        switch (expression)
        {
            case Expr.Match match when ViewedBuiltinArgument(match.Value) is { } viewed:
                return BindViewedArgument(match, match.Value, viewed, scrutinee => match with { Value = scrutinee });
            case Expr.Let let when ViewedBuiltinArgument(let.Value) is { } viewed:
                return BindViewedArgument(let, let.Value, viewed, value => CopyLetSpans(let, let with { Value = value }));
            case Expr.Call call when ViewedBuiltinArgument(call) is null:
                List<Expr> arguments = [];
                CollectCallArgs(call, arguments);
                foreach (Expr argument in arguments)
                {
                    if (ViewedBuiltinArgument(argument) is { } viewed)
                    {
                        return BindViewedArgument(call, argument, viewed, replaced => ReplaceCallArgument(call, argument, replaced));
                    }
                }

                return null;
            case Expr.Call call when ViewedBuiltinArgument(call) is { } viewed:
                return BindViewedArgument(call, call, viewed, static replaced => replaced);
            default:
                return null;
        }
    }

    private static Expr ReplaceCallArgument(Expr call, Expr argument, Expr replacement)
    {
        if (call is Expr.Call application)
        {
            Expr arg = ReferenceEquals(application.Arg, argument) ? replacement : application.Arg;
            return CopySpan(call, new Expr.Call(ReplaceCallArgument(application.Func, argument, replacement), arg));
        }

        return call;
    }
}
