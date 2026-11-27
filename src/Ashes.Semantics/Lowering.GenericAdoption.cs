using System.Collections.Immutable;
using Ashes.Frontend;

namespace Ashes.Semantics;

// Adoptable parameters: which parameters a function moves exactly once on every path, and through
// which closures it hands them on.
//
// A generic function cannot release a value of a type variable, so it can own such a parameter only
// if every path moves it exactly once. A move is one of three uses:
//   - the value is the function's result;
//   - the value is an argument at an adoptable position of a known function (the function itself and
//     its recursive siblings included, so the answer is a greatest fixpoint: every position starts
//     adoptable and loses that when its body disproves it);
//   - the value is an argument to an intermediate stage of a closure the function does not know
//     (`folder(acc)(key)`), which its caller can hand a closure that takes the argument over.
// The result of a call that moved the value is owned the same way and is held to the same rule, so
// `let afterLeft = walk(folder)(acc)(left)` makes `afterLeft` the value the rest of the body must move
// exactly once. Every other use (a match scrutinee, an operator operand, a constructor field, a capture
// in a lambda, an argument at a position that does not adopt) fails the parameter, as does a path that
// does not use it at all.
//
// Each hand-over to an unknown closure is recorded as a route: the parameter, or the captured value,
// that holds the closure. A route of a known callee maps through the call's arguments to a route of the
// caller, and a captured route of a function defined in the caller's body resolves against the
// caller's parameters, so `Map.foldLeft folder state` routes `state` through `folder` although only its
// inner `go` calls the folder.
public sealed partial class Lowering
{
    private enum AdoptionUse
    {
        Zero,
        Once,
        Rejected,
    }

    // Where a value is used: as the function's result, handed to something that takes it over, or
    // used without being taken over.
    private enum AdoptionPosition
    {
        Result,
        HandOver,
        Borrowed,
    }

    private sealed class AdoptionCandidate
    {
        public bool Adoptable { get; set; } = true;

        public SortedSet<int> ParameterRoutes { get; } = [];

        public SortedSet<string> CapturedRoutes { get; } = new(StringComparer.Ordinal);
    }

    // The routes found by one walk of one function body for one parameter, and the functions defined
    // in that body with the names bound around each definition.
    private sealed class AdoptionWalk(FuncKey function, IReadOnlyList<string> parameters)
    {
        public FuncKey Function { get; } = function;

        public IReadOnlyList<string> Parameters { get; } = parameters;

        public HashSet<int> ParameterRoutes { get; } = [];

        public HashSet<string> CapturedRoutes { get; } = new(StringComparer.Ordinal);

        public Dictionary<FuncKey, ImmutableHashSet<string>> NestedDefinitionLocals { get; } = [];
    }

    private static readonly ImmutableHashSet<string> NoAdoptionLocals =
        ImmutableHashSet.Create<string>(StringComparer.Ordinal);

    private static bool GenericAdoptionEnabled =>
        Environment.GetEnvironmentVariable("GRC_NO_GENERICADOPTION") is null;

    private readonly Dictionary<FuncKey, AdoptionCandidate[]> _maAdoption = new();

    // The type each registered function was generalized at, recorded when its binding is lowered, so
    // an adoptable parameter can be told apart from one of a concrete type after lowering.
    private readonly Dictionary<SourceFunctionOrigin, TypeRef> _genericAdoptionBindingTypes = new();

