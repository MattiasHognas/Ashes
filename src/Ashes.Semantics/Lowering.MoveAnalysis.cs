using Ashes.Frontend;

namespace Ashes.Semantics;

// Interprocedural move/linearity analysis for in-place-reuse copy elision.
//
// In-place reuse makes a fold's accumulator uniquely owned by deep-copying it at the fold
// function's entry, so the specialized `f$reuse` body may overwrite the accumulator's cells in
// place. That entry copy is the machinery's *only* mechanism for establishing uniqueness. When an
// outer loop threads a growing accumulator into an inner reuse fold, the inner fold's prologue copy
// re-executes on every re-entry and re-copies the whole structure — an O(re-entries × size) leak.
//
// This analysis proves, conservatively and whole-program, that the accumulator entering a fold is
// *already* uniquely owned at every external call site. When proven, the prologue copy is redundant
// (it would only duplicate an already-unique value) and is elided; the fold then receives exactly
// the precondition the copy used to guarantee. The default is always copy-stays: any shape the
// analysis cannot fully prove keeps the copy, so an incomplete analysis can only leak, never corrupt.
//
// Soundness rests on three checkable facts, no reuse-internals reasoning:
//   1. Move-linearity: at the call passing the accumulator, the argument has no other live
//      reference in the enclosing function (used at most once on any execution path, never captured
//      by a nested lambda). So overwriting it in place cannot corrupt a concurrently-live alias.
//   2. Transitivity: a Var argument is unique only if it is itself a move-safe accumulator
//      parameter of its enclosing function — recursively, down to a base seed. A greatest-fixpoint
//      computed on demand (cycles resolve to "not proven" = keep copy).
//   3. Seed safety: the base case is a value that is the *sole nullary constructor* of its type
//      (e.g. `Ashes.Collection.Map.empty = Empty`). Such a cell holds only its tag; the only reuse token it
//      can produce is a 0-field token, which — in a well-typed program — is consumed to rebuild the
//      same unique nullary constructor, writing the identical tag (a no-op). It therefore can never
//      be observably mutated, so it is safe to move even when shared. Field-bearing seeds have no
//      such guarantee and are not accepted.
//   4. Full visibility: a fold (or an intermediate function whose parameter is threaded) is only
//      considered if its name never escapes as a value — it appears solely as the head of a
//      saturated direct call — so the call-site census is provably complete.
public sealed partial class Lowering
{
    // Reference-identity key for one specific binding occurrence: its own Let/LetRecursive/LetResult
    // node, or a RecursiveGroupExpr plus member ordinal. Expr is a C# record (structural equality), so
    // — exactly like this file's other Expr-keyed
    // tables (_maExpressionFreshnessAll/_maExpressionFreshness below, both keyed by
    // ReferenceEqualityComparer) — wrapping the node and comparing by reference is what lets two
    // unrelated bindings that happen to share a bare name (e.g. two different functions each defining
    // their own local `let recursive go = ...` helper) resolve to two distinct identities instead of
    // colliding as dictionary keys.
    private readonly struct FuncKey : IEquatable<FuncKey>
    {
        private readonly Expr _owner;
        private readonly int _memberOrdinal;

        public FuncKey(Expr binder)
            : this(binder, -1)
        {
        }

        public FuncKey(Expr owner, int memberOrdinal)
        {
            _owner = owner;
            _memberOrdinal = memberOrdinal;
        }

        public bool Equals(FuncKey other) =>
            ReferenceEquals(_owner, other._owner) && _memberOrdinal == other._memberOrdinal;

        public override bool Equals(object? obj) => obj is FuncKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_owner),
            _memberOrdinal);
    }

    private sealed record MoveCallSite(
        FuncKey? Enclosing,
        IReadOnlyList<Expr> Args,
        IReadOnlyList<IReadOnlyDictionary<string, FuncKey>> ArgumentScopes);

    // Per top-level function: its curried parameter names (in order) and its innermost body. Keyed by
    // the binding's own FuncKey (its defining Let/LetRecursive/LetResult node), not its bare name.
    private readonly Dictionary<FuncKey, (List<string> Params, Expr Body)> _maFuncs = new();

    // The lexical function scope at the entry of each registered function body. Populated by the same
    // normalized-body walk that collects calls, so ResultReach and move analysis resolve copied binders
    // to the same FuncKey as the call census.
    private readonly Dictionary<FuncKey, IReadOnlyDictionary<string, FuncKey>> _maFunctionScopes = new();

    // Every globally-unambiguous value binding's right-hand side (used to resolve accumulator seeds).
    // Colliding plain values stay conservatively unresolved under the bare-name compatibility lookup.
    private readonly Dictionary<string, Expr> _maValueRhs = new(StringComparer.Ordinal);

    // Compatibility/global index for names seen exactly once across every value binding, removed when
    // a second binding of the same name is found. Bare-name call/escape collection, ResultReach/move
    // analysis, provenance, and TcoParamFactsWalk resolve through their own live lexical scopes.
    // Qualified module references and compatibility/introspection lookups still use this index.
    private readonly Dictionary<string, FuncKey> _maNameIndex = new(StringComparer.Ordinal);

    // The source name each registered FuncKey was declared under. Unlike _maNameIndex, this retains an
    // entry for every colliding function so internal analysis and later stable-origin materialization
    // never lose the binding identity.
    private readonly Dictionary<FuncKey, string> _maKeyName = new();

    // Stable source/report identities are kept separately from FuncKey so later reporting can sort
    // and filter functions without leaking AST reference identity.
    private readonly Dictionary<FuncKey, SourceFunctionOrigin> _maFunctionOrigins = new();
    private readonly Dictionary<Expr.Lambda, SourceFunctionOrigin> _sourceFunctionOriginsByLambda =
        new(ReferenceEqualityComparer.Instance);

    // StripModuleAliasPrefix substitutes stitched aliases by rebuilding the remaining AST. Preserve an
    // explicit link from every rebuilt binding node to its original binding occurrence; name lookup
    // cannot recover this identity once two local functions legitimately share a source name.
    private readonly Dictionary<Expr, Expr> _maOriginalBinderByCopy =
        new(ReferenceEqualityComparer.Instance);

    // Saturated direct call sites, keyed by the callee's FuncKey: (enclosing function's FuncKey, or null
    // at top level, flattened args).
    private readonly Dictionary<FuncKey, List<MoveCallSite>> _maCallSites = new();

    // Functions that appear anywhere other than as the head of a saturated direct call. Their
    // call-site census is not provably complete, so they are never treated as move-safe.
    private readonly HashSet<FuncKey> _maEscaped = new();
    private readonly Dictionary<FuncKey, FunctionCallCensusCause> _maCallCensusCauses = new();

    // Memoization for the on-demand greatest fixpoint. A (func,param) currently being resolved is in
    // _maInProgress; re-encountering it (a cycle) yields "not proven" — the sound under-approximation.
    private readonly Dictionary<(FuncKey, string), bool> _maMoveSafeMemo = new();
    private readonly HashSet<(FuncKey, string)> _maInProgress = new();
    private readonly HashSet<(FuncKey, string)> _maMoveSafetyCycles = new();

    private bool _maAnalyzed;

    // The fully-desugared program body. Used as the "enclosing body" for the local-let move check at
    // top-level call sites (enclosing == ""), which have no registered function frame: a top-level
    // `let seed = <fresh> in ... F(seed) ...` binds `seed` on this spine, and move-linearity over the
    // whole program is the (stronger) proof that no other live reference exists.
    private Expr? _maBody;

    // Names bound by more than one let/letrec in the desugared tree. Internal function resolution is
    // lexical and does not consult this set. Name-keyed value/partial-application compatibility paths
    // remain conservative when a duplicated name cannot identify one binding occurrence.
    private readonly HashSet<string> _maAmbiguous = new(StringComparer.Ordinal);

    // Result-reachability (may-alias) summary (CO-2 result-alias elision): per registered function, a
    // conservative OVER-APPROXIMATION of which of its own parameters the function's RESULT value may be
    // reachable-through / alias, as a per-parameter multiplicity, plus a "poison" flag meaning the
    // result is not provably confined to the parameters (it may alias a top-level/global binding, an
    // unmodeled value, or be internally shared). Multiplicities are capped at 2: a parameter reachable
    // through two simultaneous heap positions (internal sharing, e.g. `Node(x)(0)(x)`) forces poison,
    // because moving such an argument would leave two live aliases the reuse fold could corrupt.
    // Computed once as a monotone least fixpoint — every function starts with empty reach and not
    // poisoned; reach sets and poison only grow until stable — so an under-computed early pass can only
    // stay smaller, never over-claim (the sound direction for a may-analysis). Used to admit a fold
    // accumulator seeded by a builder's *result* as a move: a `wrap`-style builder
    // (`let wrap x = Node(x)(0)(Leaf)`) reaches {x}, so `wrap(<arg>)` is a move iff the argument bound
    // to `x` is itself a move. A function whose result reaches {} and is not poisoned is result-fresh
    // (its result is a uniquely-owned freshly-allocated value for any arguments) — the higher-order-seed
    // case, subsumed here as the empty-reach special case.
    private readonly record struct ResultReachState(
        Dictionary<string, int> Counts,
        ResultReachCause Causes)
    {
        public bool Poison => Causes != ResultReachCause.None;
    }

    private readonly Dictionary<FuncKey, ResultReachState> _maResultReach = new();

    // Stable per-function ownership contracts materialized after the move-safety and result-reach
    // fixpoints converge. Later lowering passes read this table instead of reaching into the analysis
    // dictionaries directly, through the name-based GetOwnershipSummary surface below (resolved via
    // _maNameIndex), not this dictionary directly.
    private readonly Dictionary<FuncKey, FunctionOwnershipSummary> _ownershipSummaries = new();
    private readonly HashSet<OwnershipFactConsumption> _ownershipFactConsumptions = new();

    // Flat merge of every registered function's ExpressionFreshness map (see AnalyzeReuseCopyElision),
    // keyed by reference identity like the per-function maps it merges. Lets a shadow-compare hook
    // elsewhere in the lowering pass (see Lowering.OwnershipShadowCompare.cs) look up an arbitrary
    // expression's freshness fact without first having to determine which function's body it belongs to.
    private readonly Dictionary<Expr, bool> _maExpressionFreshnessAll = new(ReferenceEqualityComparer.Instance);

    // Recording sink for ExpressionFreshness (see RecordExpressionFreshness): non-null only during the
    // dedicated post-fixpoint recording pass for the function currently being (re-)walked in
    // ComputeExpressionFreshness, null (a no-op) during the hot while-changed fixpoint loop in
    // ComputeResultReach. Expr is a C# record (structural equality), so this — like every other
    // Expr-keyed table in this compiler (e.g. Lowering.cs's _inPlaceReuseCallExprs) — must key by
    // reference identity, not structural equality.
    private Dictionary<Expr, bool>? _maExpressionFreshness;

    // Reach multiplicity cap: any count reaching this is folded into the poison flag (internal sharing).
    private const int ReachCap = 2;

    // Per-binding synthetic identity token counter (reset at the start of each function's ResultReach
    // pass). Every locally-introduced binding (a `let`/let-result value, a `match` pattern variable) is
    // given a unique synthetic reach token summed into its env reach, so multiplicity is tracked for a
    // *fresh* (non-parameter) heap value exactly as it is for a parameter: embedding the same bound name
    // through two simultaneous heap positions (e.g. `let x = Node(...)(u)(...) in Node(x)(0)(x)`, or
    // `[x, x]`) sums the token to the cap and poisons — the fresh value is internally shared, so not
    // uniquely owned, and its entry copy (which exists precisely to unshare it) must stay. Tokens are
    // stripped from the stored per-function summary (see ComputeResultReach): a count-1 token is a fresh
    // internal cell escaping via a single path (harmless, confined), and a count-2 token has already set
    // poison during the sum. Token keys are prefixed with '#', which no real identifier uses, so they can
    // never collide with a parameter name and never reach IsResultAliasMove (which maps real params only).
    private int _maReachToken;

    // Recursion guard for over-application (CO-2d closure seeds): OverApplicationReach inlines a
    // callee's body one level to see through a returned closure; a bounded depth caps any chain of
    // nested over-applications (each level poisons past the cap — the sound direction).
    private const int MaxOverAppDepth = 4;
    private int _maOverAppDepth;

    // Nested-rec-return (Map.set-shape) functions: a chain of outer-parameter lambdas whose innermost body
    // is `let rec go = (given acc -> B) in go`, i.e. the function RETURNS a recursive single-accumulator
    // function. Such a function is registered in _maFuncs with the FULL parameter list (outer params +
    // acc) and body B, so its saturated (outerCount+1)-arg application is analyzable; the inner recursive
    // self-call `go(x)` is resolved to the function's own growing summary (see _maSelfRecursive / CallReach) with
    // the outer params held at identity and the accumulator bound to x. Retains the exact inner recursive
    // binder identity and name, the outer parameter names, and the accumulator parameter name.
    private readonly Dictionary<FuncKey, (FuncKey Recursive, string RecursiveName, List<string> Outer, string Acc)>
        _maNestedRecursive = new();

    // The nested-rec context of the function currently being analyzed by ComputeResultReach (set per pass,
    // null for ordinary functions). Lets CallReach recognize the inner `go(x)` self-call and map it to the
    // enclosing function's summary. Func is the registered (full-param) function's FuncKey.
    private (FuncKey Func, FuncKey Recursive, string RecursiveName, List<string> Outer, string Acc)? _maSelfRecursive;

    /// <summary>
    /// Builds the whole-program call-site census and function tables used by
    /// <see cref="IsParamMoveSafe"/> over the fully desugared program expression (which
    /// contains the stitched stdlib bindings, the user's top-level declarations, and the trailing
    /// expression as one nested let chain). Idempotent.
    /// </summary>
    private void AnalyzeReuseCopyElision(Expr desugaredBody)
    {
        _maFuncs.Clear();
        _maValueRhs.Clear();
        _maNameIndex.Clear();
        _maKeyName.Clear();
        _maFunctionOrigins.Clear();
        _sourceFunctionOriginsByLambda.Clear();
        ClearHandlerEffectAnalysis();
        _maOriginalBinderByCopy.Clear();
        _maFunctionScopes.Clear();
        _maCallSites.Clear();
        ClearOwnershipDecisionState();
        _maAmbiguous.Clear();
        ClearOwnershipMemoization();
        _maResultReach.Clear();
        _maNestedRecursive.Clear();
        _ownershipSummaries.Clear();
        _maExpressionFreshnessAll.Clear();
        _maBody = desugaredBody;

        RegisterBindings(desugaredBody, enclosingSource: null);

        // Duplicated names remain unavailable through the global compatibility/value indexes. Every
        // lambda-valued binding itself stays registered and is resolved internally through lexical
        // FuncKey scopes.
        foreach (var name in _maAmbiguous)
        {
            _maNameIndex.Remove(name);
            _maValueRhs.Remove(name);
        }

        CollectCallsAndEscapes(desugaredBody, null, new Dictionary<string, FuncKey>(StringComparer.Ordinal));
        ComputeLiveHandlerEffects();
        ComputeResultReach();
        ComputeFunctionResultProvenanceFixpoint();
        _maAnalyzed = true;
        BuildOwnershipSummaries();
    }

    private void BuildOwnershipSummaries()
    {
        foreach (FuncKey key in _maFuncs.Keys
            .OrderBy(key => _maFunctionOrigins[key].QualifiedName, StringComparer.Ordinal)
            .ThenBy(key => _maFunctionOrigins[key].SourceName, StringComparer.Ordinal)
            .ThenBy(key => _maFunctionOrigins[key].DeclarationOffset))
        {
            var functionName = _maKeyName[key];
            var summary = CreateOwnershipSummary(key, functionName, _maFuncs[key]);
            _ownershipSummaries[key] = summary;

            // Every Expr node is reference-unique across the whole desugared program (each function's
            // body is a distinct subtree), so merging every function's ExpressionFreshness map into one
            // flat table is unambiguous and gives shadow-compare hooks elsewhere in the lowering pass an
            // O(1) lookup that does not require knowing which function currently owns a given node.
            foreach (var (expr, fresh) in summary.ExpressionFreshness)
            {
                _maExpressionFreshnessAll.TryAdd(expr, fresh);
            }
        }
    }

    private void ClearOwnershipMemoization()
    {
        _maMoveSafeMemo.Clear();
        _maInProgress.Clear();
        _maMoveSafetyCycles.Clear();
        ClearResultProvenanceAnalysis();
    }

    private void ClearOwnershipDecisionState()
    {
        _maEscaped.Clear();
        _maCallCensusCauses.Clear();
        _ownershipFactConsumptions.Clear();
        _patternBindingOwnershipDecisions.Clear();
    }

    private Expr StripOrSelf(Expr value)
    {
        try
        {
            return StripModuleAliasPrefix(
                value,
                (original, rebuilt) => _maOriginalBinderByCopy[rebuilt] = GetCanonicalBinder(original));
        }
        catch (System.NotSupportedException)
        {
            return value;
        }
    }

    /// <summary>
    /// Records every let/letrec binding in the desugared tree: lambda-valued ones as functions
    /// (params + innermost body), all as value RHS (for seed resolution). Flags duplicate names.
    /// </summary>
    private void RegisterBindings(Expr e, SourceFunctionOrigin? enclosingSource)
    {
        switch (e)
        {
            case Expr.Let l:
                RegisterBindings(l, enclosingSource);
                return;
            case Expr.LetRecursive lr:
                RegisterBindings(lr, enclosingSource);
                return;
            case Expr.LetResult lres:
                RegisterBindings(lres, enclosingSource);
                return;
            case RecursiveGroupExpr group:
                RegisterRecursiveGroupBindings(group, enclosingSource);
                return;
            case Expr.Lambda lam:
                RegisterBindings(lam.Body, enclosingSource);
                return;
            case Expr.If i:
                RegisterBindings(i.Cond, enclosingSource);
                RegisterBindings(i.Then, enclosingSource);
                RegisterBindings(i.Else, enclosingSource);
                return;
            case Expr.Match m:
                RegisterBindings(m.Value, enclosingSource);
                foreach (var c in m.Cases)
                {
                    RegisterBindings(c.Body, enclosingSource);
                    if (c.Guard is not null)
                    {
                        RegisterBindings(c.Guard, enclosingSource);
                    }
                }

                return;
            case Expr.Call c:
                RegisterBindings(c.Func, enclosingSource);
                RegisterBindings(c.Arg, enclosingSource);
                return;
            default:
                foreach (var child in EnumerateChildren(e))
                {
                    RegisterBindings(child, enclosingSource);
                }

                return;
        }
    }

    private void RegisterBindings(Expr.Let let, SourceFunctionOrigin? enclosingSource)
    {
        SourceFunctionOrigin? declared = RegisterOneBinding(
            let,
            let.Name,
            let.Value,
            AstSpans.GetLetNameOrDefault(let),
            enclosingSource);
        RegisterBindings(let.Value, declared ?? enclosingSource);
        RegisterBindings(let.Body, enclosingSource);
    }

    private void RegisterBindings(Expr.LetRecursive let, SourceFunctionOrigin? enclosingSource)
    {
        SourceFunctionOrigin? declared = RegisterOneBinding(
            let,
            let.Name,
            let.Value,
            AstSpans.GetLetRecursiveNameOrDefault(let),
            enclosingSource);
        RegisterBindings(let.Value, declared ?? enclosingSource);
        RegisterBindings(let.Body, enclosingSource);
    }

    private void RegisterBindings(Expr.LetResult let, SourceFunctionOrigin? enclosingSource)
    {
        SourceFunctionOrigin? declared = RegisterOneBinding(
            let,
            let.Name,
            let.Value,
            AstSpans.GetLetResultNameOrDefault(let),
            enclosingSource);
        RegisterBindings(let.Value, declared ?? enclosingSource);
        RegisterBindings(let.Body, enclosingSource);
    }

    private void RegisterRecursiveGroupBindings(
        RecursiveGroupExpr group,
        SourceFunctionOrigin? enclosingSource)
    {
        // A group is one AST node containing several simultaneous bindings. Register every member
        // first so subsequent traversal can seed the complete sibling scope.
        var origins = new SourceFunctionOrigin?[group.Bindings.Count];
        for (int i = 0; i < group.Bindings.Count; i++)
        {
            (string name, Expr value) = group.Bindings[i];
            TextSpan nameSpan = i < group.BindingNameSpans.Count
                ? group.BindingNameSpans[i]
                : AstSpans.GetOrDefault(value);
            origins[i] = RegisterOneBinding(
                GetRecursiveGroupMemberKey(group, i),
                name,
                value,
                nameSpan,
                enclosingSource);
        }

        for (int i = 0; i < group.Bindings.Count; i++)
        {
            RegisterBindings(group.Bindings[i].Value, origins[i] ?? enclosingSource);
        }

        RegisterBindings(group.Body, enclosingSource);
    }

    private SourceFunctionOrigin? RegisterOneBinding(
        Expr binder,
        string name,
        Expr value,
        TextSpan nameSpan,
        SourceFunctionOrigin? enclosingSource)
    {
        return RegisterOneBinding(
            GetCanonicalFuncKey(binder),
            name,
            value,
            nameSpan,
            enclosingSource);
    }

    private SourceFunctionOrigin? RegisterOneBinding(
        FuncKey key,
        string name,
        Expr value,
        TextSpan nameSpan,
        SourceFunctionOrigin? enclosingSource)
    {
        bool duplicate = _maAmbiguous.Contains(name)
            || _maValueRhs.ContainsKey(name)
            || _maNameIndex.ContainsKey(name);
        if (duplicate)
        {
            _maAmbiguous.Add(name);
            _maValueRhs.Remove(name);
            _maNameIndex.Remove(name);
        }

        var stripped = StripOrSelf(value);
        if (!duplicate)
        {
            _maValueRhs[name] = stripped;
        }

        if (stripped is Expr.Lambda lam)
        {
            SourceFunctionOrigin origin = CreateSourceFunctionOrigin(name, nameSpan, enclosingSource);
            _maFunctionOrigins[key] = origin;
            _maFunctionKeyByLambda[lam] = key;
            if (FindInnermostLambdaUnderLets(value) is { } sourceLambda)
            {
                _sourceFunctionOriginsByLambda[sourceLambda] = origin;
            }

            if (TryGetNestedRecursiveReturnShape(
                lam, out var outer, out var accParam, out var recursiveBinder, out var recursiveName, out var innerBody))
            {
                // Register the Map.set-shape with the accumulator as a real trailing parameter and the
                // inner recursive body as the function body, so its full (outer + accumulator) application
                // is analyzable. The inner `go(x)` self-call is resolved in CallReach via _maSelfRecursive.
                var full = new List<string>(outer) { accParam };
                _maFuncs[key] = (full, innerBody);
                _maNestedRecursive[key] = (
                    GetCanonicalFuncKey(recursiveBinder),
                    recursiveName,
                    outer,
                    accParam);
            }
            else
            {
                _maFuncs[key] = (CollectLambdaParams(lam), GetInnermostBody(lam));
            }

            if (!duplicate)
            {
                _maNameIndex[name] = key;
            }

            _maKeyName[key] = name;
            return origin;
        }

        return null;
    }

    private SourceFunctionOrigin CreateSourceFunctionOrigin(
        string compilerName,
        TextSpan nameSpan,
        SourceFunctionOrigin? enclosingSource)
    {
        string sourceName = compilerName;
        string? qualifiedName = null;
        if (enclosingSource is null
            && _functionSourceNames is not null
            && _functionSourceNames.TryGetValue(compilerName, out SourceFunctionName? mapped))
        {
            sourceName = mapped.SourceName;
            qualifiedName = mapped.QualifiedName;
        }
        else if (enclosingSource?.QualifiedName is { } parentQualified)
        {
            qualifiedName = $"{parentQualified}.{compilerName}";
        }

        return new SourceFunctionOrigin(
            sourceName,
            qualifiedName,
            ResolveSourceLocation(nameSpan),
            nameSpan.Start);
    }

    private FuncKey GetCanonicalFuncKey(Expr binder)
    {
        return new FuncKey(GetCanonicalBinder(binder));
    }

    private static FuncKey GetRecursiveGroupMemberKey(RecursiveGroupExpr group, int memberOrdinal)
    {
        return new FuncKey(group, memberOrdinal);
    }

    private Expr GetCanonicalBinder(Expr binder)
    {
        var visited = new HashSet<Expr>(ReferenceEqualityComparer.Instance);
        Expr canonical = binder;
        while (visited.Add(canonical)
            && _maOriginalBinderByCopy.TryGetValue(canonical, out Expr? original))
        {
            canonical = original;
        }

        return canonical;
    }

    /// <summary>
    /// Recognizes the nested-rec-return (Map.set) shape: a chain of outer-parameter lambdas whose innermost
    /// body is <c>let rec go = (given acc -&gt; B) in go</c> (the letrec binder returned bare, its value a
    /// single-parameter lambda whose own body is not a further lambda). Outputs the outer parameter names,
    /// the accumulator parameter name, the recursive binder name, and the inner body B.
    /// </summary>
    private static bool TryGetNestedRecursiveReturnShape(
        Expr.Lambda lam,
        out List<string> outer,
        out string accParam,
        out Expr.LetRecursive recursiveBinder,
        out string recursiveName,
        out Expr innerBody)
    {
        outer = new List<string>();
        accParam = string.Empty;
        recursiveBinder = null!;
        recursiveName = string.Empty;
        innerBody = lam;
        Expr body = lam;
        while (body is Expr.Lambda inner)
        {
            outer.Add(inner.ParamName);
            body = inner.Body;
        }

        if (body is Expr.LetRecursive { Value: Expr.Lambda recursiveValue, Body: Expr.Var recursiveRef } letRecursive
            && string.Equals(letRecursive.Name, recursiveRef.Name, StringComparison.Ordinal)
            && recursiveValue.Body is not Expr.Lambda)
        {
            accParam = recursiveValue.ParamName;
            recursiveBinder = letRecursive;
            recursiveName = letRecursive.Name;
            innerBody = recursiveValue.Body;
            return true;
        }

        return false;
    }

    private static IEnumerable<Expr> EnumerateChildren(Expr e)
    {
        switch (e)
        {
            case Expr.Add or Expr.Subtract or Expr.Multiply or Expr.Divide or Expr.Modulo
                or Expr.BitwiseAnd or Expr.BitwiseOr or Expr.BitwiseXor
                or Expr.ShiftLeft or Expr.ShiftRight or Expr.BitwiseNot or Expr.LogicalNot:
                return EnumerateChildrenArithmetic(e);
            case Expr.GreaterThan or Expr.LessThan or Expr.GreaterOrEqual or Expr.LessOrEqual
                or Expr.Equal or Expr.NotEqual or Expr.ResultPipe or Expr.ResultMapErrorPipe
                or Expr.Cons or Expr.Await:
                return EnumerateChildrenComparisons(e);
            default:
                return EnumerateChildrenAggregates(e);
        }
    }

    private static IEnumerable<Expr> EnumerateChildrenArithmetic(Expr e)
    {
        switch (e)
        {
            case Expr.Add x: yield return x.Left; yield return x.Right; break;
            case Expr.Subtract x: yield return x.Left; yield return x.Right; break;
            case Expr.Multiply x: yield return x.Left; yield return x.Right; break;
            case Expr.Divide x: yield return x.Left; yield return x.Right; break;
            case Expr.Modulo x: yield return x.Left; yield return x.Right; break;
            case Expr.BitwiseAnd x: yield return x.Left; yield return x.Right; break;
            case Expr.BitwiseOr x: yield return x.Left; yield return x.Right; break;
            case Expr.BitwiseXor x: yield return x.Left; yield return x.Right; break;
            case Expr.ShiftLeft x: yield return x.Left; yield return x.Right; break;
            case Expr.ShiftRight x: yield return x.Left; yield return x.Right; break;
            case Expr.BitwiseNot x: yield return x.Operand; break;
            case Expr.LogicalNot x: yield return x.Operand; break;
            default:
                break;
        }
    }

    private static IEnumerable<Expr> EnumerateChildrenComparisons(Expr e)
    {
        switch (e)
        {
            case Expr.GreaterThan x: yield return x.Left; yield return x.Right; break;
            case Expr.LessThan x: yield return x.Left; yield return x.Right; break;
            case Expr.GreaterOrEqual x: yield return x.Left; yield return x.Right; break;
            case Expr.LessOrEqual x: yield return x.Left; yield return x.Right; break;
            case Expr.Equal x: yield return x.Left; yield return x.Right; break;
            case Expr.NotEqual x: yield return x.Left; yield return x.Right; break;
            case Expr.ResultPipe x: yield return x.Left; yield return x.Right; break;
            case Expr.ResultMapErrorPipe x: yield return x.Left; yield return x.Right; break;
            case Expr.Cons x: yield return x.Head; yield return x.Tail; break;
            case Expr.Await x: yield return x.Task; break;
            default:
                break;
        }
    }

    private static IEnumerable<Expr> EnumerateChildrenAggregates(Expr e)
    {
        switch (e)
        {
            case Expr.TupleLit x:
                foreach (var el in x.Elements) { yield return el; }

                break;
            case Expr.ListLit x:
                foreach (var el in x.Elements) { yield return el; }

                break;
            case Expr.RecordLit x:
                foreach (var (_, fv) in x.Fields) { yield return fv; }

                break;
            case Expr.RecordUpdate x:
                yield return x.Target;
                foreach (var (_, uv) in x.Updates) { yield return uv; }

                break;
            default:
                break;
        }
    }

    /// <summary>
    /// The authoritative reuse-elision predicate: the prologue deep-copy of accumulator parameter
    /// <paramref name="accParam"/> of fold <paramref name="funcName"/> can be safely elided exactly
    /// when the accumulator is a unique parameter of the fold's ownership summary (proven uniquely
    /// owned at every external call site). Read through <see cref="GetOwnershipSummary(string)"/> so the
    /// summary is the single source of truth for the decision. Conservative — false on any uncertainty
    /// (no summary, or the parameter not proven unique).
    /// </summary>
    private bool ReuseAccumulatorIsUnique(
        FuncKey? function,
        string funcName,
        string accParam,
        out ParameterMoveSafetyCause moveSafetyCauses)
    {
        FunctionOwnershipSummary? summary = function is { } key
            ? GetOwnershipSummary(key)
            : GetOwnershipSummary(funcName);
        if (summary is null)
        {
            moveSafetyCauses = ParameterMoveSafetyCause.ConservativeUnknown;
            return false;
        }

        ParameterMoveSafetyProof? proof =
            summary.ParameterMoveSafety.GetValueOrDefault(accParam);
        bool unique = proof?.IsMoveSafe == true;
        moveSafetyCauses =
            proof?.Causes ?? ParameterMoveSafetyCause.ConservativeUnknown;
        RecordOwnershipFactConsumption(
            summary,
            OwnershipDecisionKind.ReuseEntryCopyElision,
            accParam,
            OwnershipDecisionFact.ParameterMoveSafety,
            unique ? OwnershipDecisionFact.ParameterMoveSafety : OwnershipDecisionFact.None,
            unique);
        return unique;
    }

    /// <summary>
    /// The names of every top-level function the ownership analysis registered (empty until the
    /// program has been analysed). Introspection surface for ownership summaries.
    /// </summary>
    internal IReadOnlyCollection<string> AnalyzedFunctionNames =>
        _maAnalyzed
            ? _ownershipSummaries.Keys.Select(key => _maKeyName[key]).OrderBy(name => name, StringComparer.Ordinal).ToList()
            : [];

    /// <summary>
    /// Every retained ownership summary in stable source order. Returns an immutable projection
    /// rather than exposing the mutable analysis dictionary or its internal <see cref="FuncKey"/>.
    /// </summary>
    internal IReadOnlyList<FunctionOwnershipSummary> OwnershipSummaries =>
        _maAnalyzed ? OrderOwnershipSummaries(_ownershipSummaries.Values).ToList() : [];

    internal IReadOnlyList<OwnershipFactConsumption> OwnershipFactConsumptions =>
        _ownershipFactConsumptions
            .OrderBy(consumption => consumption.Function.QualifiedName, StringComparer.Ordinal)
            .ThenBy(consumption => consumption.Function.DeclarationOffset)
            .ThenBy(consumption => consumption.Decision)
            .ThenBy(consumption => consumption.Parameter, StringComparer.Ordinal)
            .ToList();

    private void RecordOwnershipFactConsumption(
        FunctionOwnershipSummary summary,
        OwnershipDecisionKind decision,
        string? parameter,
        OwnershipDecisionFact evaluatedFacts,
        OwnershipDecisionFact positiveFacts,
        bool outcome)
    {
        _ownershipFactConsumptions.Add(
            new OwnershipFactConsumption(
                summary.Origin,
                decision,
                parameter,
                evaluatedFacts,
                positiveFacts,
                outcome));
    }

    /// <summary>
    /// Returns the materialized <see cref="FunctionOwnershipSummary"/> for a top-level function, or null if
    /// the program has not been analysed or <paramref name="function"/> is not a registered,
    /// fully-visible top-level function. The summary combines the same move-safety and result-reach
    /// fixpoints the reuse path consumes with resource borrow inference and captured values.
    /// </summary>
    internal FunctionOwnershipSummary? GetOwnershipSummary(string function)
    {
        return _maAnalyzed
            && _maNameIndex.TryGetValue(function, out var key)
            ? GetOwnershipSummary(key)
            : null;
    }

    private FunctionOwnershipSummary? GetOwnershipSummary(FuncKey function)
    {
        return _maAnalyzed
            && _ownershipSummaries.TryGetValue(function, out FunctionOwnershipSummary? summary)
                ? summary
                : null;
    }

    private FuncKey? GetRegisteredFunctionKey(Expr binder)
    {
        FuncKey key = GetCanonicalFuncKey(binder);
        return _maFuncs.ContainsKey(key) ? key : null;
    }

    private (
        IReadOnlySet<int> LoopInvariant,
        IReadOnlySet<int> ArenaSelfContainedListRebuild,
        IReadOnlySet<int> FreshClosureRebuild,
        IReadOnlySet<int> BytesProvenanceSafeListRebuild,
        IReadOnlySet<int> AffineConsList,
        IReadOnlySet<int> ConsumedListTail,
        IReadOnlySet<int> BorrowInspectOnly,
        IReadOnlySet<int> AffineSelfAppendOnly) GetTcoParameterOrdinalFacts(FuncKey? function)
        => (
            GetTcoParameterOrdinals(function, TcoSelfCallArgumentShape.UnchangedPassthrough),
            GetTcoParameterOrdinals(function, static facts => facts.ArenaSelfContainedListRebuild),
            GetTcoParameterOrdinals(function, static facts => facts.FreshClosureRebuild),
            GetTcoParameterOrdinals(
                function,
                static facts => facts.BytesProvenanceSafeListRebuild),
            GetTcoParameterOrdinals(function, TcoSelfCallArgumentShape.GrownCons),
            GetTcoParameterOrdinals(function, TcoSelfCallArgumentShape.ConsumedTail),
            GetTcoParameterOrdinals(
                function,
                static facts => facts.UseMode == TcoParamUseMode.BorrowInspectOnly),
            GetTcoParameterOrdinals(
                function,
                static facts => facts.ReuseAffinity == TcoParamReuseAffinity.SelfAppendOnly));

    private IReadOnlyList<PatternBindingOwnershipFact> GetPatternBindingOwnershipFacts(
        FuncKey? function)
    {
        return function is { } key && GetOwnershipSummary(key) is { } summary
            ? summary.PatternBindingOwnership
            : [];
    }

    private IReadOnlySet<int> GetTcoParameterOrdinals(
        FuncKey? function,
        TcoSelfCallArgumentShape shape)
        => GetTcoParameterOrdinals(function, facts => facts.Shape == shape);

    private IReadOnlySet<int> GetTcoParameterOrdinals(
        FuncKey? function,
        Func<TcoParamStructuralFacts, bool> matches)
    {
        HashSet<int> result = [];
        if (function is not { } key || GetOwnershipSummary(key) is not { } summary)
        {
            return result;
        }

        foreach (TcoParamStructuralFacts facts in summary.TcoParamFacts)
        {
            if (matches(facts))
            {
                result.Add(facts.ParameterOrdinal);
            }
        }

        return result;
    }

    private void RegisterOwnershipFunctionLabel(string label, Expr binder)
    {
        if (GetRegisteredFunctionKey(binder) is { } key)
        {
            RegisterOwnershipFunctionLabel(label, key);
        }
    }

    private void RegisterOwnershipFunctionLabel(string label, FuncKey function)
    {
        if (_maFuncs.ContainsKey(function))
        {
            _functionKeyByLabel[label] = function;
        }
    }

    private FunctionOwnershipSummary? GetOwnershipSummaryForLabel(string label)
    {
        if (_functionKeyByLabel.TryGetValue(label, out FuncKey key))
        {
            return GetOwnershipSummary(key);
        }

        return _functionNameByLabel.TryGetValue(label, out string? function)
            ? GetOwnershipSummary(function)
            : null;
    }

    private FunctionOwnershipSummary? GetOwnershipSummaryForCallRoot(Expr root)
    {
        if (TryResolveKnownFunctionLabel(root, out string label)
            && GetOwnershipSummaryForLabel(label) is { } exact)
        {
            return exact;
        }

        if (root is Expr.Var variable && Lookup(variable.Name) is not null)
        {
            return null;
        }

        string? function = root switch
        {
            Expr.Var unboundVariable => unboundVariable.Name,
            Expr.QualifiedVar => ResolveSpecializableCalleeName(root),
            _ => null,
        };
        return function is null ? null : GetOwnershipSummary(function);
    }

    // Collision-aware introspection for focused compiler tests. A bare-name compatibility lookup must
    // remain null when the name is ambiguous; this surface deliberately returns every retained binding
    // so tests can prove their distinct facts without weakening source-name resolution.
    internal IReadOnlyList<FunctionOwnershipSummary> GetOwnershipSummaries(string function)
    {
        return _maAnalyzed
            ? OrderOwnershipSummaries(_ownershipSummaries
                .Where(entry => string.Equals(_maKeyName[entry.Key], function, StringComparison.Ordinal))
                .Select(entry => entry.Value))
                .ToList()
            : [];
    }

    private static IOrderedEnumerable<FunctionOwnershipSummary> OrderOwnershipSummaries(
        IEnumerable<FunctionOwnershipSummary> summaries)
        => summaries
            .OrderBy(summary => summary.Origin.QualifiedName, StringComparer.Ordinal)
            .ThenBy(summary => summary.Origin.SourceName, StringComparer.Ordinal)
            .ThenBy(summary => summary.Origin.DeclarationOffset)
            .ThenBy(
                summary => summary.Origin.DeclarationLocation?.FilePath,
                StringComparer.Ordinal)
            .ThenBy(summary => summary.Origin.DeclarationLocation?.Line)
            .ThenBy(summary => summary.Origin.DeclarationLocation?.Column);

    private bool IsFreshOwnershipResultCall(Expr expression)
    {
        if (expression is not Expr.Call)
        {
            return false;
        }

        var arguments = new List<Expr>();
        Expr root = CollectCallArgs(expression, arguments);
        return GetOwnershipSummaryForCallRoot(root) is { ResultFresh: true } summary
            && arguments.Count == summary.Parameters.Count;
    }

    private FunctionOwnershipSummary CreateOwnershipSummary(
        FuncKey function,
        string functionName,
        (List<string> Params, Expr Body) info)
    {
        (HashSet<string> unique, Dictionary<string, ParameterMoveSafetyProof> moveSafety) =
            CreateParameterMoveSafety(function, info.Params);

        var parameterOwnership = new Dictionary<string, ParameterOwnership>(StringComparer.Ordinal);
        foreach (var param in info.Params)
        {
            parameterOwnership[param] = ParamUsedOnlyAsBorrowRead(info.Body, param)
                ? ParameterOwnership.Borrowed
                : ParameterOwnership.Consumed;
        }

        var bound = new HashSet<string>(info.Params, StringComparer.Ordinal);
        var captures = FreeVars(info.Body, bound)
            .Where(name => _maValueRhs.ContainsKey(name) && !_maNameIndex.ContainsKey(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        ResultReachState resultReach = _maResultReach.TryGetValue(function, out ResultReachState reach)
            ? reach
            : ReachPoisoned(ResultReachCause.ConservativeUnknown);
        var callCensus = new FunctionCallCensus(
            _maCallSites.GetValueOrDefault(function)?.Count ?? 0,
            _maCallCensusCauses.GetValueOrDefault(function));

        var expressionFreshness = ComputeExpressionFreshness(function, info);
        ResolvedFunctionResultProvenance resolvedProvenance = ResolveFunctionResultProvenance(function);
        string? forwardName = resolvedProvenance.ForwardsTo is { } forward
            && _maKeyName.TryGetValue(forward, out string? targetName)
                ? targetName
                : null;
        var provenance = new FunctionResultProvenance(
            resolvedProvenance.RcEligible,
            forwardName,
            resolvedProvenance.BytesProvenance);
        var tcoParamFacts = ComputeTcoParamFacts(function, info, expressionFreshness);
        IReadOnlyList<PatternBindingOwnershipFact> patternBindingOwnership =
            ComputePatternBindingOwnership(function, info);

        return new FunctionOwnershipSummary(
            functionName,
            _maFunctionOrigins[function],
            info.Params.ToList(),
            parameterOwnership,
            unique,
            callCensus,
            moveSafety,
            captures,
            new FunctionResultReachFacts(
                new SortedDictionary<string, int>(
                    resultReach.Counts,
                    StringComparer.Ordinal),
                resultReach.Causes),
            expressionFreshness,
            _maFunctionsMayExecuteUnderLiveHandlerPost.Contains(function),
            provenance,
            tcoParamFacts,
            patternBindingOwnership);
    }

    private (
        HashSet<string> Unique,
        Dictionary<string, ParameterMoveSafetyProof> Proofs)
        CreateParameterMoveSafety(FuncKey function, IReadOnlyList<string> parameters)
    {
        var unique = new HashSet<string>(StringComparer.Ordinal);
        var proofs = new Dictionary<string, ParameterMoveSafetyProof>(StringComparer.Ordinal);
        foreach (string parameter in parameters)
        {
            bool isMoveSafe = IsParamMoveSafe(function, parameter);
            if (isMoveSafe)
            {
                unique.Add(parameter);
            }

            proofs[parameter] = new ParameterMoveSafetyProof(
                isMoveSafe,
                isMoveSafe
                    ? ParameterMoveSafetyCause.None
                    : ExplainParameterMoveSafetyFailure(function, parameter));
        }

        return (unique, proofs);
    }

    // Mutable accumulator threaded through TcoParamFactsWalk*/ComputeTcoParamFacts: Observed[i]
    // narrows from "unclassified" to one shape, or locks into Mixed the first time two self-call
    // sites at the same position disagree. The three orthogonal booleans are ANDed across those exact
    // edges. SawSelfCall distinguishes "no self-call found at all" (an empty result) from
    // "self-calls found, every position landed on Mixed."
    private sealed class TcoParamFactsState
    {
        public required TcoSelfCallArgumentShape?[] Observed { get; init; }
        public required bool?[] ArenaSelfContainedListRebuild { get; init; }
        public required bool?[] FreshClosureRebuild { get; init; }
        public required bool?[] BytesProvenanceSafeListRebuild { get; init; }
        public required string SelfName { get; init; }
        public bool SawSelfCall { get; set; }
    }

    /// <summary>
    /// Classifies every parameter of <paramref name="function"/> that some self-recursive call site
    /// supplies an argument for, into the <see cref="TcoSelfCallArgumentShape"/> that argument's
    /// reference-ownership shape takes plus independently aggregated arena-self-contained-list,
    /// direct-closure-rebuild, and Bytes-provenance-safe-list facts across ALL such call sites. The
    /// shape is re-derived from this
    /// same fixpoint's already-computed <paramref name="expressionFreshness"/> map. Arena
    /// self-containment uses the narrower reset-boundary predicate, while closure construction stays
    /// separate because a new closure may capture an input reference and therefore is not reference
    /// fresh. A
    /// parameter with no self-recursive call site to classify from (an ordinary non-recursive
    /// function, or a parameter never itself threaded through the self-call) gets no entry at all —
    /// absence, not <see cref="TcoSelfCallArgumentShape.Mixed"/>, is "never asked."
    /// </summary>
    private IReadOnlyList<TcoParamStructuralFacts> ComputeTcoParamFacts(
        FuncKey function,
        (List<string> Params, Expr Body) info,
        IReadOnlyDictionary<Expr, bool> expressionFreshness)
    {
        var paramNames = info.Params;
        var state = new TcoParamFactsState
        {
            Observed = new TcoSelfCallArgumentShape?[paramNames.Count],
            ArenaSelfContainedListRebuild = new bool?[paramNames.Count],
            FreshClosureRebuild = new bool?[paramNames.Count],
            BytesProvenanceSafeListRebuild = new bool?[paramNames.Count],
            SelfName = _maKeyName[function],
        };

        // Use the same body-entry scope recorded by call census. It contains this binding's own name
        // only for recursive definitions; a plain local function whose body refers to an outer
        // same-named function must not be mistaken for self-recursion.
        IReadOnlyDictionary<string, FuncKey> scope = _maFunctionScopes.GetValueOrDefault(function)
            ?? new Dictionary<string, FuncKey>(StringComparer.Ordinal);
        TcoParamFactsWalk(
            info.Body,
            new Dictionary<string, int>(StringComparer.Ordinal),
            function,
            scope,
            CreateTcoParameterScope(paramNames),
            paramNames,
            expressionFreshness,
            state);

        var result = new List<TcoParamStructuralFacts>(paramNames.Count);
        if (!state.SawSelfCall)
        {
            return result;
        }

        IReadOnlySet<int> affineSelfAppendOnly =
            ComputeAffineSelfAppendOrdinals(function, info);
        for (int i = 0; i < paramNames.Count; i++)
        {
            if (state.Observed[i] is { } shape)
            {
                result.Add(new TcoParamStructuralFacts(
                    i,
                    paramNames[i],
                    shape,
                    state.ArenaSelfContainedListRebuild[i] == true,
                    state.FreshClosureRebuild[i] == true,
                    state.BytesProvenanceSafeListRebuild[i] == true,
                    shape == TcoSelfCallArgumentShape.ConsumedTail
                        && BorrowInspectOnly(function, info, i)
                            ? TcoParamUseMode.BorrowInspectOnly
                            : TcoParamUseMode.GeneralOrUnknown,
                    affineSelfAppendOnly.Contains(i)
                        ? TcoParamReuseAffinity.SelfAppendOnly
                        : TcoParamReuseAffinity.GeneralOrUnknown));
            }
        }

        return result;
    }

    private sealed class AffineSelfAppendState
    {
        public required FuncKey Function { get; init; }
        public required string SelfName { get; init; }
        public required int ParamCount { get; init; }
        public required HashSet<int> Candidates { get; init; }
        public bool SawSelfCall { get; set; }
    }

    /// <summary>
    /// Proves which parameters are affine across every loop-continuing path: each is consumed at
    /// most once, only as the leftmost leaf of the addition chain producing its own argument to an
    /// exact lexical self-call, or passed through unchanged. Exit-path uses are unrestricted because
    /// no later loop iteration can observe a mutated reservation.
    /// </summary>
    private IReadOnlySet<int> ComputeAffineSelfAppendOrdinals(
        FuncKey function,
        (List<string> Params, Expr Body) info)
    {
        IReadOnlyDictionary<string, int> parameterScope = CreateTcoParameterScope(info.Params);
        if (!_maFunctionScopes.TryGetValue(
            function,
            out IReadOnlyDictionary<string, FuncKey>? functionScope))
        {
            return new HashSet<int>();
        }

        var state = new AffineSelfAppendState
        {
            Function = function,
            SelfName = _maKeyName[function],
            ParamCount = info.Params.Count,
            Candidates = new HashSet<int>(parameterScope.Values),
        };
        AffineSelfAppendWalk(
            info.Body,
            new HashSet<string>(StringComparer.Ordinal),
            functionScope,
            parameterScope,
            state);
        return state.SawSelfCall ? state.Candidates : new HashSet<int>();
    }

    // Returns whether the subtree contains an exact tail self-call. Uses on an exit-only path are
    // unrestricted; conditions, scrutinees, guards, and bindings are checked only when a descendant
    // continues the loop.
    private bool AffineSelfAppendWalk(
        Expr expression,
        HashSet<string> shadowed,
        IReadOnlyDictionary<string, FuncKey> functionScope,
        IReadOnlyDictionary<string, int> parameterScope,
        AffineSelfAppendState state)
    {
        switch (expression)
        {
            case Expr.If conditional:
                bool thenContinues = AffineSelfAppendWalk(
                    conditional.Then, shadowed, functionScope, parameterScope, state);
                bool elseContinues = AffineSelfAppendWalk(
                    conditional.Else, shadowed, functionScope, parameterScope, state);
                if (thenContinues || elseContinues)
                {
                    DisqualifyAffineSelfAppendMentions(
                        conditional.Cond, shadowed, parameterScope, state.Candidates);
                }

                return thenContinues || elseContinues;
            case Expr.Match match:
                return AffineSelfAppendWalkMatch(
                    match, shadowed, functionScope, parameterScope, state);
            case Expr.Let let:
                return AffineSelfAppendWalkLet(
                    let, shadowed, functionScope, parameterScope, state);
            case Expr.LetResult letResult:
                return AffineSelfAppendWalkLetResult(
                    letResult, shadowed, functionScope, parameterScope, state);
            case Expr.LetRecursive letRecursive:
                return AffineSelfAppendWalkLetRecursive(
                    letRecursive, shadowed, functionScope, parameterScope, state);
            case Expr.Call:
                return AffineSelfAppendWalkCall(
                    expression, shadowed, functionScope, parameterScope, state);
            default:
                return false;
        }
    }

    private bool AffineSelfAppendWalkMatch(
        Expr.Match match,
        HashSet<string> shadowed,
        IReadOnlyDictionary<string, FuncKey> functionScope,
        IReadOnlyDictionary<string, int> parameterScope,
        AffineSelfAppendState state)
    {
        bool anyContinues = false;
        foreach (MatchCase matchCase in match.Cases)
        {
            var binders = new HashSet<string>(StringComparer.Ordinal);
            CollectPatternBinders(matchCase.Pattern, binders);
            var armShadowed = new HashSet<string>(shadowed, StringComparer.Ordinal);
            armShadowed.UnionWith(binders);
            bool caseContinues = AffineSelfAppendWalk(
                matchCase.Body,
                armShadowed,
                RemoveFuncNames(functionScope, binders),
                RemoveTcoParameterNames(parameterScope, binders),
                state);
            if (caseContinues && matchCase.Guard is not null)
            {
                DisqualifyAffineSelfAppendMentions(
                    matchCase.Guard, armShadowed, parameterScope, state.Candidates);
            }

            anyContinues |= caseContinues;
        }

        if (anyContinues)
        {
            DisqualifyAffineSelfAppendMentions(
                match.Value, shadowed, parameterScope, state.Candidates);
        }

        return anyContinues;
    }

    private bool AffineSelfAppendWalkLet(
        Expr.Let let,
        HashSet<string> shadowed,
        IReadOnlyDictionary<string, FuncKey> functionScope,
        IReadOnlyDictionary<string, int> parameterScope,
        AffineSelfAppendState state)
    {
        HashSet<string> bodyShadowed = SetAffineSelfAppendShadow(shadowed, let.Name);
        bool bodyContinues = AffineSelfAppendWalk(
            let.Body,
            bodyShadowed,
            ExtendTcoFuncScope(functionScope, let, let.Name, let.Value),
            RemoveTcoParameterNames(parameterScope, [let.Name]),
            state);
        if (bodyContinues)
        {
            DisqualifyAffineSelfAppendMentions(
                let.Value, shadowed, parameterScope, state.Candidates);
        }

        return bodyContinues;
    }

    private bool AffineSelfAppendWalkLetResult(
        Expr.LetResult letResult,
        HashSet<string> shadowed,
        IReadOnlyDictionary<string, FuncKey> functionScope,
        IReadOnlyDictionary<string, int> parameterScope,
        AffineSelfAppendState state)
    {
        bool bodyContinues = AffineSelfAppendWalk(
            letResult.Body,
            SetAffineSelfAppendShadow(shadowed, letResult.Name),
            ExtendFuncScope(functionScope, letResult, letResult.Name),
            RemoveTcoParameterNames(parameterScope, [letResult.Name]),
            state);
        if (bodyContinues)
        {
            DisqualifyAffineSelfAppendMentions(
                letResult.Value, shadowed, parameterScope, state.Candidates);
        }

        return bodyContinues;
    }

    private bool AffineSelfAppendWalkLetRecursive(
        Expr.LetRecursive letRecursive,
        HashSet<string> shadowed,
        IReadOnlyDictionary<string, FuncKey> functionScope,
        IReadOnlyDictionary<string, int> parameterScope,
        AffineSelfAppendState state)
    {
        HashSet<string> bodyShadowed = SetAffineSelfAppendShadow(shadowed, letRecursive.Name);
        IReadOnlyDictionary<string, FuncKey> bodyFunctionScope =
            ExtendFuncScope(functionScope, letRecursive, letRecursive.Name);
        IReadOnlyDictionary<string, int> bodyParameterScope =
            RemoveTcoParameterNames(parameterScope, [letRecursive.Name]);
        bool bodyContinues = AffineSelfAppendWalk(
            letRecursive.Body,
            bodyShadowed,
            bodyFunctionScope,
            bodyParameterScope,
            state);
        if (bodyContinues)
        {
            DisqualifyAffineSelfAppendMentions(
                letRecursive.Value, bodyShadowed, bodyParameterScope, state.Candidates);
        }

        return bodyContinues;
    }

    private bool AffineSelfAppendWalkCall(
        Expr expression,
        HashSet<string> shadowed,
        IReadOnlyDictionary<string, FuncKey> functionScope,
        IReadOnlyDictionary<string, int> parameterScope,
        AffineSelfAppendState state)
    {
        var arguments = new List<Expr>();
        Expr root = CollectCallArgs(expression, arguments);
        if (root is not Expr.Var callee
            || !string.Equals(callee.Name, state.SelfName, StringComparison.Ordinal)
            || !functionScope.TryGetValue(callee.Name, out FuncKey calleeKey)
            || !calleeKey.Equals(state.Function)
            || arguments.Count != state.ParamCount)
        {
            return false;
        }

        state.SawSelfCall = true;
        for (int argumentIndex = 0; argumentIndex < arguments.Count; argumentIndex++)
        {
            foreach (int candidate in state.Candidates.ToArray())
            {
                if (argumentIndex == candidate
                    && IsAffineSelfAppendOwnArgument(
                        arguments[argumentIndex], shadowed, parameterScope, candidate))
                {
                    continue;
                }

                DisqualifyAffineSelfAppendMentions(
                    arguments[argumentIndex],
                    shadowed,
                    parameterScope,
                    state.Candidates,
                    candidate);
            }
        }

        return true;
    }

    private bool IsAffineSelfAppendOwnArgument(
        Expr argument,
        HashSet<string> shadowed,
        IReadOnlyDictionary<string, int> parameterScope,
        int candidate)
    {
        Expr chain = argument;
        while (chain is Expr.Add addition)
        {
            if (ReferencesAffineSelfAppendCandidate(
                addition.Right, shadowed, parameterScope, candidate))
            {
                return false;
            }

            chain = addition.Left;
        }

        return chain is Expr.Var variable
            && !shadowed.Contains(variable.Name)
            && parameterScope.TryGetValue(variable.Name, out int ordinal)
            && ordinal == candidate;
    }

    private void DisqualifyAffineSelfAppendMentions(
        Expr expression,
        HashSet<string> shadowed,
        IReadOnlyDictionary<string, int> parameterScope,
        HashSet<int> candidates,
        int? onlyCandidate = null)
    {
        foreach (string name in FreeVars(expression, shadowed))
        {
            if (parameterScope.TryGetValue(name, out int ordinal)
                && (onlyCandidate is null || onlyCandidate == ordinal))
            {
                candidates.Remove(ordinal);
            }
        }
    }

    private bool ReferencesAffineSelfAppendCandidate(
        Expr expression,
        HashSet<string> shadowed,
        IReadOnlyDictionary<string, int> parameterScope,
        int candidate)
    {
        foreach (string name in FreeVars(expression, shadowed))
        {
            if (parameterScope.TryGetValue(name, out int ordinal) && ordinal == candidate)
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<string> SetAffineSelfAppendShadow(
        HashSet<string> shadowed,
        string name)
        => new(shadowed, StringComparer.Ordinal) { name };

    private enum BorrowInspectTaint
    {
        Tail,
        Head,
    }

    private readonly record struct BorrowInspectContext(
        FuncKey SelfFunction,
        string SelfName,
        int ParamCount,
        int ParamIndex);

    /// <summary>
    /// Proves that one consumed-tail parameter and every head/tail reference structurally derived
    /// from it are used only for inspection or transferred to the same parameter of an exact
    /// lexical self-call. The live taint environment is lexical: crossing a let or pattern binder
    /// replaces the source name's prior meaning, so same-named bindings cannot inherit ownership
    /// from an unrelated parameter or extracted value.
    /// </summary>
    private bool BorrowInspectOnly(
        FuncKey function,
        (List<string> Params, Expr Body) info,
        int paramIndex)
    {
        IReadOnlyDictionary<string, int> parameterScope = CreateTcoParameterScope(info.Params);
        string paramName = info.Params[paramIndex];
        if (!parameterScope.TryGetValue(paramName, out int visibleOrdinal)
            || visibleOrdinal != paramIndex
            || !_maFunctionScopes.TryGetValue(
                function,
                out IReadOnlyDictionary<string, FuncKey>? functionScope))
        {
            return false;
        }

        Dictionary<string, BorrowInspectTaint> taints = new(StringComparer.Ordinal)
        {
            [paramName] = BorrowInspectTaint.Tail,
        };
        BorrowInspectContext context = new(
            function,
            _maKeyName[function],
            info.Params.Count,
            paramIndex);
        return BorrowInspectExpression(info.Body, taints, context, functionScope);
    }

    // Approved uses are a tainted head/tail as a match scrutinee and a tainted tail at the
    // candidate's own position in an exact self-call. Every other bare reference escapes. Unknown
    // expression shapes fail closed, retaining the existing normalization path.
    private bool BorrowInspectExpression(
        Expr expression,
        IReadOnlyDictionary<string, BorrowInspectTaint> taints,
        BorrowInspectContext context,
        IReadOnlyDictionary<string, FuncKey> functionScope)
    {
        switch (expression)
        {
            case Expr.Var variable:
                return !taints.ContainsKey(variable.Name);
            case Expr.QualifiedVar:
            case Expr.IntLit:
            case Expr.BigIntLit:
            case Expr.UIntLit:
            case Expr.FloatLit:
            case Expr.StrLit:
            case Expr.BoolLit:
                return true;
            case Expr.If conditional:
                return BorrowInspectAll(
                    [conditional.Cond, conditional.Then, conditional.Else],
                    taints,
                    context,
                    functionScope);
            case Expr.Let let:
                return BorrowInspectLet(let, taints, context, functionScope);
            case Expr.LetResult letResult:
                return BorrowInspectExpression(letResult.Value, taints, context, functionScope)
                    && BorrowInspectExpression(
                        letResult.Body,
                        RemoveBorrowInspectTaints(taints, [letResult.Name]),
                        context,
                        ExtendFuncScope(functionScope, letResult, letResult.Name));
            case Expr.LetRecursive letRecursive:
                IReadOnlyDictionary<string, FuncKey> recursiveScope =
                    ExtendFuncScope(functionScope, letRecursive, letRecursive.Name);
                IReadOnlyDictionary<string, BorrowInspectTaint> recursiveTaints =
                    RemoveBorrowInspectTaints(taints, [letRecursive.Name]);
                return BorrowInspectExpression(
                        letRecursive.Value,
                        recursiveTaints,
                        context,
                        recursiveScope)
                    && BorrowInspectExpression(
                        letRecursive.Body,
                        recursiveTaints,
                        context,
                        recursiveScope);
            case Expr.Match match:
                return BorrowInspectMatch(match, taints, context, functionScope);
            case Expr.Call:
                return BorrowInspectCall(expression, taints, context, functionScope);
            default:
                return BorrowInspectCompositeExpression(
                    expression,
                    taints,
                    context,
                    functionScope);
        }
    }

    private bool BorrowInspectCompositeExpression(
        Expr expression,
        IReadOnlyDictionary<string, BorrowInspectTaint> taints,
        BorrowInspectContext context,
        IReadOnlyDictionary<string, FuncKey> functionScope)
    {
        IReadOnlyList<Expr>? operands = expression switch
        {
            Expr.Add binary => [binary.Left, binary.Right],
            Expr.Subtract binary => [binary.Left, binary.Right],
            Expr.Multiply binary => [binary.Left, binary.Right],
            Expr.Divide binary => [binary.Left, binary.Right],
            Expr.Modulo binary => [binary.Left, binary.Right],
            Expr.Equal binary => [binary.Left, binary.Right],
            Expr.NotEqual binary => [binary.Left, binary.Right],
            Expr.GreaterThan binary => [binary.Left, binary.Right],
            Expr.LessThan binary => [binary.Left, binary.Right],
            Expr.GreaterOrEqual binary => [binary.Left, binary.Right],
            Expr.LessOrEqual binary => [binary.Left, binary.Right],
            Expr.TupleLit tuple => tuple.Elements,
            _ => null,
        };
        return operands is not null
            && BorrowInspectAll(operands, taints, context, functionScope);
    }

    private bool BorrowInspectAll(
        IReadOnlyList<Expr> expressions,
        IReadOnlyDictionary<string, BorrowInspectTaint> taints,
        BorrowInspectContext context,
        IReadOnlyDictionary<string, FuncKey> functionScope)
    {
        foreach (Expr expression in expressions)
        {
            if (!BorrowInspectExpression(expression, taints, context, functionScope))
            {
                return false;
            }
        }

        return true;
    }

    private bool BorrowInspectLet(
        Expr.Let let,
        IReadOnlyDictionary<string, BorrowInspectTaint> taints,
        BorrowInspectContext context,
        IReadOnlyDictionary<string, FuncKey> functionScope)
    {
        IReadOnlyDictionary<string, FuncKey> bodyFunctionScope =
            ExtendTcoFuncScope(functionScope, let, let.Name, let.Value);

        // A bare reference carries the same owner into the new lexical binding. Any other value
        // must be independently clean, and the new binder shadows an older same-named taint.
        if (let.Value is Expr.Var alias
            && taints.TryGetValue(alias.Name, out BorrowInspectTaint aliasTaint))
        {
            return BorrowInspectExpression(
                let.Body,
                SetBorrowInspectTaint(taints, let.Name, aliasTaint),
                context,
                bodyFunctionScope);
        }

        return BorrowInspectExpression(let.Value, taints, context, functionScope)
            && BorrowInspectExpression(
                let.Body,
                RemoveBorrowInspectTaints(taints, [let.Name]),
                context,
                bodyFunctionScope);
    }

    private bool BorrowInspectMatch(
        Expr.Match match,
        IReadOnlyDictionary<string, BorrowInspectTaint> taints,
        BorrowInspectContext context,
        IReadOnlyDictionary<string, FuncKey> functionScope)
    {
        BorrowInspectTaint? scrutineeTaint = match.Value is Expr.Var scrutinee
            && taints.TryGetValue(scrutinee.Name, out BorrowInspectTaint found)
                ? found
                : null;
        if (scrutineeTaint is null
            && !BorrowInspectExpression(match.Value, taints, context, functionScope))
        {
            return false;
        }

        foreach (MatchCase matchCase in match.Cases)
        {
            HashSet<string> binders = new(StringComparer.Ordinal);
            CollectPatternBinders(matchCase.Pattern, binders);
            IReadOnlyDictionary<string, FuncKey> armFunctionScope =
                RemoveFuncNames(functionScope, binders);
            IReadOnlyDictionary<string, BorrowInspectTaint> armTaints =
                RemoveBorrowInspectTaints(taints, binders);
            if (scrutineeTaint is { } tracked
                && !TryBindBorrowInspectPattern(
                    matchCase.Pattern,
                    tracked,
                    armTaints,
                    out armTaints))
            {
                return false;
            }

            if (matchCase.Guard is { } guard
                && !BorrowInspectExpression(guard, armTaints, context, armFunctionScope))
            {
                return false;
            }

            if (!BorrowInspectExpression(
                matchCase.Body,
                armTaints,
                context,
                armFunctionScope))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryBindBorrowInspectPattern(
        Pattern pattern,
        BorrowInspectTaint scrutineeTaint,
        IReadOnlyDictionary<string, BorrowInspectTaint> taints,
        out IReadOnlyDictionary<string, BorrowInspectTaint> boundTaints)
    {
        boundTaints = taints;
        if (scrutineeTaint == BorrowInspectTaint.Head)
        {
            switch (pattern)
            {
                case Pattern.Wildcard:
                case Pattern.Constructor:
                    return true;
                case Pattern.Var alias:
                    boundTaints = SetBorrowInspectTaint(
                        taints,
                        alias.Name,
                        BorrowInspectTaint.Head);
                    return true;
                default:
                    return false;
            }
        }

        switch (pattern)
        {
            case Pattern.EmptyList:
            case Pattern.Wildcard:
                return true;
            case Pattern.Var whole:
                boundTaints = SetBorrowInspectTaint(
                    taints,
                    whole.Name,
                    BorrowInspectTaint.Tail);
                return true;
            case Pattern.Cons cons:
                if (cons.Head is Pattern.Var head)
                {
                    boundTaints = SetBorrowInspectTaint(
                        boundTaints,
                        head.Name,
                        BorrowInspectTaint.Head);
                }
                else if (cons.Head is not Pattern.Wildcard)
                {
                    return false;
                }

                if (cons.Tail is Pattern.Var tail)
                {
                    boundTaints = SetBorrowInspectTaint(
                        boundTaints,
                        tail.Name,
                        BorrowInspectTaint.Tail);
                }
                else if (cons.Tail is not Pattern.Wildcard)
                {
                    return false;
                }

                return true;
            default:
                return false;
        }
    }

    private bool BorrowInspectCall(
        Expr expression,
        IReadOnlyDictionary<string, BorrowInspectTaint> taints,
        BorrowInspectContext context,
        IReadOnlyDictionary<string, FuncKey> functionScope)
    {
        List<Expr> arguments = [];
        Expr root = CollectCallArgs(expression, arguments);
        bool isSelfCall = root is Expr.Var function
            && string.Equals(function.Name, context.SelfName, StringComparison.Ordinal)
            && functionScope.TryGetValue(function.Name, out FuncKey callee)
            && callee.Equals(context.SelfFunction)
            && arguments.Count == context.ParamCount;
        for (int i = 0; i < arguments.Count; i++)
        {
            if (isSelfCall
                && i == context.ParamIndex
                && arguments[i] is Expr.Var tailArgument
                && taints.GetValueOrDefault(tailArgument.Name) == BorrowInspectTaint.Tail)
            {
                continue;
            }

            if (!BorrowInspectExpression(arguments[i], taints, context, functionScope))
            {
                return false;
            }
        }

        return root is Expr.Var or Expr.QualifiedVar
            || BorrowInspectExpression(root, taints, context, functionScope);
    }

    private static IReadOnlyDictionary<string, BorrowInspectTaint> SetBorrowInspectTaint(
        IReadOnlyDictionary<string, BorrowInspectTaint> taints,
        string name,
        BorrowInspectTaint taint)
    {
        Dictionary<string, BorrowInspectTaint> next = new(taints, StringComparer.Ordinal)
        {
            [name] = taint,
        };
        return next;
    }

    private static IReadOnlyDictionary<string, BorrowInspectTaint> RemoveBorrowInspectTaints(
        IReadOnlyDictionary<string, BorrowInspectTaint> taints,
        IEnumerable<string> names)
    {
        Dictionary<string, BorrowInspectTaint> next = new(taints, StringComparer.Ordinal);
        foreach (string name in names)
        {
            next.Remove(name);
        }

        return next;
    }

    // Walks the tail spine (If arms, Match case bodies, Let bodies), threading a tail-owner map
    // (extracted binding name -> the parameter it was pattern-matched out of, one Cons level deep)
    // so a self-call argument that is just that extracted tail can be recognized as ConsumedTail.
    // Also threads a live, lexically-
    // extended scope (name -> the FuncKey it currently denotes), copy-on-write extended at every
    // Let/LetRecursive/LetResult crossed exactly the way ExtendEnv extends ResultReach's own env: a
    // locally nested binding of the same name as one already in scope shadows it for the rest of this
    // node's body, resolving to the newly-crossed binder's own FuncKey when that binder itself
    // registered as a function, to an existing FuncKey for an immutable plain-let function alias,
    // or to no function at all (removed from scope) when it did not —
    // matching a bare _maFuncs.ContainsKey check's own verdict for that exact occurrence, not a
    // whole-program name lookup. parameterScope separately resolves live value names to their
    // original parameter ordinal. Crossing a let or pattern binder removes a same-named parameter,
    // so source spelling alone can never classify a rebound value as the original parameter.
    private void TcoParamFactsWalk(
        Expr expression,
        IReadOnlyDictionary<string, int> tailOwners,
        FuncKey function,
        IReadOnlyDictionary<string, FuncKey> scope,
        IReadOnlyDictionary<string, int> parameterScope,
        IReadOnlyList<string> paramNames,
        IReadOnlyDictionary<Expr, bool> expressionFreshness,
        TcoParamFactsState state)
    {
        switch (expression)
        {
            case Expr.If iff:
                TcoParamFactsWalk(
                    iff.Then, tailOwners, function, scope, parameterScope, paramNames, expressionFreshness, state);
                TcoParamFactsWalk(
                    iff.Else, tailOwners, function, scope, parameterScope, paramNames, expressionFreshness, state);
                return;
            case Expr.Match match:
                TcoParamFactsWalkMatch(
                    match, tailOwners, function, scope, parameterScope, paramNames, expressionFreshness, state);
                return;
            case Expr.Let let:
                TcoParamFactsWalk(
                    let.Body,
                    RemoveTailOwnerNames(tailOwners, [let.Name]),
                    function,
                    ExtendTcoFuncScope(scope, let, let.Name, let.Value),
                    RemoveTcoParameterNames(parameterScope, [let.Name]),
                    paramNames,
                    expressionFreshness,
                    state);
                return;
            case Expr.LetResult letResult:
                TcoParamFactsWalk(
                    letResult.Body,
                    RemoveTailOwnerNames(tailOwners, [letResult.Name]),
                    function,
                    ExtendFuncScope(scope, letResult, letResult.Name),
                    RemoveTcoParameterNames(parameterScope, [letResult.Name]),
                    paramNames,
                    expressionFreshness, state);
                return;
            case Expr.LetRecursive letRecursive:
                TcoParamFactsWalk(
                    letRecursive.Body,
                    RemoveTailOwnerNames(tailOwners, [letRecursive.Name]),
                    function,
                    ExtendFuncScope(scope, letRecursive, letRecursive.Name),
                    RemoveTcoParameterNames(parameterScope, [letRecursive.Name]),
                    paramNames,
                    expressionFreshness, state);
                return;
            case Expr.Call:
                TcoParamFactsWalkCall(
                    expression,
                    tailOwners,
                    function,
                    scope,
                    parameterScope,
                    paramNames,
                    expressionFreshness,
                    state);
                return;
        }
    }

    private void TcoParamFactsWalkMatch(
        Expr.Match match,
        IReadOnlyDictionary<string, int> tailOwners,
        FuncKey function,
        IReadOnlyDictionary<string, FuncKey> scope,
        IReadOnlyDictionary<string, int> parameterScope,
        IReadOnlyList<string> paramNames,
        IReadOnlyDictionary<Expr, bool> expressionFreshness,
        TcoParamFactsState state)
    {
        foreach (MatchCase matchCase in match.Cases)
        {
            var binders = new HashSet<string>(StringComparer.Ordinal);
            CollectPatternBinders(matchCase.Pattern, binders);
            var armTailOwners = new Dictionary<string, int>(tailOwners, StringComparer.Ordinal);
            foreach (string binder in binders)
            {
                armTailOwners.Remove(binder);
            }

            if (match.Value is Expr.Var source
                && parameterScope.TryGetValue(source.Name, out int sourceParameterOrdinal)
                && matchCase.Pattern is Pattern.Cons { Tail: Pattern.Var tail })
            {
                armTailOwners[tail.Name] = sourceParameterOrdinal;
            }

            TcoParamFactsWalk(
                matchCase.Body,
                armTailOwners,
                function,
                RemoveFuncNames(scope, binders),
                RemoveTcoParameterNames(parameterScope, binders),
                paramNames,
                expressionFreshness,
                state);
        }
    }

    private static IReadOnlyDictionary<string, int> CreateTcoParameterScope(
        IReadOnlyList<string> paramNames)
    {
        var scope = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < paramNames.Count; i++)
        {
            // Curried lambdas are nested from left to right, so a later same-named parameter is the
            // live binding in the innermost body.
            scope[paramNames[i]] = i;
        }

        return scope;
    }

    private static IReadOnlyDictionary<string, int> RemoveTcoParameterNames(
        IReadOnlyDictionary<string, int> parameterScope,
        IEnumerable<string> names)
    {
        var next = new Dictionary<string, int>(parameterScope, StringComparer.Ordinal);
        foreach (string name in names)
        {
            next.Remove(name);
        }

        return next;
    }

    private static IReadOnlyDictionary<string, int> RemoveTailOwnerNames(
        IReadOnlyDictionary<string, int> tailOwners,
        IEnumerable<string> names)
    {
        var next = new Dictionary<string, int>(tailOwners, StringComparer.Ordinal);
        foreach (string name in names)
        {
            next.Remove(name);
        }

        return next;
    }

    // A nested binder shadows `name` for everything in its own body. Original and
    // StripModuleAliasPrefix-rebuilt binders both resolve through their canonical reference identity;
    // a non-function binder removes the name.
    private IReadOnlyDictionary<string, FuncKey> ExtendFuncScope(
        IReadOnlyDictionary<string, FuncKey> scope, Expr binder, string name)
    {
        var next = new Dictionary<string, FuncKey>(scope, StringComparer.Ordinal);
        if (TryResolveBindingFunctionKey(binder, out FuncKey key))
        {
            next[name] = key;
        }
        else
        {
            next.Remove(name);
        }

        return next;
    }

    // Plain lets may bind an immutable alias of an exact function already present in scope. Live
    // TCO preserves that identity through the local slot's known function label, so structural TCO
    // analyses must preserve the same FuncKey or they can omit a live back edge. Other let-like
    // forms retain ExtendFuncScope's binder-identity semantics.
    private IReadOnlyDictionary<string, FuncKey> ExtendTcoFuncScope(
        IReadOnlyDictionary<string, FuncKey> scope,
        Expr binder,
        string name,
        Expr value)
    {
        if (value is Expr.Var alias && scope.TryGetValue(alias.Name, out FuncKey aliasedFunction))
        {
            return SetFuncName(scope, name, aliasedFunction);
        }

        return ExtendFuncScope(scope, binder, name);
    }

    // The self-call site itself: classifies every argument position's local shape and narrows (or
    // locks to Mixed on disagreement) the running per-position verdict in state.Observed.
    private void TcoParamFactsWalkCall(
        Expr expression,
        IReadOnlyDictionary<string, int> tailOwners,
        FuncKey function,
        IReadOnlyDictionary<string, FuncKey> scope,
        IReadOnlyDictionary<string, int> parameterScope,
        IReadOnlyList<string> paramNames,
        IReadOnlyDictionary<Expr, bool> expressionFreshness,
        TcoParamFactsState state)
    {
        var arguments = new List<Expr>();
        Expr root = CollectCallArgs(expression, arguments);
        if (root is not Expr.Var callee
            || !string.Equals(callee.Name, state.SelfName, StringComparison.Ordinal)
            || !scope.TryGetValue(callee.Name, out var calleeKey)
            || !calleeKey.Equals(function)
            || arguments.Count != paramNames.Count)
        {
            return;
        }

        state.SawSelfCall = true;
        for (int i = 0; i < paramNames.Count; i++)
        {
            Expr argument = arguments[i];
            bool arenaSelfContainedListRebuild =
                IsArenaSelfContainedListRebuildExpr(argument);
            bool freshClosureRebuild = IsFreshClosureRebuildExpr(argument);
            bool bytesProvenanceSafeListRebuild =
                IsBytesProvenanceSafeListRebuildExpr(argument);
            TcoSelfCallArgumentShape local =
                argument is Expr.Var argVar
                    && parameterScope.TryGetValue(argVar.Name, out int parameterOrdinal)
                    && parameterOrdinal == i
                    ? TcoSelfCallArgumentShape.UnchangedPassthrough
                : argument is Expr.Var tailVar
                    && tailOwners.TryGetValue(tailVar.Name, out int ownerOrdinal)
                    && ownerOrdinal == i
                    ? TcoSelfCallArgumentShape.ConsumedTail
                : argument is Expr.Cons { Tail: Expr.Var consTail }
                    && parameterScope.TryGetValue(consTail.Name, out int tailParameterOrdinal)
                    && tailParameterOrdinal == i
                    ? TcoSelfCallArgumentShape.GrownCons
                : expressionFreshness.TryGetValue(argument, out bool fresh) && fresh
                    ? TcoSelfCallArgumentShape.FreshRebuilt
                : TcoSelfCallArgumentShape.Mixed;

            state.Observed[i] = state.Observed[i] is { } already && already != local
                ? TcoSelfCallArgumentShape.Mixed
                : state.Observed[i] ?? local;
            state.ArenaSelfContainedListRebuild[i] =
                (state.ArenaSelfContainedListRebuild[i] ?? true)
                && arenaSelfContainedListRebuild;
            state.FreshClosureRebuild[i] =
                (state.FreshClosureRebuild[i] ?? true)
                && freshClosureRebuild;
            state.BytesProvenanceSafeListRebuild[i] =
                (state.BytesProvenanceSafeListRebuild[i] ?? true)
                && bytesProvenanceSafeListRebuild;
        }
    }

    private bool IsBytesProvenanceSafeListRebuildExpr(Expr expression)
    {
        return expression switch
        {
            Expr.Cons cons => IsBytesProvenanceSafeAggregateValue(cons.Head),
            Expr.ListLit list => list.Elements.All(IsBytesProvenanceSafeAggregateValue),
            Expr.If conditional => IsBytesProvenanceSafeListRebuildExpr(conditional.Then)
                && IsBytesProvenanceSafeListRebuildExpr(conditional.Else),
            _ => false,
        };
    }

    private bool IsBytesProvenanceSafeAggregateValue(Expr expression)
    {
        if (TryGetBuiltinBytesProvenance(expression, out var provenance))
        {
            return provenance is BuiltinRegistry.BytesOwnershipProvenance.FreshOwnedBuffer
                or BuiltinRegistry.BytesOwnershipProvenance.BorrowedView;
        }

        if (expression is Expr.TupleLit tuple)
        {
            return tuple.Elements.All(IsBytesProvenanceSafeAggregateValue);
        }

        if (expression is Expr.ListLit list)
        {
            return list.Elements.All(IsBytesProvenanceSafeAggregateValue);
        }

        if (expression is Expr.Cons cons)
        {
            return IsBytesProvenanceSafeAggregateValue(cons.Head)
                && IsBytesProvenanceSafeAggregateValue(cons.Tail);
        }

        if (expression is Expr.Call)
        {
            var arguments = new List<Expr>();
            Expr head = CollectCallArgs(expression, arguments);
            return head is Expr.Var constructor
                && _constructorSymbols.ContainsKey(constructor.Name)
                && arguments.All(IsBytesProvenanceSafeAggregateValue);
        }

        return expression is Expr.IntLit or Expr.UIntLit or Expr.FloatLit or Expr.BoolLit
            or Expr.BigIntLit or Expr.StrLit or Expr.Add;
    }

    private static bool IsFreshClosureRebuildExpr(Expr expression)
        => expression is Expr.Lambda
            || expression is Expr.If conditional
                && IsFreshClosureRebuildExpr(conditional.Then)
                && IsFreshClosureRebuildExpr(conditional.Else);

    /// <summary>
    /// Formats stable, single-line ownership summaries. <paramref name="selection"/> may be a
    /// comma-separated function list; null, empty, <c>1</c>, and <c>all</c> select every function.
    /// </summary>
    internal IReadOnlyList<string> FormatOwnershipSummaries(string? selection = null)
    {
        HashSet<string>? selected = ParseOwnershipExplainSelection(selection);
        var lines = new List<string>();
        foreach (var function in AnalyzedFunctionNames)
        {
            if (selected is not null && !selected.Contains(function))
            {
                continue;
            }

            if (GetOwnershipSummary(function) is not { } summary)
            {
                continue;
            }

            string parameters = string.Join(", ", summary.Parameters.Select(parameter =>
                $"{parameter}:{summary.ParameterOwnership[parameter].ToString().ToLowerInvariant()}"));
            string unique = string.Join(",", summary.UniqueParameters.OrderBy(name => name, StringComparer.Ordinal));
            string captures = string.Join(",", summary.CapturedValues);
            string result = summary.ResultPoisoned
                ? "poisoned"
                : summary.ResultFresh
                    ? "fresh"
                    : $"reaches{{{string.Join(",", summary.ResultReach.Keys.OrderBy(name => name, StringComparer.Ordinal))}}}";
            int freshExpressions = summary.ExpressionFreshness.Values.Count(fresh => fresh);
            string provenance = $"rc-eligible:{summary.ResultProvenance.RcEligible.ToString().ToLowerInvariant()} "
                + $"forwards-to:{summary.ResultProvenance.ForwardsTo ?? "none"} "
                + $"bytes:{summary.ResultProvenance.BytesProvenance.ToString().ToLowerInvariant()}";
            lines.Add(
                $"[ownership] {summary.Function}({parameters}) unique={{{unique}}} captures={{{captures}}} "
                    + $"result={result} expr-fresh={freshExpressions}/{summary.ExpressionFreshness.Count} "
                    + $"handler-post={summary.MayExecuteUnderLiveHandlerPost.ToString().ToLowerInvariant()} "
                    + $"provenance={{{provenance}}}");
        }

        return lines;
    }

    private static HashSet<string>? ParseOwnershipExplainSelection(string? selection)
    {
        if (string.IsNullOrWhiteSpace(selection)
            || string.Equals(selection, "1", StringComparison.Ordinal)
            || string.Equals(selection, "all", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new HashSet<string>(
            selection.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Greatest-fixpoint node: the value bound to parameter <paramref name="param"/> of top-level
    /// function <paramref name="func"/> is uniquely owned at every invocation. Computed on demand
    /// with cycle-breaking (a cycle resolves to false — the sound under-approximation).
    /// </summary>
    private bool IsParamMoveSafe(FuncKey func, string param)
    {
        var key = (func, param);
        if (_maMoveSafeMemo.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (!_maInProgress.Add(key))
        {
            _maMoveSafetyCycles.Add(key);
            return false; // cycle — not proven this pass
        }

        bool result = ComputeParamMoveSafe(func, param);
        _maInProgress.Remove(key);
        _maMoveSafeMemo[key] = result;
        return result;
    }

    private bool ComputeParamMoveSafe(FuncKey func, string param)
    {
        // The function must be fully visible (never escapes as a value) and have a known parameter
        // list, otherwise its call sites are not provably complete.
        if (!_maFuncs.TryGetValue(func, out var info)
            || _maEscaped.Contains(func)
            || _maCallCensusCauses.GetValueOrDefault(func)
                != FunctionCallCensusCause.None)
        {
            return false;
        }

        int paramIndex = info.Params.IndexOf(param);
        if (paramIndex < 0)
        {
            return false;
        }

        if (!_maCallSites.TryGetValue(func, out var sites))
        {
            return false; // no observed call site — never proven (dead or hidden)
        }

        bool sawExternal = false;
        foreach (MoveCallSite site in sites)
        {
            if (site.Enclosing is { } enclosingKey && enclosingKey.Equals(func))
            {
                continue; // self-recursion is the TCO back-edge, not an external entry
            }

            if (paramIndex >= site.Args.Count)
            {
                return false; // under-applied at this site — cannot map the argument
            }

            sawExternal = true;
            if (!ArgIsMove(site.Args[paramIndex], site.Enclosing, site.ArgumentScopes[paramIndex]))
            {
                return false;
            }
        }

        return sawExternal;
    }

    private ParameterMoveSafetyCause ExplainParameterMoveSafetyFailure(
        FuncKey func,
        string param)
    {
        if (!_maFuncs.TryGetValue(func, out var info))
        {
            return ParameterMoveSafetyCause.ConservativeUnknown;
        }

        ParameterMoveSafetyCause causes = ParameterMoveSafetyCause.None;
        FunctionCallCensusCause censusCauses = _maCallCensusCauses.GetValueOrDefault(func);
        if (_maEscaped.Contains(func))
        {
            causes |= ParameterMoveSafetyCause.FunctionEscaped;
        }

        if (censusCauses != FunctionCallCensusCause.None)
        {
            causes |= ParameterMoveSafetyCause.IncompleteCallCensus;
        }

        if ((censusCauses & FunctionCallCensusCause.AmbiguousResolution)
            != FunctionCallCensusCause.None)
        {
            causes |= ParameterMoveSafetyCause.AmbiguousResolution;
        }

        if (!info.Params.Contains(param))
        {
            return causes | ParameterMoveSafetyCause.ConservativeUnknown;
        }

        if (!_maCallSites.TryGetValue(func, out var sites))
        {
            return causes | ParameterMoveSafetyCause.NoDirectCallSites;
        }

        causes |= ExplainMoveCallSites(func, info.Params.IndexOf(param), sites);

        if (_maMoveSafetyCycles.Contains((func, param)))
        {
            causes |= ParameterMoveSafetyCause.ProofCycle;
        }

        return causes == ParameterMoveSafetyCause.None
            ? ParameterMoveSafetyCause.ConservativeUnknown
            : causes;
    }

    private ParameterMoveSafetyCause ExplainMoveCallSites(
        FuncKey function,
        int parameterIndex,
        IReadOnlyList<MoveCallSite> sites)
    {
        ParameterMoveSafetyCause causes = ParameterMoveSafetyCause.None;
        bool sawExternal = false;
        foreach (MoveCallSite site in sites)
        {
            if (site.Enclosing is { } enclosingKey && enclosingKey.Equals(function))
            {
                continue;
            }

            sawExternal = true;
            if (parameterIndex >= site.Args.Count)
            {
                causes |= ParameterMoveSafetyCause.CallArityMismatch
                    | ParameterMoveSafetyCause.IncompleteCallCensus;
            }
            else if (!ArgIsMove(
                site.Args[parameterIndex],
                site.Enclosing,
                site.ArgumentScopes[parameterIndex]))
            {
                causes |= ExplainMoveArgumentFailure(
                    site.Args[parameterIndex],
                    site.Enclosing,
                    site.ArgumentScopes[parameterIndex]);
            }
        }

        return sawExternal
            ? causes
            : causes | ParameterMoveSafetyCause.NoExternalCallSites;
    }

    private ParameterMoveSafetyCause ExplainMoveArgumentFailure(
        Expr argument,
        FuncKey? enclosing,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        if (argument is Expr.Call)
        {
            return ParameterMoveSafetyCause.ResultAliasUnsafe;
        }

        if (argument is not Expr.Var variable)
        {
            return ParameterMoveSafetyCause.SeedNotSafe;
        }

        if (_constructorSymbols.ContainsKey(variable.Name))
        {
            return ParameterMoveSafetyCause.SeedNotSafe;
        }

        if (enclosing is { } enclosingKey
            && _maFuncs.TryGetValue(enclosingKey, out var enclosingInfo)
            && enclosingInfo.Params.Contains(variable.Name))
        {
            int occurrences = MaxPathOccurrences(variable.Name, enclosingInfo.Body);
            if (occurrences > 1)
            {
                return IsCapturedByNestedLambda(variable.Name, enclosingInfo.Body)
                    ? ParameterMoveSafetyCause.CapturedByClosure
                    : ParameterMoveSafetyCause.MoveLinearity;
            }

            if (!IsParamMoveSafe(enclosingKey, variable.Name))
            {
                return ParameterMoveSafetyCause.TransitiveParameterUnsafe;
            }

            return ParameterMoveSafetyCause.ConservativeUnknown;
        }

        return ExplainLocalMoveArgumentFailure(variable, enclosing, scope);
    }

    private ParameterMoveSafetyCause ExplainLocalMoveArgumentFailure(
        Expr.Var variable,
        FuncKey? enclosing,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        ParameterMoveSafetyCause causes = ParameterMoveSafetyCause.None;
        Expr? enclosingBody = enclosing is null
            ? _maBody
            : _maFuncs.GetValueOrDefault(enclosing.Value).Body;
        if (_maAmbiguous.Contains(variable.Name))
        {
            causes |= ParameterMoveSafetyCause.AmbiguousResolution;
        }

        if (enclosingBody is not null
            && TryFindLocalLet(variable.Name, enclosingBody) is var (boundRhs, boundScope)
            && boundRhs is not null
            && boundScope is not null)
        {
            int occurrences = MaxPathOccurrences(variable.Name, boundScope);
            if (occurrences > 1)
            {
                causes |= IsCapturedByNestedLambda(variable.Name, boundScope)
                    ? ParameterMoveSafetyCause.CapturedByClosure
                    : ParameterMoveSafetyCause.MoveLinearity;
            }

            if (!IsFullyFreshConstruction(boundRhs)
                && !IsResultAliasMove(boundRhs, enclosing, scope))
            {
                causes |= ParameterMoveSafetyCause.ResultAliasUnsafe;
            }

            return causes;
        }

        return causes | ParameterMoveSafetyCause.ConservativeUnknown;
    }

    private bool IsCapturedByNestedLambda(string name, Expr expression)
    {
        switch (expression)
        {
            case Expr.Lambda lambda:
                if (string.Equals(lambda.ParamName, name, StringComparison.Ordinal))
                {
                    return false;
                }

                return FreeVars(
                    lambda.Body,
                    new HashSet<string>(StringComparer.Ordinal) { lambda.ParamName })
                    .Contains(name);
            case Expr.Let let:
                return IsCapturedByNestedLambda(name, let.Value)
                    || !string.Equals(let.Name, name, StringComparison.Ordinal)
                    && IsCapturedByNestedLambda(name, let.Body);
            case Expr.LetResult letResult:
                return IsCapturedByNestedLambda(name, letResult.Value)
                    || !string.Equals(letResult.Name, name, StringComparison.Ordinal)
                    && IsCapturedByNestedLambda(name, letResult.Body);
            case Expr.LetRecursive recursive:
                return !string.Equals(recursive.Name, name, StringComparison.Ordinal)
                    && (IsCapturedByNestedLambda(name, recursive.Value)
                        || IsCapturedByNestedLambda(name, recursive.Body));
            case Expr.If conditional:
                return IsCapturedByNestedLambda(name, conditional.Cond)
                    || IsCapturedByNestedLambda(name, conditional.Then)
                    || IsCapturedByNestedLambda(name, conditional.Else);
            case Expr.Call call:
                return IsCapturedByNestedLambda(name, call.Func)
                    || IsCapturedByNestedLambda(name, call.Arg);
            case Expr.Match match:
                return IsCapturedByNestedLambdaInMatch(name, match);
            case Expr.Perform perform:
                return IsCapturedByNestedLambda(name, perform.Operation);
            case Expr.Handle handle:
                return IsCapturedByNestedLambdaInHandle(name, handle);
            case RecursiveGroupExpr group:
                return IsCapturedByNestedLambdaInRecursiveGroup(name, group);
            case CapabilityPostExpr post:
                return IsCapturedByNestedLambda(name, post.Value)
                    || IsCapturedByNestedLambda(name, post.PostLambda);
        }

        foreach (Expr child in EnumerateChildren(expression))
        {
            if (IsCapturedByNestedLambda(name, child))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsCapturedByNestedLambdaInHandle(string name, Expr.Handle handle)
    {
        if (IsCapturedByNestedLambda(name, handle.Body))
        {
            return true;
        }

        foreach (HandlerArm arm in handle.Arms)
        {
            if (arm.Parameters.Any(pattern => PatternBinds(pattern, name)))
            {
                continue;
            }

            if (IsCapturedByNestedLambda(name, arm.Body))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsCapturedByNestedLambdaInRecursiveGroup(
        string name,
        RecursiveGroupExpr group)
    {
        if (group.Bindings.Any(binding =>
            string.Equals(binding.Name, name, StringComparison.Ordinal)))
        {
            return false;
        }

        return group.Bindings.Any(binding =>
                IsCapturedByNestedLambda(name, binding.Value))
            || IsCapturedByNestedLambda(name, group.Body);
    }

    private bool IsCapturedByNestedLambdaInMatch(string name, Expr.Match match)
    {
        if (IsCapturedByNestedLambda(name, match.Value))
        {
            return true;
        }

        foreach (MatchCase matchCase in match.Cases)
        {
            if (PatternBinds(matchCase.Pattern, name))
            {
                continue;
            }

            if (IsCapturedByNestedLambda(name, matchCase.Body)
                || matchCase.Guard is not null
                && IsCapturedByNestedLambda(name, matchCase.Guard))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when argument <paramref name="arg"/>, passed from function <paramref name="enclosing"/>
    /// (null at top level), denotes a uniquely-owned value that is moved (not retained) here: either a
    /// safe nullary seed, or a move-linear reference to a move-safe accumulator parameter of the
    /// enclosing function.
    /// </summary>
    private bool ArgIsMove(
        Expr arg,
        FuncKey? enclosing,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        if (IsNullarySeed(arg, new HashSet<string>(StringComparer.Ordinal)))
        {
            return true;
        }

        // A syntactically fully-fresh allocation (a saturated constructor application / literal /
        // aggregate literal built solely from constructors and literals, with NO variable reference
        // anywhere in it) is unaliased by construction: every reachable cell is freshly allocated by
        // this very expression and reachable only through this single argument reference, so moving
        // it into a reuse fold can never corrupt a concurrently-live value. Unlike a nullary seed
        // (safe even when *shared*), the guarantee here is uniqueness-by-construction, so any nullary
        // constructor — not only the sole one — is admissible, and non-nullary shapes are covered.
        if (IsFullyFreshConstruction(arg))
        {
            return true;
        }

        // (CO-2 result-alias) A saturated call to a registered function written inline at the call site
        // whose result is a move here: the callee's result-reach summary is not poisoned, and for every
        // parameter its result may alias, the argument bound to it is itself a move (recursively). A
        // result-fresh callee reaches {} and is admitted unconditionally (the empty-reach special case,
        // subsuming the earlier higher-order-seed rule); a `wrap`-style builder that returns a parameter
        // is admitted exactly when that parameter's argument is a move.
        if (IsResultAliasMove(arg, enclosing, scope))
        {
            return true;
        }

        if (arg is Expr.Var v)
        {
            return ArgIsMoveVar(v, enclosing, scope);
        }

        return false;
    }

    /// <summary>
    /// The <c>Var</c>-argument cases of <see cref="ArgIsMove"/>: a move-linear reference to a
    /// move-safe accumulator parameter of the enclosing function, or a let-bound fresh construction
    /// that is move-linear within its binding scope.
    /// </summary>
    private bool ArgIsMoveVar(
        Expr.Var v,
        FuncKey? enclosing,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        FuncKey? encKey = null;
        (List<string> Params, Expr Body)? encInfo = null;
        if (enclosing is { } candidateKey && _maFuncs.TryGetValue(candidateKey, out var candidateInfo))
        {
            encKey = candidateKey;
            encInfo = candidateInfo;
        }

        // (i) A move-linear reference to a move-safe accumulator parameter of the enclosing
        // function (the transitive, interprocedural step).
        if (encInfo is { } enc
            && encKey is { } resolvedKey
            && enc.Params.Contains(v.Name)
            && IsMoveLinear(v.Name, enc.Body)
            && IsParamMoveSafe(resolvedKey, v.Name))
        {
            return true;
        }

        // (ii) Richer aliasing (CO-2 increment): a `Var` that is NOT syntactically fresh at the
        // call site, but is bound by a `let` LOCAL to the enclosing scope to a fully-fresh
        // construction, and is move-linear here (used at most once on any path, never captured).
        // The `let` confines the name's scope to this body, so move-linearity there proves no
        // other live reference exists anywhere; the fresh construction proves the bound value is
        // unaliased and free of internal sharing. Together the value is uniquely owned and
        // dead-after-this-use — a sound move — even though the freshness is at the binding site
        // rather than at the call site. Only accepted when the name is unambiguous program-wide (a
        // single binding), so the located RHS is definitive. At a top-level call site (no
        // registered function frame) the enclosing body is the whole desugared program; a
        // top-level `let seed = <fresh>` lives on its spine and move-linearity over the whole
        // program is the stronger proof of unique ownership.
        Expr? encBody = encInfo?.Body ?? (enclosing is null ? _maBody : null);
        if (encBody is not null
            && !_maAmbiguous.Contains(v.Name)
            && TryFindLocalLet(v.Name, encBody) is var (boundRhs, boundScope)
            && boundRhs is not null
            && boundScope is not null
            // Fresh by construction, or (CO-2 result-alias) the result of a builder call that is a
            // move here (result-reach not poisoned, and every reached parameter's argument is itself
            // a move) — both give a uniquely-owned, internally-unshared bound value.
            && (IsFullyFreshConstruction(boundRhs) || IsResultAliasMove(boundRhs, enclosing, scope))
            // Move-linear within the binding's own scope (its `let` body): used at most once on
            // any path there, never captured. Counting in the scope — not the whole enclosing
            // body — is essential: the whole-body count would stop at this very definition
            // (treating it as a shadow) and spuriously report zero uses.
            && MaxPathOccurrences(v.Name, boundScope) <= 1)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Locates a non-recursive <c>let</c>/<c>let-result</c> binding of <paramref name="name"/> within
    /// <paramref name="body"/> and returns its right-hand side together with the binding's <b>scope</b>
    /// (the <c>let</c> body, over which <paramref name="name"/> is live). Returns <c>(null, null)</c>
    /// when no such binding exists. Traverses control-flow structure but never descends into a nested
    /// <c>Lambda</c> body — a nested lambda is a separate function scope, so a binding inside it is not
    /// local to this function. Recursive (<c>let rec</c>) bindings are excluded: their RHS is
    /// self-referential and never a fresh construction. Returning the scope (not the whole enclosing
    /// body) is what lets the caller count uses correctly — a whole-body occurrence count would stop
    /// at this very definition (treating it as a shadow) and report zero uses.
    /// </summary>
    private static (Expr? Rhs, Expr? Scope) TryFindLocalLet(string name, Expr body)
    {
        switch (body)
        {
            case Expr.Let l:
                if (string.Equals(l.Name, name, StringComparison.Ordinal))
                {
                    return (l.Value, l.Body);
                }

                return FirstFound(TryFindLocalLet(name, l.Value), () => TryFindLocalLet(name, l.Body));

            case Expr.LetResult lr:
                if (string.Equals(lr.Name, name, StringComparison.Ordinal))
                {
                    return (lr.Value, lr.Body);
                }

                return FirstFound(TryFindLocalLet(name, lr.Value), () => TryFindLocalLet(name, lr.Body));

            case Expr.LetRecursive lrec:
                // A self-referential binding is never a fresh construction; only search its subtrees.
                if (string.Equals(lrec.Name, name, StringComparison.Ordinal))
                {
                    return (null, null);
                }

                return FirstFound(TryFindLocalLet(name, lrec.Value), () => TryFindLocalLet(name, lrec.Body));

            case RecursiveGroupExpr group:
                return TryFindLocalLetInRecursiveGroup(name, group);

            case Expr.If i:
                return FirstFound(
                    TryFindLocalLet(name, i.Cond),
                    () => FirstFound(TryFindLocalLet(name, i.Then), () => TryFindLocalLet(name, i.Else)));

            case Expr.Match m:
                return TryFindLocalLetInMatch(name, m);

            case Expr.Call c:
                return FirstFound(TryFindLocalLet(name, c.Func), () => TryFindLocalLet(name, c.Arg));

            // A nested lambda is a separate function scope: do NOT descend (a binding inside it is not
            // local to this function, and this function's `name` cannot be bound there).
            case Expr.Lambda:
                return (null, null);

            default:
                return TryFindLocalLetInChildren(name, body);
        }
    }

    private static (Expr? Rhs, Expr? Scope) TryFindLocalLetInRecursiveGroup(
        string name,
        RecursiveGroupExpr group)
    {
        // Every member name is recursively in scope in all member values and in the continuation,
        // so a matching member shadows the searched outer binding throughout.
        if (group.Bindings.Any(
            binding => string.Equals(binding.Name, name, StringComparison.Ordinal)))
        {
            return (null, null);
        }

        foreach ((_, Expr value) in group.Bindings)
        {
            var found = TryFindLocalLet(name, value);
            if (found.Rhs is not null)
            {
                return found;
            }
        }

        return TryFindLocalLet(name, group.Body);
    }

    private static (Expr? Rhs, Expr? Scope) TryFindLocalLetInMatch(string name, Expr.Match m)
    {
        var found = TryFindLocalLet(name, m.Value);
        if (found.Rhs is not null)
        {
            return found;
        }

        foreach (var c in m.Cases)
        {
            // A pattern binding of the same name shadows the let we are after in that arm.
            if (PatternBinds(c.Pattern, name))
            {
                continue;
            }

            found = TryFindLocalLet(name, c.Body);
            if (found.Rhs is not null)
            {
                return found;
            }

            if (c.Guard is not null)
            {
                found = TryFindLocalLet(name, c.Guard);
                if (found.Rhs is not null)
                {
                    return found;
                }
            }
        }

        return (null, null);
    }

    private static (Expr? Rhs, Expr? Scope) TryFindLocalLetInChildren(string name, Expr body)
    {
        foreach (var child in EnumerateChildren(body))
        {
            var found = TryFindLocalLet(name, child);
            if (found.Rhs is not null)
            {
                return found;
            }
        }

        return (null, null);
    }

    private static (Expr? Rhs, Expr? Scope) FirstFound((Expr? Rhs, Expr? Scope) first, System.Func<(Expr? Rhs, Expr? Scope)> second)
    {
        return first.Rhs is not null ? first : second();
    }

    /// <summary>
    /// True when <paramref name="arg"/> resolves to the sole nullary constructor of its type — a
    /// value whose cell can never be observably overwritten by in-place reuse (see the file header).
    /// Follows top-level value aliases (e.g. <c>Ashes.Collection.Map.empty → Empty</c>), cycle-guarded.
    /// </summary>
    private bool IsNullarySeed(Expr arg, HashSet<string> visiting)
    {
        switch (arg)
        {
            case Expr.Var v:
                if (_constructorSymbols.TryGetValue(v.Name, out var ctor))
                {
                    return IsSoleNullaryConstructor(ctor);
                }

                if (visiting.Add(v.Name) && _maValueRhs.TryGetValue(v.Name, out var rhs))
                {
                    return IsNullarySeed(rhs, visiting);
                }

                return false;

            case Expr.QualifiedVar qv:
                var resolved = ResolveSpecializableCalleeName(qv);
                if (resolved is not null && visiting.Add(resolved) && _maValueRhs.TryGetValue(resolved, out var qrhs))
                {
                    return IsNullarySeed(qrhs, visiting);
                }

                return false;

            default:
                return false;
        }
    }

    private bool IsSoleNullaryConstructor(ConstructorSymbol ctor)
    {
        if (ctor.Arity != 0 || !_typeSymbols.TryGetValue(ctor.ParentType, out var typeSym))
        {
            return false;
        }

        int nullaryCount = 0;
        foreach (var c in typeSym.Constructors)
        {
            if (c.Arity == 0)
            {
                nullaryCount++;
            }
        }

        return nullaryCount == 1;
    }

    /// <summary>
    /// True when <paramref name="arg"/> is a syntactically fully-fresh allocation: a saturated
    /// constructor application, a scalar/string literal, or a tuple/list/cons/record literal, whose
    /// every sub-expression is itself fully fresh. Crucially it contains <b>no variable reference</b>
    /// (no <c>Var</c>/<c>QualifiedVar</c>, no <c>Call</c> to a non-constructor) — a variable could
    /// alias shared data or introduce internal sharing (e.g. <c>let x = Node(..) in Node(0, x, x)</c>),
    /// which reuse could then corrupt. With no variables the value is a fresh tree with no internal
    /// sharing, reachable only through this one argument reference, hence a sound move. A bare
    /// (unapplied) <c>Var</c> is never fresh here — nullary seeds go through <see cref="IsNullarySeed"/>.
    /// </summary>
    private bool IsFullyFreshConstruction(Expr arg)
    {
        switch (arg)
        {
            case Expr.IntLit:
            case Expr.UIntLit:
            case Expr.BigIntLit:
            case Expr.FloatLit:
            case Expr.StrLit:
            case Expr.BoolLit:
                return true;

            // A bare name is fresh only when it is the sole nullary constructor of its type — a
            // 0-field cell whose reuse-overwrite is a no-op even if shared (the seed rule). Any other
            // variable may alias shared data, so it breaks freshness.
            case Expr.Var:
            case Expr.QualifiedVar:
                return IsNullarySeed(arg, new HashSet<string>(StringComparer.Ordinal));

            case Expr.TupleLit t:
                return t.Elements.All(IsFullyFreshConstruction);
            case Expr.ListLit lst:
                return lst.Elements.All(IsFullyFreshConstruction);
            case Expr.Cons cons:
                return IsFullyFreshConstruction(cons.Head) && IsFullyFreshConstruction(cons.Tail);
            case Expr.RecordLit rec:
                return rec.Fields.All(f => IsFullyFreshConstruction(f.Value));

            case Expr.Call:
                {
                    var args = new List<Expr>();
                    var head = CollectCallArgs(arg, args);
                    // Only a saturated application of a data constructor is a fresh allocation; any
                    // other call may return an aliased/shared (or reuse-rewritten) value.
                    if (head is Expr.Var hv
                        && _constructorSymbols.TryGetValue(hv.Name, out var ctor)
                        && args.Count == ctor.Arity)
                    {
                        return args.All(IsFullyFreshConstruction);
                    }

                    return false;
                }

            default:
                return false;
        }
    }

    /// <summary>
    /// Computes the result-reachability (may-alias) summary (<see cref="_maResultReach"/>) as a monotone
    /// least fixpoint: every registered function starts with empty reach and not poisoned, and each pass
    /// recomputes its result-reach from its body under the current summaries, unioning the growth in,
    /// until stable. Starting from the empty (bottom) approximation and only growing is the sound
    /// direction for a MAY-analysis (over-approximation): an under-computed early pass can only stay
    /// smaller, never over-claim confinement. Recursion is handled naturally — a self/mutual call reads
    /// the callee's current (growing) summary — so a recursive builder converges without a special cycle
    /// rule; a poison source (a global reference, an unmodeled node, or internal sharing) is detected
    /// directly in the body and propagates through the fixpoint.
    /// </summary>
    private void ComputeResultReach()
    {
        _maResultReach.Clear();
        foreach (var name in _maFuncs.Keys)
        {
            _maResultReach[name] = ReachBottom();
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (name, info) in _maFuncs)
            {
                var env = new Dictionary<string, ResultReachState>(StringComparer.Ordinal);
                foreach (var p in info.Params)
                {
                    env[p] = new ResultReachState(
                        new Dictionary<string, int>(StringComparer.Ordinal) { [p] = 1 },
                        ResultReachCause.None);
                }

                _maReachToken = 0;
                _maSelfRecursive = _maNestedRecursive.TryGetValue(name, out var nr)
                    ? (name, nr.Recursive, nr.RecursiveName, nr.Outer, nr.Acc)
                    : null;
                IReadOnlyDictionary<string, FuncKey> scope = _maFunctionScopes.GetValueOrDefault(name)
                    ?? new Dictionary<string, FuncKey>(StringComparer.Ordinal);
                var computed = StripSyntheticTokens(ResultReach(info.Body, env, scope));
                _maSelfRecursive = null;
                var merged = ReachJoin(_maResultReach[name], computed);
                if (!ReachEquals(_maResultReach[name], merged))
                {
                    _maResultReach[name] = merged;
                    changed = true;
                }
            }
        }
    }

    /// <summary>
    /// Expression-level freshness for every sub-expression <see cref="ResultReach"/> visits while
    /// walking <paramref name="function"/>'s body, keyed by node identity. Must run only after
    /// <see cref="ComputeResultReach"/> has converged (<c>_maResultReach</c> stable for every function):
    /// this re-walks the body exactly once more with the same env-construction rules, now with
    /// recording turned on, so it reproduces the converged pass's per-node verdicts rather than an
    /// earlier, not-yet-stable iteration's.
    /// </summary>
    private Dictionary<Expr, bool> ComputeExpressionFreshness(FuncKey function, (List<string> Params, Expr Body) info)
    {
        var env = new Dictionary<string, ResultReachState>(StringComparer.Ordinal);
        foreach (var p in info.Params)
        {
            env[p] = new ResultReachState(
                new Dictionary<string, int>(StringComparer.Ordinal) { [p] = 1 },
                ResultReachCause.None);
        }

        var map = new Dictionary<Expr, bool>(ReferenceEqualityComparer.Instance);
        _maExpressionFreshness = map;
        _maReachToken = 0;
        _maSelfRecursive = _maNestedRecursive.TryGetValue(function, out var nr)
            ? (function, nr.Recursive, nr.RecursiveName, nr.Outer, nr.Acc)
            : null;
        IReadOnlyDictionary<string, FuncKey> scope = _maFunctionScopes.GetValueOrDefault(function)
            ?? new Dictionary<string, FuncKey>(StringComparer.Ordinal);
        ResultReach(info.Body, env, scope);
        _maSelfRecursive = null;
        _maExpressionFreshness = null;
        return map;
    }

    private static ResultReachState ReachBottom()
        => new(new Dictionary<string, int>(StringComparer.Ordinal), ResultReachCause.None);

    private static ResultReachState ReachPoisoned(ResultReachCause cause = ResultReachCause.UnmodelledReach)
        => new(new Dictionary<string, int>(StringComparer.Ordinal), cause);

    // Sequential composition (simultaneously-live heap positions — a constructor's heap fields, an
    // aggregate's elements): multiplicities add, so a parameter reachable through two positions reaches
    // the cap and poisons (internal sharing — a moved argument would be doubly aliased in the result).
    private static ResultReachState ReachSum(
        ResultReachState a,
        ResultReachState b)
    {
        var counts = new Dictionary<string, int>(a.Counts, StringComparer.Ordinal);
        ResultReachCause causes = a.Causes | b.Causes;
        foreach (var (k, v) in b.Counts)
        {
            int nv = (counts.TryGetValue(k, out var e) ? e : 0) + v;
            counts[k] = nv >= ReachCap ? ReachCap : nv;
        }

        foreach (var v in counts.Values)
        {
            if (v >= ReachCap)
            {
                causes |= ResultReachCause.InternalSharing;
            }
        }

        // Internal-sharing at the cell-identity level: two SIMULTANEOUSLY-live tokens where one is a
        // proper path-ancestor of the other (e.g. "map" and "map/1", or a fresh "#3" and "#3/0") mean the
        // result embeds both a value AND one of its own sub-cells — the sub-cell is reachable through two
        // paths, exactly the internal sharing the entry copy exists to unshare. Disjoint sibling sub-cells
        // ("map/1" and "map/2") are NOT in an ancestor relation, so partitioning a value and re-embedding
        // its distinct parts (a rebalance/rebuild) never falsely poisons. Only summed (simultaneously-live)
        // positions are checked — branch joins (ReachMax) never manufacture this.
        if ((causes & ResultReachCause.InternalSharing) == ResultReachCause.None
            && HasPathAncestorPair(counts))
        {
            causes |= ResultReachCause.InternalSharing;
        }

        return new ResultReachState(counts, causes);
    }

    // True when the token set contains a proper path-ancestor/descendant pair: some key k and another key
    // equal to k + "/" + suffix. Path segments are separated by '/', which no identifier or synthetic
    // token segment contains, so a prefix up to a '/' boundary is a genuine containment relation.
    private static bool HasPathAncestorPair(Dictionary<string, int> counts)
    {
        foreach (var outer in counts.Keys)
        {
            string prefix = outer + "/";
            foreach (var inner in counts.Keys)
            {
                if (inner.Length > prefix.Length && inner.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Extends every token in a reach by a heap-field position: destructuring a value's field <c>field</c>
    // yields a DISTINCT sub-cell of every cell the value may alias, so each token k becomes "k/field".
    // Distinct fields therefore stay disjoint (siblings), while a field of a field nests deeper — the path
    // records the containment used by <see cref="HasPathAncestorPair"/>.
    private static ResultReachState ExtendPaths(
        ResultReachState r,
        int field)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (k, v) in r.Counts)
        {
            counts[k + "/" + field] = v;
        }

        return new ResultReachState(counts, r.Causes);
    }

    // Branch join (if/match arms — at most one executes): multiplicities take the max, so distinct arms
    // never manufacture sharing.
    private static ResultReachState ReachMax(
        ResultReachState a,
        ResultReachState b)
    {
        var counts = new Dictionary<string, int>(a.Counts, StringComparer.Ordinal);
        foreach (var (k, v) in b.Counts)
        {
            counts[k] = counts.TryGetValue(k, out var e) && e > v ? e : v;
        }

        return new ResultReachState(counts, a.Causes | b.Causes);
    }

    // Fixpoint join: identical to the branch max (grow reach sets / poison until stable).
    private static ResultReachState ReachJoin(
        ResultReachState a,
        ResultReachState b)
        => ReachMax(a, b);

    // Scale by a callee's per-parameter multiplicity: a callee embedding a parameter twice doubles the
    // reach of the argument bound to it (again capped into poison at the sharing boundary).
    private static ResultReachState ReachScale(
        ResultReachState a,
        int factor)
    {
        if (factor <= 0)
        {
            return ReachBottom();
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        ResultReachCause causes = a.Causes;
        foreach (var (k, v) in a.Counts)
        {
            int nv = v * factor;
            if (nv >= ReachCap)
            {
                nv = ReachCap;
                causes |= ResultReachCause.InternalSharing;
            }

            counts[k] = nv;
        }

        return new ResultReachState(counts, causes);
    }

    private static bool ReachEquals(
        ResultReachState a,
        ResultReachState b)
    {
        if (a.Causes != b.Causes || a.Counts.Count != b.Counts.Count)
        {
            return false;
        }

        foreach (var (k, v) in a.Counts)
        {
            if (!b.Counts.TryGetValue(k, out var bv) || bv != v)
            {
                return false;
            }
        }

        return true;
    }

    // A fresh per-binding synthetic identity token, reach {token:1}. Summed into a binding's env reach so
    // multiplicity of a fresh (non-parameter) heap value is tracked exactly as for a parameter.
    private ResultReachState TokenReach()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal) { ["#" + _maReachToken] = 1 };
        _maReachToken++;
        return new ResultReachState(counts, ResultReachCause.None);
    }

    // Collapses a working reach to the stored per-function summary: each path token is reduced to its ROOT
    // (the segment before the first '/'), synthetic '#'-rooted identity tokens are dropped (they are local
    // to one function's ResultReach pass and must never be stored or reach IsResultAliasMove), and each
    // surviving real parameter is recorded at presence (multiplicity 1) — a parameter reached through two
    // DISJOINT sub-cells ("map/1" and "map/2") is reached once, not twice; a genuine same-cell double
    // (which would be internal sharing) already set poison during the sum before this collapse. Poison is
    // preserved. The result keys are exactly parameter names, so IsResultAliasMove/CallReach can map them.
    private static ResultReachState StripSyntheticTokens(
        ResultReachState r)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var k in r.Counts.Keys)
        {
            int slash = k.IndexOf('/');
            string root = slash < 0 ? k : k.Substring(0, slash);
            if (root.Length == 0 || root[0] == '#')
            {
                continue;
            }

            counts[root] = 1;
        }

        return new ResultReachState(counts, r.Causes);
    }

    /// <summary>
    /// The result-reach of <paramref name="e"/> as the returned value of a function body: the set of the
    /// enclosing function's parameters (with multiplicity) the value may alias, plus a poison flag when
    /// the value is not provably confined to those parameters. <paramref name="env"/> maps each in-scope
    /// name to its reach (each parameter to itself; let/pattern bindings to the reach of what they bind).
    /// A bare free reference (a top-level/global binding or unmodeled name), a non-sole nullary or
    /// partially-applied constructor, an unresolved/under-or-over-applied call, or any unmodeled node
    /// poisons; the conservative default is poison, so an unproven shape never over-claims confinement.
    /// </summary>
    private ResultReachState ResultReach(
        Expr e,
        Dictionary<string, ResultReachState> env,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        var result = ResultReachCore(e, env, scope);
        RecordExpressionFreshness(e, result);
        return result;
    }

    // Every node ResultReach visits is, by construction, exactly the set of sub-expressions whose
    // freshness matters below the whole-function-result level (see ExpressionFreshness on
    // FunctionOwnershipSummary): a let/let-result value and body, an if/match arm, a match scrutinee,
    // an aggregate literal's elements, a call's arguments. Recording each one's verdict here — rather
    // than forking a parallel traversal — guarantees the expression-level fact can never drift from the
    // already-audited whole-function fixpoint it generalizes. Only active during the dedicated
    // post-fixpoint recording pass (see ComputeExpressionFreshness); a null map during the hot
    // while-changed fixpoint loop makes this a no-op there, so recording cannot affect convergence.
    private void RecordExpressionFreshness(Expr e, ResultReachState result)
    {
        if (_maExpressionFreshness is { } map)
        {
            map[e] = result is { Poison: false, Counts.Count: 0 };
        }
    }

    private ResultReachState ResultReachCore(
        Expr e,
        Dictionary<string, ResultReachState> env,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        if (ResultReachIsCopyScalar(e))
        {
            return ReachBottom();
        }

        switch (e)
        {
            case Expr.Var v:
                return ResultReachVar(v, env);

            case Expr.QualifiedVar:
                return ReachPoisoned(ResultReachCause.GlobalOrTopLevelReach);

            // Control flow: the returned value is one of the arms; the scrutinee/condition is not part
            // of the returned value (but a match's scrutinee reach flows into its pattern bindings).
            case Expr.If i:
                return ReachMax(ResultReach(i.Then, env, scope), ResultReach(i.Else, env, scope));
            case Expr.Match m:
                return MatchReach(m, env, scope);

            case Expr.Let l:
                return ResultReach(
                    l.Body,
                    ExtendEnv(env, l.Name, ReachSum(ResultReach(l.Value, env, scope), TokenReach())),
                    ExtendFuncScope(scope, l, l.Name));
            case Expr.LetResult lr:
                return ResultReach(
                    lr.Body,
                    ExtendEnv(env, lr.Name, ReachSum(ResultReach(lr.Value, env, scope), TokenReach())),
                    ExtendFuncScope(scope, lr, lr.Name));
            case Expr.LetRecursive lrec:
                // A self-referential local binding is not modeled; treat any use of it as poison.
                return ResultReach(
                    lrec.Body,
                    ExtendEnv(env, lrec.Name, ReachPoisoned()),
                    ExtendFuncScope(scope, lrec, lrec.Name));

            // Aggregate/list literals: every element is simultaneously live, so reach sums.
            case Expr.Cons cons:
                return ReachSum(ResultReach(cons.Head, env, scope), ResultReach(cons.Tail, env, scope));
            case Expr.ListLit lst:
                return SumReach(lst.Elements, env, scope);
            case Expr.TupleLit t:
                return SumReach(t.Elements, env, scope);
            case Expr.RecordLit rec:
                return ResultReachRecordLit(rec, env, scope);

            case Expr.Call:
                return CallReach(e, env, scope);

            default:
                // Unmodeled node (Lambda, Await, RecordUpdate, Result pipes, …): not provably confined.
                return ReachPoisoned();
        }
    }

    // Literal and arithmetic/comparison/bitwise/shift results are copy-typed scalars — they reach no
    // heap cell, so they are confined and reach no parameter.
    private static bool ResultReachIsCopyScalar(Expr e)
    {
        return e is Expr.IntLit or Expr.UIntLit or Expr.BigIntLit or Expr.FloatLit or Expr.StrLit
            or Expr.BoolLit
            or Expr.Add or Expr.Subtract or Expr.Multiply or Expr.Divide or Expr.Modulo
            or Expr.BitwiseAnd or Expr.BitwiseOr or Expr.BitwiseXor
            or Expr.ShiftLeft or Expr.ShiftRight or Expr.BitwiseNot or Expr.LogicalNot
            or Expr.GreaterThan or Expr.LessThan or Expr.GreaterOrEqual or Expr.LessOrEqual
            or Expr.Equal or Expr.NotEqual;
    }

    private ResultReachState ResultReachVar(
        Expr.Var v,
        Dictionary<string, ResultReachState> env)
    {
        if (env.TryGetValue(v.Name, out var bound))
        {
            return bound;
        }

        if (_constructorSymbols.TryGetValue(v.Name, out var ctor))
        {
            // A nullary constructor value reaches no parameter; it is confined only when it is
            // the sole nullary constructor of its type (a no-op-safe tag cell). Any other nullary
            // (a possibly-shared non-sole singleton) or a partially-applied constructor poisons.
            return ctor.Arity == 0 && IsSoleNullaryConstructor(ctor) ? ReachBottom() : ReachPoisoned();
        }

        // A registered nonlocal value is a top-level/global reach. A name absent from the binding
        // census is conservatively unknown rather than guessed to be global.
        return ReachPoisoned(
            _maValueRhs.ContainsKey(v.Name)
                ? ResultReachCause.GlobalOrTopLevelReach
                : ResultReachCause.ConservativeUnknown);
    }

    private ResultReachState ResultReachRecordLit(
        Expr.RecordLit rec,
        Dictionary<string, ResultReachState> env,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        ConstructorSymbol? constructor = _constructorSymbols.GetValueOrDefault(rec.TypeName);
        IReadOnlyList<string> fieldNames = constructor?.DeclaringSyntax.FieldNames ?? [];
        var acc = ReachBottom();
        foreach (var (fieldName, fieldValue) in rec.Fields)
        {
            int fieldIndex = -1;
            for (int i = 0; i < fieldNames.Count; i++)
            {
                if (string.Equals(fieldNames[i], fieldName, StringComparison.Ordinal))
                {
                    fieldIndex = i;
                    break;
                }
            }
            bool copyField = constructor is not null
                && fieldIndex >= 0
                && fieldIndex < constructor.ParameterTypes.Count
                && CanArenaReset(constructor.ParameterTypes[fieldIndex]);
            if (!copyField)
            {
                acc = ReachSum(acc, ResultReach(fieldValue, env, scope));
            }
        }

        return acc;
    }

    private ResultReachState SumReach(
        IReadOnlyList<Expr> exprs,
        Dictionary<string, ResultReachState> env,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        var acc = ReachBottom();
        foreach (var el in exprs)
        {
            acc = ReachSum(acc, ResultReach(el, env, scope));
        }

        return acc;
    }

    private static Dictionary<string, ResultReachState> ExtendEnv(
        Dictionary<string, ResultReachState> env,
        string name,
        ResultReachState value)
    {
        var env2 = new Dictionary<string, ResultReachState>(env, StringComparer.Ordinal)
        {
            [name] = value,
        };
        return env2;
    }

    /// <summary>
    /// Result-reach of a <c>match</c>: the scrutinee's reach flows into each arm's pattern variables
    /// (each pattern binding may alias the scrutinee — sub-values of a value the fold could overwrite),
    /// then the arms join by max (at most one executes). A copy-typed pattern variable so bound is only
    /// ever used in a copy position (ignored by the constructor case), so over-approximating it here is
    /// harmless.
    /// </summary>
    private ResultReachState MatchReach(
        Expr.Match m,
        Dictionary<string, ResultReachState> env,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        var scrut = ResultReach(m.Value, env, scope);
        ResultReachState? acc = null;
        foreach (var c in m.Cases)
        {
            var env2 = BindPatternReach(c.Pattern, scrut, env);
            var binders = new HashSet<string>(StringComparer.Ordinal);
            CollectPatternBinders(c.Pattern, binders);
            var arm = ResultReach(c.Body, env2, RemoveFuncNames(scope, binders));
            acc = acc is null ? arm : ReachMax(acc.Value, arm);
        }

        return acc ?? ReachPoisoned(ResultReachCause.ConservativeUnknown);
    }

    private Dictionary<string, ResultReachState> BindPatternReach(
        Pattern p,
        ResultReachState scrut,
        Dictionary<string, ResultReachState> env)
    {
        var env2 = new Dictionary<string, ResultReachState>(env, StringComparer.Ordinal);
        BindPatternPaths(p, scrut, env2);
        return env2;
    }

    /// <summary>
    /// Binds each variable of pattern <paramref name="p"/> to its reach, deriving DISJOINT sub-cell paths
    /// from the scrutinee reach <paramref name="parentReach"/>: a constructor/tuple/cons field at index
    /// <c>i</c> is a distinct sub-cell, so its reach is <see cref="ExtendPaths"/>(parent, i). Distinct
    /// fields stay disjoint siblings (rebuilding a value from its own distinct parts never manufactures
    /// sharing), while the same variable used twice, or a field-of-a-field co-embedded with its parent,
    /// still poisons through the path relation. A bound variable additionally carries a fresh identity
    /// token so the same-variable-twice case poisons even when the scrutinee is a fresh (path-less) value.
    /// </summary>
    private void BindPatternPaths(
        Pattern p,
        ResultReachState parentReach,
        Dictionary<string, ResultReachState> env2)
    {
        switch (p)
        {
            case Pattern.Var v:
                // A bare Var pattern whose name is a data constructor is a NULLARY-constructor pattern
                // (matching a specific 0-field tag), not a variable binding — it binds nothing, and the
                // matched value is that nullary constant. Leaving it unbound lets any reference to the
                // name in the arm resolve as the constructor (a sole-nullary reaches nothing; a non-sole
                // nullary poisons), instead of aliasing the whole scrutinee. Without this, a nullary arm
                // that reuses the tag (`| Empty -> makeNode(Empty)(k)(v)(Empty)`) would bind "Empty" to the
                // scrutinee and, using it twice, falsely report the scrutinee as internally shared.
                if (!_constructorSymbols.ContainsKey(v.Name))
                {
                    env2[v.Name] = ReachSum(parentReach, TokenReach());
                }

                break;
            case Pattern.Constructor c:
                for (int i = 0; i < c.Patterns.Count; i++)
                {
                    BindPatternPaths(c.Patterns[i], ExtendPaths(parentReach, i), env2);
                }

                break;
            case Pattern.Tuple t:
                for (int i = 0; i < t.Elements.Count; i++)
                {
                    BindPatternPaths(t.Elements[i], ExtendPaths(parentReach, i), env2);
                }

                break;
            case Pattern.Cons cons:
                BindPatternPaths(cons.Head, ExtendPaths(parentReach, 0), env2);
                BindPatternPaths(cons.Tail, ExtendPaths(parentReach, 1), env2);
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// Result-reach of a call expression: a saturated data-constructor application (its heap-typed fields
    /// sum — copy-typed fields hold scalars inline and are ignored), or a saturated call to a registered
    /// function (substitute the argument bound to each parameter the callee's result may reach, scaled by
    /// its multiplicity, and sum). A partial/over-applied constructor, an unresolved/ambiguous/mis-arity
    /// call, or a poisoned callee poisons.
    /// </summary>
    private ResultReachState CallReach(
        Expr e,
        Dictionary<string, ResultReachState> env,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        var args = new List<Expr>();
        var head = CollectCallArgs(e, args);

        // Inner recursive self-call of a nested-rec (Map.set-shape) function: `go(x)` denotes the enclosing
        // function applied to the SAME outer params with the accumulator set to x. Resolve it against the
        // enclosing function's own (growing) summary: substitute the accumulator parameter's reach with
        // reach(x) and hold every outer parameter at its identity reach in env. This closes the recursion
        // through the standard monotone fixpoint (the self summary starts at bottom and only grows).
        if (_maSelfRecursive is { } sr
            && head is Expr.Var rv
            && scope.TryGetValue(rv.Name, out FuncKey recursiveKey)
            && recursiveKey.Equals(sr.Recursive)
            && args.Count == 1)
        {
            return CallReachSelfRecursive(sr, args, env, scope);
        }

        if (head is Expr.Var hv && _constructorSymbols.TryGetValue(hv.Name, out var ctor))
        {
            return CallReachConstructor(ctor, args, env, scope);
        }

        string? name = head switch
        {
            Expr.Var v => v.Name,
            Expr.QualifiedVar => ResolveSpecializableCalleeName(head),
            _ => null,
        };

        if (name is null
            || TryResolveFunctionKey(head, name, scope) is not { } key
            || !_maFuncs.TryGetValue(key, out var info))
        {
            return ReachPoisoned(ResultReachCause.UnmodelledReach);
        }

        // (CO-2d) Over-application: the callee returns a closure that is applied to the surplus
        // arguments, e.g. `(makeBuilder(x))(n)` where `makeBuilder`'s body reduces to a lambda behind
        // an if/match (so currying did not statically flatten it into one function). Model it by
        // inlining the callee's body one level, binding each surplus argument to the returned lambda's
        // parameter (capture-aware: a captured parameter embedded in the produced value is reached via
        // its argument marker; a captured global or unmodeled capture poisons). The symbolic reach is
        // over "@i" argument-position markers; substitute each marker's argument's reach and sum.
        if (args.Count > info.Params.Count)
        {
            return CallReachOverApplied(key, info, args, env, scope);
        }

        return CallReachRegistered(key, info, args, env, scope);
    }

    private ResultReachState CallReachSelfRecursive(
        (FuncKey Func, FuncKey Recursive, string RecursiveName, List<string> Outer, string Acc) sr,
        List<Expr> args,
        Dictionary<string, ResultReachState> env,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        if (!_maResultReach.TryGetValue(sr.Func, out var selfSummary))
        {
            return ReachBottom();
        }

        var selfResult = new ResultReachState(
            new Dictionary<string, int>(StringComparer.Ordinal),
            selfSummary.Causes);
        foreach (var (paramName, mult) in selfSummary.Counts)
        {
            ResultReachState paramReach;
            if (string.Equals(paramName, sr.Acc, StringComparison.Ordinal))
            {
                paramReach = ResultReach(args[0], env, scope);
            }
            else if (env.TryGetValue(paramName, out var outerReach))
            {
                paramReach = outerReach; // an outer parameter, held at identity
            }
            else
            {
                return ReachPoisoned(ResultReachCause.ConservativeUnknown);
            }

            selfResult = ReachSum(selfResult, ReachScale(paramReach, mult));
        }

        return selfResult;
    }

    private ResultReachState CallReachConstructor(
        ConstructorSymbol ctor,
        List<Expr> args,
        Dictionary<string, ResultReachState> env,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        if (args.Count != ctor.Arity)
        {
            return ReachPoisoned(); // partial or over-applied constructor
        }

        var acc = ReachBottom();
        for (int i = 0; i < args.Count; i++)
        {
            bool copyField = ctor.ParameterTypes.Count == ctor.Arity
                && CanArenaReset(ctor.ParameterTypes[i]);
            if (copyField)
            {
                continue; // inline scalar — cannot alias a heap cell the fold overwrites
            }

            acc = ReachSum(acc, ResultReach(args[i], env, scope));
        }

        return acc;
    }

    private ResultReachState CallReachOverApplied(
        FuncKey key,
        (List<string> Params, Expr Body) info,
        List<Expr> args,
        Dictionary<string, ResultReachState> env,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        var over = OverApplicationReach(key, info, args);
        if (over is not { } ov)
        {
            return ReachPoisoned(ResultReachCause.ConservativeUnknown);
        }

        var acc = new ResultReachState(
            new Dictionary<string, int>(StringComparer.Ordinal),
            ov.Causes);
        foreach (var (marker, mult) in ov.Counts)
        {
            int ai = ArgMarkerIndex(marker);
            if (ai < 0 || ai >= args.Count)
            {
                return ReachPoisoned(ResultReachCause.ConservativeUnknown);
            }

            acc = ReachSum(acc, ReachScale(ResultReach(args[ai], env, scope), mult));
        }

        return acc;
    }

    private ResultReachState CallReachRegistered(
        FuncKey key,
        (List<string> Params, Expr Body) info,
        List<Expr> args,
        Dictionary<string, ResultReachState> env,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        if (args.Count != info.Params.Count
            || !_maResultReach.TryGetValue(key, out var summary))
        {
            RecordUncoveredCallArgumentsFreshness(args, env, scope, covered: null);
            return ReachPoisoned(ResultReachCause.UnmodelledReach);
        }

        var result = new ResultReachState(
            new Dictionary<string, int>(StringComparer.Ordinal),
            summary.Causes);
        HashSet<int>? covered = _maExpressionFreshness is not null ? new HashSet<int>() : null;
        foreach (var (paramName, mult) in summary.Counts)
        {
            int idx = info.Params.IndexOf(paramName);
            if (idx < 0 || idx >= args.Count)
            {
                return ReachPoisoned(ResultReachCause.ConservativeUnknown);
            }

            result = ReachSum(result, ReachScale(ResultReach(args[idx], env, scope), mult));
            covered?.Add(idx);
        }

        RecordUncoveredCallArgumentsFreshness(args, env, scope, covered);
        return result;
    }

    // Visits every argument at position NOT already covered by the loop above purely so
    // RecordExpressionFreshness gets a chance to record its own freshness verdict, without changing
    // what this call contributes to the caller's own reach (that contribution is exactly the sum the
    // loop above already computed from summary.Counts, and stays untouched). This exists because a
    // callee's own result-reach summary only lists parameters the callee's FINAL result may alias — a
    // self-recursive accumulator that is consumed/rendered into something else (never itself returned)
    // has no entry there at all, so without this, none of that self-call's own argument expressions
    // would ever reach RecordExpressionFreshness, leaving every downstream consumer of
    // ExpressionFreshness (including FunctionOwnershipSummary.TcoParamFacts) with no fact to read for
    // that position. Outside the dedicated post-fixpoint recording pass (see ComputeExpressionFreshness)
    // this is a genuine no-op: _maExpressionFreshness is null during the hot while-changed fixpoint loop
    // that computes _maResultReach itself, so this can never affect that fixpoint's convergence or its
    // stored result for any function. Idempotent for a position visited twice (deterministic given the
    // same expression and env), so re-visiting a position the ordinary loop above already covered would
    // be harmless too, even though covered lets us skip it.
    private void RecordUncoveredCallArgumentsFreshness(
        List<Expr> args,
        Dictionary<string, ResultReachState> env,
        IReadOnlyDictionary<string, FuncKey> scope,
        HashSet<int>? covered)
    {
        if (_maExpressionFreshness is null)
        {
            return;
        }

        for (int i = 0; i < args.Count; i++)
        {
            if (covered is null || !covered.Contains(i))
            {
                ResultReach(args[i], env, scope);
            }
        }
    }

    /// <summary>
    /// True when <paramref name="arg"/> is a saturated call to a registered function whose result is a
    /// MOVE at this site: the callee's result-reach summary is not poisoned, and for every parameter its
    /// result may alias, the argument bound to it here is itself a move (recursively via
    /// <see cref="ArgIsMove"/>). A result-fresh callee reaches {} and is admitted unconditionally (the
    /// empty-reach special case — subsuming the earlier higher-order-seed rule); a <c>wrap</c>-style
    /// builder that returns/embeds a parameter is admitted exactly when that parameter's argument is a
    /// move. A saturated constructor application is excluded (already covered by
    /// <see cref="IsFullyFreshConstruction"/>); this is the non-constructor case that rule cannot see
    /// through.
    /// </summary>
    private bool IsResultAliasMove(
        Expr arg,
        FuncKey? enclosing,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        if (arg is not Expr.Call)
        {
            return false;
        }

        var args = new List<Expr>();
        var head = CollectCallArgs(arg, args);

        // A constructor application is not a "result call" — IsFullyFreshConstruction handles it.
        if (head is Expr.Var cv && _constructorSymbols.ContainsKey(cv.Name))
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
            || !_maFuncs.TryGetValue(key, out var info))
        {
            return false;
        }

        // (CO-2d) Over-application through a returned closure: `(makeBuilder(x))(n)` is a move iff the
        // over-application's symbolic result-reach is not poisoned and every argument position its
        // result may alias is itself a move. A closure capturing a fresh/moved value is admitted; one
        // capturing a retained value or a global is declined (poison / non-move argument).
        if (args.Count > info.Params.Count)
        {
            return IsResultAliasMoveOverApplied(key, info, args, enclosing, scope);
        }

        if (args.Count != info.Params.Count
            || !_maResultReach.TryGetValue(key, out var summary)
            || summary.Poison)
        {
            return false;
        }

        foreach (var (paramName, _) in summary.Counts)
        {
            int idx = info.Params.IndexOf(paramName);
            if (idx < 0 || idx >= args.Count || !ArgIsMove(args[idx], enclosing, scope))
            {
                return false;
            }
        }

        return true;
    }

    private bool IsResultAliasMoveOverApplied(
        FuncKey key,
        (List<string> Params, Expr Body) info,
        List<Expr> args,
        FuncKey? enclosing,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        var over = OverApplicationReach(key, info, args);
        if (over is not { } ov || ov.Poison)
        {
            return false;
        }

        foreach (var (marker, _) in ov.Counts)
        {
            int ai = ArgMarkerIndex(marker);
            if (ai < 0 || ai >= args.Count || !ArgIsMove(args[ai], enclosing, scope))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// (CO-2d) Symbolic result-reach of an <b>over-applied</b> call to registered function
    /// <paramref name="key"/> — one whose surplus arguments are applied to a closure the callee
    /// returns. Returns a reach over <c>"@i"</c> argument-position markers (with the callee's own
    /// parameters mapped to their argument markers, so capture of a parameter is tracked), plus a
    /// poison flag; <c>null</c> when not over-applied. Inlines the callee body one level, binding each
    /// surplus argument to the returned lambda's parameter as it descends through the closure-producing
    /// structure (a returned lambda, or lambdas behind if/match/let). Any other closure source (a
    /// nested call result, an unmodeled node) poisons — the conservative default. Synthetic identity
    /// tokens are stripped; the depth guard poisons a chain of nested over-applications.
    /// </summary>
    private ResultReachState? OverApplicationReach(
        FuncKey key,
        (List<string> Params, Expr Body) info,
        List<Expr> args)
    {
        if (args.Count <= info.Params.Count)
        {
            return null;
        }

        if (_maOverAppDepth >= MaxOverAppDepth)
        {
            return ReachPoisoned(ResultReachCause.ConservativeUnknown);
        }

        var symEnv = new Dictionary<string, ResultReachState>(StringComparer.Ordinal);
        for (int i = 0; i < info.Params.Count; i++)
        {
            symEnv[info.Params[i]] = new ResultReachState(
                new Dictionary<string, int>(StringComparer.Ordinal) { ["@" + i] = 1 },
                ResultReachCause.None);
        }

        var extra = new List<string>();
        for (int j = info.Params.Count; j < args.Count; j++)
        {
            extra.Add("@" + j);
        }

        _maOverAppDepth++;
        IReadOnlyDictionary<string, FuncKey> scope = _maFunctionScopes.GetValueOrDefault(key)
            ?? new Dictionary<string, FuncKey>(StringComparer.Ordinal);
        var reach = OverApplyReachSym(info.Body, extra, 0, symEnv, scope);
        _maOverAppDepth--;
        return StripSyntheticTokens(reach);
    }

    /// <summary>
    /// Descends through a closure-producing expression, binding the surplus argument markers
    /// (<paramref name="extra"/>, indexed by <paramref name="idx"/>) to the parameters of the lambdas
    /// it returns, until every surplus argument is consumed — at which point the fully-applied value is
    /// the terminal ADT and its reach is taken by <see cref="ResultReach"/>. Control-flow arms join by
    /// max. Any node that is not a returned lambda / control-flow structure while an argument is still
    /// unconsumed (a nested call, an unmodeled node) poisons.
    /// </summary>
    private ResultReachState OverApplyReachSym(
        Expr body,
        List<string> extra,
        int idx,
        Dictionary<string, ResultReachState> env,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        if (idx >= extra.Count)
        {
            // All surplus arguments consumed — the value is the terminal (ADT) result.
            return ResultReach(body, env, scope);
        }

        switch (body)
        {
            case Expr.Lambda lam:
                {
                    var env2 = ExtendEnv(
                        env,
                        lam.ParamName,
                        new ResultReachState(
                            new Dictionary<string, int>(StringComparer.Ordinal) { [extra[idx]] = 1 },
                            ResultReachCause.None));
                    return OverApplyReachSym(
                        lam.Body, extra, idx + 1, env2, RemoveFuncNames(scope, [lam.ParamName]));
                }

            case Expr.If i:
                return ReachMax(
                    OverApplyReachSym(i.Then, extra, idx, env, scope),
                    OverApplyReachSym(i.Else, extra, idx, env, scope));

            case Expr.Match m:
                return OverApplyMatchReachSym(m, extra, idx, env, scope);

            case Expr.Let l:
                return OverApplyReachSym(
                    l.Body,
                    extra,
                    idx,
                    ExtendEnv(env, l.Name, ReachSum(ResultReach(l.Value, env, scope), TokenReach())),
                    ExtendFuncScope(scope, l, l.Name));
            case Expr.LetResult lr:
                return OverApplyReachSym(
                    lr.Body,
                    extra,
                    idx,
                    ExtendEnv(env, lr.Name, ReachSum(ResultReach(lr.Value, env, scope), TokenReach())),
                    ExtendFuncScope(scope, lr, lr.Name));
            case Expr.LetRecursive lrec:
                return OverApplyReachSym(
                    lrec.Body,
                    extra,
                    idx,
                    ExtendEnv(env, lrec.Name, ReachPoisoned()),
                    ExtendFuncScope(scope, lrec, lrec.Name));

            default:
                // A closure produced by anything else (a nested call result, an unmodeled node) — not
                // provably confined; poison keeps the copy.
                return ReachPoisoned();
        }
    }

    private ResultReachState OverApplyMatchReachSym(
        Expr.Match match,
        List<string> extra,
        int idx,
        Dictionary<string, ResultReachState> env,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        var scrut = ResultReach(match.Value, env, scope);
        ResultReachState? acc = null;
        foreach (MatchCase matchCase in match.Cases)
        {
            var env2 = BindPatternReach(matchCase.Pattern, scrut, env);
            var binders = new HashSet<string>(StringComparer.Ordinal);
            CollectPatternBinders(matchCase.Pattern, binders);
            var arm = OverApplyReachSym(
                matchCase.Body, extra, idx, env2, RemoveFuncNames(scope, binders));
            acc = acc is null ? arm : ReachMax(acc.Value, arm);
        }

        return acc ?? ReachPoisoned(ResultReachCause.ConservativeUnknown);
    }

    // Parses an "@i" argument-position marker back to its index, or -1 when not a marker.
    private static int ArgMarkerIndex(string marker)
    {
        if (marker.Length < 2 || marker[0] != '@')
        {
            return -1;
        }

        return int.TryParse(marker.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture, out var i) ? i : -1;
    }

    /// <summary>
    /// True when <paramref name="name"/> is used at most once on any execution path through
    /// <paramref name="body"/> and is never captured by a nested lambda — so on the path that moves
    /// it into a call, no other live reference to it exists.
    /// </summary>
    private static bool IsMoveLinear(string name, Expr body)
    {
        return MaxPathOccurrences(name, body) <= 1;
    }

    // Sentinel forcing decline: any capture by a nested lambda, or an unmodeled node.
    private const int OccEscape = 1 << 20;

    /// <summary>
    /// Maximum number of times <paramref name="name"/> can be evaluated on a single execution path
    /// through <paramref name="e"/>. Branches take the max of their arms; sequential sub-expressions
    /// sum. A capturing lambda or an unmodeled node returns <see cref="OccEscape"/> (forces decline).
    /// A binding that shadows <paramref name="name"/> stops counting in the shadowed scope.
    /// </summary>
    private static int MaxPathOccurrences(string name, Expr e)
    {
        switch (e)
        {
            case Expr.Var v:
                return string.Equals(v.Name, name, StringComparison.Ordinal) ? 1 : 0;

            case Expr.QualifiedVar:
            case Expr.IntLit:
            case Expr.UIntLit:
            case Expr.BigIntLit:
            case Expr.FloatLit:
            case Expr.StrLit:
            case Expr.BoolLit:
                return 0;

            case Expr.If i:
                return MaxPathOccurrences(name, i.Cond)
                    + System.Math.Max(MaxPathOccurrences(name, i.Then), MaxPathOccurrences(name, i.Else));

            case Expr.Match m:
                return MaxPathOccurrencesMatch(name, m);

            case Expr.Let l:
                // The value is evaluated in the outer scope; the body sees the new binding, which
                // shadows `name` when the names coincide.
                return MaxPathOccurrences(name, l.Value)
                    + (string.Equals(l.Name, name, StringComparison.Ordinal) ? 0 : MaxPathOccurrences(name, l.Body));

            case Expr.LetResult lr:
                return MaxPathOccurrences(name, lr.Value)
                    + (string.Equals(lr.Name, name, StringComparison.Ordinal) ? 0 : MaxPathOccurrences(name, lr.Body));

            case Expr.LetRecursive lrec:
                // Recursive: the bound name is in scope for both value and body.
                return string.Equals(lrec.Name, name, StringComparison.Ordinal)
                    ? 0
                    : MaxPathOccurrences(name, lrec.Value) + MaxPathOccurrences(name, lrec.Body);

            case RecursiveGroupExpr group:
                return MaxPathOccurrencesInRecursiveGroup(name, group);

            case Expr.Lambda lam:
                // A capture keeps the value live beyond a single evaluation and may alias it.
                if (string.Equals(lam.ParamName, name, StringComparison.Ordinal))
                {
                    return 0; // shadowed
                }

                return MaxPathOccurrences(name, lam.Body) > 0 ? OccEscape : 0;

            case Expr.Call c:
                return MaxPathOccurrences(name, c.Func) + MaxPathOccurrences(name, c.Arg);

            default:
                return MaxPathOccurrencesOperators(name, e);
        }
    }

    private static int MaxPathOccurrencesInRecursiveGroup(string name, RecursiveGroupExpr group)
    {
        // Every group name is recursively in scope in every member value and the continuation.
        // Otherwise all member definitions are evaluated before the body.
        if (group.Bindings.Any(
            binding => string.Equals(binding.Name, name, StringComparison.Ordinal)))
        {
            return 0;
        }

        int total = MaxPathOccurrences(name, group.Body);
        foreach ((_, Expr value) in group.Bindings)
        {
            total += MaxPathOccurrences(name, value);
        }

        return total;
    }

    private static int MaxPathOccurrencesMatch(string name, Expr.Match m)
    {
        int worst = 0;
        foreach (var c in m.Cases)
        {
            // A pattern binding of the same name shadows it inside that arm.
            int arm = PatternBinds(c.Pattern, name)
                ? 0
                : MaxPathOccurrences(name, c.Body)
                    + (c.Guard is null ? 0 : MaxPathOccurrences(name, c.Guard));
            worst = System.Math.Max(worst, arm);
        }

        return MaxPathOccurrences(name, m.Value) + worst;
    }

    private static int MaxPathOccurrencesOperators(string name, Expr e)
    {
        switch (e)
        {
            case Expr.Add x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.Subtract x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.Multiply x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.Divide x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.Modulo x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.BitwiseAnd x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.BitwiseOr x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.BitwiseXor x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.ShiftLeft x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.ShiftRight x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.BitwiseNot x: return MaxPathOccurrences(name, x.Operand);
            case Expr.LogicalNot x: return MaxPathOccurrences(name, x.Operand);
            case Expr.GreaterThan x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.LessThan x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.GreaterOrEqual x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.LessOrEqual x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.Equal x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.NotEqual x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.ResultPipe x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.ResultMapErrorPipe x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);

            default:
                return MaxPathOccurrencesAggregates(name, e);
        }
    }

    private static int MaxPathOccurrencesAggregates(string name, Expr e)
    {
        switch (e)
        {
            case Expr.Cons cons:
                return MaxPathOccurrences(name, cons.Head) + MaxPathOccurrences(name, cons.Tail);

            case Expr.Await aw:
                return MaxPathOccurrences(name, aw.Task);

            case Expr.TupleLit t:
                return SumOccurrences(name, t.Elements);
            case Expr.ListLit lst:
                return SumOccurrences(name, lst.Elements);

            case Expr.RecordLit rec:
                {
                    int total = 0;
                    foreach (var (_, fv) in rec.Fields)
                    {
                        total += MaxPathOccurrences(name, fv);
                    }

                    return total;
                }

            case Expr.RecordUpdate ru:
                {
                    int total = MaxPathOccurrences(name, ru.Target);
                    foreach (var (_, uv) in ru.Updates)
                    {
                        total += MaxPathOccurrences(name, uv);
                    }

                    return total;
                }

            default:
                // Unmodeled node — decline conservatively.
                return OccEscape;
        }
    }

    private static int SumOccurrences(string name, IReadOnlyList<Expr> exprs)
    {
        int total = 0;
        foreach (var e in exprs)
        {
            total += MaxPathOccurrences(name, e);
        }

        return total;
    }

    private static bool PatternBinds(Pattern p, string name)
    {
        switch (p)
        {
            case Pattern.Var v:
                return string.Equals(v.Name, name, StringComparison.Ordinal);
            case Pattern.Constructor c:
                foreach (var sub in c.Patterns)
                {
                    if (PatternBinds(sub, name))
                    {
                        return true;
                    }
                }

                return false;
            case Pattern.Tuple t:
                foreach (var sub in t.Elements)
                {
                    if (PatternBinds(sub, name))
                    {
                        return true;
                    }
                }

                return false;
            case Pattern.Cons cons:
                return PatternBinds(cons.Head, name) || PatternBinds(cons.Tail, name);
            default:
                return false;
        }
    }

    /// <summary>
    /// Records saturated direct call sites and marks any function name that appears in a non-call-head
    /// position (bare value, partial/over-application) as escaped.
    /// </summary>
    private void CollectCallsAndEscapes(
        Expr e,
        FuncKey? enclosing,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        switch (e)
        {
            case Expr.Call:
                CollectCallsAndEscapesCall(e, enclosing, scope);
                return;

            case Expr.Var v:
                CollectFunctionValueEscape(v, scope);
                return;

            case Expr.QualifiedVar qv:
                CollectQualifiedFunctionValueEscape(qv);
                return;

            case Expr.If i:
                CollectCallsAndEscapes(i.Cond, enclosing, scope);
                CollectCallsAndEscapes(i.Then, enclosing, scope);
                CollectCallsAndEscapes(i.Else, enclosing, scope);
                return;

            case Expr.Match m:
                CollectCallsAndEscapesMatch(m, enclosing, scope);
                return;

            case Expr.Let l when TryCollectPartialFoldBinding(l, enclosing, scope):
                return;

            case Expr.Let l:
                WalkBindingValue(l, l.Name, l.Value, enclosing, scope);
                CollectCallsAndEscapes(l.Body, enclosing, ExtendFuncScope(scope, l, l.Name));
                return;

            case Expr.LetResult lr:
                WalkBindingValue(lr, lr.Name, lr.Value, enclosing, scope);
                CollectCallsAndEscapes(lr.Body, enclosing, ExtendFuncScope(scope, lr, lr.Name));
                return;

            case Expr.LetRecursive lrec:
                CollectCallsAndEscapesRecursiveBinding(lrec, enclosing, scope);
                return;

            case RecursiveGroupExpr group:
                CollectCallsAndEscapesRecursiveGroup(group, enclosing, scope);
                return;

            case Expr.Lambda lam:
                CollectCallsAndEscapes(lam.Body, enclosing, RemoveFuncNames(scope, [lam.ParamName]));
                return;

            case Expr.Perform perform: CollectCallsAndEscapes(perform.Operation, enclosing, scope); return;

            case Expr.Handle handle:
                CollectCallsAndEscapesHandle(handle, enclosing, scope);
                return;

            default:
                CollectCallsAndEscapesOperators(e, enclosing, scope);
                return;
        }
    }

    private void CollectQualifiedFunctionValueEscape(Expr.QualifiedVar variable)
    {
        if (ResolveSpecializableCalleeName(variable) is { } qualifiedName
            && _maNameIndex.TryGetValue(qualifiedName, out FuncKey function))
        {
            MarkFunctionEscaped(function);
        }
    }

    private void CollectCallsAndEscapesHandle(
        Expr.Handle handle,
        FuncKey? enclosing,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        CollectCallsAndEscapes(handle.Body, enclosing, scope);
        foreach (HandlerArm arm in handle.Arms)
        {
            var binders = new HashSet<string>(StringComparer.Ordinal);
            foreach (Pattern parameter in arm.Parameters)
            {
                CollectPatternBinders(parameter, binders);
            }

            CollectCallsAndEscapes(
                arm.Body,
                enclosing,
                RemoveFuncNames(scope, binders));
        }
    }

    private void CollectFunctionValueEscape(
        Expr.Var variable,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        if (scope.TryGetValue(variable.Name, out FuncKey key))
        {
            MarkFunctionEscaped(key);
        }
    }

    private void CollectCallsAndEscapesRecursiveBinding(
        Expr.LetRecursive binding,
        FuncKey? enclosing,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        IReadOnlyDictionary<string, FuncKey> recursiveScope =
            ExtendFuncScope(scope, binding, binding.Name);
        WalkBindingValue(binding, binding.Name, binding.Value, enclosing, recursiveScope);
        CollectCallsAndEscapes(binding.Body, enclosing, recursiveScope);
    }

    private void CollectCallsAndEscapesRecursiveGroup(
        RecursiveGroupExpr group,
        FuncKey? enclosing,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        var groupScope = new Dictionary<string, FuncKey>(scope, StringComparer.Ordinal);
        for (int i = 0; i < group.Bindings.Count; i++)
        {
            (string name, _) = group.Bindings[i];
            FuncKey key = GetRecursiveGroupMemberKey(group, i);
            if (_maFuncs.ContainsKey(key))
            {
                groupScope[name] = key;
            }
            else
            {
                groupScope.Remove(name);
            }
        }

        for (int i = 0; i < group.Bindings.Count; i++)
        {
            (string name, Expr value) = group.Bindings[i];
            WalkBindingValue(GetRecursiveGroupMemberKey(group, i), name, value, groupScope);
        }

        CollectCallsAndEscapes(group.Body, enclosing, groupScope);
    }

    private void CollectCallsAndEscapesCall(
        Expr e,
        FuncKey? enclosing,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        var args = new List<Expr>();
        var root = CollectCallArgs(e, args);
        string? calleeName = root switch
        {
            Expr.Var v => v.Name,
            Expr.QualifiedVar qv => ResolveSpecializableCalleeName(qv),
            _ => null,
        };
        if (calleeName is not null
            && TryResolveFunctionKey(root, calleeName, scope) is { } calleeKey
            && _maFuncs.TryGetValue(calleeKey, out var callee)
            && args.Count == callee.Params.Count)
        {
            // A complete, saturated call to a known function: record it and recurse into
            // the arguments only (the head is accounted for, not an escape).
            if (!_maCallSites.TryGetValue(calleeKey, out var list))
            {
                list = new List<MoveCallSite>();
                _maCallSites[calleeKey] = list;
            }

            list.Add(new MoveCallSite(
                enclosing,
                args,
                Enumerable.Repeat(scope, args.Count).ToList()));
            foreach (var a in args)
            {
                CollectCallsAndEscapes(a, enclosing, scope);
            }

            return;
        }

        RecordIncompleteCallCensus(root, calleeName, scope);

        // Not a complete saturated call to a known function: fall through to the generic
        // walk, which will surface any known-function name as an escape.
        var call = (Expr.Call)e;
        CollectCallsAndEscapes(call.Func, enclosing, scope);
        CollectCallsAndEscapes(call.Arg, enclosing, scope);
    }

    private void RecordIncompleteCallCensus(
        Expr root,
        string? calleeName,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        if (calleeName is null)
        {
            return;
        }

        if (TryResolveFunctionKey(root, calleeName, scope) is { } incompleteKey
            && _maFuncs.ContainsKey(incompleteKey))
        {
            MarkFunctionEscaped(
                incompleteKey,
                FunctionCallCensusCause.IncompleteApplication);
            return;
        }

        if (!_maAmbiguous.Contains(calleeName))
        {
            return;
        }

        foreach ((FuncKey key, string name) in _maKeyName)
        {
            if (string.Equals(name, calleeName, StringComparison.Ordinal))
            {
                AddCallCensusCause(key, FunctionCallCensusCause.AmbiguousResolution);
            }
        }
    }

    // `let g = f(partialArgs) in body` where f is a known function and g is used ONLY as saturated
    // completing calls `g(moreArgs)`. Resolving g to f's completed call sites makes f fully visible
    // (its accumulator can then be proven uniquely owned) instead of escaping as a partially-applied
    // value. Sound by construction, fail-closed: any use of g that is not a completing call leaves f to
    // escape via the normal walk. Returns true when the binding was resolved and handled here.
    private bool TryCollectPartialFoldBinding(
        Expr.Let l,
        FuncKey? enclosing,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        if (_maAmbiguous.Contains(l.Name))
        {
            return false;
        }

        var partialArgs = new List<Expr>();
        var root = CollectCallArgs(l.Value, partialArgs);
        if (root is not Expr.Var f
            || !scope.TryGetValue(f.Name, out var fKey)
            || !_maFuncs.TryGetValue(fKey, out var callee)
            || partialArgs.Count == 0
            || partialArgs.Count >= callee.Params.Count)
        {
            return false;
        }

        int neededMore = callee.Params.Count - partialArgs.Count;
        var completing = new List<(List<Expr> Args, IReadOnlyDictionary<string, FuncKey> Scope)>();
        IReadOnlyDictionary<string, FuncKey> bodyScope = ExtendFuncScope(scope, l, l.Name);
        if (!AllUsesAreCompletingCalls(l.Body, l.Name, neededMore, completing, bodyScope))
        {
            return false;
        }

        // Record f's now-visible saturated call sites (partial args ++ each completing call's args).
        if (!_maCallSites.TryGetValue(fKey, out var sites))
        {
            sites = new List<MoveCallSite>();
            _maCallSites[fKey] = sites;
        }

        foreach (var completion in completing)
        {
            var combined = new List<Expr>(partialArgs);
            combined.AddRange(completion.Args);
            var argumentScopes = Enumerable.Repeat(scope, partialArgs.Count).ToList();
            argumentScopes.AddRange(Enumerable.Repeat(completion.Scope, completion.Args.Count));
            sites.Add(new MoveCallSite(enclosing, combined, argumentScopes));
        }

        // The partial args live in the binding value (not walked below); account for them. The body
        // walk handles the completing-call arguments and everything else — g is not a known function,
        // so it never escapes anything.
        foreach (var a in partialArgs)
        {
            CollectCallsAndEscapes(a, enclosing, scope);
        }

        CollectCallsAndEscapes(l.Body, enclosing, bodyScope);
        return true;
    }

    // Fail-closed: true only when every occurrence of g in e is the root of a saturated completing call
    // g(exactly neededMore args); each call's arg list is collected into completing. Any bare use, wrong
    // arity, closure capture, shadowing binder, or unhandled node yields false.
    private bool AllUsesAreCompletingCalls(
        Expr e,
        string g,
        int neededMore,
        List<(List<Expr> Args, IReadOnlyDictionary<string, FuncKey> Scope)> completing,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        switch (e)
        {
            case Expr.IntLit or Expr.BigIntLit or Expr.UIntLit or Expr.FloatLit
                or Expr.StrLit or Expr.BoolLit or Expr.QualifiedVar:
                return true;

            case Expr.Var v:
                return !string.Equals(v.Name, g, StringComparison.Ordinal);

            case Expr.Call:
                return CallUsesGOnlyAsCompleting(e, g, neededMore, completing, scope);

            case Expr.If i:
                return AllUsesAreCompletingCalls(i.Cond, g, neededMore, completing, scope)
                    && AllUsesAreCompletingCalls(i.Then, g, neededMore, completing, scope)
                    && AllUsesAreCompletingCalls(i.Else, g, neededMore, completing, scope);

            case Expr.Let l:
                return CompletingLetLike(l, l.Name, l.Value, l.Body, recursive: false, g, neededMore, completing, scope);
            case Expr.LetResult lr:
                return CompletingLetLike(lr, lr.Name, lr.Value, lr.Body, recursive: false, g, neededMore, completing, scope);
            case Expr.LetRecursive lc:
                return CompletingLetLike(lc, lc.Name, lc.Value, lc.Body, recursive: true, g, neededMore, completing, scope);

            case Expr.Lambda lam:
                return string.Equals(lam.ParamName, g, StringComparison.Ordinal) || !MentionsVar(lam.Body, g);

            case Expr.Match m:
                return CompletingMatch(m, g, neededMore, completing, scope);

            case Expr.TupleLit t:
                return t.Elements.All(x => AllUsesAreCompletingCalls(x, g, neededMore, completing, scope));
            case Expr.ListLit ls:
                return ls.Elements.All(x => AllUsesAreCompletingCalls(x, g, neededMore, completing, scope));

            case Expr.Cons cons:
                return AllUsesAreCompletingCalls(cons.Head, g, neededMore, completing, scope)
                    && AllUsesAreCompletingCalls(cons.Tail, g, neededMore, completing, scope);

            case Expr.Await aw:
                return AllUsesAreCompletingCalls(aw.Task, g, neededMore, completing, scope);

            case Expr.BitwiseNot bn:
                return AllUsesAreCompletingCalls(bn.Operand, g, neededMore, completing, scope);

            case Expr.LogicalNot logicalNot:
                return AllUsesAreCompletingCalls(logicalNot.Operand, g, neededMore, completing, scope);

            default:
                return CompletingBinary(e, g, neededMore, completing, scope);
        }
    }

    private FuncKey? TryResolveFunctionKey(
        Expr root,
        string calleeName,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        IReadOnlyDictionary<string, FuncKey> source =
            root is Expr.QualifiedVar ? _maNameIndex : scope;
        return source.TryGetValue(calleeName, out FuncKey key) ? key : null;
    }

    private bool CallUsesGOnlyAsCompleting(
        Expr call,
        string g,
        int neededMore,
        List<(List<Expr> Args, IReadOnlyDictionary<string, FuncKey> Scope)> completing,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        var args = new List<Expr>();
        var root = CollectCallArgs(call, args);
        if (root is Expr.Var rv && string.Equals(rv.Name, g, StringComparison.Ordinal))
        {
            // g at a call head: it must be a saturated completing call, and its args must not re-use g.
            if (args.Count != neededMore)
            {
                return false;
            }

            if (!args.All(a => AllUsesAreCompletingCalls(a, g, neededMore, completing, scope)))
            {
                return false;
            }

            completing.Add((args, scope));
            return true;
        }

        return AllUsesAreCompletingCalls(root, g, neededMore, completing, scope)
            && args.All(a => AllUsesAreCompletingCalls(a, g, neededMore, completing, scope));
    }

    private bool CompletingLetLike(
        Expr binder,
        string boundName,
        Expr value,
        Expr body,
        bool recursive,
        string g,
        int neededMore,
        List<(List<Expr> Args, IReadOnlyDictionary<string, FuncKey> Scope)> completing,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        if (string.Equals(boundName, g, StringComparison.Ordinal))
        {
            return false;
        }

        IReadOnlyDictionary<string, FuncKey> bodyScope = ExtendFuncScope(scope, binder, boundName);
        IReadOnlyDictionary<string, FuncKey> valueScope = recursive ? bodyScope : scope;
        return AllUsesAreCompletingCalls(value, g, neededMore, completing, valueScope)
            && AllUsesAreCompletingCalls(body, g, neededMore, completing, bodyScope);
    }

    private bool CompletingMatch(
        Expr.Match m,
        string g,
        int neededMore,
        List<(List<Expr> Args, IReadOnlyDictionary<string, FuncKey> Scope)> completing,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        if (!AllUsesAreCompletingCalls(m.Value, g, neededMore, completing, scope))
        {
            return false;
        }

        foreach (var c in m.Cases)
        {
            var binders = new HashSet<string>(StringComparer.Ordinal);
            CollectPatternBinders(c.Pattern, binders);
            IReadOnlyDictionary<string, FuncKey> armScope = RemoveFuncNames(scope, binders);
            if (PatternBinds(c.Pattern, g)
                || !AllUsesAreCompletingCalls(c.Body, g, neededMore, completing, armScope)
                || (c.Guard is not null
                    && !AllUsesAreCompletingCalls(c.Guard, g, neededMore, completing, armScope)))
            {
                return false;
            }
        }

        return true;
    }

    private bool CompletingBinary(
        Expr e,
        string g,
        int neededMore,
        List<(List<Expr> Args, IReadOnlyDictionary<string, FuncKey> Scope)> completing,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        (Expr Left, Expr Right)? ops = e switch
        {
            Expr.Add x => (x.Left, x.Right),
            Expr.Subtract x => (x.Left, x.Right),
            Expr.Multiply x => (x.Left, x.Right),
            Expr.Divide x => (x.Left, x.Right),
            Expr.Modulo x => (x.Left, x.Right),
            Expr.BitwiseAnd x => (x.Left, x.Right),
            Expr.BitwiseOr x => (x.Left, x.Right),
            Expr.BitwiseXor x => (x.Left, x.Right),
            Expr.ShiftLeft x => (x.Left, x.Right),
            Expr.ShiftRight x => (x.Left, x.Right),
            Expr.GreaterThan x => (x.Left, x.Right),
            Expr.LessThan x => (x.Left, x.Right),
            Expr.GreaterOrEqual x => (x.Left, x.Right),
            Expr.LessOrEqual x => (x.Left, x.Right),
            Expr.Equal x => (x.Left, x.Right),
            Expr.NotEqual x => (x.Left, x.Right),
            Expr.ResultPipe x => (x.Left, x.Right),
            Expr.ResultMapErrorPipe x => (x.Left, x.Right),
            _ => null,
        };

        if (ops is not { } b)
        {
            return !MentionsVar(e, g); // unmodeled node: fail-closed
        }

        return AllUsesAreCompletingCalls(b.Left, g, neededMore, completing, scope)
            && AllUsesAreCompletingCalls(b.Right, g, neededMore, completing, scope);
    }

    private void CollectCallsAndEscapesMatch(
        Expr.Match m,
        FuncKey? enclosing,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        CollectCallsAndEscapes(m.Value, enclosing, scope);
        foreach (var c in m.Cases)
        {
            var binders = new HashSet<string>(StringComparer.Ordinal);
            CollectPatternBinders(c.Pattern, binders);
            IReadOnlyDictionary<string, FuncKey> armScope = RemoveFuncNames(scope, binders);
            CollectCallsAndEscapes(c.Body, enclosing, armScope);
            if (c.Guard is not null)
            {
                CollectCallsAndEscapes(c.Guard, enclosing, armScope);
            }
        }
    }

    private void CollectCallsAndEscapesOperators(
        Expr e,
        FuncKey? enclosing,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        switch (e)
        {
            case Expr.Add x: CollectBinary(x.Left, x.Right, enclosing, scope); return;
            case Expr.Subtract x: CollectBinary(x.Left, x.Right, enclosing, scope); return;
            case Expr.Multiply x: CollectBinary(x.Left, x.Right, enclosing, scope); return;
            case Expr.Divide x: CollectBinary(x.Left, x.Right, enclosing, scope); return;
            case Expr.Modulo x: CollectBinary(x.Left, x.Right, enclosing, scope); return;
            case Expr.BitwiseAnd x: CollectBinary(x.Left, x.Right, enclosing, scope); return;
            case Expr.BitwiseOr x: CollectBinary(x.Left, x.Right, enclosing, scope); return;
            case Expr.BitwiseXor x: CollectBinary(x.Left, x.Right, enclosing, scope); return;
            case Expr.ShiftLeft x: CollectBinary(x.Left, x.Right, enclosing, scope); return;
            case Expr.ShiftRight x: CollectBinary(x.Left, x.Right, enclosing, scope); return;
            case Expr.BitwiseNot x: CollectCallsAndEscapes(x.Operand, enclosing, scope); return;
            case Expr.LogicalNot x: CollectCallsAndEscapes(x.Operand, enclosing, scope); return;
            case Expr.GreaterThan x: CollectBinary(x.Left, x.Right, enclosing, scope); return;
            case Expr.LessThan x: CollectBinary(x.Left, x.Right, enclosing, scope); return;
            case Expr.GreaterOrEqual x: CollectBinary(x.Left, x.Right, enclosing, scope); return;
            case Expr.LessOrEqual x: CollectBinary(x.Left, x.Right, enclosing, scope); return;
            case Expr.Equal x: CollectBinary(x.Left, x.Right, enclosing, scope); return;
            case Expr.NotEqual x: CollectBinary(x.Left, x.Right, enclosing, scope); return;

            default:
                CollectCallsAndEscapesAggregates(e, enclosing, scope);
                return;
        }
    }

    private void CollectCallsAndEscapesAggregates(
        Expr e,
        FuncKey? enclosing,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        switch (e)
        {
            case Expr.ResultPipe x: CollectBinary(x.Left, x.Right, enclosing, scope); return;
            case Expr.ResultMapErrorPipe x: CollectBinary(x.Left, x.Right, enclosing, scope); return;

            case Expr.Cons cons:
                CollectBinary(cons.Head, cons.Tail, enclosing, scope);
                return;

            case Expr.Await aw:
                CollectCallsAndEscapes(aw.Task, enclosing, scope);
                return;

            case Expr.TupleLit t:
                foreach (var el in t.Elements)
                {
                    CollectCallsAndEscapes(el, enclosing, scope);
                }

                return;

            case Expr.ListLit lst:
                foreach (var el in lst.Elements)
                {
                    CollectCallsAndEscapes(el, enclosing, scope);
                }

                return;

            case Expr.RecordLit rec:
                foreach (var (_, fv) in rec.Fields)
                {
                    CollectCallsAndEscapes(fv, enclosing, scope);
                }

                return;

            case Expr.RecordUpdate ru:
                CollectCallsAndEscapes(ru.Target, enclosing, scope);
                foreach (var (_, uv) in ru.Updates)
                {
                    CollectCallsAndEscapes(uv, enclosing, scope);
                }

                return;

            default:
                return;
        }
    }

    private void CollectBinary(
        Expr left,
        Expr right,
        FuncKey? enclosing,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        CollectCallsAndEscapes(left, enclosing, scope);
        CollectCallsAndEscapes(right, enclosing, scope);
    }

    /// <summary>
    /// Walks a binding's normalized value while attributing its calls to the registered function
    /// identity. This keeps module-alias substitutions visible to the census without losing the
    /// original <see cref="FuncKey"/> when normalization copied nested binder nodes.
    /// </summary>
    private void WalkBindingValue(
        Expr binder,
        string name,
        Expr value,
        FuncKey? enclosing,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        WalkBindingValueCore(TryResolveBindingFunction(binder, value), value, enclosing, scope);
    }

    private void WalkBindingValue(
        FuncKey functionKey,
        string name,
        Expr value,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        WalkBindingValueCore(
            TryResolveBindingFunction(functionKey, value),
            value,
            functionKey,
            scope);
    }

    private void WalkBindingValueCore(
        (
            FuncKey Key,
            IReadOnlyList<string> Params,
            Expr Body,
            (string RecursiveName, IReadOnlyList<string> Outer, string Acc)? Nested)? function,
        Expr value,
        FuncKey? enclosing,
        IReadOnlyDictionary<string, FuncKey> scope)
    {
        if (function is { } resolved)
        {
            IReadOnlyDictionary<string, FuncKey> bodyScope;
            if (resolved.Nested is { } nested)
            {
                bodyScope = RemoveFuncNames(scope, nested.Outer);
                FuncKey recursiveKey = _maNestedRecursive[resolved.Key].Recursive;
                bodyScope = SetFuncName(bodyScope, nested.RecursiveName, recursiveKey);
                bodyScope = RemoveFuncNames(bodyScope, [nested.Acc]);
                _maFunctionScopes[recursiveKey] = bodyScope;
                _maProvenanceBodies[recursiveKey] = resolved.Body;
            }
            else
            {
                bodyScope = RemoveFuncNames(scope, resolved.Params);
            }

            _maFunctionScopes[resolved.Key] = bodyScope;
            _maProvenanceBodies[resolved.Key] = resolved.Body;
            CollectCallsAndEscapes(resolved.Body, resolved.Key, bodyScope);
        }
        else
        {
            CollectCallsAndEscapes(StripOrSelf(value), enclosing, scope);
        }
    }

    private (
        FuncKey Key,
        IReadOnlyList<string> Params,
        Expr Body,
        (string RecursiveName, IReadOnlyList<string> Outer, string Acc)? Nested)?
        TryResolveBindingFunction(Expr binder, Expr value)
    {
        if (!TryResolveBindingFunctionKey(binder, out FuncKey key))
        {
            return null;
        }

        return TryResolveBindingFunction(key, value);
    }

    private (
        FuncKey Key,
        IReadOnlyList<string> Params,
        Expr Body,
        (string RecursiveName, IReadOnlyList<string> Outer, string Acc)? Nested)?
        TryResolveBindingFunction(FuncKey key, Expr value)
    {
        if (!_maFuncs.ContainsKey(key) || StripOrSelf(value) is not Expr.Lambda lambda)
        {
            return null;
        }

        if (TryGetNestedRecursiveReturnShape(
            lambda,
            out var outer,
            out string acc,
            out _,
            out string recursiveName,
            out Expr innerBody))
        {
            return (key, new List<string>(outer) { acc }, innerBody, (recursiveName, outer, acc));
        }

        return (key, CollectLambdaParams(lambda), GetInnermostBody(lambda), null);
    }

    private bool TryResolveBindingFunctionKey(Expr binder, out FuncKey key)
    {
        key = GetCanonicalFuncKey(binder);
        return _maFuncs.ContainsKey(key);
    }

    private void MarkFunctionEscaped(
        FuncKey key,
        FunctionCallCensusCause cause = FunctionCallCensusCause.EscapedAsValue)
    {
        _maEscaped.Add(key);
        AddCallCensusCause(key, cause);
    }

    private void AddCallCensusCause(FuncKey key, FunctionCallCensusCause cause)
    {
        _maCallCensusCauses[key] = _maCallCensusCauses.GetValueOrDefault(key) | cause;
    }

    private static IReadOnlyDictionary<string, FuncKey> RemoveFuncNames(
        IReadOnlyDictionary<string, FuncKey> scope,
        IEnumerable<string> names)
    {
        var next = new Dictionary<string, FuncKey>(scope, StringComparer.Ordinal);
        foreach (string name in names)
        {
            next.Remove(name);
        }

        return next;
    }

    private static IReadOnlyDictionary<string, FuncKey> SetFuncName(
        IReadOnlyDictionary<string, FuncKey> scope,
        string name,
        FuncKey key)
    {
        return new Dictionary<string, FuncKey>(scope, StringComparer.Ordinal)
        {
            [name] = key,
        };
    }
}
