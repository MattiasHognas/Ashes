using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    private static bool ArenaSuccessorJoinEnabled
        => Environment.GetEnvironmentVariable("GRC_NO_ARENASUCCESSORJOIN") is null;

    // The if or match whose value becomes a loop parameter left in the arena: a tail self-call's
    // argument written in place, or the value of a `let` the self-call passes on.
    private Expr? _arenaSuccessorJoin;

    private static Expr? SuccessorJoinOf(Expr value)
    {
        while (value is Expr.Let let)
        {
            value = let.Body;
        }

        return value is Expr.If or Expr.Match ? value : null;
    }

    private bool IsArenaSuccessorJoin(Expr.If iff) => ReferenceEquals(iff, _arenaSuccessorJoin);

    private bool IsArenaSuccessorMatch(IReadOnlyList<MatchCase> cases)
        => _arenaSuccessorJoin is Expr.Match match && ReferenceEquals(match.Cases, cases);

    // Marks the join a `let` value ends in when the self-call in tail position of its body passes
    // the binding to a parameter left in the arena. Returns the join it replaces.
    private Expr? EnterArenaSuccessorLetJoin(Expr.Let let)
    {
        Expr? outer = _arenaSuccessorJoin;
        if (ArenaSuccessorJoinEnabled
            && _tcoCtx is { } tco
            && TailSelfCallOrdinalPassing(let.Body, let.Name, tco.SelfName) is int ordinal
            && ordinal < tco.ParamSlots.Count
            && !tco.IsRuntimeManagedSlot(tco.ParamSlots[ordinal])
            && SuccessorJoinOf(let.Value) is { } join)
        {
            _arenaSuccessorJoin = join;
        }

        return outer;
    }

    // The position at which a self-call in tail position of the expression passes the name as an
    // argument, or null.
    private static int? TailSelfCallOrdinalPassing(Expr expression, string name, string selfName)
    {
        switch (expression)
        {
            case Expr.Let let:
                return string.Equals(let.Name, name, StringComparison.Ordinal)
                    ? null
                    : TailSelfCallOrdinalPassing(let.Body, name, selfName);
            case Expr.If iff:
                return TailSelfCallOrdinalPassing(iff.Then, name, selfName)
                    ?? TailSelfCallOrdinalPassing(iff.Else, name, selfName);
            case Expr.Match match:
                foreach (MatchCase matchCase in match.Cases)
                {
                    if (TailSelfCallOrdinalPassing(matchCase.Body, name, selfName) is int found)
                    {
                        return found;
                    }
                }

                return null;
            case Expr.Call call:
                return SelfCallArgumentPassing(call, name, selfName);
            default:
                return null;
        }
    }

    // Walks a saturated self-call's argument chain from the last argument inward.
    private static int? SelfCallArgumentPassing(Expr.Call call, string name, string selfName)
    {
        var arguments = new List<Expr>();
        Expr root = call;
        while (root is Expr.Call applied)
        {
            arguments.Add(applied.Arg);
            root = applied.Func;
        }

        if (root is not Expr.Var callee || !string.Equals(callee.Name, selfName, StringComparison.Ordinal))
        {
            return null;
        }

        arguments.Reverse();
        int ordinal = arguments.FindIndex(argument => argument is Expr.Var variable && string.Equals(variable.Name, name, StringComparison.Ordinal));
        return ordinal < 0 ? null : ordinal;
    }

    /// <summary>
    /// A join whose arms mix a reference-counted value with an arena value, when the join becomes a
    /// loop parameter left in the arena: normalizing the arena arm onto the reference-counted heap
    /// would hand the parameter an owned reference that nothing releases, since an arena parameter
    /// releases nothing it is given. The reference-counted arms are copied into the arena instead,
    /// an owned one released after its copy, so the join is an arena value on every arm. Returns
    /// true when the arms were copied.
    /// </summary>
    private bool DemoteArenaSuccessorJoinArms(bool successorJoin, IReadOnlyList<JoinArm> arms, TypeRef resultType)
    {
        TypeRef pruned = Prune(resultType);
        List<JoinArm> reaching = arms.Where(arm => !IsOwnershipNeutralArm(arm)).ToList();
        if (!ArenaSuccessorJoinEnabled
            || !GeneralRcEnabled
            || _tcoCtx is null
            || !successorJoin
            || reaching.Count < 2
            || reaching.Any(arm => arm.Store is null)
            || !reaching.Any(IsReferenceCountedJoinArm)
            || reaching.All(IsReferenceCountedJoinArm)
            || CanArenaReset(pruned)
            || ContainsUnresolvedLayoutType(pruned, [])
            || !IsGeneralRcAdmissible(pruned))
        {
            return false;
        }

        foreach (JoinArm arm in reaching)
        {
            if (IsReferenceCountedJoinArm(arm))
            {
                bool owned = IsOwnedJoinArm(arm);
                ReplaceJoinArmStore(arm.Store!, source => EmitArenaCopyOfReferenceCounted(source, pruned, releasesSource: owned), runtimeManaged: false);
            }
        }

        return true;
    }
}
