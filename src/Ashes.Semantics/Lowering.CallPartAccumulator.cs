using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    private static bool CallPartAccumulatorEnabled
        => Environment.GetEnvironmentVariable("GRC_NO_CALLPARTACCUMULATOR") is null;

    // An arm of a match over a named function's result that hands on a value its own pattern bound:
    // a part of the call's result. The join takes a reference of its own to the part (a retain when
    // the part is reference-counted, a copy when it is not) before the scrutinee is released, so the
    // arm assumes nothing about where the callee placed its result. A callee that is a loop
    // parameter or any other computed function is not admitted.
    private static bool IsCallResultPartArm(Expr.Match match, MatchCase arm, IReadOnlyDictionary<string, int> parameterScope)
        => CallPartAccumulatorEnabled
            && match.Value is Expr.Call call
            && CallRoot(call) is Expr.Var callee
            && !parameterScope.ContainsKey(callee.Name)
            && arm.Guard is null
            && arm.Body is Expr.Var part
            && PatternBinds(arm.Pattern, part.Name);

    private static Expr CallRoot(Expr.Call call)
    {
        Expr root = call;
        while (root is Expr.Call applied)
        {
            root = applied.Func;
        }

        return root;
    }
}