    /// <summary>
    /// Computes <see cref="FunctionOwnershipSummary.AdoptableParameters"/> for every registered function
    /// as a greatest fixpoint: every parameter starts adoptable with no routes, a pass removes the ones
    /// a body disproves under the current assumptions and adds the routes it finds, and passes repeat
    /// until nothing changes. Adoptability only shrinks and routes only grow, so it terminates.
    /// </summary>
    private void ComputeAdoptableParameters()
    {
        _maAdoption.Clear();
        if (!GenericAdoptionEnabled)
        {
            return;
        }

        foreach ((FuncKey key, (List<string> Params, Expr Body) info) in _maFuncs)
        {
            if (!_maFunctionScopes.ContainsKey(key))
            {
                continue;
            }

            var candidates = new AdoptionCandidate[info.Params.Count];
            for (int i = 0; i < candidates.Length; i++)
            {
                candidates[i] = new AdoptionCandidate();
            }

            _maAdoption[key] = candidates;
        }

        WholeProgramFixpoint.RunToFixpoint(() =>
        {
            bool changed = false;
            foreach ((FuncKey key, AdoptionCandidate[] candidates) in _maAdoption)
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    changed |= candidates[i].Adoptable && RefineAdoptionCandidate(key, i, candidates[i]);
                }
            }

            return changed;
        });
    }

    // One pass over one parameter: drops it when the body disproves it, or adds the routes found.
    // Returns whether the candidate changed.
    private bool RefineAdoptionCandidate(FuncKey function, int ordinal, AdoptionCandidate candidate)
    {
        (List<string> Params, Expr Body) info = _maFuncs[function];
        string parameter = info.Params[ordinal];

        // A later parameter of the same name shadows this one in the whole body.
        var walk = new AdoptionWalk(function, info.Params);
        if (info.Params.LastIndexOf(parameter) != ordinal
            || CountAdoptionUse(
                info.Body,
                parameter,
                AdoptionPosition.Result,
                _maFunctionScopes[function],
                NoAdoptionLocals,
                walk) != AdoptionUse.Once)
        {
            candidate.Adoptable = false;
            return true;
        }

        bool changed = false;
        foreach (int route in walk.ParameterRoutes)
        {
            changed |= candidate.ParameterRoutes.Add(route);
        }

        foreach (string route in walk.CapturedRoutes)
        {
            changed |= candidate.CapturedRoutes.Add(route);
        }

        return changed;
    }

    // The adoptable parameters of one function, in parameter order, for its ownership summary.
    private IReadOnlyList<ParameterAdoptionFacts> GetAdoptableParameterFacts(FuncKey function, IReadOnlyList<string> parameters)
    {
        if (!_maAdoption.TryGetValue(function, out AdoptionCandidate[]? candidates))
        {
            return [];
        }

        var facts = new List<ParameterAdoptionFacts>();
        for (int i = 0; i < candidates.Length; i++)
        {
            if (candidates[i].Adoptable)
            {
                facts.Add(new ParameterAdoptionFacts(
                    parameters[i],
                    i,
                    [.. candidates[i].ParameterRoutes],
                    [.. candidates[i].CapturedRoutes]));
            }
        }

        return facts;
    }

    /// <summary>
    /// How often <paramref name="expression"/>, used at <paramref name="position"/>, moves the value
    /// bound to <paramref name="target"/>: never, exactly once on every path, or in a way that fails
    /// the contract. <paramref name="locals"/> holds the names bound inside the function body on the way
    /// here, which a route may not name.
    /// </summary>
    private AdoptionUse CountAdoptionUse(
        Expr expression,
        string target,
        AdoptionPosition position,
        IReadOnlyDictionary<string, FuncKey> scope,
        ImmutableHashSet<string> locals,
        AdoptionWalk walk)
    {
        switch (expression)
        {
            case Expr.Var variable:
                if (!string.Equals(variable.Name, target, StringComparison.Ordinal))
                {
                    return AdoptionUse.Zero;
                }

                return position == AdoptionPosition.Borrowed ? AdoptionUse.Rejected : AdoptionUse.Once;
            case Expr.QualifiedVar:
            case Expr.IntLit:
            case Expr.BigIntLit:
            case Expr.UIntLit:
            case Expr.FloatLit:
            case Expr.StrLit:
            case Expr.RuneLit:
            case Expr.BoolLit:
                return AdoptionUse.Zero;
            case Expr.Call:
                return CountAdoptionCallUse(expression, target, position, scope, locals, walk);
            case Expr.Let let:
                return CountAdoptionLetUse(let, target, position, scope, locals, walk);
            case Expr.LetRecursive letRecursive:
                return CountAdoptionLetRecursiveUse(letRecursive, target, position, scope, locals, walk);
            case RecursiveGroupExpr group:
                return CountAdoptionGroupUse(group, target, position, scope, locals, walk);
            case Expr.If conditional:
                return CountAdoptionUse(conditional.Cond, target, AdoptionPosition.Borrowed, scope, locals, walk)
                    != AdoptionUse.Zero
                        ? AdoptionUse.Rejected
                        : JoinAdoptionUses(
                            CountAdoptionUse(conditional.Then, target, position, scope, locals, walk),
                            CountAdoptionUse(conditional.Else, target, position, scope, locals, walk));
            case Expr.Match match:
                return CountAdoptionMatchUse(match, target, position, scope, locals, walk);
            default:
                // A lambda, a constructor literal, an operator, a result binding and every other form
                // neither returns nor hands over the value as itself.
                return AdoptionNameIsFree(expression, target) ? AdoptionUse.Rejected : AdoptionUse.Zero;
        }
    }

    // Two branches of which exactly one runs agree on the use, or fail.
    private static AdoptionUse JoinAdoptionUses(AdoptionUse left, AdoptionUse right)
        => left == right ? left : AdoptionUse.Rejected;

    private AdoptionUse CountAdoptionLetRecursiveUse(
        Expr.LetRecursive letRecursive,
        string target,
        AdoptionPosition position,
        IReadOnlyDictionary<string, FuncKey> scope,
        ImmutableHashSet<string> locals,
        AdoptionWalk walk)
    {
        if (AdoptionNameIsFree(letRecursive.Value, target))
        {
            return AdoptionUse.Rejected;
        }

        NoteNestedAdoptionDefinition(letRecursive, locals, walk);
        if (string.Equals(letRecursive.Name, target, StringComparison.Ordinal))
        {
            return AdoptionUse.Zero;
        }

        return CountAdoptionUse(
            letRecursive.Body,
            target,
            position,
            ExtendFuncScope(scope, letRecursive, letRecursive.Name),
            locals.Add(letRecursive.Name),
            walk);
    }

    private bool AdoptionNameIsFree(Expr expression, string target)
        => FreeVars(expression, new HashSet<string>(StringComparer.Ordinal)).Contains(target);

    // A function defined in the walked body: its captured routes resolve against the names bound around
    // its definition when the body calls it.
    private void NoteNestedAdoptionDefinition(Expr binder, ImmutableHashSet<string> locals, AdoptionWalk walk)
    {
        if (TryResolveBindingFunctionKey(binder, out FuncKey key))
        {
            walk.NestedDefinitionLocals[key] = locals;
        }
    }

    private AdoptionUse CountAdoptionLetUse(
        Expr.Let let,
        string target,
        AdoptionPosition position,
        IReadOnlyDictionary<string, FuncKey> scope,
        ImmutableHashSet<string> locals,
        AdoptionWalk walk)
    {
        IReadOnlyDictionary<string, FuncKey> bodyScope = ExtendTcoFuncScope(scope, let, let.Name, let.Value);
        ImmutableHashSet<string> bodyLocals = locals.Add(let.Name);
        bool shadows = string.Equals(let.Name, target, StringComparison.Ordinal);
        if (let.Value is Expr.Lambda)
        {
            if (AdoptionNameIsFree(let.Value, target))
            {
                return AdoptionUse.Rejected;
            }

            NoteNestedAdoptionDefinition(let, locals, walk);
            return shadows
                ? AdoptionUse.Zero
                : CountAdoptionUse(let.Body, target, position, bodyScope, bodyLocals, walk);
        }

        // The bound value is handed to the binder: a value that moved the target is owned, and the body
        // must move the binder exactly once in its turn.
        AdoptionUse valueUse = CountAdoptionUse(let.Value, target, AdoptionPosition.HandOver, scope, locals, walk);
        switch (valueUse)
        {
            case AdoptionUse.Rejected:
                return AdoptionUse.Rejected;
            case AdoptionUse.Once:
                if (CountAdoptionUse(let.Body, let.Name, position, bodyScope, bodyLocals, walk) != AdoptionUse.Once)
                {
                    return AdoptionUse.Rejected;
                }

                return shadows
                    || CountAdoptionUse(let.Body, target, position, bodyScope, bodyLocals, walk) == AdoptionUse.Zero
                    ? AdoptionUse.Once
                    : AdoptionUse.Rejected;
            default:
                return shadows
                    ? AdoptionUse.Zero
                    : CountAdoptionUse(let.Body, target, position, bodyScope, bodyLocals, walk);
        }
    }

    private AdoptionUse CountAdoptionGroupUse(
        RecursiveGroupExpr group,
        string target,
        AdoptionPosition position,
        IReadOnlyDictionary<string, FuncKey> scope,
        ImmutableHashSet<string> locals,
        AdoptionWalk walk)
    {
        var groupScope = new Dictionary<string, FuncKey>(scope, StringComparer.Ordinal);
        ImmutableHashSet<string> groupLocals = locals;
        bool shadows = false;
        for (int i = 0; i < group.Bindings.Count; i++)
        {
            (string name, Expr value) = group.Bindings[i];
            if (AdoptionNameIsFree(value, target))
            {
                return AdoptionUse.Rejected;
            }

            FuncKey key = GetRecursiveGroupMemberKey(group, i);
            if (_maFuncs.ContainsKey(key))
            {
                groupScope[name] = key;
            }
            else
            {
                groupScope.Remove(name);
            }

            groupLocals = groupLocals.Add(name);
            shadows |= string.Equals(name, target, StringComparison.Ordinal);
        }

        for (int i = 0; i < group.Bindings.Count; i++)
        {
            walk.NestedDefinitionLocals[GetRecursiveGroupMemberKey(group, i)] = groupLocals;
        }

        return shadows
            ? AdoptionUse.Zero
            : CountAdoptionUse(group.Body, target, position, groupScope, groupLocals, walk);
    }

    private AdoptionUse CountAdoptionMatchUse(
        Expr.Match match,
        string target,
        AdoptionPosition position,
        IReadOnlyDictionary<string, FuncKey> scope,
        ImmutableHashSet<string> locals,
        AdoptionWalk walk)
    {
        if (CountAdoptionUse(match.Value, target, AdoptionPosition.Borrowed, scope, locals, walk) != AdoptionUse.Zero)
        {
            return AdoptionUse.Rejected;
        }

        AdoptionUse? armsUse = null;
        foreach (MatchCase matchCase in match.Cases)
        {
            var binders = new HashSet<string>(StringComparer.Ordinal);
            CollectPatternBinders(matchCase.Pattern, binders);
            AdoptionUse armUse;
            if (binders.Contains(target))
            {
                armUse = AdoptionUse.Zero;
            }
            else
            {
                IReadOnlyDictionary<string, FuncKey> armScope = RemoveFuncNames(scope, binders);
                ImmutableHashSet<string> armLocals = locals.Union(binders);
                if (matchCase.Guard is { } guard
                    && CountAdoptionUse(guard, target, AdoptionPosition.Borrowed, armScope, armLocals, walk)
                        != AdoptionUse.Zero)
                {
                    return AdoptionUse.Rejected;
                }

                armUse = CountAdoptionUse(matchCase.Body, target, position, armScope, armLocals, walk);
            }

            if (armUse == AdoptionUse.Rejected || (armsUse is { } previous && previous != armUse))
            {
                return AdoptionUse.Rejected;
            }

            armsUse = armUse;
        }

        return armsUse ?? AdoptionUse.Rejected;
    }

    private AdoptionUse CountAdoptionCallUse(
        Expr call,
        string target,
        AdoptionPosition position,
        IReadOnlyDictionary<string, FuncKey> scope,
        ImmutableHashSet<string> locals,
        AdoptionWalk walk)
    {
        var arguments = new List<Expr>();
        Expr root = CollectCallArgs(call, arguments);
        if (!TryResolveAdoptionCallee(root, target, scope, locals, walk, out FuncKey? callee, out string? unknownClosure))
        {
            return AdoptionUse.Rejected;
        }

        AdoptionPosition[] positions = GetAdoptionArgumentPositions(callee, unknownClosure, arguments.Count, position);
        int moved = -1;
        for (int i = 0; i < arguments.Count; i++)
        {
            AdoptionUse use = CountAdoptionUse(arguments[i], target, positions[i], scope, locals, walk);
            if (use == AdoptionUse.Rejected || (use == AdoptionUse.Once && moved >= 0))
            {
                return AdoptionUse.Rejected;
            }

            if (use == AdoptionUse.Once)
            {
                moved = i;
            }
        }

        if (moved < 0)
        {
            return AdoptionUse.Zero;
        }

        // The call's result holds what it took over, so it must be returned or handed on in its turn.
        if (position == AdoptionPosition.Borrowed)
        {
            return AdoptionUse.Rejected;
        }

        bool routed = callee is { } routedCallee
            ? MapCalleeAdoptionRoutes(routedCallee, _maAdoption[routedCallee][moved], arguments, target, scope, locals, walk)
            : AddAdoptionRoute(unknownClosure!, target, scope, locals, walk);
        return routed ? AdoptionUse.Once : AdoptionUse.Rejected;
    }

    // Resolves a call's head to a known function or to the name of a closure this function does not
    // know. A constructor or any other head resolves to neither; a head that uses the value fails.
    private bool TryResolveAdoptionCallee(
        Expr root,
        string target,
        IReadOnlyDictionary<string, FuncKey> scope,
        ImmutableHashSet<string> locals,
        AdoptionWalk walk,
        out FuncKey? callee,
        out string? unknownClosure)
    {
        callee = null;
        unknownClosure = null;
        switch (root)
        {
            case Expr.Var variable when string.Equals(variable.Name, target, StringComparison.Ordinal):
                return false;
            case Expr.Var variable when scope.TryGetValue(variable.Name, out FuncKey known):
                callee = known;
                return true;
            case Expr.Var variable:
                if (variable.Name.Length > 0 && !char.IsUpper(variable.Name[0]))
                {
                    unknownClosure = variable.Name;
                }

                return true;
            case Expr.QualifiedVar qualified:
                if (ResolveSpecializableCalleeName(qualified) is { } stitched
                    && _maNameIndex.TryGetValue(stitched, out FuncKey qualifiedKey))
                {
                    callee = qualifiedKey;
                }

                return true;
            default:
                return CountAdoptionUse(root, target, AdoptionPosition.Borrowed, scope, locals, walk) == AdoptionUse.Zero;
        }
    }

    // The position each argument of a call is used at: handed over at an adoptable position of a known
    // function and at every stage but the last of an unknown closure, borrowed everywhere else.
    private AdoptionPosition[] GetAdoptionArgumentPositions(
        FuncKey? callee,
        string? unknownClosure,
        int argumentCount,
        AdoptionPosition callPosition)
    {
        var positions = new AdoptionPosition[argumentCount];
        Array.Fill(positions, AdoptionPosition.Borrowed);
        if (callee is { } calleeKey && _maAdoption.TryGetValue(calleeKey, out AdoptionCandidate[]? candidates))
        {
            // A partial application moves its arguments only when the function returns it, and an
            // over-application through its saturated prefix, whose result is the closure the rest of
            // the arguments are applied to.
            if (argumentCount >= candidates.Length || callPosition == AdoptionPosition.Result)
            {
                int covered = Math.Min(argumentCount, candidates.Length);
                for (int i = 0; i < covered; i++)
                {
                    if (candidates[i].Adoptable)
                    {
                        positions[i] = AdoptionPosition.HandOver;
                    }
                }
            }
        }
        else if (callee is null && unknownClosure is not null)
        {
            // Every stage but the last returns the closure the next argument is applied to.
            for (int i = 0; i < argumentCount - 1; i++)
            {
                positions[i] = AdoptionPosition.HandOver;
            }
        }

        return positions;
    }

    // Maps the routes of the callee's parameter the value was handed to onto this function's own.
    private bool MapCalleeAdoptionRoutes(
        FuncKey callee,
        AdoptionCandidate candidate,
        IReadOnlyList<Expr> arguments,
        string target,
        IReadOnlyDictionary<string, FuncKey> scope,
        ImmutableHashSet<string> locals,
        AdoptionWalk walk)
    {
        foreach (int route in candidate.ParameterRoutes)
        {
            if (route >= arguments.Count
                || arguments[route] is not Expr.Var closure
                || !AddAdoptionRoute(closure.Name, target, scope, locals, walk))
            {
                return false;
            }
        }

        if (candidate.CapturedRoutes.Count == 0)
        {
            return true;
        }

        if (callee.Equals(walk.Function))
        {
            walk.CapturedRoutes.UnionWith(candidate.CapturedRoutes);
            return true;
        }

        // A captured route names a binding visible where the callee is defined, which only a function
        // defined in this body lets this walk resolve.
        if (!walk.NestedDefinitionLocals.TryGetValue(callee, out ImmutableHashSet<string>? definitionLocals))
        {
            return false;
        }

        foreach (string captured in candidate.CapturedRoutes)
        {
            if (definitionLocals.Contains(captured))
            {
                return false;
            }

            AddParameterOrCapturedRoute(captured, walk);
        }

        return true;
    }

    // Records the closure named <paramref name="closure"/> at this point of the body as a route. A
    // closure bound inside the body, a known function and the moved value itself are not routes.
    private static bool AddAdoptionRoute(
        string closure,
        string target,
        IReadOnlyDictionary<string, FuncKey> scope,
        ImmutableHashSet<string> locals,
        AdoptionWalk walk)
    {
        if (string.Equals(closure, target, StringComparison.Ordinal)
            || locals.Contains(closure)
            || scope.ContainsKey(closure))
        {
            return false;
        }

        AddParameterOrCapturedRoute(closure, walk);
        return true;
    }

    private static void AddParameterOrCapturedRoute(string name, AdoptionWalk walk)
    {
        int ordinal = -1;
        for (int i = walk.Parameters.Count - 1; i >= 0; i--)
        {
            if (string.Equals(walk.Parameters[i], name, StringComparison.Ordinal))
            {
                ordinal = i;
                break;
            }
        }

        if (ordinal >= 0)
        {
            walk.ParameterRoutes.Add(ordinal);
        }
        else
        {
            walk.CapturedRoutes.Add(name);
        }
    }

    // A recursive binding's generalized scheme, registered for its evidence demands, and the type it
    // was generalized at, recorded for its adopting parameters.
    private void RegisterRecursiveBindingSchemes(Expr.LetRecursive letRecursive, TypeScheme recursiveScheme, TypeRef schemeType)
    {
        RegisterRecursiveDemandScheme(letRecursive.Value, recursiveScheme);
        RecordGenericAdoptionBindingType(GetRegisteredFunctionKey(letRecursive), schemeType);
    }

    // Remembers the type a registered function's binding was generalized at, keyed by its source
    // origin, the first time the binding is lowered.
    private void RecordGenericAdoptionBindingType(FuncKey? function, TypeRef type)
    {
        if (function is { } key && _maFunctionOrigins.TryGetValue(key, out SourceFunctionOrigin? origin))
        {
            _genericAdoptionBindingTypes.TryAdd(origin, type);
        }
    }

    /// <summary>
    /// The adoptable parameters of <paramref name="summary"/> whose type is a type variable in the
    /// type the function was generalized at: the adopting parameters of the generic-parameter contract.
    /// Empty before the function's binding is lowered.
    /// </summary>
    internal IReadOnlyList<ParameterAdoptionFacts> GetGenericAdoptingParameters(FunctionOwnershipSummary summary)
    {
        if (summary.AdoptableParameters.Count == 0
            || !_genericAdoptionBindingTypes.TryGetValue(summary.Origin, out TypeRef? bindingType))
        {
            return [];
        }

        var parameterTypes = new List<TypeRef>();
        TypeRef cursor = Prune(bindingType);
        while (cursor is TypeRef.TFun function && parameterTypes.Count < summary.Parameters.Count)
        {
            parameterTypes.Add(Prune(function.Arg));
            cursor = Prune(function.Ret);
        }

        var adopting = new List<ParameterAdoptionFacts>();
        foreach (ParameterAdoptionFacts facts in summary.AdoptableParameters)
        {
            if (facts.Ordinal < parameterTypes.Count
                && parameterTypes[facts.Ordinal] is TypeRef.TVar or TypeRef.TTypeParam)
            {
                adopting.Add(facts);
            }
        }

        return adopting;
    }
}
