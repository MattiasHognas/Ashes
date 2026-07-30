using Ashes.Frontend;

namespace Ashes.Semantics;

// Result-provenance classification for FunctionOwnershipSummary. This generalizes and strictly
// subsumes what Lowering.cs's old IR-level backward-scan mechanism (_functionReturnedClosureLabels /
// _runtimeManagedFunctionResultLabels, populated by RecordFunctionResultProvenance /
// RecordReturnedClosureLabel) used to track: that mechanism only recognized a returned closure when a
// function's body temp was produced by a literal MakeClosure/MakeClosureStack instruction found by
// scanning backward through already-emitted IR for the CURRENT function only — it had no way to see a
// result computed by CALLING another named function (a sibling helper), because a Call instruction
// does not look like a closure-producing instruction even though its callee, at runtime, returns one.
//
// This pass instead classifies each registered function's innermost (fully curried, per _maFuncs)
// body once, at the AST level, before lowering. Exact saturated calls to other registered functions
// form a FuncKey graph whose strongly-connected components converge together: productive mutual
// recursion inherits a fresh result base, while ungrounded or mixed components fail closed. Phase 3
// wired this directly into the real decision path
// (TryResolveKnownFunctionResultOwnership/IsDirectRuntimeManagedFunctionCall in Lowering.cs) and
// retired _runtimeManagedFunctionResultLabels/RecordFunctionResultProvenance entirely;
// _functionReturnedClosureLabels/RecordReturnedClosureLabel remain, but now serve only
// IsKnownRuntimeNormalizedFunctionArgument's unrelated TCO-argument-normalization question.
public sealed partial class Lowering
{
    private sealed record ResolvedFunctionResultProvenance(bool RcEligible, FuncKey? ForwardsTo);

    private sealed record ProvenanceFunctionNode(
        bool HasDirectEligibleResult,
        bool HasRejectedResult,
        int ConsideredArmCount,
        IReadOnlySet<FuncKey> ForwardTargets,
        FuncKey? UnambiguousForwardTarget);

    private sealed record ProvenanceComponent(
        bool HasDirectEligibleResult,
        bool HasRejectedResult,
        IReadOnlySet<int> Dependencies);

    private sealed record ProvenanceLetBinding(
        Expr Value,
        IReadOnlyDictionary<string, ProvenanceLetBinding> LetBindings,
        IReadOnlyDictionary<string, FuncKey> FunctionScope);

    private sealed record ProvenanceArm(
        Expr Expression,
        IReadOnlyDictionary<string, ProvenanceLetBinding> LetBindings,
        IReadOnlyDictionary<string, FuncKey> FunctionScope);

    private readonly Dictionary<FuncKey, ResolvedFunctionResultProvenance> _maProvenanceMemo = new();

    private bool _maProvenanceAnalysisComplete;

    // Canonical alias-stripped body for provenance only. Other summary facts intentionally retain their
    // registered AST bodies and node identities; provenance needs the normalized body so its lexical
    // function scope and call heads describe the same tree.
    private readonly Dictionary<FuncKey, Expr> _maProvenanceBodies = new();

    /// <summary>
    /// The result provenance of registered function <paramref name="function"/>, resolved by one
    /// whole-program, exact-<see cref="FuncKey"/> fixpoint. Mutually-recursive components are eligible
    /// only when every result arm stays inside the candidate set and the component can reach an
    /// independently eligible construction. A pure forwarding cycle therefore remains conservative,
    /// while a cycle with a fresh base arm converges independent of dictionary or query order.
    /// </summary>
    private ResolvedFunctionResultProvenance ResolveFunctionResultProvenance(FuncKey function)
    {
        if (!_maProvenanceAnalysisComplete)
        {
            ComputeFunctionResultProvenanceFixpoint();
        }

        return _maProvenanceMemo.GetValueOrDefault(
            function,
            new ResolvedFunctionResultProvenance(false, null));
    }

    private void ComputeFunctionResultProvenanceFixpoint()
    {
        var nodes = new Dictionary<FuncKey, ProvenanceFunctionNode>();
        foreach (FuncKey function in _maFuncs.Keys)
        {
            nodes[function] = BuildProvenanceFunctionNode(function);
        }

        _maProvenanceMemo.Clear();
        if (nodes.Count == 0)
        {
            _maProvenanceAnalysisComplete = true;
            return;
        }

        Dictionary<FuncKey, int> componentByFunction =
            ComputeProvenanceStronglyConnectedComponents(nodes);
        IReadOnlyList<ProvenanceComponent> components =
            BuildProvenanceComponents(nodes, componentByFunction);
        HashSet<int> eligibleComponents = ComputeEligibleProvenanceComponents(components);

        foreach ((FuncKey function, ProvenanceFunctionNode node) in nodes)
        {
            _maProvenanceMemo[function] = new ResolvedFunctionResultProvenance(
                eligibleComponents.Contains(componentByFunction[function]),
                node.UnambiguousForwardTarget);
        }

        _maProvenanceAnalysisComplete = true;
    }

