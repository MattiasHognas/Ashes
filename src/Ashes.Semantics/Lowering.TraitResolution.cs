using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    private const int MaximumTraitResolutionDepth = 64;
    private readonly SortedDictionary<string, TraitEvidencePlan> _concreteTraitEvidenceCache =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _reportedTraitResolutionFailures = new(StringComparer.Ordinal);

    /// <summary>Successfully cached concrete evidence plans in canonical goal order.</summary>
    public IReadOnlyList<TraitEvidencePlan> ResolvedConcreteTraitEvidence =>
        _concreteTraitEvidenceCache.Values.ToArray();

    /// <summary>
    /// Resolves one goal without refining any caller inference variable. An unresolved rigid goal is
    /// represented by a hidden evidence parameter; a concrete missing or incoherent goal reports a
    /// diagnostic and returns <see langword="null"/>.
    /// </summary>
    public TraitEvidencePlan? ResolveTraitEvidence(TraitConstraint constraint)
    {
        return ResolveTraitEvidence(
            PruneTraitConstraint(constraint),
            new TextSpan(0, 1),
            [],
            0);
    }

    private IReadOnlyList<TraitConstraint> SimplifyAndResolveTraitConstraints(
        IEnumerable<TraitConstraint> constraints,
        TextSpan span)
    {
        IReadOnlyList<TraitConstraint> canonical = RemoveImpliedSupertraits(
            TraitConstraint.Canonicalize(constraints.Select(PruneTraitConstraint)));
        List<TraitConstraint> residual = [];
        foreach (TraitConstraint constraint in canonical)
        {
            TraitEvidencePlan? plan = ResolveTraitEvidence(constraint, span, [], 0);
            if (plan is null)
            {
                residual.Add(constraint);
                continue;
            }
            CollectEvidenceParameters(plan, residual);
        }
        return RemoveImpliedSupertraits(TraitConstraint.Canonicalize(residual));
    }

    private TraitEvidencePlan? ResolveTraitEvidence(
        TraitConstraint goal,
        TextSpan span,
        IReadOnlyList<TraitConstraint> trace,
        int depth)
    {
        goal = PruneTraitConstraint(goal);
        string key = TraitConstraint.StableKey(goal);
        if (IsConcreteTraitConstraint(goal)
            && _concreteTraitEvidenceCache.TryGetValue(key, out TraitEvidencePlan? cached))
        {
            return cached;
        }
        if (!ValidateTraitResolutionTraversal(goal, span, trace, depth, key))
        {
            return null;
        }

        TraitInstanceMatch[] matches = FindTraitInstanceMatches(goal);
        if (matches.Length == 0)
        {
            if (!IsConcreteTraitConstraint(goal))
            {
                return new TraitEvidencePlan.Parameter(goal);
            }
            ReportTraitResolutionFailure(
                key,
                span,
                $"No implementation supplies '{FormatTraitConstraint(goal)}'. Requirement trace: {FormatResolutionTrace(trace, goal)}.");
            return null;
        }
        if (matches.Length > 1)
        {
            string locations = string.Join(
                ", ",
                matches.Select(match => FormatTraitSourceLocation(match.Instance)).OrderBy(path => path, StringComparer.Ordinal));
            ReportTraitResolutionFailure(
                key,
                span,
                $"Multiple implementations supply '{FormatTraitConstraint(goal)}' at {locations}. Requirement trace: {FormatResolutionTrace(trace, goal)}.",
                TraitCoherenceCode);
            return null;
        }

        TraitEvidencePlan? resolved = ResolveMatchedTraitEvidence(
            goal,
            matches[0],
            span,
            [.. trace, goal],
            depth);
        if (resolved is not null && IsFullyConcreteEvidence(resolved))
        {
            _concreteTraitEvidenceCache[key] = resolved;
        }
        return resolved;
    }

    private bool ValidateTraitResolutionTraversal(
        TraitConstraint goal,
        TextSpan span,
        IReadOnlyList<TraitConstraint> trace,
        int depth,
        string key)
    {
        if (depth >= MaximumTraitResolutionDepth)
        {
            ReportTraitResolutionFailure(
                key,
                span,
                $"Trait resolution exceeded the maximum depth of {MaximumTraitResolutionDepth}: {FormatResolutionTrace(trace, goal)}.");
            return false;
        }
        if (trace.Any(parent => string.Equals(TraitConstraint.StableKey(parent), key, StringComparison.Ordinal)))
        {
            ReportTraitResolutionFailure(
                key,
                span,
                $"Cyclic trait resolution requirement: {FormatResolutionTrace(trace, goal)}.");
            return false;
        }
        return true;
    }

    private TraitEvidencePlan? ResolveMatchedTraitEvidence(
        TraitConstraint goal,
        TraitInstanceMatch match,
        TextSpan span,
        IReadOnlyList<TraitConstraint> trace,
        int depth)
    {
        TraitConstraint[] requirements = match.Instance.Requirements
            .Select(requirement => SubstituteInstanceParameters(requirement, match.Substitution))
            .ToArray();
        int goalSize = TraitConstraintStructuralSize(goal);
        foreach (TraitConstraint requirement in requirements)
        {
            if (trace.Any(parent => TraitResolutionConstraintsEqual(parent, requirement)))
            {
                ReportTraitResolutionFailure(
                    TraitConstraint.StableKey(goal),
                    span,
                    $"Cyclic trait resolution requirement: {FormatResolutionTrace(trace, requirement)}.");
                return null;
            }
            if (TraitConstraintStructuralSize(requirement) >= goalSize)
            {
                ReportTraitResolutionFailure(
                    TraitConstraint.StableKey(goal),
                    span,
                    $"Non-decreasing trait requirement '{FormatTraitConstraint(requirement)}' while resolving {FormatResolutionTrace(trace, requirement)}.");
                return null;
            }
        }

        IReadOnlyDictionary<string, TypeRef> traitArguments = goal.Trait.TypeParameters
            .Select((parameter, index) => (parameter.Name, Type: goal.TypeArgs[index]))
            .ToDictionary(item => item.Name, item => item.Type, StringComparer.Ordinal);
        TraitConstraint[] supertraits = goal.Trait.Supertraits
            .Select(supertrait => SubstituteInstanceParameters(supertrait, traitArguments))
            .ToArray();
        TraitEvidencePlan[]? requirementPlans = ResolveTraitDependencies(requirements, span, trace, depth + 1);
        TraitEvidencePlan[]? supertraitPlans = ResolveTraitDependencies(supertraits, span, trace, depth + 1);
        return requirementPlans is null || supertraitPlans is null
            ? null
            : new TraitEvidencePlan.Instance(goal, match.Instance, requirementPlans, supertraitPlans);
    }

    private TraitEvidencePlan[]? ResolveTraitDependencies(
        IReadOnlyList<TraitConstraint> dependencies,
        TextSpan span,
        IReadOnlyList<TraitConstraint> trace,
        int depth)
    {
        var plans = new TraitEvidencePlan[dependencies.Count];
        for (int i = 0; i < dependencies.Count; i++)
        {
            TraitEvidencePlan? plan = ResolveTraitEvidence(dependencies[i], span, trace, depth);
            if (plan is null)
            {
                return null;
            }
            plans[i] = plan;
        }
        return plans;
    }

    private TraitInstanceMatch[] FindTraitInstanceMatches(TraitConstraint goal)
    {
        List<TraitInstanceMatch> matches = [];
        foreach (TraitInstanceSymbol instance in _traitInstances
                     .Where(candidate => string.Equals(
                         candidate.Head.Trait.QualifiedName,
                         goal.Trait.QualifiedName,
                         StringComparison.Ordinal))
                     .OrderBy(candidate => candidate.Provenance.PackageId, StringComparer.Ordinal)
                     .ThenBy(candidate => candidate.Provenance.ModuleName, StringComparer.Ordinal)
                     .ThenBy(candidate => candidate.Provenance.SourcePath, StringComparer.Ordinal)
                     .ThenBy(candidate => candidate.Provenance.Span.Start))
        {
            Dictionary<string, TypeRef> substitution = new(StringComparer.Ordinal);
            if (MatchInstanceHeadTypes(instance.Head.TypeArgs, goal.TypeArgs, substitution))
            {
                matches.Add(new TraitInstanceMatch(instance, substitution));
            }
        }
        return matches.ToArray();
    }

    private static bool MatchInstanceHeadTypes(
        IReadOnlyList<TypeRef> heads,
        IReadOnlyList<TypeRef> goals,
        Dictionary<string, TypeRef> substitution)
    {
        return heads.Count == goals.Count
            && heads.Select((head, index) => MatchInstanceHeadType(head, goals[index], substitution)).All(result => result);
    }

    private static bool MatchInstanceHeadType(
        TypeRef head,
        TypeRef goal,
        Dictionary<string, TypeRef> substitution)
    {
        if (head is TypeRef.TTypeParam parameter)
        {
            return !substitution.TryGetValue(parameter.Symbol.Name, out TypeRef? existing)
                ? AddInstanceSubstitution(substitution, parameter.Symbol.Name, goal)
                : TraitResolutionTypesEqual(existing, goal);
        }
        if (goal is TypeRef.TVar or TypeRef.TTypeParam)
        {
            return false;
        }
        return (head, goal) switch
        {
            (TypeRef.TInt, TypeRef.TInt) or
            (TypeRef.TFloat, TypeRef.TFloat) or
            (TypeRef.TBigInt, TypeRef.TBigInt) or
            (TypeRef.TStr, TypeRef.TStr) or
            (TypeRef.TBytes, TypeRef.TBytes) or
            (TypeRef.TBool, TypeRef.TBool) or
            (TypeRef.TNever, TypeRef.TNever) => true,
            (TypeRef.TUInt left, TypeRef.TUInt right) => left.Bits == right.Bits,
            (TypeRef.TList left, TypeRef.TList right) => MatchInstanceHeadType(left.Element, right.Element, substitution),
            (TypeRef.TTuple left, TypeRef.TTuple right) => MatchInstanceHeadTypes(left.Elements, right.Elements, substitution),
            (TypeRef.TNamedType left, TypeRef.TNamedType right) =>
                string.Equals(left.Symbol.Name, right.Symbol.Name, StringComparison.Ordinal)
                && MatchInstanceHeadTypes(left.TypeArgs, right.TypeArgs, substitution),
            (TypeRef.TPtr left, TypeRef.TPtr right) => MatchInstanceHeadType(left.Pointee, right.Pointee, substitution),
            (TypeRef.TOpaque left, TypeRef.TOpaque right) => string.Equals(left.Name, right.Name, StringComparison.Ordinal),
            _ => false,
        };
    }

    private static bool AddInstanceSubstitution(
        Dictionary<string, TypeRef> substitution,
        string name,
        TypeRef type)
    {
        substitution[name] = type;
        return true;
    }

    private static TraitConstraint SubstituteInstanceParameters(
        TraitConstraint constraint,
        IReadOnlyDictionary<string, TypeRef> substitution)
    {
        return new TraitConstraint(
            constraint.Trait,
            constraint.TypeArgs.Select(type => SubstituteInstanceParameters(type, substitution)).ToArray());
    }

    private static TypeRef SubstituteInstanceParameters(
        TypeRef type,
        IReadOnlyDictionary<string, TypeRef> substitution)
    {
        return type switch
        {
            TypeRef.TTypeParam parameter when substitution.TryGetValue(parameter.Symbol.Name, out TypeRef? replacement) => replacement,
            TypeRef.TFun function => new TypeRef.TFun(
                SubstituteInstanceParameters(function.Arg, substitution),
                SubstituteInstanceParameters(function.Ret, substitution))
            {
                Row = function.Row is null ? null : SubstituteInstanceParameters(function.Row, substitution),
            },
            TypeRef.TList list => new TypeRef.TList(SubstituteInstanceParameters(list.Element, substitution)),
            TypeRef.TTuple tuple => new TypeRef.TTuple(tuple.Elements.Select(element =>
                SubstituteInstanceParameters(element, substitution)).ToArray()),
            TypeRef.TNamedType named => new TypeRef.TNamedType(
                named.Symbol,
                named.TypeArgs.Select(argument => SubstituteInstanceParameters(argument, substitution)).ToArray()),
            TypeRef.TPtr pointer => new TypeRef.TPtr(SubstituteInstanceParameters(pointer.Pointee, substitution)),
            TypeRef.TCapability capability => new TypeRef.TCapability(
                capability.Symbol,
                capability.Args.Select(argument => SubstituteInstanceParameters(argument, substitution)).ToArray()),
            TypeRef.TRow row => new TypeRef.TRow(
                row.Capabilities.Select(capability =>
                    (TypeRef.TCapability)SubstituteInstanceParameters(capability, substitution)).ToArray(),
                row.Tail is null ? null : SubstituteInstanceParameters(row.Tail, substitution)),
            _ => type,
        };
    }

    private IReadOnlyList<TraitConstraint> RemoveImpliedSupertraits(
        IReadOnlyList<TraitConstraint> constraints)
    {
        return constraints
            .Where(candidate => !constraints.Any(stronger =>
                !ReferenceEquals(candidate, stronger) && TraitConstraintImplies(stronger, candidate)))
            .ToArray();
    }

    private bool TraitConstraintImplies(TraitConstraint stronger, TraitConstraint target)
    {
        var pending = new Queue<TraitConstraint>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        pending.Enqueue(stronger);
        while (pending.TryDequeue(out TraitConstraint? current))
        {
            string key = TraitConstraint.StableKey(current);
            if (!visited.Add(key))
            {
                continue;
            }
            if (!ReferenceEquals(current, stronger) && TraitResolutionConstraintsEqual(current, target))
            {
                return true;
            }
            IReadOnlyDictionary<string, TypeRef> substitution = current.Trait.TypeParameters
                .Select((parameter, index) => (parameter.Name, Type: current.TypeArgs[index]))
                .ToDictionary(item => item.Name, item => item.Type, StringComparer.Ordinal);
            foreach (TraitConstraint supertrait in current.Trait.Supertraits)
            {
                pending.Enqueue(SubstituteInstanceParameters(supertrait, substitution));
            }
        }
        return false;
    }

    private static bool TraitResolutionConstraintsEqual(TraitConstraint left, TraitConstraint right)
    {
        return string.Equals(left.Trait.QualifiedName, right.Trait.QualifiedName, StringComparison.Ordinal)
            && left.TypeArgs.Count == right.TypeArgs.Count
            && left.TypeArgs.Select((type, index) => TraitResolutionTypesEqual(type, right.TypeArgs[index])).All(equal => equal);
    }

    private static bool TraitResolutionTypesEqual(TypeRef left, TypeRef right)
    {
        return (left, right) switch
        {
            (TypeRef.TVar a, TypeRef.TVar b) => a.Id == b.Id,
            (TypeRef.TTypeParam a, TypeRef.TTypeParam b) => string.Equals(a.Symbol.Name, b.Symbol.Name, StringComparison.Ordinal),
            (TypeRef.TInt, TypeRef.TInt) or
            (TypeRef.TFloat, TypeRef.TFloat) or
            (TypeRef.TBigInt, TypeRef.TBigInt) or
            (TypeRef.TStr, TypeRef.TStr) or
            (TypeRef.TBytes, TypeRef.TBytes) or
            (TypeRef.TBool, TypeRef.TBool) or
            (TypeRef.TNever, TypeRef.TNever) => true,
            (TypeRef.TUInt a, TypeRef.TUInt b) => a.Bits == b.Bits,
            (TypeRef.TList a, TypeRef.TList b) => TraitResolutionTypesEqual(a.Element, b.Element),
            (TypeRef.TTuple a, TypeRef.TTuple b) => TraitResolutionTypeListsEqual(a.Elements, b.Elements),
            (TypeRef.TFun a, TypeRef.TFun b) => TraitResolutionTypesEqual(a.Arg, b.Arg)
                && TraitResolutionTypesEqual(a.Ret, b.Ret)
                && (a.Row is null && b.Row is null
                    || a.Row is not null && b.Row is not null && TraitResolutionTypesEqual(a.Row, b.Row)),
            (TypeRef.TNamedType a, TypeRef.TNamedType b) => string.Equals(a.Symbol.Name, b.Symbol.Name, StringComparison.Ordinal)
                && TraitResolutionTypeListsEqual(a.TypeArgs, b.TypeArgs),
            (TypeRef.TPtr a, TypeRef.TPtr b) => TraitResolutionTypesEqual(a.Pointee, b.Pointee),
            (TypeRef.TOpaque a, TypeRef.TOpaque b) => string.Equals(a.Name, b.Name, StringComparison.Ordinal),
            _ => false,
        };
    }

    private static bool TraitResolutionTypeListsEqual(
        IReadOnlyList<TypeRef> left,
        IReadOnlyList<TypeRef> right)
    {
        return left.Count == right.Count
            && left.Select((type, index) => TraitResolutionTypesEqual(type, right[index])).All(equal => equal);
    }

    private static bool IsConcreteTraitConstraint(TraitConstraint constraint) =>
        constraint.TypeArgs.All(IsConcreteTraitType);

    private static bool IsConcreteTraitType(TypeRef type) => type switch
    {
        TypeRef.TVar or TypeRef.TTypeParam => false,
        TypeRef.TFun function => IsConcreteTraitType(function.Arg)
            && IsConcreteTraitType(function.Ret)
            && (function.Row is null || IsConcreteTraitType(function.Row)),
        TypeRef.TList list => IsConcreteTraitType(list.Element),
        TypeRef.TTuple tuple => tuple.Elements.All(IsConcreteTraitType),
        TypeRef.TNamedType named => named.TypeArgs.All(IsConcreteTraitType),
        TypeRef.TPtr pointer => IsConcreteTraitType(pointer.Pointee),
        TypeRef.TCapability capability => capability.Args.All(IsConcreteTraitType),
        TypeRef.TRow row => row.Capabilities.All(IsConcreteTraitType)
            && (row.Tail is null || IsConcreteTraitType(row.Tail)),
        _ => true,
    };

    private static bool IsFullyConcreteEvidence(TraitEvidencePlan plan) => plan switch
    {
        TraitEvidencePlan.Parameter => false,
        TraitEvidencePlan.Instance instance => instance.Requirements.All(IsFullyConcreteEvidence)
            && instance.Supertraits.All(IsFullyConcreteEvidence),
        _ => false,
    };

    private static void CollectEvidenceParameters(
        TraitEvidencePlan plan,
        ICollection<TraitConstraint> constraints)
    {
        switch (plan)
        {
            case TraitEvidencePlan.Parameter parameter:
                constraints.Add(parameter.Constraint);
                break;
            case TraitEvidencePlan.Instance instance:
                foreach (TraitEvidencePlan requirement in instance.Requirements)
                {
                    CollectEvidenceParameters(requirement, constraints);
                }
                foreach (TraitEvidencePlan supertrait in instance.Supertraits)
                {
                    CollectEvidenceParameters(supertrait, constraints);
                }
                break;
        }
    }

    private static int TraitConstraintStructuralSize(TraitConstraint constraint) =>
        1 + constraint.TypeArgs.Sum(TraitTypeStructuralSize);

    private static int TraitTypeStructuralSize(TypeRef type) => type switch
    {
        TypeRef.TVar or TypeRef.TTypeParam => 0,
        TypeRef.TFun function => 1 + TraitTypeStructuralSize(function.Arg) + TraitTypeStructuralSize(function.Ret),
        TypeRef.TList list => 1 + TraitTypeStructuralSize(list.Element),
        TypeRef.TTuple tuple => 1 + tuple.Elements.Sum(TraitTypeStructuralSize),
        TypeRef.TNamedType named => 1 + named.TypeArgs.Sum(TraitTypeStructuralSize),
        TypeRef.TPtr pointer => 1 + TraitTypeStructuralSize(pointer.Pointee),
        TypeRef.TCapability capability => 1 + capability.Args.Sum(TraitTypeStructuralSize),
        TypeRef.TRow row => 1 + row.Capabilities.Sum(TraitTypeStructuralSize)
            + (row.Tail is null ? 0 : TraitTypeStructuralSize(row.Tail)),
        _ => 1,
    };

    private bool ValidateInstanceRequirementTermination(TraitInstanceSymbol instance)
    {
        int headSize = 1 + instance.Head.TypeArgs.Sum(TraitTypeStructuralSize);
        bool valid = true;
        foreach (TraitConstraint requirement in instance.Requirements)
        {
            int requirementSize = TraitConstraintStructuralSize(requirement);
            if (requirementSize < headSize)
            {
                continue;
            }
            ReportDiagnostic(
                GetSpan(instance.DeclaringSyntax),
                $"Implementation requirement '{FormatTraitConstraint(requirement)}' must be structurally smaller than head '{FormatTraitConstraint(new TraitConstraint(instance.Head.Trait, instance.Head.TypeArgs))}'.",
                TraitCycleCode);
            valid = false;
        }
        return valid;
    }

    private void ReportTraitResolutionFailure(
        string key,
        TextSpan span,
        string message,
        string code = DiagnosticCodes.TraitResolution)
    {
        if (_reportedTraitResolutionFailures.Add($"{code}:{key}:{message}"))
        {
            ReportDiagnostic(span, message, code);
        }
    }

    private string FormatResolutionTrace(
        IReadOnlyList<TraitConstraint> trace,
        TraitConstraint goal)
    {
        return string.Join(" -> ", trace.Append(goal).Select(FormatTraitConstraint));
    }

    private sealed record TraitInstanceMatch(
        TraitInstanceSymbol Instance,
        IReadOnlyDictionary<string, TypeRef> Substitution);
}
