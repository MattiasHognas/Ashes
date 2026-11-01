using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    // The parameter of the plain function whose body is being lowered, when that body returns the
    // parameter itself on some arms and a fresh value on every other arm. The arm returning the
    // parameter copies it into an owned reference-counted graph, so the function's result is
    // reference-counted on every path and its callers adopt it instead of leaving it unreleased.
    private PassthroughParameter? _passthroughParameter;

    // Labels whose result was promised reference-counted before their body was lowered, so their
    // own recursive call sites could treat the result as owned; the promise is kept at the return.
    private readonly HashSet<string> _predictedRuntimeManagedResultLabels = new(StringComparer.Ordinal);

    // The curry stage whose body is the lambda about to be lowered, so the returned-closure link is
    // recorded before that body's own recursive calls walk it.
    private (string Label, Expr Body)? _curryStage;

    private sealed record PassthroughParameter(string Name, int Slot, TypeRef Type, string Label);

    private PassthroughParameter? ResolvePassthroughParameter(Expr.Lambda lambda, string label, int argumentSlot, TypeRef parameterType)
    {
        if (_tcoCtx is not null
            || _inCoroutineBody
            || _collectInferredTraitElaboration
            || !AllowsOrdinaryRcPlacement
            || !AllowsAsyncIndependentRcPlacement
            || _normalizedAlwaysReturnedParameter is not null
            || !ReturnsParameterOrFreshValue(lambda.Body, lambda.ParamName))
        {
            return null;
        }

        return new PassthroughParameter(lambda.ParamName, argumentSlot, parameterType, label);
    }

    private bool ReturnsParameterOrFreshValue(Expr body, string parameterName)
    {
        bool returnsParameter = false;
        return TerminalArmsAreParameterOrFresh(body, parameterName, ref returnsParameter, 0) && returnsParameter;
    }

    private bool TerminalArmsAreParameterOrFresh(Expr expression, string parameterName, ref bool returnsParameter, int depth)
    {
        if (depth > 32)
        {
            return false;
        }

        switch (expression)
        {
            case Expr.Let let:
                return !string.Equals(let.Name, parameterName, StringComparison.Ordinal)
                    && TerminalArmsAreParameterOrFresh(let.Body, parameterName, ref returnsParameter, depth + 1);
            case Expr.Var variable:
                if (!string.Equals(variable.Name, parameterName, StringComparison.Ordinal))
                {
                    return false;
                }

                returnsParameter = true;
                return true;
            case Expr.If conditional:
                return TerminalArmsAreParameterOrFresh(conditional.Then, parameterName, ref returnsParameter, depth + 1)
                    && TerminalArmsAreParameterOrFresh(conditional.Else, parameterName, ref returnsParameter, depth + 1);
            case Expr.Match match:
                if (match.Cases.Count == 0)
                {
                    return false;
                }

                foreach (MatchCase matchCase in match.Cases)
                {
                    if (PatternBinds(matchCase.Pattern, parameterName)
                        || !TerminalArmsAreParameterOrFresh(matchCase.Body, parameterName, ref returnsParameter, depth + 1))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return ProducesFreshValue(expression);
        }
    }

    // A terminal arm that builds its value: a constructor application, list, record or tuple
    // literal, a cons, a call to a function compiled with a reference-counted result, or a call to
    // the function itself, whose result is promised reference-counted.
    private bool ProducesFreshValue(Expr expression)
    {
        switch (expression)
        {
            case Expr.Cons or Expr.RecordLit or Expr.RecordUpdate or Expr.TupleLit or Expr.ListLit:
                return true;
            case Expr.Call call:
                var arguments = new List<Expr>();
                Expr root = CollectCallArgs(call, arguments);
                if (root is not Expr.Var callee)
                {
                    return false;
                }

                if (_constructorSymbols.ContainsKey(callee.Name))
                {
                    return true;
                }

                Binding? binding = Lookup(callee.Name);
                return binding is Binding.Self
                    || TryResolveKnownFunctionLabel(root, out string label)
                        && TryGetCompiledFunctionResultRuntimeManaged(label, arguments.Count, out bool runtimeManaged)
                        && runtimeManaged;
            default:
                return false;
        }
    }

    // The parameter types the passthrough arm can copy into an owned reference-counted graph.
    // Strings stay out: their affine growth already decides their placement.
    private bool IsPassthroughNormalizableParameterType(TypeRef parameterType)
        => parameterType switch
        {
            TypeRef.TList list => CanRuntimeManageTcoListElement(list.Element),
            TypeRef.TNamedType named => CanCopyOutAdt(named, out _) || CanRuntimeManageTcoAdt(named),
            TypeRef.TTuple tuple => CanRuntimeManageOwnedTupleType(tuple),
            _ => false,
        };

    // Copies a branch result that is a plain read of the passthrough parameter into an owned
    // reference-counted graph, so the join it flows into is reference-counted on every branch.
    private int NormalizeParameterPassthroughBranch(Expr branch, int branchTemp)
    {
        if (_passthroughParameter is not { } parameter || IsRuntimeManagedResultTemp(branchTemp))
        {
            return branchTemp;
        }

        Expr result = branch;
        while (result is Expr.Let let)
        {
            result = let.Body;
        }

        if (ConsOntoSelfResultRuleEnabled
            && IsConsChainOntoSelfCall(result, parameter.Label)
            && Prune(parameter.Type) is TypeRef.TList list
            && IsPassthroughNormalizableParameterType(list))
        {
            int ownedSlot = ConsChainTailOwnedSlot(branchTemp);
            int movedTemp = EmitRuntimeManagedTcoListDeepCopyByCopy(branchTemp, list.Element, shareReferenceCountedSuffix: true, moveSharedSuffix: ownedSlot != -2, takenOverSlot: ownedSlot);
            MarkRuntimeManagedTemp(movedTemp, LoweredTempOwnershipReason.CopyOutNormalization, type: list);
            return movedTemp;
        }

        if (result is not Expr.Var variable
            || !string.Equals(variable.Name, parameter.Name, StringComparison.Ordinal)
            || Lookup(variable.Name) is not Binding.Local { Slot: int slot }
            || slot != parameter.Slot)
        {
            return branchTemp;
        }

        TypeRef parameterType = Prune(parameter.Type);
        if (!IsPassthroughNormalizableParameterType(parameterType))
        {
            return branchTemp;
        }

        return EmitOwnedPassthroughCopy(branchTemp, parameterType);
    }

    private static bool ConsOntoSelfResultRuleEnabled
        => Environment.GetEnvironmentVariable("GRC_NO_CONSONTOSELFRESULT") is null;

    // A branch consing arena cells onto the function's own full recursive call: that call's result
    // was promised reference-counted, so the last cell holds an owned reference nothing else
    // releases. The branch copies its cells onto the heap and links that result as their tail,
    // taking the reference over, so the join is owned on every branch and copied no further.
    private bool IsConsChainOntoSelfCall(Expr expression, string label)
    {
        Expr tail = expression;
        if (tail is not Expr.Cons)
        {
            return false;
        }

        while (tail is Expr.Cons cons)
        {
            tail = cons.Tail;
        }

        if (tail is not Expr.Call call)
        {
            return false;
        }

        var arguments = new List<Expr>();
        Expr root = CollectCallArgs(call, arguments);
        if (root is not Expr.Var callee)
        {
            return false;
        }

        Binding? binding = Lookup(callee.Name);
        string? selfLabel = binding is Binding.Self self
            ? self.FuncLabel
            : binding is not null && _selfCaptureLabels.TryGetValue(binding, out string? captured) ? captured : null;
        return selfLabel is not null && CurryStagesTo(selfLabel, label) == arguments.Count;
    }

    // Where the reference a cons chain's last arena cell holds to its tail is kept: the owned slot
    // keeping the recursive call's result, which the function's end releases when its result no
    // longer reaches it; -1 when the tail is a call result or a read of a slot nothing releases; -2
    // when the chain or its tail is not recognized, and the reference is retained rather than taken
    // over.
    private int ConsChainTailOwnedSlot(int valueTemp)
    {
        HashSet<int> ownedSlots = [.. _generalRcOwnedSlots.Select(owned => owned.Slot)];
        int current = ArenaCellOf(valueTemp);
        for (int hops = 0; hops < 64 && current >= 0; hops++)
        {
            int tailTemp = -1;
            for (int i = _inst.Count - 1; i >= 0; i--)
            {
                if (_inst[i] is IrInst.StoreMemOffset { OffsetBytes: 8 } store && store.BasePtr == current)
                {
                    tailTemp = store.Source;
                    break;
                }
            }

            switch (tailTemp < 0 ? null : DefinitionOf(tailTemp))
            {
                case IrInst.LoadLocal load when ownedSlots.Contains(load.Slot):
                    return load.Slot;
                case IrInst.LoadLocal:
                    return -1;
                case IrInst.CallClosure or IrInst.CallKnown:
                    return _inst.OfType<IrInst.StoreLocal>().FirstOrDefault(store => store.Source == tailTemp && ownedSlots.Contains(store.Slot))?.Slot ?? -1;
                case var cell when IsConsChainCell(cell):
                    current = tailTemp;
                    break;
                default:
                    return -2;
            }
        }

        return -2;
    }

    // A cons cell the chain builds here: an arena cell, or a list cell built over a reuse token
    // (the matched parameter's own cell), which is fresh whether the token's cell was on the
    // arena or the heap.
    private static bool IsConsChainCell(IrInst? definition)
        => definition is IrInst.Alloc { RuntimeManaged: false }
            || (definition is IrInst.AllocReusing { ListCell: true, RuntimeManaged: false }
                && Environment.GetEnvironmentVariable("GRC_NO_REUSEDCONSCELL") is null);

    // The arena cell a value is, directly or through the one store of the local it is read from.
    private int ArenaCellOf(int temp)
    {
        switch (DefinitionOf(temp))
        {
            case var cell when IsConsChainCell(cell):
                return temp;
            case IrInst.LoadLocal load:
                List<IrInst.StoreLocal> stores = [.. _inst.OfType<IrInst.StoreLocal>().Where(store => store.Slot == load.Slot)];
                return stores.Count == 1 && IsConsChainCell(DefinitionOf(stores[0].Source)) ? stores[0].Source : -1;
            default:
                return -1;
        }
    }

    // The number of curried stages from `selfLabel` up to and including `label`, or -1 when the
    // chain of returned closures from `selfLabel` does not reach it.
    private int CurryStagesTo(string selfLabel, string label)
    {
        string current = selfLabel;
        for (int stages = 1; stages <= 64; stages++)
        {
            if (string.Equals(current, label, StringComparison.Ordinal))
            {
                return stages;
            }

            if (!_functionReturnedClosureLabels.TryGetValue(current, out string? next))
            {
                return -1;
            }

            current = next;
        }

        return -1;
    }

    private int EmitOwnedPassthroughCopy(int sourceTemp, TypeRef type)
    {
        int copiedTemp = EmitRuntimeManagedTcoParamCopy(sourceTemp, type);
        MarkRuntimeManagedTemp(copiedTemp, LoweredTempOwnershipReason.CopyOutNormalization, type: type);
        return copiedTemp;
    }

    // Promises the function's result reference-counted before its body is lowered, when the
    // passthrough parameter's type is already known to be copyable: the body's own recursive call
    // sites then hand the result over as an owned value instead of leaving it unreleased.
    private void PredictRuntimeManagedResult(string label, TypeRef parameterType)
    {
        if (_passthroughParameter is null || !IsPassthroughNormalizableParameterType(Prune(parameterType)))
        {
            return;
        }

        _predictedRuntimeManagedResultLabels.Add(label);
        _bodyRuntimeManagedByLabel[label] = true;
    }

    // Keeps the promise made by PredictRuntimeManagedResult: a body result that did not end up
    // reference-counted is copied into an owned graph before it is returned.
    private int KeepPredictedRuntimeManagedResult(string label, int bodyTemp, TypeRef bodyType)
    {
        if (!_predictedRuntimeManagedResultLabels.Contains(label) || IsRuntimeManagedResultTemp(bodyTemp))
        {
            return bodyTemp;
        }

        TypeRef resultType = Prune(bodyType);
        return IsPassthroughNormalizableParameterType(resultType)
            ? EmitOwnedPassthroughCopy(bodyTemp, resultType)
            : throw new InvalidOperationException(
                $"Predicted runtime-managed result of {label} cannot be normalized: {Pretty(resultType)}.");
    }

    // Records the returned-closure link of a curry stage as soon as the stage's body lambda gets
    // its label, so a recursive call inside that body resolves through the chain while it is
    // still being lowered.
    private void BeginCurryStage(string label, Expr.Lambda lambda)
    {
        _curryStage = lambda.Body is Expr.Lambda ? (label, lambda.Body) : null;
    }

    private void LinkCurryStage(string label, Expr.Lambda lambda)
    {
        if (_curryStage is { } stage && ReferenceEquals(stage.Body, lambda))
        {
            _functionReturnedClosureLabels[stage.Label] = label;
            NoteLetBoundCurryStage(stage.Label, label);
        }

        _curryStage = null;
    }
}