    private static Dictionary<FuncKey, int> ComputeProvenanceStronglyConnectedComponents(
        IReadOnlyDictionary<FuncKey, ProvenanceFunctionNode> nodes)
    {
        var postOrder = new List<FuncKey>(nodes.Count);
        var visited = new HashSet<FuncKey>();
        foreach (FuncKey function in nodes.Keys)
        {
            AppendProvenancePostOrder(function, nodes, visited, postOrder);
        }

        Dictionary<FuncKey, List<FuncKey>> reverseEdges = BuildReverseProvenanceEdges(nodes);
        var componentByFunction = new Dictionary<FuncKey, int>();
        int component = 0;
        for (int i = postOrder.Count - 1; i >= 0; i--)
        {
            FuncKey function = postOrder[i];
            if (componentByFunction.ContainsKey(function))
            {
                continue;
            }

            AssignProvenanceComponent(
                function,
                component,
                reverseEdges,
                componentByFunction);
            component++;
        }

        return componentByFunction;
    }

    private static void AppendProvenancePostOrder(
        FuncKey function,
        IReadOnlyDictionary<FuncKey, ProvenanceFunctionNode> nodes,
        HashSet<FuncKey> visited,
        List<FuncKey> postOrder)
    {
        var pending = new Stack<(FuncKey Function, bool Expanded)>();
        pending.Push((function, false));
        while (pending.TryPop(out var item))
        {
            if (item.Expanded)
            {
                postOrder.Add(item.Function);
                continue;
            }

            if (!visited.Add(item.Function))
            {
                continue;
            }

            pending.Push((item.Function, true));
            foreach (FuncKey target in nodes[item.Function].ForwardTargets)
            {
                pending.Push((target, false));
            }
        }
    }

    private static Dictionary<FuncKey, List<FuncKey>> BuildReverseProvenanceEdges(
        IReadOnlyDictionary<FuncKey, ProvenanceFunctionNode> nodes)
    {
        var reverseEdges = nodes.Keys.ToDictionary(
            function => function,
            _ => new List<FuncKey>());
        foreach ((FuncKey function, ProvenanceFunctionNode node) in nodes)
        {
            foreach (FuncKey target in node.ForwardTargets)
            {
                reverseEdges[target].Add(function);
            }
        }

        return reverseEdges;
    }

    private static void AssignProvenanceComponent(
        FuncKey function,
        int component,
        IReadOnlyDictionary<FuncKey, List<FuncKey>> reverseEdges,
        Dictionary<FuncKey, int> componentByFunction)
    {
        var pending = new Stack<FuncKey>();
        pending.Push(function);
        while (pending.TryPop(out FuncKey current))
        {
            if (!componentByFunction.TryAdd(current, component))
            {
                continue;
            }

            foreach (FuncKey predecessor in reverseEdges[current])
            {
                pending.Push(predecessor);
            }
        }
    }

    private static IReadOnlyList<ProvenanceComponent> BuildProvenanceComponents(
        IReadOnlyDictionary<FuncKey, ProvenanceFunctionNode> nodes,
        IReadOnlyDictionary<FuncKey, int> componentByFunction)
    {
        int componentCount = componentByFunction.Values.Max() + 1;
        var direct = new bool[componentCount];
        var rejected = new bool[componentCount];
        var considered = new int[componentCount];
        var dependencies = Enumerable.Range(0, componentCount)
            .Select(_ => new HashSet<int>())
            .ToArray();
        foreach ((FuncKey function, ProvenanceFunctionNode node) in nodes)
        {
            int component = componentByFunction[function];
            direct[component] |= node.HasDirectEligibleResult;
            rejected[component] |= node.HasRejectedResult;
            considered[component] += node.ConsideredArmCount;
            foreach (FuncKey target in node.ForwardTargets)
            {
                int dependency = componentByFunction[target];
                if (dependency != component)
                {
                    dependencies[component].Add(dependency);
                }
            }
        }

        var result = new List<ProvenanceComponent>(componentCount);
        for (int component = 0; component < componentCount; component++)
        {
            result.Add(new ProvenanceComponent(
                direct[component],
                rejected[component] || considered[component] == 0,
                dependencies[component]));
        }

        return result;
    }

