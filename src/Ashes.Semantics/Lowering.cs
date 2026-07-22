using System.Diagnostics;
using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    public readonly record struct HoverTypeInfo(TextSpan Span, string? Name, TypeRef Type);

    private readonly Diagnostics _diag;
    private int _nextTempSlot;
    private int _nextLocalSlot;
    private int _nextTypeVar;
    private int _nextLambdaId;
    private int _nextLabelId;

    private readonly List<IrInst> _inst = new();
    private readonly List<IrFunction> _funcs = new();
    private readonly HashSet<IrInst.CallClosure> _borrowedArgumentCalls = new(ReferenceEqualityComparer.Instance);

    // '+' overload resolution. '+' is Int+Int / Float+Float / Str+Str, but the IR op (AddInt vs
    // ConcatStr) must be chosen at lowering time. When both operands are still type variables we
    // can't choose yet, so we emit a provisional AddInt, record it here keyed by object identity,
    // and patch it to ConcatStr/AddFloat once inference resolves the operand type
    // (ResolveDeferredAdds). The shared operand var is added to _addConstrainedTvars so it stays
    // monomorphic (not generalized) — that is what lets a later use resolve it.
    private bool _hasDeferredAdds;
    private readonly List<TypeRef.TVar> _addConstrainedVars = new();

    // Current representative ids of the '+'-constrained type vars (a var may have been unified since
    // it was recorded, so resolve through the union-find each time).
    private HashSet<int> ConstrainedAddVarRepIds()
    {
        var ids = new HashSet<int>();
        foreach (var v in _addConstrainedVars)
        {
            if (Prune(v) is TypeRef.TVar rep)
            {
                ids.Add(rep.Id);
            }
        }

        return ids;
    }

    // Same mechanism as the '+'-constrained vars above, but for '==' / '!=' operand types (Int /
    // Float / Str, no type classes). See ResolveDeferredEqs.
    private bool _hasDeferredEqs;
    private readonly List<TypeRef.TVar> _eqConstrainedVars = new();

    private HashSet<int> ConstrainedEqVarRepIds()
    {
        var ids = new HashSet<int>();
        foreach (var v in _eqConstrainedVars)
        {
            if (Prune(v) is TypeRef.TVar rep)
            {
                ids.Add(rep.Id);
            }
        }

        return ids;
    }

    // Same mechanism as the '+'-constrained vars above, but for '*' operand types (Int / Float /
    // BigInt / UInt, no type classes). When both operands are still type variables we emit a
    // provisional MulInt and keep the shared var monomorphic so a later use resolves it. See
    // ResolveDeferredMuls.
    private bool _hasDeferredMuls;
    private readonly List<TypeRef.TVar> _mulConstrainedVars = new();

    private HashSet<int> ConstrainedMulVarRepIds()
    {
        var ids = new HashSet<int>();
        foreach (var v in _mulConstrainedVars)
        {
            if (Prune(v) is TypeRef.TVar rep)
            {
                ids.Add(rep.Id);
            }
        }

        return ids;
    }
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

    // Source location tracking for debug info
    private string? _currentFilePath;
    private int[]? _lineStarts;
    private int _sourceLength;
    private Expr? _currentSourceExpr;
    private IReadOnlyList<(string FilePath, int StartOffset, int EndOffset)>? _moduleOffsets;
    private int[][]? _moduleLineStarts;

    private readonly bool _hasAshesIO;
    private readonly IReadOnlyDictionary<string, string> _moduleAliases;
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

    // Non-recursive let-bound functions whose body compares (`==`/`!=`) or adds (`+`) two of their
    // own parameters directly, so the operand type is a generalizable type variable rather than a
    // concrete one. `==`/`+` pick a type-specific IR op (CmpIntEq vs CmpStrEq, AddInt vs ConcatStr)
    // that a single shared function can't be polymorphic over, so — exactly like the capability-
    // generic functions above — each concrete call site inlines a fresh copy of the body that
    // resolves the operator at that call's type. This is what lets `assertEqual` (and similar
    // helpers) be used at Str, Int, Bool, and Float within one program. Must be called saturated
    // (a first-class/partial use has no concrete type to specialize at and keeps the shared,
    // Int-defaulted body).
    private readonly HashSet<string> _overloadGenericInline = new(StringComparer.Ordinal);

    // Unqualified alias → stitched canonical name for overload-generic stdlib functions, so a call
    // to `assertEqual` resolves the registration under `Ashes_Test_assertEqual`. Ambiguous short
    // names (two modules exporting the same overload-generic name) are dropped (mapped to null),
    // falling back to today's monomorphic behavior rather than inlining the wrong body.
    private readonly Dictionary<string, string?> _overloadGenericAlias = new(StringComparer.Ordinal);

    // Top-level functions specializable for in-place reuse, by name. Two shapes:
    //   • single-parameter recursion: let rec f = given p -> body (LinearParam = p, ArgCount = 1);
    //   • nested-rec-returning: let f = given a -> ... -> (let rec go = given m -> _ in go) — f isn't
    //     itself recursive but returns a recursive single-param function (LinearParam = m, ArgCount =
    //     outer params + 1, the accumulator being the last applied argument), e.g. Map.set.
    // Applied to a uniquely-owned accumulator (the last arg), f is specialized into an f$reuse clone
    // whose recursive parameter (LinearParam) is a linear reuse root, so its match-then-rebuild
    // reuses the node in place and the recursion stays within the reuse-enabled body.
    private readonly Dictionary<string, (Expr.Lambda Lambda, string LinearParam, int ArgCount)> _specializableFunctions = new(StringComparer.Ordinal);

    // Cache of generated reuse specializations: original name → f$reuse function label.
    private readonly Dictionary<string, string> _reuseSpecializations = new(StringComparer.Ordinal);

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

    // Set only while lowering a direct local record literal whose uses are copy-field reads. This
    // narrow boundary cannot escape through calls, returns, matches, updates, or captured variables.
    private bool _runtimeRcRecordAllocationRequested;

    // Set while lowering a copy-only user ADT that is consumed by its immediately enclosing match.
    private bool _runtimeRcCopyAdtAllocationRequested;
    // Set while lowering a match arm with a runtime reuse token. Unlike the general copy-ADT
    // request, this is restricted to the scrutinee's type so unrelated constructors remain arena
    // managed and cannot consume the token merely because their layouts happen to match.
    private TypeRef.TNamedType? _runtimeRcReuseAllocationTypeRequested;
    private Dictionary<string, bool>? _runtimeRcAdtChildBindings;
    private readonly Stack<List<bool>?> _runtimeManagedMatchResultArms = new();
    private readonly HashSet<int> _runtimeManagedResultTemps = [];
    private bool _runtimeRcStringAllocationRequested;
    private bool _runtimeRcBytesAllocationRequested;
    private bool _runtimeRcBigIntAllocationRequested;

    // Set while lowering a fully fresh list of copy elements consumed by an immediate match.
    private bool _runtimeRcListAllocationRequested;
    private string? _runtimeRcListTailBinding;
    private bool _runtimeRcListTailShared;

    // Accumulator names made uniquely-owned at loop entry (deep-copied) specifically so a call
    // f(acc) to a specializable function can be rewritten to f$reuse(acc). Distinct from
    // _linearReuseNames, which marks accumulators matched directly in the loop body.
    private readonly HashSet<string> _linearSpecializationAccumulators = new(StringComparer.Ordinal);

    // Per-function map from a let-bound local's slot to its binding value AST. Lets the reset-safety
    // check (IsStableAccumulatorExpr) trace a `let m2 = match … in loop(m2)` accumulator back to its
    // binding: m2 is address-stable when every leaf of that match/if is itself stable. Cleared at each
    // function boundary because local slots are numbered per function.
    private readonly Dictionary<int, Expr> _letBindingValues = new();

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
    private readonly Dictionary<string, ConstructorSymbol> _constructorSymbols = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TypeRef.TNamedType> _resolvedTypes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _externalOpaqueTypes = new(StringComparer.Ordinal);
    private readonly List<IrExternalFunction> _externalFunctions = new();

    public IReadOnlyDictionary<string, TypeSymbol> TypeSymbols => _typeSymbols;
    public IReadOnlyDictionary<string, ConstructorSymbol> ConstructorSymbols => _constructorSymbols;
    public IReadOnlyDictionary<string, TypeRef.TNamedType> ResolvedTypes => _resolvedTypes;
    public TypeRef? LastLoweredType { get; private set; }

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

    public string FormatType(TypeRef type)
    {
        return Pretty(type);
    }

    public Lowering(Diagnostics diag, IReadOnlySet<string>? importedStdModules = null, IReadOnlyDictionary<string, string>? moduleAliases = null)
    {
        _diag = diag;
        _hasAshesIO = importedStdModules?.Contains("Ashes.IO") == true;
        _moduleAliases = moduleAliases ?? new Dictionary<string, string>(StringComparer.Ordinal);
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

    public IrProgram Lower(Program program)
    {
        // Type, external, and capability declarations are registered upfront; their relative order among
        // value bindings does not affect visibility under Model-A scoping.
        RegisterTypeDeclarations(program.TypeDecls);
        RegisterExternalDeclarations(program.ExternalDecls);
        RegisterCapabilityDeclarations(program.Items);
        RegisterProviderDeclarations(program.Items);

        // Compile generic (parameterized-`needs`) functions to dictionary-passing form: each needed
        // operation becomes a hidden parameter. Runs after capability/provider registration and
        // before value collection so the rest of the pipeline sees ordinary functions.
        program = RegisterAndTransformDictionaryFunctions(program);

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
        Expr.IntLit or Expr.UIntLit or Expr.BigIntLit or Expr.FloatLit or Expr.StrLit or Expr.BoolLit or Expr.Var or Expr.QualifiedVar => false,
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







    public IrProgram Lower(Expr expr)
    {
        // Entry function lowering (no env/arg params)
        var (resultTemp, resultType) = LowerExpr(expr);
        Emit(new IrInst.Return(resultTemp));

        ResolveDeferredAdds();
        ResolveDeferredMuls();
        ResolveDeferredEqs();
        // After the operator resolutions: argument types the back-edge copy-out decision was
        // waiting on are now as concrete as they will ever be.
        ResolveDeferredTcoResets();

        // Any concrete capability left in the entry expression's row after inference has no handler
        // discharging it — a compile-time error, not a runtime failure.
        CheckUnhandledCapabilities();

        // After ResolveDeferredAdds, an unresolved '+' operand var has been defaulted to Int, so the
        // reported result type (e.g. the REPL's `add : Int -> Int -> Int`) is concrete.
        LastLoweredType = Prune(resultType);

        var entry = new IrFunction(
            Label: "_start_main",
            Instructions: _inst,
            LocalCount: _nextLocalSlot,
            TempCount: _nextTempSlot,
            HasEnvAndArgParams: false,
            LocalNames: new Dictionary<int, string>(_localNames),
            LocalTypes: SnapshotLocalTypes()
        );

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
        };

        return PerceusLifetimePlacement.Place(loweredProgram, _borrowedArgumentCalls);
    }

    /// <summary>Everything a TCO back-edge arena block needs, captured at the back edge so the
    /// block can be generated later (after inference resolves the argument types) at the exact
    /// point marked by an <see cref="IrInst.TcoResetPending"/> placeholder. The
    /// <see cref="ArgTypes"/> are live inference references — pruning them at resolution time
    /// yields the final types. The AST/scope-dependent facts (pass-through, single-fresh-cons,
    /// stable-accumulator) are evaluated eagerly, since the scope is gone by resolution time.</summary>
    /// <summary>
    /// True when a tail-call argument expression rebuilds its list THIS iteration: a call result
    /// (a function's list result is copied out of the callee's arena scope on return, so it is
    /// self-contained), a list literal, or a cons spine ending in one of those. Only such an arg
    /// may take the whole-list DeepAdt clone at the back-edge — the body already paid O(length)
    /// to construct it, so the clone at most doubles that. Anything else (bare var, pattern tail,
    /// cons onto the accumulator param) may share unbounded structure with the previous iteration.
    /// </summary>
    private static bool IsFreshListRebuildExpr(Expr expr)
        => expr switch
        {
            Expr.Call => true,
            Expr.ListLit => true,
            Expr.Cons cons => IsFreshListRebuildExpr(cons.Tail),
            Expr.Let let => IsFreshListRebuildExpr(let.Body),
            _ => false,
        };

    private sealed record PendingTcoReset(
        int[] ArgTemps,
        TypeRef[] ArgTypes,
        bool[] PassThrough,
        bool[] SingleFreshCons,
        bool[] FreshListRebuild,
        bool[] StableAccArg,
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
    /// copy-out with the fixed/advancing watermark choice — from the captured facts. Called inline
    /// at the back edge when every argument type is already resolved, or from
    /// <see cref="ResolveDeferredTcoResets"/> (with <c>_inst</c> pointed at the splice list) when
    /// the decision had to wait for inference.
    /// </summary>
    private void EmitTcoBackEdgeArenaBlock(PendingTcoReset info)
    {
        var argTypes = info.ArgTypes;
        int tcoPreRestoreEndSlot = NewLocal();

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
            if (info.ArgResvStartSlots[r] < 0)
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
                if (info.ArgResvStartSlots[r] >= 0)
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
    /// Replaces every <see cref="IrInst.TcoResetPending"/> placeholder — a back-edge whose copy-out
    /// decision was deferred because an argument type was still an unresolved inference variable —
    /// with the real arena block (or with nothing, when the now-resolved types do not qualify).
    /// Runs at the end of lowering, after the deferred operator resolutions, so the pruned types
    /// are as concrete as they will ever be. Splices in place per function, temporarily pointing
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
        _inst.Clear();
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
                _inst.Add(inst);
            }
        }

        _funcs[fi] = f with { Instructions = new List<IrInst>(_inst), TempCount = _nextTempSlot, LocalCount = _nextLocalSlot };
        _inst.Clear();
        _inst.AddRange(savedInst);
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

    // Patches the provisional AddInts emitted for '+' with two unconstrained operands, now that
    // inference is complete. Any operand var still unbound (e.g. an unused generic '+') defaults to
    // Int. Then each provisional add becomes ConcatStr (Str), AddFloat (Float), or stays AddInt.
    private void ResolveDeferredAdds()
    {
        if (!_hasDeferredAdds)
        {
            return;
        }

        ResolveDeferredAddsIn(_inst);
        foreach (var func in _funcs)
        {
            ResolveDeferredAddsIn(func.Instructions);
        }
    }

    private void ResolveDeferredAddsIn(List<IrInst> instructions)
    {
        for (int i = 0; i < instructions.Count; i++)
        {
            if (instructions[i] is not IrInst.AddInt { DeferredType: { } operandType } add)
            {
                continue;
            }

            // An operand var still unbound (e.g. an unused generic '+') defaults to Int.
            if (Prune(operandType) is TypeRef.TVar)
            {
                Unify(operandType, new TypeRef.TInt());
            }

            instructions[i] = Prune(operandType) switch
            {
                TypeRef.TStr when add.AffineResvStartSlot >= 0 => SetUsesConcatStr(new IrInst.ConcatStrTip(add.Target, add.Left, add.Right, add.AffineResvStartSlot, add.AffineResvEndSlot) { Location = add.Location }),
                TypeRef.TStr => SetUsesConcatStr(new IrInst.ConcatStr(add.Target, add.Left, add.Right) { Location = add.Location }),
                TypeRef.TFloat => new IrInst.AddFloat(add.Target, add.Left, add.Right) { Location = add.Location },
                _ => new IrInst.AddInt(add.Target, add.Left, add.Right) { Location = add.Location },
            };
        }
    }

    private IrInst SetUsesConcatStr(IrInst inst)
    {
        _usesConcatStr = true;
        return inst;
    }

    // Patches the provisional MulInts emitted for '*' with two unconstrained operands, now that
    // inference is complete. Any operand var still unbound defaults to Int. Then each provisional
    // multiply becomes MulFloat (Float), BigIntBinary "mul" (BigInt), or stays MulInt.
    private void ResolveDeferredMuls()
    {
        if (!_hasDeferredMuls)
        {
            return;
        }

        ResolveDeferredMulsIn(_inst);
        foreach (var func in _funcs)
        {
            ResolveDeferredMulsIn(func.Instructions);
        }
    }

    private void ResolveDeferredMulsIn(List<IrInst> instructions)
    {
        for (int i = 0; i < instructions.Count; i++)
        {
            if (instructions[i] is not IrInst.MulInt { DeferredType: { } operandType } mul)
            {
                continue;
            }

            if (Prune(operandType) is TypeRef.TVar)
            {
                Unify(operandType, new TypeRef.TInt());
            }

            instructions[i] = Prune(operandType) switch
            {
                TypeRef.TFloat => new IrInst.MulFloat(mul.Target, mul.Left, mul.Right) { Location = mul.Location },
                TypeRef.TBigInt => new IrInst.BigIntBinary(mul.Target, mul.Left, mul.Right, "mul") { Location = mul.Location },
                _ => new IrInst.MulInt(mul.Target, mul.Left, mul.Right) { Location = mul.Location },
            };
        }
    }

    // Patches the provisional CmpIntEq/CmpIntNe emitted for '==' / '!=' with two unconstrained
    // operands, now that inference is complete. Any operand var still unbound (e.g. an unused
    // generic '==') defaults to Int, matching ResolveDeferredAdds.
    private void ResolveDeferredEqs()
    {
        if (!_hasDeferredEqs)
        {
            return;
        }

        ResolveDeferredEqsIn(_inst);
        foreach (var func in _funcs)
        {
            ResolveDeferredEqsIn(func.Instructions);
        }
    }

    private void ResolveDeferredEqsIn(List<IrInst> instructions)
    {
        for (int i = 0; i < instructions.Count; i++)
        {
            switch (instructions[i])
            {
                case IrInst.CmpIntEq { DeferredType: { } operandType } eq:
                    if (Prune(operandType) is TypeRef.TVar)
                    {
                        Unify(operandType, new TypeRef.TInt());
                    }

                    instructions[i] = Prune(operandType) switch
                    {
                        TypeRef.TStr => new IrInst.CmpStrEq(eq.Target, eq.Left, eq.Right) { Location = eq.Location },
                        TypeRef.TFloat => new IrInst.CmpFloatEq(eq.Target, eq.Left, eq.Right) { Location = eq.Location },
                        _ => new IrInst.CmpIntEq(eq.Target, eq.Left, eq.Right) { Location = eq.Location },
                    };
                    break;

                case IrInst.CmpIntNe { DeferredType: { } operandType } ne:
                    if (Prune(operandType) is TypeRef.TVar)
                    {
                        Unify(operandType, new TypeRef.TInt());
                    }

                    instructions[i] = Prune(operandType) switch
                    {
                        TypeRef.TStr => new IrInst.CmpStrNe(ne.Target, ne.Left, ne.Right) { Location = ne.Location },
                        TypeRef.TFloat => new IrInst.CmpFloatNe(ne.Target, ne.Left, ne.Right) { Location = ne.Location },
                        _ => new IrInst.CmpIntNe(ne.Target, ne.Left, ne.Right) { Location = ne.Location },
                    };
                    break;
            }
        }
    }

    private (int Temp, TypeRef Type) LowerExpr(Expr e)
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
            _currentSourceExpr = previousExpr;
            return (helperLowered.Item1, Prune(helperLowered.Item2));
        }

        var lowered = LowerExprDispatch(e);

        RecordExprHoverType(e, lowered.Type);
        _currentSourceExpr = previousExpr;
        return (lowered.Temp, Prune(lowered.Type));
    }

    private (int Temp, TypeRef Type) LowerExprDispatch(Expr e)
    {
        return e switch
        {
            Expr.IntLit lit => LowerInt(lit),
            Expr.UIntLit lit => LowerUInt(lit),
            Expr.BigIntLit lit => LowerBigIntLit(lit),
            Expr.FloatLit lit => LowerFloat(lit),
            Expr.StrLit str => LowerStr(str),
            Expr.BoolLit b => LowerBool(b),
            Expr.Var v => LowerVar(v),
            Expr.QualifiedVar qv => LowerQualifiedVar(qv),
            Expr.Add add => LowerAdd(add),
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
            Expr.GreaterThan gt => LowerGreaterThan(gt),
            Expr.GreaterOrEqual ge => LowerGreaterOrEqual(ge),
            Expr.LessThan lt => LowerLessThan(lt),
            Expr.LessOrEqual le => LowerLessOrEqual(le),
            Expr.Equal eq => LowerEqual(eq),
            Expr.NotEqual ne => LowerNotEqual(ne),
            Expr.ResultPipe pipe => LowerResultPipe(pipe),
            Expr.ResultMapErrorPipe pipe => LowerResultMapErrorPipe(pipe),
            Expr.Let let => LowerLet(let),
            Expr.LetResult letResult => LowerLetResult(letResult),
            Expr.LetRecursive letRecursive => LowerLetRecursive(letRecursive),
            RecursiveGroupExpr group => LowerRecursiveGroup(group),
            Expr.If iff => LowerIf(iff),
            Expr.Lambda lam => LowerLambda(lam),
            Expr.Call call => LowerCall(call),
            Expr.TupleLit tuple => LowerTupleLit(tuple),
            Expr.ListLit list => LowerListLit(list),
            Expr.Cons cons => LowerCons(cons),
            Expr.Match match => LowerMatch(match),
            Expr.Await awaitExpr => LowerAwait(awaitExpr),
            Expr.RecordLit rl => LowerRecordLit(rl),
            Expr.RecordUpdate ru => LowerRecordUpdate(ru),
            Expr.Perform perform => LowerPerform(perform),
            Expr.Handle handle => LowerHandle(handle),
            CapabilityPostExpr capabilityPost => LowerCapabilityPost(capabilityPost),
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

    private (int, TypeRef) LowerVar(Expr.Var v)
    {
        var b = Lookup(v.Name);
        if (_reuseBindingSeenBySlot.Count > 0 && b is Binding.Local seenLocal
            && _reuseBindingSeenBySlot.ContainsKey(seenLocal.Slot))
        {
            _reuseBindingSeenBySlot[seenLocal.Slot]++;
        }

        if (b is null)
        {
            return LowerVarUnbound(v);
        }

        var result = LowerVarBound(v, b);

        RecordHoverType(GetSpan(v), v.Name, result.Type);

        // Compiler-inferred borrowing.
        // When an owned binding is accessed, emit a Borrow instruction.
        // This tells the IR that we're taking a non-owning reference — the
        // owning scope is still responsible for the Drop.
        var ownerInfo = LookupOwnedValue(v.Name);
        if (ownerInfo is not null && !ownerInfo.IsDropped)
        {
            int borrowTemp = NewTemp();
            Emit(new IrInst.Borrow(borrowTemp, result.Temp));
            ownerInfo.ActiveBorrows++;
            result = (borrowTemp, result.Type);
        }

        return result;
    }

    private (int, TypeRef) LowerVarUnbound(Expr.Var v)
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
            Emit(new IrInst.MakeClosure(closTemp, topRef.Label, envTemp, 0));
            return (closTemp, Instantiate(topRef.Scheme));
        }

        if (_constructorSymbols.TryGetValue(v.Name, out var ctorSym))
        {
            if (ctorSym.Arity == 0)
            {
                return LowerNullaryConstructor(ctorSym);
            }

            return LowerExpr(BuildConstructorLambda(ctorSym));
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

    private (int Temp, TypeRef Type) LowerVarBound(Expr.Var v, Binding b)
    {
        int temp = NewTemp();
        (int Temp, TypeRef Type) result;

        switch (b)
        {
            case Binding.Local loc:
                Emit(new IrInst.LoadLocal(temp, loc.Slot));
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
                Emit(new IrInst.MakeClosure(temp, self.FuncLabel, envTemp, self.EnvSizeBytes));
                result = (temp, self.Type);
                break;

            case Binding.Intrinsic intrinsic:
                ReportDiagnostic(GetSpan(v), $"Intrinsic '{v.Name}' must be called directly.");
                Emit(new IrInst.LoadConstInt(temp, 0));
                result = (temp, intrinsic.Type);
                break;

            case Binding.ExternalFunction externalFunction:
                result = EmitExternalFunctionThunk(externalFunction.Function, externalFunction.Type, GetSpan(v));
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

    private (int, TypeRef) LowerLet(Expr.Let let)
    {
        var savedTailPos = _tcoCtx?.InTailPosition ?? false;
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        // Save the arena watermark before the bound value so allocations from
        // both value and body belong to this let scope.
        EmitArenaWatermark();

        int depth0Before = _depth0LambdaCount;

        var (valueTemp, valueType) = LowerLetAnnotatedValue(let);

        int slot = NewLocal();
        Emit(new IrInst.StoreLocal(slot, valueTemp));
        RecordLocalDebugInfo(slot, let.Name, valueType);
        // Record the binding value so a later tail call `loop(<this name>)` can prove the accumulator
        // address-stable by tracing it back through this let into the value's match/if leaves.
        _letBindingValues[slot] = let.Value;
        var scheme = Generalize(Prune(valueType));
        RecordHoverType(AstSpans.GetLetNameOrDefault(let), let.Name, scheme.Body);

        // Register a top-level, empty-env function so reuse specializations can call it by label. The
        // guard (exactly one depth-0 lambda lowered while lowering this value) means the value is this
        // function's own outer lambda — not a stale label from a sibling or a non-lambda value.
        if (_lambdaDepth == 0 && _depth0LambdaCount == depth0Before + 1 && _lastLoweredLambdaEmptyEnv)
        {
            _topLevelFunctionRefs[let.Name] = (_lastLoweredLambdaLabel, scheme);
        }

        PushLetScope(let, slot, scheme);
        PushOwnershipScope();
        TrackLetOwnership(let, slot, valueTemp, valueType);

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
        var (bodyTemp, bodyType) = LowerExpr(let.Body);
        if (shadowed) PopInlinableShadow(let.Name);

        return PopLetScope(bodyTemp, bodyType);
    }

    // Seed parameter types from the annotation (if any) before lowering the value, so operators on
    // annotated parameters resolve with the annotated numeric type instead of defaulting to Int.
    private (int Temp, TypeRef Type) LowerLetAnnotatedValue(Expr.Let let)
    {
        var annotatedLetType = let.TypeAnnotation is { } letAnnotation ? ResolveAnnotationType(letAnnotation) : null;
        var savedAnnotParams = _annotationParamTypes;
        var savedAnnotCursor = _annotationParamCursor;
        if (annotatedLetType is not null && let.Value is Expr.Lambda letLambda)
        {
            _annotationParamTypes = PeelAnnotationParamTypes(annotatedLetType, CountLambdaChain(letLambda));
            _annotationParamCursor = 0;
        }

        var (valueTemp, valueType) = LowerLetValue(let);
        _annotationParamTypes = savedAnnotParams;
        _annotationParamCursor = savedAnnotCursor;

        // If the user wrote a type annotation, verify it matches the inferred type.
        if (annotatedLetType is not null)
        {
            using var annotationSpan = PushDiagnosticSpan(GetSpan(let.Value));
            Unify(annotatedLetType, valueType);
        }

        return (valueTemp, valueType);
    }

    private (int Temp, TypeRef Type) LowerLetValue(Expr.Let let)
    {
        var stackAllocateClosure = let.Value is Expr.Lambda && UsesNameOnlyAsDirectCallee(let.Body, let.Name);
        if (stackAllocateClosure && let.Value is Expr.Lambda lambda)
        {
            return LowerLambda(lambda, stackAllocateClosure: true);
        }

        var stackAllocateAdt = IsConstructorExpression(let.Value)
            && IsImmediateSingleArmAdtDestructuringMatch(let.Name, let.Body);
        if (stackAllocateAdt && TryLowerConstructorExpression(let.Value, stackAllocate: true, out var loweredAdt))
        {
            return loweredAdt;
        }

        if (IsRuntimeRcStringProducer(let.Value) && IsImmediateRuntimeStringUse(let.Body, let.Name))
        {
            bool savedRequest = _runtimeRcStringAllocationRequested;
            _runtimeRcStringAllocationRequested = true;
            try
            {
                return LowerExpr(let.Value);
            }
            finally
            {
                _runtimeRcStringAllocationRequested = savedRequest;
            }
        }

        if (IsRuntimeRcBytesProducer(let.Value) && IsImmediateRuntimeBytesUse(let.Body, let.Name))
        {
            bool savedRequest = _runtimeRcBytesAllocationRequested;
            _runtimeRcBytesAllocationRequested = true;
            try
            {
                return LowerExpr(let.Value);
            }
            finally
            {
                _runtimeRcBytesAllocationRequested = savedRequest;
            }
        }

        if (TryLowerRuntimeRcRecordLet(let, out (int Temp, TypeRef Type) loweredRecord))
        {
            return loweredRecord;
        }

        if (TryLowerRuntimeRcListLet(let, out (int Temp, TypeRef Type) loweredList))
        {
            return loweredList;
        }

        if (TryLowerRuntimeRcAdtLet(let, out (int Temp, TypeRef Type) loweredAdtValue))
        {
            return loweredAdtValue;
        }

        return TryLowerRuntimeRcBigIntLet(let, out (int Temp, TypeRef Type) loweredBigInt)
            ? loweredBigInt
            : LowerExpr(let.Value);
    }

    private bool TryLowerRuntimeRcBigIntLet(Expr.Let let, out (int Temp, TypeRef Type) lowered)
    {
        if (!IsRuntimeRcBigIntProducer(let.Value) || !IsImmediateRuntimeBigIntUse(let.Body, let.Name))
        {
            lowered = default;
            return false;
        }

        bool savedRequest = _runtimeRcBigIntAllocationRequested;
        _runtimeRcBigIntAllocationRequested = true;
        try
        {
            lowered = LowerExpr(let.Value);
            return true;
        }
        finally
        {
            _runtimeRcBigIntAllocationRequested = savedRequest;
        }
    }

    private bool TryLowerRuntimeRcRecordLet(
        Expr.Let let,
        out (int Temp, TypeRef Type) lowered)
    {
        if (let.Value is not Expr.RecordLit
            || (!IsImmediateCopyUseOfRecord(let.Body, let.Name)
                && !IsImmediateRuntimeRecordMatchUse(let.Body, let.Name)
                && !IsRuntimeManagedRecordChildConsumedByImmediateParent(let.Name, let.Body)))
        {
            lowered = default;
            return false;
        }

        TryCollectRuntimeRcRecordChildBindings(let.Value, let.Body, out Dictionary<string, bool>? childBindings);
        bool savedRequest = _runtimeRcRecordAllocationRequested;
        Dictionary<string, bool>? savedChildBindings = _runtimeRcAdtChildBindings;
        _runtimeRcRecordAllocationRequested = true;
        _runtimeRcAdtChildBindings = childBindings;
        try
        {
            lowered = LowerExpr(let.Value);
            return true;
        }
        finally
        {
            _runtimeRcRecordAllocationRequested = savedRequest;
            _runtimeRcAdtChildBindings = savedChildBindings;
        }
    }

    private bool IsImmediateRuntimeStringUse(Expr body, string bindingName)
    {
        if (body is not Expr.Call(
                Expr.QualifiedVar qualified,
                Expr.Var argument)
            || !string.Equals(argument.Name, bindingName, StringComparison.Ordinal))
        {
            return false;
        }

        string module = ResolveModuleAlias(qualified.Module);
        return (string.Equals(module, "Ashes.Text", StringComparison.Ordinal)
                && string.Equals(qualified.Name, "length", StringComparison.Ordinal))
            || (string.Equals(module, "Ashes.IO", StringComparison.Ordinal)
                && string.Equals(qualified.Name, "print", StringComparison.Ordinal));
    }

    private bool IsRuntimeRcStringProducer(Expr expression)
    {
        if (expression is Expr.Add)
        {
            return true;
        }

        if (expression is Expr.Call(Expr.QualifiedVar textProducer, _)
            && string.Equals(ResolveModuleAlias(textProducer.Module), "Ashes.Text", StringComparison.Ordinal)
            && (string.Equals(textProducer.Name, "fromInt", StringComparison.Ordinal)
                || string.Equals(textProducer.Name, "toHex", StringComparison.Ordinal)))
        {
            return true;
        }

        if (expression is Expr.Call(Expr.QualifiedVar floatProducer, _)
            && string.Equals(ResolveModuleAlias(floatProducer.Module), "Ashes.Text", StringComparison.Ordinal)
            && string.Equals(floatProducer.Name, "fromFloat", StringComparison.Ordinal))
        {
            return true;
        }

        if (expression is Expr.Call(
                Expr.Call(Expr.QualifiedVar formatProducer, _),
                _)
            && string.Equals(ResolveModuleAlias(formatProducer.Module), "Ashes.Text", StringComparison.Ordinal)
            && string.Equals(formatProducer.Name, "formatFloat", StringComparison.Ordinal))
        {
            return true;
        }

        if (expression is Expr.Call(Expr.QualifiedVar caseProducer, _)
            && string.Equals(ResolveModuleAlias(caseProducer.Module), "Ashes.Text", StringComparison.Ordinal)
            && (string.Equals(caseProducer.Name, "asciiUpper", StringComparison.Ordinal)
                || string.Equals(caseProducer.Name, "asciiLower", StringComparison.Ordinal)))
        {
            return true;
        }

        return expression is Expr.Call(
                Expr.Call(
                    Expr.Call(Expr.QualifiedVar qualified, _),
                    _),
                _)
            && string.Equals(ResolveModuleAlias(qualified.Module), "Ashes.Byte", StringComparison.Ordinal)
            && string.Equals(qualified.Name, "subText", StringComparison.Ordinal);
    }

    private bool IsRuntimeRcBytesProducer(Expr expression)
    {
        Expr.QualifiedVar? qualified = expression switch
        {
            Expr.Call(Expr.Call(Expr.QualifiedVar binary, _), _) => binary,
            Expr.Call(Expr.QualifiedVar unary, _) => unary,
            _ => null,
        };
        if (qualified is null
            || !string.Equals(ResolveModuleAlias(qualified.Module), "Ashes.Byte", StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(qualified.Name, "append", StringComparison.Ordinal)
            || string.Equals(qualified.Name, "appendByte", StringComparison.Ordinal)
            || string.Equals(qualified.Name, "fromList", StringComparison.Ordinal)
            || string.Equals(qualified.Name, "singleton", StringComparison.Ordinal)
            || string.Equals(qualified.Name, "empty", StringComparison.Ordinal)
            || string.Equals(qualified.Name, "u16Le", StringComparison.Ordinal)
            || string.Equals(qualified.Name, "u32Le", StringComparison.Ordinal)
            || string.Equals(qualified.Name, "u64Le", StringComparison.Ordinal);
    }

    private bool IsImmediateRuntimeBytesUse(Expr body, string bindingName)
    {
        if (body is not Expr.Call(
                Expr.QualifiedVar qualified,
                Expr.Var argument)
            || !string.Equals(argument.Name, bindingName, StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(ResolveModuleAlias(qualified.Module), "Ashes.Byte", StringComparison.Ordinal)
            && string.Equals(qualified.Name, "length", StringComparison.Ordinal);
    }

    private bool IsRuntimeRcBigIntProducer(Expr expression)
    {
        return expression is Expr.Call(Expr.QualifiedVar qualified, _)
            && string.Equals(ResolveModuleAlias(qualified.Module), "Ashes.Number.BigInt", StringComparison.Ordinal)
            && string.Equals(qualified.Name, "fromInt", StringComparison.Ordinal);
    }

    private bool IsImmediateRuntimeBigIntUse(Expr body, string bindingName)
    {
        if (body is not Expr.Call(
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

    private bool TryLowerRuntimeRcAdtLet(Expr.Let let, out (int Temp, TypeRef Type) lowered)
    {
        bool immediateMatch = IsConstructorExpression(let.Value)
            && IsImmediateSafeAdtMatchUse(let.Name, let.Value, let.Body);
        bool consumedByParent = IsConstructorExpression(let.Value)
            && IsRecursiveAdtChildConsumedByImmediateMatch(let.Name, let.Body);
        if (!immediateMatch && !consumedByParent)
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

        bool savedRequest = _runtimeRcCopyAdtAllocationRequested;
        Dictionary<string, bool>? savedChildBindings = _runtimeRcAdtChildBindings;
        _runtimeRcCopyAdtAllocationRequested = true;
        _runtimeRcAdtChildBindings = childBindings;
        try
        {
            lowered = LowerExpr(let.Value);
            return true;
        }
        finally
        {
            _runtimeRcCopyAdtAllocationRequested = savedRequest;
            _runtimeRcAdtChildBindings = savedChildBindings;
        }
    }

    private bool TryLowerRuntimeRcListLet(Expr.Let let, out (int Temp, TypeRef Type) lowered)
    {
        bool freshRuntimeList = IsFreshListConstructionExpression(let.Value)
            && (IsImmediateCopyListMatchUse(let.Name, let.Body)
                || IsTailConsumedByImmediateListMatch(let.Name, let.Body));
        bool extendsRuntimeList = TryGetRuntimeRcListTailExtension(let.Name, let.Value, let.Body, out string? tailBinding);
        if (!freshRuntimeList && !extendsRuntimeList)
        {
            lowered = default;
            return false;
        }

        bool savedRequest = _runtimeRcListAllocationRequested;
        string? savedTailBinding = _runtimeRcListTailBinding;
        bool savedTailShared = _runtimeRcListTailShared;
        _runtimeRcListAllocationRequested = true;
        _runtimeRcListTailBinding = extendsRuntimeList ? tailBinding : null;
        _runtimeRcListTailShared = tailBinding is not null
            && ExprReferencesName(let.Body, tailBinding, shadowed: false);
        try
        {
            lowered = LowerExpr(let.Value);
            return true;
        }
        finally
        {
            _runtimeRcListAllocationRequested = savedRequest;
            _runtimeRcListTailBinding = savedTailBinding;
            _runtimeRcListTailShared = savedTailShared;
        }
    }

    private static bool IsFreshListConstructionExpression(Expr expression)
        => expression switch
        {
            Expr.ListLit => true,
            Expr.Cons cons => IsFreshListConstructionExpression(cons.Tail),
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
        if (info is not { RuntimeManaged: true, IsDropped: false, Type: TypeRef.TList list }
            || !CanArenaReset(Prune(list.Element)))
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
                && !CanRuntimeManageRecordChildAdt(parentType))
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
                && !CanRuntimeManageRecordChildAdt(resultType)))
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
            Expr.IntLit or Expr.UIntLit or Expr.BigIntLit or Expr.FloatLit or Expr.StrLit or Expr.BoolLit => true,
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
        var prunedValueType = Prune(valueType);
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
            }
        }
    }

    private bool IsRuntimeManagedResultTemp(int valueTemp)
    {
        return _runtimeManagedResultTemps.Contains(valueTemp)
            || _inst.Any(instruction => instruction switch
            {
                IrInst.AllocAdt { Target: var target, RuntimeManaged: true } => target == valueTemp,
                IrInst.AllocReusing { Target: var target, RuntimeManaged: true } => target == valueTemp,
                IrInst.Alloc { Target: var target, RuntimeManaged: true } => target == valueTemp,
                IrInst.ConcatStr { Target: var target, RuntimeManaged: true } => target == valueTemp,
                IrInst.BytesSubText { Target: var target, RuntimeManaged: true } => target == valueTemp,
                IrInst.TextFromInt { Target: var target, RuntimeManaged: true } => target == valueTemp,
                IrInst.TextToHex { Target: var target, RuntimeManaged: true } => target == valueTemp,
                IrInst.TextAsciiCase { Target: var target, RuntimeManaged: true } => target == valueTemp,
                IrInst.TextFromFloat { Target: var target, RuntimeManaged: true } => target == valueTemp,
                IrInst.TextFormatFloat { Target: var target, RuntimeManaged: true } => target == valueTemp,
                IrInst.BigIntFromInt { Target: var target, RuntimeManaged: true } => target == valueTemp,
                IrInst.BytesAppend { Target: var target, RuntimeManaged: true } => target == valueTemp,
                IrInst.BytesAppendByte { Target: var target, RuntimeManaged: true } => target == valueTemp,
                IrInst.BytesFromList { Target: var target, RuntimeManaged: true } => target == valueTemp,
                IrInst.BytesSingleton { Target: var target, RuntimeManaged: true } => target == valueTemp,
                IrInst.BytesEmpty { Target: var target, RuntimeManaged: true } => target == valueTemp,
                IrInst.BytesU16Le { Target: var target, RuntimeManaged: true } => target == valueTemp,
                IrInst.BytesU32Le { Target: var target, RuntimeManaged: true } => target == valueTemp,
                IrInst.BytesU64Le { Target: var target, RuntimeManaged: true } => target == valueTemp,
                _ => false,
            });
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
            return (resultTemp, bodyType);
        }

        int finalScopeTemp = PopOwnershipScope(bodyType, bodyTemp);
        _scopes.Pop();
        return (finalScopeTemp, bodyType);
    }

    private (int, TypeRef) LowerLetResult(Expr.LetResult letResult)
    {
        using var diagnosticSpan = PushDiagnosticSpan(letResult);
        if (!TryGetStandardResultParts(out var resultSymbol, out var okConstructor, out _))
        {
            return ReturnNeverWithDummyTemp();
        }

        var savedTailPos = _tcoCtx?.InTailPosition ?? false;
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        var (valueTemp, valueType) = LowerExpr(letResult.Value);
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
        var (bodyTemp, bodyType) = LowerExpr(letResult.Body);
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

    private (int, TypeRef) LowerLetRecursive(Expr.LetRecursive letRecursive)
    {
        int slot = NewLocal();
        // The module system may wrap a lambda in alias lets: let alias = mangled in given (x) -> ...
        // Unwrap let-chains to find the innermost lambda for type and TCO purposes.
        var innerLambda = FindInnermostLambdaUnderLets(letRecursive.Value);
        var recursiveType = LowerLetRecursiveBindSelf(letRecursive, innerLambda, slot);

        var (savedAnnotationParamTypes, savedAnnotationParamCursor) = LowerLetRecursiveSeedAnnotation(letRecursive, innerLambda, recursiveType);

        (int valTemp, TypeRef valType) valueAndType;
        bool helperMarkerAdded = false;
        if (letRecursive.Value is Expr.Lambda lam
            && _inCoroutineBody
            && !IsAsyncIntrinsicCall(GetInnermostBody(lam))
            && ContainsAwaitOutsideNestedLambda(GetInnermostBody(lam)))
        {
            valueAndType = LowerLetRecursiveCoroutineHelperValue(letRecursive, lam, recursiveType, out helperMarkerAdded);
        }
        else if (letRecursive.Value is Expr.Lambda lam2)
        {
            valueAndType = LowerLetRecursiveLambdaValue(letRecursive, lam2, recursiveType);
        }
        else if (innerLambda is not null)
        {
            valueAndType = LowerLetRecursiveAliasChainValue(letRecursive, innerLambda, recursiveType);
        }
        else
        {
            ReportDiagnostic(GetSpan(letRecursive.Value), "let recursive currently requires a function value.");
            valueAndType = LowerExpr(letRecursive.Value);
        }

        _annotationParamTypes = savedAnnotationParamTypes;
        _annotationParamCursor = savedAnnotationParamCursor;

        LowerLetRecursiveFinalizeValue(letRecursive, slot, recursiveType, valueAndType);

        var (bodyTemp, bodyType) = LowerExpr(letRecursive.Body);
        if (helperMarkerAdded)
        {
            _coroutineHelperArity.Remove(letRecursive.Name);
        }

        _scopes.Pop();
        return (bodyTemp, bodyType);
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
    private (IReadOnlyList<TypeRef>? SavedTypes, int SavedCursor) LowerLetRecursiveSeedAnnotation(Expr.LetRecursive letRecursive, Expr.Lambda? innerLambda, TypeRef recursiveType)
    {
        var savedAnnotationParamTypes = _annotationParamTypes;
        var savedAnnotationParamCursor = _annotationParamCursor;
        _annotationParamTypes = null;
        _annotationParamCursor = 0;
        if (letRecursive.TypeAnnotation is { } seedAnnotation && innerLambda is not null)
        {
            var seedAnnotationType = ResolveAnnotationType(seedAnnotation);
            Unify(recursiveType, seedAnnotationType);
            _annotationParamTypes = PeelAnnotationParamTypes(seedAnnotationType, CountLambdaChain(innerLambda));
            _annotationParamCursor = 0;
        }

        return (savedAnnotationParamTypes, savedAnnotationParamCursor);
    }

    // Async tail-recursive loop: the helper's body awaits and it is defined inside a coroutine
    // body, so lower it as a task-returning closure around a transparent coroutine (awaits
    // suspend on the enclosing run; self tail calls restart the coroutine in place). Without
    // this, every await in the helper would compile to a nested blocking scheduler run.
    private (int, TypeRef) LowerLetRecursiveCoroutineHelperValue(Expr.LetRecursive letRecursive, Expr.Lambda lam, TypeRef recursiveType, out bool helperMarkerAdded)
    {
        var loopParamCount = CountLambdaChain(lam);
        var savedPending = _pendingHelperCoroutine;
        var savedHelperTco = _tcoCtx;
        _tcoCtx = null;
        _pendingHelperCoroutine = new HelperCoroutineInfo(letRecursive.Name, CollectLambdaParams(lam), GetInnermostBody(lam));
        helperMarkerAdded = !_coroutineHelperArity.ContainsKey(letRecursive.Name);
        if (helperMarkerAdded)
        {
            _coroutineHelperArity[letRecursive.Name] = loopParamCount;
        }

        var valueAndType = LowerLambdaRecursive(letRecursive.Name, recursiveType, lam);
        _pendingHelperCoroutine = savedPending;
        _tcoCtx = savedHelperTco;
        return valueAndType;
    }

    private (int, TypeRef) LowerLetRecursiveLambdaValue(Expr.LetRecursive letRecursive, Expr.Lambda lam2, TypeRef recursiveType)
    {
        // Detect lambda chain for TCO: given (x) -> given (y) -> body
        var paramCount = CountLambdaChain(lam2);
        var innermostBody = GetInnermostBody(lam2);
        var hasTailSelfCalls = HasTailSelfCalls(innermostBody, letRecursive.Name, paramCount);

        var savedTcoCtx = _tcoCtx;
        if (hasTailSelfCalls)
        {
            var tcoParamNames = CollectLambdaParams(lam2);
            _tcoCtx = new TcoContext
            {
                SelfName = letRecursive.Name,
                ParamCount = paramCount,
                ParamNames = tcoParamNames,
                InTailPosition = false,
                LoopInvariantParams = CollectLoopInvariantParams(GetInnermostBody(lam2), tcoParamNames, letRecursive.Name),
                AffineStrParams = CollectAffineAccumulators(GetInnermostBody(lam2), tcoParamNames, letRecursive.Name)
            };
        }
        else
        {
            _tcoCtx = null;
        }

        var valueAndType = LowerLambdaRecursive(letRecursive.Name, recursiveType, lam2);

        _tcoCtx = savedTcoCtx;
        return valueAndType;
    }

    // Value is a let-chain of alias bindings (injected by the module system) wrapping a lambda.
    // Process each alias let into scope first, then lower the innermost lambda with the
    // self-reference (selfName) set so that recursive calls use Binding.Self rather than
    // capturing the uninitialized slot value (which would be 0 at closure-creation time).
    //
    // Self-aliases (let unmangledName = mangledSelf) must NOT be processed as regular lets
    // because the mangled slot is uninitialized at this point. Instead, they are collected
    // as selfAliases and given Binding.Self treatment inside LowerLambdaCore.
    private (int, TypeRef) LowerLetRecursiveAliasChainValue(Expr.LetRecursive letRecursive, Expr.Lambda innerLambda, TypeRef recursiveType)
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
                var (aliasValueTemp, aliasValueType) = LowerExpr(aliasLet.Value);
                int aliasSlot = NewLocal();
                Emit(new IrInst.StoreLocal(aliasSlot, aliasValueTemp));
                RecordLocalDebugInfo(aliasSlot, aliasLet.Name, aliasValueType);
                var aliasScheme = Generalize(Prune(aliasValueType));
                RecordHoverType(AstSpans.GetLetNameOrDefault(aliasLet), aliasLet.Name, aliasScheme.Body);
                PushLetScope(aliasLet, aliasSlot, aliasScheme);
                aliasCount++;
            }

            aliasExpr = aliasLet.Body;
        }

        var valueAndType = LowerLambdaRecursive(letRecursive.Name, recursiveType, innerLambda, selfAliases: selfAliases);

        for (int i = 0; i < aliasCount; i++)
        {
            _scopes.Pop();
        }

        _tcoCtx = savedTcoCtx;
        return valueAndType;
    }

    // Unifies the lowered value with the self-type (and the declared annotation, if any), records
    // hover info, and stores the closure into the recursive slot.
    private void LowerLetRecursiveFinalizeValue(Expr.LetRecursive letRecursive, int slot, TypeRef recursiveType, (int valTemp, TypeRef valType) valueAndType)
    {
        Unify(recursiveType, valueAndType.valType);

        // If the user wrote a type annotation, verify it matches the inferred type.
        if (letRecursive.TypeAnnotation is { } recursiveAnnotation)
        {
            using var annotationSpan = PushDiagnosticSpan(GetSpan(letRecursive.Value));
            var annotatedRecursiveType = ResolveAnnotationType(recursiveAnnotation);
            Unify(annotatedRecursiveType, recursiveType);
        }

        RecordHoverType(AstSpans.GetLetRecursiveNameOrDefault(letRecursive), letRecursive.Name, recursiveType);
        Emit(new IrInst.StoreLocal(slot, valueAndType.valTemp));

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
            var selfScope = _scopes.Pop();
            var helperScheme = FreshenScheme(Generalize(Prune(recursiveType)));
            _scopes.Push(selfScope);
            _topLevelFunctionRefs[letRecursive.Name] = (_lastLoweredLambdaLabel, helperScheme);
        }
    }



































    private (int, TypeRef) LowerIf(Expr.If iff)
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
        var (tTemp, tType) = LowerExpr(iff.Then);
        EndExclusiveBranch(thenCredits);
        var thenType = Prune(tType);
        Emit(new IrInst.StoreLocal(slot, tTemp));

        Emit(new IrInst.Jump(endLabel));
        Emit(new IrInst.Label(elseLabel));

        _reuseTokens.Clear();
        _reuseTokens.AddRange(reuseTokensAtIf);

        if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;
        var elseCredits = BeginExclusiveBranch([iff.Then]);
        var (eTemp, eType) = LowerExpr(iff.Else);
        EndExclusiveBranch(elseCredits);
        var elseType = Prune(eType);
        Emit(new IrInst.StoreLocal(slot, eTemp));

        // unify branch types
        using (PushDiagnosticContext("in if branches"))
        {
            Unify(thenType, elseType);
        }

        // if expression result: put into a temp (phi) by storing chosen into target
        int target = NewTemp();
        Emit(new IrInst.Label(endLabel));
        Emit(new IrInst.LoadLocal(target, slot));

        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        var resultType = thenType is TypeRef.TNever ? elseType : thenType;
        return (target, Prune(resultType));
    }

    private (int, TypeRef) LowerLambda(Expr.Lambda lam, bool stackAllocateClosure = false)
    {
        return LowerLambdaCore(lam, null, null, stackAllocateClosure);
    }

    private (int, TypeRef) LowerLambdaRecursive(string selfName, TypeRef selfType, Expr.Lambda lam, bool stackAllocateClosure = false, IReadOnlyList<string>? selfAliases = null)
    {
        return LowerLambdaCore(lam, selfName, selfType, stackAllocateClosure, selfAliases);
    }

    private (int, TypeRef) LowerLambdaCore(Expr.Lambda lam, string? selfName, TypeRef? selfType, bool stackAllocateClosure, IReadOnlyList<string>? selfAliases = null, RecursiveGroupContext? recursiveGroup = null, string? forcedLabel = null)
    {
        _usesClosures = true;

        // Create type variables for param, return, and the arrow's capability row. The row variable
        // becomes the body's ambient row: every operation performed and capability-performing call made while
        // lowering the body inserts its capabilities there, so the arrow ends up carrying exactly the
        // capabilities the body performs (open, generalized at the enclosing let).
        var paramTy = NewTypeVar();
        var retTy = NewTypeVar();
        var rowTy = NewTypeVar();
        var funTy = new TypeRef.TFun(paramTy, retTy) { Row = rowTy };
        LowerLambdaCoreSeedParamType(lam, paramTy);

        // Compute free variables for capture, then allocate and fill the env at the creation site.
        var (free, captures, envPtrTemp) = LowerLambdaCoreBuildEnv(lam, selfName, recursiveGroup, stackAllocateClosure);

        // Create lambda function label
        string label = forcedLabel ?? $"lambda_{_nextLambdaId++}";

        // Build function body IR in isolation
        var savedFrame = LowerLambdaCoreSaveFrame(label, captures);
        int argSlot = LowerLambdaCoreResetFrame();
        RecordLocalDebugInfo(argSlot, lam.ParamName, paramTy);
        LowerLambdaCoreBuildScope(lam, label, paramTy, argSlot, free, captures, selfName, selfType, selfAliases, recursiveGroup, savedFrame.Scopes);

        // TCO: for the innermost lambda in a recursive chain, create local copies of captured params
        // and emit a loop start label so tail self-calls can jump back (see the loop-entry helper).
        bool wasDescendingChain = _tcoCtx?.DescendingChain ?? false;
        bool isChainLambda = wasDescendingChain;
        var isInnermostTco = isChainLambda && lam.Body is not Expr.Lambda;
        var reuseDefensiveCopy = new List<(int Slot, TypeRef TypeRef)>();
        var directReuseSlots = new HashSet<int>();
        var specElidedAccs = new HashSet<string>(StringComparer.Ordinal);
        int reuseInsertIndex = -1;
        if (isInnermostTco)
        {
            reuseInsertIndex = LowerLambdaCoreEnterTcoLoop(lam, label, captures, reuseDefensiveCopy, directReuseSlots, specElidedAccs);
        }

        var outerTcoCtx = LowerLambdaCoreSuspendOuterTco(isChainLambda, lam);
        var savedTcoCtx = isInnermostTco ? outerTcoCtx : null;
        var (bodyTemp, bodyType) = LowerLambdaCoreLowerBody(lam, rowTy, selfName);
        if (isInnermostTco && savedTcoCtx is not null) savedTcoCtx.InTailPosition = false;

        LowerLambdaCoreSpliceReuseCopies(reuseDefensiveCopy, directReuseSlots, reuseInsertIndex);

        LowerLambdaCoreRecordAccStableFold(lam, savedTcoCtx, specElidedAccs);

        _tcoCtx = outerTcoCtx;
        if (isChainLambda) _tcoCtx!.DescendingChain = wasDescendingChain;

        Unify(bodyType, retTy);
        Emit(new IrInst.Return(bodyTemp));

        LowerLambdaCoreFinishFunction(label);
        LowerLambdaCoreRestoreFrame(savedFrame);

        int closureTemp = LowerLambdaCoreMakeClosure(label, envPtrTemp, captures, stackAllocateClosure);
        return (closureTemp, funTy);
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
            && _annotationParamCursor < annotationParamTypes.Count)
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

    private (HashSet<string> Free, IReadOnlyList<string> Captures, int EnvPtrTemp) LowerLambdaCoreBuildEnv(Expr.Lambda lam, string? selfName, RecursiveGroupContext? recursiveGroup, bool stackAllocateClosure)
    {
        var bound = new HashSet<string>(StringComparer.Ordinal) { lam.ParamName };
        if (selfName is not null)
        {
            bound.Add(selfName);
        }

        var free = FreeVars(lam.Body, bound);

        // For a mutual-recursion group member, every member shares one identical environment so a
        // sibling's closure can be reconstructed (via Binding.Self) using the current member's env.
        // Otherwise compute this lambda's own captures and build its env. Remove vars that are not in
        // scope (should not happen if earlier checks).
        var captures = recursiveGroup is not null
            ? recursiveGroup.SharedCaptures
            : free.Where(n => Lookup(n) is Binding.Local or Binding.Env or Binding.EnvScheme or Binding.Self or Binding.Scheme).Distinct(StringComparer.Ordinal).ToList();

        // At lambda creation site: allocate env if needed
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
                Emit(new IrInst.Alloc(envPtrTemp, captures.Count * 8));
            }

            for (int i = 0; i < captures.Count; i++)
            {
                var (capTemp, capTy) = LowerVar(new Expr.Var(captures[i]));
                // store capTemp into [envPtr + i*8]
                Emit(new IrInst.StoreMemOffset(envPtrTemp, i * 8, capTemp));
                // Constrain types: the captured binding type should match capTy; already does.
            }
        }

        return (free, captures, envPtrTemp);
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
        Dictionary<int, Expr> LetBindingValues);

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
        var savedLetBindingValues = new Dictionary<int, Expr>(_letBindingValues);
        _linearReuseNames.Clear();
        _reuseTokens.Clear();
        _linearSpecializationAccumulators.Clear();
        _resetSafeAccumulators.Clear();
        _reuseResultTemps.Clear();
        _letBindingValues.Clear();

        return new LowerLambdaCoreFrame(
            savedInst, savedTemp, savedLocal, savedScopes, savedInCoroutineBody,
            savedLocalNames, savedLocalTypes, savedLinearReuseNames, savedReuseTokens,
            savedSpecAccumulators, savedResetSafe, savedReuseResultTemps, savedLetBindingValues);
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

    private void LowerLambdaCoreBuildScope(Expr.Lambda lam, string label, TypeRef paramTy, int argSlot, HashSet<string> free, IReadOnlyList<string> captures, string? selfName, TypeRef? selfType, IReadOnlyList<string>? selfAliases, RecursiveGroupContext? recursiveGroup, Dictionary<string, Binding>[] enclosingScopes)
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

            if (capBinding is Binding.Scheme capScheme)
            {
                scope[captures[i]] = new Binding.EnvScheme(i, capScheme.S, capScheme.DefinitionSpan);
            }
            else
            {
                scope[captures[i]] = new Binding.Env(i, capBinding.Type, capBinding.DefinitionSpan);
            }
        }
        if (recursiveGroup is not null)
        {
            // Bind every group member (this one and its siblings) so each resolves to its own IR
            // function. Reconstructing a sibling's closure uses this member's env (LoadLocal 0), which
            // is correct precisely because the whole group shares one identical environment layout.
            foreach (var member in recursiveGroup.Members)
            {
                scope[member.Name] = new Binding.Self(member.Label, member.Type, captures.Count * 8, member.Span);
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
        RecordHoverType(paramSpan, lam.ParamName, paramTy);
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
    private int LowerLambdaCoreEnterTcoLoop(Expr.Lambda lam, string label, IReadOnlyList<string> captures, List<(int Slot, TypeRef TypeRef)> reuseDefensiveCopy, HashSet<int> directReuseSlots, HashSet<string> specElidedAccs)
    {
        var tco = _tcoCtx!;
        var scope = _scopes.Peek();
        tco.ParamSlots.Clear();

        LowerLambdaCoreBindTcoParamSlots(lam, captures, scope, tco);

        var reuseParamNames = new HashSet<string>(tco.ParamNames, StringComparer.Ordinal) { lam.ParamName };
        int reuseInsertIndex = LowerLambdaCoreScanDirectReuse(lam, tco, reuseParamNames, reuseDefensiveCopy, directReuseSlots, specElidedAccs);
        LowerLambdaCoreScanSpecializationReuse(lam, tco, reuseParamNames, reuseDefensiveCopy, specElidedAccs);
        LowerLambdaCoreEmitTcoLoopEntry(label, tco);

        tco.InTailPosition = true;
        return reuseInsertIndex;
    }

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

        // Build ParamSlots in PARAMETER (declaration/application) order, so ParamSlots[i] is the slot
        // of the i-th curried parameter — which is also the i-th collected back-edge argument. The
        // captured params were just bound to fresh locals in capture-DISCOVERY order (the order free
        // variables appear in the body), which need not match declaration order; indexing the slots by
        // capture order stored each back-edge argument into the wrong parameter's slot (a swap that
        // corrupts both when, e.g., a string and a list parameter are captured in reverse order).
        // Every parameter — including the innermost, bound to argSlot — resolves through the scope.
        foreach (var pname in tco.ParamNames)
        {
            if (scope.TryGetValue(pname, out var pBinding) && pBinding is Binding.Local pLocal)
            {
                tco.ParamSlots.Add(pLocal.Slot);
            }
            else
            {
                throw new InvalidOperationException($"TCO parameter '{pname}' has no local slot for the back-edge.");
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
    private int LowerLambdaCoreScanDirectReuse(Expr.Lambda lam, TcoContext tco, HashSet<string> reuseParamNames, List<(int Slot, TypeRef TypeRef)> reuseDefensiveCopy, HashSet<int> directReuseSlots, HashSet<string> specElidedAccs)
    {
        _linearReuseNames.Clear();
        var reuseScan = new Dictionary<string, string>(StringComparer.Ordinal);
        CollectCtorMatchedScrutinees(lam.Body, reuseParamNames, reuseScan);
        int reuseInsertIndex = _inst.Count;
        foreach (var (accName, ctorName) in reuseScan)
        {
            if (_scopes.Peek().TryGetValue(accName, out var accBinding)
                && accBinding is Binding.Local accLocal
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
                // directReuseSlots so the non-structural-reuse revert below still governs it — a
                // move-safe *pure reader* (nullary-only reuse, result type ≠ accumulator) must
                // still fall back to a fresh allocation so its returned cell is not a reused
                // accumulator cell. When the reuse is structural, the AllocReusing fires in place
                // against the already-unique accumulator with no copy — the actual win.
                directReuseSlots.Add(accLocal.Slot);
                bool elideDirect = tco.SelfName.Length > 0
                    && ReuseAccumulatorIsUnique(tco.SelfName, accName);
                if (!elideDirect)
                {
                    reuseDefensiveCopy.Add((accLocal.Slot, accLocal.T));
                }
                else
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
    private void LowerLambdaCoreScanSpecializationReuse(Expr.Lambda lam, TcoContext tco, HashSet<string> reuseParamNames, List<(int Slot, TypeRef TypeRef)> reuseDefensiveCopy, HashSet<string> specElidedAccs)
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
                && Lookup(funcName) is { } funcBinding
                && _specializableFunctions.TryGetValue(funcName, out var funcSpec)
                && NthCurriedArgType(Prune(funcBinding.Type), funcSpec.ArgCount - 1) is TypeRef.TNamedType paramNamed
                && !BuiltinRegistry.IsResourceTypeName(paramNamed.Symbol.Name)
                && !IsResourceBearing(paramNamed)
                && !CanCopyOutAdt(paramNamed, out _)
                && TrySynthesizeAdtCopier(paramNamed) is not null)
            {
                _linearSpecializationAccumulators.Add(accName);

                // Move/linearity elision: the entry deep-copy exists only to make the
                // accumulator uniquely owned so f$reuse may overwrite it in place. When the
                // whole-program move analysis proves the accumulator is already uniquely owned
                // at every external call site of this fold (moved, unaliased, seeded from a
                // never-overwritable value), the copy is redundant and re-executes on every
                // re-entry (the nested-reuse leak). Skip it only when provably safe; the
                // conservative default keeps the copy.
                bool elide = tco.SelfName.Length > 0
                    && ReuseAccumulatorIsUnique(tco.SelfName, accName);
                if (!elide)
                {
                    reuseDefensiveCopy.Add((accL.Slot, accL.T));
                }
                else
                {
                    specElidedAccs.Add(accName);
                }
            }
        }
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
        foreach (var affineParam in tco.AffineStrParams)
        {
            int resvStart = NewLocal();
            int resvEnd = NewLocal();
            Emit(new IrInst.StoreLocal(resvStart, compactionZero));
            Emit(new IrInst.StoreLocal(resvEnd, compactionZero));
            tco.AffineResvSlots[affineParam] = (resvStart, resvEnd);
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
            _tcoCtx!.DescendingChain = lam.Body is Expr.Lambda;
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
        bool pushedDictShadow = PushDictFnShadow(lam.ParamName, selfName);
        var savedAmbientRow = _ambientRow;
        _ambientRow = rowTy;
        var (bodyTemp, bodyType) = LowerExpr(lam.Body);
        _ambientRow = savedAmbientRow;
        PopDictFnShadow(lam.ParamName, pushedDictShadow);
        ExitOpParamScope(opParamScope);
        if (paramShadowsInlinable) PopInlinableShadow(lam.ParamName);
        return (bodyTemp, bodyType);
    }

    // In-place reuse: now that the body is lowered and the accumulators' types are
    // resolved, generate the one-time defensive deep copies and splice them in at loop entry
    // (before the body label, recorded as reuseInsertIndex). Generated at the end of _inst, then
    // moved up — the block is self-contained (loads the slot, deep-copies, stores it back).
    // Run when there is any copy to emit, or any direct-reuse slot whose copy was elided by the
    // move analysis (directReuseSlots without a matching reuseDefensiveCopy entry) — the latter
    // still needs the non-structural-reuse revert below to protect a move-safe pure reader.
    private void LowerLambdaCoreSpliceReuseCopies(List<(int Slot, TypeRef TypeRef)> reuseDefensiveCopy, HashSet<int> directReuseSlots, int reuseInsertIndex)
    {
        if ((reuseDefensiveCopy.Count == 0 && directReuseSlots.Count == 0) || reuseInsertIndex < 0)
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
        bool structuralReuse = false;
        for (int i = reuseInsertIndex; i < _inst.Count; i++)
        {
            if (_inst[i] is IrInst.AllocReusing { FieldCount: > 0 })
            {
                structuralReuse = true;
                break;
            }
        }

        if (!structuralReuse)
        {
            for (int i = reuseInsertIndex; i < _inst.Count; i++)
            {
                if (_inst[i] is IrInst.AllocReusing ar)
                {
                    _inst[i] = new IrInst.AllocAdt(ar.Target, ar.Tag, ar.FieldCount) { Location = ar.Location };
                }
            }
        }

        int genStart = _inst.Count;
        foreach (var (slot, typeRef) in reuseDefensiveCopy)
        {
            if (directReuseSlots.Contains(slot) && !structuralReuse)
            {
                continue;
            }

            int loaded = NewTemp();
            Emit(new IrInst.LoadLocal(loaded, slot));
            int copied = EmitDeepCopy(loaded, Prune(typeRef));
            Emit(new IrInst.StoreLocal(slot, copied));
        }

        int genCount = _inst.Count - genStart;
        var generated = _inst.GetRange(genStart, genCount);
        _inst.RemoveRange(genStart, genCount);
        _inst.InsertRange(reuseInsertIndex, generated);
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

    private void LowerLambdaCoreFinishFunction(string label)
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

        _funcs.Add(func);
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
        _reuseTokens.Clear();
        _reuseTokens.AddRange(frame.ReuseTokens);
        _letBindingValues.Clear();
        foreach (var kv in frame.LetBindingValues) _letBindingValues[kv.Key] = kv.Value;
    }

    private int LowerLambdaCoreMakeClosure(string label, int envPtrTemp, IReadOnlyList<string> captures, bool stackAllocateClosure)
    {
        // Produce closure object: alloc 24 bytes and store (code_ptr, env_ptr, env_size)
        int closureTemp = NewTemp();
        int envSizeBytes = captures.Count * 8;
        if (stackAllocateClosure)
        {
            Emit(new IrInst.MakeClosureStack(closureTemp, label, envPtrTemp, envSizeBytes));
        }
        else
        {
            Emit(new IrInst.MakeClosure(closureTemp, label, envPtrTemp, envSizeBytes));
        }

        // Record any resource captured by this closure, with its env offset (capture i lives at
        // env+i*8) and type. Ownership scopes are separate from binding scopes, so the captured
        // names still resolve to their owning bindings here.
        var resourceCaptures = new List<(int EnvOffset, string Name, TypeRef Type)>();
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
        }

        if (resourceCaptures.Count > 0)
        {
            _closureResourceCaptures[closureTemp] = resourceCaptures;
        }

        return closureTemp;
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
            // A '+'- or '*'-constrained var is a numeric scalar (never a function), so the arity is
            // exact even though it is not yet a concrete type. This keeps oversaturated-call
            // detection working for functions like `add a b = a + b` / `mul a b = a * b`.
            if (ConstrainedAddVarRepIds().Contains(resultVar.Id) || ConstrainedMulVarRepIds().Contains(resultVar.Id))
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


















    private (int, TypeRef) LowerCall(Expr.Call call)
    {
        using var diagnosticSpan = PushDiagnosticSpan(call);
        // Collect all args from the call chain to support multi-arg constructor application:
        //   Pair(1, 2) is parsed as Call(Call(Var("Pair"), 1), 2) — collect [1, 2] with root Var("Pair")
        var collectedArgs = new List<Expr>();
        var rootExpr = CollectCallArgs(call, collectedArgs);

        if (LowerCallTryDirectForms(call, rootExpr, collectedArgs) is { } directResult)
        {
            return directResult;
        }

        if (LowerCallTryParallelAndReuseForms(call, rootExpr, collectedArgs) is { } routedResult)
        {
            return routedResult;
        }

        if (LowerCallTryGenericInlineForm(rootExpr, collectedArgs) is { } genericInlineResult)
        {
            return genericInlineResult;
        }

        if (LowerCallTryReuseInlineForm(rootExpr, collectedArgs) is { } reuseInlineResult)
        {
            return reuseInlineResult;
        }

        // TCO: detect tail-position self-call chain: f(a1)(a2)...(aN)
        if (_tcoCtx is { InTailPosition: true } tco
            && rootExpr is Expr.Var selfVar
            && string.Equals(selfVar.Name, tco.SelfName, StringComparison.Ordinal)
            && collectedArgs.Count == tco.ParamCount)
        {
            return LowerCallTcoSelfCall(tco, collectedArgs);
        }

        if (LowerCallTryCoroutineHelperForm(call, rootExpr, collectedArgs) is { } helperResult)
        {
            return helperResult;
        }

        if (rootExpr is Expr.Var varFunc && Lookup(varFunc.Name) is Binding.Intrinsic intrinsic)
        {
            return LowerCallIntrinsic(rootExpr, intrinsic, collectedArgs);
        }

        if (rootExpr is Expr.Var externalVar && Lookup(externalVar.Name) is Binding.ExternalFunction externalFunction)
        {
            return LowerExternalCall(rootExpr, externalFunction.Function, collectedArgs);
        }

        // Qualified intrinsic call: Ashes.IO.print(...), Ashes.IO.panic(...)
        if (rootExpr is Expr.QualifiedVar qv && LowerCallQualifiedBuiltin(rootExpr, qv, collectedArgs) is { } builtinResult)
        {
            return builtinResult;
        }

        return LowerCallGeneral(call, rootExpr, collectedArgs);
    }

    // Direct call forms resolved from the root expression alone: constructor application, the
    // built-in Stop.stop, a capability operation call, and a dictionary-passing generic function.
    private (int, TypeRef)? LowerCallTryDirectForms(Expr.Call call, Expr rootExpr, List<Expr> collectedArgs)
    {
        if (rootExpr is Expr.Var varCtor && _constructorSymbols.TryGetValue(varCtor.Name, out var ctorSym))
        {
            return LowerConstructorApplication(ctorSym, collectedArgs);
        }

        // Capability operation call: Clock.now(x) — the implicit form of `perform Clock.now(x)`.
        if (rootExpr is Expr.QualifiedVar stopQv
            && string.Equals(stopQv.Module, "Stop", StringComparison.Ordinal)
            && string.Equals(stopQv.Name, "stop", StringComparison.Ordinal)
            && collectedArgs.Count > 0)
        {
            return LowerBuiltinStopCall(collectedArgs, GetSpan(stopQv));
        }

        if (rootExpr is Expr.QualifiedVar capabilityQv && _capabilitySymbols.TryGetValue(capabilityQv.Module, out var capabilitySym))
        {
            return LowerCapabilityOperationCall(capabilitySym, capabilityQv, collectedArgs);
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

        // Indirect in-place reuse: f(acc) where f is a specializable recursive function and acc is a
        // loop accumulator we deep-copied to uniqueness at loop entry. Route to f$reuse, which rewrites
        // the unique tree in place. The accumulator is dead after this call (it's the loop's only use).
        // f may be a plain Var or a qualified stdlib reference (Ashes.Collection.Map.set → Ashes_Map_set).
        if (ResolveSpecializableCalleeName(rootExpr) is { } specName
            && _specializableFunctions.TryGetValue(specName, out var specInfo)
            && collectedArgs.Count == specInfo.ArgCount
            && collectedArgs[^1] is Expr.Var accArg
            && _linearSpecializationAccumulators.Contains(accArg.Name)
            && Lookup(specName) is { } specBinding
            && SpecializationRebuildsAccumulator(Prune(specBinding.Type), collectedArgs.Count))
        {
            return LowerReuseSpecializedCall(specName, Prune(specBinding.Type), collectedArgs, call);
        }

        return null;
    }

    // Capability monomorphization: a saturated call to a capability-generic function is inlined so
    // the body lowers with the call's concrete argument types, letting a parameterized capability
    // operation (`Ord.compare` at `Ord(Int)`) resolve to its provider. Guarded against recursion
    // (the function is non-recursive) and re-entrancy (a call to the same function while inlining).
    // Capability-generic and overload-generic (==/+ on two params) functions inline a fresh,
    // type-resolved copy of their body at each concrete call site. For an overload-generic stdlib
    // function called by its imported short name, resolve the alias to the stitched name first.
    private (int, TypeRef)? LowerCallTryGenericInlineForm(Expr rootExpr, List<Expr> collectedArgs)
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
            if (!_capabilityGenericInline.Contains(capGenName) && !_overloadGenericInline.Contains(capGenName)
                && _overloadGenericAlias.TryGetValue(capGenName, out var aliasTarget) && aliasTarget is not null)
            {
                capGenName = aliasTarget;
            }

            if ((_capabilityGenericInline.Contains(capGenName) || _overloadGenericInline.Contains(capGenName))
                && !_shadowedInlinables.ContainsKey(capGenVar.Name)
                && !_inliningInProgress.Contains(capGenName)
                && Lookup(capGenVar.Name) is not (Binding.Local or Binding.Env or Binding.EnvScheme)
                && _inlinableFunctions.TryGetValue(capGenName, out var capGenInlinable)
                && capGenInlinable.Params.Count == collectedArgs.Count)
            {
                return InlineCall(capGenName, capGenInlinable.Params, capGenInlinable.Body, collectedArgs);
            }
        }

        return null;
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
    private (int, TypeRef)? LowerCallTryReuseInlineForm(Expr rootExpr, List<Expr> collectedArgs)
    {
        if ((_reuseTokens.Count > 0 || _inSpecialization)
            && ResolveSpecializableCalleeName(rootExpr) is { } inlineName
            && (rootExpr is not Expr.Var vRoot || !_shadowedInlinables.ContainsKey(vRoot.Name))
            && !_inliningInProgress.Contains(inlineName)
            && _inlinableFunctions.TryGetValue(inlineName, out var inlinable)
            && inlinable.Params.Count == collectedArgs.Count)
        {
            return InlineCall(inlineName, inlinable.Params, inlinable.Body, collectedArgs);
        }

        return null;
    }

    private (int, TypeRef) LowerCallTcoSelfCall(TcoContext tco, List<Expr> collectedArgs)
    {
        // Evaluate all new arg values first (into temps), BEFORE storing any
        var savedTail = tco.InTailPosition;
        tco.InTailPosition = false;

        var (newArgTemps, newArgTypes) = LowerCallTcoEvalArgs(tco, collectedArgs);

        // Store new values into TCO param slots
        for (int i = 0; i < tco.ParamSlots.Count; i++)
        {
            Emit(new IrInst.StoreLocal(tco.ParamSlots[i], newArgTemps[i]));
        }

        List<(OwnershipInfo Info, ResourceReleaseKind ReleaseKind)> releaseSnapshot =
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
        if (tco.ArenaCursorSlot >= 0)
        {
            var facts = LowerCallTcoGatherResetFacts(tco, collectedArgs, newArgTypes);
            LowerCallTcoEmitReset(tco, collectedArgs, newArgTemps, newArgTypes, facts);
        }

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

        // Return a dummy value — this code path won't execute at runtime
        int dummy = NewTemp();
        Emit(new IrInst.LoadConstInt(dummy, 0));
        return (dummy, NewTypeVar());
    }

    private List<(OwnershipInfo Info, ResourceReleaseKind ReleaseKind)> LowerCallTcoPrepareOwnedDrops(
        TcoContext tco,
        List<Expr> collectedArgs)
    {
        // Back-edge release state belongs only to this control-flow path. Match/if siblings are
        // lowered afterwards using the same OwnershipInfo objects, so restore it after the jump.
        List<(OwnershipInfo Info, ResourceReleaseKind ReleaseKind)> snapshot =
            SnapshotOwnershipReleaseKinds();
        LowerCallTcoMarkMovedArgs(collectedArgs);
        EmitTcoBackEdgeOwnedDrops(tco);
        return snapshot;
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
            if (arg is Expr.Var argVar && LookupOwnedValue(argVar.Name) is { IsDropped: false } movedOwned)
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
            // An affine accumulator's own-position `acc + r1 + ... + rk` argument (a left-nested
            // concat chain with the accumulator as its leftmost leaf) appends in place at every
            // chain step (ConcatStrTip) — arm the LowerAdd hook for this argument's lowering.
            var savedAffineCtx = _affineAppendCtx;
            if (i < tco.ParamNames.Count
                && tco.FixedCursorSlot >= 0
                && tco.AffineStrParams.Contains(tco.ParamNames[i])
                && i < tco.ParamSlots.Count
                && collectedArgs[i] is Expr.Add)
            {
                var chainLeaf = collectedArgs[i];
                while (chainLeaf is Expr.Add chainAdd)
                {
                    chainLeaf = chainAdd.Left;
                }

                if (chainLeaf is Expr.Var affineVar
                    && string.Equals(affineVar.Name, tco.ParamNames[i], StringComparison.Ordinal)
                    && tco.AffineResvSlots.TryGetValue(tco.ParamNames[i], out var resvSlots))
                {
                    _affineAppendCtx = (tco.ParamNames[i], tco.ParamSlots[i], resvSlots.Start, resvSlots.End);
                }
            }

            var (argTemp, argType) = LowerExpr(collectedArgs[i]);
            _affineAppendCtx = savedAffineCtx;
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
            // iteration's accumulator copy below an advancing one.
            passThrough[i] = i < tco.ParamNames.Count
                && tco.LoopInvariantParams.Contains(tco.ParamNames[i])
                && collectedArgs[i] is Expr.Var passVar
                && string.Equals(passVar.Name, tco.ParamNames[i], StringComparison.Ordinal);

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
                && tco.ParamNames.Contains(tailVar.Name);

            // A back-edge DeepAdt clone of a LIST costs O(length) per iteration, so it is
            // licensed only when the list was freshly REBUILT this iteration (see
            // IsFreshListRebuildExpr); a threaded/consumed shape falls back to no reset.
            freshListRebuild[i] = IsFreshListRebuildExpr(argExpr);

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

    private void LowerCallTcoEmitReset(TcoContext tco, List<Expr> collectedArgs, int[] newArgTemps, TypeRef[] newArgTypes, (bool[] PassThrough, bool[] SingleFreshCons, bool[] FreshListRebuild, bool[] StableAccArg) facts)
    {
        var resetInfo = new PendingTcoReset(
            newArgTemps,
            newArgTypes,
            facts.PassThrough,
            facts.SingleFreshCons,
            facts.FreshListRebuild,
            facts.StableAccArg,
            tco.ParamSlots.ToArray(),
            tco.FixedCursorSlot,
            tco.FixedEndSlot,
            tco.ArenaCursorSlot,
            tco.ArenaEndSlot,
            tco.CoroutineLoopReset,
            tco.CompactionSizeSlot,
            Enumerable.Range(0, collectedArgs.Count).Select(k =>
                k < tco.ParamNames.Count && tco.AffineResvSlots.TryGetValue(tco.ParamNames[k], out var rp) ? rp.Start : -1).ToArray(),
            Enumerable.Range(0, collectedArgs.Count).Select(k =>
                k < tco.ParamNames.Count && tco.AffineResvSlots.TryGetValue(tco.ParamNames[k], out var rq) ? rq.End : -1).ToArray());

        // The copy-out decision dispatches on the ARG TYPES — but an accumulator's type can
        // still be an unresolved inference variable here (e.g. constrained only by a deferred
        // `+`, or by the caller, lowered later). Deciding on a TVar would silently decline the
        // reset and leak every iteration. Emit a placeholder instead and let
        // ResolveDeferredTcoResets re-run the decision at the end of lowering, when the types
        // are as resolved as they will ever be.
        if (newArgTypes.Any(t => Prune(t) is TypeRef.TVar or TypeRef.TTypeParam))
        {
            int pendingId = _nextTcoResetId++;
            _pendingTcoResets[pendingId] = resetInfo;
            Emit(new IrInst.TcoResetPending(pendingId));
        }
        else
        {
            EmitTcoBackEdgeArenaBlock(resetInfo);
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
            var (helperTaskTemp, helperTaskType) = LowerCall(call);
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

    private (int, TypeRef) LowerCallIntrinsic(Expr rootExpr, Binding.Intrinsic intrinsic, List<Expr> collectedArgs)
    {
        int expectedArity = GetIntrinsicArity(intrinsic.Kind);
        if (collectedArgs.Count != expectedArity)
        {
            return ReportArityMismatch(rootExpr, expectedArity, collectedArgs.Count);
        }

        // The dispatch is split into ordered groups; each group falls through (null) to the next.
        return LowerCallIntrinsicIoText(intrinsic.Kind, collectedArgs)
            ?? LowerCallIntrinsicNetBytes(intrinsic.Kind, collectedArgs)
            ?? LowerCallIntrinsicMathProcess(intrinsic.Kind, collectedArgs)
            ?? throw new NotSupportedException($"Unknown intrinsic: {intrinsic.Kind}");
    }

    private (int, TypeRef)? LowerCallIntrinsicIoText(IntrinsicKind kind, List<Expr> collectedArgs) => kind switch
    {
        IntrinsicKind.Print => LowerPrint(collectedArgs[0]),
        IntrinsicKind.Write => LowerWrite(collectedArgs[0], appendNewline: false),
        IntrinsicKind.WriteBytes => LowerWriteBytes(collectedArgs[0]),
        IntrinsicKind.WriteLine => LowerWrite(collectedArgs[0], appendNewline: true),
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
        IntrinsicKind.TextUncons => LowerTextUncons(collectedArgs[0]),
        IntrinsicKind.RegexCompile => LowerRegexCompile(collectedArgs[0]),
        IntrinsicKind.RegexCompileError => LowerRegexCompileError(collectedArgs[0]),
        IntrinsicKind.RegexFind => LowerRegexFind(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
        IntrinsicKind.RegexCaptures => LowerRegexCaptures(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
        IntrinsicKind.RegexSubstitute => LowerRegexSubstitute(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
        IntrinsicKind.TextParseInt => LowerTextParseInt(collectedArgs[0]),
        IntrinsicKind.TextParseFloat => LowerTextParseFloat(collectedArgs[0]),
        IntrinsicKind.TextFromInt => LowerTextFromInt(collectedArgs[0]),
        IntrinsicKind.TextFromFloat => LowerTextFromFloat(collectedArgs[0]),
        IntrinsicKind.TextFormatFloat => LowerTextFormatFloat(collectedArgs[0], collectedArgs[1]),
        IntrinsicKind.BigIntFromInt => LowerBigIntFromInt(collectedArgs[0]),
        IntrinsicKind.BigIntToString => LowerBigIntToString(collectedArgs[0]),
        IntrinsicKind.BigIntToInt => LowerBigIntToInt(collectedArgs[0]),
        IntrinsicKind.BigIntFromString => LowerBigIntFromString(collectedArgs[0]),
        IntrinsicKind.BigIntAdd => LowerBigIntBinary(collectedArgs[0], collectedArgs[1], "add", "Ashes.Number.BigInt.add()", false),
        IntrinsicKind.BigIntSub => LowerBigIntBinary(collectedArgs[0], collectedArgs[1], "sub", "Ashes.Number.BigInt.sub()", false),
        IntrinsicKind.BigIntMul => LowerBigIntBinary(collectedArgs[0], collectedArgs[1], "mul", "Ashes.Number.BigInt.mul()", false),
        IntrinsicKind.BigIntDiv => LowerBigIntBinary(collectedArgs[0], collectedArgs[1], "div", "Ashes.Number.BigInt.div()", false),
        IntrinsicKind.BigIntMod => LowerBigIntBinary(collectedArgs[0], collectedArgs[1], "mod", "Ashes.Number.BigInt.mod()", false),
        IntrinsicKind.BigIntCompare => LowerBigIntBinary(collectedArgs[0], collectedArgs[1], "cmp", "Ashes.Number.BigInt.compare()", true),
        IntrinsicKind.TextToHex => LowerTextToHex(collectedArgs[0]),
        IntrinsicKind.TextAsciiUpper => LowerTextAsciiCase(collectedArgs[0], upper: true),
        IntrinsicKind.TextAsciiLower => LowerTextAsciiCase(collectedArgs[0], upper: false),
        _ => null,
    };

    private (int, TypeRef)? LowerCallIntrinsicNetBytes(IntrinsicKind kind, List<Expr> collectedArgs) => kind switch
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
        IntrinsicKind.BytesEmpty => LowerBytesEmpty(collectedArgs[0]),
        IntrinsicKind.BytesSingleton => LowerBytesSingleton(collectedArgs[0]),
        IntrinsicKind.BytesLength => LowerBytesLength(collectedArgs[0]),
        IntrinsicKind.BytesGet => LowerBytesGet(collectedArgs[0], collectedArgs[1]),
        IntrinsicKind.BytesIndexOf => LowerBytesIndexOf(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
        IntrinsicKind.BytesCompare => LowerBytesCompare(collectedArgs[0], collectedArgs[1]),
        IntrinsicKind.BytesScanHash => LowerBytesScanHash(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
        IntrinsicKind.BytesSubText => LowerBytesSubText(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
        IntrinsicKind.BytesSubView => LowerBytesSubView(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
        IntrinsicKind.BytesAppend => LowerBytesAppend(collectedArgs[0], collectedArgs[1]),
        IntrinsicKind.BytesAppendByte => LowerBytesAppendByte(collectedArgs[0], collectedArgs[1]),
        IntrinsicKind.BytesFromList => LowerBytesFromList(collectedArgs[0]),
        IntrinsicKind.BytesFromText => LowerBytesFromText(collectedArgs[0]),
        IntrinsicKind.BytesHash => LowerBytesHash(collectedArgs[0]),
        IntrinsicKind.BytesU16Le => LowerBytesU16Le(collectedArgs[0]),
        IntrinsicKind.BytesU32Le => LowerBytesU32Le(collectedArgs[0]),
        IntrinsicKind.BytesU64Le => LowerBytesU64Le(collectedArgs[0]),
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

    private (int, TypeRef)? LowerCallQualifiedBuiltin(Expr rootExpr, Expr.QualifiedVar qv, List<Expr> collectedArgs)
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

        // The dispatch is split into ordered groups; each group falls through (null) to the next.
        return LowerCallBuiltinIoText(builtinMember.Kind, collectedArgs)
            ?? LowerCallBuiltinNetBytes(builtinMember.Kind, collectedArgs)
            ?? LowerCallBuiltinMathProcess(builtinMember.Kind, collectedArgs)
            ?? StdMemberNotFound(resolvedModule, qv.Name);
    }

    private (int, TypeRef)? LowerCallBuiltinIoText(BuiltinRegistry.BuiltinValueKind kind, List<Expr> collectedArgs) => kind switch
    {
        BuiltinRegistry.BuiltinValueKind.Print => LowerPrint(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.Panic => LowerPanic(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.Write => LowerWrite(collectedArgs[0], appendNewline: false),
        BuiltinRegistry.BuiltinValueKind.IoWriteBytes => LowerWriteBytes(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.WriteLine => LowerWrite(collectedArgs[0], appendNewline: true),
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
        BuiltinRegistry.BuiltinValueKind.TextUncons => LowerTextUncons(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.RegexCompile => LowerRegexCompile(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.RegexCompileError => LowerRegexCompileError(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.RegexFind => LowerRegexFind(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
        BuiltinRegistry.BuiltinValueKind.RegexCaptures => LowerRegexCaptures(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
        BuiltinRegistry.BuiltinValueKind.RegexSubstitute => LowerRegexSubstitute(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
        BuiltinRegistry.BuiltinValueKind.TextParseInt => LowerTextParseInt(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.TextParseFloat => LowerTextParseFloat(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.TextFromInt => LowerTextFromInt(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.TextFromFloat => LowerTextFromFloat(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.TextFormatFloat => LowerTextFormatFloat(collectedArgs[0], collectedArgs[1]),
        BuiltinRegistry.BuiltinValueKind.BigIntFromInt => LowerBigIntFromInt(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.BigIntToString => LowerBigIntToString(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.BigIntToInt => LowerBigIntToInt(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.BigIntFromString => LowerBigIntFromString(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.BigIntAdd => LowerBigIntBinary(collectedArgs[0], collectedArgs[1], "add", "Ashes.Number.BigInt.add()", false),
        BuiltinRegistry.BuiltinValueKind.BigIntSub => LowerBigIntBinary(collectedArgs[0], collectedArgs[1], "sub", "Ashes.Number.BigInt.sub()", false),
        BuiltinRegistry.BuiltinValueKind.BigIntMul => LowerBigIntBinary(collectedArgs[0], collectedArgs[1], "mul", "Ashes.Number.BigInt.mul()", false),
        BuiltinRegistry.BuiltinValueKind.BigIntDiv => LowerBigIntBinary(collectedArgs[0], collectedArgs[1], "div", "Ashes.Number.BigInt.div()", false),
        BuiltinRegistry.BuiltinValueKind.BigIntMod => LowerBigIntBinary(collectedArgs[0], collectedArgs[1], "mod", "Ashes.Number.BigInt.mod()", false),
        BuiltinRegistry.BuiltinValueKind.BigIntCompare => LowerBigIntBinary(collectedArgs[0], collectedArgs[1], "cmp", "Ashes.Number.BigInt.compare()", true),
        BuiltinRegistry.BuiltinValueKind.TextToHex => LowerTextToHex(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.TextAsciiUpper => LowerTextAsciiCase(collectedArgs[0], upper: true),
        BuiltinRegistry.BuiltinValueKind.TextAsciiLower => LowerTextAsciiCase(collectedArgs[0], upper: false),
        _ => null,
    };

    private (int, TypeRef)? LowerCallBuiltinNetBytes(BuiltinRegistry.BuiltinValueKind kind, List<Expr> collectedArgs) => kind switch
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
        BuiltinRegistry.BuiltinValueKind.BytesEmpty => LowerBytesEmpty(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.BytesSingleton => LowerBytesSingleton(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.BytesLength => LowerBytesLength(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.BytesGet => LowerBytesGet(collectedArgs[0], collectedArgs[1]),
        BuiltinRegistry.BuiltinValueKind.BytesIndexOf => LowerBytesIndexOf(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
        BuiltinRegistry.BuiltinValueKind.BytesCompare => LowerBytesCompare(collectedArgs[0], collectedArgs[1]),
        BuiltinRegistry.BuiltinValueKind.BytesScanHash => LowerBytesScanHash(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
        BuiltinRegistry.BuiltinValueKind.BytesSubText => LowerBytesSubText(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
        BuiltinRegistry.BuiltinValueKind.BytesSubView => LowerBytesSubView(collectedArgs[0], collectedArgs[1], collectedArgs[2]),
        BuiltinRegistry.BuiltinValueKind.BytesAppend => LowerBytesAppend(collectedArgs[0], collectedArgs[1]),
        BuiltinRegistry.BuiltinValueKind.BytesAppendByte => LowerBytesAppendByte(collectedArgs[0], collectedArgs[1]),
        BuiltinRegistry.BuiltinValueKind.BytesFromList => LowerBytesFromList(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.BytesFromText => LowerBytesFromText(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.BytesHash => LowerBytesHash(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.BytesU16Le => LowerBytesU16Le(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.BytesU32Le => LowerBytesU32Le(collectedArgs[0]),
        BuiltinRegistry.BuiltinValueKind.BytesU64Le => LowerBytesU64Le(collectedArgs[0]),
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

    private (int, TypeRef) LowerCallGeneral(Expr.Call call, Expr rootExpr, List<Expr> collectedArgs)
    {
        // Per-call arena watermark — save the heap cursor/end before
        // evaluating the callee and arguments so that intermediate allocations
        // (closures from partial application, temporary data structures inside
        // the callee, argument construction) can be reclaimed after the call
        // chain completes.  The watermark is managed independently of the
        // _arenaWatermarks / _ownershipScopes stacks to avoid unbalancing them.
        int callWmCursorSlot = NewLocal();
        int callWmEndSlot = NewLocal();
        Emit(new IrInst.SaveArenaState(callWmCursorSlot, callWmEndSlot));

        // For non-TCO calls, sub-expressions are NOT in tail position
        var savedTailPos = _tcoCtx?.InTailPosition ?? false;
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        var (currentTemp, currentType) = rootExpr is Expr.Lambda lam
            ? LowerLambda(lam, stackAllocateClosure: true)
            : LowerExpr(rootExpr);

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

        if (LowerCallApplyArgs(call, rootExpr, collectedArgs, ref currentTemp, ref currentType) is { } earlyResult)
        {
            return earlyResult;
        }

        var callResultType = Prune(currentType);
        currentTemp = LowerCallRestoreArena(callWmCursorSlot, callWmEndSlot, currentTemp, callResultType);

        return (currentTemp, currentType);
    }

    // Applies the collected arguments one closure call at a time, unifying each parameter and
    // recording the applied arrow's capabilities. Returns a diagnostic result to propagate on an
    // early error, or null when the whole chain applied cleanly.
    private (int, TypeRef)? LowerCallApplyArgs(Expr.Call call, Expr rootExpr, List<Expr> collectedArgs, ref int currentTemp, ref TypeRef currentType)
    {
        for (int i = 0; i < collectedArgs.Count; i++)
        {
            var (argTemp, argType) = LowerExpr(collectedArgs[i]);
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

            // A resource passed to an opaque function normally moves into the callee (no borrowing: the
            // caller must not use or drop it afterwards, or it double-closes). Borrow inference skips the
            // move when the callee provably only READS this parameter — never closing, storing,
            // returning, or capturing it — so the caller keeps ownership and drops it once. Conservative:
            // only proven borrows are skipped; everything else still moves.
            bool borrowsOnly = CalleeParamBorrowsOnly(rootExpr, i);
            if (!borrowsOnly)
            {
                MarkResourceArgMoved(collectedArgs[i]);
            }

            int target = NewTemp();
            EmitClosureCall(target, currentTemp, argTemp, borrowsOnly);
            currentTemp = target;
            currentType = Prune(funType.Ret);
        }

        return null;
    }

    private void EmitClosureCall(int target, int closureTemp, int argumentTemp, bool borrowsArgument)
    {
        var callInstruction = new IrInst.CallClosure(target, closureTemp, argumentTemp);
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
    private int LowerCallRestoreArena(int callWmCursorSlot, int callWmEndSlot, int currentTemp, TypeRef callResultType)
    {
        int callPreRestoreEndSlot = NewLocal();
        if (CanArenaReset(callResultType))
        {
            // A pending one-shot post (and everything it captures) lives in this window's
            // allocations; skip the reclaim while any is outstanding.
            var callResetSkipLabel = BeginLivePostsGuard();
            Emit(new IrInst.RestoreArenaState(callWmCursorSlot, callWmEndSlot, callPreRestoreEndSlot));
            Emit(new IrInst.ReclaimArenaChunks(callWmEndSlot, callPreRestoreEndSlot));
            EndLivePostsGuard(callResetSkipLabel);
            return currentTemp;
        }

        var callCopyOutKind = GetCopyOutKind(callResultType, out int callCopySize);
        if (callCopyOutKind == CopyOutKind.None)
        {
            return currentTemp;
        }

        return LowerCallCopyOutResult(callWmCursorSlot, callWmEndSlot, callPreRestoreEndSlot, currentTemp, callCopyOutKind, callCopySize);
    }

    private int LowerCallCopyOutResult(int callWmCursorSlot, int callWmEndSlot, int callPreRestoreEndSlot, int currentTemp, CopyOutKind callCopyOutKind, int callCopySize)
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
        switch (callCopyOutKind)
        {
            case CopyOutKind.Shallow:
                Emit(new IrInst.CopyOutArena(copyDest, currentTemp, callCopySize));
                break;
            case CopyOutKind.List:
                Emit(new IrInst.CopyOutList(copyDest, currentTemp));
                break;
            case CopyOutKind.Closure:
                Emit(new IrInst.CopyOutClosure(copyDest, currentTemp));
                break;
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

    private (int, TypeRef) LowerExternalCall(Expr rootExpr, IrExternalFunction externalFunction, List<Expr> args)
    {
        if (externalFunction.ParameterTypes.Count == 0
            && args.Count == 1
            && args[0] is Expr.Var { Name: "Unit" })
        {
            args = [];
        }

        if (args.Count != externalFunction.ParameterTypes.Count)
        {
            return ReportArityMismatch(rootExpr, externalFunction.ParameterTypes.Count, args.Count);
        }

        var loweredArgTemps = new List<int>(args.Count);
        for (int i = 0; i < args.Count; i++)
        {
            var (argTemp, argType) = LowerExpr(args[i]);
            var expectedType = FromFfiType(externalFunction.ParameterTypes[i]);
            using (PushDiagnosticContext($"in argument #{i + 1} of external call to '{externalFunction.Name}'"))
            {
                Unify(expectedType, argType);
            }

            if (externalFunction.ParameterTypes[i] is FfiType.Str)
            {
                int cStringTemp = NewTemp();
                Emit(new IrInst.ToCString(cStringTemp, argTemp));
                loweredArgTemps.Add(cStringTemp);
            }
            else
            {
                loweredArgTemps.Add(argTemp);
            }
        }

        int target = NewTemp();
        Emit(new IrInst.CallExternal(target, externalFunction.SymbolName, externalFunction.LibraryName, loweredArgTemps, externalFunction.ParameterTypes, externalFunction.ReturnType));
        if (externalFunction.ReturnType is FfiType.Void)
        {
            return LowerUnitValue();
        }

        return (target, FromFfiType(externalFunction.ReturnType));
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

        EmitExternalFunctionThunkLayers(externalFunc, layerLabels);

        // Restore outer compilation state.
        _inst.Clear();
        _inst.AddRange(savedInst);
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
    private void EmitExternalFunctionThunkLayers(IrExternalFunction externalFunc, string[] layerLabels)
    {
        int n = externalFunc.ParameterTypes.Count;
        for (int layer = n - 1; layer >= 0; layer--)
        {
            _inst.Clear();
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
            _funcs.Add(func);
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
            _ => throw new InvalidOperationException($"Unknown FFI type '{ffiType.GetType().Name}'.")
        };
    }







    private (int, TypeRef) LowerListLit(Expr.ListLit list)
    {
        using var diagnosticSpan = PushDiagnosticSpan(list);
        var savedTailPos = _tcoCtx?.InTailPosition ?? false;
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        var elemType = NewTypeVar();
        var (tailTemp, tailType) = LowerEmptyList();

        for (int i = list.Elements.Count - 1; i >= 0; i--)
        {
            var (headTemp, headType) = LowerExpr(list.Elements[i]);
            using (PushDiagnosticCode(DiagnosticCodes.ListElementTypeMismatch))
            {
                Unify(headType, elemType);
            }
            (tailTemp, tailType) = LowerConsCell(headTemp, tailTemp, elemType, tailType);
        }

        if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;

        return (tailTemp, Prune(tailType));
    }

    private (int, TypeRef) LowerTupleLit(Expr.TupleLit tuple)
    {
        var savedTailPos = _tcoCtx?.InTailPosition ?? false;
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        var elementTypes = new List<TypeRef>(tuple.Elements.Count);
        var elementTemps = new List<int>(tuple.Elements.Count);
        for (int i = 0; i < tuple.Elements.Count; i++)
        {
            var (temp, type) = LowerExpr(tuple.Elements[i]);
            elementTemps.Add(temp);
            elementTypes.Add(type);
            MarkResourceArgMoved(tuple.Elements[i]);
        }

        int tupleTemp = NewTemp();
        Emit(new IrInst.Alloc(tupleTemp, tuple.Elements.Count * 8));
        for (int i = 0; i < elementTemps.Count; i++)
        {
            Emit(new IrInst.StoreMemOffset(tupleTemp, i * 8, elementTemps[i]));
        }

        if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;

        return (tupleTemp, new TypeRef.TTuple(elementTypes));
    }

    private (int, TypeRef) LowerCons(Expr.Cons cons)
    {
        using var diagnosticSpan = PushDiagnosticSpan(cons);
        var savedTailPos = _tcoCtx?.InTailPosition ?? false;
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        var (headTemp, headType) = LowerExpr(cons.Head);
        var (tailTemp, tailType) = LowerExpr(cons.Tail);
        if (_runtimeRcListAllocationRequested && CanArenaReset(headType))
        {
            tailTemp = PrepareRuntimeRcListTail(cons.Tail, tailTemp);
        }
        MarkResourceArgMoved(cons.Head);
        MarkResourceArgMoved(cons.Tail);

        if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;

        return LowerConsCell(headTemp, tailTemp, headType, tailType);
    }

    private int PrepareRuntimeRcListTail(Expr tailExpression, int tailTemp)
    {
        if (tailExpression is not Expr.Var tail
            || _runtimeRcListTailBinding is null
            || !string.Equals(tail.Name, _runtimeRcListTailBinding, StringComparison.Ordinal)
            || LookupOwnedValue(tail.Name) is not { RuntimeManaged: true, IsDropped: false } info)
        {
            return tailTemp;
        }

        if (_runtimeRcListTailShared)
        {
            int duplicatedTemp = NewTemp();
            Emit(new IrInst.RcDup(duplicatedTemp, tailTemp, RuntimeManaged: true));
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
            case Expr.StrLit:
            case Expr.BoolLit:
                return true;
            case Expr.Var v:
                if (!bnd.Contains(v.Name))
                {
                    res.Add(v.Name);
                }

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

    private static Expr SubstituteVars(Expr e, Dictionary<string, string> renames)
    {
        if (renames.Count == 0)
        {
            return e;
        }

        Expr S(Expr x) => SubstituteVars(x, renames);

        switch (e)
        {
            case Expr.IntLit or Expr.UIntLit or Expr.BigIntLit or Expr.FloatLit or Expr.StrLit or Expr.BoolLit or Expr.QualifiedVar:
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
                return SubstituteVarsBinders(e, renames);
            default:
                throw new NotSupportedException($"SubstituteVars: unhandled {e.GetType().Name}");
        }
    }

    private static Expr SubstituteVarsBinders(Expr e, Dictionary<string, string> renames)
    {
        Expr S(Expr x) => SubstituteVars(x, renames);

        // A binder shadows a renamed name within its scope: drop it from the rename set for the subtree.
        T WithShadowed<T>(IEnumerable<string> bound, Func<Dictionary<string, string>, T> build)
        {
            var sub = renames.Where(kv => !bound.Contains(kv.Key, StringComparer.Ordinal)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            return build(sub);
        }

        switch (e)
        {
            case Expr.Lambda lam:
                return WithShadowed([lam.ParamName], sub => new Expr.Lambda(lam.ParamName, SubstituteVars(lam.Body, sub)));
            case Expr.Let l:
                return new Expr.Let(l.Name, S(l.Value), WithShadowed([l.Name], sub => SubstituteVars(l.Body, sub)));
            case Expr.LetResult l:
                return new Expr.LetResult(l.Name, S(l.Value), WithShadowed([l.Name], sub => SubstituteVars(l.Body, sub)));
            case Expr.LetRecursive l:
                return WithShadowed([l.Name], sub => new Expr.LetRecursive(l.Name, SubstituteVars(l.Value, sub), SubstituteVars(l.Body, sub)));
            case Expr.Match m:
                return new Expr.Match(
                    S(m.Value),
                    m.Cases.Select(mc => WithShadowed(
                        PatternBindings(mc.Pattern),
                        sub => new MatchCase(mc.Pattern, SubstituteVars(mc.Body, sub), mc.Guard is null ? null : SubstituteVars(mc.Guard, sub)))).ToList(),
                    m.Pos);
            default:
                throw new NotSupportedException($"SubstituteVars: unhandled {e.GetType().Name}");
        }
    }

    private bool TryLowerConstructorExpression(Expr expr, bool stackAllocate, out (int Temp, TypeRef Type) lowered)
    {
        if (expr is Expr.Var varCtor && _constructorSymbols.TryGetValue(varCtor.Name, out var nullaryCtor) && nullaryCtor.Arity == 0)
        {
            lowered = LowerNullaryConstructor(nullaryCtor, stackAllocate);
            return true;
        }

        var args = new List<Expr>();
        var rootExpr = CollectCallArgs(expr, args);
        if (rootExpr is Expr.Var callCtor && _constructorSymbols.TryGetValue(callCtor.Name, out var ctor))
        {
            lowered = LowerConstructorApplication(ctor, args, stackAllocate);
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
            case Expr.StrLit:
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
