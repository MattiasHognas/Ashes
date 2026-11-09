using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    // The exit release of an arena loop parameter's own fields (GRC_NO_EXITPARAMOWNFIELDS). A loop
    // parameter left in the arena (a linear-reuse accumulator, `agreed`) whose tail self-call builds its
    // successor as a constructor retaining its heap fields (`None -> loop(tail)(Some(label))`) carries an
    // arena cell owning a reference nothing releases: the arena is never walked. A flag per such
    // parameter records whether its current value is a cell this activation built: set by a constructor
    // successor, cleared by a nullary one, kept by passing the parameter on, zero at the loop's entry so
    // a caller's value is never touched. At the return, once the result holds references of its own, a
    // set flag releases the reference-counted direct fields of the arena cell.
    //
    // The shape is checked before the body is lowered: every tail self-call hands the parameter on, a
    // nullary constructor, or a constructor built in an arm where the parameter was matched nullary (so
    // the cell it replaces held nothing). Each constructor successor's heap fields must be retains once
    // the loop's ownership is finalized; any other shape declines.
    private static bool ExitParameterOwnFieldsRuleEnabled =>
        Environment.GetEnvironmentVariable("GRC_NO_EXITPARAMOWNFIELDS") is null;

    private sealed class ExitParameterOwnFields
    {
        public required int Slot { get; init; }

        public required int FlagSlot { get; init; }

        public required int Ordinal { get; init; }

        public List<(int Cell, List<int> Stored)> Constructions { get; } = [];
    }

    private readonly Dictionary<TcoContext, List<ExitParameterOwnFields>> _exitParameterOwnFields = new(ReferenceEqualityComparer.Instance);

    // Before the loop's entry: a flag, zeroed, per parameter of the checked shape.
    private void PrepareExitParameterOwnFields(Expr.Lambda lam, TcoContext tco)
    {
        if (!GeneralRcEnabled || !ExitParameterOwnFieldsRuleEnabled || tco.SelfName is not { Length: > 0 } selfName)
        {
            return;
        }

        Expr body = GetInnermostBody(lam);
        int arity = tco.ParamSlots.Count;
        List<ExitParameterOwnFields> tracked = [];
        int zeroTemp = -1;
        for (int ordinal = 0; ordinal < arity && ordinal < tco.ParamNames.Count; ordinal++)
        {
            string name = tco.ParamNames[ordinal];
            bool constructs = false;
            if (!ExitParameterSuccessorsQualify(body, selfName, ordinal, name, arity, nullaryKnown: false, ref constructs) || !constructs)
            {
                continue;
            }

            if (zeroTemp < 0)
            {
                zeroTemp = NewTemp();
                Emit(new IrInst.LoadConstInt(zeroTemp, 0));
            }

            int flagSlot = NewLocal();
            Emit(new IrInst.StoreLocal(flagSlot, zeroTemp));
            tracked.Add(new ExitParameterOwnFields { Slot = tco.ParamSlots[ordinal], FlagSlot = flagSlot, Ordinal = ordinal });
        }

        if (tracked.Count > 0)
        {
            _exitParameterOwnFields[tco] = tracked;
        }
    }

    private bool ExitParameterSuccessorsQualify(Expr expression, string selfName, int ordinal, string name, int arity, bool nullaryKnown, ref bool constructs)
    {
        switch (StripOrSelf(expression))
        {
            case Expr.If iff:
                return ExitParameterSuccessorsQualify(iff.Then, selfName, ordinal, name, arity, nullaryKnown, ref constructs)
                    && ExitParameterSuccessorsQualify(iff.Else, selfName, ordinal, name, arity, nullaryKnown, ref constructs);
            case Expr.Match match:
                {
                    bool onParameter = StripOrSelf(match.Value) is Expr.Var scrutinee && string.Equals(scrutinee.Name, name, StringComparison.Ordinal);
                    foreach (MatchCase arm in match.Cases)
                    {
                        if (PatternBinderNames(arm.Pattern).Contains(name))
                        {
                            return false;
                        }

                        bool armNullary = nullaryKnown || (onParameter && IsNullaryConstructorPattern(arm.Pattern));
                        if (!ExitParameterSuccessorsQualify(arm.Body, selfName, ordinal, name, arity, armNullary, ref constructs))
                        {
                            return false;
                        }
                    }

                    return true;
                }
            case Expr.Let let:
                return !string.Equals(let.Name, name, StringComparison.Ordinal)
                    && ExitParameterSuccessorsQualify(let.Body, selfName, ordinal, name, arity, nullaryKnown, ref constructs);
            case Expr.Call call:
                {
                    List<Expr> arguments = CallArguments(call, out Expr root);
                    if (root is not Expr.Var callee
                        || !string.Equals(callee.Name, selfName, StringComparison.Ordinal)
                        || arguments.Count != arity)
                    {
                        return true;
                    }

                    switch (ExitParameterSuccessorKind(arguments[ordinal], name))
                    {
                        case 0:
                        case 1:
                            return true;
                        case 2:
                            constructs = true;
                            return nullaryKnown;
                        default:
                            return false;
                    }
                }
            default:
                return true;
        }
    }

    // 0: the parameter handed on; 1: a nullary constructor; 2: a constructor application; -1: anything else.
    private int ExitParameterSuccessorKind(Expr argument, string name)
    {
        Expr stripped = StripOrSelf(argument);
        if (stripped is Expr.Var variable)
        {
            if (string.Equals(variable.Name, name, StringComparison.Ordinal))
            {
                return 0;
            }

            return _constructorSymbols.TryGetValue(variable.Name, out ConstructorSymbol? nullary) && nullary.Arity == 0 ? 1 : -1;
        }

        if (stripped is Expr.Call call)
        {
            List<Expr> arguments = CallArguments(call, out Expr root);
            return root is Expr.Var constructorName
                && _constructorSymbols.TryGetValue(constructorName.Name, out ConstructorSymbol? constructor)
                && constructor.Arity == arguments.Count
                ? 2
                : -1;
        }

        return -1;
    }

    private bool IsNullaryConstructorPattern(Pattern pattern)
        => pattern switch
        {
            Pattern.Constructor { Patterns.Count: 0 } constructor => _constructorSymbols.TryGetValue(constructor.Name, out ConstructorSymbol? symbol) && symbol.Arity == 0,
            Pattern.Var variable => _constructorSymbols.TryGetValue(variable.Name, out ConstructorSymbol? symbol) && symbol.Arity == 0,
            _ => false,
        };

    // After a tail self-call argument is lowered into a tracked parameter's position: the flag follows
    // the successor, and a constructor successor's stored fields are kept for the check at the return.
    private void NoteExitParameterSuccessor(TcoContext tco, int index, Expr argument, int loweredTemp)
    {
        if (!_exitParameterOwnFields.TryGetValue(tco, out List<ExitParameterOwnFields>? tracked)
            || index >= tco.ParamSlots.Count
            || index >= tco.ParamNames.Count
            || tracked.Find(entry => entry.Slot == tco.ParamSlots[index]) is not { } entry)
        {
            return;
        }

        int kind = ExitParameterSuccessorKind(argument, tco.ParamNames[index]);
        if (kind == 0)
        {
            return;
        }

        int valueTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(valueTemp, kind == 2 ? 1 : 0));
        Emit(new IrInst.StoreLocal(entry.FlagSlot, valueTemp));
        if (kind == 2)
        {
            List<int> stored = [];
            for (int field = 0; ConstructionFieldSource(loweredTemp, field) is int source; field++)
            {
                stored.Add(source);
            }

            entry.Constructions.Add((loweredTemp, stored));
        }
    }

    // The loop's exit: the runtime-managed parameter drops, then the arena parameters' own fields.
    private void LowerLambdaCoreEmitTcoExitReleases(Expr.Lambda lam, TcoContext? tco, int bodyTemp, TypeRef bodyType, bool bodyRuntimeManaged)
    {
        LowerLambdaCoreEmitRuntimeManagedTcoExitDrops(lam, tco, bodyTemp);
        EmitExitParameterOwnFieldReleases(tco, bodyTemp, bodyType, bodyRuntimeManaged);
    }

    // At the return, ahead of the result's normalization: a set flag releases the arena cell's own
    // fields, when every constructor successor's heap fields are retains and the result cannot hold the
    // released fields without a reference of its own.
    private void EmitExitParameterOwnFieldReleases(TcoContext? tco, int bodyTemp, TypeRef bodyType, bool bodyRuntimeManaged)
    {
        if (tco is null || !_exitParameterOwnFields.Remove(tco, out List<ExitParameterOwnFields>? tracked))
        {
            return;
        }

        foreach (ExitParameterOwnFields entry in tracked)
        {
            if (tco.IsRuntimeManagedSlot(entry.Slot)
                || !tco.ParamTypes.TryGetValue(entry.Ordinal, out TypeRef? parameterType)
                || Prune(parameterType) is not TypeRef.TNamedType named
                || ContainsUnresolvedLayoutType(named, [])
                || entry.Constructions.Count == 0)
            {
                continue;
            }

            List<TypeRef> heapFields = HeapFieldTypes(named);
            bool resultOwns = bodyRuntimeManaged
                || !heapFields.Any(fieldType => ContainsResolvedType(Prune(bodyType), fieldType, []));
            if (!resultOwns || !entry.Constructions.All(construction => ExitParameterConstructionRetains(construction.Cell, construction.Stored, named)))
            {
                DeclineTakenOver("exit-parameter-own-fields", SourceNameOfLabel(tco.SelfLabel));
                continue;
            }

            string skipLabel = NewLabel("exit_param_own_fields_skip");
            int flagTemp = NewTemp();
            Emit(new IrInst.LoadLocal(flagTemp, entry.FlagSlot));
            Emit(new IrInst.JumpIfFalse(flagTemp, skipLabel));
            int valueTemp = NewTemp();
            Emit(new IrInst.LoadLocal(valueTemp, entry.Slot));
            EmitOwnFieldWalk(valueTemp, named);
            Emit(new IrInst.Label(skipLabel));
        }
    }

    // A constructor successor holds its heap fields as its own: an arena cell whose every stored
    // reference-counted field is a realized retain.
    private bool ExitParameterConstructionRetains(int cell, List<int> stored, TypeRef.TNamedType named)
    {
        if (!IsArenaCellConstruction(cell))
        {
            return false;
        }

        foreach (int source in stored)
        {
            if (!IsDefinedByRealizedRetain(source) && !IsScalarOrStaticStoredValue(source))
            {
                return false;
            }
        }

        return true;
    }

    // A stored word that holds no reference: a constant.
    private bool IsScalarOrStaticStoredValue(int temp)
    {
        int index = FindLastInstruction(instruction => StateMachineTransform.GetDefinedTemps(instruction).Contains(temp));
        return index >= 0 && _inst[index] is IrInst.LoadConstInt or IrInst.LoadConstBool or IrInst.LoadConstStr;
    }
}