    private static HashSet<int> ComputeEligibleProvenanceComponents(
        IReadOnlyList<ProvenanceComponent> components)
    {
        var eligible = new HashSet<int>();
        bool changed;
        do
        {
            changed = false;
            for (int component = 0; component < components.Count; component++)
            {
                ProvenanceComponent node = components[component];
                bool grounded = node.HasDirectEligibleResult
                    || node.Dependencies.Any(eligible.Contains);
                if (!eligible.Contains(component)
                    && !node.HasRejectedResult
                    && grounded
                    && node.Dependencies.All(eligible.Contains))
                {
                    eligible.Add(component);
                    changed = true;
                }
            }
        }
        while (changed);

        return eligible;
    }

    /// <summary>
    /// Classifies a registered function's result by its terminal arms without resolving forward targets
    /// recursively. Directly eligible constructions seed the later fixpoint; exact saturated forwarding
    /// calls become graph edges; any other terminal result rejects the node. Exact self-recursive arms
    /// remain neutral, as before, because they cannot produce a representation different from the
    /// function whose result is being classified.
    /// </summary>
    private ProvenanceFunctionNode BuildProvenanceFunctionNode(FuncKey function)
    {
        if (!_maFuncs.TryGetValue(function, out var info))
        {
            return new ProvenanceFunctionNode(false, true, 0, new HashSet<FuncKey>(), null);
        }

        Expr body = _maProvenanceBodies.GetValueOrDefault(function) ?? info.Body;

        // A directly returned string literal is normalized to a fresh RC string by LowerEscapingResult.
        // The same literal behind control flow or a let is not unconditionally normalized, so retain the
        // existing exact-body special case before terminal-arm collection.
        if (body is Expr.StrLit)
        {
            return new ProvenanceFunctionNode(true, false, 1, new HashSet<FuncKey>(), null);
        }

        var letBindings = new Dictionary<string, ProvenanceLetBinding>(StringComparer.Ordinal);
        var arms = new List<ProvenanceArm>();
        IReadOnlyDictionary<string, FuncKey> scope = _maFunctionScopes.GetValueOrDefault(function)
            ?? new Dictionary<string, FuncKey>(StringComparer.Ordinal);
        CollectResultProvenanceTerminalArms(body, arms, letBindings, scope);
        return BuildProvenanceFunctionNodeFromArms(function, arms);
    }

    private ProvenanceFunctionNode BuildProvenanceFunctionNodeFromArms(
        FuncKey function,
        IReadOnlyList<ProvenanceArm> arms)
    {
        bool hasDirectEligibleResult = false;
        bool hasRejectedResult = false;
        int consideredArmCount = 0;
        var forwardTargets = new HashSet<FuncKey>();

        foreach (ProvenanceArm arm in arms)
        {
            if (IsSelfRecursiveArm(arm, function))
            {
                continue;
            }

            consideredArmCount++;
            if (IsDirectRcConstruction(arm.Expression, arm.LetBindings)
                || IsRuntimeRcFreshBuiltinProducer(arm.Expression)
                || arm.Expression is Expr.Add)
            {
                hasDirectEligibleResult = true;
            }
            else if (TryResolveForwardTarget(
                arm.Expression,
                arm.FunctionScope,
                out FuncKey target))
            {
                forwardTargets.Add(target);
            }
            else
            {
                hasRejectedResult = true;
            }
        }

        FuncKey? unambiguousForwardTarget = forwardTargets.Count == 1
            ? forwardTargets.Single()
            : null;
        return new ProvenanceFunctionNode(
            hasDirectEligibleResult,
            hasRejectedResult,
            consideredArmCount,
            forwardTargets,
            unambiguousForwardTarget);
    }

