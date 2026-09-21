using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    // A call whose result type was still a variable when it was lowered: typically a call to a later
    // sibling of a recursive group, which is lowered only after its caller. The ownership contract
    // decides at the call (and at the caller's own return) from the result type, so a type inference
    // settles only later arrives too late. The discovery pass records such sites, and the types its
    // whole-program substitution settled are pinned at the same sites by the emitting pass.
    private readonly List<(string Key, TypeRef Type)> _pendingCallResultTypes = [];

    private readonly Dictionary<string, TypeRef> _provenCallResultTypes = new(StringComparer.Ordinal);

    // A call site, stable across the discovery and emitting passes: the callee expression's span and
    // how many arguments the call applied.
    private static string? CallResultTypeKey(Expr rootExpr, int argumentCount)
    {
        TextSpan span = AstSpans.GetOrDefault(rootExpr);
        return span.Length == 0 ? null : $"{span.Start}:{span.End}:{argumentCount}";
    }

    private void RecordOrPinCallResultType(Expr rootExpr, int argumentCount, TypeRef callResultType)
    {
        if (!ContainsUnsettledVariable(callResultType)
            || CallResultTypeKey(rootExpr, argumentCount) is not { } key)
        {
            return;
        }

        if (_collectInferredTraitElaboration)
        {
            _pendingCallResultTypes.Add((key, callResultType));
        }
        else if (_provenCallResultTypes.TryGetValue(key, out TypeRef? proven)
            && TranslateProvenType(proven) is { } translated)
        {
            Unify(callResultType, translated);
        }
    }

    /// <summary>
    /// The recorded sites whose type this pass's own substitution has since settled on one concrete,
    /// closure-free type. A site lowered more than once (a derived implementation per type, say) that
    /// settled differently is left unpinned.
    /// </summary>
    private Dictionary<string, TypeRef> ResolveProvenCallResultTypes()
    {
        var proven = new Dictionary<string, TypeRef>(StringComparer.Ordinal);
        var conflicting = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string key, TypeRef type) in _pendingCallResultTypes)
        {
            TypeRef resolved = PruneConstraintType(type);
            if (!IsPinnableType(resolved))
            {
                conflicting.Add(key);
                continue;
            }

            if (proven.TryGetValue(key, out TypeRef? earlier) && !TypesStructurallyEqual(earlier, resolved))
            {
                conflicting.Add(key);
            }

            proven[key] = resolved;
        }

        foreach (string key in conflicting)
        {
            proven.Remove(key);
        }

        return proven;
    }

    private bool ContainsUnsettledVariable(TypeRef type) => Prune(type) switch
    {
        TypeRef.TVar => true,
        TypeRef.TList list => ContainsUnsettledVariable(list.Element),
        TypeRef.TTuple tuple => tuple.Elements.Any(ContainsUnsettledVariable),
        TypeRef.TNamedType named => named.TypeArgs.Any(ContainsUnsettledVariable),
        TypeRef.TPtr pointer => ContainsUnsettledVariable(pointer.Pointee),
        _ => false,
    };

    // Only inline values, strings, lists, tuples and named types are carried over: a function type
    // brings a capability row, and a type still holding a variable says nothing settled.
    private static bool IsPinnableType(TypeRef type) => type switch
    {
        TypeRef.TVar or TypeRef.TFun or TypeRef.TRow or TypeRef.TCapability => false,
        TypeRef.TList list => IsPinnableType(list.Element),
        TypeRef.TTuple tuple => tuple.Elements.All(IsPinnableType),
        TypeRef.TNamedType named => named.TypeArgs.All(IsPinnableType),
        TypeRef.TPtr pointer => IsPinnableType(pointer.Pointee),
        _ => true,
    };

    // The parameter types of a generic function at a call that could route to its element
    // specialization, recorded where one was still a variable (the function's own result, say, when
    // the call sits in that function's body) and pinned where the discovery pass settled them.
    private readonly List<(string Key, List<TypeRef> Types)> _pendingSpecializationTypes = [];

    private readonly Dictionary<string, List<TypeRef>> _provenSpecializationTypes = new(StringComparer.Ordinal);

    private void RecordOrPinSpecializationTypes(Expr rootExpression, List<TypeRef> parameterTypes)
    {
        if (!parameterTypes.Exists(ValueTypeRemainsAbstract)
            || CallResultTypeKey(rootExpression, parameterTypes.Count) is not { } key)
        {
            return;
        }

        if (_collectInferredTraitElaboration)
        {
            _pendingSpecializationTypes.Add((key, parameterTypes));
        }
        else if (_provenSpecializationTypes.TryGetValue(key, out List<TypeRef>? proven) && proven.Count == parameterTypes.Count)
        {
            for (int index = 0; index < proven.Count; index++)
            {
                PinSettledParts(parameterTypes[index], proven[index]);
            }
        }
    }

    // Carries the call-site types the discovery pass settled over to this, the emitting, pass.
    private void CopyProvenCallSiteTypes(Lowering discovery)
    {
        foreach ((string key, TypeRef type) in discovery.ResolveProvenCallResultTypes())
        {
            _provenCallResultTypes[key] = type;
        }

        foreach ((string key, List<TypeRef> types) in discovery.ResolveProvenSpecializationTypes())
        {
            _provenSpecializationTypes[key] = types;
        }
    }

    private Dictionary<string, List<TypeRef>> ResolveProvenSpecializationTypes()
    {
        var proven = new Dictionary<string, List<TypeRef>>(StringComparer.Ordinal);
        var conflicting = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string key, List<TypeRef> types) in _pendingSpecializationTypes)
        {
            List<TypeRef> resolved = types.Select(PruneConstraintType).ToList();
            if (proven.TryGetValue(key, out List<TypeRef>? earlier)
                && !earlier.Zip(resolved).All(pair => TypesStructurallyEqual(pair.First, pair.Second)))
            {
                conflicting.Add(key);
            }

            proven[key] = resolved;
        }

        foreach (string key in conflicting)
        {
            proven.Remove(key);
        }

        return proven;
    }

    // Unifies each variable of this pass's type with the settled, closure-free part of the discovery
    // pass's type at the same position; a function type is walked through its argument and result,
    // never its capability row.
    private void PinSettledParts(TypeRef current, TypeRef proven)
    {
        TypeRef pruned = Prune(current);
        switch (pruned, proven)
        {
            case (TypeRef.TVar, _) when IsPinnableType(proven) && TranslateProvenType(proven) is { } translated:
                Unify(pruned, translated);
                break;
            case (TypeRef.TFun function, TypeRef.TFun provenFunction):
                PinSettledParts(function.Arg, provenFunction.Arg);
                PinSettledParts(function.Ret, provenFunction.Ret);
                break;
            case (TypeRef.TList list, TypeRef.TList provenList):
                PinSettledParts(list.Element, provenList.Element);
                break;
            case (TypeRef.TTuple tuple, TypeRef.TTuple provenTuple) when tuple.Elements.Count == provenTuple.Elements.Count:
                for (int index = 0; index < tuple.Elements.Count; index++)
                {
                    PinSettledParts(tuple.Elements[index], provenTuple.Elements[index]);
                }

                break;
            case (TypeRef.TNamedType named, TypeRef.TNamedType provenNamed)
                when string.Equals(named.Symbol.Name, provenNamed.Symbol.Name, StringComparison.Ordinal)
                    && named.TypeArgs.Count == provenNamed.TypeArgs.Count:
                for (int index = 0; index < named.TypeArgs.Count; index++)
                {
                    PinSettledParts(named.TypeArgs[index], provenNamed.TypeArgs[index]);
                }

                break;
        }
    }

    // The discovery pass's type rebuilt over this pass's own type symbols, or null when one of its
    // named types is unknown here.
    private TypeRef? TranslateProvenType(TypeRef type)
    {
        switch (type)
        {
            case TypeRef.TList list:
                return TranslateProvenType(list.Element) is { } listElement ? new TypeRef.TList(listElement) : null;
            case TypeRef.TPtr pointer:
                return TranslateProvenType(pointer.Pointee) is { } pointee ? new TypeRef.TPtr(pointee) : null;
            case TypeRef.TTuple tuple:
                {
                    var elements = new TypeRef[tuple.Elements.Count];
                    for (int i = 0; i < elements.Length; i++)
                    {
                        if (TranslateProvenType(tuple.Elements[i]) is not { } element)
                        {
                            return null;
                        }

                        elements[i] = element;
                    }

                    return new TypeRef.TTuple(elements);
                }
            case TypeRef.TNamedType named:
                {
                    if (!_typeSymbols.TryGetValue(named.Symbol.Name, out TypeSymbol? symbol))
                    {
                        return null;
                    }

                    var arguments = new TypeRef[named.TypeArgs.Count];
                    for (int i = 0; i < arguments.Length; i++)
                    {
                        if (TranslateProvenType(named.TypeArgs[i]) is not { } argument)
                        {
                            return null;
                        }

                        arguments[i] = argument;
                    }

                    return new TypeRef.TNamedType(symbol, arguments);
                }
            default:
                return type;
        }
    }
}
