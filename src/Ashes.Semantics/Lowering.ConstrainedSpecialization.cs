using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    private static bool ConstrainedSpecializationEnabled
        => Environment.GetEnvironmentVariable("GRC_NO_CONSTRAINEDSPECIALIZATION") is null;

    /// <summary>
    /// Top-level dictionary-passing generic functions whose generic body could not settle what it holds:
    /// a cons it declined because the element was still a type variable, or a call to another such
    /// function at its own abstract types. A call that fixes every parameter type routes to a copy
    /// lowered from the function's own source at those types, where each trait goal resolves to its
    /// instance and no dictionary is passed.
    /// </summary>
    private sealed record ConstrainedSpecializationCandidate(ElementSpecializationCandidate Copy, TypeScheme Scheme);

    private readonly Dictionary<string, ConstrainedSpecializationCandidate> _constrainedSpecializationCandidates =
        new(StringComparer.Ordinal);

    // Labels of the constrained copies: inside one, the function's own recursion is a plain call to the
    // copy, never a dictionary call to the generic function.
    private readonly HashSet<string> _constrainedSpecializationLabels = new(StringComparer.Ordinal);

    // The parameter types at calls to top-level generic functions, recorded by the discovery pass and
    // carried over where its whole-program substitution settled them: a loop parameter's type is often
    // fixed only by the very call that would route.
    private readonly List<(string Key, List<TypeRef> Types)> _pendingConstrainedCallTypes = [];

    private readonly Dictionary<string, List<TypeRef>> _provenConstrainedCallTypes = new(StringComparer.Ordinal);

    private void RegisterConstrainedSpecializationCandidate(
        string name,
        Expr binder,
        Expr value,
        TypeScheme? scheme,
        bool usesTraitDictionary,
        int declinesBeforeValue)
    {
        if (!ConstrainedSpecializationEnabled
            || !_configuration.EnableElementSpecialization
            || !_emitTraitDictionaries
            || _lambdaDepth != 0
            || _elementSpecializationDepth != 0
            || (_abstractElementTmcDeclines == declinesBeforeValue && binder is not Expr.LetRecursive)
            || value is not Expr.Lambda lambda
            || scheme is null
            || scheme.Quantified.Count == 0
            || !ContainsFreeTypeVariable(scheme.Body)
            || (!usesTraitDictionary && (_topLevelFunctionRefs.ContainsKey(name) || !MentionsConstrainedCandidate(lambda))))
        {
            return;
        }

        string genericLabel = _topLevelFunctionRefs.TryGetValue(name, out var reference)
            ? reference.Label
            : $"constrained_{name}";
        _constrainedSpecializationCandidates[name] = new ConstrainedSpecializationCandidate(
            new ElementSpecializationCandidate(name, genericLabel, binder, lambda, CountLambdaChain(lambda)),
            scheme);
    }

    // The root names the registered top-level function, not a local binding of the same name.
    private bool NamesConstrainedCandidate(string name, ConstrainedSpecializationCandidate constrained)
        => Lookup(name) switch
        {
            Binding.Scheme scheme => SameConstrainedScheme(scheme.S, constrained.Scheme),
            Binding.EnvScheme captured => SameConstrainedScheme(captured.S, constrained.Scheme),
            null when _elementSpecializationDepth > 0 => true,
            _ => TryResolveKnownFunctionLabel(name, out string label)
                && string.Equals(label, constrained.Copy.GenericLabel, StringComparison.Ordinal),
        };

    // A closure capturing the function carries its scheme over again: the same quantified shape.
    private bool SameConstrainedScheme(TypeScheme bound, TypeScheme registered)
        => ReferenceEquals(bound, registered)
            || (bound.Quantified.Count == registered.Quantified.Count
                && bound.Constraints.Count == registered.Constraints.Count
                && string.Equals(Pretty(Prune(bound.Body)), Pretty(Prune(registered.Body)), StringComparison.Ordinal));

    // A generic function that captures a constrained candidate has no label of its own and is no
    // element specialization candidate; it is lowered again at its caller's types like the candidate.
    private bool MentionsConstrainedCandidate(Expr.Lambda lambda)
    {
        foreach (Expr? node in EnumerateFlowNodes(lambda))
        {
            if (node is null)
            {
                return false;
            }

            if (node is Expr.Var { Name: var name } && _constrainedSpecializationCandidates.ContainsKey(name))
            {
                return true;
            }
        }

        return false;
    }

    // A constrained copy's call to itself: lowered as an ordinary self call.
    private bool IsConstrainedSpecializationSelfCall(Expr rootExpression)
        => _constrainedSpecializationLabels.Count > 0
            && rootExpression is Expr.Var variable
            && Lookup(variable.Name) is Binding.Self self
            && _constrainedSpecializationLabels.Contains(self.FuncLabel);

    /// <summary>
    /// Routes a call to a constrained candidate whose parameter types are all settled to the candidate's
    /// copy at those types: the copy's closure is bound under a fresh name and the call is lowered as an
    /// ordinary call to it, so its ownership summary, result contract and argument handling are the
    /// copy's own. Returns null when the call stays a dictionary call.
    /// </summary>
    private (int Temp, TypeRef Type)? TryLowerConstrainedSpecializedCall(
        Expr.Call call,
        Expr rootExpression,
        List<Expr> arguments,
        LoweredValueRequest request)
    {
        if (_constrainedSpecializationCandidates.Count == 0
            || _elementSpecializationDepth >= ElementSpecializationDepthLimit
            || ResolveSpecializableCalleeName(rootExpression) is not { } name
            || !_constrainedSpecializationCandidates.TryGetValue(name, out ConstrainedSpecializationCandidate? constrained)
            || !NamesConstrainedCandidate(name, constrained))
        {
            return null;
        }

        ElementSpecializationCandidate candidate = constrained.Copy;
        if (arguments.Count != candidate.ChainArity)
        {
            // A generic body threads its own evidence into the call: a call at its abstract types.
            if (arguments.Count > candidate.ChainArity
                && arguments[0] is Expr.Var { Name: var evidence }
                && evidence.StartsWith("__trait_evidence_", StringComparison.Ordinal))
            {
                _abstractElementTmcDeclines++;
            }

            return null;
        }

        TypeRef calleeType = InstantiateScheme(constrained.Scheme).Body;
        List<TypeRef> parameterTypes = CollectAppliedParameterTypes(calleeType, arguments.Count);
        if (parameterTypes.Count != arguments.Count
            || !SettleConstrainedCallTypes(rootExpression, arguments, parameterTypes))
        {
            // The caller is generic over the same types: a call it cannot settle is evidence, like a cons
            // it declined, that it is a candidate itself.
            _abstractElementTmcDeclines++;
            return null;
        }

        if (GetOrCreateElementSpecialization(candidate, parameterTypes, constrained: true) is not { } label)
        {
            return null;
        }

        (string rootName, int scopeDepth) = BindConstrainedSpecializationRoot(label, rootExpression, calleeType);
        try
        {
            return LowerCallGeneral(call, new Expr.Var(rootName), arguments, request);
        }
        finally
        {
            LeaveElementSpecializationRoot(scopeDepth);
        }
    }

    // The copy's closure, bound under a fresh name in a scope of its own, so every call-site fact that
    // resolves the root describes the copy actually called.
    private (string RootName, int ScopeDepth) BindConstrainedSpecializationRoot(string label, Expr rootExpression, TypeRef calleeType)
    {
        int environmentTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(environmentTemp, 0));
        int closureTemp = NewTemp();
        Emit(new IrInst.MakeClosure(
            closureTemp,
            label,
            environmentTemp,
            0,
            ReturnsRuntimeManaged: _bodyRuntimeManagedByLabel.GetValueOrDefault(label),
            AcceptsRuntimeManagedArgument: _runtimeNormalizedFunctionArgumentLabels.Contains(label)));

        string rootName = $"__constrained_specialization_{_elementSpecializations.Count}_{closureTemp}";
        if (CalleeSaturatedResultIsQuantifiedList(rootExpression))
        {
            _quantifiedListResultSpecializationRoots.Add(rootName);
        }

        int slot = NewLocal();
        Emit(new IrInst.StoreLocal(slot, closureTemp));
        _knownFunctionLabelsBySlot[slot] = label;
        int scopeDepth = _scopes.Count;
        _scopes.Push(_scopes.Peek().SetItem(rootName, new Binding.Local(slot, Prune(calleeType))));
        return (rootName, scopeDepth);
    }

    // Pins the call's parameter types from what the discovery pass settled at the same site, or else from
    // arguments that are variables of a settled type, and reports whether every parameter is concrete.
    private bool SettleConstrainedCallTypes(Expr rootExpression, List<Expr> arguments, List<TypeRef> parameterTypes)
    {
        if (CallResultTypeKey(rootExpression, arguments.Count) is { } key
            && _provenConstrainedCallTypes.TryGetValue(key, out List<TypeRef>? proven)
            && proven.Count == parameterTypes.Count)
        {
            for (int index = 0; index < proven.Count; index++)
            {
                PinSettledParts(parameterTypes[index], proven[index]);
            }
        }

        for (int index = 0; index < arguments.Count; index++)
        {
            if (arguments[index] is Expr.Var variable
                && IsSettlingArgumentBinding(Lookup(variable.Name))
                && Prune(Lookup(variable.Name)!.Type) is { } argumentType
                && !ContainsFreeTypeVariable(PruneConstraintType(argumentType))
                && IsPinnableType(PruneConstraintType(argumentType))
                && ParameterTypeAccepts(parameterTypes[index], PruneConstraintType(argumentType)))
            {
                Unify(parameterTypes[index], argumentType);
            }
        }

        return !parameterTypes.Exists(ValueTypeRemainsAbstract);
    }

    // A parameter or a capture, and a let-bound value alike: once its type holds no variable it says
    // as much as a parameter's. The program entry binds every argument it passes that way.
    private static bool IsSettlingArgumentBinding(Binding? binding)
        => binding is Binding.Local or Binding.Env or Binding.Self
            || (binding is Binding.Scheme or Binding.EnvScheme
                && Environment.GetEnvironmentVariable("GRC_NO_LETBOUNDCONSTRAINEDARGUMENT") is null);

    // Whether a settled argument type fits the parameter's shape, so unifying them reports nothing.
    private bool ParameterTypeAccepts(TypeRef parameter, TypeRef argument)
    {
        TypeRef pruned = Prune(parameter);
        return (pruned, argument) switch
        {
            (TypeRef.TVar, _) => true,
            (TypeRef.TList list, TypeRef.TList argumentList) => ParameterTypeAccepts(list.Element, argumentList.Element),
            (TypeRef.TPtr pointer, TypeRef.TPtr argumentPointer) => ParameterTypeAccepts(pointer.Pointee, argumentPointer.Pointee),
            (TypeRef.TTuple tuple, TypeRef.TTuple argumentTuple) =>
                tuple.Elements.Count == argumentTuple.Elements.Count
                && tuple.Elements.Zip(argumentTuple.Elements).All(pair => ParameterTypeAccepts(pair.First, pair.Second)),
            (TypeRef.TNamedType named, TypeRef.TNamedType argumentNamed) =>
                ReferenceEquals(named.Symbol, argumentNamed.Symbol)
                && named.TypeArgs.Count == argumentNamed.TypeArgs.Count
                && named.TypeArgs.Zip(argumentNamed.TypeArgs).All(pair => ParameterTypeAccepts(pair.First, pair.Second)),
            (TypeRef.TFun, _) or (_, TypeRef.TFun) => false,
            _ => pruned.GetType() == argument.GetType() && TypesStructurallyEqual(pruned, argument),
        };
    }

    // Discovery pass: the parameter types at a call to a top-level generic function, as the pass's
    // substitution will have settled them by its end.
    private void RecordConstrainedCallTypes(Expr rootExpression, int argumentCount, TypeRef calleeType)
    {
        if (!_collectInferredTraitElaboration
            || !ConstrainedSpecializationEnabled
            || _elementSpecializationDepth != 0
            || ResolveSpecializableCalleeName(rootExpression) is not { } name
            || (rootExpression is Expr.Var && Lookup(name) is not null)
            || !_topLevelFunctionRefs.TryGetValue(name, out var reference)
            || reference.Scheme.Quantified.Count == 0
            || CallResultTypeKey(rootExpression, argumentCount) is not { } key)
        {
            return;
        }

        List<TypeRef> types = CollectAppliedParameterTypes(calleeType, argumentCount);
        if (types.Count == argumentCount)
        {
            _pendingConstrainedCallTypes.Add((key, types));
        }
    }

    private Dictionary<string, List<TypeRef>> ResolveProvenConstrainedCallTypes()
    {
        var proven = new Dictionary<string, List<TypeRef>>(StringComparer.Ordinal);
        var conflicting = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string key, List<TypeRef> types) in _pendingConstrainedCallTypes)
        {
            List<TypeRef> resolved = types.Select(PruneConstraintType).ToList();
            if (!resolved.TrueForAll(IsPinnableType)
                || (proven.TryGetValue(key, out List<TypeRef>? earlier)
                    && !earlier.Zip(resolved).All(pair => TypesStructurallyEqual(pair.First, pair.Second))))
            {
                conflicting.Add(key);
                continue;
            }

            proven[key] = resolved;
        }

        foreach (string key in conflicting)
        {
            proven.Remove(key);
        }

        return proven;
    }
}