    /// <summary>
    /// Collects the terminal (non-control-flow) arms of <paramref name="body"/>: recurses through
    /// If/Match/Let/LetResult while carrying immutable snapshots of plain-let aliases and the lexical
    /// function scope. LetRecursive contributes only its body: a recursive binding is a separate
    /// definition, not a forwarding alias. Match-pattern binders shadow both snapshots in their arm.
    /// A terminal bare variable substitutes the plain-let value captured for it, using the alias and
    /// function scopes from the binding's value site rather than its later use site. This makes
    /// "let result = ‹fresh construction› in result" transparent without leaking a sibling branch's
    /// bindings or resolving a substituted call under a shadow introduced after it was evaluated.
    /// </summary>
    private void CollectResultProvenanceTerminalArms(
        Expr body,
        List<ProvenanceArm> arms,
        IReadOnlyDictionary<string, ProvenanceLetBinding> letBindings,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        switch (body)
        {
            case Expr.If iff:
                CollectResultProvenanceTerminalArms(iff.Then, arms, letBindings, scope);
                CollectResultProvenanceTerminalArms(iff.Else, arms, letBindings, scope);
                break;
            case Expr.Match match:
                foreach (MatchCase matchCase in match.Cases)
                {
                    var binders = new HashSet<string>(StringComparer.Ordinal);
                    CollectPatternBinders(matchCase.Pattern, binders);
                    CollectResultProvenanceTerminalArms(
                        matchCase.Body,
                        arms,
                        RemoveProvenanceLetBindings(letBindings, binders),
                        RemoveFuncNames(scope, binders));
                }

                break;
            case Expr.Let let:
                CollectResultProvenanceTerminalArms(
                    let.Body,
                    arms,
                    ExtendProvenanceLetBindings(letBindings, let.Name, let.Value, scope),
                    ExtendFuncScope(scope, let, let.Name));
                break;
            case Expr.LetResult letResult:
                CollectResultProvenanceTerminalArms(
                    letResult.Body,
                    arms,
                    ExtendProvenanceLetBindings(letBindings, letResult.Name, letResult.Value, scope),
                    ExtendFuncScope(scope, letResult, letResult.Name));
                break;
            case Expr.LetRecursive letRecursive:
                CollectResultProvenanceTerminalArms(
                    letRecursive.Body,
                    arms,
                    RemoveProvenanceLetBindings(letBindings, [letRecursive.Name]),
                    ExtendFuncScope(scope, letRecursive, letRecursive.Name));
                break;
            case Expr.Var v when letBindings.TryGetValue(v.Name, out ProvenanceLetBinding? binding):
                CollectResultProvenanceTerminalArms(
                    binding.Value, arms, binding.LetBindings, binding.FunctionScope);
                break;
            default:
                arms.Add(new ProvenanceArm(body, letBindings, scope));
                break;
        }
    }

    private static IReadOnlyDictionary<string, ProvenanceLetBinding> ExtendProvenanceLetBindings(
        IReadOnlyDictionary<string, ProvenanceLetBinding> letBindings,
        string name,
        Expr value,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        return new Dictionary<string, ProvenanceLetBinding>(letBindings, StringComparer.Ordinal)
        {
            [name] = new ProvenanceLetBinding(value, letBindings, scope),
        };
    }

    private static IReadOnlyDictionary<string, ProvenanceLetBinding> RemoveProvenanceLetBindings(
        IReadOnlyDictionary<string, ProvenanceLetBinding> letBindings,
        IEnumerable<string> names)
    {
        var next = new Dictionary<string, ProvenanceLetBinding>(letBindings, StringComparer.Ordinal);
        foreach (string name in names)
        {
            next.Remove(name);
        }

        return next;
    }

    private bool IsSelfRecursiveArm(ProvenanceArm arm, FuncKey function)
    {
        if (arm.Expression is not Expr.Call)
        {
            return false;
        }

        var arguments = new List<Expr>();
        Expr head = CollectCallArgs(arm.Expression, arguments);
        string? name = head switch
        {
            Expr.Var variable => variable.Name,
            Expr.QualifiedVar => ResolveSpecializableCalleeName(head),
            _ => null,
        };
        if (name is null
            || TryResolveFunctionKey(head, name, arm.FunctionScope) is not { } target)
        {
            return false;
        }

        if (target.Equals(function)
            && _maFuncs.TryGetValue(function, out var current)
            && arguments.Count == current.Params.Count)
        {
            return true;
        }

        return _maNestedRecursive.TryGetValue(function, out var nested)
            && target.Equals(nested.Recursive)
            && arguments.Count == 1;
    }

