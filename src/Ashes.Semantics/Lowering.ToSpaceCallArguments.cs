using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    private (int Temp, bool CopiedToSpace) PrepareGenericCallArgument(
        Expr rootExpr,
        Expr argument,
        int argumentIndex,
        TypeRef.TFun functionType,
        int remainingArguments,
        int argumentTemp,
        TypeRef argumentType,
        List<ConsumedRuntimeArgument> consumedRuntimeArguments)
    {
        if (!IsCalleeParameterQuantifiedInScheme(rootExpr, argumentIndex))
        {
            return (argumentTemp, false);
        }

        TypeRef resolved = Prune(argumentType);
        if (!IsToSpaceCopySafeType(resolved))
        {
            return (argumentTemp, false);
        }

        if (OwnsGenericStringArgument(resolved, functionType, remainingArguments)
            || OwnsGenericAdtArgument(resolved, functionType, remainingArguments)
            || OwnsGenericListArgument(resolved, functionType, remainingArguments))
        {
            return (EmitHandedOverGenericStringArgument(argumentTemp, resolved, consumedRuntimeArguments), false);
        }

        bool freshOwnedSource = IsFreshRuntimeArgument(argument, argumentTemp);
        int copied = EmitDeepCopyToSpace(argumentTemp, resolved);
        bool converted = copied != argumentTemp;
        if (converted && freshOwnedSource
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GRC_NO_TOSPACEARGUMENTREPRESENTATION")))
        {
            // The copy-safety proof covers every child. The callee receives only the
            // independent copy; the fresh source reference remains ours to release.
            consumedRuntimeArguments.Add(new ConsumedRuntimeArgument(
                argumentTemp, resolved, PreserveEscapedChildren: false));
        }

        return (copied, converted);
    }

    // A persistent-region copy has no RC header, even when the source expression is
    // an owned parameter or pattern binding. Its original binding still owns its own
    // reference; no retain, adoption bit, or accessor-result RC inference transfers
    // from that binding to this different representation.
    private int ApplyToSpaceArgument(
        Expr rootExpr,
        int closureTemp,
        int argumentTemp,
        bool needsResultOwnership,
        bool requestsArenaResult,
        int[]? tupleResultParts,
        ref int runtimeManagedResultFlagTemp)
    {
        CheckClosureCapturesLiveAtApplication(rootExpr, closureTemp);
        if (needsResultOwnership)
        {
            runtimeManagedResultFlagTemp = EmitClosureReturnsRuntimeManagedFlag(closureTemp);
        }

        int target = NewTemp();
        EmitClosureCall(target, closureTemp, argumentTemp, borrowsArgument: true,
            requestsArenaResult: requestsArenaResult, tupleResultParts: tupleResultParts);
        return target;
    }
}
