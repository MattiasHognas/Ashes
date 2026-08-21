using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    private const string InvalidTraitDeclarationCode = DiagnosticCodes.InvalidTraitDeclaration;
    private const string InvalidTraitImplementationDeclarationCode = DiagnosticCodes.InvalidTraitImplementation;
    private const string TraitCoherenceCode = DiagnosticCodes.TraitCoherence;
    private const string TraitCycleCode = DiagnosticCodes.TraitCycle;

    private readonly Dictionary<string, TraitSymbol> _traitSymbols = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ambiguousTraitNames = new(StringComparer.Ordinal);
    private readonly List<TraitInstanceSymbol> _traitInstances = [];
    private sealed class TraitConstraintScope
    {
        public List<TraitConstraint> Constraints { get; } = [];
        public bool NeedsLateTypeHint { get; set; }
    }

    private readonly Stack<TraitConstraintScope> _traitConstraintScopes = new();
    private int _traitImplementationValidationDepth;
    private bool _suppressTraitConstraintCollection;
    private int _nextTraitEtaSiteId;

    /// <summary>The residual constraints inferred for the complete trailing expression.</summary>
    public IReadOnlyList<TraitConstraint> LastTraitConstraints { get; private set; } = [];

    /// <summary>The traits registered for the current complete program, addressable by source name.</summary>
    public IReadOnlyDictionary<string, TraitSymbol> TraitSymbols => _traitSymbols;

    /// <summary>The coherent program-global trait implementation set in deterministic source order.</summary>
    public IReadOnlyList<TraitInstanceSymbol> TraitInstances => _traitInstances;

    private void PushTraitConstraintScope()
    {
        _traitConstraintScopes.Push(new TraitConstraintScope());
    }

    private IReadOnlyList<TraitConstraint> PopTraitConstraintScope() =>
        PopTraitConstraintScope(out _);

    private IReadOnlyList<TraitConstraint> PopTraitConstraintScope(out bool needsLateTypeHint)
    {
        TraitConstraintScope scope = _traitConstraintScopes.Pop();
        needsLateTypeHint = scope.NeedsLateTypeHint;
        return TraitConstraint.Canonicalize(scope.Constraints.Select(PruneTraitConstraint));
    }

    private void RequireLateTraitTypeHint()
    {
        if (_traitConstraintScopes.Count > 0)
        {
            _traitConstraintScopes.Peek().NeedsLateTypeHint = true;
        }
    }

    private void RequireTraitConstraint(TraitConstraint constraint)
    {
        if (!_suppressTraitConstraintCollection && _traitConstraintScopes.Count > 0)
        {
            _traitConstraintScopes.Peek().Constraints.Add(constraint);
        }
    }

    private void RequireTraitConstraints(IEnumerable<TraitConstraint> constraints)
    {
        foreach (TraitConstraint constraint in constraints)
        {
            RequireTraitConstraint(constraint);
        }
    }

    private TraitConstraint PruneTraitConstraint(TraitConstraint constraint)
    {
        return new TraitConstraint(
            constraint.Trait,
            constraint.TypeArgs.Select(PruneConstraintType).ToArray());
    }

    private TypeRef PruneConstraintType(TypeRef type)
    {
        TypeRef pruned = Prune(type);
        return pruned switch
        {
            TypeRef.TFun function => new TypeRef.TFun(
                PruneConstraintType(function.Arg),
                PruneConstraintType(function.Ret))
            {
                Row = function.Row is null ? null : PruneConstraintType(function.Row),
            },
            TypeRef.TList list => new TypeRef.TList(PruneConstraintType(list.Element)),
            TypeRef.TTuple tuple => new TypeRef.TTuple(tuple.Elements.Select(PruneConstraintType).ToArray()),
            TypeRef.TNamedType named => new TypeRef.TNamedType(
                named.Symbol,
                named.TypeArgs.Select(PruneConstraintType).ToArray()),
            TypeRef.TPtr pointer => new TypeRef.TPtr(PruneConstraintType(pointer.Pointee)),
            TypeRef.TCapability capability => new TypeRef.TCapability(
                capability.Symbol,
                capability.Args.Select(PruneConstraintType).ToArray()),
            TypeRef.TRow row => new TypeRef.TRow(
                row.Capabilities.Select(capability => (TypeRef.TCapability)PruneConstraintType(capability)).ToArray(),
                row.Tail is null ? null : PruneConstraintType(row.Tail)),
            _ => pruned,
        };
    }

    private sealed record ResolvedBindingSignature(
        TypeRef? Type,
        IReadOnlyList<TraitConstraint> Requirements,
        IReadOnlyList<TraitConstraint> SourceOrderedRequirements);

    private ResolvedBindingSignature ResolveBindingSignature(
        TypeExpr? annotation,
        IReadOnlyList<TraitConstraintSyntax> requirements,
        TextSpan span)
    {
        if (annotation is null && requirements.Count == 0)
        {
            return new ResolvedBindingSignature(null, [], []);
        }

        Dictionary<string, TypeRef>? savedParameterScope = _typeExprParamScope;
        Dictionary<string, TypeRef>? savedRowVariables = _annotationRowVars;
        IEnumerable<TypeExpr> annotationTypes = annotation is null ? [] : [annotation];
        HashSet<string> parameterNames = CollectTypeParameterNames(
            annotationTypes.Concat(requirements.SelectMany(requirement => requirement.TypeArgs)));
        _typeExprParamScope = _activeTraitImplementationMethods.Count > 0
            && savedParameterScope is not null
                ? new Dictionary<string, TypeRef>(savedParameterScope, StringComparer.Ordinal)
                : new Dictionary<string, TypeRef>(StringComparer.Ordinal);
        foreach (string name in parameterNames.OrderBy(name => name, StringComparer.Ordinal))
        {
            _typeExprParamScope.TryAdd(name, NewTypeVar());
        }
        _annotationRowVars = new Dictionary<string, TypeRef>(StringComparer.Ordinal);
        try
        {
            TypeRef? resolvedType = annotation is null ? null : ResolveTypeExpr(annotation);
            List<TraitConstraint> resolvedRequirements = [];
            foreach (TraitConstraintSyntax requirement in requirements)
            {
                TraitSymbol? trait = LookupTrait(requirement.TraitName, span);
                if (trait is null)
                {
                    ReportDiagnostic(
                        GetSpan(requirement),
                        $"Unknown trait '{requirement.TraitName}' in requires clause.",
                        InvalidTraitDeclarationCode);
                    continue;
                }
                if (requirement.TypeArgs.Count != trait.TypeParameters.Count)
                {
                    ReportDiagnostic(
                        GetSpan(requirement),
                        $"Trait '{trait.Name}' expects {trait.TypeParameters.Count} type argument(s), but got {requirement.TypeArgs.Count}.",
                        InvalidTraitDeclarationCode);
                    continue;
                }
                resolvedRequirements.Add(new TraitConstraint(
                    trait,
                    requirement.TypeArgs.Select(ResolveTypeExpr).ToArray()));
            }
            return new ResolvedBindingSignature(
                resolvedType,
                TraitConstraint.Canonicalize(resolvedRequirements),
                resolvedRequirements.ToArray());
        }
        finally
        {
            _typeExprParamScope = savedParameterScope;
            _annotationRowVars = savedRowVariables;
        }
    }

    private IReadOnlyList<TraitConstraint> SelectBindingConstraints(
        IReadOnlyList<TraitConstraint> inferred,
        IReadOnlyList<TraitConstraint> written,
        TypeRef bindingType,
        TextSpan span,
        bool allowRedundantWritten = false,
        bool suppressDiagnostics = false)
    {
        IReadOnlyList<TraitConstraint> canonicalInferred = SimplifyAndResolveTraitConstraints(
            inferred,
            span);
        if (written.Count == 0)
        {
            return canonicalInferred;
        }

        IReadOnlyList<TraitConstraint> canonicalWritten = SimplifyAndResolveTraitConstraints(
            written,
            span);
        if (suppressDiagnostics)
        {
            return canonicalWritten;
        }
        Dictionary<int, int> variableRanks = BuildTypeVariableRanks(PruneConstraintType(bindingType));
        HashSet<string> writtenKeys = canonicalWritten
            .Select(constraint => ConstraintBoundaryKey(constraint, variableRanks))
            .ToHashSet(StringComparer.Ordinal);
        foreach (TraitConstraint missing in canonicalInferred.Where(constraint =>
                     !writtenKeys.Contains(ConstraintBoundaryKey(constraint, variableRanks))))
        {
            ReportDiagnostic(
                span,
                $"Written requires clause does not include inferred requirement '{FormatTraitConstraint(missing)}'.",
                InvalidTraitDeclarationCode);
        }
        HashSet<string> inferredKeys = canonicalInferred
            .Select(constraint => ConstraintBoundaryKey(constraint, variableRanks))
            .ToHashSet(StringComparer.Ordinal);
        foreach (TraitConstraint extra in canonicalWritten.Where(constraint =>
                     !allowRedundantWritten
                     && _traitImplementationValidationDepth == 0
                     && !inferredKeys.Contains(ConstraintBoundaryKey(constraint, variableRanks))))
        {
            ReportDiagnostic(
                span,
                $"Written requires clause includes unjustified requirement '{FormatTraitConstraint(extra)}'.",
                InvalidTraitDeclarationCode);
        }
        return canonicalWritten;
    }

    private bool SuppressSourceConstraintDiagnostics(string bindingName) =>
        (_isTraitValidationSubpass || _sourceTraitConstraintBoundariesValidated)
        && !bindingName.StartsWith("__trait_validate_", StringComparison.Ordinal);

    private Dictionary<int, int> BuildTypeVariableRanks(TypeRef type)
    {
        Dictionary<int, int> ranks = [];
        void Visit(TypeRef current)
        {
            current = PruneConstraintType(current);
            switch (current)
            {
                case TypeRef.TVar variable:
                    ranks.TryAdd(variable.Id, ranks.Count);
                    break;
                case TypeRef.TFun function:
                    Visit(function.Arg);
                    Visit(function.Ret);
                    break;
                case TypeRef.TList list:
                    Visit(list.Element);
                    break;
                case TypeRef.TTuple tuple:
                    foreach (TypeRef element in tuple.Elements) Visit(element);
                    break;
                case TypeRef.TNamedType named:
                    foreach (TypeRef argument in named.TypeArgs) Visit(argument);
                    break;
                case TypeRef.TPtr pointer:
                    Visit(pointer.Pointee);
                    break;
            }
        }
        Visit(type);
        return ranks;
    }

    private string ConstraintBoundaryKey(
        TraitConstraint constraint,
        IReadOnlyDictionary<int, int> variableRanks)
    {
        string TypeKey(TypeRef type)
        {
            type = PruneConstraintType(type);
            return type switch
            {
                TypeRef.TVar variable when variableRanks.TryGetValue(variable.Id, out int rank) => $"v{rank}",
                TypeRef.TVar variable => $"free{variable.Id}",
                TypeRef.TTypeParam parameter => $"p{parameter.Symbol.Name}",
                TypeRef.TInt => "Int",
                TypeRef.TUInt unsigned => $"u{unsigned.Bits}",
                TypeRef.TFloat => "Float",
                TypeRef.TBigInt => "BigInt",
                TypeRef.TStr => "Str",
                TypeRef.TRune => "Rune",
                TypeRef.TBytes => "Bytes",
                TypeRef.TBool => "Bool",
                TypeRef.TNever => "Never",
                TypeRef.TList list => $"List({TypeKey(list.Element)})",
                TypeRef.TTuple tuple => $"Tuple({string.Join(",", tuple.Elements.Select(TypeKey))})",
                TypeRef.TFun function => $"Fun({TypeKey(function.Arg)},{TypeKey(function.Ret)})",
                TypeRef.TNamedType named => $"{named.Symbol.Name}({string.Join(",", named.TypeArgs.Select(TypeKey))})",
                TypeRef.TPtr pointer => $"Ptr({TypeKey(pointer.Pointee)})",
                TypeRef.TOpaque opaque => $"Opaque({opaque.Name})",
                _ => type.GetType().Name,
            };
        }
        return $"{constraint.Trait.QualifiedName}({string.Join(",", constraint.TypeArgs.Select(TypeKey))})";
    }

    private void ValidateBindingConstraintBoundary(
        TypeScheme scheme,
        TextSpan span)
    {
        IReadOnlySet<int> bodyVariables = TraitTypeOperations.FreeVariables(
            new TypeScheme([], PruneConstraintType(scheme.Body)));
        HashSet<int> quantifiedVariables = scheme.Quantified
            .Select(variable => variable.Id)
            .ToHashSet();
        foreach (TraitConstraint constraint in scheme.Constraints)
        {
            IReadOnlySet<int> constraintVariables = TraitTypeOperations.FreeVariables(
                new TypeScheme([], new TypeRef.TTuple(constraint.TypeArgs)));
            int[] ambiguous = constraintVariables
                .Where(variable => quantifiedVariables.Contains(variable)
                    && !bodyVariables.Contains(variable))
                .OrderBy(variable => variable)
                .ToArray();
            if (ambiguous.Length > 0)
            {
                ReportDiagnostic(
                    span,
                    $"Trait requirement '{FormatTraitConstraint(constraint)}' is ambiguous because its type variable does not occur in the binding type.",
                    InvalidTraitDeclarationCode);
            }
        }
    }

    private TypeScheme GeneralizeBindingType(
        TypeRef type,
        IReadOnlyList<TraitConstraint> constraints)
    {
        TypeScheme generalized = Generalize(type, constraints);
        HashSet<int> quantified = generalized.Quantified.Select(variable => variable.Id).ToHashSet();
        List<TraitConstraint> retained = [];
        foreach (TraitConstraint constraint in generalized.Constraints)
        {
            IReadOnlySet<int> variables = TraitTypeOperations.FreeVariables(
                new TypeScheme([], new TypeRef.TTuple(constraint.TypeArgs)));
            if (variables.All(quantified.Contains))
            {
                retained.Add(constraint);
            }
            else
            {
                // Strict let-bound values execute at the binding site. Evidence fixed by the outer
                // environment therefore belongs to that outer expression, not to a polymorphic
                // scheme that might never be referenced.
                RequireTraitConstraint(constraint);
            }
        }
        return new TypeScheme(generalized.Quantified, generalized.Body, retained);
    }

    private string FormatTraitConstraint(TraitConstraint constraint)
    {
        return $"{constraint.Trait.QualifiedName}({string.Join(", ", constraint.TypeArgs.Select(Pretty))})";
    }

    private bool TryGetTraitMethod(
        Expr.QualifiedVar reference,
        out TraitSymbol trait,
        out TraitMethodSymbol method)
    {
        // Capability operations and trait methods intentionally share the readable
        // `Namespace.member` surface. A capability in the referencing source module keeps the
        // ordinary qualified-value precedence, but it must not shadow a same-named trait local to
        // another stitched module (for example Main.Ord versus Ashes.Trait.Ord defaults).
        if (TryGetReferencedCapability(reference.Module, out CapabilitySymbol sameNamedCapability)
            && sameNamedCapability.Operations.ContainsKey(reference.Name)
            && !HasTraitMethodDeclaredInSourceModule(reference))
        {
            trait = null!;
            method = null!;
            return false;
        }

        trait = LookupTrait(reference.Module, GetSpan(reference))!;
        if (trait is null || !trait.Methods.TryGetValue(reference.Name, out TraitMethodSymbol? found))
        {
            method = null!;
            return false;
        }
        method = found;
        return true;
    }

    private bool HasTraitMethodDeclaredInSourceModule(Expr.QualifiedVar reference)
    {
        TraitDeclarationProvenance context = ResolveDeclarationProvenance(GetSpan(reference));
        string localTraitName = string.Equals(context.ModuleName, "Main", StringComparison.Ordinal)
            ? reference.Module
            : $"{context.ModuleName}.{reference.Module}";
        return _traitSymbols.TryGetValue(localTraitName, out TraitSymbol? sourceLocalTrait)
            && string.Equals(sourceLocalTrait.QualifiedName, localTraitName, StringComparison.Ordinal)
            && sourceLocalTrait.Methods.ContainsKey(reference.Name);
    }

    private bool TryGetCapabilityOperationReference(
        Expr.QualifiedVar reference,
        out CapabilitySymbol capability)
    {
        if (TryGetReferencedCapability(reference.Module, out CapabilitySymbol found)
            && found.Operations.ContainsKey(reference.Name)
            && !HasTraitMethodDeclaredInSourceModule(reference))
        {
            capability = found;
            return true;
        }

        capability = null!;
        return false;
    }

    private bool TryGetReferencedCapability(string module, out CapabilitySymbol capability)
    {
        if (_capabilitySymbols.TryGetValue(module, out CapabilitySymbol? direct))
        {
            capability = direct;
            return true;
        }
        int separator = module.LastIndexOf('.');
        if (separator >= 0
            && _capabilitySymbols.TryGetValue(
                module[(separator + 1)..],
                out CapabilitySymbol? qualified))
        {
            capability = qualified;
            return true;
        }
        capability = null!;
        return false;
    }

    private (TypeRef Signature, TraitConstraint Constraint) InstantiateTraitMethod(TraitSymbol trait, TraitMethodSymbol method)
    {
        TypeRef[] arguments = trait.TypeParameters.Select(_ => NewTypeVar()).ToArray();
        Dictionary<string, TypeRef> substitution = trait.TypeParameters
            .Select((parameter, index) => (parameter.Name, Type: arguments[index]))
            .ToDictionary(item => item.Name, item => item.Type, StringComparer.Ordinal);
        TypeRef signature = SubstituteTraitParameters(method.Scheme.Body, substitution);
        return (signature, new TraitConstraint(trait, arguments));
    }

    private TypeRef SubstituteTraitParameters(
        TypeRef type,
        IReadOnlyDictionary<string, TypeRef> substitution)
    {
        return type switch
        {
            TypeRef.TTypeParam parameter when substitution.TryGetValue(parameter.Symbol.Name, out TypeRef? replacement) => replacement,
            TypeRef.TFun function => new TypeRef.TFun(
                SubstituteTraitParameters(function.Arg, substitution),
                SubstituteTraitParameters(function.Ret, substitution))
            {
                Row = function.Row is null ? null : SubstituteTraitParameters(function.Row, substitution),
            },
            TypeRef.TList list => new TypeRef.TList(SubstituteTraitParameters(list.Element, substitution)),
            TypeRef.TTuple tuple => new TypeRef.TTuple(tuple.Elements.Select(element =>
                SubstituteTraitParameters(element, substitution)).ToArray()),
            TypeRef.TNamedType named => new TypeRef.TNamedType(
                named.Symbol,
                named.TypeArgs.Select(argument => SubstituteTraitParameters(argument, substitution)).ToArray()),
            TypeRef.TPtr pointer => new TypeRef.TPtr(SubstituteTraitParameters(pointer.Pointee, substitution)),
            TypeRef.TCapability capability => new TypeRef.TCapability(
                capability.Symbol,
                capability.Args.Select(argument => SubstituteTraitParameters(argument, substitution)).ToArray()),
            TypeRef.TRow row => new TypeRef.TRow(
                row.Capabilities.Select(capability =>
                    (TypeRef.TCapability)SubstituteTraitParameters(capability, substitution)).ToArray(),
                row.Tail is null ? null : SubstituteTraitParameters(row.Tail, substitution)),
            _ => type,
        };
    }

    // A bare (uncalled) trait-method reference. Direct application is handled by
    // TryLowerTraitDirectCall/LowerTraitMethodCall; a first-class method value used as an ordinary
    // value (for example passed to List.map) eta-expands to a lambda that applies the method,
    // mirroring BuildOperationEtaLambda for capability operations, so evidence resolution happens
    // through the normal call path at the eventual application site instead of being fabricated
    // here. A zero-arity method (Default.default is not itself a function) has nothing to
    // eta-expand over and resolves directly.
    private (int, TypeRef) LowerBareTraitMethodReference(
        Expr.QualifiedVar reference,
        TraitSymbol trait,
        TraitMethodSymbol method,
        LoweredValueRequest request)
    {
        TextSpan span = GetSpan(reference);
        (TypeRef signature, TraitConstraint hoverConstraint) = InstantiateTraitMethod(trait, method);
        RecordHoverScheme(
            span,
            $"{trait.QualifiedName}.{method.Name}",
            new TypeScheme([], signature, [hoverConstraint]));

        int arity = CountArrows(method.Scheme.Body);
        return arity == 0
            ? LowerTraitMethodCall(trait, method, [], span, request)
            : LowerExpr(BuildTraitMethodEtaLambda(reference, arity), request).AsPair();
    }

    private Expr BuildTraitMethodEtaLambda(Expr.QualifiedVar reference, int arity)
    {
        TextSpan span = GetSpan(reference);
        string[] parameterNames = Enumerable.Range(0, arity)
            .Select(index => $"__trait_method_arg_{_nextTraitEtaSiteId++}_{index}")
            .ToArray();

        Expr body = new Expr.QualifiedVar(reference.Module, reference.Name);
        AstSpans.Set(body, span);
        foreach (string parameterName in parameterNames)
        {
            Expr argument = new Expr.Var(parameterName);
            AstSpans.Set(argument, span);
            body = new Expr.Call(body, argument);
            AstSpans.Set(body, span);
        }

        for (int index = parameterNames.Length - 1; index >= 0; index--)
        {
            body = new Expr.Lambda(parameterNames[index], body);
            AstSpans.Set(body, span);
        }

        return body;
    }

    // Applies each call argument to the method's curried signature, one arrow at a time, reporting
    // an over-application diagnostic (and a Never-typed sentinel) instead of unifying past the last
    // arrow. Returns null once that diagnostic has been reported, so the caller can stop immediately.
    private (TypeRef Current, List<int> ArgumentTemps)? ConsumeTraitMethodCallArguments(
        TraitSymbol trait,
        TraitMethodSymbol method,
        TypeRef signature,
        IReadOnlyList<Expr> arguments,
        TextSpan span)
    {
        TypeRef current = signature;
        List<int> argumentTemps = [];
        foreach (Expr argument in arguments)
        {
            current = Prune(current);
            if (current is TypeRef.TVar)
            {
                Unify(current, new TypeRef.TFun(NewTypeVar(), NewTypeVar()));
                current = Prune(current);
            }
            if (current is not TypeRef.TFun function)
            {
                ReportDiagnostic(
                    span,
                    $"Trait method '{trait.Name}.{method.Name}' is applied to too many arguments.",
                    DiagnosticCodes.TypeMismatch);
                return null;
            }
            (int argumentTemp, TypeRef argumentType) = LowerExpr(argument);
            argumentTemps.Add(argumentTemp);
            Unify(function.Arg, argumentType);
            SubsumeCalleeRow(function.Row, span);
            current = function.Ret;
        }
        return (current, argumentTemps);
    }

    private (int, TypeRef) LowerTraitMethodCall(
        TraitSymbol trait,
        TraitMethodSymbol method,
        IReadOnlyList<Expr> arguments,
        TextSpan span,
        LoweredValueRequest request)
    {
        (TypeRef signature, TraitConstraint constraint) = InstantiateTraitMethod(trait, method);
        if (ConsumeTraitMethodCallArguments(trait, method, signature, arguments, span) is not
            { } consumed)
        {
            return ReturnNeverWithDummyTemp();
        }
        (TypeRef current, List<int> argumentTemps) = consumed;
        ApplyExpectedTraitMethodType(current, request.ExpectedType);
        RequireTraitConstraint(constraint);
        TraitConstraint prunedConstraint = PruneTraitConstraint(constraint);
        ActiveTraitImplementationMethod? activeMethod = FindActiveTraitImplementationMethod(
            method,
            prunedConstraint);
        if (activeMethod is not null)
        {
            for (int index = 0; index < activeMethod.Constraint.TypeArgs.Count; index++)
            {
                Unify(activeMethod.Constraint.TypeArgs[index], prunedConstraint.TypeArgs[index]);
            }
            (int activeMethodTemp, _) = LowerExpr(new Expr.Var(activeMethod.BindingName)).AsPair();
            return (ApplyTraitMethodArguments(activeMethodTemp, argumentTemps), Prune(current));
        }
        TraitEvidencePlan? plan = ResolveTraitEvidence(prunedConstraint, span, [], 0);
        if (plan is null)
        {
            return ReturnNeverWithDummyTemp();
        }
        if (plan is TraitEvidencePlan.Parameter)
        {
            (int Temp, TypeRef Type)? active = TryLowerActiveTraitMethod(
                prunedConstraint,
                method);
            if (active is null)
            {
                RequireLateTraitTypeHint();
                if (_emitTraitDictionaries)
                {
                    return ReportUnresolvableTraitConstraint(prunedConstraint, span);
                }
                int placeholder = NewTemp();
                Emit(new IrInst.LoadConstInt(placeholder, 0));
                return (placeholder, Prune(current));
            }
            return (ApplyTraitMethodArguments(active.Value.Temp, argumentTemps), Prune(current));
        }
        (int methodTemp, _) = SelectTraitDictionaryMethod(
            BuildTraitDictionary(plan, span),
            trait,
            method);
        return (ApplyTraitMethodArguments(methodTemp, argumentTemps), Prune(current));
    }

    private ActiveTraitImplementationMethod? FindActiveTraitImplementationMethod(
        TraitMethodSymbol method,
        TraitConstraint constraint) =>
        _activeTraitImplementationMethods.LastOrDefault(candidate =>
            string.Equals(candidate.Method, method.Name, StringComparison.Ordinal)
            && string.Equals(
                candidate.Constraint.Trait.QualifiedName,
                constraint.Trait.QualifiedName,
                StringComparison.Ordinal)
            && TraitDispatchTypeListsCompatible(
                candidate.Constraint.TypeArgs,
                constraint.TypeArgs));

    private void ApplyExpectedTraitMethodType(TypeRef actual, TypeRef? expected)
    {
        if (expected is not null)
        {
            Unify(actual, expected);
        }
    }

    private int ApplyTraitMethodArguments(int methodTemp, IEnumerable<int> argumentTemps)
    {
        int applied = methodTemp;
        foreach (int argumentTemp in argumentTemps)
        {
            int next = NewTemp();
            Emit(new IrInst.CallClosure(next, applied, argumentTemp));
            applied = next;
        }
        return applied;
    }

    private bool TraitDispatchTypeListsCompatible(
        IReadOnlyList<TypeRef> left,
        IReadOnlyList<TypeRef> right) =>
        left.Count == right.Count
        && left.Select((type, index) => TraitDispatchTypesCompatible(type, right[index])).All(result => result);

    private bool TraitDispatchTypesCompatible(TypeRef left, TypeRef right)
    {
        left = Prune(left);
        right = Prune(right);
        return (left, right) switch
        {
            (TypeRef.TVar, TypeRef.TVar) => true,
            (TypeRef.TVar, _) or (_, TypeRef.TVar) => false,
            (TypeRef.TList a, TypeRef.TList b) => TraitDispatchTypesCompatible(a.Element, b.Element),
            (TypeRef.TTuple a, TypeRef.TTuple b) => TraitDispatchTypeListsCompatible(a.Elements, b.Elements),
            (TypeRef.TNamedType a, TypeRef.TNamedType b) => ReferenceEquals(a.Symbol, b.Symbol)
                && TraitDispatchTypeListsCompatible(a.TypeArgs, b.TypeArgs),
            _ => TraitResolutionTypesEqual(left, right),
        };
    }

    private (int, TypeRef)? RecordMappedBinaryTrait(
        string traitName,
        string methodName,
        int leftTemp,
        int rightTemp,
        TypeRef leftType,
        TypeRef rightType,
        bool returnsBool,
        TextSpan span)
    {
        TraitSymbol? trait = LookupTrait(traitName, span);
        if (trait is null || trait.TypeParameters.Count != 1)
        {
            return null;
        }

        Unify(leftType, rightType);
        TypeRef operandType = Prune(leftType);
        TraitConstraint constraint = new(trait, [operandType]);
        RequireTraitConstraint(constraint);
        if (ShouldUsePrimitiveOperatorSpecialization(traitName, operandType))
        {
            return null;
        }
        if (!trait.Methods.TryGetValue(methodName, out TraitMethodSymbol? method))
        {
            return null;
        }
        TraitEvidencePlan? plan = ResolveTraitEvidence(PruneTraitConstraint(constraint), span, [], 0);
        if (plan is null)
        {
            return ReturnNeverWithDummyTemp();
        }
        if (plan is TraitEvidencePlan.Parameter
            && FindActiveTraitDictionaryParameter(constraint) is null)
        {
            RequireLateTraitTypeHint();
            if (_emitTraitDictionaries)
            {
                return ReportUnresolvableTraitConstraint(constraint, span);
            }
            int placeholder = NewTemp();
            Emit(new IrInst.LoadConstInt(placeholder, 0));
            return (placeholder, returnsBool ? new TypeRef.TBool() : operandType);
        }
        (int methodTemp, _) = TryLowerActiveTraitMethod(constraint, method)
            ?? SelectTraitDictionaryMethod(BuildTraitDictionary(plan, span), trait, method);
        int first = NewTemp();
        Emit(new IrInst.CallClosure(first, methodTemp, leftTemp));
        int result = NewTemp();
        Emit(new IrInst.CallClosure(result, first, rightTemp));
        return (result, returnsBool ? new TypeRef.TBool() : operandType);
    }

    private (int, TypeRef)? RecordMappedUnaryTrait(
        string traitName,
        string methodName,
        int operandTemp,
        TypeRef operandType,
        TextSpan span)
    {
        TraitSymbol? trait = LookupTrait(traitName, span);
        if (trait is null || trait.TypeParameters.Count != 1)
        {
            return null;
        }

        TypeRef pruned = Prune(operandType);
        TraitConstraint constraint = new(trait, [pruned]);
        RequireTraitConstraint(constraint);
        if (ShouldUsePrimitiveOperatorSpecialization(traitName, pruned))
        {
            return null;
        }
        if (!trait.Methods.TryGetValue(methodName, out TraitMethodSymbol? method))
        {
            return null;
        }
        TraitEvidencePlan? plan = ResolveTraitEvidence(PruneTraitConstraint(constraint), span, [], 0);
        if (plan is null)
        {
            return ReturnNeverWithDummyTemp();
        }
        if (plan is TraitEvidencePlan.Parameter
            && FindActiveTraitDictionaryParameter(constraint) is null)
        {
            RequireLateTraitTypeHint();
            if (_emitTraitDictionaries)
            {
                return ReportUnresolvableTraitConstraint(constraint, span);
            }
            int placeholder = NewTemp();
            Emit(new IrInst.LoadConstInt(placeholder, 0));
            return (placeholder, pruned);
        }
        (int methodTemp, _) = TryLowerActiveTraitMethod(constraint, method)
            ?? SelectTraitDictionaryMethod(BuildTraitDictionary(plan, span), trait, method);
        int result = NewTemp();
        Emit(new IrInst.CallClosure(result, methodTemp, operandTemp));
        return (result, pruned);
    }

    private TypeRef PruneOperatorType(TypeRef type) => Prune(type);

    private bool ShouldUsePrimitiveOperatorSpecialization(string traitName, TypeRef type) =>
        SupportsPrimitiveOperatorSpecialization(traitName, PruneOperatorType(type))
        && (_configuration.EnableTraitOperatorSpecialization
            || _inSpecialization
            || _activeTraitImplementationMethods.Count > 0);

    private static bool SupportsPrimitiveOperatorSpecialization(string traitName, TypeRef type) =>
        traitName switch
        {
            "Eq" => type is TypeRef.TInt or TypeRef.TUInt or TypeRef.TFloat or TypeRef.TBigInt or TypeRef.TStr or TypeRef.TRune or TypeRef.TBool,
            "Ord" => type is TypeRef.TInt or TypeRef.TUInt or TypeRef.TFloat or TypeRef.TBigInt or TypeRef.TRune,
            "Add" => type is TypeRef.TInt or TypeRef.TUInt or TypeRef.TFloat or TypeRef.TBigInt or TypeRef.TStr,
            "Subtract" or "Multiply" or "Divide" or "Negate" =>
                type is TypeRef.TInt or TypeRef.TUInt or TypeRef.TFloat or TypeRef.TBigInt,
            "Remainder" => type is TypeRef.TInt or TypeRef.TUInt or TypeRef.TBigInt,
            "Not" => type is TypeRef.TBool,
            "BitAnd" or "BitOr" or "BitXor" or "ShiftLeft" or "ShiftRight" or "BitwiseNot" =>
                type is TypeRef.TInt or TypeRef.TUInt,
            _ => false,
        };

    private void RegisterTraitAndImplementationDeclarations(IReadOnlyList<TopLevelItem> items)
    {
        TopLevelItem.Trait[] traitItems = items.OfType<TopLevelItem.Trait>().ToArray();
        Dictionary<string, TopLevelItem.Trait> declarations = CollectTraitDeclarations(traitItems);
        IReadOnlyList<string> registrationOrder = ValidateAndOrderSupertraits(declarations);

        foreach (string traitName in registrationOrder)
        {
            RegisterTraitDeclaration(declarations[traitName].Decl);
        }

        foreach (TopLevelItem.Implementation instance in items.OfType<TopLevelItem.Implementation>())
        {
            RegisterTraitImplementationDeclaration(instance.Decl);
        }
    }

    private Program AddTraitValidationBindings(
        Program program,
        bool preserveTrailingBody = false)
    {
        List<TopLevelItem.LetDecl> validations = [];
        int ordinal = 0;
        foreach (TopLevelItem item in program.Items)
        {
            if (item is TopLevelItem.Trait traitItem)
            {
                foreach (TraitMethodDecl method in traitItem.Decl.Methods
                             .Where(method => method.DefaultImplementation is not null))
                {
                    validations.Add(CreateTraitDefaultValidationBinding(traitItem.Decl, method, ordinal++));
                }
            }
            else if (item is TopLevelItem.Implementation implementationItem)
            {
                AddTraitImplementationValidationBindings(validations, implementationItem.Decl, ref ordinal);
            }
        }
        if (validations.Count == 0)
        {
            return program;
        }
        Expr terminal = preserveTrailingBody
            ? program.Body ?? new Expr.Var("Unit")
            : new Expr.IntLit(0);
        return program with
        {
            // Validate implementations after the source declarations are in scope. The combined
            // inference/validation discovery pass retains the trailing body because local inferred
            // trait bindings live there; the fallback validation-only pass substitutes a constant so
            // the real pass remains the sole owner of trailing-body diagnostics and tooling records.
            Body = validations.AsEnumerable().Reverse()
                .Aggregate(terminal, CreateTraitValidationLet),
        };
    }

    private IrProgram? RunTraitValidationPass(Program program)
    {
        if (!_enableTraitValidationPass
            || _traitValidationCompletedDuringElaboration
            || _diag.StructuredErrors.Count > 0)
        {
            return null;
        }
        Program validationProgram = AddTraitValidationBindings(program);
        if (ReferenceEquals(validationProgram, program))
        {
            return null;
        }

        int diagnosticsBefore = _diag.StructuredErrors.Count;
        var validation = new Lowering(
            _diag,
            _importedStdModules,
            _moduleAliases,
            _constructorModulesByName,
            _configuration,
            enableInferredTraitElaboration: false,
            collectInferredTraitElaboration: false,
            enableTraitValidationPass: false,
            includeTraitValidationBindings: false,
            emitTraitDictionaries: false,
            isTraitValidationSubpass: true);
        CopySourceContextTo(validation);
        validation.CopyInferredTraitElaborations(this);
        IrProgram validationIr = validation.Lower(validationProgram);
        return _diag.StructuredErrors.Count > diagnosticsBefore ? validationIr : null;
    }

    private static Expr CreateTraitValidationLet(Expr body, TopLevelItem.LetDecl validation)
    {
        Expr.Let binding = new(validation.Name, validation.Value, body)
        {
            TypeAnnotation = validation.TypeAnnotation,
            Requires = validation.Requires,
            SugarParams = validation.SugarParams,
        };
        AstSpans.Set(binding, AstSpans.GetOrDefault(validation));
        return binding;
    }

    private void AddTraitImplementationValidationBindings(
        ICollection<TopLevelItem.LetDecl> items,
        TraitImplementationDecl implementation,
        ref int ordinal)
    {
        TraitSymbol? trait = LookupTrait(implementation.TraitName, GetSpan(implementation));
        if (trait is null)
        {
            return;
        }
        foreach (TraitImplementationMethodBinding method in implementation.Bindings)
        {
            if (implementation.TypeArgs.Count != trait.TypeParameters.Count
                || !trait.Methods.ContainsKey(method.MethodName))
            {
                continue;
            }
            items.Add(CreateTraitImplementationValidationBinding(
                implementation,
                trait,
                method,
                ordinal++));
        }
    }

    private TopLevelItem.LetDecl CreateTraitDefaultValidationBinding(
        TraitDecl trait,
        TraitMethodDecl method,
        int ordinal)
    {
        IReadOnlyList<TypeExpr> arguments = trait.TypeParameters
            .Select(parameter => (TypeExpr)new TypeExpr.Named(parameter.Name))
            .ToArray();
        TopLevelItem.LetDecl binding = new(
            $"__trait_validate_default_{trait.Name}_{method.Name}_{ordinal}",
            method.DefaultImplementation!,
            IsRecursive: false)
        {
            TypeAnnotation = method.Signature,
            Requires = CollectValidationRequirements(method.DefaultImplementation!, trait, arguments),
        };
        AstSpans.Set(binding, GetSpan(method));
        return binding;
    }

    private TopLevelItem.LetDecl CreateTraitImplementationValidationBinding(
        TraitImplementationDecl implementation,
        TraitSymbol trait,
        TraitImplementationMethodBinding method,
        int ordinal)
    {
        TraitMethodDecl declaration = trait.DeclaringSyntax.Methods.Single(candidate =>
            string.Equals(candidate.Name, method.MethodName, StringComparison.Ordinal));
        Dictionary<string, TypeExpr> substitution = trait.DeclaringSyntax.TypeParameters
            .Select((parameter, index) => (parameter.Name, Type: implementation.TypeArgs[index]))
            .ToDictionary(item => item.Name, item => item.Type, StringComparer.Ordinal);
        TopLevelItem.LetDecl binding = new(
            $"__trait_validate_implementation_{trait.Name}_{method.MethodName}_{ordinal}",
            method.Implementation,
            IsRecursive: false)
        {
            TypeAnnotation = SubstituteTraitValidationType(declaration.Signature, substitution),
            Requires = implementation.Requirements,
        };
        AstSpans.Set(binding, GetSpan(method));
        return binding;
    }

    private IReadOnlyList<TraitConstraintSyntax> CollectValidationRequirements(
        Expr body,
        TraitDecl trait,
        IReadOnlyList<TypeExpr> traitArguments)
    {
        HashSet<string> referencedTraits = CollectReferencedTraitNames(body);
        List<TraitConstraintSyntax> requirements = [];
        TraitSymbol? ownTrait = LookupTrait(trait.Name, GetSpan(trait));
        if (ownTrait is not null && referencedTraits.Contains(ownTrait.QualifiedName))
        {
            requirements.Add(new TraitConstraintSyntax(trait.Name, traitArguments));
        }
        Dictionary<string, TypeExpr> substitution = trait.TypeParameters
            .Select((parameter, index) => (parameter.Name, Type: traitArguments[index]))
            .ToDictionary(item => item.Name, item => item.Type, StringComparer.Ordinal);
        foreach (TraitConstraintSyntax supertrait in trait.Supertraits)
        {
            TraitSymbol? symbol = LookupTrait(supertrait.TraitName, GetSpan(supertrait));
            if (symbol is not null && referencedTraits.Contains(symbol.QualifiedName))
            {
                requirements.Add(new TraitConstraintSyntax(
                    supertrait.TraitName,
                    supertrait.TypeArgs.Select(argument =>
                        SubstituteTraitValidationType(argument, substitution)).ToArray()));
            }
        }
        return requirements;
    }

    private HashSet<string> CollectReferencedTraitNames(Expr body)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        void Visit(Expr current)
        {
            if (current is Expr.QualifiedVar reference
                && TryGetTraitMethod(reference, out TraitSymbol trait, out _))
            {
                names.Add(trait.QualifiedName);
            }
            _ = MapChildExpressions(current, child =>
            {
                Visit(child);
                return child;
            });
        }
        Visit(body);
        return names;
    }

    private static TypeExpr SubstituteTraitValidationType(
        TypeExpr type,
        IReadOnlyDictionary<string, TypeExpr> substitution)
    {
        return type switch
        {
            TypeExpr.Named named when substitution.TryGetValue(named.Name, out TypeExpr? replacement) => replacement,
            TypeExpr.Applied applied => new TypeExpr.Applied(
                applied.Name,
                applied.Args.Select(argument => SubstituteTraitValidationType(argument, substitution)).ToArray()),
            TypeExpr.Arrow arrow => new TypeExpr.Arrow(
                SubstituteTraitValidationType(arrow.From, substitution),
                SubstituteTraitValidationType(arrow.To, substitution))
            {
                Needs = arrow.Needs,
            },
            TypeExpr.TupleType tuple => new TypeExpr.TupleType(
                tuple.Elements.Select(element => SubstituteTraitValidationType(element, substitution)).ToArray()),
            _ => type,
        };
    }

    private Dictionary<string, TopLevelItem.Trait> CollectTraitDeclarations(
        IReadOnlyList<TopLevelItem.Trait> traitItems)
    {
        Dictionary<string, TopLevelItem.Trait> declarations = new(StringComparer.Ordinal);
        foreach (TopLevelItem.Trait item in traitItems)
        {
            TraitDecl declaration = item.Decl;
            string declarationKey = GetDeclaredTraitQualifiedName(declaration);
            if (!declarations.TryAdd(declarationKey, item))
            {
                ReportDiagnostic(
                    GetSpan(declaration),
                        $"Duplicate trait name '{declarationKey}'.",
                    InvalidTraitDeclarationCode);
            }

            if (declaration.TypeParameters.Count == 0)
            {
                ReportDiagnostic(
                    GetSpan(declaration),
                    $"Trait '{declaration.Name}' must declare at least one type parameter.",
                    InvalidTraitDeclarationCode);
            }

            ReportDuplicateNames(
                declaration.TypeParameters.Select(parameter => parameter.Name),
                duplicate => $"Duplicate type parameter '{duplicate}' in trait '{declaration.Name}'.",
                GetSpan(declaration),
                InvalidTraitDeclarationCode);
            ReportDuplicateNames(
                declaration.Methods.Select(method => method.Name),
                duplicate => $"Duplicate method '{duplicate}' in trait '{declaration.Name}'.",
                GetSpan(declaration),
                InvalidTraitDeclarationCode);
        }
        return declarations;
    }

    private IReadOnlyList<string> ValidateAndOrderSupertraits(
        IReadOnlyDictionary<string, TopLevelItem.Trait> declarations)
    {
        Dictionary<string, int> states = new(StringComparer.Ordinal);
        List<string> order = [];
        Stack<string> path = new();

        void Visit(string name)
        {
            if (states.TryGetValue(name, out int state))
            {
                if (state == 1)
                {
                    string trace = string.Join(" -> ", path.Reverse().Append(name));
                    ReportDiagnostic(
                        GetSpan(declarations[name].Decl),
                        $"Cyclic supertrait requirements: {trace}.",
                        TraitCycleCode);
                }
                return;
            }

            states[name] = 1;
            path.Push(name);
            TraitDecl declaration = declarations[name].Decl;
            foreach (TraitConstraintSyntax supertrait in declaration.Supertraits
                         .OrderBy(constraint => constraint.TraitName, StringComparer.Ordinal))
            {
                string? targetKey = ResolveTraitDeclarationKey(
                    supertrait.TraitName,
                    declaration,
                    declarations);
                if (targetKey is null || !declarations.TryGetValue(targetKey, out TopLevelItem.Trait? target))
                {
                    ReportDiagnostic(
                        GetSpan(supertrait),
                        $"Unknown supertrait '{supertrait.TraitName}' of trait '{name}'.",
                        InvalidTraitDeclarationCode);
                    continue;
                }
                if (supertrait.TypeArgs.Count != target.Decl.TypeParameters.Count)
                {
                    ReportDiagnostic(
                        GetSpan(supertrait),
                        $"Trait '{supertrait.TraitName}' expects {target.Decl.TypeParameters.Count} type argument(s), but got {supertrait.TypeArgs.Count}.",
                        InvalidTraitDeclarationCode);
                    continue;
                }
                Visit(targetKey);
            }
            path.Pop();
            states[name] = 2;
            order.Add(name);
        }

        foreach (string name in declarations.Keys.OrderBy(name => name, StringComparer.Ordinal))
        {
            Visit(name);
        }
        return order;
    }

    private void RegisterTraitDeclaration(TraitDecl declaration)
    {
        TraitDeclarationProvenance provenance = ResolveDeclarationProvenance(GetSpan(declaration));
        TypeParameterSymbol[] typeParameters = declaration.TypeParameters
            .Select(parameter => new TypeParameterSymbol(parameter.Name))
            .ToArray();
        string qualifiedName = string.Equals(provenance.ModuleName, "Main", StringComparison.Ordinal)
            ? declaration.Name
            : $"{provenance.ModuleName}.{declaration.Name}";

        Dictionary<string, TraitMethodSymbol> methods = new(StringComparer.Ordinal);
        foreach (TraitMethodDecl method in declaration.Methods)
        {
            if (methods.ContainsKey(method.Name))
            {
                continue;
            }
            TypeRef signature = ResolveAnnotationType(method.Signature, typeParameters);
            methods[method.Name] = new TraitMethodSymbol(
                method.Name,
                new TypeScheme([], signature),
                method.DefaultImplementation,
                GetSpan(method));
        }

        List<TraitConstraint> supertraits = [];
        foreach (TraitConstraintSyntax syntax in declaration.Supertraits)
        {
            TraitSymbol? target = LookupTrait(syntax.TraitName, GetSpan(declaration));
            if (target is null || syntax.TypeArgs.Count != target.TypeParameters.Count)
            {
                continue;
            }
            supertraits.Add(new TraitConstraint(
                target,
                syntax.TypeArgs.Select(type => ResolveAnnotationType(type, typeParameters)).ToArray()));
        }

        TraitSymbol symbol = new(
            declaration.Name,
            qualifiedName,
            typeParameters,
            methods,
            supertraits,
            declaration,
            provenance);
        _traitSymbols[qualifiedName] = symbol;
        if (!_ambiguousTraitNames.Contains(declaration.Name))
        {
            if (_traitSymbols.TryGetValue(declaration.Name, out TraitSymbol? existing)
                && !ReferenceEquals(existing, symbol))
            {
                _traitSymbols.Remove(declaration.Name);
                _ambiguousTraitNames.Add(declaration.Name);
            }
            else
            {
                _traitSymbols[declaration.Name] = symbol;
            }
        }
    }

    private void RegisterTraitImplementationDeclaration(TraitImplementationDecl declaration)
    {
        TraitSymbol? trait = LookupTrait(declaration.TraitName, GetSpan(declaration));
        if (trait is null)
        {
            ReportDiagnostic(
                GetSpan(declaration),
                $"Unknown trait '{declaration.TraitName}' in implementation declaration.",
                InvalidTraitImplementationDeclarationCode);
            return;
        }
        if (declaration.TypeArgs.Count != trait.TypeParameters.Count)
        {
            ReportDiagnostic(
                GetSpan(declaration),
                $"Trait '{trait.Name}' expects {trait.TypeParameters.Count} type argument(s), but the implementation supplies {declaration.TypeArgs.Count}.",
                InvalidTraitImplementationDeclarationCode);
            return;
        }

        TypeParameterSymbol[] instanceParameters = CollectInstanceTypeParameterNames(declaration)
            .Select(name => new TypeParameterSymbol(name))
            .ToArray();
        TypeRef[] headTypes = declaration.TypeArgs
            .Select(type => ResolveAnnotationType(type, instanceParameters))
            .ToArray();
        TraitConstraint[] requirements = ResolveInstanceRequirements(declaration, instanceParameters, headTypes);
        Dictionary<string, Expr> methods = ValidateInstanceMethods(declaration, trait);
        TraitDeclarationProvenance provenance = ResolveDeclarationProvenance(GetSpan(declaration));

        ValidateOrphanRule(trait, headTypes, provenance, declaration);
        TraitInstanceSymbol instance = new(
            new TraitInstanceHead(trait, headTypes),
            TraitConstraint.Canonicalize(requirements),
            methods,
            declaration,
            provenance);
        if (!ValidateInstanceRequirementTermination(instance))
        {
            return;
        }
        ValidateDefaultMethodDependencyCycles(instance);
        AddRegisteredTraitInstance(instance);
    }

    private void ValidateDefaultMethodDependencyCycles(TraitInstanceSymbol instance)
    {
        HashSet<string> activeDefaults = instance.Head.Trait.Methods.Values
            .Where(method => method.DefaultImplementation is not null
                && !instance.MethodImplementations.ContainsKey(method.Name))
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);
        Dictionary<string, int> states = new(StringComparer.Ordinal);
        void Visit(string methodName, List<string> trace)
        {
            int state = states.GetValueOrDefault(methodName);
            if (state == 2)
            {
                return;
            }
            if (state == 1)
            {
                ReportDiagnostic(
                    GetSpan(instance.DeclaringSyntax),
                    $"Default-method dependency cycle in implementation of '{instance.Head.Trait.Name}': {string.Join(" -> ", trace.Append(methodName))}.",
                    TraitCycleCode);
                return;
            }
            states[methodName] = 1;
            TraitMethodSymbol method = instance.Head.Trait.Methods[methodName];
            foreach (string dependency in CollectSelectedTraitMethodDependencies(
                         method.DefaultImplementation,
                         instance.Head.Trait).Where(activeDefaults.Contains))
            {
                Visit(dependency, [.. trace, methodName]);
            }
            states.Remove(methodName);
            states.Add(methodName, 2);
        }
        foreach (string methodName in activeDefaults.OrderBy(name => name, StringComparer.Ordinal))
        {
            Visit(methodName, []);
        }
    }

    internal void AddRegisteredTraitInstance(TraitInstanceSymbol instance)
    {
        ValidateInstanceOverlap(instance);
        _traitInstances.Add(instance);
    }

    private TraitConstraint[] ResolveInstanceRequirements(
        TraitImplementationDecl declaration,
        IReadOnlyList<TypeParameterSymbol> instanceParameters,
        IReadOnlyList<TypeRef> headTypes)
    {
        HashSet<string> headVariables = CollectTypeParameterNames(declaration.TypeArgs);
        List<TraitConstraint> requirements = [];
        foreach (TraitConstraintSyntax requirement in declaration.Requirements)
        {
            TraitSymbol? requiredTrait = LookupTrait(requirement.TraitName, GetSpan(declaration));
            if (requiredTrait is null)
            {
                ReportDiagnostic(
                    GetSpan(requirement),
                    $"Unknown trait '{requirement.TraitName}' in implementation requirement.",
                    InvalidTraitImplementationDeclarationCode);
                continue;
            }
            if (requirement.TypeArgs.Count != requiredTrait.TypeParameters.Count)
            {
                ReportDiagnostic(
                    GetSpan(requirement),
                    $"Trait '{requiredTrait.Name}' expects {requiredTrait.TypeParameters.Count} type argument(s), but got {requirement.TypeArgs.Count}.",
                    InvalidTraitImplementationDeclarationCode);
                continue;
            }
            HashSet<string> requirementVariables = CollectTypeParameterNames(requirement.TypeArgs);
            if (!requirementVariables.IsSubsetOf(headVariables))
            {
                string escaped = requirementVariables.Except(headVariables, StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).First();
                ReportDiagnostic(
                    GetSpan(requirement),
                    $"Implementation requirement variable '{escaped}' does not occur in the implementation head.",
                    InvalidTraitImplementationDeclarationCode);
                continue;
            }
            requirements.Add(new TraitConstraint(
                requiredTrait,
                requirement.TypeArgs.Select(type => ResolveAnnotationType(type, instanceParameters)).ToArray()));
        }
        return requirements.ToArray();
    }

    private Dictionary<string, Expr> ValidateInstanceMethods(TraitImplementationDecl declaration, TraitSymbol trait)
    {
        Dictionary<string, Expr> methods = new(StringComparer.Ordinal);
        foreach (TraitImplementationMethodBinding binding in declaration.Bindings)
        {
            if (!trait.Methods.ContainsKey(binding.MethodName))
            {
                ReportDiagnostic(
                    GetSpan(binding),
                    $"Unknown method '{binding.MethodName}' in implementation of '{trait.Name}'.",
                    InvalidTraitImplementationDeclarationCode);
                continue;
            }
            if (!methods.TryAdd(binding.MethodName, binding.Implementation))
            {
                ReportDiagnostic(
                    GetSpan(binding),
                    $"Method '{binding.MethodName}' is implemented more than once in implementation of '{trait.Name}'.",
                    InvalidTraitImplementationDeclarationCode);
            }
        }

        foreach (TraitMethodSymbol method in trait.Methods.Values.OrderBy(method => method.Name, StringComparer.Ordinal))
        {
            if (method.DefaultImplementation is null && !methods.ContainsKey(method.Name))
            {
                ReportDiagnostic(
                    GetSpan(declaration),
                    $"Implementation of '{trait.Name}' is missing required method '{method.Name}'.",
                    InvalidTraitImplementationDeclarationCode);
            }
        }
        return methods;
    }

    private void ValidateOrphanRule(
        TraitSymbol trait,
        IReadOnlyList<TypeRef> headTypes,
        TraitDeclarationProvenance provenance,
        TraitImplementationDecl declaration)
    {
        bool ownsTrait = string.Equals(trait.Provenance.PackageId, provenance.PackageId, StringComparison.Ordinal);
        bool ownsOuterType = headTypes.OfType<TypeRef.TNamedType>().Any(type =>
            _typeProvenanceBySymbol.TryGetValue(type.Symbol, out TraitDeclarationProvenance? typeProvenance)
            && string.Equals(typeProvenance.PackageId, provenance.PackageId, StringComparison.Ordinal));
        if (!ownsTrait && !ownsOuterType)
        {
            ReportDiagnostic(
                GetSpan(declaration),
                $"Orphan implementation '{trait.Name}({string.Join(", ", headTypes.Select(Pretty))})' is illegal: package '{provenance.PackageId}' owns neither the trait nor an outer nominal head type.",
                TraitCoherenceCode);
        }
    }

    private void ValidateInstanceOverlap(TraitInstanceSymbol candidate)
    {
        foreach (TraitInstanceSymbol existing in _traitInstances)
        {
            if (!string.Equals(existing.Head.Trait.QualifiedName, candidate.Head.Trait.QualifiedName, StringComparison.Ordinal)
                || !InstanceHeadsOverlap(existing.Head.TypeArgs, candidate.Head.TypeArgs))
            {
                continue;
            }
            ReportDiagnostic(
                GetSpan(candidate.DeclaringSyntax),
                $"Overlapping implementations for trait '{candidate.Head.Trait.Name}': first at {FormatTraitSourceLocation(existing)} and conflicting implementation at {FormatTraitSourceLocation(candidate)}.",
                TraitCoherenceCode);
        }
    }

    private static string FormatTraitSourceLocation(TraitInstanceSymbol instance) =>
        $"{instance.Provenance.SourcePath}:{AstSpans.GetOrDefault(instance.DeclaringSyntax).Start}";

    private static bool InstanceHeadsOverlap(IReadOnlyList<TypeRef> left, IReadOnlyList<TypeRef> right)
    {
        Dictionary<(bool Left, string Name), TypeRef> substitutions = [];
        return left.Count == right.Count
            && left.Select((type, index) => UnifyInstanceHead(type, right[index], true, substitutions)).All(result => result);
    }

    private static bool UnifyInstanceHead(
        TypeRef left,
        TypeRef right,
        bool leftSide,
        Dictionary<(bool Left, string Name), TypeRef> substitutions)
    {
        if (left is TypeRef.TTypeParam leftParameter)
        {
            (bool, string) key = (leftSide, leftParameter.Symbol.Name);
            if (substitutions.TryGetValue(key, out TypeRef? replacement))
            {
                return UnifyInstanceHead(replacement, right, leftSide, substitutions);
            }
            substitutions[key] = right;
            return true;
        }
        if (right is TypeRef.TTypeParam rightParameter)
        {
            (bool, string) key = (!leftSide, rightParameter.Symbol.Name);
            if (substitutions.TryGetValue(key, out TypeRef? replacement))
            {
                return UnifyInstanceHead(left, replacement, leftSide, substitutions);
            }
            substitutions[key] = left;
            return true;
        }
        return (left, right) switch
        {
            (TypeRef.TInt, TypeRef.TInt) or
            (TypeRef.TFloat, TypeRef.TFloat) or
            (TypeRef.TBigInt, TypeRef.TBigInt) or
            (TypeRef.TStr, TypeRef.TStr) or
            (TypeRef.TRune, TypeRef.TRune) or
            (TypeRef.TBytes, TypeRef.TBytes) or
            (TypeRef.TBool, TypeRef.TBool) or
            (TypeRef.TNever, TypeRef.TNever) => true,
            (TypeRef.TUInt a, TypeRef.TUInt b) => a.Bits == b.Bits,
            (TypeRef.TList a, TypeRef.TList b) => UnifyInstanceHead(a.Element, b.Element, leftSide, substitutions),
            (TypeRef.TTuple a, TypeRef.TTuple b) => InstanceHeadListsOverlap(a.Elements, b.Elements, leftSide, substitutions),
            (TypeRef.TNamedType a, TypeRef.TNamedType b) => string.Equals(a.Symbol.Name, b.Symbol.Name, StringComparison.Ordinal)
                && InstanceHeadListsOverlap(a.TypeArgs, b.TypeArgs, leftSide, substitutions),
            (TypeRef.TPtr a, TypeRef.TPtr b) => UnifyInstanceHead(a.Pointee, b.Pointee, leftSide, substitutions),
            (TypeRef.TOpaque a, TypeRef.TOpaque b) => string.Equals(a.Name, b.Name, StringComparison.Ordinal),
            _ => false,
        };
    }

    private static bool InstanceHeadListsOverlap(
        IReadOnlyList<TypeRef> left,
        IReadOnlyList<TypeRef> right,
        bool leftSide,
        Dictionary<(bool Left, string Name), TypeRef> substitutions)
    {
        return left.Count == right.Count
            && left.Select((type, index) => UnifyInstanceHead(type, right[index], leftSide, substitutions)).All(result => result);
    }

    private TraitSymbol? LookupTrait(string name, TextSpan contextSpan)
    {
        TraitDeclarationProvenance context = ResolveDeclarationProvenance(contextSpan);
        string localName = string.Equals(context.ModuleName, "Main", StringComparison.Ordinal)
            ? name
            : $"{context.ModuleName}.{name}";
        if (_traitSymbols.TryGetValue(localName, out TraitSymbol? localTrait))
        {
            return localTrait;
        }
        _traitSymbols.TryGetValue(name, out TraitSymbol? trait);
        return trait;
    }

    private string GetDeclaredTraitQualifiedName(TraitDecl declaration)
    {
        TraitDeclarationProvenance provenance = ResolveDeclarationProvenance(GetSpan(declaration));
        return string.Equals(provenance.ModuleName, "Main", StringComparison.Ordinal)
            ? declaration.Name
            : $"{provenance.ModuleName}.{declaration.Name}";
    }

    private string? ResolveTraitDeclarationKey(
        string referencedName,
        TraitDecl owner,
        IReadOnlyDictionary<string, TopLevelItem.Trait> declarations)
    {
        TraitDeclarationProvenance provenance = ResolveDeclarationProvenance(GetSpan(owner));
        string localName = string.Equals(provenance.ModuleName, "Main", StringComparison.Ordinal)
            ? referencedName
            : $"{provenance.ModuleName}.{referencedName}";
        if (declarations.ContainsKey(localName))
        {
            return localName;
        }
        if (declarations.ContainsKey(referencedName))
        {
            return referencedName;
        }
        string[] matches = declarations.Keys
            .Where(name => name.EndsWith($".{referencedName}", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private TraitDeclarationProvenance ResolveDeclarationProvenance(TextSpan span)
    {
        string sourcePath = _currentFilePath ?? "<memory>";
        if (_moduleOffsets is not null)
        {
            foreach ((string filePath, int startOffset, int endOffset) in _moduleOffsets)
            {
                if (span.Start >= startOffset && span.Start < endOffset)
                {
                    sourcePath = filePath;
                    break;
                }
            }
        }
        if (_moduleProvenanceByPath?.TryGetValue(sourcePath, out ModuleProvenance? provenance) == true)
        {
            return new TraitDeclarationProvenance(
                provenance.PackageId,
                provenance.ModuleName,
                sourcePath,
                span);
        }
        return new TraitDeclarationProvenance("standalone", "Main", sourcePath, span);
    }

    private static IEnumerable<string> CollectInstanceTypeParameterNames(TraitImplementationDecl declaration)
    {
        return CollectTypeParameterNames(declaration.TypeArgs.Concat(
            declaration.Requirements.SelectMany(requirement => requirement.TypeArgs)))
            .OrderBy(name => name, StringComparer.Ordinal);
    }

    private static HashSet<string> CollectTypeParameterNames(IEnumerable<TypeExpr> types)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (TypeExpr type in types)
        {
            CollectTypeParameterNames(type, names);
        }
        return names;
    }

    private static void CollectTypeParameterNames(TypeExpr type, HashSet<string> names)
    {
        switch (type)
        {
            case TypeExpr.Named named when named.Name.Length > 0
                && char.IsLower(named.Name[0])
                && named.Name is not ("u8" or "u16" or "u32" or "u64"):
                names.Add(named.Name);
                break;
            case TypeExpr.Applied applied:
                foreach (TypeExpr argument in applied.Args)
                {
                    CollectTypeParameterNames(argument, names);
                }
                break;
            case TypeExpr.Arrow arrow:
                CollectTypeParameterNames(arrow.From, names);
                CollectTypeParameterNames(arrow.To, names);
                break;
            case TypeExpr.TupleType tuple:
                foreach (TypeExpr element in tuple.Elements)
                {
                    CollectTypeParameterNames(element, names);
                }
                break;
        }
    }

    private void ReportDuplicateNames(
        IEnumerable<string> names,
        Func<string, string> message,
        TextSpan span,
        string code)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string name in names)
        {
            if (!seen.Add(name))
            {
                ReportDiagnostic(span, message(name), code);
            }
        }
    }
}