    /// <summary>
    /// Whether <paramref name="body"/> is a heap construction PROVABLY independent of any existing,
    /// possibly-still-aliased value — not merely a node of the right AST shape. <c>Expr.Cons</c>/
    /// <c>Expr.ListLit</c> delegate to the existing <see cref="IsFreshListConstructionExpression"/>
    /// (which itself requires the TAIL chain to bottom out at a literal <c>[]</c>/<c>[...]</c>, never a
    /// bare variable) rather than accepting any Cons node — a naive "Cons is always fresh" check misses
    /// exactly the <c>match xs with | h :: rest -> v :: rest</c> shape (rebuilding a list from an
    /// EXISTING list's tail, a genuine alias, not a fresh sub-structure): this was caught by
    /// Tco_adt_with_shared_tail_list_children_survives_across_iterations producing a corrupted value
    /// (a wrong number, not a crash) when this method still accepted any Cons/TupleLit/RecordLit/
    /// constructor-call node regardless of its children. A constructor application, tuple, or record
    /// literal is fresh only when EVERY argument/field is itself independently fresh
    /// (<see cref="IsFreshConstructionArgument"/>) — the same reasoning applied one level down, since a
    /// tuple/record/ADT cell that merely POINTS AT an aliased heap value is exactly as unsound to treat
    /// as wholly, independently owned. <paramref name="letBindings"/> is threaded through to
    /// <see cref="IsFreshConstructionArgument"/> so a terminal bare-Var argument can still resolve
    /// through a local let binding (see <see cref="CollectResultProvenanceTerminalArms"/>'s own doc).
    ///
    /// Deliberately does NOT recognize a bare <see cref="Expr.Lambda"/> as fresh construction, even
    /// though evaluating one always allocates a new closure object: WHETHER that closure ends up RC- or
    /// arena-represented depends on IsRuntimeRcCopyClosureProducer/ClosureCapturesRuntimeManagedHeapValue,
    /// which call LookupOwnedValue — live, per-scope lowering-time ownership state that does not exist
    /// during this pre-lowering, whole-program AST pass. Treating a Lambda as unconditionally fresh here
    /// would let a function whose closure is ACTUALLY arena-allocated (e.g. captures only copy-type
    /// values) be wrongly reported RC-eligible — a false positive, exactly the dangerous direction this
    /// phase must not risk. This is a permanent scope limit, not a gap to close later: closures are out
    /// of reach for a pre-lowering classifier by construction. A function whose result is a returned
    /// closure therefore always falls to the conservative default here, which in turn means
    /// TryResolveKnownFunctionResultOwnership never resolves such a call — verified this still safely
    /// preserves the untouched, pre-existing dynamic fallback (see
    /// IsConcretelyRuntimeManageableResultType's own TFun case for why that fallback is what actually
    /// matters for a closure result today).
    /// </summary>
    private bool IsDirectRcConstruction(
        Expr body,
        IReadOnlyDictionary<string, ProvenanceLetBinding> letBindings)
    {
        if (body is Expr.Cons or Expr.ListLit)
        {
            return IsFreshListConstructionExpression(body);
        }

        if (body is Expr.TupleLit tupleLit)
        {
            return tupleLit.Elements.All(element => IsFreshConstructionArgument(element, letBindings));
        }

        if (body is Expr.RecordLit recordLit)
        {
            return recordLit.Fields.All(field => IsFreshConstructionArgument(field.Value, letBindings));
        }

        // A bare nullary constructor reference (e.g. `Empty`) is an Expr.Var, not an Expr.Call — mirror
        // ResultReachVar's own treatment: confined (and so RC-eligible) only when it is the SOLE nullary
        // constructor of its type, matching the sound "no-op-safe tag cell" reasoning used throughout
        // this file (a non-sole nullary may be a shared static singleton, not a fresh allocation).
        if (body is Expr.Var nullary
            && _constructorSymbols.TryGetValue(nullary.Name, out ConstructorSymbol? nullaryConstructor)
            && nullaryConstructor is not null
            && nullaryConstructor.Arity == 0)
        {
            return IsSoleNullaryConstructor(nullaryConstructor);
        }

        if (body is not Expr.Call)
        {
            return false;
        }

        var arguments = new List<Expr>();
        Expr head = CollectCallArgs(body, arguments);
        if (head is Expr.Var variable
            && _constructorSymbols.TryGetValue(variable.Name, out ConstructorSymbol? constructor)
            && constructor is not null
            && arguments.Count == constructor.Arity
            && arguments.All(argument => IsFreshConstructionArgument(argument, letBindings)))
        {
            return true;
        }

        // The check above requires every argument to independently look fresh by this file's own
        // narrower, argument-shape-only rules — sound, but blind to a constructor field the runtime
        // dropper handles safely regardless of the argument expression's own shape (e.g. a List field
        // of copy-type elements, which the construction-time lowering always defensively normalizes to
        // an independent RC copy before it is ever stored, whatever expression produced it — see
        // LowerRuntimeManagedConstructorArgument's own CopyOutList fallback). Falling back to the exact
        // same top-cell-freshness query the construction-time lowering itself consults
        // (IsFreshRuntimeManageableAdtExpressionCore, which now also recognizes the positional
        // single-constructor accumulator shape via CanRuntimeManageTcoOwnedChildAdt) means this
        // pre-lowering classification can never claim a construction fresh that the real lowering
        // decision would not also independently treat as runtime-managed for the same reason.
        return IsFreshRuntimeManageableAdtExpressionCore(body);
    }

