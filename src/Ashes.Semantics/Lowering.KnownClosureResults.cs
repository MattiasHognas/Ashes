using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    /// <summary>
    /// A lambda applied where it is written (`value |> (given (x) -> ...)`) is a known callee: its
    /// body was lowered just before the call, so the call result takes the ownership the body
    /// decided. A body that returns a reference-counted value hands it over owned, exactly as a
    /// named function's does, and the caller releases it; without this the caller treats the
    /// result as an arena value and nothing releases it.
    /// </summary>
    private bool AppliedLambdaResultRuntimeManaged(Expr rootExpr, int argumentCount, TypeRef callResultType)
        => GeneralRcEnabled
            && Environment.GetEnvironmentVariable("GRC_NO_APPLIEDLAMBDA") is null
            && AllowsAsyncIndependentRcPlacement
            && AllowsOrdinaryRcPlacement
            && rootExpr is Expr.Lambda lambda
            && _appliedLambdaLabels.TryGetValue(lambda, out string? label)
            && !CanArenaReset(Prune(callResultType))
            && IsConcretelyRuntimeManageableResultType(callResultType)
            && TryGetCompiledFunctionResultRuntimeManaged(label, argumentCount, out bool runtimeManaged)
            && runtimeManaged;

    private void LinkLambdaLabel(string label, Expr.Lambda lambda)
    {
        LinkCurryStage(label, lambda);
        _appliedLambdaLabels[lambda] = label;
    }
}
