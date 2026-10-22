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

    /// <summary>
    /// A call through a captured self (`stepLive(base)(rest)` inside a lambda the recursive binding's
    /// body applies) reaches a function whose body is still being lowered, so its result's placement
    /// is not known yet. For a list only the recursive deep copy expresses, the call reads the
    /// callee's returns bit: a reference-counted result is taken over as it is, an arena one is
    /// copied, and the caller owns the result on both paths.
    /// </summary>
    private bool CapturedSelfListResultOwnedAtRunTime(Expr rootExpr, int argumentCount, TypeRef resultType)
    {
        if (!GeneralRcEnabled
            || Environment.GetEnvironmentVariable("GRC_NO_CAPTUREDSELFRESULT") is not null
            || rootExpr is not Expr.Var callee
            || Lookup(callee.Name) is not (Binding.Env or Binding.EnvScheme)
            || Prune(resultType) is not TypeRef.TList list
            || CanArenaReset(Prune(list.Element))
            || ContainsUnresolvedLayoutType(resultType, [])
            || !CanEmitRuntimeManagedListElementDeepCopy(list.Element)
            || !TryResolveKnownFunctionLabel(rootExpr, out string label))
        {
            return false;
        }

        for (int index = 1; index < argumentCount; index++)
        {
            if (!_functionReturnedClosureLabels.TryGetValue(label, out string? nextLabel))
            {
                return false;
            }

            label = nextLabel;
        }

        return _loweringFunctionLabels.Contains(label, StringComparer.Ordinal);
    }

    private (int Temp, TypeRef Type) LowerConsTailValue(
        Expr.Cons cons,
        LoweredValueRequest tailRequest,
        ref LoweredValue head,
        ref LoweredValueRequest request)
    {
        (int tailTemp, TypeRef tailType) = LowerExpr(cons.Tail, tailRequest).AsPair();
        (head, request) = OwnHeadOfCellOverFreshTail(cons, head, tailTemp, tailType, request);
        return (tailTemp, tailType);
    }

    /// <summary>
    /// A cons onto a fresh reference-counted tail (a call's owned result, `entry :: build(n)`) builds
    /// a reference-counted cell, the list counterpart of a constructor owning a fresh child: an arena
    /// cell never releases what it holds, so the tail would be left with no owner once the cell's
    /// normalization takes a reference of its own. The cell takes the tail over, and the head takes
    /// a reference of its own when it is reference-counted or a copy when it lies in the arena.
    /// </summary>
    private (LoweredValue Head, LoweredValueRequest Request) OwnHeadOfCellOverFreshTail(
        Expr.Cons cons,
        LoweredValue head,
        int tailTemp,
        TypeRef tailType,
        LoweredValueRequest request)
    {
        TypeRef headType = Prune(head.Type);
        if (!GeneralRcEnabled
            || Environment.GetEnvironmentVariable("GRC_NO_FRESHTAILCELL") is not null
            || !CanTestRepresentation
            || request.EmitsRuntime(LoweredValueRuntimeRepresentation.List)
            || cons.Tail is not Expr.Call
            || !IsNewlyProducedRcTemp(tailTemp)
            || IsBackEdgeCopiedCell(cons)
            || ContainsUnresolvedLayoutType(tailType, [])
            || ContainsUnresolvedLayoutType(headType, [])
            || !(CanArenaReset(headType) || IsRuntimeManagedResultTemp(head.Temp) || CanNormalizeIntoOwnedRuntimeValue(headType)))
        {
            return (head, request);
        }

        if (!CanArenaReset(headType))
        {
            int ownedHeadTemp = EmitRuntimeManagedTcoDeepCopy(head.Temp, headType);
            MarkRuntimeManagedTemp(ownedHeadTemp);
            head = CreateLoweredValue(ownedHeadTemp, head.Type);
        }

        return (head, request.AddRuntime(true, LoweredValueRuntimeRepresentation.List));
    }

    private void LinkLambdaLabel(string label, Expr.Lambda lambda)
    {
        LinkCurryStage(label, lambda);
        _appliedLambdaLabels[lambda] = label;
    }
}