    /// <summary>
    /// Whether a single constructor-application argument or tuple/record field is provably independent
    /// of any existing value — a literal scalar, a nested fresh construction
    /// (<see cref="IsDirectRcConstruction"/>), a fresh-RC-producing builtin call, or an <c>Expr.Add</c>.
    ///
    /// Deliberately does NOT treat a reference to a parameter this function CONSUMES as automatically
    /// fresh, even though that was tried first (to keep <c>helper x = Full(x)</c> RC-eligible, matching
    /// Body_that_calls_a_sibling_helper_forwards_and_inherits_its_eligibility — a pre-existing Phase 0
    /// test): CONSUMED (ownership transferred to the callee) is not the same claim as FRESH/RC-COMPATIBLE
    /// — a consumed parameter can itself be an ARENA-scoped value moved in from the caller, and folding
    /// an arena pointer into a construction this classifier then reports RC-eligible lets the caller's
    /// own arena-reclaim-without-copy path (see TryResolveKnownFunctionResultOwnership's own doc)
    /// silently invalidate that field the next time the arena resets — reading it back afterward returns
    /// garbage/zeroed memory, not a crash. This was caught empirically by
    /// reuse_result_alias_move_elision.ash: `wrap v x = Node(x)(v)(Leaf)` treating consumed `x` as fresh
    /// made `wrap`'s result RC-eligible, and every subsequent read of the wrapped Tree came back `0`
    /// (`nested`/`keep`/`bumped` all printed `0 0 0` instead of `15 7 207`) — exactly the false-positive
    /// direction this phase must never risk, worse than the missed optimization (helper/Full test now
    /// conservatively fails again) accepting this conservative default costs. A bare variable of ANY
    /// origin (a consumed parameter, a pattern-matched sub-structure, a plain local) or an arbitrary
    /// (non-constructor, non-builtin) call is therefore excluded uniformly — any of them could alias a
    /// heap value still owned/reachable (or arena-scoped) elsewhere. This is conservative in the safe
    /// direction: a genuinely fresh forwarding-call argument is missed (falls to false), never the
    /// reverse.
    /// </summary>
    private bool IsFreshConstructionArgument(
        Expr argument,
        IReadOnlyDictionary<string, ProvenanceLetBinding> letBindings)
    {
        return argument switch
        {
            Expr.IntLit or Expr.UIntLit or Expr.FloatLit or Expr.BoolLit or Expr.BigIntLit or Expr.StrLit => true,
            Expr.Add => true,
            _ => IsDirectRcConstruction(argument, letBindings) || IsRuntimeRcFreshBuiltinProducer(argument),
        };
    }

    private bool TryResolveForwardTarget(
        Expr body,
        IReadOnlyDictionary<string, FuncKey> scope,
        out FuncKey target)
    {
        target = default;
        if (body is not Expr.Call)
        {
            return false;
        }

        var arguments = new List<Expr>();
        Expr head = CollectCallArgs(body, arguments);

        // A saturated constructor application is direct construction, not forwarding — already handled
        // by IsDirectRcConstruction; guard again so a call site is never double-classified.
        if (head is Expr.Var constructorHead && _constructorSymbols.ContainsKey(constructorHead.Name))
        {
            return false;
        }

        string? name = head switch
        {
            Expr.Var v => v.Name,
            Expr.QualifiedVar => ResolveSpecializableCalleeName(head),
            _ => null,
        };

        if (name is null
            || TryResolveFunctionKey(head, name, scope) is not { } key
            || !_maFuncs.TryGetValue(key, out var function)
            || arguments.Count != function.Params.Count)
        {
            return false;
        }

        target = key;
        return true;
    }

    private void ClearResultProvenanceAnalysis()
    {
        _maProvenanceMemo.Clear();
        _maProvenanceAnalysisComplete = false;
        _maProvenanceBodies.Clear();
    }
}
