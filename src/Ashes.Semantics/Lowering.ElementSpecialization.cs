using System.Collections.Immutable;
using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    // Bounds how deeply one element specialization's body may generate further ones, so a pathological
    // chain of generic producers calling each other at ever-larger instantiations cannot recurse forever.
    private const int ElementSpecializationDepthLimit = 8;

    /// <summary>
    /// A top-level generic function whose own lowering declined at least one tail-modulo-constructor cons
    /// because the cell's element type was still a type variable. A call that fixes that type can route to
    /// a copy of the function lowered at the call's concrete types, where the same cons passes the
    /// reference-counted cell gate the generic body could not.
    /// </summary>
    private sealed record ElementSpecializationCandidate(
        string Name,
        string GenericLabel,
        Expr Binder,
        Expr.Lambda Lambda,
        int ChainArity);

    private readonly Dictionary<string, ElementSpecializationCandidate> _elementSpecializationCandidates =
        new(StringComparer.Ordinal);

    // Generated copies keyed by function name plus the structural identity of the pinned parameter types.
    private readonly Dictionary<string, string?> _elementSpecializations = new(StringComparer.Ordinal);

    // Stable numbering of named-type symbols for specialization cache keys: two distinct declarations may
    // share a display name, so the key cannot be the pretty-printed type.
    private readonly Dictionary<object, int> _elementSpecializationSymbolIds = new(ReferenceEqualityComparer.Instance);

    // Counts tail-modulo-constructor conses declined because their element type was unresolved; a
    // top-level function whose lowering moved it is an element specialization candidate.
    private int _abstractElementTmcDeclines;

    private int _elementSpecializationDepth;

    private bool _suppressHoverRecording;

    // Evidence gathered while one specialization body is lowered: conses the cell gate still declined, and
    // reads of a still-being-lowered function's own closure — each a recursion the loop did not absorb.
    private int _elementSpecializationTmcDeclines;

    private int _elementSpecializationSelfReferences;

    // Labels of the functions whose bodies are being lowered inside an element specialization.
    private readonly HashSet<string> _elementSpecializationActiveLabels = new(StringComparer.Ordinal);

    // Filling a closure environment reads each capture, including a recursive function's own closure
    // handed down to its inner curried stage; that read is plumbing, not a call.
    private int _elementSpecializationCaptureFillDepth;

    private void EnterElementSpecializationFunction(string label)
    {
        if (_elementSpecializationDepth > 0)
        {
            _elementSpecializationActiveLabels.Add(label);
        }
    }

    /// <summary>
    /// A tail self-call becomes a back edge and a transformed cons jumps through the same edge, so
    /// neither reads the function's closure. Any other read of an active function's closure inside a
    /// specialization is a call or a first-class use that keeps the recursion — the recursion whose
    /// per-level result copy a concrete element type can make quadratic.
    /// </summary>
    private void RecordElementSpecializationRecursion(string name)
    {
        if (_elementSpecializationDepth > 0
            && _elementSpecializationCaptureFillDepth == 0
            && TryResolveKnownFunctionLabel(name, out string label)
            && _elementSpecializationActiveLabels.Contains(label))
        {
            _elementSpecializationSelfReferences++;
        }
    }

    private void RecordTmcDecline(LoweredValue head)
    {
        _elementSpecializationTmcDeclines++;
        if (ValueTypeRemainsAbstract(head.Type))
        {
            _abstractElementTmcDeclines++;
        }
    }

    /// <summary>
    /// Registers a just-lowered top-level function as an element specialization candidate. Only a
    /// function whose closure environment is empty qualifies: its specialization is lowered in an isolated
    /// scope, so anything it would have captured has nothing to resolve to there.
    /// </summary>
    private void RegisterElementSpecializationCandidate(
        string name,
        Expr binder,
        Expr value,
        TypeScheme scheme,
        int declinesBeforeValue)
    {
        if (!_configuration.EnableElementSpecialization
            || _lambdaDepth != 0
            || _abstractElementTmcDeclines == declinesBeforeValue
            || value is not Expr.Lambda lambda
            || scheme.Quantified.Count == 0
            || scheme.Constraints.Count > 0
            || _traitDictionaryFunctions.ContainsKey(name)
            || !_topLevelFunctionRefs.TryGetValue(name, out var reference)
            || !string.Equals(reference.Label, _lastLoweredLambdaLabel, StringComparison.Ordinal))
        {
            return;
        }

        if (Environment.GetEnvironmentVariable("ASH_TMP_ELEMENT_SPEC_LOG") is not null)
        {
            Console.Error.WriteLine($"[element-spec] candidate {name}");
        }
        _elementSpecializationCandidates[name] = new ElementSpecializationCandidate(
            name,
            reference.Label,
            binder,
            lambda,
            CountLambdaChain(lambda));
    }

    private void RegisterRecursiveElementSpecializationCandidate(
        Expr.LetRecursive binder,
        bool usesTraitDictionary,
        int declinesBeforeValue)
    {
        if (!usesTraitDictionary && _topLevelFunctionRefs.TryGetValue(binder.Name, out var reference))
        {
            RegisterElementSpecializationCandidate(
                binder.Name,
                binder,
                binder.Value,
                reference.Scheme,
                declinesBeforeValue);
        }
    }

    /// <summary>
    /// The candidate a call's root names, when the root resolves to exactly the registered top-level
    /// function — not a local binding that happens to share its name — and the call supplies at least the
    /// function's own curried parameters.
    /// </summary>
    private ElementSpecializationCandidate? ResolveElementSpecializationCandidate(
        Expr rootExpression,
        int argumentCount)
    {
        if (_elementSpecializationCandidates.Count == 0
            || Environment.GetEnvironmentVariable("ASH_TMP_DISABLE_ELEMENT_SPEC") is not null
            || _elementSpecializationDepth >= ElementSpecializationDepthLimit
            || ResolveSpecializableCalleeName(rootExpression) is not { } name
            || !_elementSpecializationCandidates.TryGetValue(name, out ElementSpecializationCandidate? candidate)
            || argumentCount < candidate.ChainArity
            || !TryResolveKnownFunctionLabel(name, out string label)
            || !string.Equals(label, candidate.GenericLabel, StringComparison.Ordinal))
        {
            return null;
        }

        return candidate;
    }

    /// <summary>
    /// With the call's first argument lowered and unified, decides whether the callee's own curried
    /// parameters are all concrete, and if so replaces the generic callee with its element specialization:
    /// a closure over the specialized label, bound under a fresh name so every call-site fact that resolves
    /// the root — its code label, its ownership summary, whether its scheme leaves a position quantified —
    /// describes the copy actually called. Returns the scope depth to restore, or -1 when the call stays
    /// generic.
    /// </summary>
    private int TryRouteToElementSpecialization(
        ElementSpecializationCandidate candidate,
        TypeRef calleeType,
        ref Expr rootExpression,
        ref int calleeTemp)
    {
        List<TypeRef> parameterTypes = CollectAppliedParameterTypes(calleeType, candidate.ChainArity);
        if (parameterTypes.Count != candidate.ChainArity
            || parameterTypes.Any(ValueTypeRemainsAbstract))
        {
            if (Environment.GetEnvironmentVariable("ASH_TMP_ELEMENT_SPEC_LOG") is not null)
            {
                Console.Error.WriteLine($"[element-spec] abstract {candidate.Name} {string.Join(",", parameterTypes.Select(Pretty))}");
            }
            return -1;
        }
        string? maybeLabel = GetOrCreateElementSpecialization(candidate, parameterTypes);
        if (Environment.GetEnvironmentVariable("ASH_TMP_ELEMENT_SPEC_LOG") is not null)
        {
            Console.Error.WriteLine($"[element-spec] ground {(maybeLabel is null ? "rejected" : "accepted")} {candidate.Name} {string.Join(",", parameterTypes.Select(Pretty))}");
        }

        if (maybeLabel is not { } label)
        {
            return -1;
        }

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

        string rootName = $"__element_specialization_{_elementSpecializations.Count}_{closureTemp}";
        int slot = NewLocal();
        Emit(new IrInst.StoreLocal(slot, closureTemp));
        _knownFunctionLabelsBySlot[slot] = label;
        int scopeDepth = _scopes.Count;
        _scopes.Push(_scopes.Peek().SetItem(rootName, new Binding.Local(slot, Prune(calleeType))));
        rootExpression = new Expr.Var(rootName);
        calleeTemp = closureTemp;
        return scopeDepth;
    }

    private void LeaveElementSpecializationRoot(int scopeDepth)
    {
        while (scopeDepth >= 0 && _scopes.Count > scopeDepth)
        {
            _scopes.Pop();
        }
    }

    private string? GetOrCreateElementSpecialization(
        ElementSpecializationCandidate candidate,
        IReadOnlyList<TypeRef> parameterTypes)
    {
        string cacheKey = candidate.Name + "|" + string.Join(",", parameterTypes.Select(ElementSpecializationTypeKey));
        if (_elementSpecializations.TryGetValue(cacheKey, out string? cached))
        {
            return cached;
        }

        string label = _elementSpecializations.Count == 0
            ? $"{candidate.GenericLabel}__element"
            : $"{candidate.GenericLabel}__element${_elementSpecializations.Count}";
        _elementSpecializations[cacheKey] = label;
        _functionNameByLabel[label] = candidate.Name;
        RegisterOwnershipFunctionLabel(label, candidate.Binder);

        if (!LowerElementSpecializationBody(candidate, FreshenTypeVariables(parameterTypes), label, cacheKey))
        {
            _elementSpecializations[cacheKey] = null;
            return null;
        }

        return label;
    }


    private sealed record ElementSpecializationSavedContext(
        ImmutableSortedDictionary<string, Binding>[] Scopes,
        int LambdaDepth,
        IReadOnlyList<TypeRef>? ConcreteTypes,
        int ParameterCursor,
        bool InSpecialization,
        bool InParallelSpecialization,
        string? LinearParameter,
        string? ReuseLabel,
        HashSet<string>? FreshInputNames,
        ReuseToken[] ReuseTokens,
        TcoContext? Tco,
        IReadOnlyList<TypeRef>? AnnotationTypes,
        int AnnotationCursor,
        Expr.Lambda? AnnotationTarget,
        bool BackEdgeArguments,
        bool SuppressHover,
        Expr? SourceExpression,
        int TmcDeclines,
        int SelfReferences,
        int InstructionCount,
        Dictionary<int, LoweredTempOwnershipFact> TempOwnershipFacts);

    /// <summary>
    /// Lowers the candidate's own source again with its curried parameters pinned to the call's concrete
    /// types, in an isolated scope and a clean lowering context: no enclosing reuse tokens, linear reuse
    /// roots, annotation seeds, or tail-call loop leak in from the call site, and the incidental closure
    /// the lowering leaves behind is discarded because the call builds its own.
    /// </summary>
    private bool LowerElementSpecializationBody(
        ElementSpecializationCandidate candidate,
        IReadOnlyList<TypeRef> parameterTypes,
        string label,
        string cacheKey)
    {
        ElementSpecializationSavedContext saved = EnterElementSpecializationContext(parameterTypes);
        try
        {
            IrFunctionOriginSeed originSeed = CreateSpecializationOriginSeed(
                IrFunctionOriginKind.ElementSpecialization,
                candidate.Lambda,
                candidate.Name,
                cacheKey);
            if (candidate.Binder is Expr.LetRecursive recursiveBinder)
            {
                TypeRef recursiveType = NewTypeVar();
                (_, TypeRef valueType) = LowerLetRecursiveLambdaValue(
                    recursiveBinder,
                    candidate.Lambda,
                    recursiveType,
                    default,
                    forcedLabel: label,
                    originSeed: originSeed);
                Unify(recursiveType, valueType);
            }
            else
            {
                LowerLambdaCore(
                    candidate.Lambda,
                    selfName: null,
                    selfType: null,
                    stackAllocateClosure: false,
                    forcedLabel: label,
                    originSeed: originSeed);
            }

            if (Environment.GetEnvironmentVariable("ASH_TMP_ELEMENT_SPEC_LOG") is not null)
            {
                Console.Error.WriteLine($"[element-spec] evidence {label} declines={_elementSpecializationTmcDeclines} self={_elementSpecializationSelfReferences}");
            }
            return _elementSpecializationTmcDeclines == 0 && _elementSpecializationSelfReferences == 0;
        }
        finally
        {
            LeaveElementSpecializationContext(saved);
        }
    }

    private ElementSpecializationSavedContext EnterElementSpecializationContext(IReadOnlyList<TypeRef> parameterTypes)
    {
        var saved = new ElementSpecializationSavedContext(
            _scopes.ToArray(),
            _lambdaDepth,
            _specializationConcreteParamTypes,
            _specializationParamCursor,
            _inSpecialization,
            _inParallelSpecialization,
            _specializingLinearParam,
            _specializingReuseLabel,
            _specFreshInputNames,
            _reuseTokens.ToArray(),
            _tcoCtx,
            _annotationParamTypes,
            _annotationParamCursor,
            _annotationTargetLambda,
            _loweringTcoBackEdgeArguments,
            _suppressHoverRecording,
            _currentSourceExpr,
            _elementSpecializationTmcDeclines,
            _elementSpecializationSelfReferences,
            _inst.Count,
            SnapshotTempOwnershipFacts());

        _scopes.Clear();
        _scopes.Push(ImmutableSortedDictionary.Create<string, Binding>(StringComparer.Ordinal));
        _lambdaDepth = saved.LambdaDepth == 0 ? 1 : saved.LambdaDepth;
        _specializationConcreteParamTypes = parameterTypes;
        _specializationParamCursor = 0;
        _inSpecialization = false;
        _inParallelSpecialization = false;
        _specializingLinearParam = null;
        _specializingReuseLabel = null;
        _specFreshInputNames = null;
        _reuseTokens.Clear();
        _tcoCtx = null;
        _annotationParamTypes = null;
        _annotationParamCursor = 0;
        _annotationTargetLambda = null;
        _loweringTcoBackEdgeArguments = false;
        _suppressHoverRecording = true;
        _currentSourceExpr = null;
        _elementSpecializationTmcDeclines = 0;
        _elementSpecializationSelfReferences = 0;
        _elementSpecializationDepth++;
        PushTraitConstraintScope();
        return saved;
    }

    private void LeaveElementSpecializationContext(ElementSpecializationSavedContext saved)
    {
        if (_inst.Count > saved.InstructionCount)
        {
            _inst.RemoveRange(saved.InstructionCount, _inst.Count - saved.InstructionCount);
        }
        RestoreTempOwnershipFacts(saved.TempOwnershipFacts);
        PopTraitConstraintScope();
        _elementSpecializationDepth--;
        _scopes.Clear();
        for (int index = saved.Scopes.Length - 1; index >= 0; index--)
        {
            _scopes.Push(saved.Scopes[index]);
        }
        _lambdaDepth = saved.LambdaDepth;
        _specializationConcreteParamTypes = saved.ConcreteTypes;
        _specializationParamCursor = saved.ParameterCursor;
        _inSpecialization = saved.InSpecialization;
        _inParallelSpecialization = saved.InParallelSpecialization;
        _specializingLinearParam = saved.LinearParameter;
        _specializingReuseLabel = saved.ReuseLabel;
        _specFreshInputNames = saved.FreshInputNames;
        _reuseTokens.Clear();
        _reuseTokens.AddRange(saved.ReuseTokens);
        _tcoCtx = saved.Tco;
        _annotationParamTypes = saved.AnnotationTypes;
        _annotationParamCursor = saved.AnnotationCursor;
        _annotationTargetLambda = saved.AnnotationTarget;
        _loweringTcoBackEdgeArguments = saved.BackEdgeArguments;
        _suppressHoverRecording = saved.SuppressHover;
        _currentSourceExpr = saved.SourceExpression;
        _elementSpecializationTmcDeclines = saved.TmcDeclines;
        _elementSpecializationSelfReferences = saved.SelfReferences;
    }

    // Copies concrete parameter types with any remaining variable (a capability row, say) replaced by a
    // fresh one, so pinning a cached specialization never binds a variable belonging to one call site.
    private List<TypeRef> FreshenTypeVariables(IReadOnlyList<TypeRef> types)
    {
        var variables = new HashSet<int>();
        foreach (TypeRef type in types)
        {
            FtvType(type, variables);
        }

        var substitution = variables.ToDictionary(id => id, _ => NewTypeVar());
        return types.Select(type => ApplyInstSubst(type, substitution)).ToList();
    }

    private string ElementSpecializationTypeKey(TypeRef type)
    {
        return Prune(type) switch
        {
            TypeRef.TList list => $"List({ElementSpecializationTypeKey(list.Element)})",
            TypeRef.TTuple tuple => $"Tuple({string.Join(",", tuple.Elements.Select(ElementSpecializationTypeKey))})",
            TypeRef.TFun function => $"Fun({ElementSpecializationTypeKey(function.Arg)},{ElementSpecializationTypeKey(function.Ret)})",
            TypeRef.TPtr pointer => $"Ptr({ElementSpecializationTypeKey(pointer.Pointee)})",
            TypeRef.TNamedType named =>
                $"Named#{ElementSpecializationSymbolId(named.Symbol)}({string.Join(",", named.TypeArgs.Select(ElementSpecializationTypeKey))})",
            TypeRef.TTypeParam parameter => $"Param#{ElementSpecializationSymbolId(parameter.Symbol)}",
            TypeRef.TVar variable => $"Var#{variable.Id}",
            TypeRef other => Pretty(other),
        };
    }

    private int ElementSpecializationSymbolId(object symbol)
    {
        if (!_elementSpecializationSymbolIds.TryGetValue(symbol, out int id))
        {
            id = _elementSpecializationSymbolIds.Count;
            _elementSpecializationSymbolIds[symbol] = id;
        }

        return id;
    }
}
