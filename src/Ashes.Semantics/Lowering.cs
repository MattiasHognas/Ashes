using System.Diagnostics;
using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    /// <summary>The inferred type of a source span, recorded during lowering so the language server can
    /// answer hover and type-at-position queries.</summary>
    /// <param name="Span">The source range this type covers.</param>
    /// <param name="Name">The bound name at the span, when the span is a named binding or reference; otherwise null.</param>
    /// <param name="Type">The inferred type of the expression occupying the span.</param>
    /// <param name="Constraints"></param>
    /// <param name="ParameterNames">Source parameter names for a directly declared function, in curried order.</param>
    /// <param name="IsParameter">Whether the named span declares or references a function parameter.</param>
    public readonly record struct HoverTypeInfo(
        TextSpan Span,
        string? Name,
        TypeRef Type,
        IReadOnlyList<TraitConstraint>? Constraints = null,
        IReadOnlyList<string>? ParameterNames = null,
        bool IsParameter = false);

    /// <summary>Declared affine ownership metadata exposed to tooling without reimplementing FFI semantics.</summary>
    public readonly record struct ExternalOwnershipInfo(
        bool IsResourceType,
        string? Destructor,
        IReadOnlyList<string> ParameterOwnerships,
        bool ReturnsOwnedResource);

    private readonly Diagnostics _diag;
    private readonly LoweringConfiguration _configuration;
    private readonly IReadOnlySet<string> _importedStdModules;
    private readonly bool _enableInferredTraitElaboration;
    private readonly bool _collectInferredTraitElaboration;
    private readonly bool _enableTraitValidationPass;
    private readonly bool _emitTraitDictionaries;
    private readonly bool _isTraitValidationSubpass;
    private int _nextTempSlot;
    private int _nextLocalSlot;
    private int _nextTypeVar;
    private int _nextLambdaId;
    private int _nextLabelId;

    private readonly List<IrInst> _inst = new();
    private readonly List<IrFunction> _funcs = new();
    private readonly HashSet<IrInst.CallClosure> _borrowedArgumentCalls = new(ReferenceEqualityComparer.Instance);

    private bool _hasDeferredTupleMaterializations;
    private readonly List<IrStringLiteral> _strings = new();
    private readonly Dictionary<string, string> _stringIntern = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _localNames = new();
    private readonly Dictionary<int, TypeRef> _localTypes = new();

    private bool _usesPrintInt;
    private bool _usesPrintStr;
    private bool _usesPrintBool;
    private bool _usesConcatStr;
    private bool _usesClosures;
    private bool _usesAsync;
    // True while lowering the body of an async task that is being built as a suspending coroutine.
    // Inside such a body an `await` is a suspension point (IrInst.AwaitTask, split by
    // StateMachineTransform), not a blocking driver (IrInst.RunTask). Outside any coroutine body an
    // `await` still lowers to a blocking RunTask, preserving today's eager semantics.
    private bool _inCoroutineBody;

    // The `async` intrinsic binding, created once at root-scope setup and re-seeded into every lambda
    // scope so a function body can itself build a task with `async(E)`.
    private Binding.Intrinsic? _asyncBinding;
    private readonly List<HoverTypeInfo> _hoverTypes = [];
    private readonly Dictionary<int, IReadOnlyList<string>> _hoverParameterNamesByDefinitionStart = [];
    private readonly HashSet<int> _hoverParameterDefinitionStarts = [];

    // Source location tracking for debug info
    private string? _currentFilePath;
    private int[]? _lineStarts;
    private int _sourceLength;
    private Expr? _currentSourceExpr;
    private IReadOnlyList<(string FilePath, int StartOffset, int EndOffset)>? _moduleOffsets;
    private int[][]? _moduleLineStarts;
    private IReadOnlyDictionary<string, SourceFunctionName>? _functionSourceNames;
    private IReadOnlyDictionary<string, ModuleProvenance>? _moduleProvenanceByPath;

    private readonly bool _hasAshesIO;
    private readonly IReadOnlyDictionary<string, string> _moduleAliases;
    // Maps a module name to the ADT constructor names its own `type` declarations introduce, so a
    // qualified reference (`alias.Ctor`) can be scoped to the module the alias actually resolves to.
    // Constructors themselves stay in the single global `_constructorSymbols` registry (types are
    // hoisted unqualified into the combined source); this only records which module a name may be
    // qualified through.
    private readonly IReadOnlyDictionary<string, IReadOnlySet<string>> _constructorModulesByName;
    private readonly List<string> _diagnosticContext = [];
    private readonly Stack<TextSpan> _diagnosticSpans = new();
    private readonly Stack<string> _diagnosticCodes = new();





    private TcoContext? _tcoCtx;

    // Async tail-recursive loops: a `let recursive` helper defined inside a coroutine body whose own
    // body awaits is lowered as a task-returning closure wrapping a *transparent* coroutine (raw body
    // result, no Ok-wrap), so its awaits become suspend points on the enclosing run instead of nested
    // blocking scheduler runs. Self tail calls restart that coroutine in place (store params + jump),
    // and every saturated call site awaits the returned task implicitly, keeping the helper's source
    // type (its body's type) at the call site.
    private sealed record HelperCoroutineInfo(string Name, List<string> ParamNames, Expr Body);
    private HelperCoroutineInfo? _pendingHelperCoroutine;
    private readonly Dictionary<string, int> _coroutineHelperArity = new(StringComparer.Ordinal);
    private int _nextAsyncLoopId;

    private readonly Stack<Dictionary<string, Binding>> _scopes = new();


    // Stack of ownership scopes, parallel to _scopes.
    // Each scope level tracks owned values introduced at that level.
    private readonly Stack<Dictionary<string, OwnershipInfo>> _ownershipScopes = new();

    // Arena watermark local slot pairs (cursor, end) for each ownership scope.
    // SaveArenaState is emitted at scope entry; RestoreArenaState may be emitted
    // at scope exit when the scope's result is a copy type (no heap escapes).
    private readonly Stack<(int CursorSlot, int EndSlot)> _arenaWatermarks = new();

    // Alias map for ownership: when `let y = x` and x is owned, y → x.
    // This prevents double-Drop and propagates diagnostics through aliases.
    // Aliases are resolved transitively (y → x → z chains are followed).
    private readonly Dictionary<string, string> _ownershipAliases = new(StringComparer.Ordinal);
    private sealed record PatternBindingPlacementSite(
        int LocalSlot,
        int RootParameterSlot,
        int InsertIndex,
        TypeRef Type,
        PatternBindingOwnershipFact Ownership);

    // Stable ownership facts are joined to emitted binder slots as soon as a pattern is lowered.
    // The semantic owner is therefore available to ordinary scope cleanup and move handling even
    // when the root TCO parameter's physical representation cannot be decided until post-body type
    // resolution. Finalization only selects arena-erased versus runtime-RC markers; it does not
    // rediscover escape behavior from source names or emitted instructions.
    private readonly List<PatternBindingPlacementSite> _patternBindingPlacementSites = [];
    private readonly List<PatternBindingOwnershipDecision> _patternBindingOwnershipDecisions = [];

    internal IReadOnlyList<PatternBindingOwnershipDecision> PatternBindingOwnershipDecisions =>
        _patternBindingOwnershipDecisions
            .OrderBy(decision => decision.Function?.QualifiedName, StringComparer.Ordinal)
            .ThenBy(decision => decision.Function?.SourceName, StringComparer.Ordinal)
            .ThenBy(decision => decision.Function?.DeclarationOffset)
            .ThenBy(decision => decision.BindingOrdinal)
            .ThenBy(decision => decision.LocalSlot)
            .ToList();

    // Closure temp → the resource bindings it captures, with each one's env offset and type. When
    // such a closure is a scope's result the captured resources escape with it; the scope moves them
    // into the closure (a synthesized dropper stored at closure+24 closes them when the closure is
    // dropped) instead of closing them at scope exit, which would be a use-after-close.
    private readonly Dictionary<int, List<(int EnvOffset, string Name, TypeRef Type)>> _closureResourceCaptures = new();

    // In-place reuse. Names of TCO accumulator params that have been made uniquely-owned (deep-copied
    // once at loop entry) and are therefore safe to reuse in place.
    private readonly HashSet<string> _linearReuseNames = new(StringComparer.Ordinal);

    // Available reuse tokens (dead ADT cells converted by DropReuse), innermost last. Each is the
    // token temp, field count, and allocation regime; a same-arity constructor in the arm consumes
    // one through the matching arena/runtime AllocReusing path. See LowerConstructorApplication /
    // LowerMatch.
    private readonly List<ReuseToken> _reuseTokens = new();
    // Source function whose current match arm produced a reuse token. This remains set while the
    // arm body lowers even after its token is consumed, so later constructors in that same body can
    // retain their concrete fresh-allocation fallback. Generated nested functions have a different
    // origin and therefore do not inherit the enclosing arm's reporting context.
    private IrFunctionOrigin? _activeReuseArmOrigin;

    // CO-23 in-place-overwrite guard: see ReuseTokenFieldIsDead in Lowering.Symbols.cs.
    private readonly Dictionary<int, Dictionary<int, (int Slot, int TotalRefs)>> _reuseTokenFieldBindings = new();
    private readonly Dictionary<int, int> _reuseBindingSeenBySlot = new();
    private readonly Dictionary<int, string> _reuseTrackedSlotNames = new();

    private static int CountNameOccurrences(object? node, string name)
    {
        if (node is null or string)
        {
            return 0;
        }

        int count = node is Expr.Var v && string.Equals(v.Name, name, StringComparison.Ordinal) ? 1 : 0;
        if (node is System.Runtime.CompilerServices.ITuple tuple)
        {
            for (int i = 0; i < tuple.Length; i++)
            {
                count += CountNameOccurrences(tuple[i], name);
            }

            return count;
        }

        if (node is System.Collections.IEnumerable seq)
        {
            foreach (var item in seq)
            {
                count += CountNameOccurrences(item, name);
            }

            return count;
        }

        if (node is not (Expr or Pattern or MatchCase))
        {
            return 0;
        }

        foreach (var prop in node.GetType().GetProperties())
        {
            if (prop.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var t = prop.PropertyType;
            if (typeof(Expr).IsAssignableFrom(t)
                || typeof(Pattern).IsAssignableFrom(t)
                || typeof(MatchCase).IsAssignableFrom(t)
                || (typeof(System.Collections.IEnumerable).IsAssignableFrom(t) && t != typeof(string)))
            {
                count += CountNameOccurrences(prop.GetValue(node), name);
            }
        }

        return count;
    }

    /// <summary>Branch adjustment for the CO-23 seen-counters: references in a mutually-exclusive
    /// sibling branch can never execute after a constructor in THIS branch, so they are
    /// pre-credited as seen while the branch lowers and reverted afterwards.</summary>
    private Dictionary<int, int>? BeginExclusiveBranch(IEnumerable<Expr> otherBranches)
    {
        if (_reuseTrackedSlotNames.Count == 0)
        {
            return null;
        }

        // Snapshot the seen-counters: on exit the whole map is restored, rolling back BOTH the
        // sibling credits below AND this branch's own increments — a sibling branch (or code after
        // the join) must not observe references that only execute on this path. Inside the branch,
        // sibling references are pre-credited as seen (they can never execute after a constructor
        // on this path).
        var snapshot = new Dictionary<int, int>(_reuseBindingSeenBySlot);
        foreach (var (slot, name) in _reuseTrackedSlotNames)
        {
            int credit = 0;
            foreach (var other in otherBranches)
            {
                credit += CountNameOccurrences(other, name);
            }

            if (credit > 0)
            {
                _reuseBindingSeenBySlot[slot] = _reuseBindingSeenBySlot.GetValueOrDefault(slot) + credit;
            }
        }

        return snapshot;
    }

    private void EndExclusiveBranch(Dictionary<int, int>? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        _reuseBindingSeenBySlot.Clear();
        foreach (var kv in snapshot)
        {
            _reuseBindingSeenBySlot[kv.Key] = kv.Value;
        }
    }

    // Non-recursive top-level functions, by name → (param names, body). When such a function is
    // called saturated inside a reuse arm (a token is live), the call is inlined so its constructor
    // becomes local and can reuse the dead cell — extending in-place reuse across a helper rebuild
    // like loop(...)(mk(l)(v+n)(r)). Recursion (let rec / RecursiveGroup) is excluded.
    private readonly Dictionary<string, (IReadOnlyList<string> Params, Expr Body)> _inlinableFunctions = new(StringComparer.Ordinal);

    // Non-recursive let-bound functions that perform a parameterized capability operation whose
    // instance depends on their inputs, and for which a provider exists. Inlining them at a concrete
    // call site monomorphizes the body so the operation resolves to the provider (a `needs {Ord(a)}`
    // function called at `Ord(Int)` gets a copy where `Ord.compare` resolves statically).
    private readonly HashSet<string> _capabilityGenericInline = new(StringComparer.Ordinal);

    // Top-level functions specializable for in-place reuse, by name. Two shapes:
    //   • single-parameter recursion: let rec f = given p -> body (LinearParam = p, ArgCount = 1);
    //   • nested-rec-returning: let f = given a -> ... -> (let rec go = given m -> _ in go) — f isn't
    //     itself recursive but returns a recursive single-param function (LinearParam = m, ArgCount =
    //     outer params + 1, the accumulator being the last applied argument), e.g. Map.set.
    // Applied to a uniquely-owned accumulator (the last arg), f is specialized into an f$reuse clone
    // whose recursive parameter (LinearParam) is a linear reuse root, so its match-then-rebuild
    // reuses the node in place and the recursion stays within the reuse-enabled body.
    // Recursive lambda chains whose final parameter can be consumed as a unique accumulator.
    // ArgCount includes any earlier configuration parameters that are forwarded unchanged.
    private readonly Dictionary<string, (Expr.Lambda Lambda, string LinearParam, int ArgCount)> _specializableFunctions = new(StringComparer.Ordinal);
    // Direct recursive functions with configuration parameters are currently specialized only for
    // a fresh call-result composition. Keep them out of loop-entry accumulator specialization,
    // whose established multi-argument support is the nested-recursive-return shape.
    private readonly HashSet<string> _freshCompositionOnlySpecializable = new(StringComparer.Ordinal);

    // Cache of generated reuse specializations: original name → f$reuse function label.
    private readonly Dictionary<string, string> _reuseSpecializations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _reuseSpecializationCaptures =
        new(StringComparer.Ordinal);
    private readonly List<ReuseDecision> _reuseDecisions = [];

    /// <summary>
    /// Reuse decisions retained in deterministic source/function order. This projection prevents
    /// reporting consumers from observing or mutating lowering's working set.
    /// </summary>
    internal IReadOnlyList<ReuseDecision> ReuseDecisions =>
        _reuseDecisions
            .OrderBy(
                decision => decision.Function.Source?.QualifiedName
                    ?? decision.Function.Source?.SourceName
                    ?? decision.Function.CompilerOwner?.Name,
                StringComparer.Ordinal)
            .ThenBy(decision => decision.Function.Source?.DeclarationOffset ?? int.MaxValue)
            .ThenBy(decision => decision.Function.GeneratedLabel, StringComparer.Ordinal)
            .ThenBy(decision => decision.Decision)
            .ThenBy(decision => decision.Mechanism)
            .ThenBy(decision => decision.Candidate?.Kind)
            .ThenBy(decision => decision.Candidate?.SourceName, StringComparer.Ordinal)
            .ThenBy(decision => decision.Candidate?.LocalSlot ?? int.MaxValue)
            .ThenBy(decision => decision.Candidate?.Temp ?? int.MaxValue)
            .ThenBy(decision => decision.TargetFunction, StringComparer.Ordinal)
            .ThenBy(decision => decision.RelatedGeneratedLabel, StringComparer.Ordinal)
            .ThenBy(decision => decision.Location?.FilePath, StringComparer.Ordinal)
            .ThenBy(decision => decision.Location?.Line ?? int.MaxValue)
            .ThenBy(decision => decision.Location?.Column ?? int.MaxValue)
            .ThenBy(decision => decision.Outcome)
            .ThenBy(decision => decision.Reason)
            .ThenBy(decision => decision.MoveSafetyCauses)
            .ThenBy(decision => decision.Layout?.TargetConstructor, StringComparer.Ordinal)
            .ThenBy(decision => decision.Layout?.ProducedFieldCount ?? int.MaxValue)
            .ThenBy(decision => decision.Layout?.RequestedFieldCount ?? int.MaxValue)
            .ThenBy(decision => decision.Layout?.ProducedListCell)
            .ThenBy(decision => decision.Layout?.RequestedListCell)
            .ThenBy(decision => decision.Layout?.RuntimeManagedToken)
            .ThenBy(decision => decision.Layout?.RuntimeManagedAllowed)
            .ThenBy(decision => decision.TokenLifecycle?.TokenTemp ?? int.MaxValue)
            .ThenBy(decision => decision.TokenLifecycle?.SourceValueTemp ?? int.MaxValue)
            .ThenBy(decision => decision.TokenLifecycle?.AllocationTemp ?? int.MaxValue)
            .ThenBy(decision => decision.TokenLifecycle?.FieldCount ?? int.MaxValue)
            .ThenBy(decision => decision.TokenLifecycle?.ListCell)
            .ThenBy(decision => decision.TokenLifecycle?.RuntimeManaged)
            .ThenBy(decision => decision.TokenLifecycle?.TargetConstructor, StringComparer.Ordinal)
            .ThenBy(decision => decision.TokenLifecycle?.FallbackKind)
            .ToList();

    // Stitched names of the data-parallel combinators. The grain-parameterized `mapGrained`/`reduceGrained`
    // are the recursive divide-and-conquer functions whose above-grain split routes through the
    // concrete-result-typed `both` primitive, so a monomorphic specialization at a concrete element type
    // lets `both` genuinely fork (the polymorphic copy runs sequentially). `map`/`reduce` are the grain-1
    // wrappers — a saturated call to one routes to the corresponding grained combinator with grain = 1.
    private static readonly string ParallelModulePrefix = ProjectSupport.SanitizeModuleBindingName("Ashes.Task.Parallel");
    private static readonly string ParallelMapName = ParallelModulePrefix + "_map";
    private static readonly string ParallelReduceName = ParallelModulePrefix + "_reduce";
    private static readonly string ParallelMapGrainedName = ParallelModulePrefix + "_mapGrained";
    private static readonly string ParallelReduceGrainedName = ParallelModulePrefix + "_reduceGrained";

    // The stripped lambda + full arity of each grained parallel combinator (registered when the embedded
    // module is lowered), used to generate a monomorphic self-recursive specialization at each concrete
    // call. Keyed by the grained name; `map`/`reduce` calls are rewritten to the grained form first.
    private readonly Dictionary<string, (Expr.Lambda Lambda, int ArgCount)> _parallelSpecializable = new(StringComparer.Ordinal);

    // Cache of generated parallel specializations: name|concrete-param-types → specialized function label.
    private readonly Dictionary<string, string> _parallelSpecializations = new(StringComparer.Ordinal);

    // Concrete recursive functions whose abstract trait operators can be lowered back to the
    // primitive IR selected at a concrete call site. This retains optimizations that depend on the
    // operator living in the caller's loop body, notably affine string-accumulator growth.
    private readonly Dictionary<string, string> _traitOperatorSpecializations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _traitOperatorSpecializationCaptures =
        new(StringComparer.Ordinal);

    // True while generating a parallel specialization body, so a self-recursive call to the combinator
    // resolves to the specialization's own label (Binding.Self) instead of re-triggering specialization.
    private bool _inParallelSpecialization;

    // f$reuse labels that are "fully reusing": every value they return is below the loop watermark
    // (an AllocReusing result, the scrutinee, or a recursive f$reuse result), with only self-recursion
    // scaffolding (env allocs + self-closures) freshly allocated. Only these allow the loop arena
    // reset — anything else could leave a fresh, above-watermark cell in the result.
    private readonly HashSet<string> _fullyReusingLabels = new(StringComparer.Ordinal);

    // Accumulator names whose specialization is fully reusing, so the loop back-edge may reset the
    // arena: the new accumulator is rewritten in place below the watermark and survives the reset.
    // Membership alone is not sufficient to reset: the actual back-edge argument expression must also
    // be proven address-stable (IsStableAccumulatorExpr) — a name-marked accumulator threaded back
    // through a relocating (declined) entry copy is above the watermark and a plain reset frees it.
    private readonly HashSet<string> _resetSafeAccumulators = new(StringComparer.Ordinal);

    // Reuse-specialized call nodes whose result IS the accumulator (their last argument) rewritten in
    // place — the specialization fully reuses and the accumulator is fully persistent. Recorded by the
    // call Expr's identity so IsStableAccumulatorExpr can tell an in-place rewrite (address-stable when
    // its input is) from a relocating allocation. Reference identity, not name/span keyed.
    private readonly HashSet<Expr> _inPlaceReuseCallExprs = new(ReferenceEqualityComparer.Instance);

    // User folds proven to thread their accumulator through at a stable address: the accumulator is the
    // last curried param, its spec-path entry deep-copy was elided (no relocation on entry), and every
    // tail leaf of the body preserves the accumulator's address (Var acc, an in-place reuse call with a
    // stable acc arg, or a self back-edge with a stable acc arg). Keyed by the fold's definition span
    // (Binding.Self inherits the outer binding's span, so a caller and the fold's self-calls resolve the
    // identical span; any shadowing binder has a distinct span) → the fold's curried parameter count.
    private readonly Dictionary<TextSpan, int> _accStableFolds = new();

    // When generating an f$reuse specialization, the parameter name to treat as a linear
    // (uniquely-owned) reuse root inside the lowered body. Null outside specialization.
    private string? _specializingLinearParam;

    // Set when the linear param above is injected: the IR label of the function that owns it (the
    // recursive reuse function — f$reuse itself, or the inner go inside a nested-rec specialization).
    // That is the function whose return values determine reset-safety, not the outer wrapper.
    private string? _specializingReuseLabel;

    // True while lowering a reuse specialization body, so saturated helper calls inline
    // unconditionally (folding helpers down to constructors rather than leaving uncaptured calls).
    private bool _inSpecialization;

    // Trait-operator specializations may be requested while lowering a reuse specialization, but
    // generating one trait specialization must not recursively specialize its own helper calls.
    // Those calls retain their already-resolved static dictionaries.
    private bool _inTraitOperatorSpecialization;

    // True only while evaluating a tail self-call's successor arguments. A fresh-result,
    // non-recursive helper can be inlined here so its arena graph stays inside the loop window
    // instead of crossing an intermediate call boundary before the back-edge copy/reset.
    private bool _loweringTcoBackEdgeArguments;

    // Nesting depth of LowerLambdaCore (0 = lowering a top-level declaration's value). Used to snapshot
    // the top-level scope so a lazily-generated reuse specialization can resolve the stdlib helper
    // functions it references (Ashes_Map_makeNode, ...) as globals, even though it is generated deep
    // inside a loop body whose scope no longer contains them. See LowerLambdaCore.
    private int _lambdaDepth;
    private Dictionary<string, Binding>[] _topLevelScopeStack = [];

    // Registry of top-level functions with an EMPTY closure environment (no captures), keyed by binding
    // name → (IR label, generalized type scheme). Such a function's closure can be reconstructed anywhere
    // from just its label (a null env), so a reuse specialization — which runs in an isolated scope with no
    // access to the generation-site slots — can CALL it directly (MakeClosure(label, null)) instead of
    // inlining it. This lets non-allocating helpers (e.g. an AVL height/max reader) stay out of the
    // reuse-inline set, keeping the specialized function small. See LowerVar's specialization fallback.
    private readonly Dictionary<string, (string Label, TypeScheme Scheme)> _topLevelFunctionRefs = new(StringComparer.Ordinal);

    // Top-level bindings each inline candidate's body reads, computed once per candidate. An inline
    // site checks these against the scope it would splice the body into.
    private readonly Dictionary<string, HashSet<string>> _inlinableBodyExternalReferences = new(StringComparer.Ordinal);
    // Compatibility reverse lookup from a declaration-site label to its source name. Exact ownership
    // consumers use _functionKeyByLabel; this remains for older lowering paths whose labels do not yet
    // retain a binder identity.
    private readonly Dictionary<string, string> _functionNameByLabel = new(StringComparer.Ordinal);
    // Exact ownership-analysis identity for every declaration-site label where lowering still has the
    // original binder node. This is the primary summary bridge; the name map above remains a
    // compatibility fallback for labels produced by paths that do not yet retain a binder identity.
    private readonly Dictionary<string, FuncKey> _functionKeyByLabel = new(StringComparer.Ordinal);
    // Whether a lambda's OWN compiled body (keyed by its own label) was proven by its canonical
    // lowered-temp ownership fact to produce a RuntimeManaged result — populated
    // once per LowerLambdaCore invocation, for EVERY lambda (named or anonymous), not just registered
    // top-level functions. This is deliberately NOT derived from FunctionOwnershipSummary.ResultProvenance:
    // ResultProvenance classifies from AST shape alone, before lowering, so it cannot see a result that
    // ends up RuntimeManaged only because of a lowering-time representation decision outside its model
    // (a TCO loop's own accumulator-representation analysis promoting a parameter to RC; a closure's own
    // capture analysis choosing RC for its environment) — exactly the gap that caused
    // Linux_backend_llvm_runtime_rc_unreturned_TCO_parameter_memory_should_plateau to leak linearly with
    // iteration count when this table was first replaced by a ResultProvenance-based guess: a function
    // whose TCO accumulator is actually RC, but whose AST shape isn't one ClassifyExpressionProvenance
    // recognizes, would get RcEligible=false — treated as "verified not RC", when it is actually RC via a
    // path this pre-lowering classifier cannot see; LowerVarUnbound/LowerVarBound's Binding.Self case
    // reconstruct a closure reference to this already-compiled function from a different scope and must
    // bake in this SAME, accurate fact, not a possibly-wrong AST-shape guess.
    private readonly Dictionary<string, bool> _bodyRuntimeManagedByLabel = new(StringComparer.Ordinal);
    // Curried application labels whose argument is normalized into an independent RC graph at the
    // admitted TCO loop entry. A consumed runtime argument may be released after the saturated chain.
    private readonly HashSet<string> _runtimeNormalizedFunctionArgumentLabels = new(StringComparer.Ordinal);
    private readonly Dictionary<int, int> _pendingRuntimeArgumentFlags = [];
    // Curried functions return the next lambda as a closure. Preserve that statically known label chain
    // so a saturated direct call can reach the innermost function's result provenance without treating
    // an arbitrary closure value as known.
    private readonly Dictionary<string, string> _functionReturnedClosureLabels = new(StringComparer.Ordinal);
    // Local slots that are exact aliases of a statically known function closure. Slot identity keeps
    // shadowing precise while allowing a direct let alias to retain call-result ownership provenance.
    private readonly Dictionary<int, string> _knownFunctionLabelsBySlot = new();
    private readonly Dictionary<int, string> _knownFunctionLabelsByEnvIndex = new();
    private string _lastLoweredLambdaLabel = "";
    private bool _lastLoweredLambdaEmptyEnv;
    private int _depth0LambdaCount;

    // Concrete per-parameter types for the reuse specialization currently being generated, and a cursor
    // consumed once per lambda in its curried chain. Monomorphizes the otherwise-polymorphic spec body
    // (e.g. resolves Map.set's `newKey : K` to `Str`), so the heap-field check that materializes a key
    // into the to-space can fire. Null when not generating a spec.
    private IReadOnlyList<TypeRef>? _specializationConcreteParamTypes;
    private int _specializationParamCursor;
    // Parameter arg-types peeled from a `let f : A -> B -> ... = <lambda>` annotation, seeded into each
    // curried lambda's parameter type BEFORE its body is lowered (bidirectional checking). Without this,
    // a numeric operator on an annotated-Float parameter is lowered while the parameter is still an
    // unbound type variable, so ResolveNumericOperandTypes defaults it to Int (then the annotation
    // clashes). Consumed one per lambda via the cursor, exactly like the specialization seeding above,
    // and limited to the definition's curried-lambda count so body lambdas never consume a leftover.
    private IReadOnlyList<TypeRef>? _annotationParamTypes;
    private int _annotationParamCursor;
    private Expr.Lambda? _annotationTargetLambda;
    // Outer (non-accumulator) parameter names of the reuse specialization currently being lowered — e.g.
    // compare/newKey/newValue for Map.set. A constructor field whose argument is one of these is a FRESH
    // heap input (materialize it into the persistent blob so it survives the per-iteration reset); a field
    // taken from the matched accumulator (a pattern binding) is already persistent and must not be re-copied.
    private HashSet<string>? _specFreshInputNames;

    // Temps holding a value built by in-place reuse (an AllocReusing result) — already below the
    // watermark and used linearly. When such a value is the argument to an inlined helper, the
    // helper's parameter is also linear, so a match-then-rebuild on it (e.g. balance's
    // normalized = makeNode(...)) reuses the same cell rather than allocating a fresh one.
    private readonly HashSet<int> _reuseResultTemps = new();

    private readonly Stack<List<bool>?> _runtimeManagedMatchResultArms = new();
    // Accumulator names made uniquely-owned at loop entry (deep-copied) specifically so a call
    // f(acc) to a specializable function can be rewritten to f$reuse(acc). Distinct from
    // _linearReuseNames, which marks accumulators matched directly in the loop body.
    private readonly HashSet<string> _linearSpecializationAccumulators = new(StringComparer.Ordinal);

    // Per-function map from a let-bound local's slot to its binding value AST. Lets the reset-safety
    // check (IsStableAccumulatorExpr) trace a `let m2 = match … in loop(m2)` accumulator back to its
    // binding: m2 is address-stable when every leaf of that match/if is itself stable. Cleared at each
    // function boundary because local slots are numbered per function.
    private readonly Dictionary<int, Expr> _letBindingValues = new();
    private readonly Dictionary<int, BuiltinRegistry.BytesOwnershipProvenance>
        _localBytesProvenance = new();

    // Inlinable-function names currently shadowed by a more-local binding (lambda param / let), so a
    // call to that name is NOT the top-level helper and must not be inlined. Counter per name (a name
    // can be shadowed at multiple nesting levels).
    private readonly Dictionary<string, int> _shadowedInlinables = new(StringComparer.Ordinal);

    // For each registered inlinable, the AST value object of its defining top-level let. Lets the
    // "is this let the helper's own definition (vs a rebinding that shadows it)?" check use reference
    // identity, which is robust to the stitcher's alias-wrapping (where the value is a `let`-chain, not
    // a bare lambda) — without it, every stitched stdlib helper would self-shadow and never inline.
    private readonly Dictionary<string, Expr> _inlinableDefiningValues = new(StringComparer.Ordinal);

    // Inlinable functions currently being inlined, to break any unforeseen inline cycle (fall back to
    // a normal call instead of looping). Non-recursive lets shouldn't form cycles, but this is cheap.
    private readonly HashSet<string> _inliningInProgress = new(StringComparer.Ordinal);

    // Substitution for type variables
    private readonly Dictionary<int, TypeRef> _subst = new();

    // Registered type and constructor symbols
    private readonly Dictionary<string, TypeSymbol> _typeSymbols = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TypeAliasDecl> _typeAliases = new(StringComparer.Ordinal);
    private readonly List<string> _typeAliasExpansionStack = [];
    private readonly HashSet<string> _reportedTypeAliasCycles = new(StringComparer.Ordinal);
    // Keyed by TypeSymbol reference identity, not name: two packages may each declare a type with
    // the same simple name (e.g. both a "Point"), and the orphan rule (ValidateOrphanRule) must not
    // confuse one package's outer-type ownership for the other's just because the unqualified name
    // collides.
    private readonly Dictionary<TypeSymbol, TraitDeclarationProvenance> _typeProvenanceBySymbol =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, ConstructorSymbol> _constructorSymbols = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConstructorSymbol> _builtinConstructorSymbols = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TypeRef.TNamedType> _resolvedTypes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _externalOpaqueTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExternalDecl.OpaqueType> _externalResourceTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IrExternalFunction> _externalResourceDestructors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _invalidExternalResourceDestructors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExternalDecl.Function> _externalFunctionDeclarations = new(StringComparer.Ordinal);
    private readonly List<IrExternalFunction> _externalFunctions = new();

    /// <summary>The type declarations registered during lowering, keyed by type name.</summary>
    public IReadOnlyDictionary<string, TypeSymbol> TypeSymbols => _typeSymbols;
    /// <summary>The data constructors registered during lowering, keyed by constructor name.</summary>
    public IReadOnlyDictionary<string, ConstructorSymbol> ConstructorSymbols => _constructorSymbols;
    /// <summary>The resolved named types encountered during lowering, keyed by type name.</summary>
    public IReadOnlyDictionary<string, TypeRef.TNamedType> ResolvedTypes => _resolvedTypes;
    /// <summary>The inferred type of the most recently lowered expression, exposed for tooling queries.</summary>
    public TypeRef? LastLoweredType { get; private set; }

    /// <summary>Gets the declared resource contract for an external type or function.</summary>
    public ExternalOwnershipInfo? GetExternalOwnershipInfo(string name)
    {
        if (_externalResourceTypes.TryGetValue(name, out ExternalDecl.OpaqueType? resource))
        {
            return new ExternalOwnershipInfo(true, resource.DestructorName, [], false);
        }

        IrExternalFunction? function = _externalFunctions.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, StringComparison.Ordinal));
        if (function is null)
        {
            return null;
        }

        IReadOnlyList<string> parameters = [.. function.ParameterTypes
            .Select((type, index) => type is FfiType.Opaque opaque
                && _externalResourceTypes.ContainsKey(opaque.Name)
                    ? $"#{index + 1} {opaque.Name}: {function.ParameterOwnerships[index].ToString().ToLowerInvariant()}"
                    : string.Empty)
            .Where(description => description.Length > 0)];
        bool returnsOwnedResource = function.ReturnType is FfiType.Opaque returned
            && _externalResourceTypes.ContainsKey(returned.Name);
        return parameters.Count == 0 && !returnsOwnedResource
            ? null
            : new ExternalOwnershipInfo(false, null, parameters, returnsOwnedResource);
    }

    /// <summary>
    /// Returns the narrowest recorded <see cref="HoverTypeInfo"/> whose span contains
    /// <paramref name="position"/>, or null when no recorded type covers that offset. Used by the
    /// language server for hover and type-at-cursor.
    /// </summary>
    public HoverTypeInfo? GetTypeAtPosition(int position)
    {
        HoverTypeInfo? best = null;

        foreach (var hover in _hoverTypes)
        {
            if (!ContainsPosition(hover.Span, position))
            {
                continue;
            }

            if (best is null || IsBetterHoverCandidate(hover, best.Value))
            {
                best = hover;
            }
        }

        return best;
    }

    /// <summary>Renders <paramref name="type"/> in the canonical human-readable form used for hovers and diagnostics.</summary>
    public string FormatType(TypeRef type)
    {
        return Pretty(type);
    }

    /// <summary>Renders related types with one shared type-variable naming context.</summary>
    public IReadOnlyList<string> FormatTypes(IReadOnlyList<TypeRef> types)
    {
        Dictionary<int, string> typeVariableNames = [];
        return types
            .Select(type => Pretty(type, typeVariableNames, parentPrecedence: 0))
            .ToArray();
    }

    /// <summary>Renders a constrained scheme with a canonical <c>requires</c> suffix.</summary>
    public string FormatTypeScheme(TypeScheme scheme)
    {
        Dictionary<int, string> typeVariableNames = [];
        string body = Pretty(scheme.Body, typeVariableNames, parentPrecedence: 0);
        if (scheme.Constraints.Count == 0)
        {
            return body;
        }

        string constraints = string.Join(
            ", ",
            TraitConstraint.Canonicalize(scheme.Constraints).Select(constraint =>
                $"{constraint.Trait.QualifiedName}({string.Join(", ", constraint.TypeArgs.Select(argument => Pretty(argument, typeVariableNames, parentPrecedence: 0)))})"));
        return $"{body} requires {{{constraints}}}";
    }

    /// <summary>
    /// Creates a lowering pass. <paramref name="diag"/> collects diagnostics;
    /// <paramref name="importedStdModules"/> names the standard-library modules in scope (gating, for
    /// example, the unqualified <c>Ashes.IO</c> bindings); <paramref name="moduleAliases"/> maps import
    /// aliases to their target module names for qualified-reference resolution.
    /// </summary>
    public Lowering(
        Diagnostics diag,
        IReadOnlySet<string>? importedStdModules = null,
        IReadOnlyDictionary<string, string>? moduleAliases = null,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? constructorModulesByName = null,
        LoweringConfiguration? configuration = null)
        : this(
            diag,
            importedStdModules,
            moduleAliases,
            constructorModulesByName,
            configuration,
            enableInferredTraitElaboration: true,
            collectInferredTraitElaboration: false,
            enableTraitValidationPass: true,
            emitTraitDictionaries: true,
            isTraitValidationSubpass: false)
    {
    }

    private Lowering(
        Diagnostics diag,
        IReadOnlySet<string>? importedStdModules,
        IReadOnlyDictionary<string, string>? moduleAliases,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? constructorModulesByName,
        LoweringConfiguration? configuration,
        bool enableInferredTraitElaboration,
        bool collectInferredTraitElaboration,
        bool enableTraitValidationPass,
        bool emitTraitDictionaries,
        bool isTraitValidationSubpass)
    {
        _diag = diag;
        _configuration = configuration ?? LoweringConfiguration.Default;
        _importedStdModules = importedStdModules is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(importedStdModules, StringComparer.Ordinal);
        _enableInferredTraitElaboration = enableInferredTraitElaboration;
        _collectInferredTraitElaboration = collectInferredTraitElaboration;
        _enableTraitValidationPass = enableTraitValidationPass;
        _emitTraitDictionaries = emitTraitDictionaries;
        _isTraitValidationSubpass = isTraitValidationSubpass;
        _hasAshesIO = _importedStdModules.Contains("Ashes.IO");
        _moduleAliases = moduleAliases ?? new Dictionary<string, string>(StringComparer.Ordinal);
        _constructorModulesByName = constructorModulesByName ?? new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        RegisterBuiltinSymbols();
        var rootScope = new Dictionary<string, Binding>(StringComparer.Ordinal);
        // Create the `async` binding once (it allocates a generalized type var) and reuse the same
        // instance in every lambda scope, so re-seeding it does not consume a fresh type var per lambda.
        _asyncBinding = CreateAsyncTaskBinding();
        rootScope["async"] = _asyncBinding;
        if (_hasAshesIO)
        {
            AddStdIOBindings(rootScope);
        }
        _scopes.Push(rootScope);
        _ownershipScopes.Push(new Dictionary<string, OwnershipInfo>(StringComparer.Ordinal));
        // Root scope: push sentinel arena watermark (no restore will happen at program exit)
        _arenaWatermarks.Push((-1, -1));
    }





    // All top-level value-binding names in the program, used to specialize the "undefined variable"
    // diagnostic into a forward-reference diagnostic (ASH014) when the name IS declared later.
    private readonly HashSet<string> _topLevelBindingNames = new(StringComparer.Ordinal);

    /// <summary>
    /// Lowers a complete parsed <see cref="Program"/> (its declarations and trailing expression) into
    /// the typed IR, running binding, Hindley-Milner inference, and ownership analysis. This is the
    /// primary entry point for compiling a source unit.
    /// </summary>
    public IrProgram Lower(Program program)
    {
        if (_enableInferredTraitElaboration)
        {
            program = ElaborateInferredTraitBindings(program);
        }

        // External opaque names must be known before constructor fields are resolved, so an ADT field
        // naming an external handle remains concrete instead of being inferred as a type parameter.
        RegisterExternalOpaqueTypes(program.ExternalDecls);
        RegisterTypeDeclarations(program.TypeDecls, program.TypeAliasDecls, program.ZeroCostTypeDecls);
        RegisterCapabilityDeclarations(program.Items);
        RegisterExternalFunctions(program.ExternalDecls);
        RegisterProviderDeclarations(program.Items);
        program = ExpandDerivedImplementations(program);
        RegisterTraitAndImplementationDeclarations(program.Items);
        if (RunTraitValidationPass(program) is { } failedValidation)
        {
            return failedValidation;
        }
        // Capability dictionary passing is an independent source elaboration and must run in trait
        // discovery/validation passes too; otherwise a same-named capability operation can be
        // mistaken for a trait method before its hidden operation parameter is introduced.
        program = RegisterAndTransformDictionaryFunctions(program);
        if (_emitTraitDictionaries)
        {
            RegisterTraitDictionaryFunctions(program);

        }

        var valueItems = program.Items
            .Where(item => item is TopLevelItem.LetDecl or TopLevelItem.RecursiveGroup)
            .ToList();

        CollectTopLevelBindingNames(valueItems);
        RegisterInlinableFunctions(valueItems);
        RegisterEntryBodyFunctions(program.Body);
        if (Environment.GetEnvironmentVariable("ASH_DBG_REUSE") is not null)
        {
            Console.Error.WriteLine($"[reuse] specializable funcs: {string.Join(", ", _specializableFunctions.Keys)}");
            Console.Error.WriteLine($"[reuse] inlinable funcs: {string.Join(", ", _inlinableFunctions.Keys.Where(k => k.Contains("Map", StringComparison.Ordinal)))}");
        }

        // Desugar the ordered value declarations into the existing nested let / let rec forms so
        // Model-A sequential scoping falls out for free: each binding's body sees the just-bound
        // name and all enclosing ones, never a later sibling.
        var body = DesugarTopLevel(valueItems, program.Body);
        AnalyzeReuseCopyElision(body);
        return Lower(body);
    }

    /// <summary>
    /// Records non-recursive top-level functions (a plain <c>let</c> whose value is a lambda chain)
    /// so a saturated call to one inside a reuse arm can be inlined, letting the helper's constructor
    /// reuse a dead cell. <c>let rec</c> / mutually-recursive groups are excluded — they can't be
    /// inlined, and self-reference would loop.
    /// </summary>
    // True if the expression can produce a heap allocation (a constructor application or aggregate
    // literal) anywhere within it — directly or through a nested call. Used to keep non-allocating
    // accessor/arithmetic helpers out of the reuse-inline set: inlining them yields nothing for reuse
    // (they never allocate) but transitively inlining them into a specialization explodes its temp/slot
    // count and stack frame. They are instead resolved as ordinary by-label calls (see LowerVar /
    // _topLevelFunctionRefs). Unknown composite shapes default to true (conservatively inlinable), so this
    // never drops a helper a reuse arm depends on; only provably allocation-free leaves and their pure
    // compositions return false.
    private static bool ExprHasCallOrAggregate(Expr e) => e switch
    {
        Expr.Call or Expr.TupleLit or Expr.ListLit or Expr.Cons or Expr.RecordLit or Expr.RecordUpdate => true,
        Expr.IntLit or Expr.UIntLit or Expr.BigIntLit or Expr.FloatLit or Expr.StrLit or Expr.RuneLit or Expr.BoolLit or Expr.Var or Expr.QualifiedVar => false,
        Expr.Add x => ExprHasCallOrAggregate(x.Left) || ExprHasCallOrAggregate(x.Right),
        Expr.Subtract x => ExprHasCallOrAggregate(x.Left) || ExprHasCallOrAggregate(x.Right),
        Expr.Multiply x => ExprHasCallOrAggregate(x.Left) || ExprHasCallOrAggregate(x.Right),
        Expr.Divide x => ExprHasCallOrAggregate(x.Left) || ExprHasCallOrAggregate(x.Right),
        Expr.Modulo x => ExprHasCallOrAggregate(x.Left) || ExprHasCallOrAggregate(x.Right),
        Expr.BitwiseAnd x => ExprHasCallOrAggregate(x.Left) || ExprHasCallOrAggregate(x.Right),
        Expr.BitwiseOr x => ExprHasCallOrAggregate(x.Left) || ExprHasCallOrAggregate(x.Right),
        Expr.BitwiseXor x => ExprHasCallOrAggregate(x.Left) || ExprHasCallOrAggregate(x.Right),
        Expr.ShiftLeft x => ExprHasCallOrAggregate(x.Left) || ExprHasCallOrAggregate(x.Right),
        Expr.ShiftRight x => ExprHasCallOrAggregate(x.Left) || ExprHasCallOrAggregate(x.Right),
        Expr.BitwiseNot x => ExprHasCallOrAggregate(x.Operand),
        Expr.LogicalNot x => ExprHasCallOrAggregate(x.Operand),
        Expr.GreaterThan x => ExprHasCallOrAggregate(x.Left) || ExprHasCallOrAggregate(x.Right),
        Expr.LessThan x => ExprHasCallOrAggregate(x.Left) || ExprHasCallOrAggregate(x.Right),
        Expr.GreaterOrEqual x => ExprHasCallOrAggregate(x.Left) || ExprHasCallOrAggregate(x.Right),
        Expr.LessOrEqual x => ExprHasCallOrAggregate(x.Left) || ExprHasCallOrAggregate(x.Right),
        Expr.Equal x => ExprHasCallOrAggregate(x.Left) || ExprHasCallOrAggregate(x.Right),
        Expr.NotEqual x => ExprHasCallOrAggregate(x.Left) || ExprHasCallOrAggregate(x.Right),
        Expr.If x => ExprHasCallOrAggregate(x.Cond) || ExprHasCallOrAggregate(x.Then) || ExprHasCallOrAggregate(x.Else),
        Expr.Let x => ExprHasCallOrAggregate(x.Value) || ExprHasCallOrAggregate(x.Body),
        Expr.LetRecursive x => ExprHasCallOrAggregate(x.Value) || ExprHasCallOrAggregate(x.Body),
        Expr.LetResult x => ExprHasCallOrAggregate(x.Value) || ExprHasCallOrAggregate(x.Body),
        Expr.Lambda x => ExprHasCallOrAggregate(x.Body),
        Expr.Await x => ExprHasCallOrAggregate(x.Task),
        Expr.Match x => ExprHasCallOrAggregate(x.Value)
            || x.Cases.Any(c => ExprHasCallOrAggregate(c.Body) || (c.Guard is not null && ExprHasCallOrAggregate(c.Guard))),
        _ => true,
    };

    /// <summary>
    /// Returns the type of the <paramref name="n"/>th curried argument of a function type (0-based),
    /// i.e. peels <paramref name="n"/> <c>-&gt;</c> arrows and returns the next argument type, or null
    /// if the type isn't curried that far.
    /// </summary>
    private TypeRef? NthCurriedArgType(TypeRef funcType, int n)
    {
        var t = Prune(funcType);
        for (int i = 0; i < n; i++)
        {
            if (t is TypeRef.TFun f)
            {
                t = Prune(f.Ret);
            }
            else
            {
                return null;
            }
        }

        return t is TypeRef.TFun last ? Prune(last.Arg) : null;
    }

    private const string DispatchWhichName = "__recgroup_which";

    /// <summary>Unwraps the first <paramref name="arity"/> parameter types of a (curried) function type.</summary>
    private bool TryGetArrowParamTypes(TypeRef type, int arity, out List<TypeRef> paramTypes)
    {
        paramTypes = new List<TypeRef>(arity);
        var current = Prune(type);
        for (int i = 0; i < arity; i++)
        {
            if (current is not TypeRef.TFun fn)
            {
                return false;
            }

            paramTypes.Add(Prune(fn.Arg));
            current = Prune(fn.Ret);
        }

        return true;
    }

    /// <summary>
    /// Conservative read-only structural type equality (no unification). Unresolved type variables
    /// only compare equal when they are the same variable, so anything uncertain is treated as
    /// unequal — keeping the mutual-recursion TCO gate safe.
    /// </summary>
    private bool TypesStructurallyEqual(TypeRef a, TypeRef b)
    {
        a = Prune(a);
        b = Prune(b);
        return (a, b) switch
        {
            (TypeRef.TInt, TypeRef.TInt) => true,
            (TypeRef.TFloat, TypeRef.TFloat) => true,
            (TypeRef.TBool, TypeRef.TBool) => true,
            (TypeRef.TStr, TypeRef.TStr) => true,
            (TypeRef.TRune, TypeRef.TRune) => true,
            (TypeRef.TBytes, TypeRef.TBytes) => true,
            (TypeRef.TNever, TypeRef.TNever) => true,
            (TypeRef.TUInt ua, TypeRef.TUInt ub) => ua.Bits == ub.Bits,
            (TypeRef.TOpaque oa, TypeRef.TOpaque ob) => string.Equals(oa.Name, ob.Name, StringComparison.Ordinal),
            (TypeRef.TList la, TypeRef.TList lb) => TypesStructurallyEqual(la.Element, lb.Element),
            (TypeRef.TPtr pa, TypeRef.TPtr pb) => TypesStructurallyEqual(pa.Pointee, pb.Pointee),
            (TypeRef.TTuple ta, TypeRef.TTuple tb) => ta.Elements.Count == tb.Elements.Count
                && ta.Elements.Zip(tb.Elements).All(pair => TypesStructurallyEqual(pair.First, pair.Second)),
            (TypeRef.TFun fa, TypeRef.TFun fb) => TypesStructurallyEqual(fa.Arg, fb.Arg) && TypesStructurallyEqual(fa.Ret, fb.Ret),
            (TypeRef.TNamedType na, TypeRef.TNamedType nb) => string.Equals(na.Symbol.Name, nb.Symbol.Name, StringComparison.Ordinal)
                && na.TypeArgs.Count == nb.TypeArgs.Count
                && na.TypeArgs.Zip(nb.TypeArgs).All(pair => TypesStructurallyEqual(pair.First, pair.Second)),
            (TypeRef.TVar va, TypeRef.TVar vb) => va.Id == vb.Id,
            _ => false,
        };
    }







    /// <summary>
    /// Lowers a bare trailing expression (a program with no top-level declarations) into the typed IR,
    /// the single-expression counterpart to <see cref="Lower(Program)"/>.
    /// </summary>
    public IrProgram Lower(Expr expr)
    {
        IrFunctionOrigin entryOrigin = PrepareFunctionOrigins(expr);

        // Async syntax may occur after a pure helper in source order. Establish the program-wide
        // boundary before lowering that helper so it cannot be admitted to synchronous-only RC.
        _usesAsync |= ExprContainsAwait(expr)
            || ContainsAsyncSpawn(expr)
            || FreeVars(expr, []).Contains("async");
        // Entry function lowering (no env/arg params)
        PushTraitConstraintScope();
        var (resultTemp, resultType) = LowerExpr(expr);
        LastTraitConstraints = SimplifyAndResolveTraitConstraints(
            PopTraitConstraintScope(),
            GetSpan(expr));
        Emit(new IrInst.Return(resultTemp));

        FinishEntryInference(resultType);

        var entry = new IrFunction(
            Label: "_start_main",
            Instructions: _inst,
            LocalCount: _nextLocalSlot,
            TempCount: _nextTempSlot,
            HasEnvAndArgParams: false,
            LocalNames: new Dictionary<int, string>(_localNames),
            LocalTypes: SnapshotLocalTypes()
        )
        {
            Origin = entryOrigin
        };

        var loweredProgram = new IrProgram(
            EntryFunction: entry,
            Functions: _funcs,
            StringLiterals: _strings,
            ExternalFunctions: _externalFunctions,
            ExternalOpaqueTypes: new HashSet<string>(_externalOpaqueTypes, StringComparer.Ordinal),
            UsesPrintInt: _usesPrintInt,
            UsesPrintStr: _usesPrintStr,
            UsesPrintBool: _usesPrintBool,
            UsesConcatStr: _usesConcatStr,
            UsesClosures: _usesClosures,
            UsesAsync: _usesAsync
        )
        {
            // Per-capability evidence slots plus the pending-post register and the live-posts counter.
            CapabilityHandlerGlobals = CapabilityGlobalCount == 0 ? 0 : CapabilityGlobalCount + 2,
            TraitEvidence = BuildTraitEvidenceAnnotations(),
        };

        return PerceusLifetimePlacement.Place(loweredProgram, _borrowedArgumentCalls);
    }

    private void FinishEntryInference(TypeRef resultType)
    {
        ResolveDeferredTcoResets();
        ResolveDeferredTupleMaterializations();
        CheckUnhandledCapabilities();
        LastLoweredType = Prune(resultType);
    }

    /// <summary>
    /// True when a tail-call argument expression rebuilds its list THIS iteration: a call result
    /// (a function's list result is copied out of the callee's arena scope on return, so it is
    /// self-contained), a list literal, or a cons spine ending in one of those. Only such an arg
    /// may take the whole-list DeepAdt clone at the back-edge — the body already paid O(length)
    /// to construct it, so the clone at most doubles that. Anything else (bare var, pattern tail,
    /// cons onto the accumulator param) may share unbounded structure with the previous iteration.
    /// </summary>
    private static bool IsArenaSelfContainedListRebuildExpr(Expr expr)
        => expr switch
        {
            Expr.Call => true,
            Expr.ListLit => true,
            Expr.Cons cons => IsArenaSelfContainedListRebuildExpr(cons.Tail),
            Expr.Let let => IsArenaSelfContainedListRebuildExpr(let.Body),
            _ => false,
        };

    /// <summary>Everything a TCO back-edge arena block needs, captured at the back edge so the
    /// block can be generated later (after inference resolves the argument types and all sibling
    /// branches establish the final runtime-managed parameter set) at the exact
    /// point marked by an <see cref="IrInst.TcoResetPending"/> placeholder. The
    /// <see cref="ArgTypes"/> are live inference references — pruning them at resolution time
    /// yields the final types. The AST/scope-dependent facts (pass-through, single-fresh-cons,
    /// stable-accumulator) are evaluated eagerly, since the scope is gone by resolution time.</summary>
    private sealed record PendingTcoReset(
        TcoContext Tco,
        int[] ArgTemps,
        TypeRef[] ArgTypes,
        bool[] RuntimeManagedArgResults,
        bool[] RuntimeManagedPredecessorAliases,
        bool[] PassThrough,
        bool[] SingleFreshCons,
        bool[] FreshListRebuild,
        bool[] ConsumedListTail,
        bool[] StableAccArg,
        int[] OldRuntimeParamTemps,
        TcoParamPlacementDecision?[] ParamPlacements,
        int[] RuntimeManagedParamActiveSlots,
        int[] RuntimeManagedClosureActiveSlots,
        IReadOnlyList<OwnershipInfo> IterationOwnedDrops,
        int[] ParamSlots,
        int FixedCursorSlot,
        int FixedEndSlot,
        int ArenaCursorSlot,
        int ArenaEndSlot,
        bool CoroutineLoop,
        int CompactionSizeSlot,
        int[] ArgResvStartSlots,
        int[] ArgResvEndSlots);

    private readonly Dictionary<int, PendingTcoReset> _pendingTcoResets = new();
    private int _nextTcoResetId;

    // Set while lowering the tail-call argument of an affine string accumulator (its own param
    // position): LowerAdd's Str+Str branch emits the reservation-growing ConcatStrTip for
    // `<param> + rhs` chains instead of a copying ConcatStr. (Name, the param's slot for the
    // shadow check, and the loop's reservation start/end slots.)
    private (string Name, int Slot, int ResvStart, int ResvEnd)? _affineAppendCtx;

    // Slack added to the amortized-compaction threshold (growth > 2*live + slack): small loops with
    // tiny live sizes still batch a few KB of garbage per compaction instead of copying every
    // iteration, and loops whose live size is zero compact only once slack accumulates.
    private const long TcoCompactionSlackBytes = 4096;

    /// <summary>
    /// Emits the TCO back-edge arena block — the plain per-iteration reset, or the two-pass
    /// copy-out with the fixed/advancing watermark choice — from the captured facts. Called from
    /// <see cref="ResolveDeferredTcoResets"/> with <c>_inst</c> pointed at the splice list after
    /// inference and runtime-managed parameter discovery are complete.
    /// </summary>
    private void EmitTcoBackEdgeArenaBlock(PendingTcoReset info)
    {
        info = TcoBackEdgeRefreshRuntimeManagedParams(info);
        var argTypes = info.ArgTypes;
        int tcoPreRestoreEndSlot = NewLocal();

        if (TcoBackEdgeTryEmitRuntimeManagedReset(info, tcoPreRestoreEndSlot))
        {
            return;
        }

        // The RC-normalizing path above must establish successor ownership before releasing these
        // locals. Every other reset path preserves the historical ordering and releases them before
        // deciding whether an arena reset can run.
        EmitDeferredTcoBackEdgeOwnedDrops(info);

        if (TcoBackEdgeTryEmitPlainReset(info, tcoPreRestoreEndSlot))
        {
            return;
        }

        bool allCopyable = TcoBackEdgeAllArgsCopyable(info);
        bool useFixedWatermark = TcoBackEdgeUseFixedWatermark(info);
        int resetCursorSlot = useFixedWatermark ? info.FixedCursorSlot : info.ArenaCursorSlot;
        int resetEndSlot = useFixedWatermark ? info.FixedEndSlot : info.ArenaEndSlot;

        if (!allCopyable)
        {
            return; // complex heap types — no arena reset.
        }

        // Two-pass copy-out. Carrying TWO+ freshly heap-allocated args across the back-edge cannot
        // be done with a single round of copy-outs to the watermark W: each copy-out compacts its
        // arg *down* to W, but a copy whose destination block [W, …) reaches high enough overwrites
        // a later arg's still-unread source bytes.
        //
        // Phase A (BEFORE the reset): copy every heap arg UP to a fresh alloc above the current
        // cursor. Sources are all below the cursor, destinations above it → disjoint. Phase B
        // (AFTER the reset): copy each up-copy DOWN to W.
        // Skipped entirely while a one-shot capability post pushed this iteration is still pending.
        var tcoCopySkipLabel = BeginLivePostsGuard();

        string? compactSkipLabel = TcoBackEdgeEmitCompactionCheck(info, useFixedWatermark);

        var upCopyTemps = TcoBackEdgeEmitPhaseAUpCopies(info);

        TcoBackEdgeEmitResetAndZeroReservations(info, resetCursorSlot, resetEndSlot, tcoPreRestoreEndSlot);

        TcoBackEdgeEmitPhaseBDownCopies(info, upCopyTemps);

        // Free the chunks abandoned above W (including the Phase A up-copies, now fully consumed).
        Emit(new IrInst.ReclaimArenaChunks(resetEndSlot, tcoPreRestoreEndSlot) { CoroutineLoop = info.CoroutineLoop });

        if (compactSkipLabel is not null)
        {
            TcoBackEdgeEmitCompactionRecord(info, compactSkipLabel);
        }

        EndLivePostsGuard(tcoCopySkipLabel);
    }

    private PendingTcoReset TcoBackEdgeRefreshRuntimeManagedParams(PendingTcoReset info)
        => info with
        {
            RuntimeManagedArgResults = info.RuntimeManagedArgResults
                .Select((runtimeManaged, index) =>
                    runtimeManaged || IsRuntimeManagedResultTemp(info.ArgTemps[index]))
                .ToArray(),
            ParamPlacements = info.ParamSlots
                .Select(slot => info.Tco.ParamPlacements[slot].Current)
                .ToArray(),
            RuntimeManagedParamActiveSlots = info.ParamSlots
                .Select(slot => info.Tco.RuntimeManagedParamActiveSlots.GetValueOrDefault(slot, -1))
                .ToArray(),
            RuntimeManagedClosureActiveSlots = info.ParamSlots
                .Select(slot => info.Tco.RuntimeManagedClosureActiveSlots.GetValueOrDefault(slot, -1))
                .ToArray()
        };

    private bool TcoBackEdgeTryEmitRuntimeManagedReset(
        PendingTcoReset info,
        int tcoPreRestoreEndSlot)
    {
        if (info.CoroutineLoop
            || !info.ParamPlacements.Any(decision =>
                decision?.Representation == TcoPlacementRepresentation.RuntimeRc)
            || Enumerable.Range(0, info.ArgTypes.Length).Any(i =>
                !CanArenaReset(info.ArgTypes[i])
                && !IsResourceHandleType(info.ArgTypes[i])
                && !info.PassThrough[i]
                && !TcoBackEdgeConsumedInlineListTailCanReset(info, i)
                && !TcoBackEdgeRuntimeManagedArgCanReset(info, i)))
        {
            return false;
        }

        int[] normalizedTemps = TcoBackEdgeNormalizeAndReleaseRuntimeManagedArgs(info);

        int resetCursorSlot = info.FixedCursorSlot >= 0 ? info.FixedCursorSlot : info.ArenaCursorSlot;
        int resetEndSlot = info.FixedEndSlot >= 0 ? info.FixedEndSlot : info.ArenaEndSlot;
        TcoBackEdgeEmitResetAndZeroReservations(
            info,
            resetCursorSlot,
            resetEndSlot,
            tcoPreRestoreEndSlot);
        for (int i = 0; i < normalizedTemps.Length; i++)
        {
            if (normalizedTemps[i] >= 0)
            {
                Emit(new IrInst.StoreLocal(info.ParamSlots[i], normalizedTemps[i]));
                if (info.RuntimeManagedParamActiveSlots[i] >= 0)
                {
                    int activeTemp = NewTemp();
                    Emit(new IrInst.LoadConstInt(activeTemp, 1));
                    Emit(new IrInst.StoreLocal(info.RuntimeManagedParamActiveSlots[i], activeTemp));
                }
                if (info.RuntimeManagedClosureActiveSlots[i] >= 0)
                {
                    int activeTemp = NewTemp();
                    Emit(new IrInst.LoadConstInt(activeTemp, 1));
                    Emit(new IrInst.StoreLocal(info.RuntimeManagedClosureActiveSlots[i], activeTemp));
                }
            }
        }
        Emit(new IrInst.ReclaimArenaChunks(resetEndSlot, tcoPreRestoreEndSlot));
        return true;
    }

    private int[] TcoBackEdgeNormalizeAndReleaseRuntimeManagedArgs(PendingTcoReset info)
    {
        int[] normalizedTemps = Enumerable.Repeat(-1, info.ArgTypes.Length).ToArray();
        var dropPredecessor = new bool[info.ArgTypes.Length];
        for (int i = 0; i < info.ArgTypes.Length; i++)
        {
            if (info.ParamPlacements[i]?.Representation != TcoPlacementRepresentation.RuntimeRc
                || info.PassThrough[i])
            {
                continue;
            }

            TypeRef argType = Prune(info.ArgTypes[i]);
            if (!info.RuntimeManagedArgResults[i]
                && info.FreshListRebuild[i]
                && argType is TypeRef.TList { Element: TypeRef.TNamedType elementType }
                && info.RuntimeManagedParamActiveSlots[i] >= 0
                && TryGetCopyOnlyRecordConstructor(elementType, out ConstructorSymbol recordConstructor))
            {
                normalizedTemps[i] = EmitRuntimeManagedRecordListOverwriteOrNormalize(
                    info.ArgTemps[i],
                    info.OldRuntimeParamTemps[i],
                    info.RuntimeManagedParamActiveSlots[i],
                    elementType,
                    recordConstructor);
            }
            else
            {
                normalizedTemps[i] = TcoBackEdgeNormalizeRuntimeManagedArg(
                    info.ArgTemps[i],
                    argType,
                    info.RuntimeManagedArgResults[i],
                    info.RuntimeManagedPredecessorAliases[i],
                    info.ConsumedListTail[i]);
            }
            bool movedListTail = argType is TypeRef.TList
                && info.SingleFreshCons[i]
                && info.RuntimeManagedArgResults[i];
            bool consumedStringPredecessor = info.RuntimeManagedArgResults[i]
                && IsRuntimeManagedConcatStrTipResult(info.ArgTemps[i]);
            dropPredecessor[i] = !movedListTail && !consumedStringPredecessor;
        }

        // A tail call is a parallel assignment. Successor arguments may alias more than one
        // predecessor parameter, so releasing a predecessor while later successors are still
        // being normalized lets the exact-size free list reuse and overwrite storage that those
        // later normalizations still need. Establish ownership for every successor first.
        for (int i = 0; i < info.ArgTypes.Length; i++)
        {
            if (dropPredecessor[i])
            {
                TcoBackEdgeDropRuntimeManagedArg(info, i, Prune(info.ArgTypes[i]));
            }
        }

        // Arena successor aggregates borrow their RC children. Keep iteration-local owners alive
        // until every successor has normalized its own RC graph, then release them before the arena
        // reset invalidates the borrowed shells.
        EmitDeferredTcoBackEdgeOwnedDrops(info);

        return normalizedTemps;
    }

    private void EmitDeferredTcoBackEdgeOwnedDrops(PendingTcoReset info)
    {
        foreach (OwnershipInfo owned in info.IterationOwnedDrops)
        {
            EmitOwnedValueDrop(owned);
        }
    }

    /// <summary>
    /// True when the tail-consumed list parameter at <paramref name="paramIndex"/> is only inspected
    /// by the recursive body (nothing derived from it escapes — see
    /// <see cref="TcoParamStaticFacts.BorrowInspectOnly"/>) AND its element is an all-inline-copy
    /// record. Under both conditions the traversal reads only scalar fields and never retains a cell
    /// or head, so the caller's graph can be borrowed instead of RC-normalized into a private copy.
    /// </summary>
    private bool IsBorrowableInspectOnlyList(TcoContext tco, int paramIndex, TypeRef.TList list)
        => paramIndex >= 0
            && tco.ParamFacts[tco.ParamSlots[paramIndex]].BorrowInspectOnly
            && Prune(list.Element) is TypeRef.TNamedType element
            && TryGetCopyOnlyRecordConstructor(element, out _);

    private bool TryGetCopyOnlyRecordConstructor(
        TypeRef.TNamedType named,
        out ConstructorSymbol constructor)
    {
        ConstructorSymbol? candidate = named.Symbol.Constructors.Count == 1
            ? named.Symbol.Constructors[0]
            : null;
        if (candidate is null
            || candidate.DeclaringSyntax.FieldNames.Count == 0)
        {
            constructor = null!;
            return false;
        }

        for (int i = 0; i < candidate.Arity; i++)
        {
            if (!CanArenaReset(Prune(InstantiateConstructorParameterType(candidate, i, named))))
            {
                constructor = null!;
                return false;
            }
        }

        constructor = candidate;
        return true;
    }

    private int EmitRuntimeManagedRecordListOverwriteOrNormalize(
        int sourceTemp,
        int predecessorTemp,
        int predecessorActiveSlot,
        TypeRef.TNamedType elementType,
        ConstructorSymbol constructor)
    {
        int resultSlot = NewLocal();
        int sourceCursorSlot = NewLocal();
        int predecessorCursorSlot = NewLocal();
        string overwriteLabel = NewLabel("rc_list_overwrite");
        string fallbackLabel = NewLabel("rc_list_overwrite_fallback");
        string doneLabel = NewLabel("rc_list_overwrite_result");
        int zeroTemp = EmitRuntimeManagedRecordListOverwritePreflight(
            sourceTemp,
            predecessorTemp,
            predecessorActiveSlot,
            sourceCursorSlot,
            predecessorCursorSlot,
            overwriteLabel,
            fallbackLabel);
        EmitRuntimeManagedRecordListOverwrite(
            sourceTemp,
            predecessorTemp,
            predecessorActiveSlot,
            sourceCursorSlot,
            predecessorCursorSlot,
            zeroTemp,
            constructor,
            resultSlot,
            overwriteLabel,
            doneLabel);

        Emit(new IrInst.Label(fallbackLabel));
        int normalizedTemp = EmitRuntimeManagedTcoListDeepCopy(sourceTemp, elementType);
        Emit(new IrInst.StoreLocal(resultSlot, normalizedTemp));
        Emit(new IrInst.Label(doneLabel));
        int resultTemp = NewTemp();
        Emit(new IrInst.LoadLocal(resultTemp, resultSlot));
        MarkRuntimeManagedTemp(resultTemp);
        return resultTemp;
    }

    private int EmitRuntimeManagedRecordListOverwritePreflight(
        int sourceTemp,
        int predecessorTemp,
        int predecessorActiveSlot,
        int sourceCursorSlot,
        int predecessorCursorSlot,
        string overwriteLabel,
        string fallbackLabel)
    {
        string preflightLabel = NewLabel("rc_list_overwrite_preflight");
        string sourceEmptyLabel = NewLabel("rc_list_overwrite_source_empty");
        int predecessorActiveTemp = NewTemp();
        Emit(new IrInst.LoadLocal(predecessorActiveTemp, predecessorActiveSlot));
        Emit(new IrInst.JumpIfFalse(predecessorActiveTemp, fallbackLabel));
        Emit(new IrInst.StoreLocal(sourceCursorSlot, sourceTemp));
        Emit(new IrInst.StoreLocal(predecessorCursorSlot, predecessorTemp));

        // Check the complete shape and every destination node before mutating anything. A late
        // mismatch or shared node therefore falls back without exposing a partially overwritten
        // predecessor graph.
        Emit(new IrInst.Label(preflightLabel));
        int preflightSourceTemp = NewTemp();
        Emit(new IrInst.LoadLocal(preflightSourceTemp, sourceCursorSlot));
        int preflightPredecessorTemp = NewTemp();
        Emit(new IrInst.LoadLocal(preflightPredecessorTemp, predecessorCursorSlot));
        int zeroTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(zeroTemp, 0));
        int sourceNonEmptyTemp = NewTemp();
        Emit(new IrInst.CmpIntNe(sourceNonEmptyTemp, preflightSourceTemp, zeroTemp));
        int predecessorNonEmptyTemp = NewTemp();
        Emit(new IrInst.CmpIntNe(predecessorNonEmptyTemp, preflightPredecessorTemp, zeroTemp));
        Emit(new IrInst.JumpIfFalse(sourceNonEmptyTemp, sourceEmptyLabel));
        Emit(new IrInst.JumpIfFalse(predecessorNonEmptyTemp, fallbackLabel));

        int predecessorUniqueTemp = NewTemp();
        Emit(new IrInst.RcIsUnique(predecessorUniqueTemp, preflightPredecessorTemp));
        Emit(new IrInst.JumpIfFalse(predecessorUniqueTemp, fallbackLabel));
        int predecessorHeadTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(
            predecessorHeadTemp,
            preflightPredecessorTemp,
            HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListHeadIndex)));
        int predecessorHeadUniqueTemp = NewTemp();
        Emit(new IrInst.RcIsUnique(predecessorHeadUniqueTemp, predecessorHeadTemp));
        Emit(new IrInst.JumpIfFalse(predecessorHeadUniqueTemp, fallbackLabel));

        int preflightSourceTailTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(
            preflightSourceTailTemp,
            preflightSourceTemp,
            HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListTailIndex)));
        int preflightPredecessorTailTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(
            preflightPredecessorTailTemp,
            preflightPredecessorTemp,
            HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListTailIndex)));
        Emit(new IrInst.StoreLocal(sourceCursorSlot, preflightSourceTailTemp));
        Emit(new IrInst.StoreLocal(predecessorCursorSlot, preflightPredecessorTailTemp));
        Emit(new IrInst.Jump(preflightLabel));

        Emit(new IrInst.Label(sourceEmptyLabel));
        Emit(new IrInst.JumpIfFalse(predecessorNonEmptyTemp, overwriteLabel));
        Emit(new IrInst.Jump(fallbackLabel));
        return zeroTemp;
    }

    private void EmitRuntimeManagedRecordListOverwrite(
        int sourceTemp,
        int predecessorTemp,
        int predecessorActiveSlot,
        int sourceCursorSlot,
        int predecessorCursorSlot,
        int zeroTemp,
        ConstructorSymbol constructor,
        int resultSlot,
        string overwriteLabel,
        string doneLabel)
    {
        string overwriteDoneLabel = NewLabel("rc_list_overwrite_done");
        Emit(new IrInst.Label(overwriteLabel));
        Emit(new IrInst.StoreLocal(sourceCursorSlot, sourceTemp));
        Emit(new IrInst.StoreLocal(predecessorCursorSlot, predecessorTemp));
        string overwriteLoopLabel = NewLabel("rc_list_overwrite_loop");
        Emit(new IrInst.Label(overwriteLoopLabel));
        int overwriteSourceTemp = NewTemp();
        Emit(new IrInst.LoadLocal(overwriteSourceTemp, sourceCursorSlot));
        int overwriteSourceNonEmptyTemp = NewTemp();
        Emit(new IrInst.CmpIntNe(overwriteSourceNonEmptyTemp, overwriteSourceTemp, zeroTemp));
        Emit(new IrInst.JumpIfFalse(overwriteSourceNonEmptyTemp, overwriteDoneLabel));
        int overwritePredecessorTemp = NewTemp();
        Emit(new IrInst.LoadLocal(overwritePredecessorTemp, predecessorCursorSlot));
        int sourceHeadTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(
            sourceHeadTemp,
            overwriteSourceTemp,
            HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListHeadIndex)));
        int destinationHeadTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(
            destinationHeadTemp,
            overwritePredecessorTemp,
            HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListHeadIndex)));
        for (int field = 0; field < constructor.Arity; field++)
        {
            int fieldTemp = NewTemp();
            Emit(new IrInst.GetAdtField(fieldTemp, sourceHeadTemp, field));
            Emit(new IrInst.SetAdtField(destinationHeadTemp, field, fieldTemp));
        }

        int overwriteSourceTailTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(
            overwriteSourceTailTemp,
            overwriteSourceTemp,
            HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListTailIndex)));
        int overwritePredecessorTailTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(
            overwritePredecessorTailTemp,
            overwritePredecessorTemp,
            HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListTailIndex)));
        Emit(new IrInst.StoreLocal(sourceCursorSlot, overwriteSourceTailTemp));
        Emit(new IrInst.StoreLocal(predecessorCursorSlot, overwritePredecessorTailTemp));
        Emit(new IrInst.Jump(overwriteLoopLabel));

        Emit(new IrInst.Label(overwriteDoneLabel));
        Emit(new IrInst.StoreLocal(resultSlot, predecessorTemp));
        int inactiveTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(inactiveTemp, 0));
        Emit(new IrInst.StoreLocal(predecessorActiveSlot, inactiveTemp));
        Emit(new IrInst.Jump(doneLabel));
    }

    private bool IsRuntimeManagedConcatStrTipResult(int temp)
        => _tempOwnershipFacts.TryGetValue(temp, out LoweredTempOwnershipFact? fact)
            && fact.Representation == LoweredTempRepresentation.RuntimeRc
            && fact.Producer == LoweredTempProducerKind.ConcatStrTip;

    private int TcoBackEdgeNormalizeRuntimeManagedArg(
        int sourceTemp,
        TypeRef argType,
        bool alreadyRuntimeManaged,
        bool aliasesPredecessor,
        bool consumedListTail)
    {
        if (alreadyRuntimeManaged)
        {
            return TcoBackEdgeRetainRuntimeManagedArg(sourceTemp, argType, aliasesPredecessor);
        }

        if (consumedListTail && argType is TypeRef.TList)
        {
            return EmitRuntimeManagedNullableDup(sourceTemp);
        }

        int normalizedTemp = NewTemp();
        if (argType is TypeRef.TList list)
        {
            if (TryGetRuntimeManagedListHeadCopy(list.Element, out IrInst.ListHeadCopyKind headCopy))
            {
                Emit(new IrInst.CopyOutList(
                    normalizedTemp,
                    sourceTemp,
                    headCopy,
                    RuntimeManaged: true,
                    IrInst.CopyOutPurpose.RcNormalization));
            }
            else
            {
                return EmitRuntimeManagedTcoListDeepCopy(sourceTemp, list.Element);
            }
        }
        else if (argType is TypeRef.TFun)
        {
            Emit(new IrInst.CopyOutClosure(
                normalizedTemp,
                sourceTemp,
                RuntimeManaged: true,
                IrInst.CopyOutPurpose.RcNormalization));
        }
        else if (argType is TypeRef.TTuple tuple
            && !tuple.Elements.All(element => CanArenaReset(Prune(element))))
        {
            return EmitRuntimeManagedTcoDeepCopy(sourceTemp, tuple);
        }
        else if (argType is TypeRef.TNamedType named && !CanCopyOutAdt(named, out _))
        {
            return EmitRuntimeManagedTcoDeepCopy(sourceTemp, named);
        }
        else
        {
            Emit(new IrInst.CopyOutArena(
                normalizedTemp,
                sourceTemp,
                TcoRuntimeManagedCopySize(argType),
                RuntimeManaged: true,
                IrInst.CopyOutPurpose.RcNormalization));
        }

        MarkRuntimeManagedTemp(normalizedTemp);
        return normalizedTemp;
    }

    private int TcoBackEdgeRetainRuntimeManagedArg(
        int sourceTemp,
        TypeRef argType,
        bool aliasesPredecessor)
    {
        if (!aliasesPredecessor)
        {
            return sourceTemp;
        }

        if (MayUseEmptyListRepresentation(argType))
        {
            return EmitRuntimeManagedNullableDup(sourceTemp);
        }

        int duplicatedTemp = NewTemp();
        Emit(new IrInst.RcDup(duplicatedTemp, sourceTemp, RuntimeManaged: true));
        MarkRuntimeManagedTemp(duplicatedTemp);
        return duplicatedTemp;
    }

    /// <summary>
    /// Duplicates a runtime-managed value whose type admits the empty-list representation. The
    /// duplicate is identity-preserving, so an empty value is its own result and codegen skips the
    /// reference-count update rather than reading a header the null pointer does not have.
    /// </summary>
    private int EmitRuntimeManagedNullableDup(int sourceTemp)
    {
        int duplicatedTemp = NewTemp();
        Emit(new IrInst.RcDup(duplicatedTemp, sourceTemp, RuntimeManaged: true, MayBeEmpty: true));
        MarkRuntimeManagedTemp(duplicatedTemp);
        return duplicatedTemp;
    }

    private void TcoBackEdgeDropRuntimeManagedArg(PendingTcoReset info, int index, TypeRef argType)
    {
        int activeSlot = info.RuntimeManagedParamActiveSlots[index];
        if (activeSlot < 0)
        {
            TcoBackEdgeDropRuntimeManagedArgCore(info, index, argType);
            return;
        }

        int activeTemp = NewTemp();
        string skipLabel = NewLabel("rc_tco_drop_inactive");
        Emit(new IrInst.LoadLocal(activeTemp, activeSlot));
        Emit(new IrInst.JumpIfFalse(activeTemp, skipLabel));
        TcoBackEdgeDropRuntimeManagedArgCore(info, index, argType);
        Emit(new IrInst.Label(skipLabel));
    }

    private void TcoBackEdgeDropRuntimeManagedArgCore(PendingTcoReset info, int index, TypeRef argType)
    {
        int sourceTemp = info.OldRuntimeParamTemps[index];
        if (argType is TypeRef.TFun)
        {
            if (info.RuntimeManagedClosureActiveSlots[index] < 0)
            {
                return;
            }
            EmitRuntimeManagedClosureDropIfActive(
                sourceTemp,
                info.RuntimeManagedClosureActiveSlots[index]);
            return;
        }

        if (argType is TypeRef.TList list)
        {
            EmitRuntimeManagedListDrop(sourceTemp, list.Element);
            return;
        }

        if (argType is TypeRef.TTuple tuple)
        {
            EmitRuntimeManagedTupleDrop(sourceTemp, tuple);
            return;
        }

        if (argType is TypeRef.TNamedType named)
        {
            EmitRuntimeManagedAdtDrop(sourceTemp, named);
            return;
        }

        Emit(new IrInst.RcDrop(
            sourceTemp,
            TcoRuntimeManagedTypeName(argType),
            OwnerSlot: -1,
            RuntimeManaged: true));
    }

    private bool TcoBackEdgeRuntimeManagedArgCanReset(PendingTcoReset info, int index)
    {
        if (info.ParamPlacements[index]?.Representation != TcoPlacementRepresentation.RuntimeRc)
        {
            return false;
        }

        TypeRef type = Prune(info.ArgTypes[index]);
        return IsRcEligibleScalarTupleOrAdtType(type)
            || type switch
            {
                TypeRef.TList list => CanRuntimeManageTcoListElement(list.Element)
                    && (info.PassThrough[index]
                        || info.FreshListRebuild[index]
                        || info.ConsumedListTail[index]
                        || info.SingleFreshCons[index] && info.RuntimeManagedArgResults[index]),
                TypeRef.TFun => info.RuntimeManagedArgResults[index],
                _ => false,
            };
    }

    private int EmitRuntimeManagedTcoDeepCopy(int sourceTemp, TypeRef type)
    {
        TypeRef valueType = Prune(type);
        if (CanArenaReset(valueType))
        {
            return sourceTemp;
        }

        int resultTemp = NewTemp();
        switch (valueType)
        {
            case TypeRef.TStr or TypeRef.TBytes or TypeRef.TBigInt:
                Emit(new IrInst.CopyOutArena(
                    resultTemp,
                    sourceTemp,
                    TcoRuntimeManagedCopySize(valueType),
                    RuntimeManaged: true,
                    IrInst.CopyOutPurpose.RcNormalization));
                break;
            case TypeRef.TList list:
                if (TryGetRuntimeManagedListHeadCopy(list.Element, out IrInst.ListHeadCopyKind headCopy))
                {
                    Emit(new IrInst.CopyOutList(
                        resultTemp,
                        sourceTemp,
                        headCopy,
                        RuntimeManaged: true,
                        IrInst.CopyOutPurpose.RcNormalization));
                }
                else
                {
                    return EmitRuntimeManagedTcoListDeepCopy(sourceTemp, list.Element);
                }
                break;
            case TypeRef.TTuple tuple:
                Emit(new IrInst.Alloc(resultTemp, tuple.Elements.Count * 8, RuntimeManaged: true));
                for (int i = 0; i < tuple.Elements.Count; i++)
                {
                    int childTemp = NewTemp();
                    Emit(new IrInst.LoadMemOffset(childTemp, sourceTemp, i * 8));
                    int copiedChild = EmitRuntimeManagedTcoDeepCopy(childTemp, tuple.Elements[i]);
                    Emit(new IrInst.StoreMemOffset(resultTemp, i * 8, copiedChild));
                }
                break;
            case TypeRef.TNamedType named when CanCopyOutAdt(named, out int sizeBytes):
                Emit(new IrInst.CopyOutArena(
                    resultTemp,
                    sourceTemp,
                    sizeBytes,
                    RuntimeManaged: true,
                    IrInst.CopyOutPurpose.RcNormalization));
                break;
            case TypeRef.TNamedType named when CanRuntimeManageTcoAdt(named):
                return EmitRuntimeManagedTcoAdtDeepCopy(sourceTemp, named);
            default:
                throw new InvalidOperationException("Unsupported runtime-managed TCO aggregate.");
        }

        MarkRuntimeManagedTemp(resultTemp);
        return resultTemp;
    }

    private bool CanRuntimeManageTcoAdt(TypeRef.TNamedType named)
        => CanRuntimeManageAdt(named)
            || CanRuntimeManageOwnedChildAdt(named)
            || CanRuntimeManageTcoOwnedChildAdt(named);

    private int EmitRuntimeManagedTcoListDeepCopy(int sourceTemp, TypeRef elementType)
    {
        int currentSlot = NewLocal();
        int firstSlot = NewLocal();
        int lastSlot = NewLocal();
        int zeroTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(zeroTemp, 0));
        Emit(new IrInst.StoreLocal(currentSlot, sourceTemp));
        Emit(new IrInst.StoreLocal(firstSlot, zeroTemp));
        Emit(new IrInst.StoreLocal(lastSlot, zeroTemp));

        string loopLabel = NewLabel("rc_normalize_list");
        string endLabel = NewLabel("rc_normalize_list_end");
        Emit(new IrInst.Label(loopLabel));
        int currentTemp = NewTemp();
        Emit(new IrInst.LoadLocal(currentTemp, currentSlot));
        int nonEmptyTemp = NewTemp();
        Emit(new IrInst.CmpIntNe(nonEmptyTemp, currentTemp, zeroTemp));
        Emit(new IrInst.JumpIfFalse(nonEmptyTemp, endLabel));

        int headTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(
            headTemp,
            currentTemp,
            HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListHeadIndex)));
        int tailTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(
            tailTemp,
            currentTemp,
            HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListTailIndex)));
        int copiedHead = EmitRuntimeManagedTcoDeepCopy(headTemp, elementType);
        int cellTemp = NewTemp();
        Emit(new IrInst.Alloc(cellTemp, HeapLayouts.List.FixedAllocationSizeBytes, RuntimeManaged: true));
        Emit(new IrInst.StoreMemOffset(
            cellTemp,
            HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListHeadIndex),
            copiedHead));
        Emit(new IrInst.StoreMemOffset(
            cellTemp,
            HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListTailIndex),
            zeroTemp));

        EmitRuntimeManagedTcoListAppendCell(firstSlot, lastSlot, zeroTemp, cellTemp);
        Emit(new IrInst.StoreLocal(currentSlot, tailTemp));
        Emit(new IrInst.Jump(loopLabel));

        Emit(new IrInst.Label(endLabel));
        int resultTemp = NewTemp();
        Emit(new IrInst.LoadLocal(resultTemp, firstSlot));
        MarkRuntimeManagedTemp(resultTemp);
        return resultTemp;
    }

    private void EmitRuntimeManagedTcoListAppendCell(
        int firstSlot,
        int lastSlot,
        int zeroTemp,
        int cellTemp)
    {
        string firstCellLabel = NewLabel("rc_normalize_list_first");
        string linkedLabel = NewLabel("rc_normalize_list_linked");
        int firstTemp = NewTemp();
        Emit(new IrInst.LoadLocal(firstTemp, firstSlot));
        int hasFirstTemp = NewTemp();
        Emit(new IrInst.CmpIntNe(hasFirstTemp, firstTemp, zeroTemp));
        Emit(new IrInst.JumpIfFalse(hasFirstTemp, firstCellLabel));
        int lastTemp = NewTemp();
        Emit(new IrInst.LoadLocal(lastTemp, lastSlot));
        Emit(new IrInst.StoreMemOffset(
            lastTemp,
            HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListTailIndex),
            cellTemp));
        Emit(new IrInst.Jump(linkedLabel));
        Emit(new IrInst.Label(firstCellLabel));
        Emit(new IrInst.StoreLocal(firstSlot, cellTemp));
        Emit(new IrInst.Label(linkedLabel));
        Emit(new IrInst.StoreLocal(lastSlot, cellTemp));
    }

    private int EmitRuntimeManagedTcoAdtDeepCopy(int sourceTemp, TypeRef.TNamedType named)
    {
        if (named.Symbol.Constructors.Count == 1)
        {
            return EmitRuntimeManagedTcoConstructorDeepCopy(
                sourceTemp,
                named,
                named.Symbol.Constructors[0]);
        }

        int resultSlot = NewLocal();
        int tagTemp = NewTemp();
        Emit(new IrInst.GetAdtTag(tagTemp, sourceTemp));
        List<(long Tag, string Label)> cases = named.Symbol.Constructors
            .Select(constructor => ((long)GetConstructorTag(constructor), NewLabel("rc_normalize_adt")))
            .ToList();
        string endLabel = NewLabel("rc_normalize_adt_end");
        Emit(new IrInst.SwitchTag(tagTemp, cases, cases[0].Label));
        for (int i = 0; i < named.Symbol.Constructors.Count; i++)
        {
            Emit(new IrInst.Label(cases[i].Label));
            int branchTemp = EmitRuntimeManagedTcoConstructorDeepCopy(
                sourceTemp,
                named,
                named.Symbol.Constructors[i]);
            Emit(new IrInst.StoreLocal(resultSlot, branchTemp));
            Emit(new IrInst.Jump(endLabel));
        }

        Emit(new IrInst.Label(endLabel));
        int resultTemp = NewTemp();
        Emit(new IrInst.LoadLocal(resultTemp, resultSlot));
        MarkRuntimeManagedTemp(resultTemp);
        return resultTemp;
    }

    private int EmitRuntimeManagedTcoConstructorDeepCopy(
        int sourceTemp,
        TypeRef.TNamedType named,
        ConstructorSymbol constructor)
    {
        int resultTemp = NewTemp();
        Emit(new IrInst.CopyOutArena(
            resultTemp,
            sourceTemp,
            HeapLayouts.Adt.AllocationSizeBytes(constructor.Arity),
            RuntimeManaged: true,
            IrInst.CopyOutPurpose.RcNormalization));
        List<OrdinaryHeapLayoutChild> children =
            GetOwnedOrdinaryHeapChildren(named, constructor);
        foreach (OrdinaryHeapLayoutChild child in children)
        {
            int childTemp = NewTemp();
            Emit(new IrInst.GetAdtField(childTemp, sourceTemp, child.Index));
            int copiedChild = EmitRuntimeManagedTcoDeepCopy(childTemp, child.Type);
            Emit(new IrInst.SetAdtField(resultTemp, child.Index, copiedChild));
        }

        MarkRuntimeManagedTemp(resultTemp);
        return resultTemp;
    }

    private int TcoRuntimeManagedCopySize(TypeRef type)
        => type switch
        {
            TypeRef.TBigInt => IrInst.CopyOutArena.BigIntSize,
            TypeRef.TTuple tuple => tuple.Elements.Count * 8,
            TypeRef.TNamedType named when CanCopyOutAdt(named, out int sizeBytes) => sizeBytes,
            _ => -1,
        };

    private static string TcoRuntimeManagedTypeName(TypeRef type)
        => type switch
        {
            TypeRef.TBigInt => "BigInt",
            TypeRef.TTuple => "Tuple",
            TypeRef.TNamedType named => named.Symbol.Name,
            TypeRef.TFun => "Function",
            _ => "String",
        };

    /// <summary>
    /// Emits the plain per-iteration reset when every argument is reset-safe; returns false (and
    /// emits nothing) when some argument needs a copy-out instead.
    /// </summary>
    private bool TcoBackEdgeTryEmitPlainReset(PendingTcoReset info, int tcoPreRestoreEndSlot)
    {
        var argTypes = info.ArgTypes;

        // An arg needs no copy-out at the reset if it's a copy type (inline), a resource handle
        // (a scalar fd/HANDLE — no heap reference, and a reset never Drops it), a loop-invariant
        // pass-through (holds the pre-loop value, below the watermark), or a fully-reusing
        // specialized accumulator (rewritten in place below the watermark).
        bool ArgResetSafe(int i) => CanArenaReset(argTypes[i])
            || IsResourceHandleType(argTypes[i])
            || info.PassThrough[i]
            || info.StableAccArg[i];

        if (!Enumerable.Range(0, argTypes.Length).All(ArgResetSafe))
        {
            return false;
        }
        // All copy types and/or in-place-reused accumulators: plain reset. Skipped
        // while a one-shot capability post pushed this iteration is still pending — the
        // post (and its captures) lives in the iteration's allocations.
        var tcoResetSkipLabel = BeginLivePostsGuard();
        Emit(new IrInst.RestoreArenaState(info.ArenaCursorSlot, info.ArenaEndSlot, tcoPreRestoreEndSlot) { CoroutineLoop = info.CoroutineLoop });
        Emit(new IrInst.ReclaimArenaChunks(info.ArenaEndSlot, tcoPreRestoreEndSlot) { CoroutineLoop = info.CoroutineLoop });
        EndLivePostsGuard(tcoResetSkipLabel);
        return true;
    }

    private bool TcoBackEdgeConsumedInlineListTailCanReset(PendingTcoReset info, int index)
        => info.ConsumedListTail[index]
            && Prune(info.ArgTypes[index]) is TypeRef.TList list
            && CanArenaReset(Prune(list.Element));

    // The whole-list DeepAdt clone is licensed per ARG, not per type: it costs O(length)
    // at every back-edge, affordable only when the body already paid O(length) rebuilding the
    // list this iteration (info.FreshListRebuild). A threaded/consumed list (a bare var, a
    // pattern-derived tail, a cons onto the accumulator) can share unbounded structure with
    // the previous iteration — cloning it per back-edge multiplies the loop's cost by the
    // list length (1brc's merge phase regressed ~400x) — so it downgrades to None here.
    private CopyOutKind TcoBackEdgeArgCopyOutKind(PendingTcoReset info, int i, out int sizeBytes, out IrInst.ListHeadCopyKind headCopy)
    {
        var argKind = GetTcoCopyOutKind(info.ArgTypes[i], out sizeBytes, out headCopy);
        if (argKind == CopyOutKind.DeepAdt
            && Prune(info.ArgTypes[i]) is TypeRef.TList
            && !info.FreshListRebuild[i])
        {
            return CopyOutKind.None;
        }

        return argKind;
    }

    // Check whether every heap-type arg can be copy-outed. The single-cell list copy-outs
    // preserve only the list's TOP cons cell across the reset, valid only for the
    // `head :: <loop accumulator param>` shape (captured in SingleFreshCons); any other list
    // shape — except a DeepAdt list, which the synthesized copier clones WHOLE — disqualifies
    // the reset (those iterations simply don't reclaim).
    private bool TcoBackEdgeAllArgsCopyable(PendingTcoReset info)
    {
        var argTypes = info.ArgTypes;
        for (int i = 0; i < argTypes.Length; i++)
        {
            if (info.PassThrough[i])
            {
                continue;
            }

            if (!CanArenaReset(argTypes[i])
                && TcoBackEdgeArgCopyOutKind(info, i, out _, out _) == CopyOutKind.None)
            {
                return false;
            }

            if (Prune(argTypes[i]) is TypeRef.TList
                && TcoBackEdgeArgCopyOutKind(info, i, out _, out _) != CopyOutKind.DeepAdt
                && !info.SingleFreshCons[i])
            {
                return false;
            }
        }

        return true;
    }

    // Reset to the FIXED loop-entry watermark (reclaiming the previous iteration's whole-value
    // accumulator copies) instead of the per-iteration one WHEN every arg is a non-sharing
    // whole-value type: a copy type, a resource handle, a String, a BigInt, a self-contained
    // DeepAdt clone (ADT/tuple/list), or a loop-invariant pass-through. A single-fresh-cons
    // list shares its tail with the prior accumulator, which sits below the per-iteration
    // watermark and would be overwritten by a fixed-mark reset — it keeps the advancing one.
    // This is what turns a growing String/BigInt accumulator from O(N^2) to O(N) resident.
    private bool TcoBackEdgeUseFixedWatermark(PendingTcoReset info)
    {
        var argTypes = info.ArgTypes;
        return info.FixedCursorSlot >= 0
            && Enumerable.Range(0, argTypes.Length).All(i =>
                info.PassThrough[i]
                || CanArenaReset(argTypes[i])
                || IsResourceHandleType(argTypes[i])
                || Prune(argTypes[i]) is TypeRef.TStr or TypeRef.TBigInt
                || (Prune(argTypes[i]) is TypeRef.TNamedType n && (CanCopyOutAdt(n, out _) || CanDeepCopyOutAdt(n)))
                || (Prune(argTypes[i]) is TypeRef.TTuple && IsDeepCopyOutSafeType(Prune(argTypes[i])))
                || (Prune(argTypes[i]) is TypeRef.TList && TcoBackEdgeArgCopyOutKind(info, i, out _, out _) == CopyOutKind.DeepAdt));
    }

    // Amortized compaction (fixed watermark only): copying the WHOLE growing accumulator at
    // every back-edge is O(N^2) TIME (each of N iterations copies O(N) live bytes). Instead,
    // skip the copy-out + reset while the arena has grown less than 2x the live size recorded
    // at the last compaction (+ slack) — the skipped iterations just keep allocating above W.
    // Each compaction then reclaims at least as much garbage as it copies live bytes, so total
    // copy work is LINEAR in bytes allocated (the doubling amortization) and resident memory
    // stays bounded by ~3x the live accumulator. Skipping is trivially safe: it is exactly the
    // no-reset behavior every non-qualifying loop already has. The advancing-mark path is NOT
    // amortized — its single-cell list copies must track the moving mark every iteration.
    // Returns the skip label when the check was emitted, null otherwise.
    private string? TcoBackEdgeEmitCompactionCheck(PendingTcoReset info, bool useFixedWatermark)
    {
        if (!useFixedWatermark || info.CompactionSizeSlot < 0)
        {
            return null;
        }

        int curCursorSlot = NewLocal();
        int curEndSlot = NewLocal();
        Emit(new IrInst.SaveArenaState(curCursorSlot, curEndSlot));
        int curTemp = NewTemp();
        Emit(new IrInst.LoadLocal(curTemp, curCursorSlot));
        int wTemp = NewTemp();
        Emit(new IrInst.LoadLocal(wTemp, info.FixedCursorSlot));
        int growthTemp = NewTemp();
        Emit(new IrInst.SubInt(growthTemp, curTemp, wTemp));
        growthTemp = TcoBackEdgeNetReservationSpans(info, growthTemp);
        int mTemp = NewTemp();
        Emit(new IrInst.LoadLocal(mTemp, info.CompactionSizeSlot));
        int oneTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(oneTemp, 1));
        int twoMTemp = NewTemp();
        Emit(new IrInst.ShlInt(twoMTemp, mTemp, oneTemp));
        int slackTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(slackTemp, TcoCompactionSlackBytes));
        int thresholdTemp = NewTemp();
        Emit(new IrInst.AddInt(thresholdTemp, twoMTemp, slackTemp));
        int growthGtTemp = NewTemp();
        Emit(new IrInst.CmpIntGt(growthGtTemp, growthTemp, thresholdTemp));
        // cursor - W is only meaningful while the cursor is still in W's chunk; once the arena
        // grew into another chunk the difference is garbage (distinct mmaps). A crossed chunk
        // also means at least a chunk's worth of allocation since W — compact unconditionally.
        int wEndTemp = NewTemp();
        Emit(new IrInst.LoadLocal(wEndTemp, info.FixedEndSlot));
        int curEndTemp = NewTemp();
        Emit(new IrInst.LoadLocal(curEndTemp, curEndSlot));
        int endsDifferTemp = NewTemp();
        Emit(new IrInst.CmpIntNe(endsDifferTemp, curEndTemp, wEndTemp));
        int needTemp = NewTemp();
        Emit(new IrInst.OrInt(needTemp, growthGtTemp, endsDifferTemp));
        string compactSkipLabel = $"tco_compact_skip_{_nextLambdaId++}";
        Emit(new IrInst.JumpIfFalse(needTemp, compactSkipLabel));
        return compactSkipLabel;
    }

    // An active string reservation (ConcatStrTip) is LIVE capacity, not garbage — without
    // subtracting its span, a fresh doubling reservation (~2x live) plus the live data
    // always exceeds the 2M threshold, so every doubling would resonate into an immediate
    // compact -> zero -> re-reserve -> compact cycle: one full copy per append, quadratic
    // again. Netting the span out restores the intended accounting (only abandoned copies
    // and iteration scraps count), keeping compactions geometric.
    private int TcoBackEdgeNetReservationSpans(PendingTcoReset info, int growthTemp)
    {
        for (int r = 0; r < info.ArgResvStartSlots.Length; r++)
        {
            if (info.ArgResvStartSlots[r] < 0 || info.RuntimeManagedArgResults[r])
            {
                continue;
            }

            int resvStartTemp = NewTemp();
            Emit(new IrInst.LoadLocal(resvStartTemp, info.ArgResvStartSlots[r]));
            int resvEndTemp = NewTemp();
            Emit(new IrInst.LoadLocal(resvEndTemp, info.ArgResvEndSlots[r]));
            int resvSpanTemp = NewTemp();
            Emit(new IrInst.SubInt(resvSpanTemp, resvEndTemp, resvStartTemp));
            int nettedTemp = NewTemp();
            Emit(new IrInst.SubInt(nettedTemp, growthTemp, resvSpanTemp));
            growthTemp = nettedTemp;
        }

        return growthTemp;
    }

    // Phase A: copy every heap arg UP above the current cursor; -1 marks args with no up-copy.
    private int[] TcoBackEdgeEmitPhaseAUpCopies(PendingTcoReset info)
    {
        var argTypes = info.ArgTypes;
        var upCopyTemps = new int[argTypes.Length];
        for (int i = 0; i < argTypes.Length; i++)
        {
            // A loop-invariant pass-through arg lives below the fixed watermark; its slot already
            // holds it — no copy at all.
            if (info.PassThrough[i] || CanArenaReset(argTypes[i]))
            {
                upCopyTemps[i] = -1;
                continue;
            }

            var kind = TcoBackEdgeArgCopyOutKind(info, i, out int sizeBytes, out var headCopy);
            if (kind == CopyOutKind.None)
            {
                upCopyTemps[i] = -1;
                continue;
            }

            // A deep-ADT copy returns its own temp (a self-contained recursive clone), rather than
            // writing into a pre-allocated dest like the shallow kinds.
            //
            // It is cloned TWICE (a clone of the clone). Phase B writes its down-copy at [W, W+S)
            // while reading the up-copy at [W+B, W+B+S), where B is what the loop body allocated
            // this iteration — they overlap whenever B < S. The shallow kinds are safe because the
            // fresh accumulator itself was just body-allocated (B >= S), but a deep clone's size
            // includes copier env/closure overhead beyond the raw value, and a list-tail argument
            // may not be body-allocated at all (B ~ 0) — an overlapping, skewed Phase B copy then
            // reads its own partially-written output. The second clone starts at least one full
            // clone-size above W, so Phase B's destination end (W + S) never reaches its source
            // start (W + B + S): disjoint for any B >= 0, for any number of DeepAdt args.
            upCopyTemps[i] = kind == CopyOutKind.DeepAdt
                ? EmitDeepCopy(EmitDeepCopy(info.ArgTemps[i], argTypes[i]), argTypes[i])
                : NewTemp();
            if (kind != CopyOutKind.DeepAdt)
            {
                EmitTcoCopyOut(kind, upCopyTemps[i], info.ArgTemps[i], sizeBytes, headCopy);
            }
        }

        return upCopyTemps;
    }

    private void TcoBackEdgeEmitResetAndZeroReservations(PendingTcoReset info, int resetCursorSlot, int resetEndSlot, int tcoPreRestoreEndSlot)
    {
        // Reset (pointer reset only, no chunk freeing): cursor → W.
        Emit(new IrInst.RestoreArenaState(resetCursorSlot, resetEndSlot, tcoPreRestoreEndSlot) { CoroutineLoop = info.CoroutineLoop });

        // The reset reclaims any string reservations (they live above the watermark) — zero their
        // slots; the reserving Phase-B copy below writes fresh bounds for the affine args.
        if (info.ArgResvStartSlots.Any(s => s >= 0))
        {
            int resvZeroTemp = NewTemp();
            Emit(new IrInst.LoadConstInt(resvZeroTemp, 0));
            for (int r = 0; r < info.ArgResvStartSlots.Length; r++)
            {
                if (info.ArgResvStartSlots[r] >= 0 && !info.RuntimeManagedArgResults[r])
                {
                    Emit(new IrInst.StoreLocal(info.ArgResvStartSlots[r], resvZeroTemp));
                    Emit(new IrInst.StoreLocal(info.ArgResvEndSlots[r], resvZeroTemp));
                }
            }
        }
    }

    // Phase B: copy each up-copy down to W and store into the slot.
    private void TcoBackEdgeEmitPhaseBDownCopies(PendingTcoReset info, int[] upCopyTemps)
    {
        var argTypes = info.ArgTypes;
        for (int i = 0; i < argTypes.Length; i++)
        {
            if (upCopyTemps[i] < 0)
                continue;

            var kind = TcoBackEdgeArgCopyOutKind(info, i, out int sizeBytes, out var headCopy);
            int copyDest;
            if (kind == CopyOutKind.DeepAdt)
            {
                copyDest = EmitDeepCopy(upCopyTemps[i], argTypes[i]);
            }
            else if (info.ArgResvStartSlots[i] >= 0 && kind == CopyOutKind.Shallow && sizeBytes == -1)
            {
                // An affine string accumulator's down-copy RESERVES (ConcatStrTip with an empty
                // right; the slots were just zeroed, so its fallback reserves 2x and records fresh
                // bounds). Without this, the first post-compaction append re-reserves in a fresh
                // allocation — which, for an accumulator larger than the watermark chunk's
                // remainder, lands in ANOTHER chunk and re-triggers the crossed-chunk compaction
                // every back-edge (one full copy per append). Reserving here keeps the accumulator
                // and its headroom exactly where the rebase puts the watermark.
                int emptyTemp = NewTemp();
                Emit(new IrInst.LoadConstStr(emptyTemp, InternString(string.Empty)));
                copyDest = NewTemp();
                Emit(new IrInst.ConcatStrTip(copyDest, upCopyTemps[i], emptyTemp, info.ArgResvStartSlots[i], info.ArgResvEndSlots[i]));
            }
            else
            {
                copyDest = NewTemp();
                EmitTcoCopyOut(kind, copyDest, upCopyTemps[i], sizeBytes, headCopy);
            }

            Emit(new IrInst.StoreLocal(info.ParamSlots[i], copyDest));
        }
    }

    // Record the compacted live size (cursor - W) for the next amortization trigger. When
    // the down-copy overflowed into a NEW chunk (the accumulator outgrew W's chunk), the
    // difference is garbage — instead REBASE the fixed watermark to the post-copy position
    // in the new chunk and restart the epoch (M = 0). The old chunk's region above the old
    // W is stranded, but crossings happen only when the live size doubles past a chunk
    // (the grow path gives oversized chunks 2x headroom), so the stranded generations form
    // a geometric series bounded by ~2x the final live size. After the rebase, appends and
    // compactions run entirely inside the roomy new chunk.
    private void TcoBackEdgeEmitCompactionRecord(PendingTcoReset info, string compactSkipLabel)
    {
        int afterCursorSlot = NewLocal();
        int afterEndSlot = NewLocal();
        Emit(new IrInst.SaveArenaState(afterCursorSlot, afterEndSlot));
        int afterTemp = NewTemp();
        Emit(new IrInst.LoadLocal(afterTemp, afterCursorSlot));
        int wAfterTemp = NewTemp();
        Emit(new IrInst.LoadLocal(wAfterTemp, info.FixedCursorSlot));
        int liveTemp = NewTemp();
        Emit(new IrInst.SubInt(liveTemp, afterTemp, wAfterTemp));
        int afterEndTemp = NewTemp();
        Emit(new IrInst.LoadLocal(afterEndTemp, afterEndSlot));
        int wEndAfterTemp = NewTemp();
        Emit(new IrInst.LoadLocal(wEndAfterTemp, info.FixedEndSlot));
        int sameChunkTemp = NewTemp();
        Emit(new IrInst.CmpIntEq(sameChunkTemp, afterEndTemp, wEndAfterTemp));
        string rebaseLabel = $"tco_compact_rebase_{_nextLambdaId++}";
        string recordedLabel = $"tco_compact_recorded_{_nextLambdaId++}";
        Emit(new IrInst.JumpIfFalse(sameChunkTemp, rebaseLabel));
        Emit(new IrInst.StoreLocal(info.CompactionSizeSlot, liveTemp));
        Emit(new IrInst.Jump(recordedLabel));
        Emit(new IrInst.Label(rebaseLabel));
        // W' = the new chunk's allocation start, recovered from the chunk FOOTER (the usable
        // end holds the chunk's own base; allocations start at base + 8). The down-copy landed
        // exactly there, so the live accumulator sits AT W' — future compactions copy down to
        // W' with the accumulator's full size counted in the body-allocation term (B >= S), and
        // in-place appends see acc >= W' immediately. M restarts at the region size.
        int chunkBaseTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(chunkBaseTemp, afterEndTemp, 0));
        int eightTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(eightTemp, 8));
        int rebaseCursorTemp = NewTemp();
        Emit(new IrInst.AddInt(rebaseCursorTemp, chunkBaseTemp, eightTemp));
        Emit(new IrInst.StoreLocal(info.FixedCursorSlot, rebaseCursorTemp));
        Emit(new IrInst.StoreLocal(info.FixedEndSlot, afterEndTemp));
        int rebasedLiveTemp = NewTemp();
        Emit(new IrInst.SubInt(rebasedLiveTemp, afterTemp, rebaseCursorTemp));
        Emit(new IrInst.StoreLocal(info.CompactionSizeSlot, rebasedLiveTemp));
        Emit(new IrInst.Label(recordedLabel));
        Emit(new IrInst.Label(compactSkipLabel));
    }

    /// <summary>
    /// Replaces every <see cref="IrInst.TcoResetPending"/> placeholder with the real arena block (or
    /// with nothing, when the resolved types do not qualify).
    /// Runs at the end of lowering, after inference, so the pruned types are as concrete as they
    /// will ever be. Splices in place per function, temporarily pointing
    /// <c>_inst</c> and the temp/local counters at the target function.
    /// </summary>
    private void ResolveDeferredTcoResets()
    {
        if (_pendingTcoResets.Count == 0)
        {
            return;
        }

        // The entry instruction list (_inst) is spliced in place with the live counters.
        if (_inst.Any(x => x is IrInst.TcoResetPending))
        {
            var entryOriginal = new List<IrInst>(_inst);
            _inst.Clear();
            foreach (var inst in entryOriginal)
            {
                if (inst is IrInst.TcoResetPending p && _pendingTcoResets.TryGetValue(p.Id, out var info))
                {
                    EmitTcoBackEdgeArenaBlock(info);
                }
                else
                {
                    _inst.Add(inst);
                }
            }
        }

        // Lifted functions: splice each, with the counters swapped to the function's. Synthesized
        // copiers appended by the emission land after originalCount and never contain placeholders.
        int originalCount = _funcs.Count;
        for (int fi = 0; fi < originalCount; fi++)
        {
            var f = _funcs[fi];
            if (!f.Instructions.Any(x => x is IrInst.TcoResetPending))
            {
                continue;
            }

            ResolveDeferredTcoResetsInFunction(fi, f);
        }

        _pendingTcoResets.Clear();
    }

    // Splices one lifted function's placeholders, with the counters swapped to the function's and
    // restored afterwards.
    private void ResolveDeferredTcoResetsInFunction(int fi, IrFunction f)
    {
        var savedInst = new List<IrInst>(_inst);
        var savedTemp = _nextTempSlot;
        var savedLocal = _nextLocalSlot;
        var savedLocalNames = new Dictionary<int, string>(_localNames);
        var savedLocalTypes = new Dictionary<int, TypeRef>(_localTypes);
        Dictionary<int, LoweredTempOwnershipFact> savedTempOwnershipFacts =
            SnapshotTempOwnershipFacts();
        IrFunctionOrigin? savedActiveFunctionOrigin = _activeFunctionOrigin;
        _inst.Clear();
        _tempOwnershipFacts.Clear();
        _activeFunctionOrigin = f.Origin;
        _nextTempSlot = f.TempCount;
        _nextLocalSlot = f.LocalCount;
        foreach (var inst in f.Instructions)
        {
            if (inst is IrInst.TcoResetPending p && _pendingTcoResets.TryGetValue(p.Id, out var info))
            {
                EmitTcoBackEdgeArenaBlock(info);
            }
            else
            {
                RecordEmittedTempOwnership(inst);
                _inst.Add(inst);
            }
        }

        _funcs[fi] = f with { Instructions = new List<IrInst>(_inst), TempCount = _nextTempSlot, LocalCount = _nextLocalSlot };
        _inst.Clear();
        _inst.AddRange(savedInst);
        RestoreTempOwnershipFacts(savedTempOwnershipFacts);
        _activeFunctionOrigin = savedActiveFunctionOrigin;
        _nextTempSlot = savedTemp;
        _nextLocalSlot = savedLocal;
        _localNames.Clear();
        foreach (var kv in savedLocalNames)
        {
            _localNames[kv.Key] = kv.Value;
        }

        _localTypes.Clear();
        foreach (var kv in savedLocalTypes)
        {
            _localTypes[kv.Key] = kv.Value;
        }
    }


    // Patches the provisional string copy-outs emitted by MaterializeEscapingStringTupleElement for
    // tuple fields whose element type was an unresolved var at lowering time. Now that inference is
    // complete: a field that resolved to Str really is a runtime-managed string that would dangle
    // into the reused arena, so its copy-out stays. A field that resolved to a scalar (Int/Float/Bool)
    // never dangles — rewrite its copy-out to a plain Borrow alias (dest = src, no RC change, no
    // byte copy) so the scalar value is stored verbatim instead of being mis-read as a length-prefixed
    // string.
    private void ResolveDeferredTupleMaterializations()
    {
        if (!_hasDeferredTupleMaterializations)
        {
            return;
        }

        ResolveDeferredTupleMaterializationsIn(_inst);
        foreach (var func in _funcs)
        {
            ResolveDeferredTupleMaterializationsIn(func.Instructions);
        }
    }

    private void ResolveDeferredTupleMaterializationsIn(List<IrInst> instructions)
    {
        for (int i = 0; i < instructions.Count; i++)
        {
            if (instructions[i] is not IrInst.CopyOutArena { DeferredElementType: { } elementType } copy)
            {
                continue;
            }

            if (Prune(elementType) is TypeRef.TStr)
            {
                instructions[i] = copy with { DeferredElementType = null };
            }
            else
            {
                IrInst replacement = new IrInst.Borrow(copy.DestTemp, copy.SrcTemp)
                {
                    Location = copy.Location,
                };
                instructions[i] = replacement;
                if (ReferenceEquals(instructions, _inst))
                {
                    ReplaceEmittedTempOwnership(copy, replacement);
                }
            }
        }
    }

    private LoweredValue LowerExpr(
        Expr e,
        LoweredValueRequest request = default)
    {
        var previousExpr = _currentSourceExpr;
        _currentSourceExpr = e;

        // Innermost body of a helper being lowered as an async loop: emit the transparent coroutine
        // task instead of lowering the body inline (see HelperCoroutineInfo).
        if (_pendingHelperCoroutine is { } pendingHelper && ReferenceEquals(e, pendingHelper.Body))
        {
            _pendingHelperCoroutine = null;
            var helperLowered = LowerHelperCoroutineTask(pendingHelper);
            RecordExprHoverType(e, helperLowered.Item2);
            LoweredValue helperValue = CreateLoweredValue(helperLowered.Item1, helperLowered.Item2);
            _currentSourceExpr = previousExpr;
            return helperValue;
        }

        // A bare trait-method reference eta-expands to a lambda (LowerBareTraitMethodReference) and
        // needs the same early expected-type unification an ordinary Expr.Lambda argument gets
        // below, so its parameter type is pinned before its body's constraint is resolved instead of
        // staying an unconstrained variable that can never be discharged.
        bool forwardsExpectedType = e is Expr.Let or Expr.LetResult or Expr.LetRecursive or Expr.Lambda
            or RecursiveGroupExpr or Expr.If or Expr.Match or Expr.Handle or Expr.Call
            or Expr.ListLit or Expr.Cons
            || e is Expr.QualifiedVar qualifiedTraitMethod && TryGetTraitMethod(qualifiedTraitMethod, out _, out _);
        TypeRef? expectedType = request.ExpectedType;
        var lowered = LowerExprDispatch(
            e,
            forwardsExpectedType ? request : request.WithoutExpectedType());
        if (expectedType is not null && !forwardsExpectedType)
        {
            Unify(expectedType, lowered.Type);
            lowered = (lowered.Temp, Prune(lowered.Type));
        }

        RecordExprHoverType(e, lowered.Type);
        LoweredValue value = CreateLoweredValue(lowered.Temp, lowered.Type);
        _currentSourceExpr = previousExpr;
        return value;
    }

    private (int Temp, TypeRef Type) LowerExprDispatch(
        Expr e,
        LoweredValueRequest request)
    {
        return e switch
        {
            Expr.IntLit lit => LowerInt(lit),
            Expr.UIntLit lit => LowerUInt(lit),
            Expr.BigIntLit lit => LowerBigIntLit(lit, request),
            Expr.FloatLit lit => LowerFloat(lit),
            Expr.StrLit str => LowerStr(str),
            Expr.RuneLit rune => LowerRune(rune),
            Expr.BoolLit b => LowerBool(b),
            Expr.Var v => LowerVar(v, request),
            Expr.QualifiedVar qv => LowerQualifiedVar(qv, request),
            Expr.Add add => LowerAdd(add, request),
            Expr.Subtract sub => LowerSubtract(sub),
            Expr.Multiply mul => LowerMultiply(mul),
            Expr.Divide div => LowerDivide(div),
            Expr.Modulo mod => LowerModulo(mod),
            Expr.BitwiseAnd bitAnd => LowerBitwiseAnd(bitAnd),
            Expr.BitwiseOr bitOr => LowerBitwiseOr(bitOr),
            Expr.BitwiseXor bitXor => LowerBitwiseXor(bitXor),
            Expr.ShiftLeft shiftLeft => LowerShiftLeft(shiftLeft),
            Expr.ShiftRight shiftRight => LowerShiftRight(shiftRight),
            Expr.BitwiseNot bitwiseNot => LowerBitwiseNot(bitwiseNot),
            Expr.LogicalNot logicalNot => LowerLogicalNot(logicalNot),
            Expr.GreaterThan gt => LowerGreaterThan(gt),
            Expr.GreaterOrEqual ge => LowerGreaterOrEqual(ge),
            Expr.LessThan lt => LowerLessThan(lt),
            Expr.LessOrEqual le => LowerLessOrEqual(le),
            Expr.Equal eq => LowerEqual(eq),
            Expr.NotEqual ne => LowerNotEqual(ne),
            Expr.ResultPipe pipe => LowerResultPipe(pipe),
            Expr.ResultMapErrorPipe pipe => LowerResultMapErrorPipe(pipe),
            Expr.Let let => LowerLet(let, request),
            Expr.LetResult letResult => LowerLetResult(letResult, request),
            Expr.LetRecursive letRecursive => LowerLetRecursive(letRecursive, request),
            RecursiveGroupExpr group => LowerRecursiveGroup(group, request),
            Expr.If iff => LowerIf(iff, request),
            Expr.Lambda lam => LowerLambda(lam, request: request),
            Expr.Call call => LowerCall(call, request),
            Expr.TupleLit tuple => LowerTupleLit(tuple, request),
            Expr.ListLit list => LowerListLit(list, request),
            Expr.Cons cons => LowerCons(cons, request),
            Expr.Match match => LowerMatch(match, request),
            Expr.Await awaitExpr => LowerAwait(awaitExpr),
            Expr.RecordLit rl => LowerRecordLit(rl, request),
            Expr.RecordUpdate ru => LowerRecordUpdate(ru, request),
            Expr.Perform perform => LowerPerform(perform, request),
            Expr.Handle handle => LowerHandle(handle, request),
            CapabilityPostExpr capabilityPost => LowerCapabilityPost(capabilityPost, request),
            _ => throw new NotSupportedException($"Unknown expr: {e.GetType().Name}")
        };
    }

    private (int, TypeRef) LowerInt(Expr.IntLit lit)
    {
        int t = NewTemp();
        Emit(new IrInst.LoadConstInt(t, lit.Value));
        return (t, new TypeRef.TInt());
    }

    private (int, TypeRef) LowerUInt(Expr.UIntLit lit)
    {
        int t = NewTemp();
        Emit(new IrInst.LoadConstInt(t, unchecked((long)lit.Value)));
        return (t, new TypeRef.TUInt(lit.Bits));
    }

    private (int, TypeRef) LowerFloat(Expr.FloatLit lit)
    {
        int t = NewTemp();
        Emit(new IrInst.LoadConstFloat(t, lit.Value));
        return (t, new TypeRef.TFloat());
    }

    private (int, TypeRef) LowerBool(Expr.BoolLit lit)
    {
        int t = NewTemp();
        Emit(new IrInst.LoadConstBool(t, lit.Value));
        return (t, new TypeRef.TBool());
    }

    private (int, TypeRef) LowerStr(Expr.StrLit str)
    {
        var label = InternString(str.Value);
        int t = NewTemp();
        Emit(new IrInst.LoadConstStr(t, label));
        return (t, new TypeRef.TStr());
    }

    private (int, TypeRef) LowerRune(Expr.RuneLit rune)
    {
        int temp = NewTemp();
        Emit(new IrInst.LoadConstInt(temp, rune.Value));
        return (temp, new TypeRef.TRune());
    }

    /// <summary>
    /// True when a local's storage slot is one of the current TCO loop's parameters that its own
    /// static type/usage shape licenses for runtime-managed representation. This single slot-set
    /// membership test is the one fact that both a plain variable reference and a match arm's
    /// trailing-value check need to ask identically; each of those two call sites folds in further
    /// facts of its own (a variable reference also accounts for its pattern-alias and per-binding
    /// ownership state; a match arm's check is combined with a separate scan of what value the arm
    /// actually produced) that do not belong in this shared predicate.
    /// </summary>
    private bool IsRuntimeManagedTcoParamSlot(Binding.Local local) =>
        _tcoCtx?.IsRuntimeManagedSlot(local.Slot) == true;

    private (int, TypeRef) LowerVar(
        Expr.Var v,
        LoweredValueRequest request = default)
    {
        var b = Lookup(v.Name);
        if (_reuseBindingSeenBySlot.Count > 0 && b is Binding.Local seenLocal
            && _reuseBindingSeenBySlot.ContainsKey(seenLocal.Slot))
        {
            _reuseBindingSeenBySlot[seenLocal.Slot]++;
        }

        if (b is null)
        {
            return LowerVarUnbound(v, request);
        }

        var result = LowerVarBound(v, b);

        RecordHoverType(
            GetSpan(v),
            v.Name,
            result.Type,
            GetHoverParameterNames(b),
            GetHoverIsParameter(b));

        // Compiler-inferred borrowing.
        // When an owned binding is accessed, emit a Borrow instruction.
        // This tells the IR that we're taking a non-owning reference — the
        // owning scope is still responsible for the Drop.
        var ownerInfo = LookupOwnedValue(v.Name);
        bool stablePatternTransfer = _loweringTcoBackEdgeArguments
            && b is Binding.Local patternLocal
            && _tcoCtx?.TryGetPatternBindingOwnership(
                patternLocal.Slot,
                out PatternBindingOwnershipFact? patternOwnership) == true
            && patternOwnership?.Ownership == PatternBindingOwnershipKind.TransferredToSameParameter
            && patternOwnership.RootParameterOrdinal >= 0
            && patternOwnership.RootParameterOrdinal < _tcoCtx.ParamSlots.Count
            && _tcoCtx.IsRuntimeManagedSlot(
                _tcoCtx.ParamSlots[patternOwnership.RootParameterOrdinal]);
        bool runtimeManagedResult = ownerInfo is { RuntimeManaged: true }
            || stablePatternTransfer
            || b is Binding.Local runtimeLocal && IsRuntimeManagedTcoParamSlot(runtimeLocal);
        if (runtimeManagedResult)
        {
            MarkRuntimeManagedTemp(result.Temp);
        }
        if (ownerInfo is not null && !ownerInfo.IsDropped)
        {
            int borrowTemp = NewTemp();
            Emit(new IrInst.Borrow(borrowTemp, result.Temp));
            ownerInfo.ActiveBorrows++;
            result = (borrowTemp, result.Type);
            if (runtimeManagedResult)
            {
                MarkRuntimeManagedTemp(borrowTemp);
            }
        }

        return result;
    }

    private (int, TypeRef) LowerVarUnbound(
        Expr.Var v,
        LoweredValueRequest request)
    {
        if (_topLevelFunctionRefs.TryGetValue(v.Name, out var topRef))
        {
            // This name is a non-inlined top-level helper (e.g. an AVL height/max reader, or a plain
            // helper called from an inlined/specialized body) referenced from an isolated scope where its
            // generation-site slot is gone. Membership in _topLevelFunctionRefs is proof it was already
            // lowered — i.e. declared earlier — so this is a genuine backward reference, NOT the Model-A
            // forward reference the ASH014 check below would otherwise (wrongly) report. It has an empty
            // closure environment, so reconstruct its closure directly from the label with a null env, and
            // instantiate its type scheme fresh for this use (polymorphic helpers unify against the
            // concrete call). Reached only when Lookup fails; normal top-level references resolve via the
            // scope and never get here, so this cannot change well-scoped code.
            int envTemp = NewTemp();
            Emit(new IrInst.LoadConstInt(envTemp, 0));
            int closTemp = NewTemp();
            Emit(new IrInst.MakeClosure(
                closTemp,
                topRef.Label,
                envTemp,
                0,
                RuntimeManaged: request.EmitsRuntime(
                    LoweredValueRuntimeRepresentation.Closure),
                ReturnsRuntimeManaged: AllowsAsyncIndependentRcPlacement && AllowsOrdinaryRcPlacement
                    && _bodyRuntimeManagedByLabel.GetValueOrDefault(topRef.Label)));
            return (closTemp, Instantiate(topRef.Scheme));
        }

        if (TryResolveConstructorSymbol(v.Name, GetSpan(v), out var ctorSym))
        {
            return LowerConstructorReference(
                ctorSym,
                ResolveSourceLocation(AstSpans.GetOrDefault(v)),
                request);
        }

        if (_topLevelBindingNames.Contains(v.Name))
        {
            // Out of scope but declared later in the file: a forward reference under Model-A
            // sequential scoping. Self/mutual recursion needs 'let rec' / 'let rec ... and ...'.
            if (Environment.GetEnvironmentVariable("ASH_DBG_REUSE") is not null)
            {
                Console.Error.WriteLine($"[reuse] ASH014 on '{v.Name}' inSpec={_inSpecialization} inlinable={_inlinableFunctions.ContainsKey(v.Name)} depth={_lambdaDepth}");
            }

            ReportDiagnostic(GetSpan(v), $"Binding '{v.Name}' is not yet declared at this point.", ForwardReferenceCode);
        }
        else if (v.Name.Length > 0 && char.IsUpper(v.Name[0]))
        {
            ReportDiagnostic(GetSpan(v), $"Unknown constructor '{v.Name}'.{BuildUnknownConstructorHint(v.Name)}");
        }
        else
        {
            ReportDiagnostic(GetSpan(v), $"Undefined variable '{v.Name}'.{BuildUnknownVariableHint(v.Name)}", DiagnosticCodes.UnknownIdentifier);
        }

        return ReturnNeverWithDummyTemp();
    }

    /// <summary>
    /// Lowers a resolved data constructor reference used as a bare value (not immediately applied):
    /// a nullary constructor lowers to its singleton runtime value, an arity &gt; 0 constructor
    /// eta-expands to a curried lambda so it can be passed around like any other function. Shared by
    /// an unqualified constructor name (<see cref="LowerVarUnbound"/>) and a qualified reference
    /// through a module alias (<see cref="LowerQualifiedVar"/>) — both name the same runtime value.
    /// </summary>
    private (int, TypeRef) LowerConstructorReference(
        ConstructorSymbol ctorSym,
        SourceLocation? location = null,
        LoweredValueRequest request = default)
    {
        return ctorSym.Arity == 0
            ? LowerNullaryConstructor(ctorSym, location: location, request: request)
            : LowerExpr(BuildConstructorLambda(ctorSym)).AsPair();
    }

    private (int Temp, TypeRef Type) LowerVarBound(Expr.Var v, Binding b)
    {
        if (TryLowerActiveTraitDictionaryReference(v, b) is { } traitReference)
        {
            return traitReference;
        }
        int temp = NewTemp();
        (int Temp, TypeRef Type) result;
        switch (b)
        {
            case Binding.Local loc:
                LoadLocalWithBytesProvenance(temp, loc, v);
                result = (temp, loc.Type);
                break;
            case Binding.Env env:
                Emit(new IrInst.LoadEnv(temp, env.Index));
                result = (temp, env.Type);
                break;
            case Binding.EnvScheme envSch:
                Emit(new IrInst.LoadEnv(temp, envSch.Index));
                result = (temp, Instantiate(envSch.S));
                break;
            case Binding.Self self:
                int envTemp = NewTemp();
                Emit(new IrInst.LoadLocal(envTemp, 0));
                Emit(new IrInst.MakeClosure(temp, self.FuncLabel, envTemp, self.EnvSizeBytes,
                    ReturnsRuntimeManaged: AllowsAsyncIndependentRcPlacement && AllowsOrdinaryRcPlacement
                        && _bodyRuntimeManagedByLabel.GetValueOrDefault(self.FuncLabel)));
                RequireTraitConstraints(self.Requirements ?? []);
                result = (temp, self.Type);
                break;

            case Binding.Intrinsic intrinsic:
                ReportDiagnostic(GetSpan(v), $"Intrinsic '{v.Name}' must be called directly.");
                Emit(new IrInst.LoadConstInt(temp, 0));
                result = (temp, intrinsic.Type);
                break;

            case Binding.ExternalFunction externalFunction:
                result = LowerExternalFunctionReference(v, externalFunction, temp);
                break;

            case Binding.PreludeValue value:
                result = value.Kind switch
                {
                    PreludeValueKind.Args => LowerProgramArgs(temp, Instantiate(value.S)),
                    _ => throw new InvalidOperationException()
                };
                break;

            case Binding.Scheme sch:
                Emit(new IrInst.LoadLocal(temp, sch.Slot));
                result = (temp, Instantiate(sch.S));
                break;

            default:
                throw new InvalidOperationException();
        }

        return result;
    }

    private (int Temp, TypeRef Type) LowerExternalFunctionReference(
        Expr.Var variable,
        Binding.ExternalFunction externalFunction,
        int fallbackTemp)
    {
        if (!RequiresDirectExternalCall(externalFunction.Function))
        {
            return EmitExternalFunctionThunk(externalFunction.Function, externalFunction.Type, GetSpan(variable));
        }

        bool hasBuffer = externalFunction.Function.ParameterTypes.Any(type => type is FfiType.Buffer);
        bool hasOut = externalFunction.Function.ParameterTypes.Any(type => type is FfiType.Out);
        bool hasNativeString = externalFunction.Function.ReturnType is FfiType.NativeString
            || externalFunction.Function.ParameterTypes.Any(type => type is FfiType.Out { Element: FfiType.NativeString });
        string contract = hasBuffer
            ? "a call-scoped FFI buffer parameter"
            : hasNativeString
                ? "a native string conversion contract"
                : hasOut
                    ? "a compiler-owned FFI out parameter"
                : "a resource ownership contract";
        ReportDiagnostic(
            GetSpan(variable),
            $"External function '{variable.Name}' has {contract} and must be called directly.",
            hasBuffer
                ? DiagnosticCodes.InvalidFfiBuffer
                : hasNativeString
                    ? DiagnosticCodes.InvalidFfiString
                    : hasOut
                        ? DiagnosticCodes.InvalidFfiOutParameter
                    : DiagnosticCodes.InvalidExternalOwnershipMarker);
        Emit(new IrInst.LoadConstInt(fallbackTemp, 0));
        return (fallbackTemp, externalFunction.Type);
    }

    private bool HasExternalResourceOwnershipContract(IrExternalFunction function) =>
        function.ParameterOwnerships.Any(ownership => ownership != FfiParameterOwnership.Unspecified)
        || function.ReturnType is FfiType.Opaque returned
            && _externalResourceTypes.ContainsKey(returned.Name);

    private bool RequiresDirectExternalCall(IrExternalFunction function) =>
        HasExternalResourceOwnershipContract(function)
        || function.ReturnType is FfiType.NativeString
        || function.ParameterTypes.Any(type => type is FfiType.Buffer or FfiType.Out);

    private void LoadLocalWithBytesProvenance(int temp, Binding.Local local, Expr.Var variable)
    {
        Emit(new IrInst.LoadLocal(temp, local.Slot));
        RecordUnknownBorrowedTemp(
            temp,
            ResolveSourceLocation(AstSpans.GetOrDefault(variable)),
            local.Type);
        if (_localBytesProvenance.TryGetValue(
                local.Slot,
                out BuiltinRegistry.BytesOwnershipProvenance provenance))
        {
            RefineTempBytesProvenance(temp, provenance);
        }
    }

    private (int, TypeRef) LowerProgramArgs(int target, TypeRef type)
    {
        Emit(new IrInst.LoadProgramArgs(target));
        return (target, type);
    }

    private bool TryGetStandardResultParts(out TypeSymbol resultSymbol, out ConstructorSymbol okConstructor, out ConstructorSymbol errorConstructor)
    {
        resultSymbol = null!;
        okConstructor = null!;
        errorConstructor = null!;

        if (!_typeSymbols.TryGetValue("Result", out var resolvedResultSymbol))
        {
            ReportDiagnostic(0, "Result-aware pipeline operators require a type named 'Result' in scope.");
            return false;
        }

        resultSymbol = resolvedResultSymbol;

        if (resultSymbol.TypeParameters.Count != 2)
        {
            ReportDiagnostic(0, "Result-aware pipeline operators require Result to declare exactly two type parameters.");
            return false;
        }

        okConstructor = resultSymbol.Constructors.FirstOrDefault(c => string.Equals(c.Name, "Ok", StringComparison.Ordinal))!;
        errorConstructor = resultSymbol.Constructors.FirstOrDefault(c => string.Equals(c.Name, "Error", StringComparison.Ordinal))!;
        if (okConstructor is null || errorConstructor is null || okConstructor.Arity != 1 || errorConstructor.Arity != 1)
        {
            ReportDiagnostic(0, "Result-aware pipeline operators require Result(E, A) = | Ok(A) | Error(E).");
            return false;
        }

        return true;
    }

    private static bool TryGetResultTypeArgs(TypeRef type, TypeSymbol resultSymbol, out TypeRef errorType, out TypeRef successType)
    {
        if (type is TypeRef.TNamedType named
            && string.Equals(named.Symbol.Name, resultSymbol.Name, StringComparison.Ordinal)
            && named.TypeArgs.Count == 2)
        {
            errorType = named.TypeArgs[0];
            successType = named.TypeArgs[1];
            return true;
        }

        errorType = new TypeRef.TNever();
        successType = new TypeRef.TNever();
        return false;
    }

    private static bool TryBuildMissingResultDiagnostic(TypeRef type, IReadOnlyList<string> missingConstructors, out string diagnostic)
    {
        if (type is TypeRef.TNamedType named
            && string.Equals(named.Symbol.Name, "Result", StringComparison.Ordinal)
            && missingConstructors.Count > 0)
        {
            diagnostic = missingConstructors.Count == 1
                ? $"Non-exhaustive match on Result: missing {missingConstructors[0]}."
                : $"Non-exhaustive match on Result: missing {string.Join(" and ", missingConstructors)}.";
            return true;
        }

        diagnostic = string.Empty;
        return false;
    }

    private bool TryRequireResultType(TypeRef type, TypeSymbol resultSymbol, Expr origin, string diagnosticMessage, out TypeRef errorType, out TypeRef successType)
    {
        var prunedType = Prune(type);
        if (prunedType is TypeRef.TVar)
        {
            errorType = NewTypeVar();
            successType = NewTypeVar();
            var expectedType = new TypeRef.TNamedType(resultSymbol, [errorType, successType]);
            Unify(prunedType, expectedType);
            return TryGetResultTypeArgs(Prune(expectedType), resultSymbol, out errorType, out successType);
        }

        if (TryGetResultTypeArgs(prunedType, resultSymbol, out errorType, out successType))
        {
            return true;
        }

        ReportDiagnostic(GetSpan(origin), $"{diagnosticMessage} Got {Pretty(prunedType)}.");
        errorType = new TypeRef.TNever();
        successType = new TypeRef.TNever();
        return false;
    }

    private int LowerSingleFieldConstructorValue(ConstructorSymbol constructor, int payloadTemp)
    {
        int ptrTemp = NewTemp();
        Emit(new IrInst.AllocAdt(ptrTemp, GetConstructorTag(constructor), constructor.Arity));
        Emit(new IrInst.SetAdtField(ptrTemp, 0, payloadTemp));
        return ptrTemp;
    }

    // Peel the leading argument types from a function-type annotation, one per curried parameter, up to
    // `maxCount` (the definition's lambda-chain length so a body lambda never consumes a leftover).
    private List<TypeRef> PeelAnnotationParamTypes(TypeRef annotated, int maxCount)
    {
        var args = new List<TypeRef>();
        var t = Prune(annotated);
        while (args.Count < maxCount && t is TypeRef.TFun fun)
        {
            args.Add(fun.Arg);
            t = Prune(fun.Ret);
        }

        return args;
    }

    private (int Temp, TypeRef Type) LowerTraitValidationAwareLetValue(
        Expr.Let let,
        LoweredValueRequest request,
        out IReadOnlyList<TraitConstraint> writtenRequirements)
    {
        bool validatesTraitImplementation = let.Name.StartsWith(
            "__trait_validate_implementation_",
            StringComparison.Ordinal);
        if (validatesTraitImplementation)
        {
            _traitImplementationValidationDepth++;
        }
        try
        {
            return LowerLetAnnotatedValue(let, request, out writtenRequirements);
        }
        finally
        {
            if (validatesTraitImplementation)
            {
                _traitImplementationValidationDepth--;
            }
        }
    }

    private (int, TypeRef) LowerLet(
        Expr.Let let,
        LoweredValueRequest request)
    {
        var savedTailPos = _tcoCtx?.InTailPosition ?? false;
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        // Save the arena watermark before the bound value so allocations from
        // both value and body belong to this let scope.
        EmitArenaWatermark();

        int depth0Before = _depth0LambdaCount;

        PushTraitConstraintScope();
        (int valueTemp, TypeRef valueType) value = LowerTraitValidationAwareLetValue(
            let,
            request.WithoutExpectedType(),
            out IReadOnlyList<TraitConstraint> writtenRequirements);
        IReadOnlyList<TraitConstraint> inferredRequirements = PopTraitConstraintScope(
            out bool needsLateTraitTypeHint);

        int slot = NewLocal();
        Emit(new IrInst.StoreLocal(slot, value.valueTemp));
        RecordLocalBytesProvenance(slot, value.valueTemp);
        RecordLocalDebugInfo(slot, let.Name, value.valueType);
        // Record the binding value so a later tail call `loop(<this name>)` can prove the accumulator
        // address-stable by tracing it back through this let into the value's match/if leaves.
        _letBindingValues[slot] = let.Value;
        TypeScheme scheme = FinalizeLetTraitScheme(
            let,
            value.valueType,
            inferredRequirements,
            writtenRequirements,
            needsLateTraitTypeHint);

        LowerLetRegisterKnownFunctionIdentity(let, slot, scheme, depth0Before);

        PushLetScope(let, slot, scheme);
        PushOwnershipScope();
        TrackLetOwnership(let, slot, value.valueTemp, value.valueType);

        if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;
        // A let only *shadows* an inlinable helper if it rebinds the name to a different value. The
        // top-level definition itself (same lambda we registered) must stay inlinable in its own body.
        bool isOwnDefinition = (let.Value is Expr.Lambda defLam
                && _inlinableFunctions.TryGetValue(let.Name, out var reg)
                && ReferenceEquals(reg.Body, GetInnermostBody(defLam)))
            // Stitched stdlib helpers have an alias-wrapped value (a `let`-chain, not a bare lambda),
            // so match the defining let by the value object identity recorded at registration.
            || (_inlinableDefiningValues.TryGetValue(let.Name, out var defValue) && ReferenceEquals(defValue, let.Value));
        bool shadowed = !isOwnDefinition && PushInlinableShadow(let.Name);
        var (bodyTemp, bodyType) = LowerEscapingResult(let.Body, request: request);
        if (shadowed) PopInlinableShadow(let.Name);

        return PopLetScope(bodyTemp, bodyType);
    }

    private TypeScheme FinalizeLetTraitScheme(
        Expr.Let binding,
        TypeRef valueType,
        IReadOnlyList<TraitConstraint> inferredRequirements,
        IReadOnlyList<TraitConstraint> writtenRequirements,
        bool needsLateTraitTypeHint)
    {
        bool hasInferredElaboration = _inferredTraitBindingElaborations.ContainsKey(
            TraitBindingKey(binding));
        IReadOnlyList<TraitConstraint> schemeRequirements = SelectBindingConstraints(
            hasInferredElaboration ? writtenRequirements : inferredRequirements,
            writtenRequirements,
            valueType,
            GetSpan(binding),
            binding.Name.StartsWith("__trait_validate_implementation_", StringComparison.Ordinal),
            SuppressSourceConstraintDiagnostics(binding.Name));
        TypeScheme scheme = GeneralizeBindingType(Prune(valueType), schemeRequirements);
        RegisterTraitDictionarySchemeForLet(binding, scheme);
        RecordInferredTraitBindingElaboration(
            binding,
            scheme,
            writtenRequirements,
            inferredRequirements,
            needsLateTraitTypeHint);
        ValidateBindingConstraintBoundary(
            scheme,
            GetSpan(binding));
        RecordHoverScheme(
            AstSpans.GetLetNameOrDefault(binding),
            binding.Name,
            scheme,
            GetDeclaredHoverParameterNames(binding.Value));
        return scheme;
    }

    // Registers a top-level, empty-env function so reuse specializations can call it by label. The
    // guard (exactly one depth-0 lambda lowered while lowering this value) means the value is this
    // function's own outer lambda — not a stale label from a sibling or a non-lambda value.
    private void LowerLetRegisterKnownFunctionIdentity(Expr.Let let, int slot, TypeScheme scheme, int depth0Before)
    {
        if (_lambdaDepth == 0 && _depth0LambdaCount == depth0Before + 1 && _lastLoweredLambdaEmptyEnv)
        {
            _topLevelFunctionRefs[let.Name] = (_lastLoweredLambdaLabel, scheme);
            _knownFunctionLabelsBySlot[slot] = _lastLoweredLambdaLabel;
            _functionNameByLabel[_lastLoweredLambdaLabel] = let.Name;
            RegisterOwnershipFunctionLabel(_lastLoweredLambdaLabel, let);
        }
        else if (_lambdaDepth == 0 && _depth0LambdaCount == depth0Before + 1)
        {
            // A capturing top-level let is not callable as a global label because it still needs its
            // environment, but the closure in this exact local slot has a statically known code label.
            // Preserve that provenance so a later closure capture can retain its result-ownership fact.
            _knownFunctionLabelsBySlot[slot] = _lastLoweredLambdaLabel;
            _functionNameByLabel[_lastLoweredLambdaLabel] = let.Name;
            RegisterOwnershipFunctionLabel(_lastLoweredLambdaLabel, let);
        }
        else if (TryResolveKnownFunctionLabel(let.Value, out string aliasedFunctionLabel))
        {
            _knownFunctionLabelsBySlot[slot] = aliasedFunctionLabel;
        }
    }

    private (int Temp, TypeRef Type) LowerEscapingResult(
        Expr body,
        bool normalizeStaticString = false,
        LoweredValueRequest request = default)
    {
        if (normalizeStaticString && body is Expr.StrLit literal)
        {
            var (sourceTemp, sourceType) = LowerStr(literal);
            int resultTemp = NewTemp();
            Emit(new IrInst.CopyOutArena(
                resultTemp,
                sourceTemp,
                -1,
                RuntimeManaged: true,
                IrInst.CopyOutPurpose.RcNormalization));
            MarkRuntimeManagedTemp(resultTemp);
            return (resultTemp, sourceType);
        }

        if (TryLowerRuntimeManagedBuiltinResultBody(
                body,
                request,
                out (int Temp, TypeRef Type) builtinResult))
        {
            return builtinResult;
        }

        // Every representation decision here answers the same question as the closure one below it,
        // and must respect the same placement context. Deciding from expression shape alone gave a
        // coroutine body a reference-counted escaping result while everything around it stayed
        // region-backed, so the enclosing region reset could not reclaim it and no owner released it.
        bool placementAllowsRuntimeRc = AllowsAsyncIndependentRcPlacement
            && AllowsOrdinaryRcPlacement;
        bool runtimeManagedString = placementAllowsRuntimeRc && IsRuntimeRcStringProducer(body);
        bool runtimeManagedAdt = placementAllowsRuntimeRc && ProducesFreshRuntimeManageableAdt(body);
        bool runtimeManagedList = placementAllowsRuntimeRc && ProducesFreshRuntimeManageableList(body);
        bool runtimeManagedBytes = placementAllowsRuntimeRc
            && IsRuntimeRcBytesProducer(body)
            && IsRuntimeRcClosureCaptureSafeBytesProducer(body);
        bool runtimeManagedBigInt = placementAllowsRuntimeRc && IsRuntimeRcBigIntProducer(body);
        bool runtimeManagedTuple = placementAllowsRuntimeRc && ProducesFreshTuple(body);
        bool runtimeManagedRecord = placementAllowsRuntimeRc && IsFreshRuntimeManageableRecordTree(body);
        bool runtimeManagedClosure = placementAllowsRuntimeRc && IsRuntimeRcCopyClosureProducer(body);
        runtimeManagedClosure &= _lambdaDepth == 0 || ClosureCapturesRuntimeManagedHeapValue(body);
        if (!runtimeManagedString && !runtimeManagedAdt && !runtimeManagedList
            && !runtimeManagedBytes && !runtimeManagedBigInt
            && !runtimeManagedTuple && !runtimeManagedRecord && !runtimeManagedClosure)
        {
            return LowerExpr(body, request).AsPair();
        }

        return LowerEscapingRuntimeManagedResult(
            body,
            runtimeManagedString,
            runtimeManagedAdt,
            runtimeManagedList,
            runtimeManagedBytes,
            runtimeManagedBigInt,
            runtimeManagedTuple,
            runtimeManagedRecord,
            runtimeManagedClosure,
            request);
    }

    // True when the escaping result IS a tuple (directly, or through the arms of a match/if or the
    // body of a let, walked via the shared CollectFreshEscapeTerminals engine — see
    // Lowering.TopCellFreshness.cs). A tuple built in a match arm otherwise stays arena-managed, so a
    // function like `readWord acc text = ... -> (acc, rest)` returns a tuple whose runtime-managed
    // string field lives in the callee's arena; the next call reuses that arena and overwrites it.
    // Marking the result a fresh tuple adds an explicit Tuple representation request, so LowerTupleLit
    // allocates the tuple in the RC heap (only when its elements are themselves runtime-manageable — a
    // scalar tuple stays arena), keeping it alive across calls.
    //
    // An existence check (OR across terminal arms) UNLESS a genuine passthrough hazard is present, in
    // which case it must fail outright. An earlier version of this predicate used a plain existence
    // check on the theory that tuples don't share the ADT CO-38 hazard (PR #299) — that theory was
    // incomplete: `if cond then existingTuple else (fresh, tuple)` (one arm a bare-Var passthrough of an
    // existing, not-provably-fresh binding, the other a genuine TupleLit) requests the RC tuple
    // representation for the WHOLE position under a plain existence check, so the fresh arm's TupleLit
    // gets allocated on the RC heap — but the join-level
    // MarkUniformRuntimeManagedResult/MarkRuntimeManagedMatchResult machinery (which decides whether the
    // joined/matched value is actually treated as owned) requires every arm independently verified
    // runtime-managed, which the passthrough arm never is. Confirmed by a compiled-binary repro: an
    // if/match alternating a passthrough tuple arm with a fresh construction arm, discarded every
    // iteration, leaks linearly (verified 5x/10x iteration count -> ~5x/10x RSS growth; flat for an
    // all-fresh or all-passthrough control of the same shape).
    //
    // A first attempted fix (requiring EVERY terminal independently fresh, mirroring the ADT engine's
    // AND) broke text_json_parser_smoke: a match/if whose arms are DIFFERENT constructors of the same
    // ADT is not automatically a tuple hazard just because one of those constructors happens not to
    // carry a tuple at all (e.g. `Error("eof")` beside `Ok((acc, tail))` — Error's own argument is a
    // Str, not a tuple). Error's own outer cell is still a fresh constructor application regardless
    // (unlike a bare Var, which allocates nothing), so it is not a competing tuple representation and
    // must not force the whole classification to false. The genuine hazard is narrower: a terminal that
    // is NEITHER itself tuple-fresh NOR a constructor application at all (a bare Var, a projected field,
    // an ordinary call) is the only thing that can alias a pre-existing, not-provably-fresh tuple at
    // this exact position — that is what must conflict with a fresh sibling, and a self-recursive tail
    // funnel (IsSelfRecursiveTailFunnelArm) is the only such non-constructing terminal that is exempt
    // (by structural induction its eventual result bottoms out at one of this same function's own
    // terminals, already governed by this same check when ITS call frame lowers).
    private bool ProducesFreshTuple(Expr body)
    {
        var terminals = new List<Expr>();
        CollectFreshEscapeTerminals(body, terminals);
        bool sawFreshTuple = false;
        foreach (Expr terminal in terminals)
        {
            if (IsProvenFreshCallFunnelArm(terminal))
            {
                continue;
            }

            if (IsTopCellFreshTupleTerminal(terminal))
            {
                sawFreshTuple = true;
                continue;
            }

            if (IsTopCellFreshAdtConstruction(terminal, out _, out _, out _))
            {
                // A fresh constructor application of a DIFFERENT shape that simply doesn't carry a
                // tuple here (e.g. Error("eof")) -- it still allocates its own outer cell, so it is not
                // a passthrough alias and does not conflict.
                continue;
            }

            return false;
        }

        return sawFreshTuple;
    }

    // A tuple literal is always top-cell-fresh at this position; a tuple wrapped in an ADT constructor
    // (e.g. `Ok((acc, tail))` from a JSON parser) carries the same dangling-string hazard: its string
    // fields live in the callee's arena. Recurse into the constructor's arguments (via the same
    // control-flow-transparent walk) so the flag is set and LowerTupleLit materializes them. Only real
    // constructor applications, not ordinary function calls (whose tuple argument is consumed, not
    // returned).
    private bool IsTopCellFreshTupleTerminal(Expr terminal)
    {
        if (terminal is Expr.TupleLit)
        {
            return true;
        }

        return IsTopCellFreshAdtConstruction(terminal, out _, out List<Expr>? ctorArgs, out _)
            && ctorArgs is not null
            && ctorArgs.Any(ProducesFreshTuple);
    }

    // Like IsFreshRuntimeManageableAdtExpression but recurses through the arms of a match/if and the
    // body of a let (via the shared CollectFreshEscapeTerminals walk — see
    // Lowering.TopCellFreshness.cs). A tail-recursive parser like `parseStringBody acc text = match
    // uncons(text) with ... | Some((h,t)) -> if h == "\"" then Ok((acc, tail)) else
    // parseStringBody(acc + head)(tail)` returns its fresh `Ok((acc, tail))` from a nested match/if
    // arm, not as the whole body. Detecting it here marks the result an escaping runtime-managed ADT,
    // so the Ok survives the arena reset AND (via LowerEscapingRuntimeManagedResult's tuple broadening)
    // the tuple's string fields are materialized out of the reused arena.
    //
    // A self-recursive ADT's arms are not independent, though: a base-case arm that is a trivially
    // "fresh" nullary constructor (e.g. `Leaf`) does not make the WHOLE function's result fresh when a
    // sibling arm builds a DIFFERENT constructor of the same type from non-fresh children (e.g.
    // `Node(make(d - 1))(make(d - 1))`, whose fields are ordinary calls, not nested constructor
    // literals). Flagging only the trivial arm's constructor as runtime-managed would let a single
    // function return a MIX of RC cells (the fresh arm) and arena cells (the non-fresh sibling) for the
    // very same type. Since an arena cell's drop is a no-op that never walks into its children, any RC
    // cell reachable through an arena-managed parent then leaks forever once the arena resets.
    // AnyArmConsistentlyFresh (grouped by parent type name) enforces exactly that: a candidate "fresh"
    // arm is refused when any OTHER arm either also constructs the same parent type but is not
    // independently fresh, OR is any other non-constructing passthrough (a bare Var, a projected field,
    // a call to another function) — the only sibling exempt from that veto is a genuine self-recursive
    // tail funnel (IsSelfRecursiveTailFunnelArm), since every execution path through it bottoms out at
    // one of this same function's own terminals, already governed by this same reconciliation.
    private bool ProducesFreshRuntimeManageableAdt(Expr body)
    {
        bool result = ProducesFreshRuntimeManageableAdtCore(body);
        return result;
    }

    private bool ProducesFreshRuntimeManageableAdtCore(Expr body)
    {
        var terminals = new List<Expr>();
        CollectFreshEscapeTerminals(body, terminals);
        return AnyArmConsistentlyFresh(
            terminals,
            IsFreshRuntimeManageableAdtExpression,
            AdtConstructorGroupKey,
            IsProvenFreshCallFunnelArm);
    }

    // The reconciliation group key for an ADT terminal arm: the parent type name of the constructor it
    // directly applies, or null when the arm does not directly construct anything at this position (a
    // bare Var, a projected field, or a call). AnyArmConsistentlyFresh treats every null-keyed arm as
    // conflicting with a fresh sibling UNLESS it is also a self-recursive tail funnel
    // (IsSelfRecursiveTailFunnelArm) -- a plain passthrough of an existing value is not exempt.
    private string? AdtConstructorGroupKey(Expr arm)
        => IsTopCellFreshAdtConstruction(arm, out ConstructorSymbol? constructor, out _, out _)
            && constructor is not null
                ? constructor.ParentType
                : null;

    private (int Temp, TypeRef Type) LowerEscapingRuntimeManagedResult(
        Expr body,
        bool runtimeManagedString,
        bool runtimeManagedAdt,
        bool runtimeManagedList,
        bool runtimeManagedBytes,
        bool runtimeManagedBigInt,
        bool runtimeManagedTuple,
        bool runtimeManagedRecord,
        bool runtimeManagedClosure,
        LoweredValueRequest request)
    {
        request = request
            .AddRuntime(runtimeManagedString, LoweredValueRuntimeRepresentation.String)
            .AddRuntime(runtimeManagedBytes, LoweredValueRuntimeRepresentation.Bytes)
            .AddRuntime(runtimeManagedBigInt, LoweredValueRuntimeRepresentation.BigInt)
            .AddRuntime(runtimeManagedList, LoweredValueRuntimeRepresentation.List)
            .AddRuntime(runtimeManagedAdt, LoweredValueRuntimeRepresentation.Adt)
            .AddRuntime(runtimeManagedRecord, LoweredValueRuntimeRepresentation.Record)
            .AddRuntime(runtimeManagedClosure, LoweredValueRuntimeRepresentation.Closure)
            .AddRuntime(
                runtimeManagedTuple
                    || runtimeManagedAdt
                    || runtimeManagedList
                    || runtimeManagedRecord,
                LoweredValueRuntimeRepresentation.Tuple);
        // A tuple nested inside an escaping ADT/list/record (e.g. `Ok((acc, tail))` from a JSON
        // parser) carries the same dangling-string hazard as a directly-returned tuple: its string
        // fields live in the callee's arena and the next call overwrites them. The enclosing composite
        // is RC-allocated and escapes, so mark tuples runtime-managed too, letting LowerTupleLit
        // materialize (copy out) their string fields. Restricted to string-field materialization —
        // a scalar tuple still stays arena (see IsRuntimeManageableTupleElement).
        return LowerExpr(body, request).AsPair();
    }

    private bool TryLowerRuntimeManagedBuiltinResultBody(
        Expr body,
        LoweredValueRequest request,
        out (int Temp, TypeRef Type) lowered)
    {
        bool scalarResult = IsRuntimeRcScalarResultProducer(body);
        bool bigIntParseResult = IsRuntimeRcBigIntParseResultProducer(body);
        bool textUnconsResult = IsRuntimeRcTextUnconsResultProducer(body);
        if (!scalarResult && !bigIntParseResult && !textUnconsResult)
        {
            lowered = default;
            return false;
        }

        request = request
            .AddRuntime(
                scalarResult,
                LoweredValueRuntimeRepresentation.ScalarAdtResult)
            .AddRuntime(
                bigIntParseResult,
                LoweredValueRuntimeRepresentation.BigIntParseResult)
            .AddRuntime(
                textUnconsResult,
                LoweredValueRuntimeRepresentation.TextUnconsResult);
        lowered = LowerExpr(body, request).AsPair();
        return true;
    }

    // Seed parameter types from the annotation (if any) before lowering the value, so operators on
    // annotated parameters resolve with the annotated numeric type instead of defaulting to Int.
    private (int Temp, TypeRef Type) LowerLetAnnotatedValue(
        Expr.Let let,
        LoweredValueRequest request,
        out IReadOnlyList<TraitConstraint> writtenRequirements)
    {
        ResolvedBindingSignature signature = ResolveBindingSignature(
            let.TypeAnnotation,
            let.Requires,
            GetSpan(let));
        TypeRef? annotatedLetType = signature.Type;
        TypeRef? inferenceSeedType = ResolveLetInferenceSeedType(let, annotatedLetType);
        writtenRequirements = signature.Requirements;
        var savedAnnotationSeed = (_annotationParamTypes, _annotationParamCursor, _annotationTargetLambda);
        (bool usesTraitDictionary, _, TraitDictionaryFunctionInfo? traitDictionaryInfo) =
            GetLetTraitEvidence(let);
        Expr loweredValue = usesTraitDictionary
            ? TransformTraitDictionaryLetValue(
                let,
                traitDictionaryInfo!)
            : let.Value;
        if (usesTraitDictionary)
        {
            BindTraitDictionaryParameterConstraints(
                traitDictionaryInfo!,
                signature.SourceOrderedRequirements);
        }
        SeedLetAnnotationParameterTypes(
            inferenceSeedType,
            loweredValue,
            CountSourceLambdaParameters(let.Value),
            usesTraitDictionary ? traitDictionaryInfo!.Dictionaries.Count : 0);
        LoweredValueRequest valueRequest = usesTraitDictionary
            ? request
            : ApplyInferenceSeedType(request, inferenceSeedType);
        (int valueTemp, TypeRef runtimeValueType) = usesTraitDictionary
            ? LowerExpr(loweredValue, valueRequest).AsPair()
            : LowerLetValue(let, valueRequest);
        RestoreLetRecursiveAnnotationSeed(savedAnnotationSeed);

        TypeRef valueType = runtimeValueType;
        if (usesTraitDictionary)
        {
            (valueType, annotatedLetType, writtenRequirements) = FinalizeTraitDictionaryLetValue(
                let,
                runtimeValueType,
                traitDictionaryInfo!,
                signature);
        }

        // If the user wrote a type annotation, verify it matches the inferred type.
        if (annotatedLetType is not null)
        {
            using var annotationSpan = PushDiagnosticSpan(GetSpan(let.Value));
            Unify(annotatedLetType, valueType);
        }

        return (valueTemp, valueType);
    }

    private (TypeRef ValueType, TypeRef? AnnotatedType, IReadOnlyList<TraitConstraint> Requirements)
        FinalizeTraitDictionaryLetValue(
            Expr.Let binding,
            TypeRef runtimeValueType,
            TraitDictionaryFunctionInfo info,
            ResolvedBindingSignature stableSignature)
    {
        TypeRef cursor = runtimeValueType;
        foreach (TraitDictionaryShape _ in info.Dictionaries)
        {
            cursor = Prune(cursor);
            if (cursor is not TypeRef.TFun hiddenParameter)
            {
                ReportDiagnostic(
                    GetSpan(binding.Value),
                    $"Internal trait evidence elaboration for '{binding.Name}' did not produce a hidden dictionary parameter.",
                    InvalidTraitDeclarationCode);
                cursor = new TypeRef.TNever();
                break;
            }
            cursor = hiddenParameter.Ret;
        }
        if (stableSignature.Type is not null)
        {
            Unify(stableSignature.Type, cursor);
        }
        return (
            stableSignature.Type ?? Prune(cursor),
            stableSignature.Type,
            stableSignature.Requirements);
    }

    private TypeRef? ResolveLetInferenceSeedType(Expr.Let binding, TypeRef? annotatedType) =>
        annotatedType ?? ResolveInferredTraitBindingTypeHint(binding.Name, binding.Value);

    private static LoweredValueRequest ApplyInferenceSeedType(
        LoweredValueRequest request,
        TypeRef? inferenceSeedType) =>
        inferenceSeedType is null ? request : request.WithExpectedType(inferenceSeedType);

    private void SeedLetAnnotationParameterTypes(
        TypeRef? annotatedType,
        Expr value,
        int sourceParameterCount,
        int hiddenParameterCount)
    {
        Expr.Lambda? lambda = FindInnermostLambdaUnderLets(value);
        if (annotatedType is null || lambda is null)
        {
            return;
        }

        _annotationParamTypes = Enumerable.Range(0, hiddenParameterCount)
            .Select(_ => NewTypeVar())
            .Concat(PeelAnnotationParamTypes(annotatedType, sourceParameterCount))
            .ToArray();
        _annotationParamCursor = 0;
        _annotationTargetLambda = lambda;
    }

    private Expr TransformTraitDictionaryLetValue(
        Expr.Let let,
        TraitDictionaryFunctionInfo info) =>
        TransformTraitDictionaryValue(
            let.Value,
            info,
            threadDictionaryFunctions: true,
            rewriteMethodReferences: !let.Name.StartsWith(
                "__trait_validate_implementation_",
                StringComparison.Ordinal));

    private (int Temp, TypeRef Type) LowerLetValue(
        Expr.Let let,
        LoweredValueRequest request)
    {
        // A coroutine body's stack frame does not survive its own suspension: the coroutine returns to
        // the scheduler and is re-entered later on a fresh frame. A closure or ADT that only ever
        // appears as a direct callee or scrutinee still cannot live on that stack, because the use may
        // sit after an await. Both stay region-backed inside a coroutine.
        var stackAllocateClosure = !_inCoroutineBody
            && let.Value is Expr.Lambda
            && UsesNameOnlyAsDirectCallee(let.Body, let.Name);
        if (stackAllocateClosure && let.Value is Expr.Lambda lambda)
        {
            return LowerLambda(lambda, stackAllocateClosure: true);
        }

        var stackAllocateAdt = !_inCoroutineBody
            && IsConstructorExpression(let.Value)
            && IsImmediateSingleArmAdtDestructuringMatch(let.Name, let.Body);
        if (stackAllocateAdt && TryLowerConstructorExpression(let.Value, stackAllocate: true, out var loweredAdt))
        {
            return loweredAdt;
        }

        if (TryLowerRuntimeManagedLetValue(
                let,
                request,
                out (int Temp, TypeRef Type) lowered))
        {
            return lowered;
        }

        return LowerRemainingLetValue(let, request);
    }

    private bool TryLowerRuntimeManagedLetValue(
        Expr.Let let,
        LoweredValueRequest request,
        out (int Temp, TypeRef Type) lowered)
    {
        return TryLowerRuntimeRcStringLet(let, request, out lowered)
            || TryLowerRuntimeRcBuiltinResultLet(let, request, out lowered)
            || TryLowerRuntimeRcBytesLet(let, request, out lowered)
            || TryLowerRuntimeRcRecordLet(let, request, out lowered)
            || TryLowerRuntimeRcTupleLet(let, request, out lowered)
            || TryLowerRuntimeRcListLet(let, request, out lowered)
            || TryLowerRuntimeRcAdtLet(let, request, out lowered);
    }

    private bool TryLowerRuntimeRcStringLet(
        Expr.Let let,
        LoweredValueRequest request,
        out (int Temp, TypeRef Type) lowered)
    {
        bool directEscape = IsDirectBindingResult(let.Body, let.Name);
        if (!IsRuntimeRcStringProducer(let.Value)
            || (!IsImmediateRuntimeStringUse(let.Body, let.Name) && !directEscape))
        {
            lowered = default;
            return false;
        }
        if ((IsImmediateRuntimeClosureCaptureUse(let.Body, let.Name) || directEscape)
            && !IsRuntimeRcClosureCaptureSafeStringProducer(let.Value))
        {
            lowered = default;
            return false;
        }

        lowered = LowerExpr(
            let.Value,
            request.AddRuntime(
                condition: true,
                LoweredValueRuntimeRepresentation.String)).AsPair();
        return true;
    }

    private static bool IsDirectBindingResult(Expr body, string bindingName)
    {
        return body is Expr.Var variable
            && string.Equals(variable.Name, bindingName, StringComparison.Ordinal);
    }

    private static bool IsTailForwardedBindingResult(Expr body, string bindingName)
    {
        // A tail-position let chain still transfers the same binding out of its scope. The
        // intervening values may borrow it (for example spawn(async(shared))), but they do not turn
        // the final `in shared` into a new owner. Stop at a rebinding so lexical shadowing cannot
        // transfer the outer value by source name.
        return body switch
        {
            Expr.Var variable => string.Equals(variable.Name, bindingName, StringComparison.Ordinal),
            Expr.Let nested when !string.Equals(nested.Name, bindingName, StringComparison.Ordinal) =>
                IsTailForwardedBindingResult(nested.Body, bindingName),
            Expr.LetRecursive nested when !string.Equals(nested.Name, bindingName, StringComparison.Ordinal) =>
                IsTailForwardedBindingResult(nested.Body, bindingName),
            _ => false,
        };
    }

    private bool TryLowerRuntimeRcBytesLet(
        Expr.Let let,
        LoweredValueRequest request,
        out (int Temp, TypeRef Type) lowered)
    {
        bool directEscape = IsDirectBindingResult(let.Body, let.Name);
        if (!IsRuntimeRcBytesProducer(let.Value)
            || (!IsImmediateRuntimeBytesUse(let.Body, let.Name) && !directEscape))
        {
            lowered = default;
            return false;
        }
        if ((IsImmediateRuntimeClosureCaptureUse(let.Body, let.Name) || directEscape)
            && !IsRuntimeRcClosureCaptureSafeBytesProducer(let.Value))
        {
            lowered = default;
            return false;
        }

        lowered = LowerExpr(
            let.Value,
            request.AddRuntime(
                condition: true,
                LoweredValueRuntimeRepresentation.Bytes)).AsPair();
        return true;
    }

    private bool TryLowerRuntimeRcBuiltinResultLet(
        Expr.Let let,
        LoweredValueRequest request,
        out (int Temp, TypeRef Type) lowered)
    {
        return TryLowerRuntimeRcScalarResultLet(let, request, out lowered)
            || TryLowerRuntimeRcBigIntParseResultLet(let, request, out lowered)
            || TryLowerRuntimeRcTextUnconsResultLet(let, request, out lowered);
    }

    private bool TryLowerRuntimeRcScalarResultLet(
        Expr.Let let,
        LoweredValueRequest request,
        out (int Temp, TypeRef Type) lowered)
    {
        if (!IsRuntimeRcScalarResultProducer(let.Value)
            || (!IsImmediateAdtMatchUse(let.Name, let.Body)
                && !IsDirectBindingResult(let.Body, let.Name)))
        {
            lowered = default;
            return false;
        }

        lowered = LowerExpr(
            let.Value,
            request.AddRuntime(
                condition: true,
                LoweredValueRuntimeRepresentation.ScalarAdtResult)).AsPair();
        return true;
    }

    private bool IsRuntimeRcScalarResultProducer(Expr expression)
    {
        if (expression is not Expr.Call(Expr.QualifiedVar qualified, _))
        {
            return false;
        }

        string module = ResolveModuleAlias(qualified.Module);
        return string.Equals(module, "Ashes.Text", StringComparison.Ordinal)
                && (string.Equals(qualified.Name, "parseInt", StringComparison.Ordinal)
                    || string.Equals(qualified.Name, "parseFloat", StringComparison.Ordinal))
            || string.Equals(module, "Ashes.Number.BigInt", StringComparison.Ordinal)
                && string.Equals(qualified.Name, "toInt", StringComparison.Ordinal);
    }

    private bool TryLowerRuntimeRcBigIntParseResultLet(
        Expr.Let let,
        LoweredValueRequest request,
        out (int Temp, TypeRef Type) lowered)
    {
        if (!IsRuntimeRcBigIntParseResultProducer(let.Value)
            || (!IsImmediateRuntimeBigIntParseMatchUse(let.Name, let.Body)
                && !IsDirectBindingResult(let.Body, let.Name)))
        {
            lowered = default;
            return false;
        }

        lowered = LowerExpr(
            let.Value,
            request.AddRuntime(
                condition: true,
                LoweredValueRuntimeRepresentation.BigIntParseResult)).AsPair();
        return true;
    }

    private bool IsRuntimeRcBigIntParseResultProducer(Expr expression)
    {
        return expression is Expr.Call(Expr.QualifiedVar qualified, _)
            && string.Equals(ResolveModuleAlias(qualified.Module), "Ashes.Text", StringComparison.Ordinal)
            && string.Equals(qualified.Name, "parseBigInt", StringComparison.Ordinal);
    }

    private bool IsImmediateRuntimeBigIntParseMatchUse(string bindingName, Expr body)
    {
        if (!IsImmediateAdtMatchUse(bindingName, body)
            || body is not Expr.Match(_, IReadOnlyList<MatchCase> cases, _))
        {
            return false;
        }

        bool sawOk = false;
        bool sawError = false;
        foreach (MatchCase matchCase in cases)
        {
            if (matchCase.Guard is not null
                || matchCase.Pattern is not Pattern.Constructor constructor
                || constructor.Patterns.Count != 1)
            {
                return false;
            }

            if (string.Equals(constructor.Name, "Ok", StringComparison.Ordinal)
                && constructor.Patterns[0] is Pattern.Var okBinding)
            {
                sawOk = IsDirectBigIntCompareUse(matchCase.Body, okBinding.Name);
                if (!sawOk)
                {
                    return false;
                }
            }
            else if (string.Equals(constructor.Name, "Error", StringComparison.Ordinal))
            {
                sawError = matchCase.Body is Expr.IntLit;
                if (!sawError)
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        return sawOk && sawError;
    }

    private bool IsDirectBigIntCompareUse(Expr expression, string bindingName)
    {
        if (expression is not Expr.Call(
                Expr.Call(Expr.QualifiedVar qualified, Expr left),
                Expr right)
            || !string.Equals(ResolveModuleAlias(qualified.Module), "Ashes.Number.BigInt", StringComparison.Ordinal)
            || !string.Equals(qualified.Name, "compare", StringComparison.Ordinal))
        {
            return false;
        }

        return left is Expr.Var leftVariable
                && string.Equals(leftVariable.Name, bindingName, StringComparison.Ordinal)
            || right is Expr.Var rightVariable
                && string.Equals(rightVariable.Name, bindingName, StringComparison.Ordinal);
    }

    private bool TryLowerRuntimeRcTextUnconsResultLet(
        Expr.Let let,
        LoweredValueRequest request,
        out (int Temp, TypeRef Type) lowered)
    {
        if (!IsRuntimeRcTextUnconsResultProducer(let.Value)
            || (!IsImmediateRuntimeTextUnconsMatchUse(let.Name, let.Body)
                && !IsDirectBindingResult(let.Body, let.Name)))
        {
            lowered = default;
            return false;
        }

        lowered = LowerExpr(
            let.Value,
            request.AddRuntime(
                condition: true,
                LoweredValueRuntimeRepresentation.TextUnconsResult)).AsPair();
        return true;
    }

    private bool IsRuntimeRcTextUnconsResultProducer(Expr expression)
    {
        return expression is Expr.Call(Expr.QualifiedVar qualified, _)
            && string.Equals(ResolveModuleAlias(qualified.Module), "Ashes.Text", StringComparison.Ordinal)
            && string.Equals(qualified.Name, "uncons", StringComparison.Ordinal);
    }

    private bool IsImmediateRuntimeTextUnconsMatchUse(string bindingName, Expr body)
    {
        if (!IsImmediateAdtMatchUse(bindingName, body)
            || body is not Expr.Match(_, var cases, _))
        {
            return false;
        }

        bool sawNone = false;
        bool sawSome = false;
        foreach (MatchCase matchCase in cases)
        {
            if (matchCase.Guard is not null)
            {
                return false;
            }

            if (matchCase.Pattern is Pattern.Var none
                && string.Equals(none.Name, "None", StringComparison.Ordinal))
            {
                sawNone = matchCase.Body is Expr.IntLit;
                if (!sawNone)
                {
                    return false;
                }
            }
            else if (matchCase.Pattern is Pattern.Constructor constructor
                && string.Equals(constructor.Name, "Some", StringComparison.Ordinal)
                && constructor.Patterns is [Pattern.Tuple { Elements: [Pattern.Var head, Pattern.Var tail] }]
                && matchCase.Body is Expr.Add add
                && TryGetDirectTextLengthBinding(add.Left, out string? leftBinding)
                && TryGetDirectTextLengthBinding(add.Right, out string? rightBinding))
            {
                sawSome = string.Equals(leftBinding, head.Name, StringComparison.Ordinal)
                        && string.Equals(rightBinding, tail.Name, StringComparison.Ordinal)
                    || string.Equals(leftBinding, tail.Name, StringComparison.Ordinal)
                        && string.Equals(rightBinding, head.Name, StringComparison.Ordinal);
                if (!sawSome)
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        return sawNone && sawSome;
    }

    private bool TryGetDirectTextLengthBinding(Expr expression, out string? bindingName)
    {
        if (expression is Expr.Call(Expr.QualifiedVar qualified, Expr.Var variable)
            && string.Equals(ResolveModuleAlias(qualified.Module), "Ashes.Text", StringComparison.Ordinal)
            && (string.Equals(qualified.Name, "length", StringComparison.Ordinal)
                || string.Equals(qualified.Name, "byteLength", StringComparison.Ordinal)))
        {
            bindingName = variable.Name;
            return true;
        }

        bindingName = null;
        return false;
    }

    private (int Temp, TypeRef Type) LowerRemainingLetValue(
        Expr.Let let,
        LoweredValueRequest request)
    {
        return TryLowerRuntimeRcCopyClosureLet(
                let,
                request,
                out (int Temp, TypeRef Type) loweredClosure)
            ? loweredClosure
            : TryLowerRuntimeRcBigIntLet(
                    let,
                    request,
                    out (int Temp, TypeRef Type) loweredBigInt)
                ? loweredBigInt
                : LowerExpr(let.Value, request).AsPair();
    }

    private bool TryLowerRuntimeRcCopyClosureLet(
        Expr.Let let,
        LoweredValueRequest request,
        out (int Temp, TypeRef Type) lowered)
    {
        if (!AllowsAsyncIndependentRcPlacement
            || !AllowsOrdinaryRcPlacement
            || !IsRuntimeRcCopyClosureProducer(let.Value))
        {
            lowered = default;
            return false;
        }

        bool directCall = UsesNameOnlyAsDirectCallee(let.Body, let.Name);
        bool directEscape = IsDirectBindingResult(let.Body, let.Name);
        if ((!directCall && !directEscape)
            || directEscape && !directCall && _lambdaDepth > 0
                && !ClosureCapturesRuntimeManagedHeapValue(let.Value))
        {
            lowered = default;
            return false;
        }

        lowered = LowerExpr(
            let.Value,
            request.AddRuntime(
                condition: true,
                LoweredValueRuntimeRepresentation.Closure)).AsPair();
        return true;
    }

    private bool IsRuntimeRcCopyClosureProducer(Expr expression)
    {
        return expression switch
        {
            Expr.Lambda lambda => LambdaCapturesOnlyBorrowSafeValues(lambda),
            Expr.If conditional => IsRuntimeRcCopyClosureProducer(conditional.Then)
                && IsRuntimeRcCopyClosureProducer(conditional.Else),
            _ => false,
        };
    }

    private bool ClosureCapturesRuntimeManagedHeapValue(Expr expression)
    {
        if (expression is Expr.If conditional)
        {
            return ClosureCapturesRuntimeManagedHeapValue(conditional.Then)
                || ClosureCapturesRuntimeManagedHeapValue(conditional.Else);
        }

        if (expression is not Expr.Lambda lambda)
        {
            return false;
        }

        HashSet<string> bound = new(StringComparer.Ordinal) { lambda.ParamName };
        foreach (string name in FreeVars(lambda.Body, bound))
        {
            if (LookupOwnedValue(name) is
                {
                    RuntimeManaged: true,
                    Type: TypeRef.TStr or TypeRef.TBytes or TypeRef.TBigInt or TypeRef.TList
                        or TypeRef.TTuple or TypeRef.TNamedType,
                })
            {
                return true;
            }
        }

        return false;
    }

    private bool ClosureCapturesOnlyRuntimeManagedOrCopyValues(Expr expression)
    {
        if (expression is Expr.If conditional)
        {
            return ClosureCapturesOnlyRuntimeManagedOrCopyValues(conditional.Then)
                && ClosureCapturesOnlyRuntimeManagedOrCopyValues(conditional.Else);
        }

        if (expression is not Expr.Lambda lambda)
        {
            return false;
        }

        HashSet<string> bound = new(StringComparer.Ordinal) { lambda.ParamName };
        foreach (string name in FreeVars(lambda.Body, bound))
        {
            OwnershipInfo? owned = LookupOwnedValue(name);
            if (owned is null)
            {
                continue;
            }

            if (owned.IsResource || owned.IsResourceBearing
                || string.Equals(owned.TypeName, "Function", StringComparison.Ordinal)
                || !owned.RuntimeManaged && owned.Type is not null && !CanArenaReset(owned.Type))
            {
                return false;
            }
        }

        return true;
    }

    private bool LambdaCapturesOnlyBorrowSafeValues(Expr.Lambda lambda)
    {
        HashSet<string> bound = new(StringComparer.Ordinal) { lambda.ParamName };
        HashSet<string> free = FreeVars(lambda.Body, bound);
        bool capturesOwnedValue = false;
        foreach (string name in free)
        {
            OwnershipInfo? owned = LookupOwnedValue(name);
            if (owned is null)
            {
                continue;
            }

            if (owned.IsResource
                || owned.IsResourceBearing
                || string.Equals(owned.TypeName, "Function", StringComparison.Ordinal))
            {
                return false;
            }
            if (owned.RuntimeManaged
                && owned.Type is not TypeRef.TStr
                && owned.Type is not TypeRef.TBytes
                && owned.Type is not TypeRef.TBigInt
                && owned.Type is not TypeRef.TList
                && owned.Type is not TypeRef.TTuple
                && owned.Type is not TypeRef.TNamedType)
            {
                return false;
            }
            capturesOwnedValue = true;
        }

        return !capturesOwnedValue || IsKnownCopyClosureResult(lambda.Body);
    }

    private bool IsKnownCopyClosureResult(Expr expression)
    {
        if (expression is Expr.IntLit or Expr.UIntLit or Expr.FloatLit or Expr.BoolLit)
        {
            return true;
        }

        if (expression is Expr.Match match)
        {
            return match.Cases.All(matchCase => IsKnownCopyClosureResult(matchCase.Body));
        }

        if (expression is Expr.Call(Expr.QualifiedVar qualified, _))
        {
            string module = ResolveModuleAlias(qualified.Module);
            return string.Equals(module, "Ashes.Text", StringComparison.Ordinal)
                    && (string.Equals(qualified.Name, "length", StringComparison.Ordinal)
                        || string.Equals(qualified.Name, "byteLength", StringComparison.Ordinal))
                || string.Equals(module, "Ashes.Byte", StringComparison.Ordinal)
                    && string.Equals(qualified.Name, "length", StringComparison.Ordinal)
                || string.Equals(module, "Ashes.Collection.List", StringComparison.Ordinal)
                    && string.Equals(qualified.Name, "length", StringComparison.Ordinal);
        }

        return expression is Expr.Call(
                Expr.Call(Expr.QualifiedVar bigInt, _),
                _)
            && string.Equals(ResolveModuleAlias(bigInt.Module), "Ashes.Number.BigInt", StringComparison.Ordinal)
            && string.Equals(bigInt.Name, "compare", StringComparison.Ordinal);
    }

    private bool TryLowerRuntimeRcBigIntLet(
        Expr.Let let,
        LoweredValueRequest request,
        out (int Temp, TypeRef Type) lowered)
    {
        bool directEscape = IsDirectBindingResult(let.Body, let.Name);
        if (!IsRuntimeRcBigIntProducer(let.Value)
            || (!IsImmediateRuntimeBigIntUse(let.Body, let.Name) && !directEscape))
        {
            lowered = default;
            return false;
        }
        if ((IsImmediateRuntimeClosureCaptureUse(let.Body, let.Name) || directEscape)
            && !IsRuntimeRcClosureCaptureSafeBigIntProducer(let.Value))
        {
            lowered = default;
            return false;
        }

        lowered = LowerExpr(
            let.Value,
            request.AddRuntime(
                condition: true,
                LoweredValueRuntimeRepresentation.BigInt)).AsPair();
        return true;
    }

    private bool TryLowerRuntimeRcRecordLet(
        Expr.Let let,
        LoweredValueRequest request,
        out (int Temp, TypeRef Type) lowered)
    {
        bool directFreshEscape = IsDirectBindingResult(let.Body, let.Name)
            && IsFreshRuntimeManageableRecordTree(let.Value);
        bool capturedFreshRecord = IsImmediateRuntimeClosureCaptureUse(let.Body, let.Name)
            && IsFreshRuntimeManageableRecordTree(let.Value);
        if (let.Value is not Expr.RecordLit
            || (!IsImmediateCopyUseOfRecord(let.Body, let.Name)
                && !IsImmediateRuntimeRecordMatchUse(let.Body, let.Name)
                && !IsRuntimeManagedRecordChildConsumedByImmediateParent(let.Name, let.Body)
                && !directFreshEscape
                && !capturedFreshRecord))
        {
            lowered = default;
            return false;
        }

        TryCollectRuntimeRcRecordChildBindings(let.Value, let.Body, out Dictionary<string, bool>? childBindings);
        LoweredValueRequest representationRequest = request
            .AddRuntime(
                condition: true,
                LoweredValueRuntimeRepresentation.Record)
            .WithRuntimeAdtContext(childBindings);
        lowered = LowerExpr(let.Value, representationRequest).AsPair();
        return true;
    }

    private bool TryLowerRuntimeRcTupleLet(
        Expr.Let let,
        LoweredValueRequest request,
        out (int Temp, TypeRef Type) lowered)
    {
        if (let.Value is not Expr.TupleLit
            || (!IsDirectBindingResult(let.Body, let.Name)
                && !IsImmediateRuntimeClosureCaptureUse(let.Body, let.Name)))
        {
            lowered = default;
            return false;
        }

        lowered = LowerExpr(
            let.Value,
            request.AddRuntime(
                condition: true,
                LoweredValueRuntimeRepresentation.Tuple)).AsPair();
        return true;
    }

    private bool IsImmediateRuntimeStringUse(Expr body, string bindingName)
    {
        if (body is Expr.Call(
                Expr.QualifiedVar qualified,
                Expr.Var argument)
            && string.Equals(argument.Name, bindingName, StringComparison.Ordinal))
        {
            string module = ResolveModuleAlias(qualified.Module);
            return (string.Equals(module, "Ashes.Text", StringComparison.Ordinal)
                    && (string.Equals(qualified.Name, "length", StringComparison.Ordinal)
                        || string.Equals(qualified.Name, "byteLength", StringComparison.Ordinal)))
                || (string.Equals(module, "Ashes.IO", StringComparison.Ordinal)
                    && string.Equals(qualified.Name, "print", StringComparison.Ordinal));
        }

        return IsImmediateRuntimeClosureCaptureUse(body, bindingName);
    }

    private bool IsImmediateRuntimeClosureCaptureUse(Expr body, string bindingName)
    {
        return ClosureBranchesCaptureForKnownCopyResult(body, bindingName)
            || body is Expr.Let closureLet
            && (UsesNameOnlyAsDirectCalleeForClosureCapture(closureLet.Body, closureLet.Name)
                || IsDirectBindingResult(closureLet.Body, closureLet.Name))
            && ClosureBranchesCaptureForKnownCopyResult(closureLet.Value, bindingName);
    }

    private bool UsesNameOnlyAsDirectCalleeForClosureCapture(Expr expression, string name)
    {
        try
        {
            return UsesNameOnlyAsDirectCallee(expression, name);
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private bool IsRuntimeRcClosureCaptureSafeStringProducer(Expr expression)
    {
        if (expression is Expr.Add)
        {
            return true;
        }

        if (expression is Expr.Call(Expr.QualifiedVar qualified, Expr argument)
            && string.Equals(qualified.Module, "Ashes.Text", StringComparison.Ordinal))
        {
            return string.Equals(qualified.Name, "fromInt", StringComparison.Ordinal)
                || string.Equals(qualified.Name, "fromFloat", StringComparison.Ordinal)
                || string.Equals(qualified.Name, "toHex", StringComparison.Ordinal)
                || string.Equals(qualified.Name, "fromBigInt", StringComparison.Ordinal)
                || (string.Equals(qualified.Name, "asciiUpper", StringComparison.Ordinal)
                    || string.Equals(qualified.Name, "asciiLower", StringComparison.Ordinal))
                    && IsArenaAllocationFreeStringOperand(argument);
        }

        if (expression is Expr.Call(Expr.Call(Expr.QualifiedVar format, _), _)
            && string.Equals(format.Module, "Ashes.Text", StringComparison.Ordinal)
            && string.Equals(format.Name, "formatFloat", StringComparison.Ordinal))
        {
            return true;
        }

        return expression is Expr.Call(
                Expr.Call(
                    Expr.Call(Expr.QualifiedVar subText, Expr bytes),
                    _),
                _)
            && string.Equals(subText.Module, "Ashes.Byte", StringComparison.Ordinal)
            && string.Equals(subText.Name, "subText", StringComparison.Ordinal)
            && IsStableBytesOperand(bytes);
    }

    private static bool IsArenaAllocationFreeStringOperand(Expr expression)
    {
        return expression switch
        {
            Expr.StrLit or Expr.Var or Expr.QualifiedVar => true,
            Expr.If conditional => IsArenaAllocationFreeStringOperand(conditional.Then)
                && IsArenaAllocationFreeStringOperand(conditional.Else),
            _ => false,
        };
    }

    private bool ClosureBranchesCaptureForKnownCopyResult(Expr expression, string bindingName)
    {
        if (expression is Expr.If conditional)
        {
            return ClosureBranchesCaptureForKnownCopyResult(conditional.Then, bindingName)
                && ClosureBranchesCaptureForKnownCopyResult(conditional.Else, bindingName);
        }

        if (expression is not Expr.Lambda lambda || !IsKnownCopyClosureResult(lambda.Body))
        {
            return false;
        }

        HashSet<string> bound = new(StringComparer.Ordinal) { lambda.ParamName };
        return FreeVars(lambda.Body, bound).Contains(bindingName);
    }

    /// <summary>
    /// Peels a fully applied qualified call — <c>Module.name(a)(b)...</c>, parsed as left-nested
    /// <see cref="Expr.Call"/> around a <see cref="Expr.QualifiedVar"/> callee — down to that callee
    /// and the number of arguments applied. Returns false when <paramref name="expression"/> is not
    /// shaped as a direct qualified call (e.g. the callee is a variable holding a partially applied
    /// function), matching how the pre-refactor whitelist predicates only recognized syntactically
    /// direct calls to a builtin.
    /// </summary>
    private static bool TryExtractFullyAppliedQualifiedCall(
        Expr expression,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Expr.QualifiedVar? qualified,
        out int argumentCount)
    {
        argumentCount = 0;
        Expr current = expression;
        while (current is Expr.Call call)
        {
            argumentCount++;
            current = call.Func;
        }

        if (current is Expr.QualifiedVar qualifiedVar)
        {
            qualified = qualifiedVar;
            return true;
        }

        qualified = null;
        return false;
    }

    /// <summary>
    /// Whether <paramref name="expression"/> is a fully applied call to a builtin declared (in
    /// <see cref="BuiltinRegistry"/>) to always produce a fresh, uniquely owned value of any kind —
    /// the overload taking an explicit <see cref="BuiltinRegistry.FreshRcResultKind"/> specializes to
    /// one specific expected kind; the ownership-provenance terminal-arm classifier uses this any-kind
    /// overload to recognize a builtin producer as fresh construction regardless of which value kind
    /// it produces.
    /// </summary>
    private bool IsRuntimeRcFreshBuiltinProducer(Expr expression)
        => TryGetFreshRcBuiltinProducerKind(expression, out var kind)
            && kind != BuiltinRegistry.FreshRcResultKind.None;

    /// <summary>
    /// Whether <paramref name="expression"/> is a fully applied call to a builtin declared (in
    /// <see cref="BuiltinRegistry"/>) to always produce a fresh, uniquely owned value of
    /// <paramref name="expectedKind"/> — the single lookup <see cref="IsRuntimeRcStringProducer"/>,
    /// <see cref="IsRuntimeRcBytesProducer"/>, and <see cref="IsRuntimeRcBigIntProducer"/> each
    /// specialize to their own result kind, replacing what used to be independent AST pattern
    /// matches over the call site's qualified name.
    /// </summary>
    private bool IsRuntimeRcFreshBuiltinProducer(Expr expression, BuiltinRegistry.FreshRcResultKind expectedKind)
        => TryGetFreshRcBuiltinProducerKind(expression, out var kind) && kind == expectedKind;

    /// <summary>
    /// The shared lookup behind both <see cref="IsRuntimeRcFreshBuiltinProducer(Expr)"/> overloads:
    /// resolves <paramref name="expression"/> to a fully applied builtin call and reports the
    /// declared <see cref="BuiltinRegistry.FreshRcResultKind"/> of its result, or
    /// <see cref="BuiltinRegistry.FreshRcResultKind.None"/> when it is not a recognized, fully
    /// applied, fresh-RC-producing builtin call.
    /// </summary>
    private bool TryGetFreshRcBuiltinProducerKind(Expr expression, out BuiltinRegistry.FreshRcResultKind kind)
    {
        kind = BuiltinRegistry.FreshRcResultKind.None;
        if (!TryExtractFullyAppliedQualifiedCall(expression, out Expr.QualifiedVar? qualified, out int argumentCount))
        {
            return false;
        }

        string moduleName = ResolveModuleAlias(qualified.Module);
        if (!BuiltinRegistry.TryGetModule(moduleName, out BuiltinRegistry.BuiltinModule module)
            || !module.Members.TryGetValue(qualified.Name, out var member)
            || member.Arity != argumentCount)
        {
            return false;
        }

        kind = member.ProducesFreshRcResult;
        return kind != BuiltinRegistry.FreshRcResultKind.None;
    }

    private bool TryGetBuiltinBytesProvenance(
        Expr expression,
        out BuiltinRegistry.BytesOwnershipProvenance provenance)
    {
        provenance = BuiltinRegistry.BytesOwnershipProvenance.Unknown;
        if (!TryExtractFullyAppliedQualifiedCall(
                expression,
                out Expr.QualifiedVar? qualified,
                out int argumentCount))
        {
            return false;
        }

        string moduleName = ResolveModuleAlias(qualified.Module);
        if (!BuiltinRegistry.TryGetModule(moduleName, out BuiltinRegistry.BuiltinModule module)
            || !module.Members.TryGetValue(qualified.Name, out var member)
            || member.Arity != argumentCount
            || member.BytesProvenance == BuiltinRegistry.BytesOwnershipProvenance.Unknown)
        {
            return false;
        }

        provenance = member.BytesProvenance;
        return true;
    }

    private bool IsRuntimeRcStringProducer(Expr expression)
    {
        return expression is Expr.Add
            || IsRuntimeRcFreshBuiltinProducer(expression, BuiltinRegistry.FreshRcResultKind.String);
    }

    private bool IsRuntimeRcBytesProducer(Expr expression)
    {
        return IsRuntimeRcFreshBuiltinProducer(expression, BuiltinRegistry.FreshRcResultKind.Bytes);
    }

    private bool IsRuntimeRcClosureCaptureSafeBytesProducer(Expr expression)
    {
        return GetBytesOwnershipProvenance(expression)
            == BuiltinRegistry.BytesOwnershipProvenance.FreshOwnedBuffer;
    }

    private BuiltinRegistry.BytesOwnershipProvenance GetBytesOwnershipProvenance(
        Expr expression)
    {
        if (TryGetBuiltinBytesProvenance(expression, out var builtinProvenance))
        {
            return builtinProvenance;
        }

        if (expression is Expr.If conditional)
        {
            BuiltinRegistry.BytesOwnershipProvenance thenProvenance =
                GetBytesOwnershipProvenance(conditional.Then);
            return thenProvenance == GetBytesOwnershipProvenance(conditional.Else)
                ? thenProvenance
                : BuiltinRegistry.BytesOwnershipProvenance.Unknown;
        }

        if (expression is Expr.Var variable
            && Lookup(variable.Name) is Binding.Local local
            && _letBindingValues.TryGetValue(local.Slot, out Expr? value)
            && !ReferenceEquals(value, expression))
        {
            return GetBytesOwnershipProvenance(value);
        }

        var arguments = new List<Expr>();
        Expr root = CollectCallArgs(expression, arguments);
        return GetOwnershipSummaryForCallRoot(root) is { } summary
            && arguments.Count == summary.Parameters.Count
                ? summary.ResultProvenance.BytesProvenance
                : BuiltinRegistry.BytesOwnershipProvenance.Unknown;
    }

    private bool IsStableBytesOperand(Expr expression)
    {
        return expression is Expr.Var or Expr.QualifiedVar
            || GetBytesOwnershipProvenance(expression)
                != BuiltinRegistry.BytesOwnershipProvenance.Unknown;
    }

    private bool CanMaterializeOwnedBytes(Expr expression)
    {
        return GetBytesOwnershipProvenance(expression) is
            BuiltinRegistry.BytesOwnershipProvenance.FreshOwnedBuffer
            or BuiltinRegistry.BytesOwnershipProvenance.BorrowedView;
    }

    private bool IsImmediateRuntimeBytesUse(Expr body, string bindingName)
    {
        if (body is Expr.Call(
                Expr.QualifiedVar qualified,
                Expr.Var argument)
            && string.Equals(argument.Name, bindingName, StringComparison.Ordinal)
            && string.Equals(ResolveModuleAlias(qualified.Module), "Ashes.Byte", StringComparison.Ordinal)
            && string.Equals(qualified.Name, "length", StringComparison.Ordinal))
        {
            return true;
        }

        return IsImmediateRuntimeClosureCaptureUse(body, bindingName);
    }

    private bool IsRuntimeRcBigIntProducer(Expr expression)
    {
        return IsRuntimeRcFreshBuiltinProducer(expression, BuiltinRegistry.FreshRcResultKind.BigInt);
    }

    private bool IsImmediateRuntimeBigIntUse(Expr body, string bindingName)
    {
        if (body is Expr.Call(
                Expr.Call(Expr.QualifiedVar qualified, Expr left),
                Expr right)
            && string.Equals(ResolveModuleAlias(qualified.Module), "Ashes.Number.BigInt", StringComparison.Ordinal)
            && string.Equals(qualified.Name, "compare", StringComparison.Ordinal)
            && (left is Expr.Var leftVariable
                    && string.Equals(leftVariable.Name, bindingName, StringComparison.Ordinal)
                || right is Expr.Var rightVariable
                    && string.Equals(rightVariable.Name, bindingName, StringComparison.Ordinal)))
        {
            return true;
        }

        return IsImmediateRuntimeClosureCaptureUse(body, bindingName);
    }

    private bool IsRuntimeRcClosureCaptureSafeBigIntProducer(Expr expression)
    {
        return IsRuntimeRcBigIntProducer(expression);
    }

    private bool TryLowerRuntimeRcAdtLet(
        Expr.Let let,
        LoweredValueRequest request,
        out (int Temp, TypeRef Type) lowered)
    {
        bool immediateMatch = IsConstructorExpression(let.Value)
            && IsImmediateSafeAdtMatchUse(let.Name, let.Value, let.Body);
        bool consumedByParent = IsConstructorExpression(let.Value)
            && IsRecursiveAdtChildConsumedByImmediateMatch(let.Name, let.Body);
        bool directOwnedEscape = IsDirectBindingResult(let.Body, let.Name)
            && IsFreshRuntimeManageableAdtExpression(let.Value);
        bool capturedOwnedAdt = IsImmediateRuntimeClosureCaptureUse(let.Body, let.Name)
            && IsFreshRuntimeManageableAdtExpression(let.Value);
        if (!immediateMatch && !consumedByParent && !directOwnedEscape && !capturedOwnedAdt)
        {
            lowered = default;
            return false;
        }

        Dictionary<string, bool>? childBindings = null;
        if (immediateMatch)
        {
            if (!TryCollectRuntimeRcAdtChildBindings(let.Name, let.Value, let.Body, out childBindings))
            {
                TryCollectRuntimeRcRecordChildBindings(let.Value, let.Body, out childBindings);
            }
        }

        LoweredValueRequest representationRequest = request
            .AddRuntime(
                condition: true,
                LoweredValueRuntimeRepresentation.Adt)
            .WithRuntimeAdtContext(childBindings);
        lowered = LowerExpr(let.Value, representationRequest).AsPair();
        return true;
    }

    private bool IsFreshRuntimeManageableAdtExpression(Expr expression)
    {
        bool result = IsFreshRuntimeManageableAdtExpressionCore(expression);
        return result;
    }

    private bool IsFreshRuntimeManageableAdtExpressionCore(Expr expression)
    {
        return IsTopCellFreshAdtConstruction(expression, out ConstructorSymbol? directConstructor, out List<Expr>? directArguments, out TypeRef.TNamedType? resultType)
            && directConstructor is not null
            && directArguments is not null
            && resultType is not null
            && (CanRuntimeManageCopyAdt(resultType)
                || CanRuntimeManageGenericCopyAdtConstructorApplication(directConstructor, directArguments, resultType)
                || CanRuntimeManageFreshHeapChildAdtConstructorApplication(directConstructor, directArguments, resultType)
                || CanRuntimeManageOwnedChildAdtConstructorApplication(directConstructor, directArguments, resultType)
                || (CanRuntimeManageRecursiveCopyAdt(resultType)
                    && IsFreshConstructorTree(expression, resultType.Symbol)));
    }

    private bool TryLowerRuntimeRcListLet(
        Expr.Let let,
        LoweredValueRequest request,
        out (int Temp, TypeRef Type) lowered)
    {
        bool freshConstruction = IsFreshListConstructionExpression(let.Value);
        bool freshRuntimeList = freshConstruction
            && (IsImmediateCopyListMatchUse(let.Name, let.Body)
                || IsTailConsumedByImmediateListMatch(let.Name, let.Body)
                || IsDirectBindingResult(let.Body, let.Name)
                || IsImmediateRuntimeClosureCaptureUse(let.Body, let.Name));
        bool extendsRuntimeList = TryGetRuntimeRcListTailExtension(let.Name, let.Value, let.Body, out string? tailBinding);
        if (!freshRuntimeList && !extendsRuntimeList)
        {
            lowered = default;
            return false;
        }

        LoweredValueRequest representationRequest = request
            .AddRuntime(
                condition: true,
                LoweredValueRuntimeRepresentation.List)
            .WithRuntimeListContext(
                extendsRuntimeList ? tailBinding : null,
                tailBinding is not null
                    && ExprReferencesName(let.Body, tailBinding, shadowed: false),
                tcoTailSlot: null);
        lowered = LowerExpr(let.Value, representationRequest).AsPair();
        return true;
    }

    private bool IsFreshListConstructionExpression(Expr expression)
    {
        bool result = IsFreshListConstructionExpressionCore(expression);
        return result;
    }

    private static bool IsFreshListConstructionExpressionCore(Expr expression)
        => expression switch
        {
            Expr.ListLit => true,
            Expr.Cons cons => IsFreshListConstructionExpressionCore(cons.Tail),
            _ => false,
        };

    private static bool IsTailConsumedByImmediateListMatch(string name, Expr body)
        => body is Expr.Let child
            && child.Value is Expr.Cons { Tail: Expr.Var tail }
            && string.Equals(tail.Name, name, StringComparison.Ordinal)
            && IsImmediateCopyListMatchUse(child.Name, child.Body);

    private bool TryGetRuntimeRcListTailExtension(string name, Expr value, Expr body, out string? tailBinding)
    {
        tailBinding = null;
        if (value is not Expr.Cons { Tail: Expr.Var tail }
            || !IsImmediateCopyListMatchUse(name, body))
        {
            return false;
        }

        OwnershipInfo? info = LookupOwnedValue(tail.Name);
        if (info is not { RuntimeManaged: true, IsDropped: false, Type: TypeRef.TList })
        {
            return false;
        }

        tailBinding = tail.Name;
        return true;
    }

    private static bool IsImmediateCopyListMatchUse(string name, Expr body)
    {
        if (!IsImmediateAdtMatchUse(name, body) || body is not Expr.Match(_, var cases, _))
        {
            return false;
        }

        foreach (MatchCase matchCase in cases)
        {
            if (MatchCaseReferencesAnyBinding(matchCase, ListTailBindings(matchCase.Pattern)))
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<string> ListTailBindings(Pattern pattern)
    {
        switch (pattern)
        {
            case Pattern.Var variable:
                yield return variable.Name;
                break;
            case Pattern.Cons cons:
                foreach (string name in ListTailBindings(cons.Tail))
                {
                    yield return name;
                }
                break;
        }
    }

    private bool IsImmediateSafeAdtMatchUse(string name, Expr value, Expr body)
    {
        if (!IsImmediateAdtMatchUse(name, body)
            || body is not Expr.Match(_, var cases, _)
            || !TryGetConstructorExpressionType(value, out TypeSymbol? type)
            || type is null)
        {
            return false;
        }

        bool recursiveType = type.Constructors.Any(constructor => constructor.ParameterTypes.Any(fieldType =>
            fieldType is TypeRef.TNamedType child
            && string.Equals(child.Symbol.Name, type.Name, StringComparison.Ordinal)));
        if (!recursiveType)
        {
            return true;
        }

        return TryDescribeConstructorExpression(
                value,
                out _,
                out _,
                out TypeRef.TNamedType? resultType)
            && resultType is not null
            && RuntimeReusePointerFieldsAreSafe(cases, resultType);
    }

    private static bool MatchCaseReferencesAnyBinding(MatchCase matchCase, IEnumerable<string> bindings)
    {
        foreach (string binding in bindings)
        {
            if ((matchCase.Guard is not null && ExprReferencesName(matchCase.Guard, binding, shadowed: false))
                || ExprReferencesName(matchCase.Body, binding, shadowed: false))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryGetConstructorExpressionType(Expr expression, out TypeSymbol? type)
    {
        if (TryDescribeConstructorExpression(
            expression,
            out ConstructorSymbol? constructor,
            out _,
            out _)
            && constructor is not null
            && _typeSymbols.TryGetValue(constructor.ParentType, out type))
        {
            return true;
        }

        type = null;
        return false;
    }

    private bool IsRecursiveAdtChildConsumedByImmediateMatch(string name, Expr body)
    {
        if (body is not Expr.Let parent
            || !IsImmediateSafeAdtMatchUse(parent.Name, parent.Value, parent.Body)
            || !TryDescribeConstructorExpression(parent.Value, out ConstructorSymbol? constructor, out List<Expr>? arguments, out TypeRef.TNamedType? parentType)
            || constructor is null
            || arguments is null
            || parentType is null
            || !CanRuntimeManageRecursiveCopyAdt(parentType))
        {
            return false;
        }

        bool consumed = false;
        for (int i = 0; i < constructor.Arity; i++)
        {
            TypeRef fieldType = Prune(InstantiateConstructorParameterType(constructor, i, parentType));
            if (fieldType is not TypeRef.TNamedType child
                || !string.Equals(child.Symbol.Name, parentType.Symbol.Name, StringComparison.Ordinal))
            {
                continue;
            }

            if (arguments[i] is Expr.Var variable && string.Equals(variable.Name, name, StringComparison.Ordinal))
            {
                consumed = true;
            }
            else if (!IsFreshConstructorTree(arguments[i], parentType.Symbol))
            {
                return false;
            }
        }

        return consumed;
    }

    private bool IsRuntimeManagedRecordChildConsumedByImmediateParent(string name, Expr body)
    {
        if (body is not Expr.Let parent
            || !TryDescribeConstructorExpression(
                parent.Value,
                out ConstructorSymbol? constructor,
                out List<Expr>? arguments,
                out TypeRef.TNamedType? parentType)
            || constructor is null
            || arguments is null
            || parentType is null
            || (!CanRuntimeManageAdt(parentType)
                && !CanRuntimeManageOwnedChildAdt(parentType))
            || !IsImmediateRuntimeManagedParentUse(parent))
        {
            return false;
        }

        bool consumed = false;
        for (int i = 0; i < constructor.Arity; i++)
        {
            TypeRef fieldType = Prune(InstantiateConstructorParameterType(constructor, i, parentType));
            if (CanArenaReset(fieldType))
            {
                continue;
            }

            if (arguments[i] is Expr.Var variable
                && string.Equals(variable.Name, name, StringComparison.Ordinal))
            {
                consumed = true;
            }
            else if (arguments[i] is not Expr.RecordLit)
            {
                return false;
            }
        }

        return consumed;
    }

    private bool IsImmediateRuntimeManagedParentUse(Expr.Let parent)
    {
        return parent.Value is Expr.RecordLit
            ? IsImmediateCopyUseOfRecord(parent.Body, parent.Name)
                || IsImmediateRuntimeRecordMatchUse(parent.Body, parent.Name)
            : IsImmediateSafeAdtMatchUse(parent.Name, parent.Value, parent.Body);
    }

    private bool TryCollectRuntimeRcAdtChildBindings(
        string name,
        Expr value,
        Expr body,
        out Dictionary<string, bool>? bindings)
    {
        bindings = null;
        if (!IsImmediateSafeAdtMatchUse(name, value, body)
            || !TryDescribeConstructorExpression(value, out ConstructorSymbol? constructor, out List<Expr>? arguments, out TypeRef.TNamedType? resultType)
            || constructor is null
            || arguments is null
            || resultType is null
            || !CanRuntimeManageRecursiveCopyAdt(resultType))
        {
            return false;
        }

        var collected = new Dictionary<string, bool>(StringComparer.Ordinal);
        for (int i = 0; i < constructor.Arity; i++)
        {
            TypeRef fieldType = Prune(InstantiateConstructorParameterType(constructor, i, resultType));
            if (fieldType is not TypeRef.TNamedType child
                || !string.Equals(child.Symbol.Name, resultType.Symbol.Name, StringComparison.Ordinal)
                || IsFreshConstructorTree(arguments[i], resultType.Symbol))
            {
                continue;
            }

            if (arguments[i] is not Expr.Var variable
                || collected.ContainsKey(variable.Name)
                || LookupOwnedValue(variable.Name) is not { RuntimeManaged: true, IsDropped: false })
            {
                return false;
            }

            collected[variable.Name] = ExprReferencesName(body, variable.Name, shadowed: false);
        }

        bindings = collected.Count == 0 ? null : collected;
        return collected.Count > 0;
    }

    private bool TryCollectRuntimeRcRecordChildBindings(
        Expr value,
        Expr body,
        out Dictionary<string, bool>? bindings)
    {
        bindings = null;
        if (!TryDescribeConstructorExpression(
                value,
                out ConstructorSymbol? constructor,
                out List<Expr>? arguments,
                out TypeRef.TNamedType? resultType)
            || constructor is null
            || arguments is null
            || resultType is null
            || (!CanRuntimeManageAdt(resultType)
                && !CanRuntimeManageOwnedChildAdt(resultType)))
        {
            return false;
        }

        Dictionary<string, bool> collected = new(StringComparer.Ordinal);
        HashSet<string> referencedNames = FreeVars(body, []);
        for (int i = 0; i < constructor.Arity; i++)
        {
            TypeRef fieldType = Prune(InstantiateConstructorParameterType(constructor, i, resultType));
            if (CanArenaReset(fieldType) || arguments[i] is Expr.RecordLit)
            {
                continue;
            }

            if (fieldType is not TypeRef.TNamedType child
                || arguments[i] is not Expr.Var variable
                || collected.ContainsKey(variable.Name)
                || LookupOwnedValue(variable.Name) is not
                {
                    RuntimeManaged: true,
                    IsDropped: false,
                    Type: TypeRef.TNamedType ownedChild,
                }
                || !ReferenceEquals(ownedChild.Symbol, child.Symbol))
            {
                return false;
            }

            collected[variable.Name] = referencedNames.Contains(variable.Name);
        }

        bindings = collected.Count == 0 ? null : collected;
        return collected.Count > 0;
    }

    private bool TryDescribeConstructorExpression(
        Expr expression,
        out ConstructorSymbol? constructor,
        out List<Expr>? arguments,
        out TypeRef.TNamedType? resultType)
    {
        if (expression is Expr.RecordLit record
            && _constructorSymbols.TryGetValue(record.TypeName, out constructor)
            && constructor is not null
            && constructor.DeclaringSyntax.FieldNames.Count == constructor.Arity)
        {
            Dictionary<string, Expr> providedFields = new(StringComparer.Ordinal);
            foreach ((string name, Expr value) in record.Fields)
            {
                if (!providedFields.TryAdd(name, value))
                {
                    arguments = null;
                    resultType = null;
                    return false;
                }
            }

            arguments = [];
            foreach (string fieldName in constructor.DeclaringSyntax.FieldNames)
            {
                if (!providedFields.TryGetValue(fieldName, out Expr? value))
                {
                    arguments = null;
                    resultType = null;
                    return false;
                }

                arguments.Add(value);
            }

            resultType = InstantiateAdtType(constructor);
            return arguments.Count == record.Fields.Count;
        }

        arguments = [];
        Expr root = CollectCallArgs(expression, arguments);
        if (root is Expr.Var variable
            && _constructorSymbols.TryGetValue(variable.Name, out constructor)
            && constructor is not null)
        {
            resultType = InstantiateAdtType(constructor);
            return arguments.Count == constructor.Arity;
        }

        constructor = null;
        arguments = null;
        resultType = null;
        return false;
    }

    private static bool IsImmediateAdtMatchUse(string name, Expr body)
    {
        if (body is not Expr.Match(Expr.Var value, var cases, _)
            || !string.Equals(value.Name, name, StringComparison.Ordinal)
            || cases.Count < 2)
        {
            return false;
        }

        foreach (MatchCase matchCase in cases)
        {
            bool shadowed = PatternBindings(matchCase.Pattern)
                .Any(boundName => string.Equals(boundName, name, StringComparison.Ordinal));
            if ((matchCase.Guard is not null && ExprReferencesName(matchCase.Guard, name, shadowed))
                || ExprReferencesName(matchCase.Body, name, shadowed))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsImmediateRuntimeRecordMatchUse(Expr body, string recordName)
    {
        return body is Expr.Match(Expr.Var value, [MatchCase matchCase], _)
            && string.Equals(value.Name, recordName, StringComparison.Ordinal)
            && matchCase.Pattern is Pattern.Constructor
            && matchCase.Guard is null
            && !ExprReferencesName(matchCase.Body, recordName, shadowed: false);
    }

    /// <summary>
    /// Proves the deliberately small source boundary for the first RC-managed records. The record
    /// itself may only appear as a qualified field receiver in an immediate scalar expression.
    /// Binder, aggregate, control-flow, async, and handler forms stay on the arena path until RC
    /// ownership is represented across those boundaries.
    /// </summary>
    private static bool IsImmediateCopyUseOfRecord(Expr expr, string recordName)
    {
        return expr switch
        {
            Expr.IntLit or Expr.UIntLit or Expr.BigIntLit or Expr.FloatLit or Expr.StrLit or Expr.RuneLit or Expr.BoolLit => true,
            Expr.Var value => !string.Equals(value.Name, recordName, StringComparison.Ordinal),
            Expr.QualifiedVar => true,
            Expr.Add value => Both(value.Left, value.Right),
            Expr.Subtract value => Both(value.Left, value.Right),
            Expr.Multiply value => Both(value.Left, value.Right),
            Expr.Divide value => Both(value.Left, value.Right),
            Expr.Modulo value => Both(value.Left, value.Right),
            Expr.BitwiseAnd value => Both(value.Left, value.Right),
            Expr.BitwiseOr value => Both(value.Left, value.Right),
            Expr.BitwiseXor value => Both(value.Left, value.Right),
            Expr.ShiftLeft value => Both(value.Left, value.Right),
            Expr.ShiftRight value => Both(value.Left, value.Right),
            Expr.BitwiseNot value => IsImmediateCopyUseOfRecord(value.Operand, recordName),
            Expr.LogicalNot value => IsImmediateCopyUseOfRecord(value.Operand, recordName),
            Expr.GreaterThan value => Both(value.Left, value.Right),
            Expr.GreaterOrEqual value => Both(value.Left, value.Right),
            Expr.LessThan value => Both(value.Left, value.Right),
            Expr.LessOrEqual value => Both(value.Left, value.Right),
            Expr.Equal value => Both(value.Left, value.Right),
            Expr.NotEqual value => Both(value.Left, value.Right),
            Expr.Call value => Both(value.Func, value.Arg),
            _ => false,
        };

        bool Both(Expr left, Expr right)
            => IsImmediateCopyUseOfRecord(left, recordName)
                && IsImmediateCopyUseOfRecord(right, recordName);
    }

    private void PushLetScope(Expr.Let let, int slot, TypeScheme scheme)
    {
        var parent = _scopes.Peek();
        _scopes.Push(new Dictionary<string, Binding>(parent, StringComparer.Ordinal)
        {
            [let.Name] = new Binding.Scheme(slot, scheme, AstSpans.GetLetNameOrDefault(let))
        });
    }

    private void TrackLetOwnership(Expr.Let let, int slot, int valueTemp, TypeRef valueType)
    {
        if (_borrowedTraitDictionaryBindings.Contains(let.Name))
        {
            return;
        }
        var prunedValueType = Prune(valueType);
        RefineTempOwnershipType(valueTemp, prunedValueType);
        var ownedTypeName = GetOwnedTypeName(prunedValueType);
        if (ownedTypeName is not null)
        {
            // Alias detection: when `let y = x` and x is already tracked as owned,
            // record y as an alias of x instead of tracking it independently.
            // This prevents double-Drop: only the original owner emits Drop.
            // Only simple Expr.Var references are recognized as aliases. More complex
            // expressions (function calls, constructors, if/match) produce fresh
            // values that are tracked as new owners.
            if (let.Value is Expr.Var aliasSource && LookupOwnedValue(aliasSource.Name) is not null)
            {
                var resolvedSource = ResolveOwnershipAlias(aliasSource.Name);
                _ownershipAliases[let.Name] = resolvedSource;
            }
            else
            {
                var isResource = GetResourceTypeName(prunedValueType) is not null;
                bool runtimeManaged = IsRuntimeManagedResultTemp(valueTemp);
                ConstructorSymbol? runtimeConstructor = null;
                if (runtimeManaged
                    && TryDescribeConstructorExpression(let.Value, out ConstructorSymbol? constructor, out _, out _))
                {
                    runtimeConstructor = constructor;
                }

                bool runtimeDeepUnique = runtimeManaged && prunedValueType switch
                {
                    TypeRef.TList => IsFreshListConstructionExpression(let.Value),
                    TypeRef.TNamedType named when CanRuntimeManageRecursiveCopyAdt(named)
                        => IsFreshConstructorTree(let.Value, named.Symbol),
                    _ => false,
                };

                TrackOwnedValue(
                    let.Name,
                    slot,
                    ownedTypeName,
                    isResource,
                    AstSpans.GetLetNameOrDefault(let),
                    prunedValueType,
                    runtimeManaged,
                    runtimeConstructor,
                    runtimeDeepUnique);
                if (runtimeManaged && IsTailForwardedBindingResult(let.Body, let.Name))
                {
                    LookupOwnedValue(let.Name)!.ReleaseKind = ResourceReleaseKind.Moved;
                }
            }
        }
    }

    private bool IsRuntimeManagedResultTemp(int valueTemp)
    {
        return _tempOwnershipFacts.TryGetValue(valueTemp, out LoweredTempOwnershipFact? fact)
            && fact.Representation == LoweredTempRepresentation.RuntimeRc;
    }

    private (int Temp, TypeRef Type) PopLetScope(int bodyTemp, TypeRef bodyType)
    {
        // Preserve the result only when the scope has drops that could otherwise
        // invalidate or overwrite the temp holding the body result.
        if (HasAliveOwnedValuesInCurrentScope())
        {
            int resultSlot = NewLocal();
            Emit(new IrInst.StoreLocal(resultSlot, bodyTemp));
            int finalTemp = PopOwnershipScope(bodyType, bodyTemp);
            _scopes.Pop();
            if (finalTemp != bodyTemp)
            {
                // Copy-out occurred: finalTemp is the freshly allocated copy.
                return (finalTemp, bodyType);
            }

            int resultTemp = NewTemp();
            Emit(new IrInst.LoadLocal(resultTemp, resultSlot));
            RecordFrameRestoreTemp(resultTemp, bodyTemp, Prune(bodyType));
            return (resultTemp, bodyType);
        }

        int finalScopeTemp = PopOwnershipScope(bodyType, bodyTemp);
        _scopes.Pop();
        return (finalScopeTemp, bodyType);
    }

    private (int, TypeRef) LowerLetResult(
        Expr.LetResult letResult,
        LoweredValueRequest request)
    {
        using var diagnosticSpan = PushDiagnosticSpan(letResult);
        if (!TryGetStandardResultParts(out var resultSymbol, out var okConstructor, out _))
        {
            return ReturnNeverWithDummyTemp();
        }

        var savedTailPos = _tcoCtx?.InTailPosition ?? false;
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        var (valueTemp, valueType) = LowerExpr(
            letResult.Value,
            request.WithoutExpectedType());
        if (!TryRequireResultType(valueType, resultSymbol, letResult.Value, "let? requires a Result(E, A) expression.", out var errorType, out var successType))
        {
            if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;
            return ReturnNeverWithDummyTemp();
        }

        var resultSlot = NewLocal();
        var errorLabel = NewLabel("let_result_error");
        var endLabel = NewLabel("let_result_end");

        LowerLetResultOkBinding(letResult, valueTemp, okConstructor, successType, errorLabel);

        if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;
        var (bodyTemp, bodyType) = LowerExpr(letResult.Body, request);
        _scopes.Pop();
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        TypeRef resultType;
        if (!TryRequireResultType(bodyType, resultSymbol, letResult.Body, "let? body must produce a Result(E, A) expression.", out var bodyErrorType, out var bodySuccessType))
        {
            resultType = new TypeRef.TNamedType(resultSymbol, [Prune(errorType), NewTypeVar()]);
        }
        else
        {
            Unify(errorType, bodyErrorType);
            resultType = new TypeRef.TNamedType(resultSymbol, [Prune(errorType), Prune(bodySuccessType)]);
        }

        Emit(new IrInst.StoreLocal(resultSlot, bodyTemp));
        Emit(new IrInst.Jump(endLabel));
        Emit(new IrInst.Label(errorLabel));
        Emit(new IrInst.StoreLocal(resultSlot, valueTemp));
        Emit(new IrInst.Label(endLabel));

        var resultTemp = NewTemp();
        Emit(new IrInst.LoadLocal(resultTemp, resultSlot));
        return (resultTemp, Prune(resultType));
    }

    // Emits the Ok-tag test (jumping to errorLabel otherwise) and binds the Ok payload for the
    // let? body, pushing a child scope the caller pops after lowering the body.
    private void LowerLetResultOkBinding(Expr.LetResult letResult, int valueTemp, ConstructorSymbol okConstructor, TypeRef successType, string errorLabel)
    {
        var tagTemp = NewTemp();
        var expectedOkTagTemp = NewTemp();
        var isOkTemp = NewTemp();
        Emit(new IrInst.GetAdtTag(tagTemp, valueTemp));
        Emit(new IrInst.LoadConstInt(expectedOkTagTemp, GetConstructorTag(okConstructor)));
        Emit(new IrInst.CmpIntEq(isOkTemp, tagTemp, expectedOkTagTemp));
        Emit(new IrInst.JumpIfFalse(isOkTemp, errorLabel));

        var payloadTemp = NewTemp();
        Emit(new IrInst.GetAdtField(payloadTemp, valueTemp, 0));

        var boundSlot = NewLocal();
        Emit(new IrInst.StoreLocal(boundSlot, payloadTemp));
        RecordLocalDebugInfo(boundSlot, letResult.Name, successType);
        var child = new Dictionary<string, Binding>(_scopes.Peek(), StringComparer.Ordinal)
        {
            [letResult.Name] = new Binding.Local(boundSlot, Prune(successType), AstSpans.GetLetResultNameOrDefault(letResult))
        };
        _scopes.Push(child);
        RecordHoverType(AstSpans.GetLetResultNameOrDefault(letResult), letResult.Name, successType);
    }

    private (int, TypeRef) LowerLetRecursive(
        Expr.LetRecursive letRecursive,
        LoweredValueRequest request)
    {
        RegisterHoverParameterNames(AstSpans.GetLetRecursiveNameOrDefault(letRecursive), letRecursive.Value);
        RecursiveTraitEvidenceElaboration evidence = PrepareRecursiveTraitEvidence(letRecursive);
        ResolvedBindingSignature signature = ResolveRecursiveTraitSignature(letRecursive, evidence);
        bool usesTraitDictionary = evidence.UsesDictionary;
        TraitDictionaryFunctionInfo? traitDictionaryInfo = evidence.DictionaryInfo;
        Expr.LetRecursive runtimeBinding = evidence.RuntimeBinding;
        int slot = NewLocal();
        // The module system may wrap a lambda in alias lets: let alias = mangled in given (x) -> ...
        // Unwrap let-chains to find the innermost lambda for type and TCO purposes.
        var innerLambda = FindInnermostLambdaUnderLets(runtimeBinding.Value);
        var recursiveType = LowerLetRecursiveBindSelf(runtimeBinding, innerLambda, slot);

        TypeRef? inferenceSeedType = SeedRecursiveTraitInferenceType(
            letRecursive,
            signature,
            usesTraitDictionary,
            recursiveType);

        var savedAnnotationSeed = LowerLetRecursiveSeedAnnotation(innerLambda, inferenceSeedType,
            CountSourceLambdaParameters(letRecursive.Value),
            usesTraitDictionary ? traitDictionaryInfo!.Dictionaries.Count : 0);

        PushTraitConstraintScope();
        (int valTemp, TypeRef valType) valueAndType = LowerLetRecursiveValue(
            runtimeBinding,
            innerLambda,
            recursiveType,
            request.WithoutExpectedType(),
            out bool helperMarkerAdded);
        IReadOnlyList<TraitConstraint> inferredRequirements = PopTraitConstraintScope(
            out bool needsLateTraitTypeHint);
        (inferredRequirements, needsLateTraitTypeHint) = NormalizeRecursiveTraitRequirements(
            signature,
            usesTraitDictionary,
            inferredRequirements,
            needsLateTraitTypeHint);

        RestoreLetRecursiveAnnotationSeed(savedAnnotationSeed);

        LowerLetRecursiveFinalizeValue(
            letRecursive,
            slot,
            recursiveType,
            valueAndType,
            inferredRequirements,
            signature.Requirements,
            usesTraitDictionary ? signature.Type : null,
            usesTraitDictionary ? traitDictionaryInfo!.Dictionaries.Count : 0,
            needsLateTraitTypeHint);

        var (bodyTemp, bodyType) = LowerExpr(letRecursive.Body, request);
        if (helperMarkerAdded)
        {
            _coroutineHelperArity.Remove(letRecursive.Name);
        }

        _scopes.Pop();
        return (bodyTemp, bodyType);
    }

    private ResolvedBindingSignature ResolveRecursiveTraitSignature(
        Expr.LetRecursive binding,
        RecursiveTraitEvidenceElaboration evidence) =>
        _generatedTraitRecursiveTypes.TryGetValue(binding.Name, out TypeRef? generatedTraitType)
            ? new ResolvedBindingSignature(generatedTraitType, [], [])
            : evidence.Signature;

    private static (IReadOnlyList<TraitConstraint> Requirements, bool NeedsLateTypeHint)
        NormalizeRecursiveTraitRequirements(
            ResolvedBindingSignature signature,
            bool usesTraitDictionary,
            IReadOnlyList<TraitConstraint> inferredRequirements,
            bool needsLateTraitTypeHint) =>
        usesTraitDictionary
            ? (signature.Requirements, false)
            : (inferredRequirements, needsLateTraitTypeHint);

    private TypeRef? SeedRecursiveTraitInferenceType(
        Expr.LetRecursive binding,
        ResolvedBindingSignature signature,
        bool usesTraitDictionary,
        TypeRef recursiveType)
    {
        TypeRef? seedType = signature.Type
            ?? ResolveInferredTraitBindingTypeHint(binding.Name, binding.Value);
        if (seedType is not null && !usesTraitDictionary)
        {
            Unify(recursiveType, seedType);
        }
        return seedType;
    }

    private sealed record RecursiveTraitEvidenceElaboration(
        Expr.LetRecursive RuntimeBinding,
        ResolvedBindingSignature Signature,
        TraitDictionaryFunctionInfo? DictionaryInfo,
        bool UsesDictionary);

    private RecursiveTraitEvidenceElaboration PrepareRecursiveTraitEvidence(Expr.LetRecursive binding)
    {
        ResolvedBindingSignature signature = ResolveBindingSignature(
            binding.TypeAnnotation,
            binding.Requires,
            GetSpan(binding));
        bool hasDictionary = _traitDictionaryFunctionsByRecursiveBinding.TryGetValue(
            TraitBindingKey(binding),
            out TraitDictionaryFunctionInfo? info);
        bool usesDictionary = hasDictionary;
        Expr.LetRecursive runtimeBinding = usesDictionary
            ? CopyLetRecursiveSpans(
                binding,
                new Expr.LetRecursive(
                    binding.Name,
                    TransformTraitDictionaryValue(binding.Value, info!, threadRecursiveSelf: true),
                    binding.Body))
            : binding;
        if (usesDictionary)
        {
            BindTraitDictionaryParameterConstraints(info!, signature.SourceOrderedRequirements);
        }
        return new RecursiveTraitEvidenceElaboration(runtimeBinding, signature, info, usesDictionary);
    }

    private (int, TypeRef) LowerLetRecursiveValue(
        Expr.LetRecursive letRecursive,
        Expr.Lambda? innerLambda,
        TypeRef recursiveType,
        LoweredValueRequest request,
        out bool helperMarkerAdded)
    {
        helperMarkerAdded = false;
        if (letRecursive.Value is Expr.Lambda coroutineLambda
            && _inCoroutineBody
            && !IsAsyncIntrinsicCall(GetInnermostBody(coroutineLambda))
            && ContainsAwaitOutsideNestedLambda(GetInnermostBody(coroutineLambda)))
        {
            return LowerLetRecursiveCoroutineHelperValue(
                letRecursive,
                coroutineLambda,
                recursiveType,
                request,
                out helperMarkerAdded);
        }
        if (letRecursive.Value is Expr.Lambda lambda)
        {
            return LowerLetRecursiveLambdaValue(letRecursive, lambda, recursiveType, request);
        }
        if (innerLambda is not null)
        {
            return LowerLetRecursiveAliasChainValue(letRecursive, innerLambda, recursiveType, request);
        }

        ReportDiagnostic(GetSpan(letRecursive.Value), "let recursive currently requires a function value.");
        return LowerExpr(letRecursive.Value, request).AsPair();
    }

    // Binds the recursive name to its slot in a child scope (popped by the caller after the body).
    private TypeRef LowerLetRecursiveBindSelf(Expr.LetRecursive letRecursive, Expr.Lambda? innerLambda, int slot)
    {
        // The self-type's arrow must carry an OPEN row variable, not a null (pure) row: a recursive
        // helper that performs a capability — or captures a capability-performing parameter it applies, as
        // `List.map`'s `mapGo` applies `f` — has an open latent row, and unifying the real open row
        // against a null one would force it closed (`{}`), which then rejects passing any
        // capability-performing function to that helper (or to a combinator like `serve` built on
        // one). The inner arrows of a curried function carry their own rows via the return var.
        var recursiveType = innerLambda is not null
            ? (TypeRef)new TypeRef.TFun(NewTypeVar(), NewTypeVar()) { Row = NewTypeVar() }
            : NewTypeVar();
        RecordLocalDebugInfo(slot, letRecursive.Name, recursiveType);

        var parent = _scopes.Peek();
        var child = new Dictionary<string, Binding>(parent, StringComparer.Ordinal)
        {
            [letRecursive.Name] = new Binding.Local(slot, recursiveType, AstSpans.GetLetRecursiveNameOrDefault(letRecursive))
        };
        _scopes.Push(child);
        return recursiveType;
    }

    // Seed the recursive function's parameter types from its declared annotation BEFORE lowering
    // the body, so that operator-overload resolution inside the body (e.g. `a * b` on Float
    // params) sees the annotated types rather than defaulting an unresolved type var to Int.
    // Resolving the annotation against recursiveType up front also makes self-calls type-check
    // against the declared arrow. Restored after the value branches so nested lets don't inherit it.
    // Returns the previous seed state for the caller to restore.
    private (IReadOnlyList<TypeRef>? SavedTypes, int SavedCursor, Expr.Lambda? SavedTarget)
        LowerLetRecursiveSeedAnnotation(
        Expr.Lambda? innerLambda,
        TypeRef? annotationType,
        int sourceParameterCount,
        int hiddenParameterCount)
    {
        var savedAnnotationParamTypes = _annotationParamTypes;
        var savedAnnotationParamCursor = _annotationParamCursor;
        Expr.Lambda? savedAnnotationTarget = _annotationTargetLambda;
        _annotationParamTypes = null;
        _annotationParamCursor = 0;
        _annotationTargetLambda = innerLambda;
        if (annotationType is not null && innerLambda is not null)
        {
            _annotationParamTypes = Enumerable.Range(0, hiddenParameterCount)
                .Select(_ => NewTypeVar())
                .Concat(PeelAnnotationParamTypes(annotationType, sourceParameterCount))
                .ToArray();
            _annotationParamCursor = 0;
        }

        return (savedAnnotationParamTypes, savedAnnotationParamCursor, savedAnnotationTarget);
    }

    private static int CountSourceLambdaParameters(Expr value) =>
        FindInnermostLambdaUnderLets(value) is { } lambda ? CountLambdaChain(lambda) : 0;

    private bool TryGetTcoLambdaContinuation(Expr body, out Expr.Lambda continuation)
    {
        Expr current = body;
        while (true)
        {
            if (current is Expr.Lambda lambda)
            {
                continuation = lambda;
                return true;
            }
            if (current is Expr.Match
                {
                    Value: Expr.Var { Name: var dictionaryName },
                    Cases.Count: 1,
                } match
                && _traitDictionaryParameterMetadata.ContainsKey(dictionaryName))
            {
                current = match.Cases[0].Body;
                continue;
            }
            if (current is Expr.Let binding
                && _borrowedTraitDictionaryBindings.Contains(binding.Name))
            {
                current = binding.Body;
                continue;
            }
            continuation = null!;
            return false;
        }
    }

    private (List<string> Parameters, Expr Body) DescribeTcoLambdaChain(Expr.Lambda first)
    {
        List<string> parameters = [];
        Expr.Lambda current = first;
        while (true)
        {
            parameters.Add(current.ParamName);
            if (!TryGetTcoLambdaContinuation(current.Body, out Expr.Lambda continuation))
            {
                return (parameters, current.Body);
            }
            current = continuation;
        }
    }

    private void RestoreLetRecursiveAnnotationSeed(
        (IReadOnlyList<TypeRef>? SavedTypes, int SavedCursor, Expr.Lambda? SavedTarget) saved)
    {
        _annotationParamTypes = saved.SavedTypes;
        _annotationParamCursor = saved.SavedCursor;
        _annotationTargetLambda = saved.SavedTarget;
    }

    // Async tail-recursive loop: the helper's body awaits and it is defined inside a coroutine
    // body, so lower it as a task-returning closure around a transparent coroutine (awaits
    // suspend on the enclosing run; self tail calls restart the coroutine in place). Without
    // this, every await in the helper would compile to a nested blocking scheduler run.
    private (int, TypeRef) LowerLetRecursiveCoroutineHelperValue(
        Expr.LetRecursive letRecursive,
        Expr.Lambda lam,
        TypeRef recursiveType,
        LoweredValueRequest request,
        out bool helperMarkerAdded)
    {
        var loopParamCount = DescribeTcoLambdaChain(lam).Parameters.Count;
        var savedPending = _pendingHelperCoroutine;
        var savedHelperTco = _tcoCtx;
        _tcoCtx = null;
        _pendingHelperCoroutine = new HelperCoroutineInfo(letRecursive.Name, CollectLambdaParams(lam), GetInnermostBody(lam));
        helperMarkerAdded = !_coroutineHelperArity.ContainsKey(letRecursive.Name);
        if (helperMarkerAdded)
        {
            _coroutineHelperArity[letRecursive.Name] = loopParamCount;
        }

        var valueAndType = LowerLambdaRecursive(
            letRecursive.Name,
            recursiveType,
            lam,
            request: request);
        _pendingHelperCoroutine = savedPending;
        _tcoCtx = savedHelperTco;
        return valueAndType;
    }

    private (int, TypeRef) LowerLetRecursiveLambdaValue(
        Expr.LetRecursive letRecursive,
        Expr.Lambda lam2,
        TypeRef recursiveType,
        LoweredValueRequest request)
    {
        // Detect lambda chain for TCO: given (x) -> given (y) -> body
        (List<string> tcoParamNames, Expr innermostBody) = DescribeTcoLambdaChain(lam2);
        var paramCount = tcoParamNames.Count;
        var hasTailSelfCalls = HasTailSelfCalls(innermostBody, letRecursive.Name, paramCount);

        var savedTcoCtx = _tcoCtx;
        if (hasTailSelfCalls)
        {
            _tcoCtx = CreateRecursiveTcoContext(letRecursive, tcoParamNames, paramCount);
        }
        else
        {
            _tcoCtx = null;
        }

        var valueAndType = LowerLambdaRecursive(
            letRecursive.Name,
            recursiveType,
            lam2,
            request: request);

        _tcoCtx = savedTcoCtx;
        return valueAndType;
    }

    private TcoContext CreateRecursiveTcoContext(
        Expr.LetRecursive binding,
        List<string> parameterNames,
        int parameterCount)
    {
        FuncKey? ownershipFunction = GetRegisteredFunctionKey(binding);
        var facts = GetTcoParameterOrdinalFacts(ownershipFunction);
        int evidenceCount = parameterNames.TakeWhile(parameter =>
            _traitDictionaryParameterMetadata.ContainsKey(parameter)
            || _opParamMeta.ContainsKey(parameter)).Count();
        IReadOnlySet<int> Shift(IReadOnlySet<int> ordinals) => ordinals
            .Select(ordinal => ordinal + evidenceCount)
            .ToHashSet();
        HashSet<int> loopInvariant = Enumerable.Range(0, evidenceCount)
            .Concat(Shift(facts.LoopInvariant))
            .ToHashSet();
        PatternBindingOwnershipFact[] patternBindings = GetPatternBindingOwnershipFacts(ownershipFunction)
            .Select(fact => fact.RootParameterOrdinal < 0
                ? fact
                : fact with { RootParameterOrdinal = fact.RootParameterOrdinal + evidenceCount })
            .ToArray();
        return new TcoContext(
            binding.Name, parameterCount, parameterNames, loopInvariant,
            Shift(facts.ArenaSelfContainedListRebuild),
            Shift(facts.FreshClosureRebuild),
            Shift(facts.BytesProvenanceSafeListRebuild),
            Shift(facts.AffineConsList),
            Shift(facts.ConsumedListTail),
            Shift(facts.BorrowInspectOnly),
            Shift(facts.AffineSelfAppendOnly),
            patternBindings)
        {
            InTailPosition = false,
            OwnershipFunction = ownershipFunction,
        };
    }

    // Value is a let-chain of alias bindings (injected by the module system) wrapping a lambda.
    // Process each alias let into scope first, then lower the innermost lambda with the
    // self-reference (selfName) set so that recursive calls use Binding.Self rather than
    // capturing the uninitialized slot value (which would be 0 at closure-creation time).
    //
    // Self-aliases (let unmangledName = mangledSelf) must NOT be processed as regular lets
    // because the mangled slot is uninitialized at this point. Instead, they are collected
    // as selfAliases and given Binding.Self treatment inside LowerLambdaCore.
    private (int, TypeRef) LowerLetRecursiveAliasChainValue(
        Expr.LetRecursive letRecursive,
        Expr.Lambda innerLambda,
        TypeRef recursiveType,
        LoweredValueRequest request)
    {
        var savedTcoCtx = _tcoCtx;
        _tcoCtx = null;

        int aliasCount = 0;
        List<string>? selfAliases = null;
        var aliasExpr = letRecursive.Value;
        while (aliasExpr is Expr.Let aliasLet)
        {
            if (aliasLet.Value is Expr.Var selfVar && string.Equals(selfVar.Name, letRecursive.Name, StringComparison.Ordinal))
            {
                // Self-alias: let unmangledName = mangledSelf — skip slot capture, pass as Binding.Self alias.
                selfAliases ??= new List<string>();
                selfAliases.Add(aliasLet.Name);
            }
            else
            {
                var (aliasValueTemp, aliasValueType) = LowerExpr(
                    aliasLet.Value,
                    request);
                int aliasSlot = NewLocal();
                Emit(new IrInst.StoreLocal(aliasSlot, aliasValueTemp));
                RecordLocalDebugInfo(aliasSlot, aliasLet.Name, aliasValueType);
                var aliasScheme = Generalize(Prune(aliasValueType));
                RecordHoverScheme(AstSpans.GetLetNameOrDefault(aliasLet), aliasLet.Name, aliasScheme);
                PushLetScope(aliasLet, aliasSlot, aliasScheme);
                aliasCount++;
            }

            aliasExpr = aliasLet.Body;
        }

        var valueAndType = LowerLambdaRecursive(
            letRecursive.Name,
            recursiveType,
            innerLambda,
            selfAliases: selfAliases,
            request: request);

        for (int i = 0; i < aliasCount; i++)
        {
            _scopes.Pop();
        }

        _tcoCtx = savedTcoCtx;
        return valueAndType;
    }

    // Unifies the lowered value with the self-type (and the declared annotation, if any), records
    // hover info, and stores the closure into the recursive slot.
    private void LowerLetRecursiveFinalizeValue(
        Expr.LetRecursive letRecursive,
        int slot,
        TypeRef recursiveType,
        (int valTemp, TypeRef valType) valueAndType,
        IReadOnlyList<TraitConstraint> inferredRequirements,
        IReadOnlyList<TraitConstraint> writtenRequirements,
        TypeRef? exposedType,
        int hiddenDictionaryCount,
        bool needsLateTraitTypeHint)
    {
        Unify(recursiveType, valueAndType.valType);
        TypeRef schemeType = recursiveType;
        if (exposedType is not null)
        {
            TypeRef cursor = PeelRecursiveTraitDictionaryParameters(
                letRecursive,
                recursiveType,
                hiddenDictionaryCount);
            Unify(exposedType, cursor);
            schemeType = exposedType;
        }
        IReadOnlyList<TraitConstraint> requirements = SelectBindingConstraints(
            IsInferredTraitBinding(letRecursive) ? writtenRequirements : inferredRequirements,
            writtenRequirements,
            schemeType,
            GetSpan(letRecursive),
            letRecursive.Name.StartsWith("__trait_validate_implementation_", StringComparison.Ordinal)
                || letRecursive.Name.StartsWith("__trait_impl_", StringComparison.Ordinal),
            SuppressSourceConstraintDiagnostics(letRecursive.Name));
        Dictionary<string, Binding> selfScope = _scopes.Pop();
        TypeScheme recursiveScheme = GeneralizeBindingType(Prune(schemeType), requirements);
        RecordInferredTraitBindingElaboration(
            letRecursive,
            recursiveScheme,
            writtenRequirements,
            inferredRequirements,
            needsLateTraitTypeHint);
        if (hiddenDictionaryCount > 0)
        {
            TraitDictionaryFunctionInfo? dictionaryInfo =
                _traitDictionaryFunctionsByRecursiveBinding.GetValueOrDefault(TraitBindingKey(letRecursive));
            RegisterTraitDictionaryScheme(recursiveScheme, dictionaryInfo);
        }
        _scopes.Push(selfScope);
        selfScope[letRecursive.Name] = new Binding.Scheme(
            slot,
            recursiveScheme,
            AstSpans.GetLetRecursiveNameOrDefault(letRecursive));
        ValidateBindingConstraintBoundary(
            recursiveScheme,
            GetSpan(letRecursive));
        RecordHoverScheme(
            AstSpans.GetLetRecursiveNameOrDefault(letRecursive),
            letRecursive.Name,
            recursiveScheme,
            GetDeclaredHoverParameterNames(letRecursive.Value));
        Emit(new IrInst.StoreLocal(slot, valueAndType.valTemp));
        RegisterLoweredRecursiveFunctionIdentity(letRecursive, slot, recursiveScheme);
    }

    private void RegisterLoweredRecursiveFunctionIdentity(
        Expr.LetRecursive letRecursive,
        int slot,
        TypeScheme recursiveScheme)
    {
        // Register an empty-env recursive top-level function so a specialization generated later (in an
        // isolated scope) can reference it by-label — as static code with a null env — rather than
        // capturing it. Its self-recursion already goes through Binding.Self, so it captures nothing.
        // This is what lets a parallel specialization call the module's list helpers across a fork
        // boundary without materializing an arena closure a worker could race. Guarded like the
        // non-recursive registration below: exactly this function's own depth-0 lambda was lowered last.
        // Generalize against the parent scope (pop the self-binding first) so the scheme quantifies the
        // function's own type vars — otherwise the self-binding keeps them in the environment and two
        // specializations at different element types would share (and conflict on) one monotype.
        if (_lambdaDepth == 0 && _lastLoweredLambdaEmptyEnv && letRecursive.Value is Expr.Lambda)
        {
            var helperScheme = FreshenScheme(recursiveScheme);
            CopyTraitDictionarySchemeRegistration(recursiveScheme, helperScheme);
            _topLevelFunctionRefs[letRecursive.Name] = (_lastLoweredLambdaLabel, helperScheme);
            _knownFunctionLabelsBySlot[slot] = _lastLoweredLambdaLabel;
            _functionNameByLabel[_lastLoweredLambdaLabel] = letRecursive.Name;
            RegisterOwnershipFunctionLabel(_lastLoweredLambdaLabel, letRecursive);
        }
        else if (_lambdaDepth == 0 && letRecursive.Value is Expr.Lambda)
        {
            // A capturing top-level `let recursive` (one whose body calls another top-level helper, so
            // its own closure environment is non-empty) still has a statically known code label at this
            // declaration site — mirrors LowerLetRegisterKnownFunctionIdentity's own fallback for a
            // plain, non-recursive top-level let. Without this, a recursive function's result is never
            // resolvable by TryResolveKnownFunctionResultOwnership at any of its call sites (the
            // resultLabel -> function name reverse lookup always misses), silently discarding
            // FunctionOwnershipSummary.ResultProvenance for every non-trivial recursive function in a
            // program — not just an unresolved edge case, but the common case, since a recursive helper
            // calling any sibling top-level function is the ordinary shape, not the exception.
            _knownFunctionLabelsBySlot[slot] = _lastLoweredLambdaLabel;
            _functionNameByLabel[_lastLoweredLambdaLabel] = letRecursive.Name;
            RegisterOwnershipFunctionLabel(_lastLoweredLambdaLabel, letRecursive);
        }
    }

    private TypeRef PeelRecursiveTraitDictionaryParameters(
        Expr.LetRecursive binding,
        TypeRef runtimeType,
        int dictionaryCount)
    {
        TypeRef cursor = runtimeType;
        for (int index = 0; index < dictionaryCount; index++)
        {
            cursor = Prune(cursor);
            if (cursor is TypeRef.TFun function)
            {
                cursor = function.Ret;
                continue;
            }
            ReportDiagnostic(
                GetSpan(binding.Value),
                $"Internal trait evidence elaboration for recursive binding '{binding.Name}' lost a dictionary parameter.",
                InvalidTraitDeclarationCode);
            break;
        }
        return cursor;
    }



































    private (int, TypeRef) LowerIf(
        Expr.If iff,
        LoweredValueRequest request)
    {
        using var diagnosticSpan = PushDiagnosticSpan(iff);
        // Condition is NOT in tail position
        var savedTailPos = _tcoCtx?.InTailPosition ?? false;
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        var (cTemp, cType) = LowerExpr(iff.Cond);
        var ct = Prune(cType);
        Unify(ct, new TypeRef.TBool());

        var elseLabel = NewLabel("else");
        var endLabel = NewLabel("endif");

        Emit(new IrInst.JumpIfFalse(cTemp, elseLabel));

        // Both branches ARE in tail position (if the if itself is)
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;

        // In-place reuse: only one branch runs at a time, so a live reuse token is available to each
        // independently. Snapshot before Then, restore before Else, so both branches may reuse the
        // same dead cell (at runtime only one does).
        var reuseTokensAtIf = new List<ReuseToken>(_reuseTokens);

        int slot = NewLocal();
        var thenCredits = BeginExclusiveBranch([iff.Else]);
        var (tTemp, tType) = LowerExpr(iff.Then, request);
        EndExclusiveBranch(thenCredits);
        var thenType = Prune(tType);
        Emit(new IrInst.StoreLocal(slot, tTemp));

        Emit(new IrInst.Jump(endLabel));
        Emit(new IrInst.Label(elseLabel));

        _reuseTokens.Clear();
        _reuseTokens.AddRange(reuseTokensAtIf);

        if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;
        var elseCredits = BeginExclusiveBranch([iff.Then]);
        var (eTemp, eType) = LowerIfElseBranch(iff.Else, request, thenType);
        EndExclusiveBranch(elseCredits);
        var elseType = Prune(eType);
        Emit(new IrInst.StoreLocal(slot, eTemp));

        // if expression result: put into a temp (phi) by storing chosen into target
        int target = NewTemp();
        Emit(new IrInst.Label(endLabel));
        Emit(new IrInst.LoadLocal(target, slot));

        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        var resultType = thenType is TypeRef.TNever ? elseType : thenType;
        MarkUniformRuntimeManagedResult(target, tTemp, eTemp, Prune(resultType));
        return (target, Prune(resultType));
    }

    private LoweredValue LowerIfElseBranch(
        Expr expression,
        LoweredValueRequest request,
        TypeRef expectedType)
    {
        using var diagnosticContext = PushDiagnosticContext("in if branches");
        return LowerExpr(expression, request.WithExpectedType(expectedType));
    }

    private void MarkUniformRuntimeManagedResult(
        int resultTemp,
        int leftTemp,
        int rightTemp,
        TypeRef resultType)
    {
        bool runtimeManaged =
            IsRuntimeManagedResultTemp(leftTemp) && IsRuntimeManagedResultTemp(rightTemp);
        RecordControlFlowJoinTemp(resultTemp, resultType, runtimeManaged);
    }

    private (int, TypeRef) LowerLambda(
        Expr.Lambda lam,
        bool stackAllocateClosure = false,
        LoweredValueRequest request = default)
    {
        return LowerLambdaCore(
            lam,
            null,
            null,
            stackAllocateClosure,
            request: request);
    }

    private (int, TypeRef) LowerLambdaRecursive(
        string selfName,
        TypeRef selfType,
        Expr.Lambda lam,
        bool stackAllocateClosure = false,
        IReadOnlyList<string>? selfAliases = null,
        LoweredValueRequest request = default)
    {
        return LowerLambdaCore(
            lam,
            selfName,
            selfType,
            stackAllocateClosure,
            selfAliases,
            request: request);
    }

    private (int, TypeRef) LowerLambdaCore(
        Expr.Lambda lam,
        string? selfName,
        TypeRef? selfType,
        bool stackAllocateClosure,
        IReadOnlyList<string>? selfAliases = null,
        RecursiveGroupContext? recursiveGroup = null,
        string? forcedLabel = null,
        IrFunctionOriginSeed? originSeed = null,
        LoweredValueRequest request = default)
    {
        _usesClosures = true;

        // Create type variables for param, return, and the arrow's capability row. The row variable
        // becomes the body's ambient row: every operation performed and capability-performing call made while
        // lowering the body inserts its capabilities there, so the arrow ends up carrying exactly the
        // capabilities the body performs (open, generalized at the enclosing let).
        var (paramTy, retTy, rowTy, funTy) = CreateLambdaTypes();
        LowerLambdaCoreApplyExpectedType(request, funTy);
        LowerLambdaCoreSeedParamType(lam, paramTy);

        // Compute free variables for capture, then allocate and fill the env at the creation site.
        var (free, captures, envPtrTemp, knownCaptureLabels) =
            LowerLambdaCoreBuildEnv(lam, selfName, recursiveGroup, stackAllocateClosure, request);

        string label = forcedLabel ?? $"lambda_{_nextLambdaId++}";
        RecordTcoParamIdentity(lam, paramTy, label);
        LambdaFunctionPlacementFrame placementFrame =
            LowerLambdaCoreEnterFunctionPlacement(lam, label, originSeed);

        // Build function body IR in isolation
        var savedFrame = LowerLambdaCoreSaveFrame(label, captures);
        int argSlot = LowerLambdaCoreResetFrame();
        RecordLocalDebugInfo(argSlot, lam.ParamName, paramTy);
        LowerLambdaCoreBuildScope(lam, label, paramTy, argSlot, free, captures, knownCaptureLabels, selfName, selfType, selfAliases, recursiveGroup, savedFrame.Scopes);

        var (isChainLambda, isInnermostTco, reuseEntryCopies, specElidedAccs, reuseInsertIndex) =
            LowerLambdaCoreSetupTco(lam, label, captures);

        var outerTcoCtx = LowerLambdaCoreSuspendOuterTco(isChainLambda, lam);
        var savedTcoCtx = isInnermostTco ? outerTcoCtx : null;
        var (bodyTemp, bodyType) = LowerLambdaCoreLowerBody(lam, rowTy, selfName);
        if (isInnermostTco && savedTcoCtx is not null) savedTcoCtx.InTailPosition = false;

        LowerLambdaCoreFinalizeTcoOwnership(
            lam, reuseEntryCopies, savedTcoCtx, reuseInsertIndex, specElidedAccs, bodyTemp);

        _tcoCtx = outerTcoCtx;
        if (isChainLambda) _tcoCtx!.DescendingChain = isChainLambda;

        bodyTemp = FinalizeLambdaBodyOwnership(lam.Body, bodyTemp, bodyType, retTy);
        RecordReturnedClosureLabel(label, bodyTemp);
        // Accurate regardless of *why* the result is RuntimeManaged (fresh construction, TCO accumulator
        // representation, closure capture — see _bodyRuntimeManagedByLabel's own doc). Threaded to this
        // lambda's own MakeClosure call below (read before RestoreFrame restores the enclosing temp facts)
        // and persisted by label for LowerVarUnbound/Binding.Self, which reconstruct a reference to this
        // same already-compiled function from a different scope later.
        bool bodyRuntimeManaged = IsRuntimeManagedResultTemp(bodyTemp);
        _bodyRuntimeManagedByLabel[label] = bodyRuntimeManaged;
        LowerLambdaCoreEmitRuntimeManagedTcoExitDrops(savedTcoCtx, bodyTemp);
        Emit(new IrInst.Return(bodyTemp));

        LowerLambdaCoreFinishFunction(label, placementFrame.Origin);
        LowerLambdaCoreRestoreFrame(savedFrame);
        LowerLambdaCoreLeaveFunctionPlacement(placementFrame);

        return (
            LowerLambdaCoreMakeClosure(
                label, envPtrTemp, captures, stackAllocateClosure, bodyRuntimeManaged, request),
            funTy);
    }

    private int FinalizeLambdaBodyOwnership(
        Expr body,
        int bodyTemp,
        TypeRef bodyType,
        TypeRef resultType)
    {
        Unify(bodyType, resultType);
        return RetainDirectCapturedRuntimeManagedLambdaResult(body, bodyTemp);
    }

    private int RetainDirectCapturedRuntimeManagedLambdaResult(Expr body, int bodyTemp)
    {
        Expr result = body;
        while (result is Expr.Let let)
        {
            result = let.Body;
        }

        if (result is not Expr.Var variable
            || Lookup(variable.Name) is not (Binding.Env or Binding.EnvScheme)
            || LookupOwnedValue(variable.Name) is not
            { IsDropped: false, RuntimeManaged: true } owner)
        {
            return bodyTemp;
        }

        int retainedTemp = NewTemp();
        Emit(new IrInst.RcDup(
            retainedTemp,
            bodyTemp,
            RuntimeManaged: true,
            MayBeEmpty: owner.Type is TypeRef.TList));
        MarkRuntimeManagedTemp(retainedTemp);
        return retainedTemp;
    }

    private readonly record struct LambdaFunctionPlacementFrame(
        IrFunctionOrigin Origin,
        IrFunctionOrigin? SavedOrigin,
        OwnershipPlacementContext SavedPlacement);

    private LambdaFunctionPlacementFrame LowerLambdaCoreEnterFunctionPlacement(
        Expr.Lambda lambda,
        string label,
        IrFunctionOriginSeed? originSeed)
    {
        IrFunctionOrigin origin = CreateLambdaOrigin(lambda, label, originSeed);
        var frame = new LambdaFunctionPlacementFrame(
            origin,
            _activeFunctionOrigin,
            _ownershipPlacementContext);
        _activeFunctionOrigin = origin;
        _ownershipPlacementContext = EnterFunctionOwnershipPlacement(
            origin,
            label,
            frame.SavedPlacement);
        return frame;
    }

    private void LowerLambdaCoreLeaveFunctionPlacement(
        LambdaFunctionPlacementFrame frame)
    {
        _activeFunctionOrigin = frame.SavedOrigin;
        _ownershipPlacementContext = frame.SavedPlacement;
    }

    private (TypeRef Param, TypeRef Ret, TypeRef Row, TypeRef.TFun Function)
        CreateLambdaTypes()
    {
        TypeRef param = NewTypeVar();
        TypeRef ret = NewTypeVar();
        TypeRef row = NewTypeVar();
        return (param, ret, row, new TypeRef.TFun(param, ret) { Row = row });
    }

    private void LowerLambdaCoreApplyExpectedType(
        LoweredValueRequest request,
        TypeRef.TFun functionType)
    {
        if (request.ExpectedType is not null)
        {
            Unify(request.ExpectedType, functionType);
        }
    }

    // TCO: for the innermost lambda in a recursive chain, create local copies of captured params and
    // emit a loop start label so tail self-calls can jump back (see LowerLambdaCoreEnterTcoLoop).
    private (bool IsChainLambda, bool IsInnermostTco,
        List<ReuseEntryCopyCandidate> ReuseEntryCopies,
        HashSet<string> SpecElidedAccs,
        int ReuseInsertIndex)
        LowerLambdaCoreSetupTco(Expr.Lambda lam, string label, IReadOnlyList<string> captures)
    {
        bool isChainLambda = _tcoCtx?.DescendingChain ?? false;
        if (isChainLambda && _tcoCtx!.SelfLabel.Length == 0)
        {
            // The curried self reference captured by inner chain lambdas resolves back to this
            // outermost label. Keep that stable identity so a same-named lexical binding cannot be
            // mistaken for the recursive root at a live back edge.
            _tcoCtx.SelfLabel = label;
        }

        bool continuesChain = TryGetTcoLambdaContinuation(lam.Body, out _);
        var isInnermostTco = isChainLambda && !continuesChain;
        var reuseEntryCopies = new List<ReuseEntryCopyCandidate>();
        var specElidedAccs = new HashSet<string>(StringComparer.Ordinal);
        int reuseInsertIndex = -1;
        if (isInnermostTco)
        {
            reuseInsertIndex = LowerLambdaCoreEnterTcoLoop(
                lam,
                label,
                captures,
                reuseEntryCopies,
                specElidedAccs);
        }

        return (
            isChainLambda,
            isInnermostTco,
            reuseEntryCopies,
            specElidedAccs,
            reuseInsertIndex);
    }

    private void LowerLambdaCoreFinalizeTcoOwnership(
        Expr.Lambda lam,
        List<ReuseEntryCopyCandidate> reuseEntryCopies,
        TcoContext? tco,
        int reuseInsertIndex,
        HashSet<string> specElidedAccs,
        int bodyTemp)
    {
        LowerLambdaCoreRefreshRuntimeManagedTcoParams(tco);
        FinalizePerceusPatternBindingOwners(tco);
        ResolvePendingRuntimeArgumentFlags(tco);
        LowerLambdaCoreSpliceTcoEntryOwnership(
            reuseEntryCopies,
            tco,
            reuseInsertIndex);
        LowerLambdaCoreRecordAccStableFold(lam, tco, specElidedAccs);
        LowerLambdaCoreRefreshRuntimeManagedTcoResult(tco, bodyTemp);
    }

    private void FinalizePerceusPatternBindingOwners(TcoContext? tco)
    {
        if (tco is null || _patternBindingPlacementSites.Count == 0)
        {
            _patternBindingPlacementSites.Clear();
            return;
        }

        List<PatternBindingPlacementSite> placed = [];
        foreach (PatternBindingPlacementSite site in _patternBindingPlacementSites)
        {
            PatternBindingPlacementOutcome outcome = ResolvePatternBindingPlacementOutcome(site, tco);
            PatternBindingOwnershipFact ownership = site.Ownership;
            _patternBindingOwnershipDecisions.Add(new PatternBindingOwnershipDecision(
                ownership.Function,
                ownership.BindingOrdinal,
                ownership.BindingName,
                site.LocalSlot,
                ownership.RootParameterOrdinal,
                ownership.RootParameterName,
                ownership.ParentBindingOrdinal,
                ownership.ExtractionDepth,
                ownership.Uses,
                ownership.Ownership,
                ownership.Location,
                outcome));
            if (outcome == PatternBindingPlacementOutcome.ProtectiveOwnerPlaced)
            {
                PromotePatternBindingOwnerMarkers(site);
                placed.Add(site);
            }
        }

        foreach (PatternBindingPlacementSite site in placed
            .OrderByDescending(candidate => candidate.InsertIndex)
            .ThenByDescending(candidate => candidate.Ownership.BindingOrdinal))
        {
            SplicePerceusPatternBindingOwnerDup(
                site.LocalSlot,
                site.InsertIndex,
                MayUseEmptyListRepresentation(site.Type));
        }

        _patternBindingPlacementSites.Clear();
    }

    private PatternBindingPlacementOutcome ResolvePatternBindingPlacementOutcome(
        PatternBindingPlacementSite site,
        TcoContext tco)
    {
        if (CanArenaReset(Prune(site.Type)))
        {
            return PatternBindingPlacementOutcome.CopyType;
        }

        if (!site.Ownership.RequiresProtectiveDup)
        {
            return site.Ownership.Ownership == PatternBindingOwnershipKind.TransferredToSameParameter
                ? PatternBindingPlacementOutcome.TransferredToSameParameter
                : PatternBindingPlacementOutcome.Borrowed;
        }

        return tco.IsRuntimeManagedSlot(site.RootParameterSlot)
            ? PatternBindingPlacementOutcome.ProtectiveOwnerPlaced
            : PatternBindingPlacementOutcome.RootNotRuntimeManaged;
    }

    private void PromotePatternBindingOwnerMarkers(PatternBindingPlacementSite site)
    {
        string typeName = GetOwnedTypeName(Prune(site.Type)) ?? "PatternBinding";
        bool mayBeEmpty = MayUseEmptyListRepresentation(site.Type);
        // The marker drop is placed as one instruction, so a value whose release reaches past its own
        // allocation names a helper here rather than expanding the walk inline. Synthesized before the
        // rewrite below, which is scanning the instruction list this would otherwise mutate.
        string? structuralDropperLabel = SynthesizeStructuralOwnerDropper(site.Type);
        HashSet<int> aliases = [];
        bool changed;
        do
        {
            changed = false;
            for (int i = 0; i < _inst.Count; i++)
            {
                switch (_inst[i])
                {
                    case IrInst.LoadLocal load when load.Slot == site.LocalSlot:
                        changed |= aliases.Add(load.Target);
                        break;
                    case IrInst.Borrow borrow when aliases.Contains(borrow.SourceTemp):
                        changed |= aliases.Add(borrow.Target);
                        break;
                    case IrInst.RcDup duplicate when aliases.Contains(duplicate.SourceTemp):
                        if (!duplicate.RuntimeManaged)
                        {
                            _inst[i] = duplicate with { RuntimeManaged = true, MayBeEmpty = mayBeEmpty };
                        }
                        changed |= aliases.Add(duplicate.Target);
                        break;
                    case IrInst.RcDrop drop when drop.OwnerSlot == site.LocalSlot:
                        _inst[i] = drop with
                        {
                            TypeName = typeName,
                            RuntimeManaged = true,
                            MayBeEmpty = mayBeEmpty,
                            StructuralDropperLabel = structuralDropperLabel,
                        };
                        break;
                }
            }
        }
        while (changed);

        foreach (int temp in aliases)
        {
            MarkRuntimeManagedTemp(
                temp,
                LoweredTempOwnershipReason.OwnershipTransfer,
                type: Prune(site.Type),
                location: site.Ownership.Location);
        }
    }

    private void SplicePerceusPatternBindingOwnerDup(int localSlot, int insertIndex, bool mayBeEmpty)
    {
        int generatedStart = _inst.Count;
        EmitPerceusPatternBindingOwnerDup(localSlot, mayBeEmpty);
        int generatedCount = _inst.Count - generatedStart;
        List<IrInst> generated = _inst.GetRange(generatedStart, generatedCount);
        _inst.RemoveRange(generatedStart, generatedCount);
        _inst.InsertRange(insertIndex, generated);
    }

    private void EmitPerceusPatternBindingOwnerDup(int localSlot, bool mayBeEmpty)
    {
        int valueTemp = NewTemp();
        Emit(new IrInst.LoadLocal(valueTemp, localSlot));
        int duplicatedTemp = NewTemp();
        Emit(new IrInst.RcDup(duplicatedTemp, valueTemp, RuntimeManaged: true, MayBeEmpty: mayBeEmpty));
        Emit(new IrInst.StoreLocal(localSlot, duplicatedTemp));
    }

    /// <summary>
    /// True when the resolved type's values can be the empty list, whose representation is the null
    /// pointer rather than a cell carrying a reference-count header. Reference-count updates on such
    /// a value must be skipped rather than applied to a header that does not exist.
    /// </summary>
    private bool MayUseEmptyListRepresentation(TypeRef type) => Prune(type) is TypeRef.TList;

    private void LowerLambdaCoreRefreshRuntimeManagedTcoParams(TcoContext? tco)
    {
        if (tco is null)
        {
            return;
        }

        // The first scan runs before the body has constrained every parameter type. A list whose
        // head comes from another inferred function can therefore still be a TVar at loop entry
        // and miss RC eligibility. Re-run the scan after lowering the complete body, when the
        // self-call has resolved those types, so entry normalization and deferred back-edge
        // lowering see the final ownership shape.
        LowerLambdaCoreIdentifyRuntimeManagedTcoParams(
            _scopes.Peek(),
            tco,
            TcoPlacementResolutionPoint.PostBodyRefresh,
            includeFreshClosures: true);
        foreach (int slot in tco.RuntimeManagedSlotsInOrder)
        {
            if (tco.GetRuntimeManagedType(slot) is TypeRef.TFun)
            {
                if (!tco.RuntimeManagedClosureActiveSlots.ContainsKey(slot))
                {
                    tco.RuntimeManagedClosureActiveSlots[slot] = NewLocal();
                    tco.RuntimeManagedClosureSlotsNeedingEntryInitialization.Add(slot);
                }
            }
            else if (!tco.RuntimeManagedParamActiveSlots.ContainsKey(slot))
            {
                tco.RuntimeManagedParamActiveSlots[slot] = NewLocal();
            }
        }

        RecordTcoPlacementSnapshot(tco);
    }

    private void ResolvePendingRuntimeArgumentFlags(TcoContext? tco)
    {
        foreach ((int flagTemp, int parameterSlot) in _pendingRuntimeArgumentFlags)
        {
            bool runtimeManaged = tco?.IsRuntimeManagedSlot(parameterSlot) == true;
            if (runtimeManaged)
            {
                continue;
            }

            int definitionIndex = _inst.FindIndex(instruction =>
                instruction is IrInst.AndInt { Target: var target } && target == flagTemp);
            if (definitionIndex >= 0)
            {
                _inst[definitionIndex] = new IrInst.LoadConstInt(flagTemp, 0)
                {
                    Location = _inst[definitionIndex].Location,
                };
            }
        }
    }

    private void LowerLambdaCoreRefreshRuntimeManagedTcoResult(TcoContext? tco, int bodyTemp)
    {
        if (tco is null || tco.RuntimeManagedSlotCount == 0)
        {
            return;
        }

        bool[] reachableInstructions = FindReachableInstructions(_inst);
        HashSet<int> managedTemps = SnapshotRuntimeManagedTemps();
        var managedLocals = new HashSet<int>();
        bool changed;
        do
        {
            changed = false;
            for (int instructionIndex = 0; instructionIndex < _inst.Count; instructionIndex++)
            {
                if (!reachableInstructions[instructionIndex])
                {
                    continue;
                }

                IrInst instruction = _inst[instructionIndex];
                changed |= instruction switch
                {
                    IrInst.LoadLocal load
                        when tco.IsRuntimeManagedSlot(load.Slot)
                            || managedLocals.Contains(load.Slot)
                        => managedTemps.Add(load.Target),
                    IrInst.Borrow borrow when managedTemps.Contains(borrow.SourceTemp)
                        => managedTemps.Add(borrow.Target),
                    IrInst.RcDup duplicate when managedTemps.Contains(duplicate.SourceTemp)
                        => managedTemps.Add(duplicate.Target),
                    IrInst.ConcatStr concat when managedTemps.Contains(concat.Left)
                        || managedTemps.Contains(concat.Right)
                        => managedTemps.Add(concat.Target),
                    IrInst.ConcatStrTip concat when managedTemps.Contains(concat.Left)
                        => managedTemps.Add(concat.Target),
                    _ => false,
                };
            }

            foreach (IGrouping<int, IrInst.StoreLocal> stores in _inst
                         .Where((_, instructionIndex) => reachableInstructions[instructionIndex])
                         .OfType<IrInst.StoreLocal>()
                         .GroupBy(store => store.Slot))
            {
                if (stores.All(store => managedTemps.Contains(store.Source)))
                {
                    changed |= managedLocals.Add(stores.Key);
                }
            }
        }
        while (changed);

        PromoteRuntimeManagedStringConcats(managedTemps);

        if (managedTemps.Contains(bodyTemp))
        {
            MarkRuntimeManagedTemp(bodyTemp);
        }
    }

    private void PromoteRuntimeManagedStringConcats(IReadOnlySet<int> managedTemps)
    {
        for (int index = 0; index < _inst.Count; index++)
        {
            if (_inst[index] is IrInst.ConcatStr { RuntimeManaged: false } concat
                && managedTemps.Contains(concat.Target))
            {
                IrInst promoted = concat with { RuntimeManaged = true };
                _inst[index] = promoted;
                ReplaceEmittedTempOwnership(concat, promoted);
            }
            else if (_inst[index] is IrInst.ConcatStrTip { RuntimeManaged: false } concatTip
                && managedTemps.Contains(concatTip.Target))
            {
                IrInst promoted = concatTip with { RuntimeManaged = true };
                _inst[index] = promoted;
                ReplaceEmittedTempOwnership(concatTip, promoted);
            }
        }
    }

    private static bool[] FindReachableInstructions(IReadOnlyList<IrInst> instructions)
    {
        var labels = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int index = 0; index < instructions.Count; index++)
        {
            if (instructions[index] is IrInst.Label label)
            {
                labels[label.Name] = index;
            }
        }

        var reachable = new bool[instructions.Count];
        var pending = new Queue<int>();
        if (instructions.Count > 0)
        {
            pending.Enqueue(0);
        }

        void Enqueue(int index)
        {
            if (index >= 0 && index < instructions.Count && !reachable[index])
            {
                pending.Enqueue(index);
            }
        }

        while (pending.Count > 0)
        {
            int index = pending.Dequeue();
            if (reachable[index])
            {
                continue;
            }

            reachable[index] = true;
            switch (instructions[index])
            {
                case IrInst.Jump jump:
                    Enqueue(labels[jump.Target]);
                    break;
                case IrInst.JumpIfFalse jumpIfFalse:
                    Enqueue(index + 1);
                    Enqueue(labels[jumpIfFalse.Target]);
                    break;
                case IrInst.SwitchTag switchTag:
                    foreach ((long _, string label) in switchTag.Cases)
                    {
                        Enqueue(labels[label]);
                    }
                    Enqueue(labels[switchTag.DefaultLabel]);
                    break;
                case IrInst.Return:
                    break;
                default:
                    Enqueue(index + 1);
                    break;
            }
        }

        return reachable;
    }

    private void RecordTcoParamIdentity(Expr.Lambda lambda, TypeRef parameterType, string label)
    {
        if (_tcoCtx is not { DescendingChain: true } tco)
        {
            return;
        }

        int ordinal = tco.ParamCount - DescribeTcoLambdaChain(lambda).Parameters.Count;
        if (ordinal < 0
            || ordinal >= tco.ParamNames.Count
            || !string.Equals(tco.ParamNames[ordinal], lambda.ParamName, StringComparison.Ordinal))
        {
            return;
        }

        tco.ParamLabels[ordinal] = label;
        tco.ParamTypes[ordinal] = parameterType;
        tco.ParamLocations[ordinal] =
            ResolveSourceLocation(AstSpans.GetLambdaParameterOrDefault(lambda));
    }

    private void LowerLambdaCoreSpliceTcoEntryOwnership(
        List<ReuseEntryCopyCandidate> reuseEntryCopies,
        TcoContext? tco,
        int insertIndex)
    {
        LowerLambdaCoreSpliceReuseCopies(reuseEntryCopies, insertIndex);
        LowerLambdaCoreSpliceRuntimeManagedTcoParams(tco, insertIndex);
    }

    // Curried functions return the next lambda as a closure. This records THAT chain only (consumed
    // solely by IsKnownRuntimeNormalizedFunctionArgument's TCO-argument-normalization question) —
    // result-ownership resolution now goes through GetOwnershipSummary(name).ResultProvenance instead
    // (see LowerLambdaCore's caller of this method for the per-body freshness fact used there).
    private void RecordReturnedClosureLabel(string label, int bodyTemp)
    {
        string? returnedLabel = _inst.LastOrDefault(instruction => instruction switch
        {
            IrInst.MakeClosure { Target: var target } => target == bodyTemp,
            IrInst.MakeClosureStack { Target: var target } => target == bodyTemp,
            _ => false,
        }) switch
        {
            IrInst.MakeClosure closure => closure.FuncLabel,
            IrInst.MakeClosureStack closure => closure.FuncLabel,
            _ => null,
        };
        if (returnedLabel is not null)
        {
            _functionReturnedClosureLabels[label] = returnedLabel;
        }
    }

    // Monomorphize a reuse specialization: bind this curried parameter to the concrete type from
    // the routed call, so the body (and the heap-field key materialization) sees concrete types.
    // Then seed the parameter from the enclosing annotation forms.
    private void LowerLambdaCoreSeedParamType(Expr.Lambda lam, TypeRef paramTy)
    {
        if (_specializationConcreteParamTypes is { } concreteParamTypes
            && _specializationParamCursor < concreteParamTypes.Count)
        {
            Unify(paramTy, concreteParamTypes[_specializationParamCursor]);
            _specializationParamCursor++;
        }

        // Seed this parameter from the enclosing let's type annotation before lowering the body, so an
        // operator on an annotated-Float parameter resolves against Float instead of defaulting to Int.
        if (_annotationParamTypes is { } annotationParamTypes
            && _annotationParamCursor < annotationParamTypes.Count
            && (_annotationTargetLambda is null
                || ReferenceEquals(_annotationTargetLambda, lam)
                || _annotationParamCursor > 0))
        {
            Unify(paramTy, annotationParamTypes[_annotationParamCursor]);
            _annotationParamCursor++;
        }

        // An inline parameter annotation (`given (x: T) ->`, or the lambda a `let f (x: T) = ...`
        // sugar parameter desugars to) pins the parameter's type before the body is lowered.
        if (lam.ParamAnnotation is { } inlineAnnotation)
        {
            Unify(paramTy, ResolveAnnotationType(inlineAnnotation));
        }
    }

    private (HashSet<string> Free, IReadOnlyList<string> Captures, int EnvPtrTemp, Dictionary<int, string> KnownCaptureLabels) LowerLambdaCoreBuildEnv(
        Expr.Lambda lam,
        string? selfName,
        RecursiveGroupContext? recursiveGroup,
        bool stackAllocateClosure,
        LoweredValueRequest request)
    {
        HashSet<string> free = LowerLambdaCoreCollectFreeVariables(lam, selfName);

        // For a mutual-recursion group member, every member shares one identical environment so a
        // sibling's closure can be reconstructed (via Binding.Self) using the current member's env.
        // Otherwise compute this lambda's own captures and build its env. Remove vars that are not in
        // scope (should not happen if earlier checks).
        var captures = recursiveGroup is not null
            ? recursiveGroup.SharedCaptures
            : free.Where(n => Lookup(n) is Binding.Local or Binding.Env or Binding.EnvScheme or Binding.Self or Binding.Scheme).Distinct(StringComparer.Ordinal).ToList();
        var knownCaptureLabels = new Dictionary<int, string>();
        for (int i = 0; i < captures.Count; i++)
        {
            if (TryResolveKnownFunctionLabel(captures[i], out string knownLabel))
            {
                knownCaptureLabels[i] = knownLabel;
            }
        }

        int envPtrTemp = LowerLambdaCoreBuildEnvAllocation(
            captures,
            recursiveGroup,
            stackAllocateClosure,
            request);
        return (free, captures, envPtrTemp, knownCaptureLabels);
    }

    private HashSet<string> LowerLambdaCoreCollectFreeVariables(Expr.Lambda lam, string? selfName)
    {
        var bound = new HashSet<string>(StringComparer.Ordinal) { lam.ParamName };
        if (selfName is not null)
        {
            bound.Add(selfName);
        }

        var free = FreeVars(lam.Body, bound);
        if (selfName is null)
        {
            free.UnionWith(CollectActiveTraitMethodCaptures(lam.Body));
        }
        free.UnionWith(CollectActiveTraitDictionaryOperatorCaptures(lam.Body));
        foreach (string parameter in _activeTraitDictionaryParameters
                     .OrderBy(item => item.Key, StringComparer.Ordinal)
                     .SelectMany(item => item.Value)
                     .Select(active => active.ParameterName))
        {
            if (!bound.Contains(parameter))
            {
                free.Add(parameter);
            }
        }
        ExpandFreshInlinableCaptures(free, bound);
        return free;
    }

    private int LowerLambdaCoreBuildEnvAllocation(
        IReadOnlyList<string> captures,
        RecursiveGroupContext? recursiveGroup,
        bool stackAllocateClosure,
        LoweredValueRequest request)
    {
        int envPtrTemp;
        if (recursiveGroup is not null)
        {
            // The group's shared env was already allocated and filled once at the group site.
            envPtrTemp = recursiveGroup.SharedEnvPtrTemp;
        }
        else if (captures.Count == 0)
        {
            envPtrTemp = NewTemp();
            Emit(new IrInst.LoadConstInt(envPtrTemp, 0)); // null env
        }
        else
        {
            // alloc env: captures.Count * 8
            envPtrTemp = NewTemp();
            if (stackAllocateClosure)
            {
                Emit(new IrInst.AllocStack(envPtrTemp, captures.Count * 8));
            }
            else
            {
                Emit(new IrInst.Alloc(
                    envPtrTemp,
                    captures.Count * 8,
                    request.EmitsRuntime(
                        LoweredValueRuntimeRepresentation.Closure)));
            }

            for (int i = 0; i < captures.Count; i++)
            {
                bool wasSuppressingTraitConstraints = _suppressTraitConstraintCollection;
                _suppressTraitConstraintCollection = true;
                _suppressActiveTraitDictionaryReferenceDepth++;
                (int capTemp, TypeRef capTy) capture;
                try
                {
                    capture = LowerVar(new Expr.Var(captures[i]));
                }
                finally
                {
                    _suppressActiveTraitDictionaryReferenceDepth--;
                    _suppressTraitConstraintCollection = wasSuppressingTraitConstraints;
                }
                // store capTemp into [envPtr + i*8]
                Emit(new IrInst.StoreMemOffset(envPtrTemp, i * 8, capture.capTemp));
                // Constrain types: the captured binding type should match capTy; already does.
            }
        }

        return envPtrTemp;
    }

    private void ExpandFreshInlinableCaptures(HashSet<string> free, IReadOnlySet<string> outerBound)
    {
        foreach (string helperName in free.ToArray())
        {
            if (!_inlinableFunctions.TryGetValue(helperName, out var helper)
                || GetOwnershipSummary(helperName) is not { ResultFresh: true })
            {
                continue;
            }

            var helperBound = new HashSet<string>(helper.Params, StringComparer.Ordinal);
            foreach (string helperFree in FreeVars(helper.Body, helperBound))
            {
                if (!outerBound.Contains(helperFree))
                {
                    free.Add(helperFree);
                }
            }
        }
    }

    // The enclosing function's lowering state snapshotted around a lambda body (restored by
    // LowerLambdaCoreRestoreFrame).
    private sealed record LowerLambdaCoreFrame(
        List<IrInst> Inst,
        int TempSlot,
        int LocalSlot,
        Dictionary<string, Binding>[] Scopes,
        bool InCoroutineBody,
        Dictionary<int, string> LocalNames,
        Dictionary<int, TypeRef> LocalTypes,
        HashSet<string> LinearReuseNames,
        List<ReuseToken> ReuseTokens,
        HashSet<string> SpecAccumulators,
        HashSet<string> ResetSafe,
        HashSet<int> ReuseResultTemps,
        Dictionary<int, LoweredTempOwnershipFact> TempOwnershipFacts,
        Dictionary<int, int> PendingRuntimeArgumentFlags,
        List<PatternBindingPlacementSite> PatternBindingPlacementSites,
        Dictionary<int, string> KnownFunctionLabelsBySlot,
        Dictionary<int, string> KnownFunctionLabelsByEnvIndex,
        Dictionary<int, Expr> LetBindingValues,
        Dictionary<int, BuiltinRegistry.BytesOwnershipProvenance> LocalBytesProvenance);

    private LowerLambdaCoreFrame LowerLambdaCoreSaveFrame(string label, IReadOnlyList<string> captures)
    {
        var savedInst = new List<IrInst>(_inst);
        var savedTemp = _nextTempSlot;
        var savedLocal = _nextLocalSlot;
        var savedScopes = _scopes.ToArray();
        // The enclosing scope of a top-level declaration's lambda has every prior top-level binding
        // (all stdlib helper functions). Snapshot it so a reuse specialization generated later, deep
        // in a loop body, can still resolve those helpers as globals (see the scope build below).
        if (_lambdaDepth == 0)
        {
            _topLevelScopeStack = savedScopes;
            // Record this top-level function's outer-lambda label and whether it captures nothing, so
            // LowerLet can register empty-env functions for by-label calls from reuse specializations.
            _lastLoweredLambdaLabel = label;
            _lastLoweredLambdaEmptyEnv = captures.Count == 0;
            _depth0LambdaCount++;
        }

        _lambdaDepth++;

        // A lambda body is its own function and is NOT run through StateMachineTransform, so an `await`
        // inside it must lower to a blocking RunTask, not a coroutine-split AwaitTask. Only the body of
        // an `async(E)` (lowered via EmitCoroutineBody) is a suspending coroutine. Without this reset,
        // `_inCoroutineBody` leaks from an enclosing async into nested lambdas, emitting AwaitTask into a
        // never-split function — which corrupts heap results across the un-split await (segfault).
        var savedInCoroutineBody = _inCoroutineBody;
        _inCoroutineBody = false;

        var savedLocalNames = new Dictionary<int, string>(_localNames);
        var savedLocalTypes = new Dictionary<int, TypeRef>(_localTypes);
        // In-place reuse state is per-frame: a nested lambda must not see this frame's reuse
        // tokens (frame-local temps) or linear accumulators, and vice versa.
        var savedLinearReuseNames = new HashSet<string>(_linearReuseNames, StringComparer.Ordinal);
        var savedReuseTokens = new List<ReuseToken>(_reuseTokens);
        var savedSpecAccumulators = new HashSet<string>(_linearSpecializationAccumulators, StringComparer.Ordinal);
        var savedResetSafe = new HashSet<string>(_resetSafeAccumulators, StringComparer.Ordinal);
        var savedReuseResultTemps = new HashSet<int>(_reuseResultTemps);
        Dictionary<int, LoweredTempOwnershipFact> savedTempOwnershipFacts =
            SnapshotTempOwnershipFacts();
        var savedPendingRuntimeArgumentFlags = new Dictionary<int, int>(_pendingRuntimeArgumentFlags);
        var savedPatternBindingPlacementSites =
            new List<PatternBindingPlacementSite>(_patternBindingPlacementSites);
        var savedKnownFunctionLabelsBySlot = new Dictionary<int, string>(_knownFunctionLabelsBySlot);
        var savedKnownFunctionLabelsByEnvIndex = new Dictionary<int, string>(_knownFunctionLabelsByEnvIndex);
        var savedLetBindingValues = new Dictionary<int, Expr>(_letBindingValues);
        var savedLocalBytesProvenance =
            new Dictionary<int, BuiltinRegistry.BytesOwnershipProvenance>(
                _localBytesProvenance);
        ClearLambdaFrameState();

        return new LowerLambdaCoreFrame(
            savedInst, savedTemp, savedLocal, savedScopes, savedInCoroutineBody,
            savedLocalNames, savedLocalTypes, savedLinearReuseNames, savedReuseTokens,
            savedSpecAccumulators, savedResetSafe, savedReuseResultTemps,
            savedTempOwnershipFacts, savedPendingRuntimeArgumentFlags,
            savedPatternBindingPlacementSites, savedKnownFunctionLabelsBySlot,
            savedKnownFunctionLabelsByEnvIndex, savedLetBindingValues,
            savedLocalBytesProvenance);
    }

    private void ClearLambdaFrameState()
    {
        _linearReuseNames.Clear();
        _reuseTokens.Clear();
        _linearSpecializationAccumulators.Clear();
        _resetSafeAccumulators.Clear();
        _reuseResultTemps.Clear();
        _tempOwnershipFacts.Clear();
        _pendingRuntimeArgumentFlags.Clear();
        _patternBindingPlacementSites.Clear();
        _knownFunctionLabelsBySlot.Clear();
        _knownFunctionLabelsByEnvIndex.Clear();
        _letBindingValues.Clear();
        _localBytesProvenance.Clear();
    }

    private int LowerLambdaCoreResetFrame()
    {
        // new function state
        _inst.Clear();
        _nextTempSlot = 0;
        _nextLocalSlot = 0;
        _localNames.Clear();
        _localTypes.Clear();

        // Lambda function gets implicit locals for env and arg at slots 0 and 1
        int envSlot = NewLocal(); // 0
        int argSlot = NewLocal(); // 1

        // In function prologue, backend will store RDI(env) to envSlot and RSI(arg) to argSlot.
        // Our LoadEnv instruction implicitly uses envSlot; backend knows envSlot is 0.
        // We'll enforce envSlot==0.
        if (envSlot != 0)
        {
            throw new InvalidOperationException("envSlot must be 0");
        }

        return argSlot;
    }

    // Lambda bodies are lowered as separate functions with a fresh scope. Slot/env bindings are
    // captured elsewhere, but scope-independent bindings must be re-seeded so direct calls to
    // intrinsics, externals, and prelude values still resolve inside helper functions.
    private static void LowerLambdaCoreReseedScopeIndependentBindings(Dictionary<string, Binding> scope, Dictionary<string, Binding>[] enclosingScopes)
    {
        foreach (var enclosingScope in enclosingScopes.Reverse())
        {
            foreach (var (bindingName, binding) in enclosingScope)
            {
                if (binding is Binding.Intrinsic or Binding.ExternalFunction or Binding.PreludeValue)
                {
                    scope[bindingName] = binding;
                }
            }
        }
    }

    private void LowerLambdaCoreBuildScope(Expr.Lambda lam, string label, TypeRef paramTy, int argSlot, HashSet<string> free, IReadOnlyList<string> captures, IReadOnlyDictionary<int, string> knownCaptureLabels, string? selfName, TypeRef? selfType, IReadOnlyList<string>? selfAliases, RecursiveGroupContext? recursiveGroup, Dictionary<string, Binding>[] enclosingScopes)
    {
        // Bind param name as local slot
        var scope = new Dictionary<string, Binding>(StringComparer.Ordinal);
        LowerLambdaCoreReseedScopeIndependentBindings(scope, enclosingScopes);
        LowerLambdaCoreSeedScopeBindings(scope, lam, label, paramTy, argSlot);

        for (int i = 0; i < captures.Count; i++)
        {
            var capBinding = Lookup(captures[i]);
            if (capBinding is null)
            {
                continue;
            }

            scope[captures[i]] = CreateCapturedBinding(capBinding, i);

            if (knownCaptureLabels.TryGetValue(i, out string? knownLabel))
            {
                _knownFunctionLabelsByEnvIndex[i] = knownLabel;
            }
        }
        if (recursiveGroup is not null)
        {
            // Bind every group member (this one and its siblings) so each resolves to its own IR
            // function. Reconstructing a sibling's closure uses this member's env (LoadLocal 0), which
            // is correct precisely because the whole group shares one identical environment layout.
            foreach (var member in recursiveGroup.Members)
            {
                scope[member.Name] = new Binding.Self(
                    member.Label,
                    member.Type,
                    captures.Count * 8,
                    member.Span,
                    member.Requirements);
            }
        }
        else if (selfName is not null && selfType is not null)
        {
            scope[selfName] = new Binding.Self(label, selfType, captures.Count * 8, Lookup(selfName)?.DefinitionSpan);
            if (selfAliases is not null)
            {
                foreach (var alias in selfAliases)
                {
                    scope[alias] = new Binding.Self(label, selfType, captures.Count * 8, Lookup(selfName)?.DefinitionSpan);
                }
            }
        }

        LowerLambdaCoreBindSpecializationGlobals(scope, free);

        _scopes.Clear();
        _scopes.Push(scope);
    }

    private static Binding CreateCapturedBinding(Binding binding, int environmentIndex)
    {
        // A constrained polymorphic binding can cross more than one nested closure boundary.
        // Preserve its complete scheme rather than degrading a later capture to the body type,
        // otherwise applying it in the innermost closure silently loses its evidence.
        return binding switch
        {
            Binding.Scheme scheme => new Binding.EnvScheme(
                environmentIndex,
                scheme.S,
                scheme.DefinitionSpan),
            Binding.EnvScheme scheme => new Binding.EnvScheme(
                environmentIndex,
                scheme.S,
                scheme.DefinitionSpan),
            _ => new Binding.Env(environmentIndex, binding.Type, binding.DefinitionSpan),
        };
    }

    private void LowerLambdaCoreSeedScopeBindings(Dictionary<string, Binding> scope, Expr.Lambda lam, string label, TypeRef paramTy, int argSlot)
    {
        // Re-seed the always-available root-scope intrinsic `async` (like AddStdIOBindings below) so a
        // function body may itself build a task with `async(E)` — e.g. a `serve`/handler combinator.
        // Without this, `async` (an unqualified root-scope binding) is invisible inside any lambda body.
        // Reuse the cached instance so no fresh type var is consumed per lambda.
        if (_asyncBinding is not null)
        {
            scope["async"] = _asyncBinding;
        }
        if (_hasAshesIO)
        {
            AddStdIOBindings(scope);
        }
        var paramSpan = AstSpans.GetLambdaParameterOrDefault(lam);
        RecordHoverType(paramSpan, lam.ParamName, paramTy, isParameter: true);
        scope[lam.ParamName] = new Binding.Local(argSlot, paramTy, paramSpan);
        // Reuse specialization: treat this parameter as a linear reuse root so a match-then-rebuild
        // on it overwrites the node in place. Consume the request so nested lambdas don't inherit it.
        if (string.Equals(_specializingLinearParam, lam.ParamName, StringComparison.Ordinal))
        {
            _linearReuseNames.Add(lam.ParamName);
            _specializingReuseLabel = label;
            _specializingLinearParam = null;
        }
    }

    // Reuse specialization: a stdlib helper this body references (Ashes_Map_makeNode, ...) is a
    // top-level function not present in the generation-site scope (we are deep inside a loop body).
    // Bind each such free reference to its top-level Binding.Self — a direct global reference that
    // needs no env capture (the helper is inlined at its call sites, or called by label). Added to
    // the scope only, never to `captures`, so the closure construction does not try to fill it.
    private void LowerLambdaCoreBindSpecializationGlobals(Dictionary<string, Binding> scope, HashSet<string> free)
    {
        if (!_inSpecialization || _topLevelScopeStack.Length == 0)
        {
            return;
        }

        foreach (var name in free)
        {
            if (scope.ContainsKey(name))
            {
                continue;
            }

            foreach (var topScope in _topLevelScopeStack)
            {
                if (topScope.TryGetValue(name, out var topBinding) && topBinding is Binding.Self)
                {
                    scope[name] = topBinding;
                    break;
                }
            }
        }
    }

    // TCO: For the innermost lambda in a recursive chain, create local copies of captured params
    // and emit a loop start label so tail self-calls can jump back.
    // A lambda only belongs to the recursive chain while we are still descending the binding's
    // curried lambda chain. A nested let-bound lambda inside the body (e.g.
    // `let rec f n = let helper x = x + n in ...`) is a separate frame: if treated as the
    // innermost chain lambda it would emit the loop label into its own frame while the outer
    // self-call jumps to a label that frame never contains (KeyNotFoundException in codegen).
    // Returns reuseInsertIndex — the instruction index (before the loop body label) where the
    // one-time defensive deep copies are spliced in after the body is lowered.
    private int LowerLambdaCoreEnterTcoLoop(
        Expr.Lambda lam,
        string label,
        IReadOnlyList<string> captures,
        List<ReuseEntryCopyCandidate> reuseEntryCopies,
        HashSet<string> specElidedAccs)
    {
        var tco = _tcoCtx!;
        var scope = _scopes.Peek();
        tco.ParamSlots.Clear();

        LowerLambdaCoreBindTcoParamSlots(lam, captures, scope, tco);
        LowerLambdaCoreIdentifyRuntimeManagedTcoParams(
            scope,
            tco,
            TcoPlacementResolutionPoint.ProvisionalLoopEntry,
            includeFreshClosures: true);

        var reuseParamNames = new HashSet<string>(tco.ParamNames, StringComparer.Ordinal) { lam.ParamName };
        int reuseInsertIndex = LowerLambdaCoreScanDirectReuse(
            lam,
            tco,
            reuseParamNames,
            reuseEntryCopies,
            specElidedAccs);
        LowerLambdaCoreScanSpecializationReuse(
            lam,
            tco,
            reuseParamNames,
            reuseEntryCopies,
            specElidedAccs);
        LowerLambdaCoreEmitTcoLoopEntry(label, tco);

        tco.InTailPosition = true;
        return reuseInsertIndex;
    }

    private void LowerLambdaCoreIdentifyRuntimeManagedTcoParams(
        IReadOnlyDictionary<string, Binding> scope,
        TcoContext tco,
        TcoPlacementResolutionPoint resolutionPoint,
        bool includeFreshClosures)
    {
        EvaluateTcoParamPlacements(
            tco,
            ResolveTcoParamTypes(scope, tco),
            resolutionPoint,
            includeFreshClosures,
            applyReuseRestrictions: false);
    }

    /// <summary>
    /// Canonical scalar/tuple/ADT layout predicate shared by placement and classifier B.
    /// </summary>
    private bool IsRcEligibleScalarTupleOrAdtType(TypeRef type)
        => type is TypeRef.TStr or TypeRef.TBigInt
            || type is TypeRef.TTuple tuple && CanRuntimeManageOwnedTupleType(tuple)
            || type is TypeRef.TNamedType named
                && (CanCopyOutAdt(named, out _) || CanRuntimeManageTcoAdt(named));

    private void LowerLambdaCoreBindTcoParamSlots(Expr.Lambda lam, IReadOnlyList<string> captures, Dictionary<string, Binding> scope, TcoContext tco)
    {
        // Only create mutable local copies for captured params that are PART OF
        // the recursive function's lambda chain (not arbitrary outer captures).
        var tcoParamNames = new HashSet<string>(tco.ParamNames, StringComparer.Ordinal);
        tcoParamNames.Remove(lam.ParamName); // the current param is already in argSlot

        for (int i = 0; i < captures.Count; i++)
        {
            var capName = captures[i];
            if (!tcoParamNames.Contains(capName))
            {
                continue;
            }

            var envIdx = -1;
            foreach (var (name, binding) in scope)
            {
                if (string.Equals(name, capName, StringComparison.Ordinal) && binding is Binding.Env env)
                {
                    envIdx = env.Index;
                    break;
                }
            }
            if (envIdx >= 0)
            {
                var localSlot = NewLocal();
                // Load from env into local at function start
                int loadTemp = NewTemp();
                Emit(new IrInst.LoadEnv(loadTemp, envIdx));
                Emit(new IrInst.StoreLocal(localSlot, loadTemp));
                RecordLocalDebugInfo(localSlot, capName, scope[capName].Type);
                // Override binding to use local slot
                scope[capName] = new Binding.Local(localSlot, scope[capName].Type, scope[capName].DefinitionSpan);
            }
        }

        LowerLambdaCoreBuildTcoParamSlots(scope, tco);
        tco.BuildParamStaticFacts();
    }

    private void LowerLambdaCoreBuildTcoParamSlots(
        IReadOnlyDictionary<string, Binding> scope,
        TcoContext tco)
    {
        // Build in declaration/application order, not capture-discovery order. The innermost scope
        // exposes the last binding for a duplicated source name; join that binding to its ordinal
        // exactly once and retain distinct synthetic slots for shadowed, unobservable binders.
        for (int ordinal = 0; ordinal < tco.ParamNames.Count; ordinal++)
        {
            string parameterName = tco.ParamNames[ordinal];
            bool visibleBinding = tco.IsVisibleParameterOrdinal(ordinal);
            if (visibleBinding
                && scope.TryGetValue(parameterName, out Binding? parameterBinding)
                && parameterBinding is Binding.Local parameterLocal)
            {
                tco.ParamSlots.Add(parameterLocal.Slot);
            }
            else if (!visibleBinding)
            {
                int hiddenSlot = NewLocal();
                tco.ParamSlots.Add(hiddenSlot);
                int emptyValue = NewTemp();
                Emit(new IrInst.LoadConstInt(emptyValue, 0));
                Emit(new IrInst.StoreLocal(hiddenSlot, emptyValue));
                if (tco.ParamTypes.TryGetValue(ordinal, out TypeRef? hiddenType))
                {
                    RecordLocalDebugInfo(hiddenSlot, parameterName, hiddenType);
                }
            }
            else
            {
                throw new InvalidOperationException(
                    $"TCO parameter {ordinal} ('{parameterName}') has no local slot for the back-edge.");
            }
        }
    }

    // In-place reuse: mark accumulators that are deconstructed in the loop body as
    // linear (so the body's match→construct lowering reuses their nodes in place) and record
    // them for a one-time deep copy at loop entry. The copy makes the loop-local accumulator
    // region uniquely owned regardless of whether the caller still holds the initial value —
    // which is what makes the per-iteration in-place reuse sound (no runtime refcounting;
    // Ground Rule 6). The copy IR is generated after the body (resolved types) and spliced in
    // here. Type comes from the matched constructor — the param's own type var isn't unified
    // until the body is lowered.
    private sealed record ReuseEntryCopyCandidate(
        IrFunctionOrigin Function,
        int Slot,
        TypeRef Type,
        string Parameter,
        ReuseDecisionMechanism Mechanism,
        bool OwnershipProvesUnique,
        ParameterMoveSafetyCause MoveSafetyCauses,
        SourceLocation? Location);

    private ReuseEntryCopyCandidate CreateReuseEntryCopyCandidate(
        TcoContext tco,
        int slot,
        TypeRef type,
        string parameter,
        ReuseDecisionMechanism mechanism,
        bool ownershipProvesUnique,
        ParameterMoveSafetyCause moveSafetyCauses)
    {
        int parameterOrdinal = tco.ParamSlots.IndexOf(slot);
        SourceLocation? location = parameterOrdinal >= 0
            && tco.ParamLocations.TryGetValue(
                parameterOrdinal,
                out SourceLocation? parameterLocation)
                ? parameterLocation
                : null;
        return new ReuseEntryCopyCandidate(
            _activeFunctionOrigin
                ?? throw new InvalidOperationException(
                    "A reuse entry-copy candidate must belong to an active function."),
            slot,
            type,
            parameter,
            mechanism,
            ownershipProvesUnique,
            moveSafetyCauses,
            location);
    }

    private void RecordReuseEntryCopyDecision(
        ReuseEntryCopyCandidate candidate,
        ReuseDecisionOutcome outcome,
        ReuseDecisionReason reason)
    {
        _reuseDecisions.Add(
            new ReuseDecision(
                candidate.Function,
                ReuseDecisionKind.EntryCopy,
                candidate.Mechanism,
                outcome,
                reason,
                new ReuseDecisionCandidate(
                    ReuseCandidateKind.Parameter,
                    candidate.Parameter,
                    LocalSlot: candidate.Slot),
                RelatedGeneratedLabel: null,
                candidate.Location,
                candidate.MoveSafetyCauses));
    }

    private int LowerLambdaCoreScanDirectReuse(
        Expr.Lambda lam,
        TcoContext tco,
        HashSet<string> reuseParamNames,
        List<ReuseEntryCopyCandidate> reuseEntryCopies,
        HashSet<string> specElidedAccs)
    {
        _linearReuseNames.Clear();
        var reuseScan = new Dictionary<string, string>(StringComparer.Ordinal);
        CollectCtorMatchedScrutinees(lam.Body, reuseParamNames, reuseScan);
        int reuseInsertIndex = _inst.Count;
        foreach (var (accName, ctorName) in reuseScan)
        {
            if (_scopes.Peek().TryGetValue(accName, out var accBinding)
                && accBinding is Binding.Local accLocal
                && !tco.IsRuntimeManagedSlot(accLocal.Slot)
                && _constructorSymbols.TryGetValue(ctorName, out var accCtor)
                && Prune(InstantiateAdtType(accCtor)) is TypeRef.TNamedType accNamed
                && !BuiltinRegistry.IsResourceTypeName(accNamed.Symbol.Name)
                && !IsResourceBearing(accNamed)
                // Only pointer-bearing/recursive ADTs benefit: copy-type ADTs are already bounded
                // by the existing shallow copy-out, so reuse there is redundant and the entry deep
                // copy would be wasted.
                && !CanCopyOutAdt(accNamed, out _)
                && TrySynthesizeAdtCopier(accNamed) is not null)
            {
                _linearReuseNames.Add(accName);

                // Move/linearity elision (CO-2), symmetric to the specialization path below: the
                // direct-reuse entry deep-copy exists only to make the accumulator uniquely owned
                // so the loop body may overwrite its matched cells in place. When the whole-program
                // move analysis proves the accumulator is already uniquely owned at every external
                // call site of this fold, the copy is redundant (and, when the fold is called from
                // an outer loop, re-executes per re-entry). Skip it only when provably safe; the
                // conservative default keeps the copy. The slot is still tracked in
                // decision candidate so the non-structural-reuse revert below still governs it — a
                // move-safe *pure reader* (nullary-only reuse, result type ≠ accumulator) must
                // still fall back to a fresh allocation so its returned cell is not a reused
                // accumulator cell. When the reuse is structural, the AllocReusing fires in place
                // against the already-unique accumulator with no copy — the actual win.
                ParameterMoveSafetyCause moveSafetyCauses =
                    ParameterMoveSafetyCause.ConservativeUnknown;
                bool elideDirect = tco.SelfName.Length > 0
                    && ReuseAccumulatorIsUnique(
                        tco.OwnershipFunction,
                        tco.SelfName,
                        accName,
                        out moveSafetyCauses);
                reuseEntryCopies.Add(
                    CreateReuseEntryCopyCandidate(
                        tco,
                        accLocal.Slot,
                        accLocal.T,
                        accName,
                        ReuseDecisionMechanism.DirectInPlace,
                        elideDirect,
                        moveSafetyCauses));
                if (elideDirect)
                {
                    specElidedAccs.Add(accName);
                }
            }
        }

        return reuseInsertIndex;
    }

    // Indirect reuse: an accumulator passed to a specializable recursive function f(acc) is
    // also deep-copied once here (so f$reuse can rewrite it in place) and tracked so the call
    // is routed to f$reuse. Eligibility from f's parameter type (a non-resource recursive ADT).
    private void LowerLambdaCoreScanSpecializationReuse(
        Expr.Lambda lam,
        TcoContext tco,
        HashSet<string> reuseParamNames,
        List<ReuseEntryCopyCandidate> reuseEntryCopies,
        HashSet<string> specElidedAccs)
    {
        _linearSpecializationAccumulators.Clear();
        var specScan = new Dictionary<string, string>(StringComparer.Ordinal);
        CollectSpecializableCallArgs(lam.Body, reuseParamNames, specScan);
        foreach (var (accName, funcName) in specScan)
        {
            if (_linearReuseNames.Contains(accName) || _linearSpecializationAccumulators.Contains(accName))
            {
                continue;
            }

            if (_scopes.Peek().TryGetValue(accName, out var accB)
                && accB is Binding.Local accL
                && !tco.IsRuntimeManagedSlot(accL.Slot)
                && Lookup(funcName) is { } funcBinding
                && _specializableFunctions.TryGetValue(funcName, out var funcSpec)
                && IsReusableSpecializationAccumulatorType(
                    NthCurriedArgType(Prune(funcBinding.Type), funcSpec.ArgCount - 1)))
            {
                _linearSpecializationAccumulators.Add(accName);

                // Move/linearity elision: the entry deep-copy exists only to make the
                // accumulator uniquely owned so f$reuse may overwrite it in place. When the
                // whole-program move analysis proves the accumulator is already uniquely owned
                // at every external call site of this fold (moved, unaliased, seeded from a
                // never-overwritable value), the copy is redundant and re-executes on every
                // re-entry (the nested-reuse leak). Skip it only when provably safe; the
                // conservative default keeps the copy.
                ParameterMoveSafetyCause moveSafetyCauses =
                    ParameterMoveSafetyCause.ConservativeUnknown;
                bool elide = tco.SelfName.Length > 0
                    && ReuseAccumulatorIsUnique(
                        tco.OwnershipFunction,
                        tco.SelfName,
                        accName,
                        out moveSafetyCauses);
                reuseEntryCopies.Add(
                    CreateReuseEntryCopyCandidate(
                        tco,
                        accL.Slot,
                        accL.T,
                        accName,
                        ReuseDecisionMechanism.Specialization,
                        elide,
                        moveSafetyCauses));
                if (elide)
                {
                    specElidedAccs.Add(accName);
                }
            }
        }
    }

    private bool IsReusableSpecializationAccumulatorType(TypeRef? type)
    {
        TypeRef? accumulator = type is null ? null : Prune(type);
        return accumulator switch
        {
            TypeRef.TList list => IsDeepCopyOutSafeType(Prune(list.Element)),
            TypeRef.TNamedType named => !BuiltinRegistry.IsResourceTypeName(named.Symbol.Name)
                && !IsResourceBearing(named)
                && !CanCopyOutAdt(named, out _)
                && TrySynthesizeAdtCopier(named) is not null,
            _ => false,
        };
    }

    private void LowerLambdaCoreEmitTcoLoopEntry(string label, TcoContext tco)
    {
        // Save a FIXED loop-entry watermark BEFORE the loop label (runs once). A back-edge whose
        // accumulators are all non-sharing whole-value types resets here instead of the per-iteration
        // mark, so the previous iteration's whole-value copy is reclaimed rather than stranded below
        // an advancing watermark (the growing-accumulator O(N^2) leak). Same cursor position as the
        // first per-iteration save below (nothing is emitted between them).
        tco.FixedCursorSlot = NewLocal();
        tco.FixedEndSlot = NewLocal();
        Emit(new IrInst.SaveArenaState(tco.FixedCursorSlot, tco.FixedEndSlot));
        // Live-size slot for the amortized fixed-watermark compaction (see TcoContext), starts 0
        // so the first qualifying back-edge compacts and records the true live size.
        tco.CompactionSizeSlot = NewLocal();
        int compactionZero = NewTemp();
        Emit(new IrInst.LoadConstInt(compactionZero, 0));
        Emit(new IrInst.StoreLocal(tco.CompactionSizeSlot, compactionZero));

        // Reservation slots for the affine string accumulators (see ConcatStrTip): start/end,
        // zeroed here so no string matches until the loop's first fallback reserves.
        foreach (int affineParamSlot in tco.AffineSelfAppendParamSlots)
        {
            int resvStart = NewLocal();
            int resvEnd = NewLocal();
            Emit(new IrInst.StoreLocal(resvStart, compactionZero));
            Emit(new IrInst.StoreLocal(resvEnd, compactionZero));
            tco.AffineResvSlots[affineParamSlot] = (resvStart, resvEnd);
        }

        foreach (int closureSlot in tco.RuntimeManagedClosureSlotsInOrder)
        {
            int activeSlot = NewLocal();
            Emit(new IrInst.StoreLocal(activeSlot, compactionZero));
            tco.RuntimeManagedClosureActiveSlots[closureSlot] = activeSlot;
        }

        foreach (int slot in tco.RuntimeManagedSlotsInOrder)
        {
            if (!tco.IsRuntimeManagedClosureSlot(slot))
            {
                tco.RuntimeManagedParamActiveSlots[slot] = NewLocal();
            }
        }

        // Emit loop start label
        tco.BodyLabel = $"{label}_body";
        Emit(new IrInst.Label(tco.BodyLabel));

        // Save arena watermark at loop body start so per-iteration heap
        // allocations can be reclaimed before jumping back to the next iteration.
        tco.ArenaCursorSlot = NewLocal();
        tco.ArenaEndSlot = NewLocal();
        Emit(new IrInst.SaveArenaState(tco.ArenaCursorSlot, tco.ArenaEndSlot));
        // Save the stack pointer too: dynamic stack allocations in the loop body (per-iteration string /
        // syscall scratch) must be freed at each back-edge, or they accumulate across iterations and
        // overflow the stack at scale. Restored in the back-edge alongside the arena reset.
        tco.StackPtrSlot = NewLocal();
        Emit(new IrInst.SaveStackPointer(tco.StackPtrSlot));
        tco.OwnershipDepthAtEntry = _ownershipScopes.Count;
    }

    // Decide what TCO context the body sees:
    //  - a chain link whose body is the next curried lambda keeps descending,
    //  - the chain's innermost lambda stops descending so nested lambdas in the body don't
    //    re-trigger TCO,
    //  - a non-chain nested lambda suspends the outer TCO entirely (it is a separate frame, and
    //    tail-call back-edges can't cross frames).
    private TcoContext? LowerLambdaCoreSuspendOuterTco(bool isChainLambda, Expr.Lambda lam)
    {
        var outerTcoCtx = _tcoCtx;
        if (isChainLambda)
        {
            _tcoCtx!.DescendingChain = TryGetTcoLambdaContinuation(lam.Body, out _);
        }
        else if (_tcoCtx is not null)
        {
            _tcoCtx = null;
        }

        return outerTcoCtx;
    }

    private (int Temp, TypeRef Type) LowerLambdaCoreLowerBody(Expr.Lambda lam, TypeRef rowTy, string? selfName)
    {
        bool paramShadowsInlinable = PushInlinableShadow(lam.ParamName);
        // If this lambda parameter is a capability op-parameter, mark it active so a call at a
        // still-abstract instance inside the body threads it.
        var opParamScope = EnterOpParamScope(lam.ParamName);
        IReadOnlyList<string>? traitDictionaryScope = EnterTraitDictionaryParameterScope(lam.ParamName);
        bool pushedDictShadow = PushDictFnShadow(lam.ParamName, selfName);
        var savedAmbientRow = _ambientRow;
        _ambientRow = rowTy;
        var (bodyTemp, bodyType) = AllowsAsyncIndependentRcPlacement && AllowsOrdinaryRcPlacement
            ? LowerEscapingResult(lam.Body, normalizeStaticString: true)
            : LowerExpr(lam.Body).AsPair();
        _ambientRow = savedAmbientRow;
        PopDictFnShadow(lam.ParamName, pushedDictShadow);
        ExitTraitDictionaryParameterScope(traitDictionaryScope);
        ExitOpParamScope(opParamScope);
        if (paramShadowsInlinable) PopInlinableShadow(lam.ParamName);
        return (bodyTemp, bodyType);
    }

    // In-place reuse: now that the body is lowered and the accumulators' types are
    // resolved, generate the one-time defensive deep copies and splice them in at loop entry
    // (before the body label, recorded as reuseInsertIndex). Generated at the end of _inst, then
    // moved up — the block is self-contained (loads the slot, deep-copies, stores it back).
    // Run for retained and elided candidates alike: a direct-reuse candidate whose copy was elided
    // still needs the non-structural-reuse revert below to protect a move-safe pure reader.
    private void LowerLambdaCoreSpliceReuseCopies(
        List<ReuseEntryCopyCandidate> reuseEntryCopies,
        int reuseInsertIndex)
    {
        if (reuseEntryCopies.Count == 0 || reuseInsertIndex < 0)
        {
            return;
        }

        // A direct-reuse defensive copy (O(size)) is only worth it if reuse rebuilds the
        // accumulator's recursive *structure* — i.e. an AllocReusing with fields. A function that
        // matches the accumulator but only reads it — a tree lookup like Map.get, whose arms
        // return a different type (None/Some) — at most reuses a dead nullary leaf (Lf -> None),
        // an O(1) saving that does not justify the O(size) copy. Without a non-nullary rebuild,
        // copying the recursive argument turns an O(depth) traversal into an O(size) deep copy per
        // call (the 1BRC get/set O(N·K) leak). So: keep the copy only when a field-bearing
        // AllocReusing fired; otherwise skip the direct-reuse copies AND revert this body's
        // (now unbacked-by-a-copy, hence unsound) nullary reuses to fresh allocations.
        // Specialization copies (reuse lives in a $reuse clone) are unaffected.
        bool structuralReuse = PrepareDirectReuseBody(reuseInsertIndex);

        int genStart = _inst.Count;
        foreach (ReuseEntryCopyCandidate candidate in reuseEntryCopies)
        {
            if (candidate.Mechanism == ReuseDecisionMechanism.DirectInPlace
                && !structuralReuse)
            {
                RecordReuseEntryCopyDecision(
                    candidate,
                    ReuseDecisionOutcome.Omitted,
                    ReuseDecisionReason.NoStructuralReuse);
                continue;
            }

            if (candidate.OwnershipProvesUnique)
            {
                RecordReuseEntryCopyDecision(
                    candidate,
                    ReuseDecisionOutcome.Elided,
                    ReuseDecisionReason.OwnershipMoveSafe);
                continue;
            }

            int loaded = NewTemp();
            Emit(new IrInst.LoadLocal(loaded, candidate.Slot));
            int copied = EmitDeepCopy(loaded, Prune(candidate.Type));
            Emit(new IrInst.StoreLocal(candidate.Slot, copied));
            RecordReuseEntryCopyDecision(
                candidate,
                ReuseDecisionOutcome.Retained,
                ReuseDecisionReason.OwnershipMoveSafetyRejected);
        }

        int genCount = _inst.Count - genStart;
        var generated = _inst.GetRange(genStart, genCount);
        _inst.RemoveRange(genStart, genCount);
        _inst.InsertRange(reuseInsertIndex, generated);
    }

    private bool PrepareDirectReuseBody(int reuseInsertIndex)
    {
        bool structuralReuse = false;
        for (int i = reuseInsertIndex; i < _inst.Count; i++)
        {
            if (_inst[i] is IrInst.AllocReusing { FieldCount: > 0 })
            {
                structuralReuse = true;
                break;
            }
        }

        if (structuralReuse)
        {
            return true;
        }

        for (int i = reuseInsertIndex; i < _inst.Count; i++)
        {
            if (_inst[i] is IrInst.AllocReusing allocation)
            {
                ReclassifyRevertedReuseAllocation(allocation);
                IrInst replacement = new IrInst.AllocAdt(
                    allocation.Target,
                    allocation.Tag,
                    allocation.FieldCount)
                {
                    Location = allocation.Location
                };
                _inst[i] = replacement;
                ReplaceEmittedTempOwnership(allocation, replacement);
            }
        }

        return false;
    }

    private void ReclassifyRevertedReuseAllocation(IrInst.AllocReusing allocation)
    {
        int dispositionIndex = _reuseDecisions.FindLastIndex(decision =>
            decision.Decision == ReuseDecisionKind.TokenDisposition
            && decision.Outcome == ReuseDecisionOutcome.Consumed
            && decision.TokenLifecycle?.AllocationTemp == allocation.Target
            && Equals(decision.Function, _activeFunctionOrigin));
        if (dispositionIndex < 0)
        {
            return;
        }

        ReuseDecision consumed = _reuseDecisions[dispositionIndex];
        _reuseDecisions[dispositionIndex] = consumed with
        {
            Outcome = ReuseDecisionOutcome.Discarded,
            Reason = ReuseDecisionReason.NoStructuralReuse,
        };
        _reuseDecisions.RemoveAll(decision =>
            decision.Decision == ReuseDecisionKind.FallbackAllocation
            && decision.Outcome == ReuseDecisionOutcome.Available
            && decision.TokenLifecycle?.AllocationTemp == allocation.Target
            && Equals(decision.Function, _activeFunctionOrigin));
        _reuseDecisions.Add(
            consumed with
            {
                Decision = ReuseDecisionKind.FallbackAllocation,
                Outcome = ReuseDecisionOutcome.Allocated,
                Reason = ReuseDecisionReason.NoStructuralReuse,
                Location = allocation.Location ?? consumed.Location,
                TokenLifecycle = consumed.TokenLifecycle! with
                {
                    FallbackKind = ReuseFallbackAllocationKind.Arena,
                },
            });
    }

    private void LowerLambdaCoreSpliceRuntimeManagedTcoParams(TcoContext? tco, int insertIndex)
    {
        if (tco is null || insertIndex < 0 || tco.RuntimeManagedSlotCount == 0)
        {
            return;
        }

        int generatedStart = _inst.Count;
        foreach (int slot in tco.RuntimeManagedSlotsInOrder)
        {
            RecordRuntimeNormalizedTcoParamLabel(tco, slot);
            if (tco.IsRuntimeManagedClosureSlot(slot))
            {
                if (tco.RuntimeManagedClosureSlotsNeedingEntryInitialization.Remove(slot)
                    && tco.RuntimeManagedClosureActiveSlots.TryGetValue(slot, out int activeSlot))
                {
                    int inactiveTemp = NewTemp();
                    Emit(new IrInst.LoadConstInt(inactiveTemp, 0));
                    Emit(new IrInst.StoreLocal(activeSlot, inactiveTemp));
                }
                continue;
            }
            int sourceTemp = NewTemp();
            Emit(new IrInst.LoadLocal(sourceTemp, slot));
            int normalizedTemp = slot == 1
                ? EmitRuntimeManagedTcoArgumentNormalization(sourceTemp, tco.GetRuntimeManagedType(slot))
                : EmitRuntimeManagedTcoParamCopy(sourceTemp, tco.GetRuntimeManagedType(slot));
            MarkRuntimeManagedTemp(
                normalizedTemp,
                LoweredTempOwnershipReason.TcoParameterInstall,
                sourceTemp,
                tco.GetRuntimeManagedType(slot));
            Emit(new IrInst.StoreLocal(slot, normalizedTemp));
            int activeTemp = NewTemp();
            Emit(new IrInst.LoadConstInt(activeTemp, 1));
            Emit(new IrInst.StoreLocal(tco.RuntimeManagedParamActiveSlots[slot], activeTemp));
        }

        int generatedCount = _inst.Count - generatedStart;
        List<IrInst> generated = _inst.GetRange(generatedStart, generatedCount);
        _inst.RemoveRange(generatedStart, generatedCount);
        _inst.InsertRange(insertIndex, generated);
    }

    private int EmitRuntimeManagedTcoArgumentNormalization(int sourceTemp, TypeRef type)
    {
        int resultSlot = NewLocal();
        int ownershipTemp = NewTemp();
        Emit(new IrInst.LoadArgumentOwnership(ownershipTemp));
        string copyLabel = NewLabel("rc_arg_normalize_copy");
        string doneLabel = NewLabel("rc_arg_normalize_done");
        Emit(new IrInst.JumpIfFalse(ownershipTemp, copyLabel));
        Emit(new IrInst.StoreLocal(resultSlot, sourceTemp));
        Emit(new IrInst.Jump(doneLabel));
        Emit(new IrInst.Label(copyLabel));
        int copiedTemp = EmitRuntimeManagedTcoParamCopy(sourceTemp, type);
        Emit(new IrInst.StoreLocal(resultSlot, copiedTemp));
        Emit(new IrInst.Label(doneLabel));
        int resultTemp = NewTemp();
        Emit(new IrInst.LoadLocal(resultTemp, resultSlot));
        return resultTemp;
    }

    private int EmitRuntimeManagedTcoParamCopy(int sourceTemp, TypeRef type)
    {
        int normalizedTemp = NewTemp();
        if (type is TypeRef.TList list)
        {
            if (TryGetRuntimeManagedListHeadCopy(list.Element, out IrInst.ListHeadCopyKind headCopy))
            {
                Emit(new IrInst.CopyOutList(
                    normalizedTemp, sourceTemp, headCopy, RuntimeManaged: true,
                    IrInst.CopyOutPurpose.RcNormalization));
            }
            else
            {
                normalizedTemp = EmitRuntimeManagedTcoListDeepCopy(sourceTemp, list.Element);
            }
        }
        else if (type is TypeRef.TTuple tuple
            && !tuple.Elements.All(element => CanArenaReset(Prune(element))))
        {
            normalizedTemp = EmitRuntimeManagedTcoDeepCopy(sourceTemp, tuple);
        }
        else if (type is TypeRef.TNamedType named
            && !CanCopyOutAdt(named, out _))
        {
            normalizedTemp = EmitRuntimeManagedTcoDeepCopy(sourceTemp, named);
        }
        else
        {
            int copySize = TcoRuntimeManagedCopySize(type);
            Emit(new IrInst.CopyOutArena(
                normalizedTemp, sourceTemp, copySize, RuntimeManaged: true,
                IrInst.CopyOutPurpose.RcNormalization));
        }
        return normalizedTemp;
    }

    private void RecordRuntimeNormalizedTcoParamLabel(TcoContext tco, int slot)
    {
        // Only the lifted lambda's direct argument (local slot 1) can observe the hidden
        // ownership flag. Earlier arguments in a curried chain have already been captured in an
        // environment by the time the innermost TCO body normalizes them.
        if (slot != 1)
        {
            return;
        }

        int paramIndex = tco.ParamSlots.IndexOf(slot);
        if (paramIndex >= 0
            && tco.ParamLabels.TryGetValue(paramIndex, out string? paramLabel))
        {
            _runtimeNormalizedFunctionArgumentLabels.Add(paramLabel);
        }
    }

    private void LowerLambdaCoreEmitRuntimeManagedTcoExitDrops(TcoContext? tco, int bodyTemp)
    {
        if (tco is null)
        {
            return;
        }

        int transferSelectedSlot = -1;
        int zeroTemp = -1;
        if (IsRuntimeManagedResultTemp(bodyTemp))
        {
            transferSelectedSlot = NewLocal();
            zeroTemp = NewTemp();
            Emit(new IrInst.LoadConstInt(zeroTemp, 0));
            Emit(new IrInst.StoreLocal(transferSelectedSlot, zeroTemp));
        }

        foreach (int slot in tco.RuntimeManagedSlotsInOrder)
        {
            if (!tco.TryGetRuntimeManagedActiveSlot(slot, out int activeSlot))
            {
                continue;
            }
            int sourceTemp = NewTemp();
            Emit(new IrInst.LoadLocal(sourceTemp, slot));
            if (transferSelectedSlot < 0)
            {
                EmitRuntimeManagedTcoExitParamDrop(tco, slot, sourceTemp, activeSlot);
                continue;
            }

            int activeTemp = NewTemp();
            Emit(new IrInst.LoadLocal(activeTemp, activeSlot));
            int matchesResultTemp = NewTemp();
            Emit(new IrInst.CmpIntEq(matchesResultTemp, sourceTemp, bodyTemp));
            int transferSelectedTemp = NewTemp();
            Emit(new IrInst.LoadLocal(transferSelectedTemp, transferSelectedSlot));
            int transferAvailableTemp = NewTemp();
            Emit(new IrInst.CmpIntEq(transferAvailableTemp, transferSelectedTemp, zeroTemp));
            int activeMatchTemp = NewTemp();
            Emit(new IrInst.AndInt(activeMatchTemp, activeTemp, matchesResultTemp));
            int canTransferTemp = NewTemp();
            Emit(new IrInst.AndInt(canTransferTemp, activeMatchTemp, transferAvailableTemp));
            string dropLabel = NewLabel("rc_tco_exit_transfer_not_selected");
            string doneLabel = NewLabel("rc_tco_exit_transfer_done");
            Emit(new IrInst.JumpIfFalse(canTransferTemp, dropLabel));
            int oneTemp = NewTemp();
            Emit(new IrInst.LoadConstInt(oneTemp, 1));
            Emit(new IrInst.StoreLocal(transferSelectedSlot, oneTemp));
            Emit(new IrInst.Jump(doneLabel));
            Emit(new IrInst.Label(dropLabel));
            EmitRuntimeManagedTcoExitParamDrop(tco, slot, sourceTemp, activeSlot);
            Emit(new IrInst.Label(doneLabel));
        }
    }

    private void EmitRuntimeManagedTcoExitParamDrop(
        TcoContext tco,
        int slot,
        int sourceTemp,
        int activeSlot)
    {
        if (tco.IsRuntimeManagedClosureSlot(slot))
        {
            EmitRuntimeManagedClosureDropIfActive(sourceTemp, activeSlot);
        }
        else
        {
            EmitRuntimeManagedTcoParamDropIfActive(tco, slot, sourceTemp, activeSlot);
        }
    }

    private void EmitRuntimeManagedTcoParamDropIfActive(
        TcoContext tco,
        int slot,
        int sourceTemp,
        int activeSlot)
    {
        int activeTemp = NewTemp();
        string skipLabel = NewLabel("rc_tco_exit_drop_inactive");
        Emit(new IrInst.LoadLocal(activeTemp, activeSlot));
        Emit(new IrInst.JumpIfFalse(activeTemp, skipLabel));
        TypeRef type = tco.GetRuntimeManagedType(slot);
        if (tco.IsRuntimeManagedListSlot(slot) && type is TypeRef.TList list)
        {
            EmitRuntimeManagedListDrop(sourceTemp, list.Element);
        }
        else if (type is TypeRef.TTuple tuple)
        {
            EmitRuntimeManagedTupleDrop(sourceTemp, tuple);
        }
        else if (type is TypeRef.TNamedType named)
        {
            EmitRuntimeManagedAdtDrop(sourceTemp, named);
        }
        else
        {
            Emit(new IrInst.RcDrop(
                sourceTemp,
                TcoRuntimeManagedTypeName(type),
                OwnerSlot: -1,
                RuntimeManaged: true));
        }
        Emit(new IrInst.Label(skipLabel));
    }

    private void EmitRuntimeManagedClosureDropIfActive(int closureTemp, int activeSlot)
    {
        int activeTemp = NewTemp();
        string skipLabel = NewLabel("rc_closure_drop_inactive");
        Emit(new IrInst.LoadLocal(activeTemp, activeSlot));
        Emit(new IrInst.JumpIfFalse(activeTemp, skipLabel));
        Emit(new IrInst.CleanupResource(closureTemp, "Function"));
        Emit(new IrInst.RcDrop(closureTemp, "Function", RuntimeManaged: true));
        Emit(new IrInst.Label(skipLabel));
    }

    // Address-stable-fold recording: with the body lowered (its in-place reuse calls now recorded),
    // decide whether calling this fold returns its accumulator at a stable address, so a caller
    // threading it across a loop back-edge can keep the plain arena reset. The accumulator is the
    // last curried param; require its spec-path entry copy to have been elided AND every tail leaf
    // to preserve its address. Recorded by definition span → param count. Skip inside a
    // specialization clone (it re-lowers the same AST/spans; the primary lowering already records).
    private void LowerLambdaCoreRecordAccStableFold(Expr.Lambda lam, TcoContext? savedTcoCtx, HashSet<string> specElidedAccs)
    {
        if (savedTcoCtx is not null
            && !_inSpecialization
            && !_inParallelSpecialization
            && savedTcoCtx.ParamNames.Count > 0
            && specElidedAccs.Contains(savedTcoCtx.ParamNames[^1])
            && Lookup(savedTcoCtx.SelfName)?.DefinitionSpan is { } foldSpan)
        {
            string accName = savedTcoCtx.ParamNames[^1];
            int paramCount = savedTcoCtx.ParamNames.Count;
            if (TailLeavesStable(lam.Body, accName, foldSpan, paramCount, new HashSet<string>(StringComparer.Ordinal)))
            {
                _accStableFolds[foldSpan] = paramCount;
            }
        }
    }

    private void LowerLambdaCoreFinishFunction(string label, IrFunctionOrigin origin)
    {
        var func = new IrFunction(
            Label: label,
            Instructions: new List<IrInst>(_inst),
            LocalCount: _nextLocalSlot,
            TempCount: _nextTempSlot,
            HasEnvAndArgParams: true,
            LocalNames: new Dictionary<int, string>(_localNames),
            LocalTypes: SnapshotLocalTypes()
        );

        AddFunction(func, origin);
    }

    // restore state
    private void LowerLambdaCoreRestoreFrame(LowerLambdaCoreFrame frame)
    {
        _inst.Clear();
        _inst.AddRange(frame.Inst);
        _nextTempSlot = frame.TempSlot;
        _nextLocalSlot = frame.LocalSlot;
        _localNames.Clear();
        _localTypes.Clear();
        foreach (var kv in frame.LocalNames) _localNames[kv.Key] = kv.Value;
        foreach (var kv in frame.LocalTypes) _localTypes[kv.Key] = kv.Value;
        _scopes.Clear();
        foreach (var s in frame.Scopes.Reverse())
        {
            _scopes.Push(new Dictionary<string, Binding>(s, StringComparer.Ordinal));
        }

        _lambdaDepth--;
        _inCoroutineBody = frame.InCoroutineBody;

        _linearReuseNames.Clear();
        foreach (var n in frame.LinearReuseNames) _linearReuseNames.Add(n);
        _linearSpecializationAccumulators.Clear();
        foreach (var n in frame.SpecAccumulators) _linearSpecializationAccumulators.Add(n);
        _resetSafeAccumulators.Clear();
        foreach (var n in frame.ResetSafe) _resetSafeAccumulators.Add(n);
        _reuseResultTemps.Clear();
        foreach (var t in frame.ReuseResultTemps) _reuseResultTemps.Add(t);
        RestoreTempOwnershipFacts(frame.TempOwnershipFacts);
        RestoreRuntimeManagedFrameState(frame);
        _patternBindingPlacementSites.Clear();
        _patternBindingPlacementSites.AddRange(frame.PatternBindingPlacementSites);
        RestoreKnownFunctionLabels(frame);
        _reuseTokens.Clear();
        _reuseTokens.AddRange(frame.ReuseTokens);
        _letBindingValues.Clear();
        foreach (var kv in frame.LetBindingValues) _letBindingValues[kv.Key] = kv.Value;
        _localBytesProvenance.Clear();
        foreach (var pair in frame.LocalBytesProvenance)
        {
            _localBytesProvenance[pair.Key] = pair.Value;
        }
    }

    private void RestoreRuntimeManagedFrameState(LowerLambdaCoreFrame frame)
    {
        _pendingRuntimeArgumentFlags.Clear();
        foreach ((int temp, int parameterSlot) in frame.PendingRuntimeArgumentFlags)
        {
            _pendingRuntimeArgumentFlags[temp] = parameterSlot;
        }
    }

    private void RestoreKnownFunctionLabels(LowerLambdaCoreFrame frame)
    {
        _knownFunctionLabelsBySlot.Clear();
        foreach (var kv in frame.KnownFunctionLabelsBySlot) _knownFunctionLabelsBySlot[kv.Key] = kv.Value;
        _knownFunctionLabelsByEnvIndex.Clear();
        foreach (var kv in frame.KnownFunctionLabelsByEnvIndex) _knownFunctionLabelsByEnvIndex[kv.Key] = kv.Value;
    }

    private int LowerLambdaCoreMakeClosure(
        string label,
        int envPtrTemp,
        IReadOnlyList<string> captures,
        bool stackAllocateClosure,
        bool bodyRuntimeManaged,
        LoweredValueRequest request)
    {
        // Produce the closure object and its optional lifecycle metadata.
        int closureTemp = NewTemp();
        int envSizeBytes = captures.Count * 8;
        bool returnsRuntimeManaged = AllowsAsyncIndependentRcPlacement && AllowsOrdinaryRcPlacement
            && bodyRuntimeManaged;
        bool acceptsRuntimeManagedArgument = _runtimeNormalizedFunctionArgumentLabels.Contains(label);
        EmitLambdaClosureObject(
            closureTemp, label, envPtrTemp, envSizeBytes, stackAllocateClosure,
            returnsRuntimeManaged, acceptsRuntimeManagedArgument, request);

        // Record any resource captured by this closure, with its env offset (capture i lives at
        // env+i*8) and type. Ownership scopes are separate from binding scopes, so the captured
        // names still resolve to their owning bindings here.
        var resourceCaptures = new List<(int EnvOffset, string Name, TypeRef Type)>();
        var runtimeManagedCaptures = new List<(int EnvOffset, TypeRef Type)>();
        for (int ci = 0; ci < captures.Count; ci++)
        {
            var owned = LookupOwnedValue(captures[ci]);
            if (owned is not null && (owned.IsResource || owned.IsResourceBearing))
            {
                // The resource now lives inside this closure's environment. If the closure outlives
                // the owning scope — directly, via an aggregate, or through a chain of closures — the
                // scope must not close the resource at exit. Mark the owner so scope-exit drop
                // transfers ownership to the closure instead (see OwnershipInfo.CapturedByClosure).
                owned.CapturedByClosure = true;
                if (owned.IsResource && owned.Type is not null)
                {
                    resourceCaptures.Add((ci * 8, ResolveOwnershipAlias(captures[ci]), owned.Type));
                }
            }
            else if (request.EmitsRuntime(LoweredValueRuntimeRepresentation.Closure)
                && owned is { RuntimeManaged: true, Type: not null })
            {
                owned.CapturedByClosure = true;
                owned.ReleaseKind = ResourceReleaseKind.Moved;
                runtimeManagedCaptures.Add((ci * 8, owned.Type));
            }
        }

        if (resourceCaptures.Count > 0)
        {
            _closureResourceCaptures[closureTemp] = resourceCaptures;
        }
        if (runtimeManagedCaptures.Count > 0)
        {
            string dropperLabel = SynthesizeRuntimeManagedClosureDropper(runtimeManagedCaptures);
            int dropperTemp = NewTemp();
            Emit(new IrInst.LoadFuncAddr(dropperTemp, dropperLabel));
            Emit(new IrInst.StoreMemOffset(closureTemp, 24, dropperTemp));
        }

        AttachRuntimeManagedClosureNormalizer(label, captures);

        return closureTemp;
    }

    private void EmitLambdaClosureObject(
        int closureTemp,
        string label,
        int envPtrTemp,
        int envSizeBytes,
        bool stackAllocateClosure,
        bool returnsRuntimeManaged,
        bool acceptsRuntimeManagedArgument,
        LoweredValueRequest request)
    {
        if (stackAllocateClosure)
        {
            Emit(new IrInst.MakeClosureStack(
                closureTemp,
                label,
                envPtrTemp,
                envSizeBytes,
                returnsRuntimeManaged,
                acceptsRuntimeManagedArgument));
        }
        else
        {
            Emit(new IrInst.MakeClosure(closureTemp, label, envPtrTemp, envSizeBytes,
                request.EmitsRuntime(LoweredValueRuntimeRepresentation.Closure),
                returnsRuntimeManaged,
                acceptsRuntimeManagedArgument));
        }
    }

    // Collect the names a pattern binds (Var subpatterns), recursively.
    private static void CollectPatternBinders(Pattern pattern, HashSet<string> into)
    {
        switch (pattern)
        {
            case Pattern.Var v:
                into.Add(v.Name);
                break;
            case Pattern.Cons cons:
                CollectPatternBinders(cons.Head, into);
                CollectPatternBinders(cons.Tail, into);
                break;
            case Pattern.Tuple tuple:
                foreach (var p in tuple.Elements)
                {
                    CollectPatternBinders(p, into);
                }

                break;
            case Pattern.Constructor ctor:
                foreach (var p in ctor.Patterns)
                {
                    CollectPatternBinders(p, into);
                }

                break;
            case Pattern.Record record:
                foreach ((string _, Pattern fieldPattern) in record.Fields)
                {
                    CollectPatternBinders(fieldPattern, into);
                }

                break;
            case Pattern.As asPattern:
                CollectPatternBinders(asPattern.Inner, into);
                into.Add(asPattern.Name);
                break;
            case Pattern.Or { Alternatives.Count: > 0 } orPattern:
                CollectPatternBinders(orPattern.Alternatives[0], into);
                break;
        }
    }

    private bool TryGetExactFunctionArity(TypeRef type, out int arity)
    {
        arity = 0;
        var current = Prune(type);

        while (current is TypeRef.TFun funType)
        {
            arity++;
            current = Prune(funType.Ret);
        }

        if (current is TypeRef.TVar resultVar)
        {
            // A trait-constrained result is a value selected by static evidence, not another
            // function arrow. This keeps oversaturated-call diagnostics exact for generic operator
            // helpers without relying on the removed overload-specific type-variable registries.
            if (_traitConstraintScopes.SelectMany(scope => scope.Constraints).Any(constraint =>
                    TraitTypeOperations.FreeVariables(
                        new TypeScheme([], new TypeRef.TTuple(constraint.TypeArgs))).Contains(resultVar.Id)))
            {
                return true;
            }

            arity = 0;
            return false;
        }

        return true;
    }

    private static string? TryGetCalleeDisplayName(Expr expr)
    {
        return expr switch
        {
            Expr.Var v => v.Name,
            Expr.QualifiedVar qv => $"{qv.Module}.{qv.Name}",
            _ => null
        };
    }


















    private (int, TypeRef) LowerCall(
        Expr.Call call,
        LoweredValueRequest request)
    {
        using var diagnosticSpan = PushDiagnosticSpan(call);
        // Collect all args from the call chain to support multi-arg constructor application:
        //   Pair(1, 2) is parsed as Call(Call(Var("Pair"), 1), 2) — collect [1, 2] with root Var("Pair")
        var collectedArgs = new List<Expr>();
        var rootExpr = CollectCallArgs(call, collectedArgs);

        if (LowerCallTryDirectForms(call, rootExpr, collectedArgs, request) is { } directResult)
        {
            return directResult;
        }

        // A proven tail self-call is already on its most specialized path: update the loop
        // parameters and jump to the current function's body. The general reuse router must not
        // interpret a unique back-edge argument as a request to clone the recursive function.
        // Such a clone reconstructs and calls its own closure recursively, losing the stack bound
        // that TCO guarantees and relying on a later LLVM optimization to recover it.
        if (TryLowerTcoSelfCall(rootExpr, collectedArgs) is { } tailCallResult)
        {
            return tailCallResult;
        }

        if (LowerCallTryParallelAndReuseForms(call, rootExpr, collectedArgs) is { } routedResult)
        {
            return routedResult;
        }

        if (LowerCallTryGenericInlineForm(rootExpr, collectedArgs, request) is { } genericInlineResult)
        {
            return genericInlineResult;
        }

        if (LowerCallTryReuseInlineForm(rootExpr, collectedArgs, request) is { } reuseInlineResult)
        {
            return reuseInlineResult;
        }

        if (LowerCallTryCoroutineHelperForm(call, rootExpr, collectedArgs) is { } helperResult)
        {
            return helperResult;
        }

        if (LowerCallDirectNamedBinding(rootExpr, collectedArgs, request) is { } namedBindingResult)
        {
            return namedBindingResult;
        }

        // Qualified intrinsic call: Ashes.IO.print(...), Ashes.IO.panic(...)
        if (rootExpr is Expr.QualifiedVar qv
            && LowerCallQualifiedBuiltin(rootExpr, qv, collectedArgs, request) is { } builtinResult)
        {
            return builtinResult;
        }

        return LowerCallGeneral(call, rootExpr, collectedArgs, request);
    }

    private (int, TypeRef)? LowerCallDirectNamedBinding(
        Expr rootExpression,
        List<Expr> arguments,
        LoweredValueRequest request)
    {
        if (rootExpression is not Expr.Var variable)
        {
            return null;
        }

        if (Lookup(variable.Name) is Binding.Intrinsic intrinsic)
        {
            RecordHoverScheme(GetSpan(rootExpression), variable.Name, intrinsic.S);
            return LowerCallIntrinsic(rootExpression, intrinsic, arguments, request);
        }
        if (Lookup(variable.Name) is Binding.ExternalFunction externalFunction)
        {
            RecordHoverType(GetSpan(rootExpression), variable.Name, externalFunction.Type);
            return LowerExternalCall(
                rootExpression,
                externalFunction.Function,
                externalFunction.Type,
                arguments);
        }
        return null;
    }

    private (int Temp, TypeRef Type)? TryLowerTcoSelfCall(
        Expr rootExpression,
        List<Expr> arguments)
    {
        return _tcoCtx is { InTailPosition: true } tco
            && IsTcoSelfCallRoot(rootExpression, tco)
            && arguments.Count == tco.ParamCount
                ? LowerCallTcoSelfCall(tco, arguments)
                : null;
    }

    // Ordinary curried recursion must resolve back to the outermost generated label. A synthesized
    // coroutine loop instead transports its captured self slot; a few intrinsic restart loops
    // deliberately leave the marker name unbound, which is the final fallback.
    private bool IsTcoSelfCallRoot(Expr rootExpr, TcoContext tco)
    {
        if (rootExpr is not Expr.Var selfVar
            || !string.Equals(selfVar.Name, tco.SelfName, StringComparison.Ordinal))
        {
            return false;
        }

        if (TryResolveKnownFunctionLabel(rootExpr, out string selfLabel))
        {
            return string.Equals(selfLabel, tco.SelfLabel, StringComparison.Ordinal);
        }

        return Lookup(selfVar.Name) switch
        {
            Binding.Local local => tco.SelfSlot == local.Slot,
            null => true,
            _ => false,
        };
    }

    // Direct call forms resolved from the root expression alone: constructor application, the
    // built-in Stop.stop, a capability operation call, and a dictionary-passing generic function.
    private (int, TypeRef)? LowerCallTryDirectForms(
        Expr.Call call,
        Expr rootExpr,
        List<Expr> collectedArgs,
        LoweredValueRequest request)
    {
        if (rootExpr is Expr.Var varCtor
            && TryResolveConstructorSymbol(varCtor.Name, GetSpan(varCtor), out var ctorSym))
        {
            return LowerConstructorCallWithExpectedType(call, ctorSym, collectedArgs, request);
        }

        // Constructor application through a module alias: json.JsonInt(42) where `json` is
        // `Ashes.Text.Json`. Resolved directly (rather than falling through to a generic closure
        // call) so it compiles identically to the unqualified form.
        if (rootExpr is Expr.QualifiedVar ctorQv
            && !_capabilitySymbols.ContainsKey(ctorQv.Module)
            && TryResolveQualifiedConstructor(ctorQv.Name, ResolveModuleAlias(ctorQv.Module), out var qualifiedCtorSym))
        {
            return LowerConstructorCallWithExpectedType(
                call,
                qualifiedCtorSym,
                collectedArgs,
                request);
        }

        // Capability operation call: Clock.now(x) — the implicit form of `perform Clock.now(x)`.
        if (rootExpr is Expr.QualifiedVar stopQv
            && string.Equals(stopQv.Module, "Stop", StringComparison.Ordinal)
            && string.Equals(stopQv.Name, "stop", StringComparison.Ordinal)
            && collectedArgs.Count > 0)
        {
            return LowerBuiltinStopCall(collectedArgs, GetSpan(stopQv));
        }

        if (rootExpr is Expr.QualifiedVar capabilityQv && TryGetCapabilityOperationReference(capabilityQv, out CapabilitySymbol capabilitySym))
        {
            return LowerCapabilityOperationCall(capabilitySym, capabilityQv, collectedArgs);
        }

        (int Temp, TypeRef Type)? traitCall = TryLowerTraitDirectCall(call, rootExpr, collectedArgs, request);
        if (traitCall is not null)
        {
            return traitCall;
        }

        // Call to a generic function compiled to dictionary-passing form: supply its leading operation
        // arguments (provider or threaded op-parameter) before the real arguments.
        // Calls inside a dictionary function's body were threaded syntactically (an op-parameter is in
        // scope). Only an *external* call — none in scope — resolves its operations from providers. The
        // callee may be a plain `Var` (same file) or a qualified `Module.fn` cross-module reference,
        // which resolves to the stitched flat name.
        if (_activeOpParams.Count == 0
            && collectedArgs.Count > 0
            && ResolveSpecializableCalleeName(rootExpr) is { } dictFnName
            && (rootExpr is not Expr.Var dfv || !_shadowedDictFns.Contains(dfv.Name))
            && _dictFunctions.TryGetValue(dictFnName, out var dictInfo))
        {
            return LowerDictionaryFunctionCall(dictInfo, rootExpr, dictFnName, collectedArgs, GetSpan(call));
        }

        return null;
    }

    private (int Temp, TypeRef Type) LowerConstructorCallWithExpectedType(
        Expr.Call call,
        ConstructorSymbol constructor,
        List<Expr> arguments,
        LoweredValueRequest request)
    {
        (int Temp, TypeRef Type) result = LowerConstructorApplication(
            constructor,
            arguments,
            location: ResolveSourceLocation(AstSpans.GetOrDefault(call)),
            request: request.WithoutExpectedType());
        if (request.ExpectedType is not null)
        {
            Unify(result.Type, request.ExpectedType);
        }
        return result;
    }

    private (int Temp, TypeRef Type)? TryLowerTraitDirectCall(
        Expr.Call call,
        Expr rootExpression,
        List<Expr> arguments,
        LoweredValueRequest request)
    {
        if (rootExpression is Expr.QualifiedVar traitMethod
            && TryGetTraitMethod(traitMethod, out TraitSymbol trait, out TraitMethodSymbol method))
        {
            return LowerTraitMethodCall(trait, method, arguments, GetSpan(call), request);
        }
        if (rootExpression is Expr.QualifiedVar missingCapabilityOperation
            && TryGetReferencedCapability(
                missingCapabilityOperation.Module,
                out CapabilitySymbol declaredCapability))
        {
            return LowerCapabilityOperationCall(
                declaredCapability,
                missingCapabilityOperation,
                arguments);
        }
        if (TryLowerTraitDictionaryReuseSpecialization(call, rootExpression, arguments) is { } reuse)
        {
            return reuse;
        }
        return TryLowerTraitDictionaryFunctionCall(rootExpression, arguments, GetSpan(call));
    }

    private (int, TypeRef)? LowerCallTryParallelAndReuseForms(Expr.Call call, Expr rootExpr, List<Expr> collectedArgs)
    {
        // Work-conserving parallel reduce: a saturated `Parallel.reduce` call at a concrete result
        // type routes to the runtime chunk queue (workers pull element indexes from a shared atomic
        // counter; the caller merges per-index results in fixed list order), which packs workers
        // tighter than the static fork tree below. Grained calls keep the fork-tree path — an
        // explicit grain requests the divide-and-conquer shape.
        if (!_inParallelSpecialization
            && ResolveSpecializableCalleeName(rootExpr) is { } queueCalleeName
            && string.Equals(queueCalleeName, ParallelReduceName, StringComparison.Ordinal)
            && collectedArgs.Count == 4
            && _parallelSpecializable.ContainsKey(ParallelReduceGrainedName))
        {
            return LowerParallelReduceQueued(collectedArgs);
        }

        // Data-parallel map/reduce: a saturated call to a parallel combinator at a concrete result type
        // is monomorphized into a self-recursive specialization whose `both` splits fork genuinely. A
        // `map`/`reduce` call is rewritten to its grained form (grain = 1) first. A self-recursive call
        // from inside such a specialization (Binding.Self) must NOT re-specialize — it already runs the
        // concrete body — so skip while generating one.
        if (!_inParallelSpecialization
            && TryResolveParallelCombinatorCall(rootExpr, collectedArgs) is { } parCall
            && TryLowerParallelSpecializedCall(parCall.GrainedName, parCall.Lambda, parCall.Args) is { } parResult)
        {
            return parResult;
        }

        if (!_configuration.EnableReuse)
        {
            return null;
        }

        ReuseSpecializationQualification? qualification =
            QualifyReuseSpecializationCall(rootExpr, collectedArgs);
        if (qualification is null)
        {
            return null;
        }

        if (qualification.Accepted && !CannotAttemptHigherOrderReuse(collectedArgs))
        {
            return LowerReuseSpecializedCall(
                qualification.TargetFunction,
                qualification.FunctionType!,
                collectedArgs,
                call);
        }

        if (ShouldRecordReuseSpecializationCandidateRejection(
                rootExpr,
                qualification.TargetFunction))
        {
            RecordReuseSpecializationCandidateRejection(qualification, call);
        }

        return null;
    }

    // Capability monomorphization: a saturated call to a capability-generic function is inlined so
    // the body lowers with the call's concrete argument types, letting a parameterized capability
    // operation resolve to its provider. Guarded against recursion and re-entrancy.
    private (int, TypeRef)? LowerCallTryGenericInlineForm(
        Expr rootExpr,
        List<Expr> collectedArgs,
        LoweredValueRequest request)
    {
        if (Environment.GetEnvironmentVariable("ASH_DBG_REUSE") is not null
            && rootExpr is Expr.Var dbgFn && _inlinableFunctions.TryGetValue(dbgFn.Name, out var dbgInl)
            && dbgFn.Name.Contains("Map", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[reuse] call {dbgFn.Name}: inSpec={_inSpecialization} tokens={_reuseTokens.Count} shadowed={_shadowedInlinables.ContainsKey(dbgFn.Name)} inProgress={_inliningInProgress.Contains(dbgFn.Name)} params={dbgInl.Params.Count} args={collectedArgs.Count}");
        }

        if (rootExpr is Expr.Var capGenVar)
        {
            string capGenName = capGenVar.Name;
            if (_capabilityGenericInline.Contains(capGenName)
                && !_shadowedInlinables.ContainsKey(capGenVar.Name)
                && !_inliningInProgress.Contains(capGenName)
                && Lookup(capGenVar.Name) is not (Binding.Local or Binding.Env or Binding.EnvScheme)
                && _inlinableFunctions.TryGetValue(capGenName, out var capGenInlinable)
                && capGenInlinable.Params.Count == collectedArgs.Count)
            {
                return InlineCall(
                    capGenName,
                    capGenInlinable.Params,
                    capGenInlinable.Body,
                    collectedArgs,
                    request);
            }
        }

        return null;
    }

    /// <summary>
    /// True when every top-level binding an inline candidate's body reads can be resolved at the
    /// current site. Inlining splices the body into a scope the caller's free-variable analysis
    /// never saw, so a top-level value the body reads is neither on the scope chain nor recoverable
    /// by label the way a top-level function is. Leaving such a call un-inlined resolves it through
    /// the ordinary call path instead of failing to resolve the spliced reference.
    /// </summary>
    private bool InlinedBodyReferencesResolveHere(string inlineName, IReadOnlyList<string> parameters, Expr body)
    {
        if (!_inlinableBodyExternalReferences.TryGetValue(inlineName, out HashSet<string>? references))
        {
            references = FreeVars(body, [.. parameters]);
            _inlinableBodyExternalReferences[inlineName] = references;
        }

        foreach (string reference in references)
        {
            if (Lookup(reference) is null
                && !_topLevelFunctionRefs.ContainsKey(reference)
                && !_constructorSymbols.ContainsKey(reference)
                && !_inlinableFunctions.ContainsKey(reference))
            {
                return false;
            }
        }

        return true;
    }

    // In-place reuse: inside a reuse arm (a dead-cell token is live), a saturated call to a
    // non-recursive top-level helper is inlined, so the helper's constructor becomes local to
    // this arm and can reuse the token (e.g. loop(...)(mk(l)(v+n)(r)) where mk rebuilds a node).
    // Only when the callee name resolves to that top-level function (not shadowed by a local).
    // Inline a saturated helper call when a reuse token is live, OR unconditionally inside a
    // specialization (so every helper folds down to constructors and never leaves a call to a
    // top-level function the specialization didn't capture).
    // The callee may be a plain Var (module code, where the stitcher already rewrote member
    // references to flat names) or a qualified stdlib reference from user code
    // (Ashes.Collection.Map.makeNode → Ashes_Map_makeNode) — the latter matters inside a specialization
    // generated FOR a user function, whose body keeps its QualifiedVar nodes but lowers in an
    // isolated scope where only inline/by-label resolution works.
    private (int, TypeRef)? LowerCallTryReuseInlineForm(
        Expr rootExpr,
        List<Expr> collectedArgs,
        LoweredValueRequest request)
    {
        if ((_reuseTokens.Count > 0
                || _inSpecialization
                || _loweringTcoBackEdgeArguments
                    && ResolveSpecializableCalleeName(rootExpr) is not null
                    && GetOwnershipSummaryForCallRoot(rootExpr) is { ResultFresh: true })
            && ResolveSpecializableCalleeName(rootExpr) is { } inlineName
            && (rootExpr is not Expr.Var vRoot || !_shadowedInlinables.ContainsKey(vRoot.Name))
            && !_inliningInProgress.Contains(inlineName)
            && _inlinableFunctions.TryGetValue(inlineName, out var inlinable)
            && inlinable.Params.Count == collectedArgs.Count
            && InlinedBodyReferencesResolveHere(inlineName, inlinable.Params, inlinable.Body))
        {
            return InlineCall(
                inlineName,
                inlinable.Params,
                inlinable.Body,
                collectedArgs,
                request);
        }

        return null;
    }

    private (int, TypeRef) LowerCallTcoSelfCall(TcoContext tco, List<Expr> collectedArgs)
    {
        // Evaluate all new arg values first (into temps), BEFORE storing any
        var savedTail = tco.InTailPosition;
        tco.InTailPosition = false;

        (int[] newArgTemps, TypeRef[] newArgTypes) = LowerCallTcoEvalBackEdgeArgs(tco, collectedArgs);
        LowerCallTcoPromoteResolvedRuntimeParams(tco, newArgTypes);
        LowerCallTcoTransferPatternBindings(tco, collectedArgs, newArgTemps);
        int[] oldRuntimeParamTemps = LowerCallTcoLoadOldRuntimeParams(tco);

        // Store new values into TCO param slots
        for (int i = 0; i < tco.ParamSlots.Count; i++)
        {
            Emit(new IrInst.StoreLocal(tco.ParamSlots[i], newArgTemps[i]));
        }

        (List<(OwnershipInfo Info, ResourceReleaseKind ReleaseKind)> releaseSnapshot,
            List<OwnershipInfo> iterationOwnedDrops) =
            LowerCallTcoPrepareOwnedDrops(tco, collectedArgs);

        // Arena reset: restore heap state to loop-iteration watermark before
        // jumping back.
        //
        // All args are copy types (Int, Float, Bool) → plain reset.
        // No heap pointers escape, so reclaiming the iteration's allocations is safe.
        //
        // Some args are heap types but all heap-type args can be copied out
        // (TStr, or TList with copy-type element).  After the reset we copy each such
        // argument out to the fresh watermark position, then overwrite its param slot
        // with the copy pointer.  The previous iteration's cells lie BELOW the saved
        // watermark and are therefore never reclaimed.
        LowerCallTcoScheduleReset(
            tco,
            collectedArgs,
            newArgTemps,
            newArgTypes,
            oldRuntimeParamTemps,
            iterationOwnedDrops);

        // Free any dynamic stack allocations made in the loop body this iteration (restore the stack
        // pointer to the loop-body-entry watermark). The next-iteration arguments live in param slots
        // (function-entry allocas, above this watermark) and the arena, so they survive. Without this,
        // per-iteration stack scratch accumulates and overflows the stack at scale.
        if (tco.StackPtrSlot >= 0)
        {
            Emit(new IrInst.RestoreStackPointer(tco.StackPtrSlot));
        }

        // Jump back to loop start
        Emit(new IrInst.Jump(tco.BodyLabel));

        RestoreOwnershipReleaseKinds(releaseSnapshot);

        tco.InTailPosition = savedTail;

        return LowerCallTcoBackEdgeDummy();
    }

    private void LowerCallTcoScheduleReset(
        TcoContext tco,
        List<Expr> collectedArgs,
        int[] newArgTemps,
        TypeRef[] newArgTypes,
        int[] oldRuntimeParamTemps,
        IReadOnlyList<OwnershipInfo> iterationOwnedDrops)
    {
        if (tco.ArenaCursorSlot >= 0)
        {
            var facts = LowerCallTcoGatherResetFacts(tco, collectedArgs, newArgTypes);
            LowerCallTcoEmitReset(
                tco,
                collectedArgs,
                newArgTemps,
                newArgTypes,
                oldRuntimeParamTemps,
                iterationOwnedDrops,
                facts);
            return;
        }

        foreach (OwnershipInfo owned in iterationOwnedDrops)
        {
            EmitOwnedValueDrop(owned);
        }
    }

    /// <summary>
    /// Transfers a binding that canonical ownership proved is the unchanged successor for its root
    /// parameter. The successor receives one reference before the old parameter graph is released;
    /// no source-name alias state survives beyond this call site.
    /// </summary>
    private void LowerCallTcoTransferPatternBindings(
        TcoContext tco,
        IReadOnlyList<Expr> collectedArgs,
        int[] newArgTemps)
    {
        HashSet<int> transferredRoots = [];
        for (int argumentIndex = 0; argumentIndex < collectedArgs.Count; argumentIndex++)
        {
            Expr argument = collectedArgs[argumentIndex];
            if (argument is not Expr.Var variable
                || Lookup(variable.Name) is not Binding.Local local
                || !tco.TryGetPatternBindingOwnership(
                    local.Slot,
                    out PatternBindingOwnershipFact? ownership)
                || ownership?.Ownership != PatternBindingOwnershipKind.TransferredToSameParameter
                || ownership.RootParameterOrdinal < 0
                || ownership.RootParameterOrdinal >= tco.ParamSlots.Count)
            {
                continue;
            }

            int rootSlot = tco.ParamSlots[ownership.RootParameterOrdinal];
            if (!tco.IsRuntimeManagedSlot(rootSlot))
            {
                continue;
            }

            // Argument evaluation returns a borrow of the pattern child. Retain that exact
            // successor only after resolved back-edge types have established that its root
            // parameter is runtime-managed. The old root may then be released without leaving the
            // next iteration's parameter pointing into a reclaimed graph.
            newArgTemps[argumentIndex] = EmitRuntimeManagedNullableDup(newArgTemps[argumentIndex]);

            transferredRoots.Add(rootSlot);
        }

        foreach (int rootSlot in transferredRoots)
        {
            bool rootMovesToNextIteration = collectedArgs.Any(argument =>
                argument is Expr.Var variable
                && Lookup(variable.Name) is Binding.Local local
                && local.Slot == rootSlot);
            if (rootMovesToNextIteration)
            {
                continue;
            }

            int rootTemp = NewTemp();
            Emit(new IrInst.LoadLocal(rootTemp, rootSlot));
            EmitTransferredPatternBindingRootDrop(tco, rootSlot, rootTemp);

            if (tco.TryGetRuntimeManagedActiveSlot(rootSlot, out int activeSlot))
            {
                int inactiveTemp = NewTemp();
                Emit(new IrInst.LoadConstInt(inactiveTemp, 0));
                Emit(new IrInst.StoreLocal(activeSlot, inactiveTemp));
            }
        }
    }

    private void EmitTransferredPatternBindingRootDrop(
        TcoContext tco,
        int rootSlot,
        int rootTemp)
    {
        TypeRef rootType = tco.GetRuntimeManagedType(rootSlot);
        switch (rootType)
        {
            case TypeRef.TList list:
                EmitRuntimeManagedListDrop(rootTemp, list.Element);
                return;
            case TypeRef.TTuple tuple:
                EmitRuntimeManagedTupleDrop(rootTemp, tuple);
                return;
            case TypeRef.TNamedType named:
                EmitRuntimeManagedAdtDrop(rootTemp, named);
                return;
            case TypeRef.TFun:
                if (tco.RuntimeManagedClosureActiveSlots.TryGetValue(rootSlot, out int closureActiveSlot))
                {
                    EmitRuntimeManagedClosureDropIfActive(rootTemp, closureActiveSlot);
                }
                return;
            default:
                Emit(new IrInst.RcDrop(
                    rootTemp,
                    GetOwnedTypeName(rootType) ?? "Value",
                    RuntimeManaged: true));
                return;
        }
    }

    private (int[] Temps, TypeRef[] Types) LowerCallTcoEvalBackEdgeArgs(
        TcoContext tco,
        List<Expr> collectedArgs)
    {
        bool savedBackEdgeArguments = _loweringTcoBackEdgeArguments;
        _loweringTcoBackEdgeArguments = true;
        try
        {
            return LowerCallTcoEvalArgs(tco, collectedArgs);
        }
        finally
        {
            _loweringTcoBackEdgeArguments = savedBackEdgeArguments;
        }
    }

    private (int Temp, TypeRef Type) LowerCallTcoBackEdgeDummy()
    {
        // The back-edge cannot reach its expression join. Mark its synthetic value ownership-neutral
        // so reachable arms alone decide whether the function transfers an RC result.
        int dummy = NewTemp();
        Emit(new IrInst.LoadConstInt(dummy, 0));
        MarkRuntimeManagedTemp(dummy);
        return (dummy, NewTypeVar());
    }

    private int[] LowerCallTcoLoadOldRuntimeParams(TcoContext tco)
    {
        var oldRuntimeParamTemps = new int[tco.ParamSlots.Count];
        for (int i = 0; i < tco.ParamSlots.Count; i++)
        {
            oldRuntimeParamTemps[i] = NewTemp();
            Emit(new IrInst.LoadLocal(oldRuntimeParamTemps[i], tco.ParamSlots[i]));
        }

        return oldRuntimeParamTemps;
    }

    private void LowerCallTcoPromoteResolvedRuntimeParams(TcoContext tco, TypeRef[] argTypes)
    {
        var parameterTypes = new TypeRef?[tco.ParamSlots.Count];
        int count = Math.Min(argTypes.Length, parameterTypes.Length);
        for (int ordinal = 0; ordinal < count; ordinal++)
        {
            parameterTypes[ordinal] = Prune(argTypes[ordinal]);
        }

        EvaluateTcoParamPlacements(
            tco,
            parameterTypes,
            TcoPlacementResolutionPoint.ResolvedBackEdge,
            includeFreshClosures: false,
            applyReuseRestrictions: true);
        foreach (int slot in tco.RuntimeManagedSlotsInOrder)
        {
            if (tco.GetRuntimeManagedType(slot) is not TypeRef.TFun
                && !tco.RuntimeManagedParamActiveSlots.ContainsKey(slot))
            {
                tco.RuntimeManagedParamActiveSlots[slot] = NewLocal();
            }
        }
    }

    private (
        List<(OwnershipInfo Info, ResourceReleaseKind ReleaseKind)> Snapshot,
        List<OwnershipInfo> Drops) LowerCallTcoPrepareOwnedDrops(
        TcoContext tco,
        List<Expr> collectedArgs)
    {
        // Back-edge release state belongs only to this control-flow path. Match/if siblings are
        // lowered afterwards using the same OwnershipInfo objects, so restore it after the jump.
        List<(OwnershipInfo Info, ResourceReleaseKind ReleaseKind)> snapshot =
            SnapshotOwnershipReleaseKinds();
        LowerCallTcoMarkMovedArgs(collectedArgs);
        List<OwnershipInfo> drops = CollectTcoBackEdgeOwnedDrops(tco);
        return (snapshot, drops);
    }

    private List<(OwnershipInfo Info, ResourceReleaseKind ReleaseKind)> SnapshotOwnershipReleaseKinds()
    {
        var snapshot = new List<(OwnershipInfo Info, ResourceReleaseKind ReleaseKind)>();
        foreach (Dictionary<string, OwnershipInfo> scope in _ownershipScopes)
        {
            foreach (OwnershipInfo info in scope.Values)
            {
                snapshot.Add((info, info.ReleaseKind));
            }
        }

        return snapshot;
    }

    private static void RestoreOwnershipReleaseKinds(
        List<(OwnershipInfo Info, ResourceReleaseKind ReleaseKind)> snapshot)
    {
        foreach ((OwnershipInfo info, ResourceReleaseKind releaseKind) in snapshot)
        {
            info.ReleaseKind = releaseKind;
        }
    }

    // An owned value passed by name as a self-call argument moves to the next iteration —
    // it must not be dropped at this back-edge (a resource would be closed, a closure with a
    // dropper would close its captured resource). Mark it consumed so
    // EmitTcoBackEdgeOwnedDrops (and the dead-code arm Drops after the jump) skip it.
    private void LowerCallTcoMarkMovedArgs(List<Expr> collectedArgs)
    {
        foreach (var arg in collectedArgs)
        {
            if (arg is Expr.Var argVar
                && LookupOwnedValue(argVar.Name) is
                { IsDropped: false, PerceusPatternOwner: false } movedOwned)
            {
                movedOwned.ReleaseKind = ResourceReleaseKind.Moved;
            }
        }
    }

    private (int[] Temps, TypeRef[] Types) LowerCallTcoEvalArgs(TcoContext tco, List<Expr> collectedArgs)
    {
        var newArgTemps = new int[collectedArgs.Count];
        var newArgTypes = new TypeRef[collectedArgs.Count];
        // Type-check: resolve self binding and unify arg types with param types
        var selfBinding = Lookup(tco.SelfName);
        var curType = selfBinding is not null ? Prune(selfBinding.Type) : null;
        for (int i = 0; i < collectedArgs.Count; i++)
        {
            TypeRef? expectedType = curType is TypeRef.TFun expectedFunction
                ? expectedFunction.Arg
                : null;
            var (argTemp, argType) = LowerCallTcoEvalArg(
                tco,
                collectedArgs[i],
                i,
                expectedType);
            newArgTemps[i] = argTemp;
            newArgTypes[i] = argType;
            if (curType is TypeRef.TFun funType)
            {
                Unify(funType.Arg, argType);
                curType = Prune(funType.Ret);
            }
        }

        return (newArgTemps, newArgTypes);
    }

    private (int Temp, TypeRef Type) LowerCallTcoEvalArg(
        TcoContext tco,
        Expr argument,
        int index,
        TypeRef? expectedType)
    {
        (string Name, int Slot, int ResvStart, int ResvEnd)? savedAffineCtx = _affineAppendCtx;
        bool affineConsList = index < tco.ParamNames.Count
            && index < tco.ParamSlots.Count
            && tco.ParamFacts[tco.ParamSlots[index]].AffineConsList
            && argument is Expr.Cons { Tail: Expr.Var tail }
            && Lookup(tail.Name) is Binding.Local tailLocal
            && tailLocal.Slot == tco.ParamSlots[index];
        bool freshClosure = index < tco.ParamNames.Count
            && index < tco.ParamSlots.Count
            && tco.ParamFacts[tco.ParamSlots[index]].FreshClosureRebuild
            && IsRuntimeRcCopyClosureProducer(argument)
            && ClosureCapturesOnlyRuntimeManagedOrCopyValues(argument);
        bool freshAdt = LowerCallTcoTryGetAdtArguments(argument, out List<Expr>? constructorArguments);
        LowerCallTcoArmAffineStringArg(tco, argument, index);
        try
        {
            LoweredValueRequest request = affineConsList
                ? LoweredValueRequest
                    .OwnedRuntime(LoweredValueRuntimeRepresentation.List)
                    .WithRuntimeListContext(
                        tailBinding: null,
                        tailShared: false,
                        tcoTailSlot: tco.ParamSlots[index])
                : LoweredValueRequest.None;
            request = request
                .AddRuntime(
                    freshClosure,
                    LoweredValueRuntimeRepresentation.Closure)
                .AddRuntime(
                    freshAdt,
                    LoweredValueRuntimeRepresentation.TcoAdt)
                .WithRuntimeAdtContext(
                    freshAdt
                        ? LowerCallTcoAdtChildBindings(constructorArguments!)
                        : null);
            if (expectedType is not null)
            {
                request = request.WithExpectedType(expectedType);
            }
            (int Temp, TypeRef Type) lowered = LowerExpr(argument, request).AsPair();
            lowered.Temp = DuplicatePerceusPatternOwnerForAggregate(argument, lowered.Temp);
            return lowered;
        }
        finally
        {
            _affineAppendCtx = savedAffineCtx;
        }
    }

    private Dictionary<string, bool> LowerCallTcoAdtChildBindings(IReadOnlyList<Expr> arguments)
    {
        return arguments
            .OfType<Expr.Var>()
            .Where(variable => LookupOwnedValue(variable.Name) is { IsDropped: false } info
                && (info.RuntimeManaged || info.PerceusPatternOwner))
            .GroupBy(variable => variable.Name, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Count() > 1,
                StringComparer.Ordinal);
    }

    private bool LowerCallTcoTryGetAdtArguments(Expr argument, out List<Expr>? constructorArguments)
    {
        constructorArguments = null;
        return AllowsAsyncIndependentRcPlacement
            && AllowsOrdinaryRcPlacement
            && TryDescribeConstructorExpression(argument, out _, out constructorArguments, out _);
    }

    private void LowerCallTcoArmAffineStringArg(TcoContext tco, Expr argument, int index)
    {
        if (index >= tco.ParamNames.Count
            || tco.FixedCursorSlot < 0
            || index >= tco.ParamSlots.Count
            || !tco.ParamFacts[tco.ParamSlots[index]].AffineSelfAppendOnly
            || argument is not Expr.Add)
        {
            return;
        }

        Expr chainLeaf = argument;
        while (chainLeaf is Expr.Add chainAdd)
        {
            chainLeaf = chainAdd.Left;
        }

        if (chainLeaf is Expr.Var affineVar
            && string.Equals(affineVar.Name, tco.ParamNames[index], StringComparison.Ordinal)
            && tco.AffineResvSlots.TryGetValue(tco.ParamSlots[index], out var resvSlots))
        {
            _affineAppendCtx = (tco.ParamNames[index], tco.ParamSlots[index], resvSlots.Start, resvSlots.End);
        }
    }

    // Gather the AST/scope-dependent facts about each argument NOW (they need the
    // current scope and the raw arg expressions); the TYPE-dependent copy-out decision
    // may have to wait until inference finishes (see LowerCallTcoEmitReset).
    private (bool[] PassThrough, bool[] SingleFreshCons, bool[] FreshListRebuild, bool[] StableAccArg) LowerCallTcoGatherResetFacts(TcoContext tco, List<Expr> collectedArgs, TypeRef[] newArgTypes)
    {
        var passThrough = new bool[newArgTypes.Length];
        var singleFreshCons = new bool[newArgTypes.Length];
        var freshListRebuild = new bool[newArgTypes.Length];
        var stableAccArg = new bool[newArgTypes.Length];
        for (int i = 0; i < newArgTypes.Length; i++)
        {
            // A loop-invariant pass-through arg (the param's own unchanged Var at every tail
            // self-call) still holds the value passed INTO the loop — allocated before entry,
            // hence below even the FIXED loop-entry watermark. It needs no copy-out at all and
            // never endangers (or is endangered by) a reset. This is what lets a loop threading
            // a closure (fasta's randomFasta table), an invariant list, or any other heap value
            // alongside a growing accumulator keep the fixed mark instead of stranding every
            // iteration's accumulator copy below an advancing one. A shadowed earlier occurrence
            // of a duplicate parameter has no live binding in this body, so its synthetic
            // parallel-assignment slot is also reset-safe: no expression can observe it afterwards.
            bool visibleBinding = i < tco.ParamSlots.Count
                && tco.ParamFacts[tco.ParamSlots[i]].HasVisibleBinding;
            passThrough[i] = i < tco.ParamNames.Count
                && i < tco.ParamSlots.Count
                && (!visibleBinding
                    || tco.ParamFacts[tco.ParamSlots[i]].LoopInvariant
                        && collectedArgs[i] is Expr.Var passVar
                        && Lookup(passVar.Name) is Binding.Local passLocal
                        && passLocal.Slot == tco.ParamSlots[i]);

            // The single-cell list copy-outs preserve only the TOP cons cell, assuming the
            // tail already lives below the watermark — which holds only for literally
            // `head :: <loop accumulator param>` (through one level of let-binding).
            var argExpr = collectedArgs[i];
            if (argExpr is Expr.Var v
                && Lookup(v.Name) is Binding.Local local
                && _letBindingValues.TryGetValue(local.Slot, out var bound))
            {
                argExpr = bound;
            }

            singleFreshCons[i] = argExpr is Expr.Cons cons
                && cons.Tail is Expr.Var tailVar
                && Lookup(tailVar.Name) is Binding.Local tailLocal
                && tco.ParamSlots.Contains(tailLocal.Slot);

            // A back-edge DeepAdt clone of a LIST costs O(length) per iteration, so it is
            // licensed only when the list was freshly REBUILT this iteration (see
            // IsArenaSelfContainedListRebuildExpr); a threaded/consumed shape falls back to no reset.
            freshListRebuild[i] = IsArenaSelfContainedListRebuildExpr(argExpr);

            // A fully-reusing specialized accumulator is rewritten in place below the
            // watermark, so it survives a plain reset.
            stableAccArg[i] = i < tco.ParamNames.Count
                && _resetSafeAccumulators.Contains(tco.ParamNames[i])
                && IsStableAccumulatorExpr(
                    collectedArgs[i],
                    name => Lookup(name) is Binding.Local sl && sl.Slot == tco.ParamSlots[i]);
        }

        return (passThrough, singleFreshCons, freshListRebuild, stableAccArg);
    }

    private void LowerCallTcoEmitReset(
        TcoContext tco,
        List<Expr> collectedArgs,
        int[] newArgTemps,
        TypeRef[] newArgTypes,
        int[] oldRuntimeParamTemps,
        IReadOnlyList<OwnershipInfo> iterationOwnedDrops,
        (bool[] PassThrough, bool[] SingleFreshCons, bool[] FreshListRebuild, bool[] StableAccArg) facts)
    {
        var resetInfo = new PendingTcoReset(
            tco,
            newArgTemps,
            newArgTypes,
            newArgTemps.Select(IsRuntimeManagedResultTemp).ToArray(),
            collectedArgs.Select(argument => argument is Expr.Var variable
                && Lookup(variable.Name) is Binding.Local local
                && tco.ParamSlots.Contains(local.Slot)).ToArray(),
            facts.PassThrough,
            facts.SingleFreshCons,
            facts.FreshListRebuild,
            tco.ParamSlots.Select(slot => tco.ParamFacts[slot].ConsumedListTail).ToArray(),
            facts.StableAccArg,
            oldRuntimeParamTemps,
            tco.ParamSlots.Select(slot => tco.ParamPlacements[slot].Current).ToArray(),
            tco.ParamSlots.Select(slot => tco.RuntimeManagedParamActiveSlots.GetValueOrDefault(slot, -1)).ToArray(),
            tco.ParamSlots.Select(slot => tco.RuntimeManagedClosureActiveSlots.GetValueOrDefault(slot, -1)).ToArray(),
            iterationOwnedDrops,
            tco.ParamSlots.ToArray(),
            tco.FixedCursorSlot,
            tco.FixedEndSlot,
            tco.ArenaCursorSlot,
            tco.ArenaEndSlot,
            tco.CoroutineLoopReset,
            tco.CompactionSizeSlot,
            Enumerable.Range(0, collectedArgs.Count).Select(k =>
                k < tco.ParamSlots.Count && tco.AffineResvSlots.TryGetValue(tco.ParamSlots[k], out var rp) ? rp.Start : -1).ToArray(),
            Enumerable.Range(0, collectedArgs.Count).Select(k =>
                k < tco.ParamSlots.Count && tco.AffineResvSlots.TryGetValue(tco.ParamSlots[k], out var rq) ? rq.End : -1).ToArray());

        // Every reset is resolved after the complete lambda body has been lowered. A later sibling
        // branch can promote additional parameters to runtime RC; emitting an earlier branch here
        // would freeze an incomplete ownership set and let arena pointers escape across its reset.
        int pendingId = _nextTcoResetId++;
        _pendingTcoResets[pendingId] = resetInfo;
        Emit(new IrInst.TcoResetPending(
            pendingId,
            PendingTcoResetUsedTemps(resetInfo),
            PendingTcoResetReadLocalSlots(resetInfo)));
    }

    private static int[] PendingTcoResetUsedTemps(PendingTcoReset info)
        => [.. info.ArgTemps, .. info.OldRuntimeParamTemps];

    private static int[] PendingTcoResetReadLocalSlots(PendingTcoReset info)
    {
        var slots = new HashSet<int>();
        AddPendingTcoResetSlots(slots, info.ParamSlots);
        AddPendingTcoResetSlots(slots, info.RuntimeManagedParamActiveSlots);
        AddPendingTcoResetSlots(slots, info.RuntimeManagedClosureActiveSlots);
        AddPendingTcoResetSlots(slots, info.IterationOwnedDrops.Select(owned => owned.Slot));
        AddPendingTcoResetSlots(slots, info.ArgResvStartSlots);
        AddPendingTcoResetSlots(slots, info.ArgResvEndSlots);
        AddPendingTcoResetSlots(slots,
            [info.FixedCursorSlot, info.FixedEndSlot, info.ArenaCursorSlot, info.ArenaEndSlot, info.CompactionSizeSlot]);
        return [.. slots];
    }

    private static void AddPendingTcoResetSlots(HashSet<int> destination, IEnumerable<int> slots)
    {
        foreach (int slot in slots)
        {
            if (slot >= 0)
            {
                destination.Add(slot);
            }
        }
    }

    // Async-loop helper call site: the helper's closure returns a transparent coroutine task, so
    // a saturated call awaits it implicitly and yields the helper body's own type — source-level
    // transparency for `let recursive` loops with awaits. (Self tail calls were already taken by
    // the TCO branch above and restart the coroutine in place.)
    private (int, TypeRef)? LowerCallTryCoroutineHelperForm(Expr.Call call, Expr rootExpr, List<Expr> collectedArgs)
    {
        if (rootExpr is Expr.Var helperVar
            && _coroutineHelperArity.TryGetValue(helperVar.Name, out int helperArity)
            && collectedArgs.Count == helperArity
            && Lookup(helperVar.Name) is Binding.Local or Binding.Env or Binding.EnvScheme or Binding.Scheme or Binding.Self)
        {
            _coroutineHelperArity.Remove(helperVar.Name);
            var (helperTaskTemp, helperTaskType) = LowerCall(
                call,
                LoweredValueRequest.None);
            _coroutineHelperArity[helperVar.Name] = helperArity;

            _usesAsync = true;
            int helperResultTemp = NewTemp();
            Emit(_inCoroutineBody
                ? new IrInst.AwaitTask(helperResultTemp, helperTaskTemp)
                : new IrInst.RunTask(helperResultTemp, helperTaskTemp));
            var helperSuccessType = Prune(helperTaskType) is TypeRef.TNamedType { TypeArgs.Count: 2 } taskNamed
                ? taskNamed.TypeArgs[1]
                : NewTypeVar();
            return (helperResultTemp, helperSuccessType);
        }

        return null;
    }

    private (int, TypeRef) LowerCallIntrinsic(
        Expr rootExpr,
        Binding.Intrinsic intrinsic,
        List<Expr> collectedArgs,
        LoweredValueRequest request)
    {
        int expectedArity = GetIntrinsicArity(intrinsic.Kind);
        if (collectedArgs.Count != expectedArity)
        {
            return ReportArityMismatch(rootExpr, expectedArity, collectedArgs.Count);
        }

        if (AmbientCapabilityForIntrinsic(intrinsic.Kind) is { } ambientCapability)
        {
            RequireBuiltinCapability(ambientCapability, GetSpan(rootExpr));
        }

        // The dispatch is split into ordered groups; each group falls through (null) to the next.
        return LowerCallIntrinsicIoText(intrinsic.Kind, collectedArgs, request)
            ?? LowerCallIntrinsicNetBytes(intrinsic.Kind, collectedArgs, request)
            ?? LowerCallIntrinsicMathProcess(intrinsic.Kind, collectedArgs)
            ?? throw new NotSupportedException($"Unknown intrinsic: {intrinsic.Kind}");
    }

    private static string? AmbientCapabilityForIntrinsic(IntrinsicKind kind) => kind switch
    {
        IntrinsicKind.Print or IntrinsicKind.Panic or IntrinsicKind.Write or IntrinsicKind.WriteBytes
            or IntrinsicKind.WriteLine or IntrinsicKind.WriteBuffered or IntrinsicKind.WriteBufferedLine
            or IntrinsicKind.FlushStdout or IntrinsicKind.ReadLine or IntrinsicKind.ReadExact
            or IntrinsicKind.ConsoleEnableRaw or IntrinsicKind.ConsoleRestore or IntrinsicKind.ConsolePoll
            => ConsoleIoCapabilityName,
        IntrinsicKind.FileReadText or IntrinsicKind.FileReadAllBytes or IntrinsicKind.FileMmap
            or IntrinsicKind.FileExists or IntrinsicKind.FileOpen => FileReadCapabilityName,
        IntrinsicKind.FileWriteText or IntrinsicKind.FileWriteBytes => FileWriteCapabilityName,
        IntrinsicKind.SpawnProcess => ProcessSpawnCapabilityName,
        IntrinsicKind.ConsoleMonotonicMillis => TimeReadCapabilityName,
        IntrinsicKind.FfiCopyBytes => UnsafeFfiCapabilityName,
        _ => null,
    };

    private (int, TypeRef)? LowerCallIntrinsicIoText(
        IntrinsicKind kind,
        List<Expr> collectedArgs,
        LoweredValueRequest request) => kind switch
        {
            IntrinsicKind.Print => LowerPrint(collectedArgs[0]),
            IntrinsicKind.Write => LowerWrite(collectedArgs[0], appendNewline: false),
            IntrinsicKind.WriteBytes => LowerWriteBytes(collectedArgs[0]),
            IntrinsicKind.WriteLine => LowerWrite(collectedArgs[0], appendNewline: true),
            IntrinsicKind.WriteBuffered => LowerBufferedWrite(collectedArgs[0], appendNewline: false),
            IntrinsicKind.WriteBufferedLine => LowerBufferedWrite(collectedArgs[0], appendNewline: true),
            IntrinsicKind.FlushStdout => LowerFlushStdout(collectedArgs[0]),
            IntrinsicKind.ReadLine => LowerReadLine(collectedArgs[0]),
            IntrinsicKind.FileReadText => LowerFileReadText(collectedArgs[0]),
            IntrinsicKind.FileReadAllBytes => LowerFileReadAllBytes(collectedArgs[0]),
            IntrinsicKind.FileMmap => LowerFileMmap(collectedArgs[0]),
            IntrinsicKind.FileOpen => LowerFileOpen(collectedArgs[0]),
            IntrinsicKind.FileReadChunk => LowerFileReadChunk(collectedArgs[0], collectedArgs[1]),
            IntrinsicKind.FileReadLine => LowerFileReadLine(collectedArgs[0]),
            IntrinsicKind.FileClose => LowerFileClose(collectedArgs[0]),
            IntrinsicKind.InternalDeepCopy => LowerInternalDeepCopy(collectedArgs[0]),
            IntrinsicKind.ParallelBoth => LowerParallelBoth(collectedArgs[0], collectedArgs[1]),
            IntrinsicKind.ParallelWithWorkers => LowerParallelWithWorkers(collectedArgs[0], collectedArgs[1]),
            IntrinsicKind.FileWriteText => LowerFileWriteText(collectedArgs[0], collectedArgs[1]),
            IntrinsicKind.FileExists => LowerFileExists(collectedArgs[0]),
            IntrinsicKind.TextUncons => LowerTextUncons(collectedArgs[0], request),
            IntrinsicKind.TextUnconsText => LowerTextUnconsText(collectedArgs[0], request),
            IntrinsicKind.RuneToText => LowerRuneToText(collectedArgs[0], request),
            IntrinsicKind.RuneToInt => LowerRuneToInt(collectedArgs[0]),
            IntrinsicKind.RuneFromInt => LowerRuneFromInt(collectedArgs[0], request),
            IntrinsicKind.RuneIsAsciiLetter or IntrinsicKind.RuneIsAsciiDigit or IntrinsicKind.RuneIsAsciiWhiteSpace
                => LowerRunePredicate(collectedArgs[0], kind),
            IntrinsicKind.RegexCompile => LowerRegexCompile(collectedArgs[0]),
            IntrinsicKind.RegexCompileError => LowerRegexCompileError(collectedArgs[0]),
            IntrinsicKind.RegexFind => LowerRegexFind(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
            IntrinsicKind.RegexCaptures => LowerRegexCaptures(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
            IntrinsicKind.RegexSubstitute => LowerRegexSubstitute(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
            IntrinsicKind.TextParseInt => LowerTextParseInt(collectedArgs[0], request),
            IntrinsicKind.TextParseFloat => LowerTextParseFloat(collectedArgs[0], request),
            IntrinsicKind.TextFromInt => LowerTextFromInt(collectedArgs[0], request),
            IntrinsicKind.TextFromFloat => LowerTextFromFloat(collectedArgs[0], request),
            IntrinsicKind.TextFormatFloat => LowerTextFormatFloat(collectedArgs[0], collectedArgs[1], request),
            IntrinsicKind.BigIntFromInt => LowerBigIntFromInt(collectedArgs[0], request),
            IntrinsicKind.BigIntToString => LowerBigIntToString(collectedArgs[0], request),
            IntrinsicKind.BigIntToInt => LowerBigIntToInt(collectedArgs[0], request),
            IntrinsicKind.BigIntFromString => LowerBigIntFromString(collectedArgs[0], request),
            IntrinsicKind.BigIntAdd => LowerBigIntBinary(collectedArgs[0], collectedArgs[1], "add", "Ashes.Number.BigInt.add()", false, request),
            IntrinsicKind.BigIntSub => LowerBigIntBinary(collectedArgs[0], collectedArgs[1], "sub", "Ashes.Number.BigInt.sub()", false, request),
            IntrinsicKind.BigIntMul => LowerBigIntBinary(collectedArgs[0], collectedArgs[1], "mul", "Ashes.Number.BigInt.mul()", false, request),
            IntrinsicKind.BigIntDiv => LowerBigIntBinary(collectedArgs[0], collectedArgs[1], "div", "Ashes.Number.BigInt.div()", false, request),
            IntrinsicKind.BigIntMod => LowerBigIntBinary(collectedArgs[0], collectedArgs[1], "mod", "Ashes.Number.BigInt.mod()", false, request),
            IntrinsicKind.BigIntCompare => LowerBigIntBinary(collectedArgs[0], collectedArgs[1], "cmp", "Ashes.Number.BigInt.compare()", true),
            IntrinsicKind.TextToHex => LowerTextToHex(collectedArgs[0], request),
            IntrinsicKind.TextAsciiUpper => LowerTextAsciiCase(collectedArgs[0], upper: true, request),
            IntrinsicKind.TextAsciiLower => LowerTextAsciiCase(collectedArgs[0], upper: false, request),
            _ => null,
        };

    private (int, TypeRef)? LowerCallIntrinsicNetBytes(
        IntrinsicKind kind,
        List<Expr> collectedArgs,
        LoweredValueRequest request) => kind switch
        {
            IntrinsicKind.HttpGet => LowerHttpGet(collectedArgs[0]),
            IntrinsicKind.HttpPost => LowerHttpPost(collectedArgs[0], collectedArgs[1]),
            IntrinsicKind.NetTcpConnect => LowerNetTcpConnect(collectedArgs[0], collectedArgs[1]),
            IntrinsicKind.NetTcpSend => LowerNetTcpSend(collectedArgs[0], collectedArgs[1]),
            IntrinsicKind.NetTcpReceive => LowerNetTcpReceive(collectedArgs[0], collectedArgs[1]),
            IntrinsicKind.NetTcpClose => LowerNetTcpClose(collectedArgs[0]),
            IntrinsicKind.NetTcpListen => LowerNetTcpListen(collectedArgs[0]),
            IntrinsicKind.NetForkWorkers => LowerNetForkWorkers(collectedArgs[0], collectedArgs[1]),
            IntrinsicKind.NetSetDrainTimeout => LowerNetSetDrainTimeout(collectedArgs[0]),
            IntrinsicKind.NetTcpAccept => LowerNetTcpAccept(collectedArgs[0]),
            IntrinsicKind.NetTlsConnect => LowerNetTlsConnect(collectedArgs[0], collectedArgs[1]),
            IntrinsicKind.NetTlsSend => LowerNetTlsSend(collectedArgs[0], collectedArgs[1]),
            IntrinsicKind.NetTlsReceive => LowerNetTlsReceive(collectedArgs[0], collectedArgs[1]),
            IntrinsicKind.NetTlsClose => LowerNetTlsClose(collectedArgs[0]),
            IntrinsicKind.NetTlsServerHandshake => LowerNetTlsServerHandshake(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
            IntrinsicKind.Panic => LowerPanic(collectedArgs[0]),
            IntrinsicKind.AsyncRun => LowerAsyncRun(collectedArgs[0]),
            IntrinsicKind.AsyncTask => LowerAsyncTask(collectedArgs[0]),
            IntrinsicKind.AsyncFromResult => LowerAsyncFromResult(collectedArgs[0]),
            IntrinsicKind.AsyncSleep => LowerAsyncSleep(collectedArgs[0]),
            IntrinsicKind.AsyncSpawn => LowerAsyncSpawn(collectedArgs[0]),
            IntrinsicKind.AsyncAll => LowerAsyncAll(collectedArgs[0]),
            IntrinsicKind.AsyncRace => LowerAsyncRace(collectedArgs[0]),
            IntrinsicKind.AsyncScope => LowerAsyncScope(collectedArgs[0]),
            IntrinsicKind.AsyncFork => LowerAsyncFork(collectedArgs[0]),
            IntrinsicKind.AsyncJoin => LowerAsyncJoin(collectedArgs[0]),
            IntrinsicKind.BytesEmpty => LowerBytesEmpty(collectedArgs[0], request),
            IntrinsicKind.FfiCopyBytes => LowerFfiCopyBytes(collectedArgs[0], collectedArgs[1]),
            IntrinsicKind.BytesSingleton => LowerBytesSingleton(collectedArgs[0], request),
            IntrinsicKind.BytesLength => LowerBytesLength(collectedArgs[0]),
            IntrinsicKind.BytesGet => LowerBytesGet(collectedArgs[0], collectedArgs[1]),
            IntrinsicKind.BytesIndexOf => LowerBytesIndexOf(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
            IntrinsicKind.BytesCompare => LowerBytesCompare(collectedArgs[0], collectedArgs[1]),
            IntrinsicKind.BytesScanHash => LowerBytesScanHash(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
            IntrinsicKind.BytesSubText => LowerBytesSubText(collectedArgs[0], collectedArgs[1], collectedArgs[2], request),
            IntrinsicKind.BytesSubView => LowerBytesSubView(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
            IntrinsicKind.BytesAppend => LowerBytesAppend(collectedArgs[0], collectedArgs[1], request),
            IntrinsicKind.BytesAppendByte => LowerBytesAppendByte(collectedArgs[0], collectedArgs[1], request),
            IntrinsicKind.BytesAllocate => LowerBytesAllocate(collectedArgs[0]),
            IntrinsicKind.BytesCopyRange => LowerBytesCopyRange(collectedArgs[0], collectedArgs[1], collectedArgs[2], collectedArgs[3], collectedArgs[4]),
            IntrinsicKind.BytesSet => LowerBytesSet(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
            IntrinsicKind.BytesSetU16Le => LowerBytesSetU16Le(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
            IntrinsicKind.BytesSetU32Le => LowerBytesSetU32Le(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
            IntrinsicKind.BytesSetU64Le => LowerBytesSetU64Le(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
            IntrinsicKind.BytesFromList => LowerBytesFromList(collectedArgs[0], request),
            IntrinsicKind.BytesFromText => LowerBytesFromText(collectedArgs[0]),
            IntrinsicKind.BytesHash => LowerBytesHash(collectedArgs[0]),
            IntrinsicKind.BytesU16Le => LowerBytesU16Le(collectedArgs[0], request),
            IntrinsicKind.BytesU32Le => LowerBytesU32Le(collectedArgs[0], request),
            IntrinsicKind.BytesU64Le => LowerBytesU64Le(collectedArgs[0], request),
            IntrinsicKind.BytesGetU16Le => LowerBytesGetU16Le(collectedArgs[0], collectedArgs[1]),
            IntrinsicKind.BytesGetU32Le => LowerBytesGetU32Le(collectedArgs[0], collectedArgs[1]),
            IntrinsicKind.BytesGetU64Le => LowerBytesGetU64Le(collectedArgs[0], collectedArgs[1]),
            _ => null,
        };

    private (int, TypeRef)? LowerCallIntrinsicMathProcess(IntrinsicKind kind, List<Expr> collectedArgs) => kind switch
    {
        IntrinsicKind.UIntToInt => LowerUIntToInt(collectedArgs[0]),
        IntrinsicKind.UIntFromInt => LowerUIntFromInt(collectedArgs[0]),
        IntrinsicKind.MathToFloat => LowerMathToFloat(collectedArgs[0]),
        IntrinsicKind.MathSqrt => LowerMathFloatUnary(collectedArgs[0], "Ashes.Number.Math.sqrt", "llvm.sqrt.f64"),
        IntrinsicKind.MathFloor => LowerMathFloatUnary(collectedArgs[0], "Ashes.Number.Math.floor", "llvm.floor.f64"),
        IntrinsicKind.MathCeil => LowerMathFloatUnary(collectedArgs[0], "Ashes.Number.Math.ceil", "llvm.ceil.f64"),
        IntrinsicKind.MathRound => LowerMathFloatUnary(collectedArgs[0], "Ashes.Number.Math.round", "llvm.round.f64"),
        IntrinsicKind.MathTrunc => LowerMathFloatUnary(collectedArgs[0], "Ashes.Number.Math.trunc", "llvm.trunc.f64"),
        IntrinsicKind.MathFloorToInt => LowerMathFloatToInt(collectedArgs[0], "Ashes.Number.Math.floorToInt", "llvm.floor.f64"),
        IntrinsicKind.MathRoundToInt => LowerMathFloatToInt(collectedArgs[0], "Ashes.Number.Math.roundToInt", "llvm.round.f64"),
        IntrinsicKind.MathTruncToInt => LowerMathFloatToInt(collectedArgs[0], "Ashes.Number.Math.truncToInt", null),
        IntrinsicKind k when LibmIntrinsics.ContainsKey(k) => LowerLibm(k, collectedArgs),
        IntrinsicKind.FileWriteBytes => LowerFileWriteBytes(collectedArgs[0], collectedArgs[1]),
        IntrinsicKind.ReadExact => LowerReadExact(collectedArgs[0]),
        IntrinsicKind.ConsoleEnableRaw => LowerConsoleEnableRaw(collectedArgs[0]),
        IntrinsicKind.ConsoleRestore => LowerConsoleRestore(collectedArgs[0]),
        IntrinsicKind.ConsolePoll => LowerConsolePoll(collectedArgs[0]),
        IntrinsicKind.ConsoleMonotonicMillis => LowerConsoleMonotonicMillis(collectedArgs[0]),
        IntrinsicKind.TextByteLength => LowerTextByteLength(collectedArgs[0]),
        IntrinsicKind.SpawnProcess => LowerSpawnProcess(collectedArgs[0], collectedArgs[1]),
        IntrinsicKind.ProcessWriteStdin => LowerProcessWriteStdin(collectedArgs[0], collectedArgs[1]),
        IntrinsicKind.ProcessReadStdoutLine => LowerProcessReadStdoutLine(collectedArgs[0]),
        IntrinsicKind.ProcessReadStderrLine => LowerProcessReadStderrLine(collectedArgs[0]),
        IntrinsicKind.ProcessWaitForExit => LowerProcessWaitForExit(collectedArgs[0]),
        IntrinsicKind.ProcessKill => LowerProcessKill(collectedArgs[0]),
        _ => null,
    };

    private (int, TypeRef)? LowerCallQualifiedBuiltin(
        Expr rootExpr,
        Expr.QualifiedVar qv,
        List<Expr> collectedArgs,
        LoweredValueRequest request)
    {
        var resolvedModule = ResolveModuleAlias(qv.Module);
        if (!BuiltinRegistry.TryGetModule(resolvedModule, out var builtinModule)
            || !builtinModule.Members.TryGetValue(qv.Name, out var builtinMember))
        {
            return null;
        }

        if (!builtinMember.IsCallable)
        {
            ReportDiagnostic(GetSpan(qv), $"'{resolvedModule}.{qv.Name}' is not callable.");
            return ReturnNeverWithDummyTemp();
        }

        if (collectedArgs.Count != builtinMember.Arity)
        {
            return ReportArityMismatch(rootExpr, builtinMember.Arity, collectedArgs.Count);
        }

        TypeRef functionType = ResolveBuiltinModuleMember(builtinModule, qv.Name).Item2;
        RecordHoverType(
            GetSpan(qv),
            $"{resolvedModule}.{qv.Name}",
            functionType);

        if (AmbientCapabilityForBuiltin(builtinMember.Kind) is { } ambientCapability)
        {
            RequireBuiltinCapability(ambientCapability, GetSpan(rootExpr));
        }

        // The dispatch is split into ordered groups; each group falls through (null) to the next.
        return LowerCallBuiltinIoText(builtinMember.Kind, collectedArgs, request)
            ?? LowerCallBuiltinNetBytes(builtinMember.Kind, collectedArgs, request)
            ?? LowerCallBuiltinMathProcess(builtinMember.Kind, collectedArgs)
            ?? StdMemberNotFound(resolvedModule, qv.Name);
    }

    private static string? AmbientCapabilityForBuiltin(BuiltinRegistry.BuiltinValueKind kind) => kind switch
    {
        BuiltinRegistry.BuiltinValueKind.Print or BuiltinRegistry.BuiltinValueKind.Panic
            or BuiltinRegistry.BuiltinValueKind.Write or BuiltinRegistry.BuiltinValueKind.IoWriteBytes
            or BuiltinRegistry.BuiltinValueKind.WriteLine or BuiltinRegistry.BuiltinValueKind.WriteBuffered
            or BuiltinRegistry.BuiltinValueKind.WriteBufferedLine or BuiltinRegistry.BuiltinValueKind.FlushStdout
            or BuiltinRegistry.BuiltinValueKind.ReadLine
            or BuiltinRegistry.BuiltinValueKind.IoReadExact
            or BuiltinRegistry.BuiltinValueKind.ConsoleEnableRaw
            or BuiltinRegistry.BuiltinValueKind.ConsoleRestore
            or BuiltinRegistry.BuiltinValueKind.ConsolePoll => ConsoleIoCapabilityName,
        BuiltinRegistry.BuiltinValueKind.FileReadText
            or BuiltinRegistry.BuiltinValueKind.FileReadAllBytes
            or BuiltinRegistry.BuiltinValueKind.FileMmap
            or BuiltinRegistry.BuiltinValueKind.FileExists
            or BuiltinRegistry.BuiltinValueKind.FileOpen => FileReadCapabilityName,
        BuiltinRegistry.BuiltinValueKind.FileWriteText
            or BuiltinRegistry.BuiltinValueKind.FileWriteBytes => FileWriteCapabilityName,
        BuiltinRegistry.BuiltinValueKind.SpawnProcess => ProcessSpawnCapabilityName,
        BuiltinRegistry.BuiltinValueKind.ConsoleMonotonicMillis => TimeReadCapabilityName,
        BuiltinRegistry.BuiltinValueKind.FfiCopyBytes => UnsafeFfiCapabilityName,
        _ => null,
    };

    private (int, TypeRef)? LowerCallBuiltinIoText(
        BuiltinRegistry.BuiltinValueKind kind,
        List<Expr> collectedArgs,
        LoweredValueRequest request) => kind switch
        {
            BuiltinRegistry.BuiltinValueKind.Print => LowerPrint(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.Panic => LowerPanic(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.Write => LowerWrite(collectedArgs[0], appendNewline: false),
            BuiltinRegistry.BuiltinValueKind.IoWriteBytes => LowerWriteBytes(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.WriteLine => LowerWrite(collectedArgs[0], appendNewline: true),
            BuiltinRegistry.BuiltinValueKind.WriteBuffered => LowerBufferedWrite(collectedArgs[0], appendNewline: false),
            BuiltinRegistry.BuiltinValueKind.WriteBufferedLine => LowerBufferedWrite(collectedArgs[0], appendNewline: true),
            BuiltinRegistry.BuiltinValueKind.FlushStdout => LowerFlushStdout(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.ReadLine => LowerReadLine(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.FileReadText => LowerFileReadText(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.FileReadAllBytes => LowerFileReadAllBytes(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.FileMmap => LowerFileMmap(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.FileOpen => LowerFileOpen(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.FileReadChunk => LowerFileReadChunk(collectedArgs[0], collectedArgs[1]),
            BuiltinRegistry.BuiltinValueKind.FileReadLine => LowerFileReadLine(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.FileClose => LowerFileClose(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.InternalDeepCopy => LowerInternalDeepCopy(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.ParallelBoth => LowerParallelBoth(collectedArgs[0], collectedArgs[1]),
            BuiltinRegistry.BuiltinValueKind.ParallelWithWorkers => LowerParallelWithWorkers(collectedArgs[0], collectedArgs[1]),
            BuiltinRegistry.BuiltinValueKind.FileWriteText => LowerFileWriteText(collectedArgs[0], collectedArgs[1]),
            BuiltinRegistry.BuiltinValueKind.FileExists => LowerFileExists(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.TextUncons => LowerTextUncons(collectedArgs[0], request),
            BuiltinRegistry.BuiltinValueKind.TextUnconsText => LowerTextUnconsText(collectedArgs[0], request),
            BuiltinRegistry.BuiltinValueKind.RuneToText => LowerRuneToText(collectedArgs[0], request),
            BuiltinRegistry.BuiltinValueKind.RuneToInt => LowerRuneToInt(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.RuneFromInt => LowerRuneFromInt(collectedArgs[0], request),
            BuiltinRegistry.BuiltinValueKind.RuneIsAsciiLetter => LowerRunePredicate(collectedArgs[0], IntrinsicKind.RuneIsAsciiLetter),
            BuiltinRegistry.BuiltinValueKind.RuneIsAsciiDigit => LowerRunePredicate(collectedArgs[0], IntrinsicKind.RuneIsAsciiDigit),
            BuiltinRegistry.BuiltinValueKind.RuneIsAsciiWhiteSpace => LowerRunePredicate(collectedArgs[0], IntrinsicKind.RuneIsAsciiWhiteSpace),
            BuiltinRegistry.BuiltinValueKind.RegexCompile => LowerRegexCompile(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.RegexCompileError => LowerRegexCompileError(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.RegexFind => LowerRegexFind(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
            BuiltinRegistry.BuiltinValueKind.RegexCaptures => LowerRegexCaptures(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
            BuiltinRegistry.BuiltinValueKind.RegexSubstitute => LowerRegexSubstitute(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
            BuiltinRegistry.BuiltinValueKind.TextParseInt => LowerTextParseInt(collectedArgs[0], request),
            BuiltinRegistry.BuiltinValueKind.TextParseFloat => LowerTextParseFloat(collectedArgs[0], request),
            BuiltinRegistry.BuiltinValueKind.TextFromInt => LowerTextFromInt(collectedArgs[0], request),
            BuiltinRegistry.BuiltinValueKind.TextFromFloat => LowerTextFromFloat(collectedArgs[0], request),
            BuiltinRegistry.BuiltinValueKind.TextFormatFloat => LowerTextFormatFloat(collectedArgs[0], collectedArgs[1], request),
            BuiltinRegistry.BuiltinValueKind.BigIntFromInt => LowerBigIntFromInt(collectedArgs[0], request),
            BuiltinRegistry.BuiltinValueKind.BigIntToString => LowerBigIntToString(collectedArgs[0], request),
            BuiltinRegistry.BuiltinValueKind.BigIntToInt => LowerBigIntToInt(collectedArgs[0], request),
            BuiltinRegistry.BuiltinValueKind.BigIntFromString => LowerBigIntFromString(collectedArgs[0], request),
            BuiltinRegistry.BuiltinValueKind.BigIntAdd => LowerBigIntBinary(collectedArgs[0], collectedArgs[1], "add", "Ashes.Number.BigInt.add()", false, request),
            BuiltinRegistry.BuiltinValueKind.BigIntSub => LowerBigIntBinary(collectedArgs[0], collectedArgs[1], "sub", "Ashes.Number.BigInt.sub()", false, request),
            BuiltinRegistry.BuiltinValueKind.BigIntMul => LowerBigIntBinary(collectedArgs[0], collectedArgs[1], "mul", "Ashes.Number.BigInt.mul()", false, request),
            BuiltinRegistry.BuiltinValueKind.BigIntDiv => LowerBigIntBinary(collectedArgs[0], collectedArgs[1], "div", "Ashes.Number.BigInt.div()", false, request),
            BuiltinRegistry.BuiltinValueKind.BigIntMod => LowerBigIntBinary(collectedArgs[0], collectedArgs[1], "mod", "Ashes.Number.BigInt.mod()", false, request),
            BuiltinRegistry.BuiltinValueKind.BigIntCompare => LowerBigIntBinary(collectedArgs[0], collectedArgs[1], "cmp", "Ashes.Number.BigInt.compare()", true),
            BuiltinRegistry.BuiltinValueKind.TextToHex => LowerTextToHex(collectedArgs[0], request),
            BuiltinRegistry.BuiltinValueKind.TextAsciiUpper => LowerTextAsciiCase(collectedArgs[0], upper: true, request),
            BuiltinRegistry.BuiltinValueKind.TextAsciiLower => LowerTextAsciiCase(collectedArgs[0], upper: false, request),
            _ => null,
        };

    private (int, TypeRef)? LowerCallBuiltinNetBytes(
        BuiltinRegistry.BuiltinValueKind kind,
        List<Expr> collectedArgs,
        LoweredValueRequest request) => kind switch
        {
            BuiltinRegistry.BuiltinValueKind.HttpGet => LowerHttpGet(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.HttpPost => LowerHttpPost(collectedArgs[0], collectedArgs[1]),
            BuiltinRegistry.BuiltinValueKind.NetTcpConnect => LowerNetTcpConnect(collectedArgs[0], collectedArgs[1]),
            BuiltinRegistry.BuiltinValueKind.NetTcpSend => LowerNetTcpSend(collectedArgs[0], collectedArgs[1]),
            BuiltinRegistry.BuiltinValueKind.NetTcpReceive => LowerNetTcpReceive(collectedArgs[0], collectedArgs[1]),
            BuiltinRegistry.BuiltinValueKind.NetTcpClose => LowerNetTcpClose(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.NetTcpListen => LowerNetTcpListen(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.NetTcpForkWorkers => LowerNetForkWorkers(collectedArgs[0], collectedArgs[1]),
            BuiltinRegistry.BuiltinValueKind.NetTcpSetDrainTimeout => LowerNetSetDrainTimeout(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.NetTcpAccept => LowerNetTcpAccept(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.NetTlsConnect => LowerNetTlsConnect(collectedArgs[0], collectedArgs[1]),
            BuiltinRegistry.BuiltinValueKind.NetTlsSend => LowerNetTlsSend(collectedArgs[0], collectedArgs[1]),
            BuiltinRegistry.BuiltinValueKind.NetTlsReceive => LowerNetTlsReceive(collectedArgs[0], collectedArgs[1]),
            BuiltinRegistry.BuiltinValueKind.NetTlsClose => LowerNetTlsClose(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.NetTlsServerHandshake => LowerNetTlsServerHandshake(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
            BuiltinRegistry.BuiltinValueKind.AsyncRun => LowerAsyncRun(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.AsyncTask => LowerAsyncTask(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.AsyncFromResult => LowerAsyncFromResult(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.AsyncSleep => LowerAsyncSleep(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.AsyncSpawn => LowerAsyncSpawn(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.AsyncAll => LowerAsyncAll(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.AsyncRace => LowerAsyncRace(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.AsyncScope => LowerAsyncScope(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.AsyncFork => LowerAsyncFork(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.AsyncJoin => LowerAsyncJoin(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.BytesEmpty => LowerBytesEmpty(collectedArgs[0], request),
            BuiltinRegistry.BuiltinValueKind.FfiCopyBytes => LowerFfiCopyBytes(collectedArgs[0], collectedArgs[1]),
            BuiltinRegistry.BuiltinValueKind.BytesSingleton => LowerBytesSingleton(collectedArgs[0], request),
            BuiltinRegistry.BuiltinValueKind.BytesLength => LowerBytesLength(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.BytesGet => LowerBytesGet(collectedArgs[0], collectedArgs[1]),
            BuiltinRegistry.BuiltinValueKind.BytesIndexOf => LowerBytesIndexOf(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
            BuiltinRegistry.BuiltinValueKind.BytesCompare => LowerBytesCompare(collectedArgs[0], collectedArgs[1]),
            BuiltinRegistry.BuiltinValueKind.BytesScanHash => LowerBytesScanHash(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
            BuiltinRegistry.BuiltinValueKind.BytesSubText => LowerBytesSubText(collectedArgs[0], collectedArgs[1], collectedArgs[2], request),
            BuiltinRegistry.BuiltinValueKind.BytesSubView => LowerBytesSubView(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
            BuiltinRegistry.BuiltinValueKind.BytesAppend => LowerBytesAppend(collectedArgs[0], collectedArgs[1], request),
            BuiltinRegistry.BuiltinValueKind.BytesAppendByte => LowerBytesAppendByte(collectedArgs[0], collectedArgs[1], request),
            BuiltinRegistry.BuiltinValueKind.BytesAllocate => LowerBytesAllocate(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.BytesCopyRange => LowerBytesCopyRange(collectedArgs[0], collectedArgs[1], collectedArgs[2], collectedArgs[3], collectedArgs[4]),
            BuiltinRegistry.BuiltinValueKind.BytesSet => LowerBytesSet(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
            BuiltinRegistry.BuiltinValueKind.BytesSetU16Le => LowerBytesSetU16Le(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
            BuiltinRegistry.BuiltinValueKind.BytesSetU32Le => LowerBytesSetU32Le(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
            BuiltinRegistry.BuiltinValueKind.BytesSetU64Le => LowerBytesSetU64Le(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
            BuiltinRegistry.BuiltinValueKind.BytesFromList => LowerBytesFromList(collectedArgs[0], request),
            BuiltinRegistry.BuiltinValueKind.BytesFromText => LowerBytesFromText(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.BytesHash => LowerBytesHash(collectedArgs[0]),
            BuiltinRegistry.BuiltinValueKind.BytesU16Le => LowerBytesU16Le(collectedArgs[0], request),
            BuiltinRegistry.BuiltinValueKind.BytesU32Le => LowerBytesU32Le(collectedArgs[0], request),
            BuiltinRegistry.BuiltinValueKind.BytesU64Le => LowerBytesU64Le(collectedArgs[0], request),
            BuiltinRegistry.BuiltinValueKind.BytesGetU16Le => LowerBytesGetU16Le(collectedArgs[0], collectedArgs[1]),
            BuiltinRegistry.BuiltinValueKind.BytesGetU32Le => LowerBytesGetU32Le(collectedArgs[0], collectedArgs[1]),
            BuiltinRegistry.BuiltinValueKind.BytesGetU64Le => LowerBytesGetU64Le(collectedArgs[0], collectedArgs[1]),
            _ => null,
        };

    private (int, TypeRef)? LowerCallBuiltinMathProcess(BuiltinRegistry.BuiltinValueKind kind, List<Expr> collectedArgs) => kind switch
    {
        BuiltinRegistry.BuiltinValueKind.UIntToInt => LowerUIntToInt(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.UIntFromInt => LowerUIntFromInt(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.MathToFloat => LowerMathToFloat(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.MathSqrt => LowerMathFloatUnary(collectedArgs[0], "Ashes.Number.Math.sqrt", "llvm.sqrt.f64"),
        BuiltinRegistry.BuiltinValueKind.MathFloor => LowerMathFloatUnary(collectedArgs[0], "Ashes.Number.Math.floor", "llvm.floor.f64"),
        BuiltinRegistry.BuiltinValueKind.MathCeil => LowerMathFloatUnary(collectedArgs[0], "Ashes.Number.Math.ceil", "llvm.ceil.f64"),
        BuiltinRegistry.BuiltinValueKind.MathRound => LowerMathFloatUnary(collectedArgs[0], "Ashes.Number.Math.round", "llvm.round.f64"),
        BuiltinRegistry.BuiltinValueKind.MathTrunc => LowerMathFloatUnary(collectedArgs[0], "Ashes.Number.Math.trunc", "llvm.trunc.f64"),
        BuiltinRegistry.BuiltinValueKind.MathFloorToInt => LowerMathFloatToInt(collectedArgs[0], "Ashes.Number.Math.floorToInt", "llvm.floor.f64"),
        BuiltinRegistry.BuiltinValueKind.MathRoundToInt => LowerMathFloatToInt(collectedArgs[0], "Ashes.Number.Math.roundToInt", "llvm.round.f64"),
        BuiltinRegistry.BuiltinValueKind.MathTruncToInt => LowerMathFloatToInt(collectedArgs[0], "Ashes.Number.Math.truncToInt", null),
        BuiltinRegistry.BuiltinValueKind k when LibmBuiltinKinds.TryGetValue(k, out var libmKind) => LowerLibm(libmKind, collectedArgs),
        BuiltinRegistry.BuiltinValueKind.FileWriteBytes => LowerFileWriteBytes(collectedArgs[0], collectedArgs[1]),
        BuiltinRegistry.BuiltinValueKind.IoReadExact => LowerReadExact(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.ConsoleEnableRaw => LowerConsoleEnableRaw(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.ConsoleRestore => LowerConsoleRestore(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.ConsolePoll => LowerConsolePoll(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.ConsoleMonotonicMillis => LowerConsoleMonotonicMillis(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.TextByteLength => LowerTextByteLength(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.SpawnProcess => LowerSpawnProcess(collectedArgs[0], collectedArgs[1]),
        BuiltinRegistry.BuiltinValueKind.ProcessWriteStdin => LowerProcessWriteStdin(collectedArgs[0], collectedArgs[1]),
        BuiltinRegistry.BuiltinValueKind.ProcessReadStdoutLine => LowerProcessReadStdoutLine(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.ProcessReadStderrLine => LowerProcessReadStderrLine(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.ProcessWaitForExit => LowerProcessWaitForExit(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.ProcessKill => LowerProcessKill(collectedArgs[0]),
        _ => null,
    };

    private (int, TypeRef) LowerCallGeneral(
        Expr.Call call,
        Expr rootExpr,
        List<Expr> collectedArgs,
        LoweredValueRequest request)
    {
        // Keep call-chain intermediates in an independent reclaimable arena window.
        int callWmCursorSlot = NewLocal();
        int callWmEndSlot = NewLocal();
        Emit(new IrInst.SaveArenaState(callWmCursorSlot, callWmEndSlot));

        // For non-TCO calls, sub-expressions are NOT in tail position
        var savedTailPos = _tcoCtx?.InTailPosition ?? false;
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        (int currentTemp, TypeRef currentType) = LowerCallRoot(rootExpr, collectedArgs);

        if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;

        currentType = Prune(currentType);
        if (currentType is TypeRef.TNever)
        {
            // Variable already diagnosed as unknown; suppress cascading type error.
            return ReturnNeverWithDummyTemp();
        }

        if (TryGetExactFunctionArity(currentType, out var expectedArgs)
            && expectedArgs > 0
            && expectedArgs < collectedArgs.Count)
        {
            return ReportArityMismatch(rootExpr, expectedArgs, collectedArgs.Count);
        }

        PreconstrainCallResultType(currentType, collectedArgs.Count, request.ExpectedType);

        List<(int Temp, TypeRef Type)> consumedRuntimeArguments = [];
        if (LowerCallApplyArgs(call, rootExpr, collectedArgs, ref currentTemp, ref currentType,
                consumedRuntimeArguments, out int runtimeManagedResultFlagTemp) is { } earlyResult)
        {
            return earlyResult;
        }

        UnifyExpectedType(currentType, request.ExpectedType);
        var callResultType = Prune(currentType);
        LowerCallDropConsumedRuntimeArguments(callResultType, consumedRuntimeArguments);
        bool runtimeManagedResult = IsDirectRuntimeManagedFunctionCall(rootExpr, collectedArgs.Count, callResultType);
        bool stableReuseResult = IsSpecializationSelfReuseCall(rootExpr);
        TrackStableReuseCallResult(currentTemp, stableReuseResult);
        CopyOutKind callResultCopyKind = GetCallCopyOutKind(callResultType, out _, out _);
        runtimeManagedResult = ResolveUncopyableResultRuntimeManaged(rootExpr, collectedArgs.Count, callResultType, callResultCopyKind, runtimeManagedResult);
        bool normalizesRuntimeManagedResult = !runtimeManagedResult
            && runtimeManagedResultFlagTemp >= 0
            && callResultCopyKind is CopyOutKind.Shallow or CopyOutKind.List;
        currentTemp = LowerCallRestoreArena(
            callWmCursorSlot,
            callWmEndSlot,
            currentTemp,
            callResultType,
            runtimeManagedResult || stableReuseResult,
            runtimeManagedResultFlagTemp);
        RecordCallResultTempOwnership(currentTemp, callResultType, runtimeManagedResult,
            normalizesRuntimeManagedResult, GetKnownFunctionBytesProvenance(rootExpr, collectedArgs.Count));

        return (currentTemp, currentType);
    }

    private (int Temp, TypeRef Type) LowerCallRoot(Expr rootExpr, IReadOnlyList<Expr> arguments)
    {
        bool hasExplicitTraitEvidence = arguments.Count > 0
            && arguments[0] is Expr.Var evidence
            && evidence.Name.StartsWith("__trait_evidence_", StringComparison.Ordinal);
        if (hasExplicitTraitEvidence)
        {
            _suppressActiveTraitDictionaryReferenceDepth++;
        }
        try
        {
            return rootExpr is Expr.Lambda lambda
                ? LowerLambda(lambda, stackAllocateClosure: true)
                : LowerExpr(rootExpr).AsPair();
        }
        finally
        {
            if (hasExplicitTraitEvidence)
            {
                _suppressActiveTraitDictionaryReferenceDepth--;
            }
        }
    }

    /// <summary>
    /// A result with no legal arena copy-out strategy (<see cref="CopyOutKind.None"/>) that also isn't a
    /// plain copy type can ONLY ever safely escape a call's reclaimed arena window by being RC —
    /// <see cref="LowerCallRestoreArena"/>'s own <c>ck==None</c> fallback does not restore or reclaim the
    /// arena watermark AT ALL (written to be reached only for values already known RC, where no action
    /// is needed because the result lives outside this window). Trusting a definite "false" here when
    /// the actual compiled representation is RC would silently skip all arena reclamation for the call,
    /// leaking its entire intermediate allocation window every time it runs (caught empirically —
    /// Linux_backend_llvm_runtime_rc_unreturned_TCO_parameter_memory_should_plateau,
    /// Linux_backend_llvm_runtime_rc_owned_head_list_TCO_memory_should_plateau's consumed-tuple-head
    /// scenario — both are TCO loops directly returning a bare accumulator parameter, a shape
    /// FunctionResultProvenance's AST-only classifier cannot recognize as fresh, but whose ACTUAL
    /// compiled accumulator representation the TCO machinery promotes to RC for exactly this reason).
    ///
    /// But blindly forcing <c>true</c> here (tried first) is exactly as unsound in the other direction:
    /// it crashed Curried_add (an ordinary, non-TCO, genuinely arena-eligible closure reaching this same
    /// <c>ck==None</c> path, where forcing "true" wrongly reclaims the arena the closure itself still
    /// lives in) and Linux_backend_llvm_one_brc_memory_stays_bounded_as_rows_scale (exit code 245) even
    /// when narrowed to TList results only. There is no sound AST-shape-only rule for this question in
    /// either direction — it depends on the ACTUAL, lowering-time representation decision, which is
    /// exactly what <see cref="_bodyRuntimeManagedByLabel"/> records (see its own doc). So: when
    /// <see cref="TryResolveKnownFunctionResultOwnership"/> did not resolve (the common case for a bare
    /// accumulator passthrough, since FunctionResultProvenance never proves those fresh) AND this result
    /// type cannot be arena-copied out, fall back to the OLD backward-scan's own resolution strategy —
    /// walk the curried label chain <paramref name="argumentCount"/> hops via
    /// <see cref="_functionReturnedClosureLabels"/> (populated by <see cref="RecordReturnedClosureLabel"/>,
    /// unrelated to and unaffected by this phase) to the innermost curry level actually compiled for this
    /// call, and read that level's OWN accurate <see cref="_bodyRuntimeManagedByLabel"/> fact directly —
    /// exactly what the retired <c>_runtimeManagedFunctionResultLabels</c> mechanism did for this exact
    /// question, before Phase 3. If the hop chain itself does not resolve (e.g. the callee is not a
    /// statically known label at all), there is no accurate fact available either way, so this
    /// conservatively leaves <paramref name="runtimeManagedResult"/> as computed (matching this
    /// predicate's own always-conservative-on-failure convention).
    /// </summary>
    private bool ResolveUncopyableResultRuntimeManaged(
        Expr rootExpr,
        int argumentCount,
        TypeRef callResultType,
        CopyOutKind callResultCopyKind,
        bool runtimeManagedResult)
    {
        if (runtimeManagedResult
            || callResultCopyKind != CopyOutKind.None
            || CanArenaReset(callResultType)
            || argumentCount == 0
            || !TryResolveKnownFunctionLabel(rootExpr, out string resultLabel))
        {
            return runtimeManagedResult;
        }

        for (int i = 1; i < argumentCount; i++)
        {
            if (!_functionReturnedClosureLabels.TryGetValue(resultLabel, out string? nextLabel))
            {
                return runtimeManagedResult;
            }

            resultLabel = nextLabel;
        }

        return _bodyRuntimeManagedByLabel.GetValueOrDefault(resultLabel, runtimeManagedResult);
    }

    private bool IsSpecializationSelfReuseCall(Expr rootExpr)
        => _inSpecialization
            && _specializingReuseLabel is not null
            && rootExpr is Expr.Var variable
            && Lookup(variable.Name) is Binding.Self self
            && string.Equals(self.FuncLabel, _specializingReuseLabel, StringComparison.Ordinal);

    private void TrackStableReuseCallResult(int resultTemp, bool stableReuseResult)
    {
        if (stableReuseResult)
        {
            _reuseResultTemps.Add(resultTemp);
        }
    }

    private bool IsDirectRuntimeManagedFunctionCall(Expr rootExpr, int argumentCount, TypeRef callResultType)
    {
        if (TryResolveKnownFunctionResultOwnership(
                rootExpr,
                argumentCount,
                callResultType,
                out bool runtimeManaged)
            && runtimeManaged)
        {
            return true;
        }

        return IsConcretelyRuntimeManageableResultType(callResultType)
            && TryResolveKnownFunctionLabel(rootExpr, out string resultLabel)
            && TryGetCompiledFunctionResultRuntimeManaged(
                resultLabel,
                argumentCount,
                out bool compiledRuntimeManaged)
            && compiledRuntimeManaged;
    }

    private BuiltinRegistry.BytesOwnershipProvenance GetKnownFunctionBytesProvenance(
        Expr rootExpr,
        int argumentCount)
    {
        if (argumentCount == 0
            || !TryResolveKnownFunctionLabel(rootExpr, out string resultLabel)
            || GetOwnershipSummaryForLabel(resultLabel) is not { } summary
            || argumentCount != summary.Parameters.Count)
        {
            return BuiltinRegistry.BytesOwnershipProvenance.Unknown;
        }

        return summary.ResultProvenance.BytesProvenance;
    }

    /// <summary>
    /// Whether this caller may trust the callee's ownership summary about its result. The summary
    /// proves that a result shape is RC-eligible; it does not prove that this particular compiled body
    /// selected RC storage. That can differ even without async when the function's lowering request
    /// keeps a fresh aggregate in its arena. Trusting shape alone skips copy-out, reclaims the arena,
    /// and later releases a stale pointer as though it carried an RC header. The compiled body's own
    /// recorded representation is therefore the necessary final gate in every placement context.
    /// </summary>
    /// <summary>
    /// Resolves the callee whose result this caller may take ownership of. Beyond the placement
    /// context, the callee's ACTUAL compiled result must be runtime-managed: the ownership summary is
    /// an AST-level fact and can report an RC-eligible result for a function whose body compiled to a
    /// region value under its own placement context. Trusting the summary alone lets the caller skip
    /// the copy-out, then reclaim the region the result still points into and release it as if it
    /// carried a reference count.
    /// </summary>
    private bool TryResolveCalleeOwnershipSummary(
        Expr rootExpr,
        int argumentCount,
        out FunctionOwnershipSummary? summary,
        out string? resultLabel)
    {
        summary = null;
        resultLabel = null;
        if (!AllowsAsyncIndependentRcPlacement
            || !AllowsOrdinaryRcPlacement
            || argumentCount == 0
            || !TryResolveKnownFunctionLabel(rootExpr, out string? resolvedLabel))
        {
            return false;
        }

        resultLabel = resolvedLabel;
        summary = GetOwnershipSummaryForLabel(resultLabel);
        return summary is not null && argumentCount == summary.Parameters.Count;
    }

    private bool CalleeCanProvideRuntimeManagedResult(
        string resultLabel,
        int argumentCount,
        FunctionOwnershipSummary summary)
    {
        if (TryGetCompiledFunctionResultRuntimeManaged(
                resultLabel,
                argumentCount,
                out bool compiledRuntimeManaged))
        {
            return compiledRuntimeManaged;
        }
        // A mutual-recursion sibling can be referenced before its body is lowered. Only bootstrap
        // that exact forward edge when the whole-program fixpoint proves a fresh, RC-eligible result;
        // the concrete result-type gate in the caller still has to succeed before this fact is used.
        return summary.ResultFresh && summary.ResultProvenance.RcEligible;
    }

    private bool TryGetCompiledFunctionResultRuntimeManaged(
        string resultLabel,
        int argumentCount,
        out bool runtimeManaged)
    {
        for (int index = 1; index < argumentCount; index++)
        {
            if (!_functionReturnedClosureLabels.TryGetValue(resultLabel, out string? nextLabel))
            {
                runtimeManaged = false;
                return false;
            }
            resultLabel = nextLabel;
        }
        return _bodyRuntimeManagedByLabel.TryGetValue(resultLabel, out runtimeManaged);
    }

    /// <summary>
    /// Resolves whether a saturated call rooted at <paramref name="rootExpr"/> (applied to exactly
    /// <paramref name="argumentCount"/> arguments, with concrete post-unification result type
    /// <paramref name="callResultType"/>) is statically KNOWN to produce an RC-eligible result, via the
    /// ownership analysis (<see cref="FunctionOwnershipSummary.ResultProvenance"/>).
    /// <paramref name="rootExpr"/> resolves to a label through
    /// <see cref="TryResolveKnownFunctionLabel(Expr, out string)"/>'s lexical slot/env/top-level alias
    /// chase. The label is mapped to its exact <see cref="FuncKey"/> where available, with the registered
    /// source name retained only as a compatibility fallback.
    ///
    /// CRITICAL SOUNDNESS NOTE — this method only ever returns <c>true</c> (resolved), never
    /// <c>true</c> with <paramref name="runtimeManaged"/> <c>false</c>. This is deliberate and is NOT
    /// equivalent to the old backward-scan mechanism this replaced, which returned <c>true</c> (with
    /// <c>runtimeManaged=false</c>) whenever a function's ACTUAL COMPILED result temp was proven, by
    /// inspecting the real emitted instructions, not to carry a RuntimeManaged-tagged allocation — a
    /// fact derived from the real generated code, equally trustworthy whether true or false.
    /// ResultProvenance, by contrast, is classified from AST shape ALONE, before lowering — it has no
    /// visibility into representation decisions made only at lowering time from information the AST
    /// doesn't carry (a TCO loop's own accumulator-representation analysis choosing to carry a
    /// parameter as RC; a returned closure's capture analysis choosing RC for its env). A function
    /// whose actual compiled result IS RC through one of those paths, but whose AST shape isn't one this
    /// classifier recognizes, gets ResultProvenance.RcEligible=false — a merely-unproven "false", not a
    /// verified one. Callers of this method feed a bare `false` here DIRECTLY into skipping the escaping
    /// arena copy-out path (LowerCallRestoreArena treats it as "prove this is arena, safe to deep-copy
    /// and abandon the original") — if the true, actual representation is RC, that unconditional copy-out
    /// silently duplicates the value and never releases the original RC allocation: a real, reproducible,
    /// linear-in-iterations memory leak, not merely a missed optimization (caught empirically by
    /// Linux_backend_llvm_runtime_rc_unreturned_TCO_parameter_memory_should_plateau — a `given n ->
    /// given returned -> given discarded -> ...` TCO loop returning `returned`, where the OLD mechanism
    /// resolved runtime-managed=true, by inspecting the actual compiled TCO accumulator representation).
    /// So: treat a `false` RcEligible answer as UNRESOLVED (fall through to the untouched, unrelated,
    /// still-correct dynamic ownership-bit check at the call site, which reads the ACTUAL representation
    /// from the real closure object at runtime) — never as a verified negative. Only a positive
    /// (RcEligible=true) answer, which this phase's own construction proves sound (a recognized
    /// constructor/tuple/list/builtin-producer shape, gated by
    /// <see cref="IsConcretelyRuntimeManageableResultType"/> against the call's concrete type), is ever
    /// trusted through this static fast path.
    ///
    /// A partial application (fewer arguments than the function declares), an unregistered function,
    /// a call resolving to an intermediate curried label with no registered ownership identity, a
    /// result type the drop machinery cannot walk, or (per the above) a negative RcEligible answer all
    /// conservatively return false (unresolved) — the same "fall back to the dynamic ownership check"
    /// behavior this predicate has always had on failure, so none of those shapes regress past what an
    /// unresolved call already did.
    /// </summary>
    private bool TryResolveKnownFunctionResultOwnership(
        Expr rootExpr,
        int argumentCount,
        TypeRef callResultType,
        out bool runtimeManaged)
    {
        runtimeManaged = false;
        // FunctionResultProvenance is computed once, at a whole-program AST pass that precedes
        // lowering entirely — it has no notion of _usesAsync/_inCoroutineBody or the current
        // ownership-placement context, the gates that can force values in this function to arena
        // regardless of any per-value classifier's answer. Every real
        // construction-site classifier
        // (IsDirectRcConstruction's own predecessors, IsRuntimeRcStringProducer, etc.) checks these gates
        // before ever emitting a RuntimeManaged:true instruction, which is exactly why the retired
        // backward-scan mechanism (IsRuntimeManagedResultTemp) never found one to report under these
        // gates — a fact this AST-only analysis cannot see for itself. Without this same guard here, a
        // program using async/dynamic-capability-dispatch could have RcEligible=true trusted even though
        // its ACTUAL compiled result is always arena, letting the caller's arena-reclaim-without-copy
        // path silently invalidate the result — caught empirically by readme_showcase.ash (an async
        // order-pricing pipeline), which printed empty strings instead of "Price: 12.50, Count: 6".
        if (!TryResolveCalleeOwnershipSummary(
                rootExpr,
                argumentCount,
                out FunctionOwnershipSummary? resolved,
                out string? resultLabel)
            || resolved is not { } summary
            || resultLabel is null)
        {
            return false;
        }

        bool provenanceEligible = summary.ResultProvenance.RcEligible;
        OwnershipDecisionFact evaluatedFacts = OwnershipDecisionFact.ResultProvenance;
        OwnershipDecisionFact positiveFacts = provenanceEligible
            ? OwnershipDecisionFact.ResultProvenance
            : OwnershipDecisionFact.None;
        bool runtimeManageableResultType = false;
        if (provenanceEligible)
        {
            evaluatedFacts |= OwnershipDecisionFact.RuntimeManageableResultType;
            runtimeManageableResultType = IsConcretelyRuntimeManageableResultType(callResultType);
            if (runtimeManageableResultType)
            {
                positiveFacts |= OwnershipDecisionFact.RuntimeManageableResultType;
            }
        }

        bool useRuntimeManagement = provenanceEligible
            && runtimeManageableResultType
            && CalleeCanProvideRuntimeManagedResult(resultLabel, argumentCount, summary);
        RecordOwnershipFactConsumption(
            summary,
            OwnershipDecisionKind.RuntimeManagedCallResult,
            parameter: null,
            evaluatedFacts,
            positiveFacts,
            useRuntimeManagement);
        if (!useRuntimeManagement)
        {
            return false;
        }

        runtimeManaged = true;
        return true;
    }

    private bool TryResolveKnownFunctionLabel(Expr expression, out string label)
    {
        if (expression is not Expr.Var variable)
        {
            label = "";
            return false;
        }

        return TryResolveKnownFunctionLabel(variable.Name, out label);
    }

    private bool TryResolveKnownFunctionLabel(string variableName, out string label)
    {
        label = "";

        Binding? binding = Lookup(variableName);
        if (binding is Binding.Self self)
        {
            label = self.FuncLabel;
            return true;
        }

        int? slot = binding switch
        {
            Binding.Local local => local.Slot,
            Binding.Scheme scheme => scheme.Slot,
            _ => null,
        };
        if (slot is not null && _knownFunctionLabelsBySlot.TryGetValue(slot.Value, out string? slotLabel))
        {
            label = slotLabel;
            return true;
        }

        int? envIndex = binding switch
        {
            Binding.Env env => env.Index,
            Binding.EnvScheme envScheme => envScheme.Index,
            _ => null,
        };
        if (envIndex is not null && _knownFunctionLabelsByEnvIndex.TryGetValue(envIndex.Value, out string? envLabel))
        {
            label = envLabel;
            return true;
        }

        // Any lexical binding shadows the global name, even when it does not itself carry a known
        // function label. Falling through here would attach the outer function's ownership summary to
        // a local value or closure with the same source name.
        if (binding is not null)
        {
            return false;
        }

        if (_topLevelFunctionRefs.TryGetValue(variableName, out var topLevelFunction))
        {
            label = topLevelFunction.Label;
            return true;
        }

        return false;
    }

    // Applies the collected arguments one closure call at a time, unifying each parameter and
    // recording the applied arrow's capabilities. Returns a diagnostic result to propagate on an
    // early error, or null when the whole chain applied cleanly.
    private (int, TypeRef)? LowerCallApplyArgs(Expr.Call call, Expr rootExpr, List<Expr> collectedArgs,
        ref int currentTemp, ref TypeRef currentType,
        List<(int Temp, TypeRef Type)> consumedRuntimeArguments,
        out int runtimeManagedResultFlagTemp)
    {
        runtimeManagedResultFlagTemp = -1;
        PreconstrainKnownCallArgumentTypes(rootExpr, collectedArgs, currentType);
        for (int i = 0; i < collectedArgs.Count; i++)
        {
            currentType = Prune(currentType);

            if (currentType is TypeRef.TNever)
            {
                return ReturnNeverWithDummyTemp();
            }

            if (currentType is TypeRef.TVar)
            {
                // Callee type is an unresolved type variable: constrain it to a function type
                // so that the occurs check can fire if the argument is the same variable. The
                // constructed arrow shares the caller's ambient row, so a higher-order parameter
                // applied here (`given f -> given x -> f(x)`) carries its capabilities to the caller.
                Unify(currentType, new TypeRef.TFun(NewTypeVar(), NewTypeVar()) { Row = AmbientRow });
                currentType = Prune(currentType);
            }

            if (currentType is not TypeRef.TFun funType)
            {
                return ReportNonFunctionCall(rootExpr, currentType, i + 1);
            }

            (int argTemp, TypeRef argType) =
                TryLowerTraitDictionaryFunctionValue(collectedArgs[i], funType.Arg)
                ?? LowerExpr(
                    collectedArgs[i],
                    LoweredValueRequest.None.WithExpectedType(funType.Arg)).AsPair();

            var calleeName = TryGetCalleeDisplayName(rootExpr);
            var callContext = calleeName is not null
                ? $"in argument #{i + 1} of call to '{calleeName}'"
                : $"in argument #{i + 1} of function call";
            using (PushDiagnosticContext(callContext))
            {
                Unify(funType.Arg, argType);
            }

            // The applied arrow's capabilities happen here: record them in the ambient row.
            using (PushDiagnosticSpan(GetSpan(call)))
            {
                SubsumeCalleeRow(funType.Row, GetSpan(call));
            }

            currentTemp = LowerAppliedClosureCall(
                rootExpr, collectedArgs[i], i,
                AllowsAsyncIndependentRcPlacement
                    && AllowsOrdinaryRcPlacement
                    && i == collectedArgs.Count - 1
                    && !TryResolveKnownFunctionResultOwnership(rootExpr, collectedArgs.Count, Prune(funType.Ret), out _)
                    && GetCallCopyOutKind(Prune(funType.Ret), out _, out _) is CopyOutKind.Shallow or CopyOutKind.List,
                currentTemp, argTemp, argType, consumedRuntimeArguments, ref runtimeManagedResultFlagTemp);
            currentType = Prune(funType.Ret);
        }

        return null;
    }

    private int LowerAppliedClosureCall(
        Expr rootExpr,
        Expr argument,
        int argumentIndex,
        bool needsResultOwnership,
        int closureTemp,
        int argumentTemp,
        TypeRef argumentType,
        List<(int Temp, TypeRef Type)> consumedRuntimeArguments,
        ref int runtimeManagedResultFlagTemp)
    {
        // Opaque calls consume resources unless borrow analysis proves a read-only parameter.
        int originalArgumentTemp = argumentTemp;
        bool borrowsOnly = CalleeParamBorrowsOnly(rootExpr, argumentIndex);
        bool transfersFreshRuntimeArgument = !borrowsOnly
            && argument is not Expr.Var
            && IsRuntimeManagedResultTemp(originalArgumentTemp)
            && IsKnownRuntimeNormalizedFunctionArgument(rootExpr, argumentIndex);
        int runtimeManagedArgumentFlagTemp = PrepareRuntimeManagedCallArgument(
            argument,
            argumentType,
            closureTemp,
            borrowsOnly,
            transfersFreshRuntimeArgument,
            ref argumentTemp);
        if (!borrowsOnly)
        {
            MarkResourceArgMoved(argument);
            if (argument is not Expr.Var
                && IsRuntimeManagedResultTemp(originalArgumentTemp)
                && !transfersFreshRuntimeArgument)
            {
                consumedRuntimeArguments.Add((originalArgumentTemp, Prune(argumentType)));
            }
        }

        if (needsResultOwnership)
        {
            int packedEnvironmentSizeTemp = NewTemp();
            Emit(new IrInst.LoadMemOffset(packedEnvironmentSizeTemp, closureTemp, 16));
            int ownershipShiftTemp = NewTemp();
            Emit(new IrInst.LoadConstInt(ownershipShiftTemp, 63));
            runtimeManagedResultFlagTemp = NewTemp();
            Emit(new IrInst.ShrInt(
                runtimeManagedResultFlagTemp,
                packedEnvironmentSizeTemp,
                ownershipShiftTemp));
        }

        int target = NewTemp();
        EmitClosureCall(
            target,
            closureTemp,
            argumentTemp,
            borrowsOnly,
            runtimeManagedArgumentFlagTemp);
        return target;
    }

    private int PrepareRuntimeManagedCallArgument(
        Expr argument,
        TypeRef argumentType,
        int closureTemp,
        bool borrowsOnly,
        bool transfersFreshRuntimeArgument,
        ref int argumentTemp)
    {
        if (borrowsOnly
            || !TryGetRuntimeManagedCallArgument(argument, argumentTemp, out int pendingParameterSlot))
        {
            return -1;
        }

        int packedEnvironmentSizeTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(packedEnvironmentSizeTemp, closureTemp, 16));
        int ownershipShiftTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(ownershipShiftTemp, 62));
        int shiftedFlagTemp = NewTemp();
        Emit(new IrInst.ShrInt(shiftedFlagTemp, packedEnvironmentSizeTemp, ownershipShiftTemp));
        int ownershipMaskTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(ownershipMaskTemp, 1));
        int flagTemp = NewTemp();
        Emit(new IrInst.AndInt(flagTemp, shiftedFlagTemp, ownershipMaskTemp));
        if (pendingParameterSlot >= 0)
        {
            _pendingRuntimeArgumentFlags[flagTemp] = pendingParameterSlot;
        }
        if (!transfersFreshRuntimeArgument)
        {
            argumentTemp = EmitConditionallyRetainedRuntimeArgument(argumentTemp, argumentType, flagTemp);
        }
        return flagTemp;
    }

    private int EmitConditionallyRetainedRuntimeArgument(
        int argumentTemp,
        TypeRef argumentType,
        int ownershipFlagTemp)
    {
        int resultSlot = NewLocal();
        Emit(new IrInst.StoreLocal(resultSlot, argumentTemp));
        string doneLabel = NewLabel("rc_call_argument_not_retained");
        Emit(new IrInst.JumpIfFalse(ownershipFlagTemp, doneLabel));
        int retainedTemp;
        if (Prune(argumentType) is TypeRef.TList)
        {
            retainedTemp = EmitRuntimeManagedNullableDup(argumentTemp);
        }
        else
        {
            retainedTemp = NewTemp();
            Emit(new IrInst.RcDup(retainedTemp, argumentTemp, RuntimeManaged: true));
            MarkRuntimeManagedTemp(retainedTemp);
        }
        Emit(new IrInst.StoreLocal(resultSlot, retainedTemp));
        Emit(new IrInst.Label(doneLabel));
        int resultTemp = NewTemp();
        Emit(new IrInst.LoadLocal(resultTemp, resultSlot));
        MarkRuntimeManagedTemp(resultTemp);
        return resultTemp;
    }

    private bool TryGetRuntimeManagedCallArgument(
        Expr argument,
        int argumentTemp,
        out int pendingParameterSlot)
    {
        pendingParameterSlot = -1;
        if (IsRuntimeManagedResultTemp(argumentTemp)
            || argument is Expr.Var variable
                && LookupOwnedValue(variable.Name) is { RuntimeManaged: true, IsDropped: false })
        {
            return true;
        }

        if (argument is Expr.Var patternVariable
            && LookupOwnedValue(patternVariable.Name) is
            { PerceusPatternOwner: true, IsDropped: false, PerceusRootParameterSlot: >= 0 } patternOwner
            && _tcoCtx is { } patternTco)
        {
            if (patternTco.IsRuntimeManagedSlot(patternOwner.PerceusRootParameterSlot))
            {
                return true;
            }

            pendingParameterSlot = patternOwner.PerceusRootParameterSlot;
            return true;
        }

        if (argument is not Expr.Var localVariable || _tcoCtx is not { } tco)
        {
            return false;
        }

        if (Lookup(localVariable.Name) is not Binding.Local parameter
            || !tco.ParamFacts.TryGetValue(parameter.Slot, out TcoParamStaticFacts? ownership)
            || !ownership.HasVisibleBinding)
        {
            return false;
        }

        if (tco.IsRuntimeManagedSlot(parameter.Slot))
        {
            return true;
        }

        // Eligibility can resolve only after this call's surrounding tail self-call constrains the
        // parameter types. Emit the ownership flag now and replace it with zero during finalization
        // if the completed TCO frame does not admit this parameter to runtime RC.
        pendingParameterSlot = parameter.Slot;
        return true;
    }

    private void LowerCallDropConsumedRuntimeArguments(
        TypeRef resultType,
        IReadOnlyList<(int Temp, TypeRef Type)> consumedRuntimeArguments)
    {
        if (Prune(resultType) is TypeRef.TFun)
        {
            return;
        }

        HashSet<int> dropped = [];
        foreach ((int temp, TypeRef type) in consumedRuntimeArguments)
        {
            TypeRef valueType = Prune(type);
            if (CanArenaReset(valueType) || !dropped.Add(temp))
            {
                continue;
            }

            if (valueType is TypeRef.TFun)
            {
                Emit(new IrInst.CleanupResource(temp, "Function"));
                Emit(new IrInst.RcDrop(temp, "Function", RuntimeManaged: true));
            }
            else
            {
                EmitRuntimeManagedChildDrop(temp, valueType);
            }
        }
    }

    private bool IsKnownRuntimeNormalizedFunctionArgument(Expr rootExpr, int argumentIndex)
    {
        if (!TryResolveKnownFunctionLabel(rootExpr, out string label))
        {
            return false;
        }

        for (int i = 0; i < argumentIndex; i++)
        {
            if (!_functionReturnedClosureLabels.TryGetValue(label, out string? nextLabel))
            {
                return false;
            }

            label = nextLabel;
        }

        return _runtimeNormalizedFunctionArgumentLabels.Contains(label);
    }

    private void EmitClosureCall(
        int target,
        int closureTemp,
        int argumentTemp,
        bool borrowsArgument,
        int runtimeManagedArgumentFlagTemp = -1)
    {
        var callInstruction = new IrInst.CallClosure(
            target,
            closureTemp,
            argumentTemp,
            runtimeManagedArgumentFlagTemp);
        Emit(callInstruction);
        if (borrowsArgument)
        {
            _borrowedArgumentCalls.Add(callInstruction);
        }
    }

    // Restore arena after the call chain completes.
    // - Copy-type result (Int, Float, Bool): all allocations from the call
    //   chain are unreachable → reclaim via RestoreArenaState + ReclaimArenaChunks.
    // - Self-contained heap result (String, List with safe element, Closure,
    //   ADT with copy-type fields): restore pointer → copy-out → reclaim chunks
    //   (source stays readable until ReclaimArenaChunks frees the old OS chunks).
    private int LowerCallRestoreArena(
        int callWmCursorSlot,
        int callWmEndSlot,
        int currentTemp,
        TypeRef callResultType,
        bool runtimeManagedResult,
        int runtimeManagedResultFlagTemp)
    {
        int callPreRestoreEndSlot = NewLocal();
        if (runtimeManagedResult || CanArenaReset(callResultType))
        {
            // A pending one-shot post (and everything it captures) lives in this window's
            // allocations; skip the reclaim while any is outstanding.
            var callResetSkipLabel = BeginLivePostsGuard();
            Emit(new IrInst.RestoreArenaState(callWmCursorSlot, callWmEndSlot, callPreRestoreEndSlot));
            Emit(new IrInst.ReclaimArenaChunks(callWmEndSlot, callPreRestoreEndSlot));
            EndLivePostsGuard(callResetSkipLabel);
            return currentTemp;
        }

        var callCopyOutKind = GetCallCopyOutKind(
            callResultType,
            out int callCopySize,
            out IrInst.ListHeadCopyKind listHeadCopy);
        if (callCopyOutKind == CopyOutKind.None)
        {
            return currentTemp;
        }

        if (runtimeManagedResultFlagTemp >= 0)
        {
            return LowerCallConditionalCopyOutResult(
                callWmCursorSlot,
                callWmEndSlot,
                callPreRestoreEndSlot,
                currentTemp,
                runtimeManagedResultFlagTemp,
                callCopyOutKind,
                listHeadCopy,
                callCopySize);
        }

        return LowerCallCopyOutResult(
            callWmCursorSlot,
            callWmEndSlot,
            callPreRestoreEndSlot,
            currentTemp,
            callCopyOutKind,
            listHeadCopy,
            callCopySize);
    }

    private int LowerCallConditionalCopyOutResult(
        int callWmCursorSlot,
        int callWmEndSlot,
        int callPreRestoreEndSlot,
        int currentTemp,
        int runtimeManagedResultFlagTemp,
        CopyOutKind callCopyOutKind,
        IrInst.ListHeadCopyKind listHeadCopy,
        int callCopySize)
    {
        int resultSlot = NewLocal();
        Emit(new IrInst.StoreLocal(resultSlot, currentTemp));
        Emit(new IrInst.RestoreArenaState(callWmCursorSlot, callWmEndSlot, callPreRestoreEndSlot));

        string copyLabel = NewLabel("call_copy_arena_result");
        string reclaimLabel = NewLabel("call_reclaim_owned_result");
        Emit(new IrInst.JumpIfFalse(runtimeManagedResultFlagTemp, copyLabel));
        Emit(new IrInst.Jump(reclaimLabel));
        Emit(new IrInst.Label(copyLabel));
        int copiedTemp = NewTemp();
        if (callCopyOutKind == CopyOutKind.List)
        {
            Emit(new IrInst.CopyOutList(
                copiedTemp,
                currentTemp,
                listHeadCopy,
                RuntimeManaged: true,
                IrInst.CopyOutPurpose.RcNormalization));
        }
        else
        {
            Emit(new IrInst.CopyOutArena(
                copiedTemp,
                currentTemp,
                callCopySize,
                RuntimeManaged: true,
                IrInst.CopyOutPurpose.RcNormalization));
        }
        Emit(new IrInst.StoreLocal(resultSlot, copiedTemp));
        Emit(new IrInst.Label(reclaimLabel));
        Emit(new IrInst.ReclaimArenaChunks(callWmEndSlot, callPreRestoreEndSlot));

        int resultTemp = NewTemp();
        Emit(new IrInst.LoadLocal(resultTemp, resultSlot));
        return resultTemp;
    }

    private int LowerCallCopyOutResult(
        int callWmCursorSlot,
        int callWmEndSlot,
        int callPreRestoreEndSlot,
        int currentTemp,
        CopyOutKind callCopyOutKind,
        IrInst.ListHeadCopyKind listHeadCopy,
        int callCopySize)
    {
        // With capabilities in the program the copy-out is conditional on no post being
        // pending, so the result is routed through a local slot that the skipped path
        // leaves holding the original pointer.
        int callGuardResultSlot = -1;
        string? callCopySkipLabel = null;
        if (CapabilityGlobalCount > 0)
        {
            callGuardResultSlot = NewLocal();
            Emit(new IrInst.StoreLocal(callGuardResultSlot, currentTemp));
            callCopySkipLabel = BeginLivePostsGuard();
        }

        Emit(new IrInst.RestoreArenaState(callWmCursorSlot, callWmEndSlot, callPreRestoreEndSlot));
        int copyDest = NewTemp();
        bool normalizeToRuntimeOwnership = AllowsAsyncIndependentRcPlacement
            && AllowsOrdinaryRcPlacement
            && callCopyOutKind is CopyOutKind.Shallow or CopyOutKind.List;
        switch (callCopyOutKind)
        {
            case CopyOutKind.Shallow:
                Emit(new IrInst.CopyOutArena(
                    copyDest,
                    currentTemp,
                    callCopySize,
                    RuntimeManaged: normalizeToRuntimeOwnership,
                    normalizeToRuntimeOwnership
                        ? IrInst.CopyOutPurpose.RcNormalization
                        : IrInst.CopyOutPurpose.ArenaCallBoundary));
                break;
            case CopyOutKind.List:
                Emit(new IrInst.CopyOutList(
                    copyDest,
                    currentTemp,
                    listHeadCopy,
                    RuntimeManaged: normalizeToRuntimeOwnership,
                    normalizeToRuntimeOwnership
                        ? IrInst.CopyOutPurpose.RcNormalization
                        : IrInst.CopyOutPurpose.ArenaCallBoundary));
                break;
        }
        if (normalizeToRuntimeOwnership)
        {
            MarkRuntimeManagedTemp(copyDest);
        }
        Emit(new IrInst.ReclaimArenaChunks(callWmEndSlot, callPreRestoreEndSlot));
        if (callGuardResultSlot >= 0)
        {
            Emit(new IrInst.StoreLocal(callGuardResultSlot, copyDest));
            EndLivePostsGuard(callCopySkipLabel);
            int guardedResultTemp = NewTemp();
            Emit(new IrInst.LoadLocal(guardedResultTemp, callGuardResultSlot));
            return guardedResultTemp;
        }

        return copyDest;
    }

    private (int, TypeRef) LowerExternalCall(
        Expr rootExpr,
        IrExternalFunction externalFunction,
        TypeRef sourceFunctionType,
        List<Expr> args)
    {
        args = NormalizeNullaryExternalArguments(externalFunction, args);
        int expectedArgumentCount = ExternalInputParameterCount(externalFunction);

        if (args.Count != expectedArgumentCount)
        {
            return ReportArityMismatch(rootExpr, expectedArgumentCount, args.Count);
        }

        RequireExternalRuntimeCapabilities(externalFunction, GetSpan(rootExpr));

        (List<int> loweredArgTemps, List<(int SlotTemp, FfiType ElementType)> outputSlots, TypeRef sourceCursor) =
            LowerExternalArguments(externalFunction, sourceFunctionType, args);

        int nativeResult = NewTemp();
        Emit(new IrInst.CallExternal(nativeResult, externalFunction.SymbolName, externalFunction.LibraryName, loweredArgTemps, externalFunction.ParameterTypes, externalFunction.ReturnType));
        int target = MaterializeExternalOutResult(
            nativeResult,
            externalFunction.ReturnType,
            outputSlots);

        TypeRef resultType = Prune(sourceCursor);
        RecordCallResultTempOwnership(
            target,
            resultType,
            runtimeManagedResult: false,
            normalizedRuntimeManagedResult: false,
            BuiltinRegistry.BytesOwnershipProvenance.Unknown);
        return (target, resultType);
    }

    private (List<int> Args, List<(int SlotTemp, FfiType ElementType)> Outputs, TypeRef SourceCursor)
        LowerExternalArguments(IrExternalFunction function, TypeRef sourceType, IReadOnlyList<Expr> args)
    {
        var loweredArgs = new List<int>(function.ParameterTypes.Count);
        var outputs = new List<(int SlotTemp, FfiType ElementType)>();
        TypeRef sourceCursor = sourceType;
        int argumentIndex = 0;
        for (int parameterIndex = 0; parameterIndex < function.ParameterTypes.Count; parameterIndex++)
        {
            FfiType parameterType = function.ParameterTypes[parameterIndex];
            if (parameterType is FfiType.Out output)
            {
                int slotTemp = NewTemp();
                Emit(new IrInst.AllocFfiOut(slotTemp, output.Element));
                loweredArgs.Add(slotTemp);
                outputs.Add((slotTemp, output.Element));
                continue;
            }

            Expr argument = args[argumentIndex];
            FfiParameterOwnership ownership = GetExternalParameterOwnership(function, parameterIndex);
            bool explicitDestructor = function.DestructorForResource is not null
                && ownership == FfiParameterOwnership.Consume;
            CheckExternalResourceArgument(argument, ownership, explicitDestructor);

            var (argTemp, argType) = LowerExpr(argument);
            TypeRef.TFun sourceFunction = (TypeRef.TFun)Prune(sourceCursor);
            sourceCursor = sourceFunction.Ret;
            using (PushDiagnosticContext($"in argument #{argumentIndex + 1} of external call to '{function.Name}'"))
            {
                Unify(sourceFunction.Arg, argType);
            }

            if (parameterType is FfiType.Str)
            {
                int cStringTemp = NewTemp();
                Emit(new IrInst.ToCString(cStringTemp, argTemp));
                loweredArgs.Add(cStringTemp);
            }
            else
            {
                loweredArgs.Add(argTemp);
            }

            ApplyExternalResourceTransfer(argument, ownership, explicitDestructor);
            argumentIndex++;
        }

        return (loweredArgs, outputs, sourceCursor);
    }

    private static List<Expr> NormalizeNullaryExternalArguments(
        IrExternalFunction function,
        List<Expr> arguments) => ExternalInputParameterCount(function) == 0
            && arguments.Count == 1
            && arguments[0] is Expr.Var { Name: "Unit" }
                ? []
                : arguments;

    private static int ExternalInputParameterCount(IrExternalFunction function) =>
        function.ParameterTypes.Count(type => type is not FfiType.Out);

    private int MaterializeExternalOutResult(
        int nativeResult,
        FfiType returnType,
        IReadOnlyList<(int SlotTemp, FfiType ElementType)> outputSlots)
    {
        var components = new List<(int Temp, TypeRef Type)>();
        if (returnType is FfiType.NativeString nativeString)
        {
            components.Add(MaterializeNativeString(nativeResult, nativeString));
        }
        else if (returnType is not FfiType.Void)
        {
            components.Add((nativeResult, FromFfiType(returnType)));
        }
        foreach ((int slotTemp, FfiType elementType) in outputSlots)
        {
            components.Add(MaterializeNullableExternalOut(slotTemp, elementType));
        }

        if (components.Count == 0)
        {
            return LowerUnitValue().Item1;
        }
        if (components.Count == 1)
        {
            return components[0].Temp;
        }

        int tupleTemp = NewTemp();
        Emit(new IrInst.Alloc(tupleTemp, components.Count * 8, RuntimeManaged: false));
        for (int i = 0; i < components.Count; i++)
        {
            Emit(new IrInst.StoreMemOffset(tupleTemp, i * 8, components[i].Temp));
        }
        return tupleTemp;
    }

    private (int Temp, TypeRef Type) MaterializeNullableExternalOut(int slotTemp, FfiType elementType)
    {
        int valueTemp = NewTemp();
        Emit(new IrInst.LoadFfiOut(valueTemp, slotTemp, elementType));
        if (elementType is FfiType.NativeString nativeString)
        {
            return MaterializeNativeString(valueTemp, nativeString with { Nullable = true });
        }
        int zeroTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(zeroTemp, 0));
        int isNullTemp = NewTemp();
        Emit(new IrInst.CmpIntEq(isNullTemp, valueTemp, zeroTemp));

        int resultSlot = NewLocal();
        string someLabel = NewLabel("ffi_out_some");
        string endLabel = NewLabel("ffi_out_end");
        Emit(new IrInst.JumpIfFalse(isNullTemp, someLabel));
        ConstructorSymbol none = _constructorSymbols["None"];
        int noneTemp = NewTemp();
        Emit(new IrInst.AllocAdt(noneTemp, GetConstructorTag(none), 0));
        Emit(new IrInst.StoreLocal(resultSlot, noneTemp));
        Emit(new IrInst.Jump(endLabel));

        Emit(new IrInst.Label(someLabel));
        ConstructorSymbol some = _constructorSymbols["Some"];
        int someTemp = NewTemp();
        Emit(new IrInst.AllocAdt(someTemp, GetConstructorTag(some), 1));
        Emit(new IrInst.SetAdtField(someTemp, 0, valueTemp));
        Emit(new IrInst.StoreLocal(resultSlot, someTemp));

        Emit(new IrInst.Label(endLabel));
        int resultTemp = NewTemp();
        Emit(new IrInst.LoadLocal(resultTemp, resultSlot));
        return (resultTemp, CreateMaybeType(FromFfiType(elementType)));
    }

    private (int Temp, TypeRef Type) MaterializeNativeString(
        int pointerTemp,
        FfiType.NativeString nativeString)
    {
        int target = NewTemp();
        Emit(new IrInst.CopyFfiString(target, pointerTemp, nativeString));
        TypeRef successType = nativeString.Nullable
            ? CreateMaybeType(new TypeRef.TStr())
            : new TypeRef.TStr();
        return (target, CreateStringResultType(successType));
    }

    private void RequireExternalRuntimeCapabilities(IrExternalFunction function, TextSpan span)
    {
        foreach (string capability in function.RuntimeCapabilities)
        {
            RequireBuiltinCapability(capability, span);
        }
    }

    private static FfiParameterOwnership GetExternalParameterOwnership(
        IrExternalFunction function,
        int index) => index < function.ParameterOwnerships.Count
            ? function.ParameterOwnerships[index]
            : FfiParameterOwnership.Unspecified;

    private void CheckExternalResourceArgument(
        Expr argument,
        FfiParameterOwnership ownership,
        bool explicitDestructor)
    {
        if (explicitDestructor)
        {
            CheckExplicitExternalResourceClose(argument);
        }
        else if (ownership is FfiParameterOwnership.Borrow or FfiParameterOwnership.Consume)
        {
            CheckUseAfterDrop(argument);
        }
    }

    private void ApplyExternalResourceTransfer(
        Expr argument,
        FfiParameterOwnership ownership,
        bool explicitDestructor)
    {
        if (ownership != FfiParameterOwnership.Consume)
        {
            return;
        }
        if (explicitDestructor && argument is Expr.Var closed)
        {
            TryMarkDropped(closed.Name);
        }
        else
        {
            MarkResourceArgMoved(argument);
        }
    }

    private void CheckExplicitExternalResourceClose(Expr argument)
    {
        if (argument is not Expr.Var variable
            || LookupOwnedValue(variable.Name) is not { IsDropped: true } info)
        {
            return;
        }

        if (info.ReleaseKind == ResourceReleaseKind.Moved)
        {
            ReportDiagnostic(
                GetSpan(argument),
                $"Resource '{variable.Name}' has been moved and can no longer be closed here. Ownership was transferred when it was passed to a function or stored in a data structure.",
                DiagnosticCodes.UseAfterMove);
        }
        else
        {
            ReportDiagnostic(
                GetSpan(argument),
                $"Resource '{variable.Name}' has already been closed. Closing a resource twice is not allowed.",
                DiagnosticCodes.DoubleDrop);
        }
    }

    /// <summary>
    /// Synthesizes wrapper <see cref="IrFunction"/>s so that an external function can be used as
    /// a first-class closure value. For an external with N parameters, N curried wrapper functions
    /// are generated: the outermost accumulates one argument per call and the innermost ultimately
    /// issues the <see cref="IrInst.CallExternal"/> instruction with all collected arguments.
    ///
    /// For a 0-parameter external a meaningful compile error is emitted because a nullary function
    /// cannot be represented as a closure that takes an argument.
    /// </summary>
    private (int, TypeRef) EmitExternalFunctionThunk(IrExternalFunction externalFunc, TypeRef closureType, TextSpan referenceSpan)
    {
        int n = externalFunc.ParameterTypes.Count;
        if (n == 0)
        {
            int errTemp = NewTemp();
            ReportDiagnostic(referenceSpan, $"External function '{externalFunc.Name}' has no parameters and cannot be used as a first-class function value.");
            Emit(new IrInst.LoadConstInt(errTemp, 0));
            return (errTemp, closureType);
        }

        _usesClosures = true;

        // Assign a stable id to this thunk family so labels never collide.
        int lambdaId = _nextLambdaId++;
        var layerLabels = new string[n];
        for (int i = 0; i < n; i++)
        {
            layerLabels[i] = $"external_{externalFunc.Name}_thunk_{i}_{lambdaId}";
        }

        // Save outer compilation state so we can build sub-functions in isolation.
        var savedInst = new List<IrInst>(_inst);
        var savedTemp = _nextTempSlot;
        var savedLocal = _nextLocalSlot;
        var savedScopes = _scopes.ToArray();
        var savedLocalNames = new Dictionary<int, string>(_localNames);
        var savedLocalTypes = new Dictionary<int, TypeRef>(_localTypes);
        Dictionary<int, LoweredTempOwnershipFact> savedTempOwnershipFacts =
            SnapshotTempOwnershipFacts();

        EmitExternalFunctionThunkLayers(externalFunc, layerLabels, referenceSpan);

        // Restore outer compilation state.
        _inst.Clear();
        _inst.AddRange(savedInst);
        RestoreTempOwnershipFacts(savedTempOwnershipFacts);
        _nextTempSlot = savedTemp;
        _nextLocalSlot = savedLocal;
        _localNames.Clear();
        _localTypes.Clear();
        foreach (var kv in savedLocalNames) _localNames[kv.Key] = kv.Value;
        foreach (var kv in savedLocalTypes) _localTypes[kv.Key] = kv.Value;
        _scopes.Clear();
        foreach (var s in savedScopes.Reverse())
        {
            _scopes.Push(new Dictionary<string, Binding>(s, StringComparer.Ordinal));
        }

        // Produce a closure pointing at the outermost thunk layer, with a null env.
        int nullEnvTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(nullEnvTemp, 0));
        int resultTemp = NewTemp();
        Emit(new IrInst.MakeClosure(resultTemp, layerLabels[0], nullEnvTemp, 0));
        return (resultTemp, closureType);
    }

    // Build from innermost layer (n-1) outward to layer 0 so each layer can reference the
    // label of the next-inner layer.
    private void EmitExternalFunctionThunkLayers(
        IrExternalFunction externalFunc,
        string[] layerLabels,
        TextSpan referenceSpan)
    {
        int n = externalFunc.ParameterTypes.Count;
        for (int layer = n - 1; layer >= 0; layer--)
        {
            _inst.Clear();
            _tempOwnershipFacts.Clear();
            _nextTempSlot = 0;
            _nextLocalSlot = 0;
            _localNames.Clear();
            _localTypes.Clear();
            _scopes.Clear();
            _scopes.Push(new Dictionary<string, Binding>(StringComparer.Ordinal));

            int envSlot = NewLocal(); // slot 0 — must stay 0 (backend convention)
            int argSlot = NewLocal(); // slot 1
            Debug.Assert(envSlot == 0, "envSlot must be 0");

            if (layer == n - 1)
            {
                EmitExternalFunctionThunkInnermostLayer(externalFunc, layer, argSlot);
            }
            else
            {
                EmitExternalFunctionThunkOuterLayer(layerLabels, layer, argSlot);
            }

            var func = new IrFunction(
                Label: layerLabels[layer],
                Instructions: new List<IrInst>(_inst),
                LocalCount: _nextLocalSlot,
                TempCount: _nextTempSlot,
                HasEnvAndArgParams: true,
                LocalNames: new Dictionary<int, string>(_localNames),
                LocalTypes: SnapshotLocalTypes()
            );
            IrFunctionOrigin? parent = _activeFunctionOrigin;
            string? parentGeneratedLabel = layer == 0
                ? parent?.GeneratedLabel
                : layerLabels[layer - 1];
            AddFunction(
                func,
                new IrFunctionOrigin(
                    layerLabels[layer],
                    IrFunctionOriginKind.ExternalThunk,
                    parent?.Source,
                    parentGeneratedLabel,
                    new CompilerFunctionOwner(
                        CompilerFunctionOwnerKind.External,
                        externalFunc.Name),
                    $"layer:{layer}",
                    ResolveSourceLocation(referenceSpan)));
        }
    }

    // Innermost: load all previously captured args from env then call the external.
    private void EmitExternalFunctionThunkInnermostLayer(IrExternalFunction externalFunc, int layer, int argSlot)
    {
        var callArgTemps = new List<int>(externalFunc.ParameterTypes.Count);

        for (int j = 0; j < layer; j++)
        {
            int envArgTemp = NewTemp();
            Emit(new IrInst.LoadEnv(envArgTemp, j));
            if (externalFunc.ParameterTypes[j] is FfiType.Str)
            {
                int cStrTemp = NewTemp();
                Emit(new IrInst.ToCString(cStrTemp, envArgTemp));
                callArgTemps.Add(cStrTemp);
            }
            else
            {
                callArgTemps.Add(envArgTemp);
            }
        }

        int finalArgTemp = NewTemp();
        Emit(new IrInst.LoadLocal(finalArgTemp, argSlot));
        if (externalFunc.ParameterTypes[layer] is FfiType.Str)
        {
            int cStrFinalTemp = NewTemp();
            Emit(new IrInst.ToCString(cStrFinalTemp, finalArgTemp));
            callArgTemps.Add(cStrFinalTemp);
        }
        else
        {
            callArgTemps.Add(finalArgTemp);
        }

        int callResultTemp = NewTemp();
        Emit(new IrInst.CallExternal(callResultTemp, externalFunc.SymbolName, externalFunc.LibraryName, callArgTemps, externalFunc.ParameterTypes, externalFunc.ReturnType));

        int retTemp;
        if (externalFunc.ReturnType is FfiType.Void)
        {
            retTemp = NewTemp();
            Emit(new IrInst.LoadConstInt(retTemp, 0)); // Unit is represented as 0
        }
        else
        {
            retTemp = callResultTemp;
        }

        Emit(new IrInst.Return(retTemp));
    }

    // Outer layer: pack current arg together with args captured from the outer env,
    // then return a closure pointing at the next inner layer.
    private void EmitExternalFunctionThunkOuterLayer(string[] layerLabels, int layer, int argSlot)
    {
        int capturedCount = layer; // env slots used by previous layers
        int newEnvSize = (capturedCount + 1) * 8;

        int newEnvTemp = NewTemp();
        Emit(new IrInst.Alloc(newEnvTemp, newEnvSize));

        for (int j = 0; j < capturedCount; j++)
        {
            int loadedCapture = NewTemp();
            Emit(new IrInst.LoadEnv(loadedCapture, j));
            Emit(new IrInst.StoreMemOffset(newEnvTemp, j * 8, loadedCapture));
        }

        int newArgTemp = NewTemp();
        Emit(new IrInst.LoadLocal(newArgTemp, argSlot));
        Emit(new IrInst.StoreMemOffset(newEnvTemp, capturedCount * 8, newArgTemp));

        int closureTemp = NewTemp();
        Emit(new IrInst.MakeClosure(closureTemp, layerLabels[layer + 1], newEnvTemp, newEnvSize));
        Emit(new IrInst.Return(closureTemp));
    }

    private static TypeRef FromFfiType(FfiType ffiType)
    {
        return ffiType switch
        {
            FfiType.Int => new TypeRef.TInt(),
            FfiType.UInt unsigned => new TypeRef.TUInt(unsigned.Bits),
            FfiType.Float => new TypeRef.TFloat(),
            FfiType.Float32 => new TypeRef.TFloat(),
            FfiType.Bool => new TypeRef.TBool(),
            FfiType.Str => new TypeRef.TStr(),
            FfiType.Opaque opaque => new TypeRef.TOpaque(opaque.Name),
            FfiType.Ptr ptr => new TypeRef.TPtr(FromFfiType(ptr.Pointee)),
            FfiType.Buffer buffer => new TypeRef.TList(FromFfiType(buffer.Element)),
            _ => throw new InvalidOperationException($"Unknown FFI type '{ffiType.GetType().Name}'.")
        };
    }







    private (int, TypeRef) LowerListLit(
        Expr.ListLit list,
        LoweredValueRequest request)
    {
        using var diagnosticSpan = PushDiagnosticSpan(list);
        var savedTailPos = _tcoCtx?.InTailPosition ?? false;
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        TypeRef elemType = request.ExpectedType is not null
            && Prune(request.ExpectedType) is TypeRef.TList expectedList
                ? expectedList.Element
                : NewTypeVar();
        var (tailTemp, tailType) = LowerEmptyList();
        Unify(tailType, new TypeRef.TList(elemType));

        for (int i = list.Elements.Count - 1; i >= 0; i--)
        {
            LoweredValue head = LowerRuntimeManagedListElement(
                list.Elements[i],
                request,
                elemType);
            using (PushDiagnosticCode(DiagnosticCodes.ListElementTypeMismatch))
            {
                Unify(head.Type, elemType);
            }
            (tailTemp, tailType) = LowerConsCell(
                head.Temp,
                tailTemp,
                elemType,
                tailType,
                ResolveSourceLocation(AstSpans.GetOrDefault(list)),
                request);
        }

        if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;

        return (tailTemp, Prune(tailType));
    }

    private LoweredValue LowerRuntimeManagedListElement(
        Expr element,
        LoweredValueRequest listRequest,
        TypeRef? expectedElementType = null)
    {
        bool runtimeManagedList = listRequest.EmitsRuntime(
            LoweredValueRuntimeRepresentation.List);
        LoweredValueRequest elementRequest = listRequest
            .WithoutExpectedType()
            .AddRuntime(
                runtimeManagedList
                    && IsRuntimeRcStringProducer(element)
                    && IsRuntimeRcClosureCaptureSafeStringProducer(element),
                LoweredValueRuntimeRepresentation.String)
            .AddRuntime(
                runtimeManagedList
                    && IsRuntimeRcBytesProducer(element)
                    && IsRuntimeRcClosureCaptureSafeBytesProducer(element),
                LoweredValueRuntimeRepresentation.Bytes)
            .AddRuntime(
                runtimeManagedList
                    && IsRuntimeRcBigIntProducer(element)
                    && IsRuntimeRcClosureCaptureSafeBigIntProducer(element),
                LoweredValueRuntimeRepresentation.BigInt)
            .AddRuntime(
                runtimeManagedList && element is Expr.TupleLit,
                LoweredValueRuntimeRepresentation.Tuple);
        if (expectedElementType is not null)
        {
            elementRequest = elementRequest.WithExpectedType(expectedElementType);
        }
        LoweredValue lowered = LowerExpr(element, elementRequest);
        if (runtimeManagedList)
        {
            lowered = NormalizeRuntimeManagedBytesValue(lowered);
        }
        lowered = NormalizeRuntimeManagedListElement(lowered, listRequest);
        return lowered;
    }

    private LoweredValue NormalizeRuntimeManagedBytesValue(LoweredValue lowered)
    {
        if (Prune(lowered.Type) is not TypeRef.TBytes
            || lowered.Ownership.BytesProvenance is
                BuiltinRegistry.BytesOwnershipProvenance.Unknown
                or BuiltinRegistry.BytesOwnershipProvenance.ProgramLifetimeView
            || lowered.Ownership.Representation == LoweredTempRepresentation.RuntimeRc
                && lowered.Ownership.BytesProvenance
                    == BuiltinRegistry.BytesOwnershipProvenance.FreshOwnedBuffer)
        {
            return lowered;
        }

        int normalizedTemp = NewTemp();
        Emit(new IrInst.CopyOutArena(
            normalizedTemp,
            lowered.Temp,
            StaticSizeBytes: -1,
            RuntimeManaged: true,
            IrInst.CopyOutPurpose.RcNormalization));
        MarkRuntimeManagedTemp(normalizedTemp, type: new TypeRef.TBytes());
        return CreateLoweredValue(normalizedTemp, lowered.Type);
    }

    private LoweredValue NormalizeRuntimeManagedListElement(
        LoweredValue lowered,
        LoweredValueRequest request)
    {
        if (!request.EmitsRuntime(LoweredValueRuntimeRepresentation.List)
            || request.RuntimeTcoListTailSlot is null
            || lowered.Ownership.Representation
                == LoweredTempRepresentation.RuntimeRc)
        {
            return lowered;
        }

        TypeRef elementType = Prune(lowered.Type);
        if (ContainsBytesLayout(elementType, new HashSet<TypeSymbol>())
            && lowered.Ownership.BytesProvenance is
                BuiltinRegistry.BytesOwnershipProvenance.Unknown
                or BuiltinRegistry.BytesOwnershipProvenance.ProgramLifetimeView)
        {
            return lowered;
        }

        int normalizedTemp = NewTemp();
        if (elementType is TypeRef.TStr)
        {
            Emit(new IrInst.CopyOutArena(
                normalizedTemp,
                lowered.Temp,
                -1,
                RuntimeManaged: true,
                IrInst.CopyOutPurpose.RcNormalization));
        }
        else if (elementType is TypeRef.TList inner
            && TryGetRuntimeManagedListHeadCopy(inner.Element, out IrInst.ListHeadCopyKind headCopy))
        {
            Emit(new IrInst.CopyOutList(
                normalizedTemp,
                lowered.Temp,
                headCopy,
                RuntimeManaged: true,
                IrInst.CopyOutPurpose.RcNormalization));
        }
        else if (CanRuntimeManageTcoListElement(elementType))
        {
            normalizedTemp = EmitRuntimeManagedTcoDeepCopy(lowered.Temp, elementType);
        }
        else
        {
            return lowered;
        }

        MarkRuntimeManagedTemp(normalizedTemp);
        return CreateLoweredValue(normalizedTemp, lowered.Type);
    }

    private (int, TypeRef) LowerTupleLit(
        Expr.TupleLit tuple,
        LoweredValueRequest request)
    {
        var savedTailPos = _tcoCtx?.InTailPosition ?? false;
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        var elements = new List<LoweredValue>(tuple.Elements.Count);
        for (int i = 0; i < tuple.Elements.Count; i++)
        {
            Expr element = tuple.Elements[i];
            LoweredValue loweredElement = LowerTupleElement(element, request);
            if (request.EmitsRuntime(LoweredValueRuntimeRepresentation.Tuple))
            {
                loweredElement = NormalizeRuntimeManagedBytesValue(loweredElement);
            }
            LoweredValue materialized = MaterializeEscapingStringTupleElement(
                element,
                loweredElement,
                request);
            int ownedTemp = DuplicatePerceusPatternOwnerForAggregate(element, materialized.Temp);
            elements.Add(CreateLoweredValue(ownedTemp, materialized.Type));
            MarkResourceArgMoved(tuple.Elements[i]);
        }

        int tupleTemp = NewTemp();
        bool runtimeManaged = request.EmitsRuntime(
            LoweredValueRuntimeRepresentation.Tuple);
        for (int i = 0; i < elements.Count && runtimeManaged; i++)
        {
            runtimeManaged = IsRuntimeManageableTupleElement(elements[i]);
        }
        Emit(new IrInst.Alloc(tupleTemp, tuple.Elements.Count * 8, runtimeManaged));
        for (int i = 0; i < elements.Count; i++)
        {
            Emit(new IrInst.StoreMemOffset(tupleTemp, i * 8, elements[i].Temp));
        }
        RecordAggregateBytesProvenance(tupleTemp, elements);

        if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;

        return (
            tupleTemp,
            new TypeRef.TTuple(elements.Select(element => element.Type).ToList()));
    }

    // A bare string variable placed into a tuple that ESCAPES the function (the tuple is the
    // runtime-managed result — the explicit request includes Tuple) points at a value in the
    // callee's arena (a loop accumulator, a match-bound suffix). The next call reuses that arena and
    // overwrites it, so the returned tuple reads back a later value. A directly-returned string is
    // copied out at the call boundary; a string nested in a tuple field is not. Copy it out here so
    // the escaping tuple owns an independent RC string. Gated on the escape flag, so non-escaping
    // internal tuples (e.g. inside a fully-reusing specialization, where CopyOut is forbidden) are
    // untouched. Producers (`a + b`, `fromInt`) already allocate fresh and are left alone.
    private LoweredValue MaterializeEscapingStringTupleElement(
        Expr element,
        LoweredValue lowered,
        LoweredValueRequest request)
    {
        // A bare string variable placed into an escaping tuple only dangles when it lives in an arena
        // that is reused in place — the accumulator / match-bound suffix (`acc`, `tail`) of a TCO loop,
        // whose arena is reset on every back-edge and overwritten by the next call. Outside a TCO loop
        // the value either has no reuse hazard at all (a top-level `let text = fromInt(42) in (text, 2)`,
        // which the test suite pins to stay arena-managed) or is already deep-copied at the callee's
        // return boundary, so materializing would wrongly promote an otherwise arena tuple to an owning
        // RC graph. Gate on being inside a TCO loop, and never copy a value that already owns a
        // runtime-managed RC value (the ownership system keeps it alive independently of the arena).
        //
        // The element type may still be an unresolved type variable here (inference is interleaved
        // with lowering, so a string accumulator's var is only unified with Str by a later `+`, and is
        // indistinguishable from an Int accumulator's). For an unresolved var we emit the copy-out
        // PROVISIONALLY with DeferredElementType and let ResolveDeferredTupleMaterializations undo it
        // (rewrite to a plain Borrow) once the type resolves to a scalar. Concrete non-string heap
        // accumulators (List/Adt/BigInt) resolve to their own type node and take their own copy paths.
        var pruned = Prune(lowered.Type);
        if (!request.EmitsRuntime(LoweredValueRuntimeRepresentation.Tuple)
            || _tcoCtx is null
            || element is not Expr.Var varElement
            || pruned is not (TypeRef.TStr or TypeRef.TVar)
            || LookupOwnedValue(varElement.Name) is { RuntimeManaged: true })
        {
            return lowered;
        }

        int materialized = NewTemp();
        Emit(new IrInst.CopyOutArena(
            materialized, lowered.Temp, StaticSizeBytes: -1, RuntimeManaged: true,
            IrInst.CopyOutPurpose.RcNormalization,
            DeferredElementType: pruned is TypeRef.TVar ? lowered.Type : null));
        MarkRuntimeManagedTemp(materialized);
        _hasDeferredTupleMaterializations |= pruned is TypeRef.TVar;
        return CreateLoweredValue(materialized, lowered.Type);
    }

    private LoweredValue LowerTupleElement(
        Expr element,
        LoweredValueRequest tupleRequest)
    {
        bool runtimeManagedTuple = tupleRequest.EmitsRuntime(
            LoweredValueRuntimeRepresentation.Tuple);
        LoweredValueRequest elementRequest = tupleRequest
            .AddRuntime(
                runtimeManagedTuple
                    && IsRuntimeRcStringProducer(element)
                    && IsRuntimeRcClosureCaptureSafeStringProducer(element),
                LoweredValueRuntimeRepresentation.String)
            .AddRuntime(
                runtimeManagedTuple
                    && IsRuntimeRcBytesProducer(element)
                    && IsRuntimeRcClosureCaptureSafeBytesProducer(element),
                LoweredValueRuntimeRepresentation.Bytes)
            .AddRuntime(
                runtimeManagedTuple
                    && IsRuntimeRcBigIntProducer(element)
                    && IsRuntimeRcClosureCaptureSafeBigIntProducer(element),
                LoweredValueRuntimeRepresentation.BigInt)
            .AddRuntime(
                runtimeManagedTuple && IsFreshListConstructionExpression(element),
                LoweredValueRuntimeRepresentation.List)
            .AddRuntime(
                runtimeManagedTuple && element is Expr.TupleLit,
                LoweredValueRuntimeRepresentation.Tuple)
            .AddRuntime(
                runtimeManagedTuple && IsConstructorExpression(element),
                LoweredValueRuntimeRepresentation.Adt)
            .AddRuntime(
                runtimeManagedTuple && element is Expr.RecordLit,
                LoweredValueRuntimeRepresentation.Record);
        return LowerExpr(element, elementRequest);
    }

    private bool IsRuntimeManageableTupleElement(LoweredValue value)
    {
        TypeRef pruned = Prune(value.Type);
        return CanArenaReset(pruned)
            || value.Ownership.Representation
                == LoweredTempRepresentation.RuntimeRc
                && (pruned is TypeRef.TTuple or TypeRef.TStr or TypeRef.TBytes or TypeRef.TBigInt
                    || pruned is TypeRef.TList list && CanArenaReset(Prune(list.Element))
                    || pruned is TypeRef.TNamedType);
    }

    private (int, TypeRef) LowerCons(
        Expr.Cons cons,
        LoweredValueRequest request)
    {
        using var diagnosticSpan = PushDiagnosticSpan(cons);
        var savedTailPos = _tcoCtx?.InTailPosition ?? false;
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        TypeRef? expectedElementType = request.ExpectedType is not null
            && Prune(request.ExpectedType) is TypeRef.TList expectedList
                ? expectedList.Element
                : null;
        LoweredValue head = LowerRuntimeManagedListElement(
            cons.Head,
            request,
            expectedElementType);
        TypeRef listType = new TypeRef.TList(head.Type);
        LoweredValueRequest tailRequest = LoweredValueRequest.None.WithExpectedType(listType);
        var (tailTemp, tailType) = LowerExpr(cons.Tail, tailRequest);
        head = CreateLoweredValue(
            DuplicatePerceusPatternOwnerForAggregate(cons.Head, head.Temp),
            head.Type);
        tailTemp = DuplicatePerceusPatternOwnerForAggregate(cons.Tail, tailTemp);
        if (request.EmitsRuntime(LoweredValueRuntimeRepresentation.List)
            && IsRuntimeManageableListElement(head.Type, head.Temp))
        {
            tailTemp = PrepareRuntimeRcListTail(cons.Tail, tailTemp, request);
        }
        MarkResourceArgMoved(cons.Head);
        MarkResourceArgMoved(cons.Tail);

        if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;

        return LowerConsCell(
            head.Temp,
            tailTemp,
            head.Type,
            tailType,
            ResolveSourceLocation(AstSpans.GetOrDefault(cons)),
            request);
    }

    private int PrepareRuntimeRcListTail(
        Expr tailExpression,
        int tailTemp,
        LoweredValueRequest request)
    {
        if (tailExpression is Expr.Var tcoTail
            && request.RuntimeTcoListTailSlot is { } tcoTailSlot
            && Lookup(tcoTail.Name) is Binding.Local tcoTailLocal
            && tcoTailLocal.Slot == tcoTailSlot)
        {
            return tailTemp;
        }

        if (tailExpression is not Expr.Var tail
            || request.RuntimeListTailBinding is null
            || !string.Equals(
                tail.Name,
                request.RuntimeListTailBinding,
                StringComparison.Ordinal)
            || LookupOwnedValue(tail.Name) is not { IsDropped: false } info
            || (!info.RuntimeManaged && !info.PerceusPatternOwner))
        {
            return tailTemp;
        }

        if (info.PerceusPatternOwner)
        {
            return tailTemp;
        }

        if (request.RuntimeListTailShared)
        {
            int duplicatedTemp = NewTemp();
            Emit(new IrInst.RcDup(
                duplicatedTemp,
                tailTemp,
                RuntimeManaged: info.RuntimeManaged));
            info.RuntimeDeepUnique = false;
            return duplicatedTemp;
        }

        info.ReleaseKind = ResourceReleaseKind.Moved;
        return tailTemp;
    }


    private (int, TypeRef) LowerAwait(Expr.Await awaitExpr)
    {
        var (taskTemp, taskType) = LowerExpr(awaitExpr.Task);

        // Verify the operand is a Task(E, A), then run it to a Result(E, A).
        if (!_typeSymbols.TryGetValue("Task", out var taskSymbol)
            || !TryGetStandardResultParts(out var resultSymbol, out _, out _))
        {
            ReportDiagnostic(GetSpan(awaitExpr), "Internal error: Task or Result type not registered.");
            return ReturnNeverWithDummyTemp();
        }

        var errorType = NewTypeVar();
        var successType = NewTypeVar();
        var expectedType = new TypeRef.TNamedType(taskSymbol, [errorType, successType]);
        Unify(taskType, expectedType);

        int resultTemp = NewTemp();
        if (_inCoroutineBody)
        {
            // Inside a coroutine: a suspension point. StateMachineTransform splits the body here, the
            // driver runs the awaited sub-task, and resume reads its result — same value as a blocking
            // RunTask, but the enclosing task suspends instead of blocking the thread inline.
            Emit(new IrInst.AwaitTask(resultTemp, taskTemp));
        }
        else
        {
            Emit(new IrInst.RunTask(resultTemp, taskTemp));
        }

        var resultType = new TypeRef.TNamedType(resultSymbol, [Prune(errorType), Prune(successType)]);
        return (resultTemp, resultType);
    }

























































    private int NewTemp()
    {
        return _nextTempSlot++;
    }

    private int NewLocal()
    {
        return _nextLocalSlot++;
    }

    private void RecordLocalDebugInfo(int slot, string name, TypeRef type)
    {
        _localNames[slot] = name;
        _localTypes[slot] = type;
    }

    private Dictionary<int, TypeRef> SnapshotLocalTypes()
    {
        var snapshot = new Dictionary<int, TypeRef>(_localTypes.Count);
        foreach (var (slot, type) in _localTypes)
        {
            snapshot[slot] = Prune(type);
        }

        return snapshot;
    }

    private string NewLabel(string prefix)
    {
        return $"{prefix}_{_nextLabelId++}";
    }

    private string InternString(string value)
    {
        if (_stringIntern.TryGetValue(value, out var existing))
        {
            return existing;
        }

        var label = $"str_{_strings.Count}";
        _strings.Add(new IrStringLiteral(label, value));
        _stringIntern[value] = label;
        return label;
    }

    private HashSet<string> FreeVars(Expr e, HashSet<string> bound)
    {
        var res = new HashSet<string>(StringComparer.Ordinal);
        FreeVarsVisit(e, bound, res);
        return res;
    }

    // The node-kind dispatch is split into ordered groups; each group reports whether it handled
    // the node so the next group only sees the remaining kinds.
    private void FreeVarsVisit(Expr ex, HashSet<string> bnd, HashSet<string> res)
    {
        if (FreeVarsVisitAtomOrArith(ex, bnd, res))
        {
            return;
        }

        if (FreeVarsVisitBitwiseOrCompare(ex, bnd, res))
        {
            return;
        }

        if (FreeVarsVisitApplicationOrAggregate(ex, bnd, res))
        {
            return;
        }

        FreeVarsVisitBinderForms(ex, bnd, res);
    }

    private bool FreeVarsVisitAtomOrArith(Expr ex, HashSet<string> bnd, HashSet<string> res)
    {
        switch (ex)
        {
            case Expr.IntLit:
            case Expr.UIntLit:
            case Expr.BigIntLit:
            case Expr.FloatLit:
            case Expr.StrLit or Expr.RuneLit:
            case Expr.BoolLit:
                return true;
            case Expr.Var v:
                if (!bnd.Contains(v.Name))
                {
                    res.Add(v.Name);
                }
                FreeVarsAddTraitEvidence(v, bnd, res);

                return true;
            case Expr.QualifiedVar qv:
                FreeVarsVisitQualifiedVar(qv, bnd, res);
                return true;
            case Expr.Add a:
                FreeVarsVisit(a.Left, bnd, res);
                FreeVarsVisit(a.Right, bnd, res);
                return true;
            case Expr.Subtract sub:
                FreeVarsVisit(sub.Left, bnd, res);
                FreeVarsVisit(sub.Right, bnd, res);
                return true;
            case Expr.Multiply mul:
                FreeVarsVisit(mul.Left, bnd, res);
                FreeVarsVisit(mul.Right, bnd, res);
                return true;
            case Expr.Divide div:
                FreeVarsVisit(div.Left, bnd, res);
                FreeVarsVisit(div.Right, bnd, res);
                return true;
            case Expr.Modulo modExpr:
                FreeVarsVisit(modExpr.Left, bnd, res);
                FreeVarsVisit(modExpr.Right, bnd, res);
                return true;
            default:
                return false;
        }
    }

    private void FreeVarsVisitQualifiedVar(Expr.QualifiedVar qv, HashSet<string> bnd, HashSet<string> res)
    {
        var resolvedModule = ResolveModuleAlias(qv.Module);

        // An intrinsic member (Ashes.IO.print, Ashes.Text.uncons, ...) lowers directly
        // to a builtin and introduces no free variable. A SHIPPED-helper or user-module
        // member (Ashes.Text.indexOf, Ashes.Collection.Map.get, ...) lowers to a stitched
        // top-level binding `Module_name`; when such a reference appears inside a lambda
        // body it IS a free variable that the closure must capture, otherwise the
        // synthesized binding is out of scope inside the lambda and resolution fails with
        // a spurious "Unknown module".
        if (BuiltinRegistry.TryGetModule(resolvedModule, out var qvModule)
            && qvModule.Members.ContainsKey(qv.Name))
        {
            return;
        }

        var synthesized = ProjectSupport.SanitizeModuleBindingName(resolvedModule) + "_" + qv.Name;
        if (!bnd.Contains(synthesized)
            && (_topLevelBindingNames.Contains(synthesized) || Lookup(synthesized) is not null))
        {
            res.Add(synthesized);
            return;
        }

        // `receiver.field` record access: when the "module" position is actually a value binding
        // in the enclosing scope (a parameter or let), the closure must capture the receiver like
        // any other free variable, or field access inside the lambda body finds no binding and
        // fails with a spurious "Unknown module".
        if (!bnd.Contains(qv.Module)
            && !_capabilitySymbols.ContainsKey(qv.Module)
            && !_moduleAliases.ContainsKey(qv.Module)
            && Lookup(qv.Module) is Binding.Local or Binding.Scheme or Binding.Env or Binding.EnvScheme)
        {
            res.Add(qv.Module);
        }
    }

    private bool FreeVarsVisitBitwiseOrCompare(Expr ex, HashSet<string> bnd, HashSet<string> res)
    {
        switch (ex)
        {
            case Expr.BitwiseAnd bitAnd:
                FreeVarsVisit(bitAnd.Left, bnd, res);
                FreeVarsVisit(bitAnd.Right, bnd, res);
                return true;
            case Expr.BitwiseOr bitOr:
                FreeVarsVisit(bitOr.Left, bnd, res);
                FreeVarsVisit(bitOr.Right, bnd, res);
                return true;
            case Expr.BitwiseXor bitXor:
                FreeVarsVisit(bitXor.Left, bnd, res);
                FreeVarsVisit(bitXor.Right, bnd, res);
                return true;
            case Expr.ShiftLeft shiftLeft:
                FreeVarsVisit(shiftLeft.Left, bnd, res);
                FreeVarsVisit(shiftLeft.Right, bnd, res);
                return true;
            case Expr.ShiftRight shiftRight:
                FreeVarsVisit(shiftRight.Left, bnd, res);
                FreeVarsVisit(shiftRight.Right, bnd, res);
                return true;
            case Expr.BitwiseNot bitwiseNot:
                FreeVarsVisit(bitwiseNot.Operand, bnd, res);
                return true;
            case Expr.LogicalNot logicalNot:
                FreeVarsVisit(logicalNot.Operand, bnd, res);
                return true;
            case Expr.GreaterThan gt:
                FreeVarsVisit(gt.Left, bnd, res);
                FreeVarsVisit(gt.Right, bnd, res);
                return true;
            case Expr.GreaterOrEqual ge:
                FreeVarsVisit(ge.Left, bnd, res);
                FreeVarsVisit(ge.Right, bnd, res);
                return true;
            case Expr.LessThan lt:
                FreeVarsVisit(lt.Left, bnd, res);
                FreeVarsVisit(lt.Right, bnd, res);
                return true;
            case Expr.LessOrEqual le:
                FreeVarsVisit(le.Left, bnd, res);
                FreeVarsVisit(le.Right, bnd, res);
                return true;
            case Expr.Equal eq:
                FreeVarsVisit(eq.Left, bnd, res);
                FreeVarsVisit(eq.Right, bnd, res);
                return true;
            case Expr.NotEqual ne:
                FreeVarsVisit(ne.Left, bnd, res);
                FreeVarsVisit(ne.Right, bnd, res);
                return true;
            default:
                return false;
        }
    }

    private bool FreeVarsVisitApplicationOrAggregate(Expr ex, HashSet<string> bnd, HashSet<string> res)
    {
        switch (ex)
        {
            case Expr.ResultPipe pipe:
                FreeVarsVisit(pipe.Left, bnd, res);
                FreeVarsVisit(pipe.Right, bnd, res);
                return true;
            case Expr.ResultMapErrorPipe pipe:
                FreeVarsVisit(pipe.Left, bnd, res);
                FreeVarsVisit(pipe.Right, bnd, res);
                return true;
            case Expr.Call c:
                FreeVarsAddTraitEvidence(c.Func, bnd, res);
                FreeVarsVisit(c.Func, bnd, res);
                FreeVarsVisit(c.Arg, bnd, res);
                return true;
            case Expr.TupleLit tuple:
                foreach (var elem in tuple.Elements)
                {
                    FreeVarsVisit(elem, bnd, res);
                }
                return true;
            case Expr.ListLit list:
                foreach (var e in list.Elements)
                {
                    FreeVarsVisit(e, bnd, res);
                }

                return true;
            case Expr.Cons c:
                FreeVarsVisit(c.Head, bnd, res);
                FreeVarsVisit(c.Tail, bnd, res);
                return true;
            case Expr.Match m:
                FreeVarsVisitMatch(m, bnd, res);
                return true;
            case Expr.If iff:
                FreeVarsVisit(iff.Cond, bnd, res);
                FreeVarsVisit(iff.Then, bnd, res);
                FreeVarsVisit(iff.Else, bnd, res);
                return true;
            default:
                return false;
        }
    }

    private void FreeVarsAddTraitEvidence(Expr function, HashSet<string> bound, HashSet<string> result)
    {
        if (ResolveSpecializableCalleeName(function) is not { } functionName
            || !TryGetTraitDictionaryInfo(functionName, Lookup(functionName), out TraitDictionaryFunctionInfo? traitFunction))
        {
            return;
        }
        foreach (TraitDictionaryShape needed in traitFunction!.Dictionaries)
        {
            if (!_activeTraitDictionaryParameters.TryGetValue(
                    needed.Trait.QualifiedName,
                    out List<ActiveTraitDictionaryParameter>? activeParameters))
            {
                continue;
            }
            foreach (ActiveTraitDictionaryParameter active in activeParameters)
            {
                string parameterName = active.ParameterName;
                if (!bound.Contains(parameterName))
                {
                    result.Add(parameterName);
                }
            }
        }
    }

    private void FreeVarsVisitMatch(Expr.Match m, HashSet<string> bnd, HashSet<string> res)
    {
        FreeVarsVisit(m.Value, bnd, res);
        foreach (var mc in m.Cases)
        {
            var bndCase = new HashSet<string>(bnd, StringComparer.Ordinal);
            foreach (var name in PatternBindings(mc.Pattern))
            {
                bndCase.Add(name);
            }

            if (mc.Guard is not null)
            {
                FreeVarsVisit(mc.Guard, bndCase, res);
            }
            FreeVarsVisit(mc.Body, bndCase, res);
        }
    }

    private void FreeVarsVisitBinderForms(Expr ex, HashSet<string> bnd, HashSet<string> res)
    {
        switch (ex)
        {
            case Expr.Let l:
                FreeVarsVisit(l.Value, bnd, res);
                var boundWithLetVar = new HashSet<string>(bnd, StringComparer.Ordinal) { l.Name };
                FreeVarsVisit(l.Body, boundWithLetVar, res);
                return;
            case Expr.LetResult l:
                FreeVarsVisit(l.Value, bnd, res);
                var boundWithResultVar = new HashSet<string>(bnd, StringComparer.Ordinal) { l.Name };
                FreeVarsVisit(l.Body, boundWithResultVar, res);
                return;
            case Expr.LetRecursive l:
                var boundWithRecursiveVar = new HashSet<string>(bnd, StringComparer.Ordinal) { l.Name };
                FreeVarsVisit(l.Value, boundWithRecursiveVar, res);
                FreeVarsVisit(l.Body, boundWithRecursiveVar, res);
                return;
            case RecursiveGroupExpr group:
                FreeVarsVisitRecursiveGroup(group, bnd, res);
                return;
            case Expr.Lambda lam:
                var boundWithParam = new HashSet<string>(bnd, StringComparer.Ordinal) { lam.ParamName };
                FreeVarsVisit(lam.Body, boundWithParam, res);
                return;
            case Expr.Await awaitExpr:
                FreeVarsVisit(awaitExpr.Task, bnd, res);
                return;
            case Expr.RecordLit recordLit:
                foreach (var field in recordLit.Fields)
                {
                    FreeVarsVisit(field.Value, bnd, res);
                }

                return;
            case Expr.RecordUpdate recordUpdate:
                FreeVarsVisit(recordUpdate.Target, bnd, res);
                foreach (var update in recordUpdate.Updates)
                {
                    FreeVarsVisit(update.Value, bnd, res);
                }

                return;
            case Expr.Perform perform:
                FreeVarsVisit(perform.Operation, bnd, res);
                return;
            case CapabilityPostExpr capabilityPost:
                FreeVarsVisit(capabilityPost.Value, bnd, res);
                FreeVarsVisit(capabilityPost.PostLambda, bnd, res);
                return;
            case Expr.Handle handleExpr:
                FreeVarsVisitHandle(handleExpr, bnd, res);
                return;
            default:
                throw new NotSupportedException(ex.GetType().Name);
        }
    }

    private void FreeVarsVisitRecursiveGroup(RecursiveGroupExpr group, HashSet<string> bnd, HashSet<string> res)
    {
        var boundWithRecursiveGroup = new HashSet<string>(bnd, StringComparer.Ordinal);
        boundWithRecursiveGroup.UnionWith(group.Bindings.Select(binding => binding.Name));
        foreach ((_, Expr value) in group.Bindings)
        {
            FreeVarsVisit(value, boundWithRecursiveGroup, res);
        }

        FreeVarsVisit(group.Body, boundWithRecursiveGroup, res);
    }

    private void FreeVarsVisitHandle(Expr.Handle handleExpr, HashSet<string> bnd, HashSet<string> res)
    {
        FreeVarsVisit(handleExpr.Body, bnd, res);
        foreach (var arm in handleExpr.Arms)
        {
            var bndArm = new HashSet<string>(bnd, StringComparer.Ordinal) { "resume" };
            foreach (var armParam in arm.Parameters)
            {
                foreach (var name in PatternBindings(armParam))
                {
                    bndArm.Add(name);
                }
            }

            FreeVarsVisit(arm.Body, bndArm, res);
        }
    }

    private static Expr SubstituteVars(
        Expr e,
        Dictionary<string, string> renames,
        Action<Expr, Expr>? recordRebuiltBinder = null)
    {
        if (renames.Count == 0)
        {
            return e;
        }

        Expr S(Expr x) => SubstituteVars(x, renames, recordRebuiltBinder);

        switch (e)
        {
            case Expr.IntLit or Expr.UIntLit or Expr.BigIntLit or Expr.FloatLit or Expr.StrLit or Expr.RuneLit or Expr.BoolLit or Expr.QualifiedVar:
                return e;
            case Expr.Var v:
                return renames.TryGetValue(v.Name, out var tgt) ? new Expr.Var(tgt) : e;
            case Expr.Add b: return new Expr.Add(S(b.Left), S(b.Right));
            case Expr.Subtract b: return new Expr.Subtract(S(b.Left), S(b.Right));
            case Expr.Multiply b: return new Expr.Multiply(S(b.Left), S(b.Right));
            case Expr.Divide b: return new Expr.Divide(S(b.Left), S(b.Right));
            case Expr.Modulo b: return new Expr.Modulo(S(b.Left), S(b.Right));
            case Expr.BitwiseAnd b: return new Expr.BitwiseAnd(S(b.Left), S(b.Right));
            case Expr.BitwiseOr b: return new Expr.BitwiseOr(S(b.Left), S(b.Right));
            case Expr.BitwiseXor b: return new Expr.BitwiseXor(S(b.Left), S(b.Right));
            case Expr.ShiftLeft b: return new Expr.ShiftLeft(S(b.Left), S(b.Right));
            case Expr.ShiftRight b: return new Expr.ShiftRight(S(b.Left), S(b.Right));
            case Expr.BitwiseNot b: return new Expr.BitwiseNot(S(b.Operand));
            case Expr.LogicalNot b: return new Expr.LogicalNot(S(b.Operand));
            case Expr.GreaterThan b: return new Expr.GreaterThan(S(b.Left), S(b.Right));
            case Expr.GreaterOrEqual b: return new Expr.GreaterOrEqual(S(b.Left), S(b.Right));
            case Expr.LessThan b: return new Expr.LessThan(S(b.Left), S(b.Right));
            case Expr.LessOrEqual b: return new Expr.LessOrEqual(S(b.Left), S(b.Right));
            case Expr.Equal b: return new Expr.Equal(S(b.Left), S(b.Right));
            case Expr.NotEqual b: return new Expr.NotEqual(S(b.Left), S(b.Right));
            case Expr.ResultPipe b: return new Expr.ResultPipe(S(b.Left), S(b.Right));
            case Expr.ResultMapErrorPipe b: return new Expr.ResultMapErrorPipe(S(b.Left), S(b.Right));
            case Expr.Call c: return new Expr.Call(S(c.Func), S(c.Arg));
            case Expr.TupleLit t: return new Expr.TupleLit(t.Elements.Select(S).ToList());
            case Expr.ListLit l: return new Expr.ListLit(l.Elements.Select(S).ToList());
            case Expr.Cons c: return new Expr.Cons(S(c.Head), S(c.Tail));
            case Expr.If i: return new Expr.If(S(i.Cond), S(i.Then), S(i.Else));
            case Expr.Await a: return new Expr.Await(S(a.Task));
            case Expr.Perform p: return new Expr.Perform(S(p.Operation));
            case Expr.Lambda or Expr.Let or Expr.LetResult or Expr.LetRecursive or Expr.Match:
                return SubstituteVarsBinders(e, renames, recordRebuiltBinder);
            default:
                throw new NotSupportedException($"SubstituteVars: unhandled {e.GetType().Name}");
        }
    }

    private static Expr SubstituteVarsBinders(
        Expr e,
        Dictionary<string, string> renames,
        Action<Expr, Expr>? recordRebuiltBinder)
    {
        Expr S(Expr x) => SubstituteVars(x, renames, recordRebuiltBinder);

        // A binder shadows a renamed name within its scope: drop it from the rename set for the subtree.
        T WithShadowed<T>(IEnumerable<string> bound, Func<Dictionary<string, string>, T> build)
        {
            var sub = renames.Where(kv => !bound.Contains(kv.Key, StringComparer.Ordinal)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            return build(sub);
        }

        Expr RecordBinder(Expr original, Expr rebuilt)
        {
            recordRebuiltBinder?.Invoke(original, rebuilt);
            return rebuilt;
        }

        switch (e)
        {
            case Expr.Lambda lam:
                return WithShadowed(
                    [lam.ParamName],
                    sub => new Expr.Lambda(
                        lam.ParamName,
                        SubstituteVars(lam.Body, sub, recordRebuiltBinder)));
            case Expr.Let l:
                return RecordBinder(
                    l,
                    new Expr.Let(
                        l.Name,
                        S(l.Value),
                        WithShadowed(
                            [l.Name],
                            sub => SubstituteVars(l.Body, sub, recordRebuiltBinder))));
            case Expr.LetResult l:
                return RecordBinder(
                    l,
                    new Expr.LetResult(
                        l.Name,
                        S(l.Value),
                        WithShadowed(
                            [l.Name],
                            sub => SubstituteVars(l.Body, sub, recordRebuiltBinder))));
            case Expr.LetRecursive l:
                return WithShadowed(
                    [l.Name],
                    sub => RecordBinder(
                        l,
                        new Expr.LetRecursive(
                            l.Name,
                            SubstituteVars(l.Value, sub, recordRebuiltBinder),
                            SubstituteVars(l.Body, sub, recordRebuiltBinder))));
            case Expr.Match m:
                return SubstituteVarsMatch(m, renames, recordRebuiltBinder);
            default:
                throw new NotSupportedException($"SubstituteVars: unhandled {e.GetType().Name}");
        }
    }

    private static Expr SubstituteVarsMatch(
        Expr.Match match,
        Dictionary<string, string> renames,
        Action<Expr, Expr>? recordRebuiltBinder)
    {
        var cases = new List<MatchCase>(match.Cases.Count);
        foreach (MatchCase matchCase in match.Cases)
        {
            IEnumerable<string> bound = PatternBindings(matchCase.Pattern);
            var armRenames = renames
                .Where(kv => !bound.Contains(kv.Key, StringComparer.Ordinal))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            cases.Add(new MatchCase(
                matchCase.Pattern,
                SubstituteVars(matchCase.Body, armRenames, recordRebuiltBinder),
                matchCase.Guard is null
                    ? null
                    : SubstituteVars(matchCase.Guard, armRenames, recordRebuiltBinder)));
        }

        return new Expr.Match(
            SubstituteVars(match.Value, renames, recordRebuiltBinder),
            cases,
            match.Pos);
    }

    private bool TryLowerConstructorExpression(Expr expr, bool stackAllocate, out (int Temp, TypeRef Type) lowered)
    {
        if (expr is Expr.Var varCtor && _constructorSymbols.TryGetValue(varCtor.Name, out var nullaryCtor) && nullaryCtor.Arity == 0)
        {
            lowered = LowerNullaryConstructor(
                nullaryCtor,
                stackAllocate,
                ResolveSourceLocation(AstSpans.GetOrDefault(expr)));
            return true;
        }

        var args = new List<Expr>();
        var rootExpr = CollectCallArgs(expr, args);
        if (rootExpr is Expr.Var callCtor && _constructorSymbols.TryGetValue(callCtor.Name, out var ctor))
        {
            lowered = LowerConstructorApplication(
                ctor,
                args,
                stackAllocate,
                ResolveSourceLocation(AstSpans.GetOrDefault(expr)));
            return true;
        }

        lowered = default;
        return false;
    }

    private bool IsConstructorExpression(Expr expr)
    {
        if (expr is Expr.Var varCtor && _constructorSymbols.TryGetValue(varCtor.Name, out var nullaryCtor) && nullaryCtor.Arity == 0)
        {
            return true;
        }

        var args = new List<Expr>();
        var rootExpr = CollectCallArgs(expr, args);
        return rootExpr is Expr.Var callCtor && _constructorSymbols.TryGetValue(callCtor.Name, out _);
    }

    private static bool ShouldStackAllocateImmediateMatchScrutinee(Expr.Match match)
    {
        return match.Cases.Count == 1 && match.Cases[0].Pattern is Pattern.Constructor;
    }

    private static bool IsImmediateSingleArmAdtDestructuringMatch(string name, Expr body)
    {
        if (body is not Expr.Match(Expr.Var varExpr, var cases, _) || !string.Equals(varExpr.Name, name, StringComparison.Ordinal))
        {
            return false;
        }

        if (cases.Count != 1 || cases[0].Pattern is not Pattern.Constructor)
        {
            return false;
        }

        bool shadowedInArm = PatternBindings(cases[0].Pattern).Any(boundName => string.Equals(boundName, name, StringComparison.Ordinal));
        var guard = cases[0].Guard;
        return (guard is null || !ExprReferencesName(guard, name, shadowedInArm))
            && !ExprReferencesName(cases[0].Body, name, shadowedInArm);
    }

    // The node-kind dispatch is split into ordered groups; each group returns null for the kinds
    // it does not handle so the next group sees them.
    private static bool UsesNameOnlyAsDirectCallee(Expr expr, string targetName, bool shadowed = false, bool allowDirectCallee = false)
    {
        return UsesNameOnlyAsDirectCalleeAtomOrArith(expr, targetName, shadowed, allowDirectCallee)
            ?? UsesNameOnlyAsDirectCalleeBitwiseOrCompare(expr, targetName, shadowed)
            ?? UsesNameOnlyAsDirectCalleeApplicationOrAggregate(expr, targetName, shadowed)
            ?? UsesNameOnlyAsDirectCalleeBinderForms(expr, targetName, shadowed);
    }

    private static bool? UsesNameOnlyAsDirectCalleeAtomOrArith(Expr expr, string targetName, bool shadowed, bool allowDirectCallee)
    {
        switch (expr)
        {
            case Expr.IntLit:
            case Expr.UIntLit:
            case Expr.BigIntLit:
            case Expr.FloatLit:
            case Expr.StrLit or Expr.RuneLit:
            case Expr.BoolLit:
            case Expr.QualifiedVar:
                return true;

            case Expr.Var v:
                return shadowed || !string.Equals(v.Name, targetName, StringComparison.Ordinal) || allowDirectCallee;

            case Expr.Add add:
                return UsesNameOnlyAsDirectCallee(add.Left, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(add.Right, targetName, shadowed);
            case Expr.Subtract sub:
                return UsesNameOnlyAsDirectCallee(sub.Left, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(sub.Right, targetName, shadowed);
            case Expr.Multiply mul:
                return UsesNameOnlyAsDirectCallee(mul.Left, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(mul.Right, targetName, shadowed);
            case Expr.Divide div:
                return UsesNameOnlyAsDirectCallee(div.Left, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(div.Right, targetName, shadowed);
            case Expr.Modulo modExpr:
                return UsesNameOnlyAsDirectCallee(modExpr.Left, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(modExpr.Right, targetName, shadowed);
            default:
                return null;
        }
    }

    private static bool? UsesNameOnlyAsDirectCalleeBitwiseOrCompare(Expr expr, string targetName, bool shadowed)
    {
        switch (expr)
        {
            case Expr.BitwiseAnd bitAnd:
                return UsesNameOnlyAsDirectCallee(bitAnd.Left, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(bitAnd.Right, targetName, shadowed);
            case Expr.BitwiseOr bitOr:
                return UsesNameOnlyAsDirectCallee(bitOr.Left, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(bitOr.Right, targetName, shadowed);
            case Expr.BitwiseXor bitXor:
                return UsesNameOnlyAsDirectCallee(bitXor.Left, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(bitXor.Right, targetName, shadowed);
            case Expr.ShiftLeft shiftLeft:
                return UsesNameOnlyAsDirectCallee(shiftLeft.Left, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(shiftLeft.Right, targetName, shadowed);
            case Expr.ShiftRight shiftRight:
                return UsesNameOnlyAsDirectCallee(shiftRight.Left, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(shiftRight.Right, targetName, shadowed);
            case Expr.BitwiseNot bitwiseNot:
                return UsesNameOnlyAsDirectCallee(bitwiseNot.Operand, targetName, shadowed);
            case Expr.LogicalNot logicalNot:
                return UsesNameOnlyAsDirectCallee(logicalNot.Operand, targetName, shadowed);
            case Expr.GreaterThan gt:
                return UsesNameOnlyAsDirectCallee(gt.Left, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(gt.Right, targetName, shadowed);
            case Expr.GreaterOrEqual ge:
                return UsesNameOnlyAsDirectCallee(ge.Left, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(ge.Right, targetName, shadowed);
            case Expr.LessThan lt:
                return UsesNameOnlyAsDirectCallee(lt.Left, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(lt.Right, targetName, shadowed);
            case Expr.LessOrEqual le:
                return UsesNameOnlyAsDirectCallee(le.Left, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(le.Right, targetName, shadowed);
            case Expr.Equal eq:
                return UsesNameOnlyAsDirectCallee(eq.Left, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(eq.Right, targetName, shadowed);
            case Expr.NotEqual ne:
                return UsesNameOnlyAsDirectCallee(ne.Left, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(ne.Right, targetName, shadowed);
            default:
                return null;
        }
    }

    private static bool? UsesNameOnlyAsDirectCalleeApplicationOrAggregate(Expr expr, string targetName, bool shadowed)
    {
        switch (expr)
        {
            case Expr.ResultPipe pipe:
                return UsesNameOnlyAsDirectCallee(pipe.Left, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(pipe.Right, targetName, shadowed);
            case Expr.ResultMapErrorPipe pipe:
                return UsesNameOnlyAsDirectCallee(pipe.Left, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(pipe.Right, targetName, shadowed);
            case Expr.Call call:
                return UsesNameOnlyAsDirectCallee(call.Func, targetName, shadowed, allowDirectCallee: true)
                    && UsesNameOnlyAsDirectCallee(call.Arg, targetName, shadowed);
            case Expr.TupleLit tuple:
                return tuple.Elements.All(elem => UsesNameOnlyAsDirectCallee(elem, targetName, shadowed));
            case Expr.ListLit list:
                return list.Elements.All(elem => UsesNameOnlyAsDirectCallee(elem, targetName, shadowed));
            case Expr.Cons cons:
                return UsesNameOnlyAsDirectCallee(cons.Head, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(cons.Tail, targetName, shadowed);
            case Expr.If iff:
                return UsesNameOnlyAsDirectCallee(iff.Cond, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(iff.Then, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(iff.Else, targetName, shadowed);
            default:
                return null;
        }
    }

    private static bool UsesNameOnlyAsDirectCalleeBinderForms(Expr expr, string targetName, bool shadowed)
    {
        switch (expr)
        {
            case Expr.Let let:
                return UsesNameOnlyAsDirectCallee(let.Value, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(let.Body, targetName, shadowed || string.Equals(let.Name, targetName, StringComparison.Ordinal));
            case Expr.LetResult letResult:
                return UsesNameOnlyAsDirectCallee(letResult.Value, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(letResult.Body, targetName, shadowed || string.Equals(letResult.Name, targetName, StringComparison.Ordinal));
            case Expr.LetRecursive letRecursive:
                {
                    bool nextShadowed = shadowed || string.Equals(letRecursive.Name, targetName, StringComparison.Ordinal);
                    return UsesNameOnlyAsDirectCallee(letRecursive.Value, targetName, nextShadowed)
                        && UsesNameOnlyAsDirectCallee(letRecursive.Body, targetName, nextShadowed);
                }
            case RecursiveGroupExpr group:
                {
                    bool nextShadowed = shadowed
                        || group.Bindings.Any(binding => string.Equals(binding.Name, targetName, StringComparison.Ordinal));
                    return group.Bindings.All(binding =>
                            UsesNameOnlyAsDirectCallee(binding.Value, targetName, nextShadowed))
                        && UsesNameOnlyAsDirectCallee(group.Body, targetName, nextShadowed);
                }
            case Expr.Lambda lam:
                return UsesNameOnlyAsDirectCallee(lam.Body, targetName, shadowed || string.Equals(lam.ParamName, targetName, StringComparison.Ordinal));
            case Expr.Match match:
                return UsesNameOnlyAsDirectCalleeMatch(match, targetName, shadowed);
            case Expr.Await awaitExpr:
                return UsesNameOnlyAsDirectCallee(awaitExpr.Task, targetName, shadowed);
            case Expr.Perform perform:
                return UsesNameOnlyAsDirectCallee(perform.Operation, targetName, shadowed);
            case Expr.Handle:
                // Conservative: a handler's arms may use the name in arbitrary positions.
                return false;
            case Expr.RecordLit rl:
                return rl.Fields.All(f => UsesNameOnlyAsDirectCallee(f.Value, targetName, shadowed));
            case Expr.RecordUpdate ru:
                return UsesNameOnlyAsDirectCallee(ru.Target, targetName, shadowed)
                    && ru.Updates.All(u => UsesNameOnlyAsDirectCallee(u.Value, targetName, shadowed));
            default:
                throw new NotSupportedException(expr.GetType().Name);
        }
    }

    private static bool UsesNameOnlyAsDirectCalleeMatch(Expr.Match match, string targetName, bool shadowed)
    {
        if (!UsesNameOnlyAsDirectCallee(match.Value, targetName, shadowed))
        {
            return false;
        }

        foreach (var matchCase in match.Cases)
        {
            bool caseShadowed = shadowed || PatternBindings(matchCase.Pattern).Any(boundName => string.Equals(boundName, targetName, StringComparison.Ordinal));
            if (matchCase.Guard is not null && !UsesNameOnlyAsDirectCallee(matchCase.Guard, targetName, caseShadowed))
            {
                return false;
            }

            if (!UsesNameOnlyAsDirectCallee(matchCase.Body, targetName, caseShadowed))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// In-place reuse: collects the subset of <paramref name="paramNames"/> that appear as the
    /// scrutinee of a <c>match … with Ctor(…)</c> in <paramref name="e"/> (walking the if/let/match
    /// spine — enough for the common fold shapes). These are the accumulators worth deep-copying once
    /// at loop entry so their nodes can be reused in place. Conservative: missing one only forgoes an
    /// optimization, never correctness.
    /// </summary>
    private static void CollectCtorMatchedScrutinees(Expr e, HashSet<string> paramNames, Dictionary<string, string> result)
    {
        switch (e)
        {
            case Expr.If i:
                CollectCtorMatchedScrutinees(i.Then, paramNames, result);
                CollectCtorMatchedScrutinees(i.Else, paramNames, result);
                break;
            case Expr.Let l:
                CollectCtorMatchedScrutinees(l.Body, paramNames, result);
                break;
            case Expr.LetRecursive lr:
                CollectCtorMatchedScrutinees(lr.Body, paramNames, result);
                break;
            case Expr.Match m:
                if (m.Value is Expr.Var v && paramNames.Contains(v.Name))
                {
                    foreach (var mc in m.Cases)
                    {
                        if (mc.Pattern is Pattern.Constructor ctorPattern)
                        {
                            result[v.Name] = ctorPattern.Name;
                            break;
                        }

                    }
                }

                foreach (var c in m.Cases)
                {
                    CollectCtorMatchedScrutinees(c.Body, paramNames, result);
                }

                break;
        }
    }
}
