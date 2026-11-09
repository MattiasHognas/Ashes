using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    // The own-field record (GRC_NO_OWNFIELDRECORD), the taken-over record's second shape: a loop whose
    // result is an arena cell of a named type built at its exit around its own reference-counted
    // parameter (`if hops <= 0 then Some(label)`) retains the parameter for the cell, and nothing
    // releases an arena cell. A caller holding the result releases that reference once it is done with
    // the result: after the scalar call consuming it, at the end of a `let` or `match` holding it.
    // The record holds only when every value the loop can return is such a cell over parameters the
    // loop retained into it, a nullary constructor, or its own tail self-call, so every
    // reference-counted direct field of an arena result cell is a reference the cell owns.
    private static bool OwnFieldRecordRuleEnabled =>
        TakenOverRecordRuleEnabled && Environment.GetEnvironmentVariable("GRC_NO_OWNFIELDRECORD") is null;

    // Per loop label: the parameters a cell built in the loop retained outright, and those whose
    // retain waited on the loop's placement (which may leave a borrowed reference in the cell).
    private readonly Dictionary<string, HashSet<string>> _ownFieldRetainedParameters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _ownFieldPendingParameters = new(StringComparer.Ordinal);

    private void NoteOwnFieldParameterRetain(Expr argument, bool retained)
    {
        if (!OwnFieldRecordRuleEnabled
            || argument is not Expr.Var variable
            || _tcoCtx is not { SelfLabel: { Length: > 0 } label })
        {
            return;
        }

        Dictionary<string, HashSet<string>> notes = retained ? _ownFieldRetainedParameters : _ownFieldPendingParameters;
        if (!notes.TryGetValue(label, out HashSet<string>? names))
        {
            names = new HashSet<string>(StringComparer.Ordinal);
            notes[label] = names;
        }

        names.Add(variable.Name);
    }

    // The labels a call through `label` reaches along the recorded returned-closure chain.
    private List<string> ReturnedClosureChain(string label)
    {
        List<string> chain = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        string? current = label;
        while (current is not null && seen.Add(current))
        {
            chain.Add(current);
            current = _functionReturnedClosureLabels.TryGetValue(current, out string? next) ? next : null;
        }

        return chain;
    }

    private static bool IsOwnFieldCandidateType(TypeRef type, Func<TypeRef, bool> holdsHeap)
        => type is TypeRef.TNamedType named
            && named.Symbol.Constructors.Count > 0
            && named.Symbol.Constructors.Any(constructor => constructor.Arity > 0)
            && holdsHeap(named);

    private bool OwnFieldResultType(TypeRef resultType, out TypeRef.TNamedType named)
    {
        named = null!;
        if (Prune(resultType) is not TypeRef.TNamedType candidate
            || ContainsUnresolvedLayoutType(candidate, [])
            || !IsOwnFieldCandidateType(candidate, _ => HeapFieldTypes(candidate).Count > 0))
        {
            return false;
        }

        named = candidate;
        return true;
    }

    private List<TypeRef> HeapFieldTypes(TypeRef.TNamedType named)
    {
        List<TypeRef> types = [];
        foreach (ConstructorSymbol constructor in named.Symbol.Constructors)
        {
            for (int index = 0; index < constructor.Arity; index++)
            {
                TypeRef fieldType = Prune(InstantiateConstructorParameterType(constructor, index, named));
                if (!CanArenaReset(fieldType))
                {
                    types.Add(fieldType);
                }
            }
        }

        return types;
    }

    // The proof: the callee's every result is a nullary constructor, its own tail self-call, or a
    // constructor whose heap arguments are parameters the loop retained outright and never left to the
    // placement, unshadowed where they are read.
    private bool OwnFieldRecordProven(PendingTakenOverSettle settle)
    {
        string where = SourceNameOfLabel(settle.Label);
        if (settle.Callee is not { } key
            || !_maFuncs.TryGetValue(key, out var info)
            || !_maKeyName.TryGetValue(key, out string? selfName))
        {
            DeclineTakenOver("own-field-callee-unknown", where);
            return false;
        }

        HashSet<string> retained = new(StringComparer.Ordinal);
        HashSet<string> pending = new(StringComparer.Ordinal);
        foreach (string label in ReturnedClosureChain(settle.Label))
        {
            retained.UnionWith(_ownFieldRetainedParameters.GetValueOrDefault(label) ?? []);
            pending.UnionWith(_ownFieldPendingParameters.GetValueOrDefault(label) ?? []);
        }

        retained.ExceptWith(pending);
        HashSet<string> parameters = new(info.Params, StringComparer.Ordinal);
        parameters.IntersectWith(retained);
        if (parameters.Count == 0 || !OwnFieldLeaves(info.Body, parameters, selfName, info.Params.Count))
        {
            DeclineTakenOver("own-field-producer-borrows", where);
            return false;
        }

        return true;
    }

    private bool OwnFieldLeaves(Expr expression, HashSet<string> parameters, string selfName, int arity)
    {
        switch (StripOrSelf(expression))
        {
            case Expr.If iff:
                return OwnFieldLeaves(iff.Then, parameters, selfName, arity) && OwnFieldLeaves(iff.Else, parameters, selfName, arity);
            case Expr.Match match:
                foreach (MatchCase arm in match.Cases)
                {
                    HashSet<string> armParameters = new(parameters, StringComparer.Ordinal);
                    armParameters.ExceptWith(PatternBinderNames(arm.Pattern));
                    if (!OwnFieldLeaves(arm.Body, armParameters, selfName, arity))
                    {
                        return false;
                    }
                }

                return true;
            case Expr.Let let:
                {
                    HashSet<string> inner = new(parameters, StringComparer.Ordinal);
                    inner.Remove(let.Name);
                    return OwnFieldLeaves(let.Body, inner, selfName, arity);
                }
            case Expr.Var variable:
                return _constructorSymbols.TryGetValue(variable.Name, out ConstructorSymbol? nullary) && nullary.Arity == 0;
            case Expr.Call call:
                {
                    List<Expr> arguments = CallArguments(call, out Expr root);
                    if (root is not Expr.Var callee)
                    {
                        return false;
                    }

                    if (string.Equals(callee.Name, selfName, StringComparison.Ordinal) && arguments.Count == arity)
                    {
                        return true;
                    }

                    return _constructorSymbols.TryGetValue(callee.Name, out ConstructorSymbol? constructor)
                        && constructor.Arity == arguments.Count
                        && arguments.All(argument => OwnFieldArgument(argument, parameters));
                }
            default:
                return false;
        }
    }

    // A constructor argument the cell owns or that holds nothing reference-counted: a retained
    // parameter, or a scalar or static literal.
    private static bool OwnFieldArgument(Expr argument, HashSet<string> parameters)
        => argument switch
        {
            Expr.Var variable => parameters.Contains(variable.Name),
            Expr.IntLit or Expr.BoolLit or Expr.StrLit => true,
            _ => false,
        };

    private static HashSet<string> PatternBinderNames(Pattern pattern)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        CollectPatternBinderNames(pattern, names);
        return names;
    }

    private static void CollectPatternBinderNames(Pattern pattern, HashSet<string> names)
    {
        switch (pattern)
        {
            case Pattern.Var variable:
                names.Add(variable.Name);
                break;
            case Pattern.Tuple tuple:
                foreach (Pattern element in tuple.Elements)
                {
                    CollectPatternBinderNames(element, names);
                }

                break;
            case Pattern.Cons cons:
                CollectPatternBinderNames(cons.Head, names);
                CollectPatternBinderNames(cons.Tail, names);
                break;
            case Pattern.Constructor constructor:
                foreach (Pattern argument in constructor.Patterns)
                {
                    CollectPatternBinderNames(argument, names);
                }

                break;
            case Pattern.Record record:
                foreach ((string _, Pattern field) in record.Fields)
                {
                    CollectPatternBinderNames(field, names);
                }

                break;
            case Pattern.As asPattern:
                names.Add(asPattern.Name);
                CollectPatternBinderNames(asPattern.Inner, names);
                break;
            case Pattern.Or or:
                foreach (Pattern alternative in or.Alternatives)
                {
                    CollectPatternBinderNames(alternative, names);
                }

                break;
            default:
                // A literal, the empty list, or a wildcard binds nothing.
                break;
        }
    }

    // Releases the reference each reference-counted direct field of an arena result cell holds; a
    // cell on the reference-counted heap owns its fields already.
    private void EmitOwnFieldWalk(int resultTemp, TypeRef.TNamedType named)
    {
        string doneLabel = NewLabel("own_field_done");
        EmitSkipUnlessPresent(resultTemp, doneLabel);
        int countedTemp = NewTemp();
        Emit(new IrInst.IsReferenceCounted(countedTemp, resultTemp));
        string arenaLabel = NewLabel("own_field_arena_cell");
        Emit(new IrInst.JumpIfFalse(countedTemp, arenaLabel));
        Emit(new IrInst.Jump(doneLabel));
        Emit(new IrInst.Label(arenaLabel));
        TypeSymbol symbol = named.Symbol;
        int tagTemp = EmitAdtTag(resultTemp, symbol);
        List<(long Tag, string Label)> cases = symbol.Constructors
            .Select(constructor => ((long)GetConstructorTag(constructor), NewLabel("own_field_ctor")))
            .ToList();
        Emit(new IrInst.SwitchTag(tagTemp, cases, doneLabel));
        for (int i = 0; i < symbol.Constructors.Count; i++)
        {
            ConstructorSymbol constructor = symbol.Constructors[i];
            Emit(new IrInst.Label(cases[i].Label));
            bool tagless = IsTaglessConstructor(constructor);
            for (int index = 0; index < constructor.Arity; index++)
            {
                TypeRef fieldType = Prune(InstantiateConstructorParameterType(constructor, index, named));
                if (CanArenaReset(fieldType))
                {
                    continue;
                }

                string nextLabel = NewLabel("own_field_next");
                int childTemp = NewTemp();
                Emit(new IrInst.GetAdtField(childTemp, resultTemp, index, tagless));
                EmitSkipUnlessReferenceCounted(childTemp, nextLabel);
                EmitRuntimeManagedChildDrop(childTemp, fieldType);
                Emit(new IrInst.Label(nextLabel));
            }

            Emit(new IrInst.Jump(doneLabel));
        }

        Emit(new IrInst.Label(doneLabel));
    }

    // The end of a match whose scrutinee is a held call result: settled there when the match's result
    // cannot hold a value of any of the result's heap field types.
    private void SettleMatchScopedTakenOver(Expr.Match match, TypeRef matchType)
    {
        if (!OwnFieldRecordRuleEnabled || StripOrSelf(match.Value) is not Expr.Call valueCall)
        {
            return;
        }

        int index = _takenOverHeldResults.FindIndex(held => ReferenceEquals(held.Call, valueCall));
        if (index < 0)
        {
            return;
        }

        (Expr.Call call, int temp, TypeRef heldType) = _takenOverHeldResults[index];
        if (!OwnFieldResultType(heldType, out TypeRef.TNamedType named))
        {
            return;
        }

        TypeRef resultType = Prune(matchType);
        if (HeapFieldTypes(named).Any(fieldType => ContainsResolvedType(resultType, fieldType, [])))
        {
            DeclineTakenOver("match-result-holds-field", SourceNameOfLabel(_loweringFunctionLabels.TryPeek(out string? l) ? l : "?"));
            return;
        }

        _takenOverHeldResults.RemoveAt(index);
        if (TryNewTakenOverSettle(call, heldType, out PendingTakenOverSettle? settle))
        {
            int id = _nextTakenOverSettleId++;
            _pendingTakenOverSettles[id] = settle!;
            AnchorTakenOverSettle(_inst.Count > 0 ? _inst[^1] : null, id, temp);
        }
    }
}
