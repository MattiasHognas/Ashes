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

    // Available reuse tokens (dead ADT cells from matching a linear value), innermost last. Each is
    // the cell's address temp + its field count; a same-arity constructor in the arm consumes one,
    // emitting AllocReusing instead of bump-allocating. See LowerConstructorApplication / LowerMatch.
    private readonly List<(int Temp, int FieldCount)> _reuseTokens = new();

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
    // like loop(...)(mk(l)(v+n)(r)). Recursion (let rec / RecGroup) is excluded.
    private readonly Dictionary<string, (IReadOnlyList<string> Params, Expr Body)> _inlinableFunctions = new(StringComparer.Ordinal);

    // Top-level functions specializable for in-place reuse, by name. Two shapes:
    //   • single-parameter recursion: let rec f = fun p -> body (LinearParam = p, ArgCount = 1);
    //   • nested-rec-returning: let f = fun a -> ... -> (let rec go = fun m -> _ in go) — f isn't
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
    private static readonly string ParallelModulePrefix = ProjectSupport.SanitizeModuleBindingName("Ashes.Parallel");
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

    // Accumulator names made uniquely-owned at loop entry (deep-copied) specifically so a call
    // f(acc) to a specializable function can be rewritten to f$reuse(acc). Distinct from
    // _linearReuseNames, which marks accumulators matched directly in the loop body.
    private readonly HashSet<string> _linearSpecializationAccumulators = new(StringComparer.Ordinal);

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
    private readonly HashSet<string> _externOpaqueTypes = new(StringComparer.Ordinal);
    private readonly List<IrExternFunction> _externFunctions = new();

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
        rootScope["async"] = CreateAsyncTaskBinding();
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
        // Type and extern declarations are registered upfront; their relative order among value
        // bindings does not affect ADT/extern visibility under Model-A scoping.
        RegisterTypeDeclarations(program.TypeDecls);
        RegisterExternDeclarations(program.ExternDecls);

        var valueItems = program.Items
            .Where(item => item is TopLevelItem.LetDecl or TopLevelItem.RecGroup)
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
        Expr.IntLit or Expr.UIntLit or Expr.FloatLit or Expr.StrLit or Expr.BoolLit or Expr.Var or Expr.QualifiedVar => false,
        Expr.Add x => ExprHasCallOrAggregate(x.Left) || ExprHasCallOrAggregate(x.Right),
        Expr.Subtract x => ExprHasCallOrAggregate(x.Left) || ExprHasCallOrAggregate(x.Right),
        Expr.Multiply x => ExprHasCallOrAggregate(x.Left) || ExprHasCallOrAggregate(x.Right),
        Expr.Divide x => ExprHasCallOrAggregate(x.Left) || ExprHasCallOrAggregate(x.Right),
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
        Expr.LetRec x => ExprHasCallOrAggregate(x.Value) || ExprHasCallOrAggregate(x.Body),
        Expr.LetResult x => ExprHasCallOrAggregate(x.Value) || ExprHasCallOrAggregate(x.Body),
        Expr.Lambda x => ExprHasCallOrAggregate(x.Body),
        Expr.Await x => ExprHasCallOrAggregate(x.Task),
        Expr.Match x => ExprHasCallOrAggregate(x.Value)
            || x.Cases.Any(c => ExprHasCallOrAggregate(c.Body) || (c.Guard is not null && ExprHasCallOrAggregate(c.Guard))),
        _ => true,
    };

    private void RegisterInlinableFunctions(IReadOnlyList<TopLevelItem> valueItems)
    {
        // Strip the import stitcher's intra-module alias prefix (let helper = Ashes_Mod_helper in ...)
        // so a stdlib function's value is seen as the clean lambda it is — otherwise the reuse
        // registries below never recognise stdlib functions (Map.set etc.), since their value is a
        // chain of alias `let`s rather than a Lambda. On an unhandled shape, fall back to the raw value
        // (the function just isn't registered for reuse — correct, only unoptimized).
        Expr Strip(Expr value)
        {
            try
            {
                return StripModuleAliasPrefix(value);
            }
            catch (NotSupportedException)
            {
                return value;
            }
        }

        foreach (var item in valueItems)
        {
            // The grained data-parallel combinators: capture their stripped multi-parameter recursive
            // lambda so each concrete-typed call site can generate a monomorphic parallel specialization.
            // mapGrained is grain+f+xs (arity 3); reduceGrained is grain+combine+identity+f+xs (arity 5).
            if (item is TopLevelItem.LetDecl { IsRecursive: true } parLet
                && (string.Equals(parLet.Name, ParallelMapGrainedName, StringComparison.Ordinal)
                    || string.Equals(parLet.Name, ParallelReduceGrainedName, StringComparison.Ordinal))
                && Strip(parLet.Value) is Expr.Lambda parLam)
            {
                int arity = string.Equals(parLet.Name, ParallelMapGrainedName, StringComparison.Ordinal) ? 3 : 5;
                _parallelSpecializable[parLet.Name] = (parLam, arity);
            }

            if (item is TopLevelItem.LetDecl { IsRecursive: false } let && Strip(let.Value) is Expr.Lambda lam)
            {
                // A non-recursive function that returns a nested recursive single-param function
                // (the Map.set shape) is specialized, not inlined; any other plain function is an
                // inline candidate for helper rebuilds inside a reuse arm.
                if (TryGetNestedRecReturn(lam, out var nestedParam, out var nestedArgCount))
                {
                    _specializableFunctions[let.Name] = (lam, nestedParam, nestedArgCount);
                }
                else if (ExprHasCallOrAggregate(GetInnermostBody(lam)))
                {
                    // Only inline helpers that can contribute an allocation. Non-allocating accessor /
                    // arithmetic helpers are resolved as by-label calls in specializations instead (see
                    // _topLevelFunctionRefs), which keeps the specialized function small.
                    _inlinableFunctions[let.Name] = (CollectLambdaParams(lam), GetInnermostBody(lam));
                    _inlinableDefiningValues[let.Name] = let.Value;
                }
            }

            // Single-parameter recursive functions (let rec f = fun p -> body, body not a lambda)
            // are candidates for in-place-reuse specialization when applied to a unique accumulator.
            else if (item is TopLevelItem.LetDecl { IsRecursive: true } recLet && Strip(recLet.Value) is Expr.Lambda { Body: not Expr.Lambda } recLam)
            {
                _specializableFunctions[recLet.Name] = (recLam, recLam.ParamName, 1);
            }
            else if (item is TopLevelItem.RecGroup { Bindings.Count: 1 } group
                && Strip(group.Bindings[0].Value) is Expr.Lambda { Body: not Expr.Lambda } groupLam)
            {
                _specializableFunctions[group.Bindings[0].Name] = (groupLam, groupLam.ParamName, 1);
            }
        }
    }

    /// <summary>
    /// Registers functions bound in the entry expression's leading let-chain for the same reuse
    /// registries as flat top-level items. When the import stitcher combines modules with the user
    /// program, the user's top-level declarations arrive as a nested let-pyramid in the program
    /// BODY (not as TopLevelItems), so without this walk user-defined functions could never be
    /// reuse-specialized or helper-inlined — only stitched module (Ashes_*) functions could.
    /// Shadow safety: the registries are name-keyed, so a name bound more than once anywhere in
    /// the body (any let/lambda/pattern binder) is never registered.
    /// </summary>
    private void RegisterEntryBodyFunctions(Expr body)
    {
        var binderCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        CountBinders(body, binderCounts);

        var cur = body;
        while (true)
        {
            switch (cur)
            {
                case Expr.Let letExpr:
                    RegisterEntryFunction(letExpr.Name, isRecursive: false, letExpr.Value, binderCounts);
                    cur = letExpr.Body;
                    continue;
                case Expr.LetRec letRecExpr:
                    RegisterEntryFunction(letRecExpr.Name, isRecursive: true, letRecExpr.Value, binderCounts);
                    cur = letRecExpr.Body;
                    continue;
                default:
                    return;
            }
        }
    }

    private void RegisterEntryFunction(string name, bool isRecursive, Expr value, Dictionary<string, int> binderCounts)
    {
        if (binderCounts.GetValueOrDefault(name) != 1
            || _specializableFunctions.ContainsKey(name)
            || _inlinableFunctions.ContainsKey(name)
            || value is not Expr.Lambda lam)
        {
            return;
        }

        // A reuse specialization is generated in an isolated scope, where only stitched top-level
        // bindings (module functions — inlined or called by-label) and intrinsics resolve. An entry
        // function that references any OTHER user binding (a lexical sibling) would fail to lower
        // inside its spec, so only self-contained functions are registered.
        var free = FreeVars(lam, new HashSet<string>(StringComparer.Ordinal) { name });
        if (!free.All(_topLevelBindingNames.Contains))
        {
            return;
        }

        // Only the SPECIALIZABLE shapes are registered from the entry body — never the inlinable
        // helper set. A module helper's free names are other module bindings, resolvable by-label
        // from any inline site; a user helper's body may reference arbitrary earlier user bindings
        // that are not in scope at an inline site inside another function or specialization.
        if (!isRecursive)
        {
            if (TryGetNestedRecReturn(lam, out var nestedParam, out var nestedArgCount))
            {
                _specializableFunctions[name] = (lam, nestedParam, nestedArgCount);
            }
        }
        else if (lam.Body is not Expr.Lambda)
        {
            // Single-parameter recursive function — a direct reuse-specialization candidate.
            _specializableFunctions[name] = (lam, lam.ParamName, 1);
        }
    }

    /// <summary>
    /// Counts every binder occurrence (let / let rec / lambda parameter / match-pattern variable)
    /// by name across an expression tree. Walks record properties reflectively so new Expr shapes
    /// are covered by default.
    /// </summary>
    private static void CountBinders(object? node, Dictionary<string, int> counts)
    {
        if (node is null or string)
        {
            return;
        }

        void Bump(string n) => counts[n] = counts.GetValueOrDefault(n) + 1;
        switch (node)
        {
            case Expr.Let l: Bump(l.Name); break;
            case Expr.LetResult lr: Bump(lr.Name); break;
            case Expr.LetRec lrec: Bump(lrec.Name); break;
            case Expr.Lambda lam: Bump(lam.ParamName); break;
            case Pattern.Var pv: Bump(pv.Name); break;
        }

        if (node is System.Runtime.CompilerServices.ITuple tuple)
        {
            for (int i = 0; i < tuple.Length; i++)
            {
                CountBinders(tuple[i], counts);
            }

            return;
        }

        if (node is System.Collections.IEnumerable seq)
        {
            foreach (var item in seq)
            {
                CountBinders(item, counts);
            }

            return;
        }

        if (node is not (Expr or Pattern or MatchCase))
        {
            return;
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
                CountBinders(prop.GetValue(node), counts);
            }
        }
    }

    /// <summary>
    /// Detects the <c>Map.set</c> shape: a chain of outer parameter lambdas whose innermost body is
    /// <c>let rec go = (fun m -> _) in go</c> — returning a recursive single-parameter function.
    /// Outputs the recursive parameter to specialize on and the total number of arguments the full
    /// application takes (outer params + the recursive arg).
    /// </summary>
    private static bool TryGetNestedRecReturn(Expr.Lambda lam, out string linearParam, out int argCount)
    {
        linearParam = "";
        argCount = 0;
        Expr body = lam;
        int outer = 0;
        while (body is Expr.Lambda inner)
        {
            outer++;
            body = inner.Body;
        }

        if (body is Expr.LetRec { Value: Expr.Lambda recValue, Body: Expr.Var recRef } letRec
            && string.Equals(letRec.Name, recRef.Name, StringComparison.Ordinal)
            && recValue.Body is not Expr.Lambda)
        {
            linearParam = recValue.ParamName;
            argCount = outer + 1;
            return true;
        }

        return false;
    }

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

    /// <summary>
    /// Records every top-level value-binding name and reports duplicates (ASH013). The recorded set
    /// later lets <see cref="LowerVar"/> distinguish a forward reference from an unknown identifier.
    /// </summary>
    private void CollectTopLevelBindingNames(IReadOnlyList<TopLevelItem> valueItems)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in valueItems)
        {
            switch (item)
            {
                case TopLevelItem.LetDecl let:
                    RegisterTopLevelBindingName(let.Name, let.Value, seen);
                    break;
                case TopLevelItem.RecGroup group:
                    foreach (var (name, value) in group.Bindings)
                    {
                        RegisterTopLevelBindingName(name, value, seen);
                    }

                    break;
            }
        }
    }

    private void RegisterTopLevelBindingName(string name, Expr valueForSpan, HashSet<string> seen)
    {
        _topLevelBindingNames.Add(name);
        if (!seen.Add(name))
        {
            ReportDiagnostic(GetSpan(valueForSpan), $"Duplicate top-level binding '{name}'.", DuplicateTopLevelBindingCode);
        }
    }

    private Expr DesugarTopLevel(IReadOnlyList<TopLevelItem> valueItems, Expr? trailingBody)
    {
        // A program may omit the trailing expression (e.g. a module that only declares bindings);
        // synthesize a unit value so the entry point is well-typed and produces no output.
        Expr body = trailingBody ?? new Expr.Var("Unit");

        for (int i = valueItems.Count - 1; i >= 0; i--)
        {
            body = valueItems[i] switch
            {
                TopLevelItem.LetDecl { IsRecursive: true } let => new Expr.LetRec(let.Name, let.Value, body),
                TopLevelItem.LetDecl let => new Expr.Let(let.Name, let.Value, body),
                TopLevelItem.RecGroup group => DesugarRecGroup(group, body),
                _ => body
            };
        }

        return body;
    }

    /// <summary>
    /// Desugars a mutual-recursion group (<c>let rec X = ... and Y = ...</c>) into a marker node that
    /// lowering handles directly. The parser only emits a <see cref="TopLevelItem.RecGroup"/> for a
    /// genuine multi-binding <c>and</c> group (or a degenerate one-binding group), whose bindings must
    /// all see one another — a property that nesting independent <c>let rec</c> forms cannot express.
    /// The group's names are also in scope for the continuation (subsequent declarations and the
    /// trailing expression) under Model-A scoping; <paramref name="body"/> carries that continuation.
    /// </summary>
    private Expr DesugarRecGroup(TopLevelItem.RecGroup group, Expr body)
        => new RecGroupExpr(group.Bindings, body);

    /// <summary>
    /// Internal-only AST node carrying a mutual-recursion binding group plus its continuation. It only
    /// ever appears at the top level (never inside a lambda/async body), so the free-variable and
    /// other expression walkers never encounter it; only <see cref="LowerExpr"/> dispatches it.
    /// </summary>
    private sealed record RecGroupExpr(IReadOnlyList<(string Name, Expr Value)> Bindings, Expr Body) : Expr;

    /// <summary>
    /// Shared lowering state for the members of a mutual-recursion group. Every member is compiled to
    /// its own IR function but they all share one identical environment, so a member can rebuild any
    /// sibling's closure from its own env pointer (see <see cref="LowerLambdaCore"/>).
    /// </summary>
    private sealed class RecGroupContext
    {
        public required IReadOnlyList<string> SharedCaptures { get; init; }
        public required int SharedEnvPtrTemp { get; init; }
        public required IReadOnlyList<(string Name, string Label, TypeRef Type, TextSpan Span)> Members { get; init; }
    }

    /// <summary>
    /// Lowers a mutually-recursive binding group. All member names are introduced with fresh type
    /// variables before any right-hand side is inferred (so each body sees every member), every body is
    /// lowered against one shared environment, and the names then stay in scope — monomorphically, as
    /// the single <c>let rec</c> form already does — for the continuation.
    /// </summary>
    private (int, TypeRef) LowerRecGroup(RecGroupExpr group)
    {
        var bindings = group.Bindings;
        var groupNames = new HashSet<string>(bindings.Select(b => b.Name), StringComparer.Ordinal);

        // HM recursive-group rule, part 1: introduce a fresh type for every member up front. Function
        // members get an arrow shape so call sites refine arg/return before the body is solved.
        var recTypes = new TypeRef[bindings.Count];
        for (int i = 0; i < bindings.Count; i++)
        {
            recTypes[i] = FindInnermostLambdaUnderLets(bindings[i].Value) is not null
                ? new TypeRef.TFun(NewTypeVar(), NewTypeVar())
                : NewTypeVar();
        }

        // The shared environment is the union of each member body's free variables, minus the group
        // names themselves (siblings are reached by symbol, never captured). Order is fixed here and
        // reused for every member so the env layout is identical across the group.
        var sharedCaptures = new List<string>();
        var seenCaptures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, value) in bindings)
        {
            foreach (var name in FreeVars(value, groupNames))
            {
                if (seenCaptures.Add(name)
                    && Lookup(name) is Binding.Local or Binding.Env or Binding.EnvScheme or Binding.Self or Binding.Scheme)
                {
                    sharedCaptures.Add(name);
                }
            }
        }

        // Build the shared env once, at the group site, where the captured names are still in scope.
        int sharedEnvPtrTemp = NewTemp();
        if (sharedCaptures.Count == 0)
        {
            Emit(new IrInst.LoadConstInt(sharedEnvPtrTemp, 0)); // null env
        }
        else
        {
            Emit(new IrInst.Alloc(sharedEnvPtrTemp, sharedCaptures.Count * 8));
            for (int i = 0; i < sharedCaptures.Count; i++)
            {
                var (capTemp, _) = LowerVar(new Expr.Var(sharedCaptures[i]));
                Emit(new IrInst.StoreMemOffset(sharedEnvPtrTemp, i * 8, capTemp));
            }
        }

        var labels = new string[bindings.Count];
        var members = new (string Name, string Label, TypeRef Type, TextSpan Span)[bindings.Count];
        var slots = new int[bindings.Count];
        for (int i = 0; i < bindings.Count; i++)
        {
            labels[i] = $"recgroup_{_nextLambdaId++}_{bindings[i].Name}";
            members[i] = (bindings[i].Name, labels[i], recTypes[i], GetSpan(bindings[i].Value));
            slots[i] = NewLocal();
        }

        var groupContext = new RecGroupContext
        {
            SharedCaptures = sharedCaptures,
            SharedEnvPtrTemp = sharedEnvPtrTemp,
            Members = members
        };

        // Mutual recursion is reached through closure calls, not the single-function tail-call loop, so
        // disable TCO while lowering the group bodies.
        var savedTcoCtx = _tcoCtx;
        _tcoCtx = null;

        for (int i = 0; i < bindings.Count; i++)
        {
            var value = bindings[i].Value;
            if (value is not Expr.Lambda lambda)
            {
                ReportDiagnostic(GetSpan(value), "let rec currently requires a function value.");
                var (fallbackTemp, fallbackType) = LowerExpr(value);
                Unify(recTypes[i], fallbackType);
                Emit(new IrInst.StoreLocal(slots[i], fallbackTemp));
                RecordLocalDebugInfo(slots[i], bindings[i].Name, recTypes[i]);
                continue;
            }

            var (closureTemp, closureType) = LowerLambdaCore(lambda, selfName: null, selfType: null, stackAllocateClosure: false, recGroup: groupContext, forcedLabel: labels[i]);
            Unify(recTypes[i], closureType);
            RecordHoverType(members[i].Span, bindings[i].Name, recTypes[i]);
            RecordLocalDebugInfo(slots[i], bindings[i].Name, recTypes[i]);
            Emit(new IrInst.StoreLocal(slots[i], closureTemp));
        }

        _tcoCtx = savedTcoCtx;

        // Mutual-recursion TCO: when the group is eligible, synthesize a single self-recursive
        // dispatch function and rebind each member to a thin wrapper so the existing single-function
        // TCO collapses the whole group into one loop instead of growing the stack through closure
        // calls. Ineligible groups keep the closures lowered above.
        var tcoSlots = TryLowerMutualRecursionTco(bindings, recTypes, groupNames);

        // The members stay in scope for the continuation, bound to the slots holding their closures
        // (or their TCO wrappers) — monomorphic, matching the single let rec form.
        var parent = _scopes.Peek();
        var child = new Dictionary<string, Binding>(parent, StringComparer.Ordinal);
        for (int i = 0; i < bindings.Count; i++)
        {
            int memberSlot = tcoSlots?[i] ?? slots[i];
            child[bindings[i].Name] = new Binding.Local(memberSlot, Prune(recTypes[i]), members[i].Span);
        }

        _scopes.Push(child);
        var (bodyTemp, bodyType) = LowerExpr(group.Body);
        _scopes.Pop();
        return (bodyTemp, bodyType);
    }

    private const string DispatchWhichName = "__recgroup_which";

    private static string DispatchArgName(int index) => $"__recgroup_arg{index}";

    private static string WrapperArgName(int index) => $"__recgroup_w{index}";

    /// <summary>
    /// Attempts to compile a mutually-recursive group as a single self-recursive dispatch loop so
    /// mutual tail calls stop growing the stack. Returns one wrapper slot per member when the
    /// transform was applied, or <c>null</c> to keep the closure-based lowering produced above.
    /// <para>
    /// Eligible groups have at least two members, all function values of the same arity with
    /// identical parameter types (checked against each member's inferred signature), and at least
    /// one cross-member tail call. Identical parameter types mean every merged dispatch parameter
    /// keeps a single well-defined type, so the existing single-function TCO — including its arena
    /// copy-out — applies unchanged. Heterogeneous parameter types fall back to the closure path.
    /// </para>
    /// </summary>
    private int[]? TryLowerMutualRecursionTco(
        IReadOnlyList<(string Name, Expr Value)> bindings,
        TypeRef[] recTypes,
        HashSet<string> groupNames)
    {
        if (bindings.Count < 2)
        {
            return null;
        }

        // All members must be lambdas of the same arity.
        var lambdas = new Expr.Lambda[bindings.Count];
        int arity = -1;
        for (int i = 0; i < bindings.Count; i++)
        {
            if (bindings[i].Value is not Expr.Lambda lam)
            {
                return null;
            }

            lambdas[i] = lam;
            int memberArity = CountLambdaChain(lam);
            if (arity == -1)
            {
                arity = memberArity;
            }
            else if (memberArity != arity)
            {
                return null;
            }
        }

        if (arity < 1)
        {
            return null;
        }

        // Every member must expose the same parameter type at each position so the shared dispatch
        // parameters are well-typed without coercion.
        var paramTypes = new List<TypeRef>[bindings.Count];
        for (int i = 0; i < bindings.Count; i++)
        {
            if (!TryGetArrowParamTypes(recTypes[i], arity, out var memberParamTypes))
            {
                return null;
            }

            paramTypes[i] = memberParamTypes;
        }

        for (int j = 0; j < arity; j++)
        {
            for (int i = 1; i < bindings.Count; i++)
            {
                if (!TypesStructurallyEqual(paramTypes[0][j], paramTypes[i][j]))
                {
                    return null;
                }
            }
        }

        var tagOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < bindings.Count; i++)
        {
            tagOf[bindings[i].Name] = i;
        }

        // At least one genuine cross-member tail call — otherwise single-function TCO already covers it.
        bool hasCrossMemberTailCall = false;
        for (int i = 0; i < bindings.Count && !hasCrossMemberTailCall; i++)
        {
            var called = new HashSet<string>(StringComparer.Ordinal);
            CollectGroupTailCalls(GetInnermostBody(lambdas[i]), groupNames, arity, called);
            hasCrossMemberTailCall = called.Any(name => !string.Equals(name, bindings[i].Name, StringComparison.Ordinal));
        }

        if (!hasCrossMemberTailCall)
        {
            return null;
        }

        // ── Synthesize and lower the dispatch function. ──
        string dispatchName = $"__recgroup_dispatch_{_nextLambdaId++}";
        var dispatchLambda = BuildDispatchLambda(bindings, lambdas, groupNames, tagOf, dispatchName, arity);

        int dispatchSlot = NewLocal();
        var dispatchRecType = (TypeRef)new TypeRef.TFun(NewTypeVar(), NewTypeVar());
        var dispatchScope = new Dictionary<string, Binding>(_scopes.Peek(), StringComparer.Ordinal)
        {
            [dispatchName] = new Binding.Local(dispatchSlot, dispatchRecType)
        };
        _scopes.Push(dispatchScope);

        int paramCount = arity + 1; // the dispatch tag plus the shared parameters
        var savedTcoCtx = _tcoCtx;
        _tcoCtx = HasTailSelfCalls(GetInnermostBody(dispatchLambda), dispatchName, paramCount)
            ? new TcoContext
            {
                SelfName = dispatchName,
                ParamCount = paramCount,
                ParamNames = CollectLambdaParams(dispatchLambda),
                InTailPosition = false
            }
            : null;
        var (dispatchTemp, dispatchType) = LowerLambdaCore(
            dispatchLambda, dispatchName, dispatchRecType, stackAllocateClosure: false, forcedLabel: dispatchName);
        _tcoCtx = savedTcoCtx;
        Unify(dispatchRecType, dispatchType);
        Emit(new IrInst.StoreLocal(dispatchSlot, dispatchTemp));

        // ── Synthesize and lower one wrapper per member: fun p… -> dispatch(tag, p…). ──
        var wrapperSlots = new int[bindings.Count];
        for (int i = 0; i < bindings.Count; i++)
        {
            var wrapperLambda = BuildDispatchWrapper(dispatchName, tagOf[bindings[i].Name], arity);
            var (wrapperTemp, wrapperType) = LowerExpr(wrapperLambda);
            Unify(recTypes[i], wrapperType);
            int slot = NewLocal();
            Emit(new IrInst.StoreLocal(slot, wrapperTemp));
            RecordLocalDebugInfo(slot, bindings[i].Name, recTypes[i]);
            wrapperSlots[i] = slot;
        }

        _scopes.Pop(); // dispatchScope — dispatchName must not leak into the continuation.
        return wrapperSlots;
    }

    /// <summary>
    /// Builds <c>fun which -> fun arg0 -> … -> match which with | 0 -> body0 | … | _ -> bodyN</c>,
    /// where each arm is a member body with its parameters rebound to the shared dispatch arguments
    /// and its in-group tail calls redirected to <paramref name="dispatchName"/>.
    /// </summary>
    private Expr.Lambda BuildDispatchLambda(
        IReadOnlyList<(string Name, Expr Value)> bindings,
        Expr.Lambda[] lambdas,
        HashSet<string> groupNames,
        Dictionary<string, int> tagOf,
        string dispatchName,
        int arity)
    {
        var cases = new List<MatchCase>(bindings.Count);
        for (int i = 0; i < bindings.Count; i++)
        {
            var origParams = CollectLambdaParams(lambdas[i]);
            var inner = GetInnermostBody(lambdas[i]);
            Expr armBody = RewriteGroupTailCalls(inner, groupNames, tagOf, dispatchName, arity);
            for (int j = arity - 1; j >= 0; j--)
            {
                armBody = new Expr.Let(origParams[j], new Expr.Var(DispatchArgName(j)), armBody);
            }

            // The final member is the wildcard arm so the integer dispatch match is exhaustive.
            Pattern pattern = i == bindings.Count - 1 ? new Pattern.Wildcard() : new Pattern.IntLit(i);
            cases.Add(new MatchCase(pattern, armBody));
        }

        Expr body = new Expr.Match(new Expr.Var(DispatchWhichName), cases);
        for (int j = arity - 1; j >= 0; j--)
        {
            body = new Expr.Lambda(DispatchArgName(j), body);
        }

        return new Expr.Lambda(DispatchWhichName, body);
    }

    /// <summary>Builds <c>fun w0 -> … -> dispatch(tag, w0, …)</c>, the per-member entry wrapper.</summary>
    private Expr.Lambda BuildDispatchWrapper(string dispatchName, int tag, int arity)
    {
        Expr body = new Expr.Call(new Expr.Var(dispatchName), new Expr.IntLit(tag));
        for (int j = 0; j < arity; j++)
        {
            body = new Expr.Call(body, new Expr.Var(WrapperArgName(j)));
        }

        for (int j = arity - 1; j >= 0; j--)
        {
            body = new Expr.Lambda(WrapperArgName(j), body);
        }

        return (Expr.Lambda)body;
    }

    /// <summary>
    /// Rewrites fully-applied in-group member calls that sit in tail position into calls to the
    /// dispatch function, leaving non-tail occurrences (which still target the member wrappers)
    /// untouched. Only tail-position nodes are traversed.
    /// </summary>
    private Expr RewriteGroupTailCalls(Expr expr, HashSet<string> groupNames, Dictionary<string, int> tagOf, string dispatchName, int arity)
    {
        switch (expr)
        {
            case Expr.If iff:
                return iff with
                {
                    Then = RewriteGroupTailCalls(iff.Then, groupNames, tagOf, dispatchName, arity),
                    Else = RewriteGroupTailCalls(iff.Else, groupNames, tagOf, dispatchName, arity),
                };
            case Expr.Match m:
                return m with
                {
                    Cases = m.Cases
                        .Select(c => c with { Body = RewriteGroupTailCalls(c.Body, groupNames, tagOf, dispatchName, arity) })
                        .ToList(),
                };
            case Expr.Let l:
                return l with { Body = RewriteGroupTailCalls(l.Body, groupNames, tagOf, dispatchName, arity) };
            case Expr.LetRec l:
                return l with { Body = RewriteGroupTailCalls(l.Body, groupNames, tagOf, dispatchName, arity) };
            case Expr.LetResult l:
                return l with { Body = RewriteGroupTailCalls(l.Body, groupNames, tagOf, dispatchName, arity) };
            case Expr.Call call:
                var args = new List<Expr>();
                var root = CollectCallArgs(call, args);
                if (root is Expr.Var v && groupNames.Contains(v.Name) && args.Count == arity)
                {
                    Expr redirected = new Expr.Call(new Expr.Var(dispatchName), new Expr.IntLit(tagOf[v.Name]));
                    foreach (var arg in args)
                    {
                        redirected = new Expr.Call(redirected, arg);
                    }

                    return redirected;
                }

                return expr;
            default:
                return expr;
        }
    }

    /// <summary>Collects the names of group members tail-called (fully applied) within an expression.</summary>
    private void CollectGroupTailCalls(Expr expr, HashSet<string> groupNames, int arity, HashSet<string> found)
    {
        switch (expr)
        {
            case Expr.If iff:
                CollectGroupTailCalls(iff.Then, groupNames, arity, found);
                CollectGroupTailCalls(iff.Else, groupNames, arity, found);
                break;
            case Expr.Match m:
                foreach (var c in m.Cases)
                {
                    CollectGroupTailCalls(c.Body, groupNames, arity, found);
                }

                break;
            case Expr.Let l:
                CollectGroupTailCalls(l.Body, groupNames, arity, found);
                break;
            case Expr.LetRec l:
                CollectGroupTailCalls(l.Body, groupNames, arity, found);
                break;
            case Expr.LetResult l:
                CollectGroupTailCalls(l.Body, groupNames, arity, found);
                break;
            case Expr.Call call:
                var args = new List<Expr>();
                var root = CollectCallArgs(call, args);
                if (root is Expr.Var v && groupNames.Contains(v.Name) && args.Count == arity)
                {
                    found.Add(v.Name);
                }

                break;
        }
    }

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

        return new IrProgram(
            EntryFunction: entry,
            Functions: _funcs,
            StringLiterals: _strings,
            ExternFunctions: _externFunctions,
            ExternOpaqueTypes: new HashSet<string>(_externOpaqueTypes, StringComparer.Ordinal),
            UsesPrintInt: _usesPrintInt,
            UsesPrintStr: _usesPrintStr,
            UsesPrintBool: _usesPrintBool,
            UsesConcatStr: _usesConcatStr,
            UsesClosures: _usesClosures,
            UsesAsync: _usesAsync
        );
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

    private (int Temp, TypeRef Type) LowerExpr(Expr e)
    {
        var previousExpr = _currentSourceExpr;
        _currentSourceExpr = e;

        (int Temp, TypeRef Type) lowered = e switch
        {
            Expr.IntLit lit => LowerInt(lit),
            Expr.UIntLit lit => LowerUInt(lit),
            Expr.FloatLit lit => LowerFloat(lit),
            Expr.StrLit str => LowerStr(str),
            Expr.BoolLit b => LowerBool(b),
            Expr.Var v => LowerVar(v),
            Expr.QualifiedVar qv => LowerQualifiedVar(qv),
            Expr.Add add => LowerAdd(add),
            Expr.Subtract sub => LowerSubtract(sub),
            Expr.Multiply mul => LowerMultiply(mul),
            Expr.Divide div => LowerDivide(div),
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
            Expr.LetRec letRec => LowerLetRec(letRec),
            RecGroupExpr group => LowerRecGroup(group),
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
            _ => throw new NotSupportedException($"Unknown expr: {e.GetType().Name}")
        };

        RecordExprHoverType(e, lowered.Type);
        _currentSourceExpr = previousExpr;
        return (lowered.Temp, Prune(lowered.Type));
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

    /// <summary>
    /// Emits a bitmask AND so that <paramref name="valueTemp"/> wraps to <paramref name="bits"/> bits.
    /// For u64 (bits == 64) no masking is needed since i64 already wraps in two's complement.
    /// </summary>
    private int EmitUIntMask(int valueTemp, int bits)
    {
        if (bits == 64)
        {
            return valueTemp;
        }

        long mask = (1L << bits) - 1L;
        int maskTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(maskTemp, mask));
        int resultTemp = NewTemp();
        Emit(new IrInst.AndInt(resultTemp, valueTemp, maskTemp));
        return resultTemp;
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

        if (b is null && (_inSpecialization || _inParallelSpecialization) && _topLevelFunctionRefs.TryGetValue(v.Name, out var topRef))
        {
            // Reuse specialization: this name is a non-inlined top-level helper (e.g. an AVL height/max
            // reader) referenced from the spec's isolated scope, where its generation-site slot is gone.
            // It has an empty closure environment, so reconstruct its closure directly from the label with
            // a null env, and instantiate its type scheme fresh for this use (polymorphic helpers unify
            // against the concrete call). This is what lets non-allocating helpers stay out of the inline
            // set without hitting the Model-A forward-reference check (ASH014).
            int envTemp = NewTemp();
            Emit(new IrInst.LoadConstInt(envTemp, 0));
            int closTemp = NewTemp();
            Emit(new IrInst.MakeClosure(closTemp, topRef.Label, envTemp, 0));
            return (closTemp, Instantiate(topRef.Scheme));
        }

        if (b is null)
        {
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

            case Binding.ExternFunction externFunction:
                result = EmitExternFunctionThunk(externFunction.Function, externFunction.Type, GetSpan(v));
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

    private (int, TypeRef) LowerProgramArgs(int target, TypeRef type)
    {
        Emit(new IrInst.LoadProgramArgs(target));
        return (target, type);
    }

    private string ResolveModuleAlias(string moduleName)
    {
        return _moduleAliases.TryGetValue(moduleName, out var resolved) ? resolved : moduleName;
    }

    private (int, TypeRef) LowerQualifiedVar(Expr.QualifiedVar qv)
    {
        var resolvedModule = ResolveModuleAlias(qv.Module);

        if (BuiltinRegistry.TryGetModule(resolvedModule, out var builtinModule))
        {
            if (builtinModule.Members.ContainsKey(qv.Name))
            {
                var resolvedStdMember = ResolveBuiltinModuleMember(builtinModule, qv.Name);
                RecordHoverType(GetSpan(qv), $"{resolvedModule}.{qv.Name}", resolvedStdMember.Item2);
                return resolvedStdMember;
            }

            if (builtinModule.ResourceName is null)
            {
                return StdMemberNotFound(resolvedModule, qv.Name, GetSpan(qv));
            }
        }

        var sanitizedModuleName = ProjectSupport.SanitizeModuleBindingName(resolvedModule);
        var exportedBindingName = $"{sanitizedModuleName}_{qv.Name}";
        if (Lookup(exportedBindingName) is not null)
        {
            var resolvedQualifiedBinding = LowerVar(new Expr.Var(exportedBindingName));
            RecordHoverType(GetSpan(qv), $"{resolvedModule}.{qv.Name}", resolvedQualifiedBinding.Item2);
            return resolvedQualifiedBinding;
        }

        // User module: resolve to the sanitized module binding if it exists.
        var binding = Lookup(resolvedModule) ?? Lookup(sanitizedModuleName);
        if (binding is null)
        {
            ReportDiagnostic(GetSpan(qv), $"Unknown module '{qv.Module}'.");
            return ReturnNeverWithDummyTemp();
        }

        // Record field access fallback: `rec.fieldName` where `rec` is a bound record value.
        // Let-bindings create Binding.Scheme; lambda params create Binding.Local.
        int? recFieldSlot = null;
        TypeRef? recFieldType = null;
        if (binding is Binding.Local recLocal)
        {
            recFieldSlot = recLocal.Slot;
            recFieldType = Prune(recLocal.Type);
        }
        else if (binding is Binding.Scheme recScheme)
        {
            recFieldSlot = recScheme.Slot;
            recFieldType = Prune(Instantiate(recScheme.S));
        }

        if (recFieldSlot.HasValue
            && recFieldType is TypeRef.TNamedType namedRecType
            && namedRecType.Symbol.Constructors.Count == 1
            && namedRecType.Symbol.Constructors[0].DeclaringSyntax.FieldNames.Count > 0)
        {
            var ctor = namedRecType.Symbol.Constructors[0];
            var fieldNames = ctor.DeclaringSyntax.FieldNames;
            int fieldIdx = -1;
            for (int fi = 0; fi < fieldNames.Count; fi++)
            {
                if (string.Equals(fieldNames[fi], qv.Name, StringComparison.Ordinal))
                {
                    fieldIdx = fi;
                    break;
                }
            }

            if (fieldIdx >= 0)
            {
                int baseTemp = NewTemp();
                Emit(new IrInst.LoadLocal(baseTemp, recFieldSlot.Value));
                int fieldTemp = NewTemp();
                Emit(new IrInst.GetAdtField(fieldTemp, baseTemp, fieldIdx));
                var fieldType = InstantiateConstructorParameterType(ctor, fieldIdx, namedRecType);
                RecordHoverType(GetSpan(qv), $"{qv.Module}.{qv.Name}", fieldType);
                return (fieldTemp, fieldType);
            }
        }

        ReportDiagnostic(GetSpan(qv), $"Module '{qv.Module}' does not export '{qv.Name}'.");
        return ReturnNeverWithDummyTemp();
    }

    private (int, TypeRef) ResolveBuiltinModuleMember(BuiltinRegistry.BuiltinModule module, string name)
    {
        var member = module.Members[name];
        return member.Kind switch
        {
            BuiltinRegistry.BuiltinValueKind.Print => LowerQualifiedBuiltinFunctionReference(name, CreatePrintBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.Panic => LowerQualifiedBuiltinFunctionReference(name, CreatePanicBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.Args => LowerProgramArgs(NewTemp(), CreateArgsBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.Write => LowerQualifiedBuiltinFunctionReference(name, CreateWriteBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.WriteLine => LowerQualifiedBuiltinFunctionReference(name, CreateWriteLineBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.ReadLine => LowerQualifiedBuiltinFunctionReference(name, CreateReadLineBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.FileReadText => LowerQualifiedBuiltinFunctionReference(name, CreateFileReadTextBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.FileReadAllBytes => LowerQualifiedBuiltinFunctionReference(name, CreateFileReadAllBytesBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.FileMmap => LowerQualifiedBuiltinFunctionReference(name, CreateFileMmapBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.FileOpen => LowerQualifiedBuiltinFunctionReference(name, CreateFileOpenBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.FileReadChunk => LowerQualifiedBuiltinFunctionReference(name, CreateFileReadChunkBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.FileReadLine => LowerQualifiedBuiltinFunctionReference(name, CreateFileReadLineBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.FileClose => LowerQualifiedBuiltinFunctionReference(name, CreateFileCloseBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.InternalDeepCopy => LowerQualifiedBuiltinFunctionReference(name, CreateInternalDeepCopyBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.ParallelBoth => LowerQualifiedBuiltinFunctionReference(name, CreateParallelBothBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.FileWriteText => LowerQualifiedBuiltinFunctionReference(name, CreateFileWriteTextBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.FileExists => LowerQualifiedBuiltinFunctionReference(name, CreateFileExistsBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.TextUncons => LowerQualifiedBuiltinFunctionReference(name, CreateTextUnconsBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.TextParseInt => LowerQualifiedBuiltinFunctionReference(name, CreateTextParseIntBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.TextParseFloat => LowerQualifiedBuiltinFunctionReference(name, CreateTextParseFloatBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.TextFromInt => LowerQualifiedBuiltinFunctionReference(name, CreateTextFromIntBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.TextFromFloat => LowerQualifiedBuiltinFunctionReference(name, CreateTextFromFloatBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.TextFormatFloat => LowerQualifiedBuiltinFunctionReference(name, CreateTextFormatFloatBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.TextToHex => LowerQualifiedBuiltinFunctionReference(name, CreateTextToHexBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.HttpGet => LowerQualifiedBuiltinFunctionReference(name, CreateHttpGetBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.HttpPost => LowerQualifiedBuiltinFunctionReference(name, CreateHttpPostBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTcpConnect => LowerQualifiedBuiltinFunctionReference(name, CreateNetTcpConnectBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTcpSend => LowerQualifiedBuiltinFunctionReference(name, CreateNetTcpSendBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTcpReceive => LowerQualifiedBuiltinFunctionReference(name, CreateNetTcpReceiveBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTcpClose => LowerQualifiedBuiltinFunctionReference(name, CreateNetTcpCloseBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTlsConnect => LowerQualifiedBuiltinFunctionReference(name, CreateNetTlsConnectBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTlsSend => LowerQualifiedBuiltinFunctionReference(name, CreateNetTlsSendBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTlsReceive => LowerQualifiedBuiltinFunctionReference(name, CreateNetTlsReceiveBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTlsClose => LowerQualifiedBuiltinFunctionReference(name, CreateNetTlsCloseBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.AsyncRun => LowerQualifiedBuiltinFunctionReference(name, CreateAsyncRunBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.AsyncTask => LowerQualifiedBuiltinFunctionReference(name, CreateAsyncTaskBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.AsyncFromResult => LowerQualifiedBuiltinFunctionReference(name, CreateAsyncFromResultBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.AsyncSleep => LowerQualifiedBuiltinFunctionReference(name, CreateAsyncSleepBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.AsyncAll => LowerQualifiedBuiltinFunctionReference(name, CreateAsyncAllBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.AsyncRace => LowerQualifiedBuiltinFunctionReference(name, CreateAsyncRaceBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesEmpty => LowerQualifiedBuiltinFunctionReference(name, CreateBytesEmptyBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesSingleton => LowerQualifiedBuiltinFunctionReference(name, CreateBytesSingletonBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesLength => LowerQualifiedBuiltinFunctionReference(name, CreateBytesLengthBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesGet => LowerQualifiedBuiltinFunctionReference(name, CreateBytesGetBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesIndexOf => LowerQualifiedBuiltinFunctionReference(name, CreateBytesIndexOfBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesCompare => LowerQualifiedBuiltinFunctionReference(name, CreateBytesCompareBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesScanHash => LowerQualifiedBuiltinFunctionReference(name, CreateBytesScanHashBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesSubText => LowerQualifiedBuiltinFunctionReference(name, CreateBytesSubTextBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesSubView => LowerQualifiedBuiltinFunctionReference(name, CreateBytesSubViewBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesAppend => LowerQualifiedBuiltinFunctionReference(name, CreateBytesAppendBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesAppendByte => LowerQualifiedBuiltinFunctionReference(name, CreateBytesAppendByteBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesFromList => LowerQualifiedBuiltinFunctionReference(name, CreateBytesFromListBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesFromText => LowerQualifiedBuiltinFunctionReference(name, CreateBytesFromTextBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesHash => LowerQualifiedBuiltinFunctionReference(name, CreateBytesHashBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesU16Le => LowerQualifiedBuiltinFunctionReference(name, CreateBytesU16LeBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesU32Le => LowerQualifiedBuiltinFunctionReference(name, CreateBytesU32LeBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesU64Le => LowerQualifiedBuiltinFunctionReference(name, CreateBytesU64LeBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesGetU16Le => LowerQualifiedBuiltinFunctionReference(name, CreateBytesGetU16LeBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesGetU32Le => LowerQualifiedBuiltinFunctionReference(name, CreateBytesGetU32LeBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesGetU64Le => LowerQualifiedBuiltinFunctionReference(name, CreateBytesGetU64LeBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.UIntToInt => LowerQualifiedBuiltinFunctionReference(name, CreateUIntToIntBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.MathToFloat => LowerQualifiedBuiltinFunctionReference(name, CreateMathToFloatBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.MathSqrt => LowerQualifiedBuiltinFunctionReference(name, CreateMathSqrtBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.MathFloor => LowerQualifiedBuiltinFunctionReference(name, CreateMathFloorBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.MathCeil => LowerQualifiedBuiltinFunctionReference(name, CreateMathCeilBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.MathRound => LowerQualifiedBuiltinFunctionReference(name, CreateMathRoundBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.MathTrunc => LowerQualifiedBuiltinFunctionReference(name, CreateMathTruncBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.MathFloorToInt => LowerQualifiedBuiltinFunctionReference(name, CreateMathFloorToIntBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.MathRoundToInt => LowerQualifiedBuiltinFunctionReference(name, CreateMathRoundToIntBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.MathTruncToInt => LowerQualifiedBuiltinFunctionReference(name, CreateMathTruncToIntBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind k when LibmBuiltinKinds.TryGetValue(k, out var libmKind) => LowerQualifiedBuiltinFunctionReference(name, CreateLibmBinding(libmKind).S.Body),
            BuiltinRegistry.BuiltinValueKind.FileWriteBytes => LowerQualifiedBuiltinFunctionReference(name, CreateFileWriteBytesBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.IoReadExact => LowerQualifiedBuiltinFunctionReference(name, CreateReadExactBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.TextByteLength => LowerQualifiedBuiltinFunctionReference(name, CreateTextByteLengthBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.SpawnProcess => LowerQualifiedBuiltinFunctionReference(name, CreateSpawnProcessBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.ProcessWriteStdin => LowerQualifiedBuiltinFunctionReference(name, CreateProcessWriteStdinBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.ProcessReadStdoutLine => LowerQualifiedBuiltinFunctionReference(name, CreateProcessReadStdoutLineBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.ProcessReadStderrLine => LowerQualifiedBuiltinFunctionReference(name, CreateProcessReadStderrLineBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.ProcessWaitForExit => LowerQualifiedBuiltinFunctionReference(name, CreateProcessWaitForExitBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.ProcessKill => LowerQualifiedBuiltinFunctionReference(name, CreateProcessKillBinding().S.Body),
            _ => StdMemberNotFound(module.Name, name)
        };
    }

    private (int, TypeRef) LowerQualifiedBuiltinFunctionReference(string name, TypeRef type)
    {
        var temp = NewTemp();
        ReportDiagnostic(0, $"Intrinsic '{name}' must be called directly.");
        Emit(new IrInst.LoadConstInt(temp, 0));
        return (temp, type);
    }

    private (int, TypeRef) StdMemberNotFound(string module, string name)
    {
        return StdMemberNotFound(module, name, TextSpan.FromBounds(0, 1));
    }

    private (int, TypeRef) StdMemberNotFound(string module, string name, TextSpan span)
    {
        ReportDiagnostic(span, $"Unknown member '{name}' in module {module}.");
        return ReturnNeverWithDummyTemp();
    }

    private (int, TypeRef) LowerAdd(Expr.Add add)
    {
        using var diagnosticSpan = PushDiagnosticSpan(add);
        var (leftTemp, leftType) = LowerExpr(add.Left);
        var (rightTemp, rightType) = LowerExpr(add.Right);

        var leftPruned = Prune(leftType);
        var rightPruned = Prune(rightType);

        // Both operands unconstrained: don't eagerly pick Int. Unify them into one monomorphic var
        // (kept out of generalization via _addConstrainedTvars) so a later use resolves it — e.g.
        // the seed in `go("")(xs)` makes a `go(acc + x)` accumulator Str. Emit a provisional AddInt,
        // patched to ConcatStr/AddFloat in ResolveDeferredAdds once the operand type is known. If it
        // never resolves (an unused generic '+'), it defaults to Int there, matching the old result.
        if (leftPruned is TypeRef.TVar && rightPruned is TypeRef.TVar)
        {
            Unify(leftPruned, rightPruned);
            if (Prune(leftPruned) is TypeRef.TVar sharedVar)
            {
                _addConstrainedVars.Add(sharedVar);
                _hasDeferredAdds = true;
                int deferredTarget = NewTemp();
                Emit(new IrInst.AddInt(deferredTarget, leftTemp, rightTemp, sharedVar));
                return (deferredTarget, sharedVar);
            }
        }

        // Resolve type variables: prefer the other side's concrete type, defaulting to Int
        if (leftPruned is TypeRef.TVar)
        {
            TypeRef resolved = rightPruned switch
            {
                TypeRef.TStr => new TypeRef.TStr(),
                TypeRef.TUInt u => (TypeRef)new TypeRef.TUInt(u.Bits),
                _ => new TypeRef.TInt()
            };
            Unify(leftPruned, resolved);
            leftPruned = resolved;
        }
        if (rightPruned is TypeRef.TVar)
        {
            TypeRef resolved = leftPruned switch
            {
                TypeRef.TStr => new TypeRef.TStr(),
                TypeRef.TFloat => new TypeRef.TFloat(),
                TypeRef.TUInt u => (TypeRef)new TypeRef.TUInt(u.Bits),
                _ => new TypeRef.TInt()
            };
            Unify(rightPruned, resolved);
            rightPruned = resolved;
        }

        if (leftPruned is TypeRef.TInt && rightPruned is TypeRef.TInt)
        {
            int target = NewTemp();
            Emit(new IrInst.AddInt(target, leftTemp, rightTemp));
            return (target, new TypeRef.TInt());
        }

        if (leftPruned is TypeRef.TUInt luint && rightPruned is TypeRef.TUInt ruint)
        {
            if (luint.Bits != ruint.Bits)
            {
                var addUintTypes = PrettyPair(leftPruned, rightPruned);
                ReportDiagnostic(GetSpan(add), $"'+' requires matching unsigned widths, got {addUintTypes.Left} and {addUintTypes.Right}.", DiagnosticCodes.TypeMismatch);
                return CreateIntErrorFallback();
            }
            int raw = NewTemp();
            Emit(new IrInst.AddInt(raw, leftTemp, rightTemp));
            int wrapped = EmitUIntMask(raw, luint.Bits);
            return (wrapped, luint);
        }

        if (leftPruned is TypeRef.TFloat && rightPruned is TypeRef.TFloat)
        {
            int target = NewTemp();
            Emit(new IrInst.AddFloat(target, leftTemp, rightTemp));
            return (target, new TypeRef.TFloat());
        }

        if (leftPruned is TypeRef.TStr && rightPruned is TypeRef.TStr)
        {
            _usesConcatStr = true;
            int target = NewTemp();
            Emit(new IrInst.ConcatStr(target, leftTemp, rightTemp));
            return (target, new TypeRef.TStr());
        }

        var addTypes = PrettyPair(leftPruned, rightPruned);
        ReportDiagnostic(GetSpan(add), $"'+' requires Int+Int, Float+Float, or Str+Str, got {addTypes.Left} and {addTypes.Right}.", DiagnosticCodes.TypeMismatch);
        int errorTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(errorTemp, 0));
        return (errorTemp, new TypeRef.TInt());
    }

    private (int, TypeRef) LowerSubtract(Expr.Subtract sub)
    {
        using var diagnosticSpan = PushDiagnosticSpan(sub);
        var (leftTemp, leftType) = LowerExpr(sub.Left);
        var (rightTemp, rightType) = LowerExpr(sub.Right);

        return LowerNumericBinaryOp(sub, leftTemp, leftType, rightTemp, rightType, (target, left, right) => new IrInst.SubInt(target, left, right), (target, left, right) => new IrInst.SubFloat(target, left, right), "'-'");
    }

    private (int, TypeRef) LowerMultiply(Expr.Multiply mul)
    {
        using var diagnosticSpan = PushDiagnosticSpan(mul);
        var (leftTemp, leftType) = LowerExpr(mul.Left);
        var (rightTemp, rightType) = LowerExpr(mul.Right);

        return LowerNumericBinaryOp(mul, leftTemp, leftType, rightTemp, rightType, (target, left, right) => new IrInst.MulInt(target, left, right), (target, left, right) => new IrInst.MulFloat(target, left, right), "'*'");
    }

    private (int, TypeRef) LowerDivide(Expr.Divide div)
    {
        using var diagnosticSpan = PushDiagnosticSpan(div);
        var (leftTemp, leftType) = LowerExpr(div.Left);
        var (rightTemp, rightType) = LowerExpr(div.Right);

        return LowerNumericBinaryOp(div, leftTemp, leftType, rightTemp, rightType, (target, left, right) => new IrInst.DivInt(target, left, right), (target, left, right) => new IrInst.DivFloat(target, left, right), "'/'", (target, left, right) => new IrInst.DivUInt(target, left, right));
    }

    private (int, TypeRef) LowerBitwiseAnd(Expr.BitwiseAnd bitAnd)
    {
        using var diagnosticSpan = PushDiagnosticSpan(bitAnd);
        var (leftTemp, leftType) = LowerExpr(bitAnd.Left);
        var (rightTemp, rightType) = LowerExpr(bitAnd.Right);

        return LowerIntBinaryOp(bitAnd, leftTemp, leftType, rightTemp, rightType, (target, left, right) => new IrInst.AndInt(target, left, right), "'&'");
    }

    private (int, TypeRef) LowerBitwiseOr(Expr.BitwiseOr bitOr)
    {
        using var diagnosticSpan = PushDiagnosticSpan(bitOr);
        var (leftTemp, leftType) = LowerExpr(bitOr.Left);
        var (rightTemp, rightType) = LowerExpr(bitOr.Right);

        return LowerIntBinaryOp(bitOr, leftTemp, leftType, rightTemp, rightType, (target, left, right) => new IrInst.OrInt(target, left, right), "'|'");
    }

    private (int, TypeRef) LowerBitwiseXor(Expr.BitwiseXor bitXor)
    {
        using var diagnosticSpan = PushDiagnosticSpan(bitXor);
        var (leftTemp, leftType) = LowerExpr(bitXor.Left);
        var (rightTemp, rightType) = LowerExpr(bitXor.Right);

        return LowerIntBinaryOp(bitXor, leftTemp, leftType, rightTemp, rightType, (target, left, right) => new IrInst.XorInt(target, left, right), "'^'");
    }

    private (int, TypeRef) LowerShiftLeft(Expr.ShiftLeft shiftLeft)
    {
        using var diagnosticSpan = PushDiagnosticSpan(shiftLeft);
        var (leftTemp, leftType) = LowerExpr(shiftLeft.Left);
        var (rightTemp, rightType) = LowerExpr(shiftLeft.Right);

        return LowerIntBinaryOp(shiftLeft, leftTemp, leftType, rightTemp, rightType, (target, left, right) => new IrInst.ShlInt(target, left, right), "'<<'");
    }

    private (int, TypeRef) LowerShiftRight(Expr.ShiftRight shiftRight)
    {
        using var diagnosticSpan = PushDiagnosticSpan(shiftRight);
        var (leftTemp, leftType) = LowerExpr(shiftRight.Left);
        var (rightTemp, rightType) = LowerExpr(shiftRight.Right);

        return LowerIntBinaryOp(shiftRight, leftTemp, leftType, rightTemp, rightType, (target, left, right) => new IrInst.ShrInt(target, left, right), "'>>'");
    }

    private (int, TypeRef) LowerBitwiseNot(Expr.BitwiseNot bitwiseNot)
    {
        using var diagnosticSpan = PushDiagnosticSpan(bitwiseNot);
        var (operandTemp, operandType) = LowerExpr(bitwiseNot.Operand);
        var prunedOperandType = Prune(operandType);
        if (prunedOperandType is TypeRef.TVar)
        {
            Unify(prunedOperandType, new TypeRef.TInt());
            prunedOperandType = new TypeRef.TInt();
        }

        if (prunedOperandType is TypeRef.TUInt uintType)
        {
            // ~x for unsigned: XOR with the width mask so result stays within bit width.
            long uintMask = uintType.Bits == 64 ? -1L : (1L << uintType.Bits) - 1L;
            int maskTemp = NewTemp();
            Emit(new IrInst.LoadConstInt(maskTemp, uintMask));
            int xorTemp = NewTemp();
            Emit(new IrInst.XorInt(xorTemp, operandTemp, maskTemp));
            // Result already fits in width, no extra masking needed.
            return (xorTemp, uintType);
        }

        if (prunedOperandType is not TypeRef.TInt)
        {
            ReportDiagnostic(GetSpan(bitwiseNot), $"'~' requires Int or unsigned integer, got {Pretty(prunedOperandType)}.", DiagnosticCodes.TypeMismatch);
            return CreateIntErrorFallback();
        }

        int allOnes = NewTemp();
        Emit(new IrInst.LoadConstInt(allOnes, -1));
        int target = NewTemp();
        Emit(new IrInst.XorInt(target, operandTemp, allOnes));
        return (target, new TypeRef.TInt());
    }

    private (int, TypeRef) LowerGreaterThan(Expr.GreaterThan gt)
    {
        using var diagnosticSpan = PushDiagnosticSpan(gt);
        var (leftTemp, leftType) = LowerExpr(gt.Left);
        var (rightTemp, rightType) = LowerExpr(gt.Right);

        return LowerNumericComparisonOp(gt, leftTemp, leftType, rightTemp, rightType, (target, left, right) => new IrInst.CmpIntGt(target, left, right), (target, left, right) => new IrInst.CmpFloatGt(target, left, right), (target, left, right) => new IrInst.CmpUIntGt(target, left, right), "'>'");
    }

    private (int, TypeRef) LowerGreaterOrEqual(Expr.GreaterOrEqual ge)
    {
        using var diagnosticSpan = PushDiagnosticSpan(ge);
        var (leftTemp, leftType) = LowerExpr(ge.Left);
        var (rightTemp, rightType) = LowerExpr(ge.Right);

        return LowerNumericComparisonOp(ge, leftTemp, leftType, rightTemp, rightType, (target, left, right) => new IrInst.CmpIntGe(target, left, right), (target, left, right) => new IrInst.CmpFloatGe(target, left, right), (target, left, right) => new IrInst.CmpUIntGe(target, left, right), "'>='");
    }

    private (int, TypeRef) LowerLessThan(Expr.LessThan lt)
    {
        using var diagnosticSpan = PushDiagnosticSpan(lt);
        var (leftTemp, leftType) = LowerExpr(lt.Left);
        var (rightTemp, rightType) = LowerExpr(lt.Right);

        return LowerNumericComparisonOp(lt, leftTemp, leftType, rightTemp, rightType, (target, left, right) => new IrInst.CmpIntLt(target, left, right), (target, left, right) => new IrInst.CmpFloatLt(target, left, right), (target, left, right) => new IrInst.CmpUIntLt(target, left, right), "'<'");
    }

    private (int, TypeRef) LowerLessOrEqual(Expr.LessOrEqual le)
    {
        using var diagnosticSpan = PushDiagnosticSpan(le);
        var (leftTemp, leftType) = LowerExpr(le.Left);
        var (rightTemp, rightType) = LowerExpr(le.Right);

        return LowerNumericComparisonOp(le, leftTemp, leftType, rightTemp, rightType, (target, left, right) => new IrInst.CmpIntLe(target, left, right), (target, left, right) => new IrInst.CmpFloatLe(target, left, right), (target, left, right) => new IrInst.CmpUIntLe(target, left, right), "'<='");
    }

    private (int, TypeRef) LowerEqual(Expr.Equal eq)
    {
        return LowerEqualityOp(eq.Left, eq.Right, negate: false);
    }

    private (int, TypeRef) LowerNotEqual(Expr.NotEqual ne)
    {
        return LowerEqualityOp(ne.Left, ne.Right, negate: true);
    }

    private (int, TypeRef) LowerResultPipe(Expr.ResultPipe pipe)
    {
        using var diagnosticSpan = PushDiagnosticSpan(pipe);
        if (!TryGetStandardResultParts(out var resultSymbol, out var okConstructor, out _))
        {
            return ReturnNeverWithDummyTemp();
        }

        var (leftTemp, leftType) = LowerExpr(pipe.Left);
        var errorType = NewTypeVar();
        var successType = NewTypeVar();
        var expectedLeftType = new TypeRef.TNamedType(resultSymbol, [errorType, successType]);
        Unify(leftType, expectedLeftType);

        if (!TryGetResultTypeArgs(Prune(leftType), resultSymbol, out errorType, out successType))
        {
            return ReturnNeverWithDummyTemp();
        }

        var (funcTemp, funcType) = LowerExpr(pipe.Right);
        var returnType = NewTypeVar();
        Unify(funcType, new TypeRef.TFun(successType, returnType));

        if (Prune(funcType) is not TypeRef.TFun and not TypeRef.TVar)
        {
            return ReturnNeverWithDummyTemp();
        }

        var prunedErrorType = Prune(errorType);
        var prunedReturnType = Prune(returnType);
        var isFlatMap = TryGetResultTypeArgs(prunedReturnType, resultSymbol, out var nestedErrorType, out var nestedSuccessType);
        if (isFlatMap)
        {
            Unify(prunedErrorType, nestedErrorType);
        }

        TypeRef resultType = isFlatMap
            ? new TypeRef.TNamedType(resultSymbol, [Prune(prunedErrorType), Prune(nestedSuccessType)])
            : new TypeRef.TNamedType(resultSymbol, [Prune(prunedErrorType), prunedReturnType]);

        var resultSlot = NewLocal();
        var errorLabel = NewLabel("result_error");
        var endLabel = NewLabel("result_end");

        var tagTemp = NewTemp();
        var expectedOkTagTemp = NewTemp();
        var isOkTemp = NewTemp();
        Emit(new IrInst.GetAdtTag(tagTemp, leftTemp));
        Emit(new IrInst.LoadConstInt(expectedOkTagTemp, GetConstructorTag(okConstructor)));
        Emit(new IrInst.CmpIntEq(isOkTemp, tagTemp, expectedOkTagTemp));
        Emit(new IrInst.JumpIfFalse(isOkTemp, errorLabel));

        var payloadTemp = NewTemp();
        Emit(new IrInst.GetAdtField(payloadTemp, leftTemp, 0));
        var rhsResultTemp = NewTemp();
        Emit(new IrInst.CallClosure(rhsResultTemp, funcTemp, payloadTemp));

        if (isFlatMap)
        {
            Emit(new IrInst.StoreLocal(resultSlot, rhsResultTemp));
        }
        else
        {
            var wrappedTemp = LowerSingleFieldConstructorValue(okConstructor, rhsResultTemp);
            Emit(new IrInst.StoreLocal(resultSlot, wrappedTemp));
        }

        Emit(new IrInst.Jump(endLabel));
        Emit(new IrInst.Label(errorLabel));
        Emit(new IrInst.StoreLocal(resultSlot, leftTemp));
        Emit(new IrInst.Label(endLabel));

        var resultTemp = NewTemp();
        Emit(new IrInst.LoadLocal(resultTemp, resultSlot));
        return (resultTemp, Prune(resultType));
    }

    private (int, TypeRef) LowerResultMapErrorPipe(Expr.ResultMapErrorPipe pipe)
    {
        using var diagnosticSpan = PushDiagnosticSpan(pipe);
        if (!TryGetStandardResultParts(out var resultSymbol, out _, out var errorConstructor))
        {
            return ReturnNeverWithDummyTemp();
        }

        var (leftTemp, leftType) = LowerExpr(pipe.Left);
        var errorType = NewTypeVar();
        var successType = NewTypeVar();
        var expectedLeftType = new TypeRef.TNamedType(resultSymbol, [errorType, successType]);
        Unify(leftType, expectedLeftType);

        if (!TryGetResultTypeArgs(Prune(leftType), resultSymbol, out errorType, out successType))
        {
            return ReturnNeverWithDummyTemp();
        }

        var (funcTemp, funcType) = LowerExpr(pipe.Right);
        var mappedErrorType = NewTypeVar();
        Unify(funcType, new TypeRef.TFun(errorType, mappedErrorType));

        if (Prune(funcType) is not TypeRef.TFun and not TypeRef.TVar)
        {
            return ReturnNeverWithDummyTemp();
        }

        TypeRef resultType = new TypeRef.TNamedType(resultSymbol, [Prune(mappedErrorType), Prune(successType)]);
        var resultSlot = NewLocal();
        var errorLabel = NewLabel("result_map_error");
        var endLabel = NewLabel("result_map_error_end");

        var tagTemp = NewTemp();
        var expectedErrorTagTemp = NewTemp();
        var isErrorTemp = NewTemp();
        Emit(new IrInst.GetAdtTag(tagTemp, leftTemp));
        Emit(new IrInst.LoadConstInt(expectedErrorTagTemp, GetConstructorTag(errorConstructor)));
        Emit(new IrInst.CmpIntEq(isErrorTemp, tagTemp, expectedErrorTagTemp));
        Emit(new IrInst.JumpIfFalse(isErrorTemp, errorLabel));

        var payloadTemp = NewTemp();
        Emit(new IrInst.GetAdtField(payloadTemp, leftTemp, 0));
        var mappedPayloadTemp = NewTemp();
        Emit(new IrInst.CallClosure(mappedPayloadTemp, funcTemp, payloadTemp));
        var wrappedTemp = LowerSingleFieldConstructorValue(errorConstructor, mappedPayloadTemp);
        Emit(new IrInst.StoreLocal(resultSlot, wrappedTemp));
        Emit(new IrInst.Jump(endLabel));

        Emit(new IrInst.Label(errorLabel));
        Emit(new IrInst.StoreLocal(resultSlot, leftTemp));
        Emit(new IrInst.Label(endLabel));

        var resultTemp = NewTemp();
        Emit(new IrInst.LoadLocal(resultTemp, resultSlot));
        return (resultTemp, Prune(resultType));
    }

    private (int, TypeRef) LowerEqualityOp(Expr left, Expr right, bool negate)
    {
        using var diagnosticSpan = PushDiagnosticSpan(CombineSpans(left, right));
        var (leftTemp, leftType) = LowerExpr(left);
        var (rightTemp, rightType) = LowerExpr(right);

        var leftPruned = Prune(leftType);
        var rightPruned = Prune(rightType);

        // Resolve type variables: prefer the other side's concrete type, defaulting to Int
        if (leftPruned is TypeRef.TVar)
        {
            TypeRef resolved = rightPruned switch
            {
                TypeRef.TStr => new TypeRef.TStr(),
                TypeRef.TUInt u => (TypeRef)new TypeRef.TUInt(u.Bits),
                _ => new TypeRef.TInt()
            };
            Unify(leftPruned, resolved);
            leftPruned = resolved;
        }
        if (rightPruned is TypeRef.TVar)
        {
            TypeRef resolved = leftPruned switch
            {
                TypeRef.TStr => new TypeRef.TStr(),
                TypeRef.TFloat => new TypeRef.TFloat(),
                TypeRef.TUInt u => (TypeRef)new TypeRef.TUInt(u.Bits),
                _ => new TypeRef.TInt()
            };
            Unify(rightPruned, resolved);
            rightPruned = resolved;
        }

        if (leftPruned is TypeRef.TInt && rightPruned is TypeRef.TInt)
        {
            int target = NewTemp();
            Emit(negate ? new IrInst.CmpIntNe(target, leftTemp, rightTemp) : new IrInst.CmpIntEq(target, leftTemp, rightTemp));
            return (target, new TypeRef.TBool());
        }

        if (leftPruned is TypeRef.TUInt luint && rightPruned is TypeRef.TUInt ruint)
        {
            if (luint.Bits != ruint.Bits)
            {
                var uintWidthTypes = PrettyPair(leftPruned, rightPruned);
                var eqOp = negate ? "!=" : "==";
                ReportDiagnostic(CombineSpans(left, right), $"'{eqOp}' requires matching unsigned widths, got {uintWidthTypes.Left} and {uintWidthTypes.Right}.", DiagnosticCodes.TypeMismatch);
                int boolFallback = NewTemp();
                Emit(new IrInst.LoadConstBool(boolFallback, false));
                return (boolFallback, new TypeRef.TBool());
            }
            int target = NewTemp();
            Emit(negate ? new IrInst.CmpIntNe(target, leftTemp, rightTemp) : new IrInst.CmpIntEq(target, leftTemp, rightTemp));
            return (target, new TypeRef.TBool());
        }

        if (leftPruned is TypeRef.TFloat && rightPruned is TypeRef.TFloat)
        {
            int target = NewTemp();
            Emit(negate ? new IrInst.CmpFloatNe(target, leftTemp, rightTemp) : new IrInst.CmpFloatEq(target, leftTemp, rightTemp));
            return (target, new TypeRef.TBool());
        }

        if (leftPruned is TypeRef.TStr && rightPruned is TypeRef.TStr)
        {
            int target = NewTemp();
            Emit(negate ? new IrInst.CmpStrNe(target, leftTemp, rightTemp) : new IrInst.CmpStrEq(target, leftTemp, rightTemp));
            return (target, new TypeRef.TBool());
        }

        var op = negate ? "!=" : "==";
        var equalityTypes = PrettyPair(leftPruned, rightPruned);
        ReportDiagnostic(0, $"'{op}' requires Int{op}Int, Float{op}Float, or Str{op}Str, got {equalityTypes.Left} and {equalityTypes.Right}.", DiagnosticCodes.TypeMismatch);
        int errorTemp = NewTemp();
        Emit(new IrInst.LoadConstBool(errorTemp, false));
        return (errorTemp, new TypeRef.TBool());
    }

    private (int, TypeRef) LowerNumericBinaryOp(
        Expr expr,
        int leftTemp,
        TypeRef leftType,
        int rightTemp,
        TypeRef rightType,
        Func<int, int, int, IrInst> intFactory,
        Func<int, int, int, IrInst> floatFactory,
        string op,
        Func<int, int, int, IrInst>? uintFactory = null)
    {
        var (resolvedLeft, resolvedRight) = ResolveNumericOperandTypes(leftType, rightType);

        if (resolvedLeft is TypeRef.TInt && resolvedRight is TypeRef.TInt)
        {
            int target = NewTemp();
            Emit(intFactory(target, leftTemp, rightTemp));
            return (target, new TypeRef.TInt());
        }

        if (resolvedLeft is TypeRef.TUInt luint && resolvedRight is TypeRef.TUInt ruint)
        {
            if (luint.Bits != ruint.Bits)
            {
                var uintWidthTypes = PrettyPair(resolvedLeft, resolvedRight);
                ReportDiagnostic(GetSpan(expr), $"{op} requires matching unsigned widths, got {uintWidthTypes.Left} and {uintWidthTypes.Right}.", DiagnosticCodes.TypeMismatch);
                return CreateIntErrorFallback();
            }
            int raw = NewTemp();
            Emit((uintFactory ?? intFactory)(raw, leftTemp, rightTemp));
            int wrapped = EmitUIntMask(raw, luint.Bits);
            return (wrapped, luint);
        }

        if (resolvedLeft is TypeRef.TFloat && resolvedRight is TypeRef.TFloat)
        {
            int target = NewTemp();
            Emit(floatFactory(target, leftTemp, rightTemp));
            return (target, new TypeRef.TFloat());
        }

        var types = PrettyPair(resolvedLeft, resolvedRight);
        ReportDiagnostic(GetSpan(expr), $"{op} requires Int{op}Int or Float{op}Float, got {types.Left} and {types.Right}.", DiagnosticCodes.TypeMismatch);
        return CreateIntErrorFallback();
    }

    private (int, TypeRef) LowerIntBinaryOp(
        Expr expr,
        int leftTemp,
        TypeRef leftType,
        int rightTemp,
        TypeRef rightType,
        Func<int, int, int, IrInst> intFactory,
        string op)
    {
        var left = Prune(leftType);
        var right = Prune(rightType);

        if (left is TypeRef.TVar)
        {
            TypeRef resolved = right is TypeRef.TUInt u ? (TypeRef)new TypeRef.TUInt(u.Bits) : new TypeRef.TInt();
            Unify(left, resolved);
            left = resolved;
        }

        if (right is TypeRef.TVar)
        {
            TypeRef resolved = left is TypeRef.TUInt u ? (TypeRef)new TypeRef.TUInt(u.Bits) : new TypeRef.TInt();
            Unify(right, resolved);
            right = resolved;
        }

        if (left is TypeRef.TInt && right is TypeRef.TInt)
        {
            int target = NewTemp();
            Emit(intFactory(target, leftTemp, rightTemp));
            return (target, new TypeRef.TInt());
        }

        if (left is TypeRef.TUInt luint && right is TypeRef.TUInt ruint)
        {
            if (luint.Bits != ruint.Bits)
            {
                var uintWidthTypes = PrettyPair(left, right);
                ReportDiagnostic(GetSpan(expr), $"{op} requires matching unsigned widths, got {uintWidthTypes.Left} and {uintWidthTypes.Right}.", DiagnosticCodes.TypeMismatch);
                return CreateIntErrorFallback();
            }
            int raw = NewTemp();
            Emit(intFactory(raw, leftTemp, rightTemp));
            int wrapped = EmitUIntMask(raw, luint.Bits);
            return (wrapped, luint);
        }

        var types = PrettyPair(left, right);
        ReportDiagnostic(GetSpan(expr), $"{op} requires Int{op}Int, got {types.Left} and {types.Right}.", DiagnosticCodes.TypeMismatch);
        return CreateIntErrorFallback();
    }

    private (int, TypeRef) CreateIntErrorFallback()
    {
        int fallback = NewTemp();
        Emit(new IrInst.LoadConstInt(fallback, 0));
        return (fallback, new TypeRef.TInt());
    }

    private (int, TypeRef) LowerNumericComparisonOp(
        Expr expr,
        int leftTemp,
        TypeRef leftType,
        int rightTemp,
        TypeRef rightType,
        Func<int, int, int, IrInst> intFactory,
        Func<int, int, int, IrInst> floatFactory,
        Func<int, int, int, IrInst>? uintFactory,
        string op)
    {
        var (resolvedLeft, resolvedRight) = ResolveNumericOperandTypes(leftType, rightType);

        if (resolvedLeft is TypeRef.TInt && resolvedRight is TypeRef.TInt)
        {
            int target = NewTemp();
            Emit(intFactory(target, leftTemp, rightTemp));
            return (target, new TypeRef.TBool());
        }

        if (resolvedLeft is TypeRef.TUInt luint && resolvedRight is TypeRef.TUInt ruint)
        {
            if (luint.Bits != ruint.Bits)
            {
                var uintWidthTypes = PrettyPair(resolvedLeft, resolvedRight);
                ReportDiagnostic(GetSpan(expr), $"{op} requires matching unsigned widths, got {uintWidthTypes.Left} and {uintWidthTypes.Right}.", DiagnosticCodes.TypeMismatch);
                int boolFallback = NewTemp();
                Emit(new IrInst.LoadConstBool(boolFallback, false));
                return (boolFallback, new TypeRef.TBool());
            }
            int target = NewTemp();
            Emit((uintFactory ?? intFactory)(target, leftTemp, rightTemp));
            return (target, new TypeRef.TBool());
        }

        if (resolvedLeft is TypeRef.TFloat && resolvedRight is TypeRef.TFloat)
        {
            int target = NewTemp();
            Emit(floatFactory(target, leftTemp, rightTemp));
            return (target, new TypeRef.TBool());
        }

        var types = PrettyPair(resolvedLeft, resolvedRight);
        ReportDiagnostic(GetSpan(expr), $"{op} requires Int{op}Int or Float{op}Float, got {types.Left} and {types.Right}.", DiagnosticCodes.TypeMismatch);
        int fallback = NewTemp();
        Emit(new IrInst.LoadConstBool(fallback, false));
        return (fallback, new TypeRef.TBool());
    }

    private (TypeRef Left, TypeRef Right) ResolveNumericOperandTypes(TypeRef leftType, TypeRef rightType)
    {
        var left = Prune(leftType);
        var right = Prune(rightType);

        if (left is TypeRef.TVar)
        {
            TypeRef resolved = right switch
            {
                TypeRef.TFloat => new TypeRef.TFloat(),
                TypeRef.TUInt u => (TypeRef)new TypeRef.TUInt(u.Bits),
                _ => new TypeRef.TInt()
            };
            Unify(left, resolved);
            left = resolved;
        }

        if (right is TypeRef.TVar)
        {
            TypeRef resolved = left switch
            {
                TypeRef.TFloat => new TypeRef.TFloat(),
                TypeRef.TUInt u => (TypeRef)new TypeRef.TUInt(u.Bits),
                _ => new TypeRef.TInt()
            };
            Unify(right, resolved);
            right = resolved;
        }

        return (left, right);
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

    private (int, TypeRef) LowerLet(Expr.Let let)
    {
        var savedTailPos = _tcoCtx?.InTailPosition ?? false;
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        // Save the arena watermark before the bound value so allocations from
        // both value and body belong to this let scope.
        EmitArenaWatermark();

        int depth0Before = _depth0LambdaCount;
        var (valueTemp, valueType) = LowerLetValue(let);

        // If the user wrote a type annotation, verify it matches the inferred type.
        if (let.TypeAnnotation is { } letAnnotation)
        {
            var annotatedType = ResolveTypeExpr(letAnnotation);
            Unify(annotatedType, valueType);
        }

        int slot = NewLocal();
        Emit(new IrInst.StoreLocal(slot, valueTemp));
        RecordLocalDebugInfo(slot, let.Name, valueType);
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
        TrackLetOwnership(let, slot, valueType);

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

        return LowerExpr(let.Value);
    }

    private void PushLetScope(Expr.Let let, int slot, TypeScheme scheme)
    {
        var parent = _scopes.Peek();
        _scopes.Push(new Dictionary<string, Binding>(parent, StringComparer.Ordinal)
        {
            [let.Name] = new Binding.Scheme(slot, scheme, AstSpans.GetLetNameOrDefault(let))
        });
    }

    private void TrackLetOwnership(Expr.Let let, int slot, TypeRef valueType)
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
                TrackOwnedValue(let.Name, slot, ownedTypeName, isResource, AstSpans.GetLetNameOrDefault(let), prunedValueType);
            }
        }
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

    private (int, TypeRef) LowerLetRec(Expr.LetRec letRec)
    {
        int slot = NewLocal();
        // The module system may wrap a lambda in alias lets: let alias = mangled in fun (x) -> ...
        // Unwrap let-chains to find the innermost lambda for type and TCO purposes.
        var innerLambda = FindInnermostLambdaUnderLets(letRec.Value);
        var recType = innerLambda is not null
            ? (TypeRef)new TypeRef.TFun(NewTypeVar(), NewTypeVar())
            : NewTypeVar();
        RecordLocalDebugInfo(slot, letRec.Name, recType);

        var parent = _scopes.Peek();
        var child = new Dictionary<string, Binding>(parent, StringComparer.Ordinal)
        {
            [letRec.Name] = new Binding.Local(slot, recType, AstSpans.GetLetRecNameOrDefault(letRec))
        };
        _scopes.Push(child);

        (int valTemp, TypeRef valType) valueAndType;
        if (letRec.Value is Expr.Lambda lam)
        {
            // Detect lambda chain for TCO: fun (x) -> fun (y) -> body
            var paramCount = CountLambdaChain(lam);
            var innermostBody = GetInnermostBody(lam);
            var hasTailSelfCalls = HasTailSelfCalls(innermostBody, letRec.Name, paramCount);

            var savedTcoCtx = _tcoCtx;
            if (hasTailSelfCalls)
            {
                var tcoParamNames = CollectLambdaParams(lam);
                _tcoCtx = new TcoContext
                {
                    SelfName = letRec.Name,
                    ParamCount = paramCount,
                    ParamNames = tcoParamNames,
                    InTailPosition = false,
                    LoopInvariantParams = CollectLoopInvariantParams(GetInnermostBody(lam), tcoParamNames, letRec.Name)
                };
            }
            else
            {
                _tcoCtx = null;
            }

            valueAndType = LowerLambdaRecursive(letRec.Name, recType, lam);

            _tcoCtx = savedTcoCtx;
        }
        else if (innerLambda is not null)
        {
            // Value is a let-chain of alias bindings (injected by the module system) wrapping a lambda.
            // Process each alias let into scope first, then lower the innermost lambda with the
            // self-reference (selfName) set so that recursive calls use Binding.Self rather than
            // capturing the uninitialized slot value (which would be 0 at closure-creation time).
            //
            // Self-aliases (let unmangledName = mangledSelf) must NOT be processed as regular lets
            // because the mangled slot is uninitialized at this point. Instead, they are collected
            // as selfAliases and given Binding.Self treatment inside LowerLambdaCore.
            var savedTcoCtx = _tcoCtx;
            _tcoCtx = null;

            int aliasCount = 0;
            List<string>? selfAliases = null;
            var aliasExpr = letRec.Value;
            while (aliasExpr is Expr.Let aliasLet)
            {
                if (aliasLet.Value is Expr.Var selfVar && string.Equals(selfVar.Name, letRec.Name, StringComparison.Ordinal))
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

            valueAndType = LowerLambdaRecursive(letRec.Name, recType, innerLambda, selfAliases: selfAliases);

            for (int i = 0; i < aliasCount; i++)
            {
                _scopes.Pop();
            }

            _tcoCtx = savedTcoCtx;
        }
        else
        {
            ReportDiagnostic(GetSpan(letRec.Value), "let rec currently requires a function value.");
            valueAndType = LowerExpr(letRec.Value);
        }

        Unify(recType, valueAndType.valType);

        // If the user wrote a type annotation, verify it matches the inferred type.
        if (letRec.TypeAnnotation is { } recAnnotation)
        {
            var annotatedRecType = ResolveTypeExpr(recAnnotation);
            Unify(annotatedRecType, recType);
        }

        RecordHoverType(AstSpans.GetLetRecNameOrDefault(letRec), letRec.Name, recType);
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
        if (_lambdaDepth == 0 && _lastLoweredLambdaEmptyEnv && letRec.Value is Expr.Lambda)
        {
            var selfScope = _scopes.Pop();
            var helperScheme = FreshenScheme(Generalize(Prune(recType)));
            _scopes.Push(selfScope);
            _topLevelFunctionRefs[letRec.Name] = (_lastLoweredLambdaLabel, helperScheme);
        }

        var (bodyTemp, bodyType) = LowerExpr(letRec.Body);
        _scopes.Pop();
        return (bodyTemp, bodyType);
    }

    private static Expr.Lambda? FindInnermostLambdaUnderLets(Expr value)
    {
        if (value is Expr.Lambda lam) return lam;
        if (value is Expr.Let let) return FindInnermostLambdaUnderLets(let.Body);
        return null;
    }

    private static int CountLambdaChain(Expr.Lambda lam)
    {
        int count = 1;
        var body = lam.Body;
        while (body is Expr.Lambda inner)
        {
            count++;
            body = inner.Body;
        }
        return count;
    }

    private static List<string> CollectLambdaParams(Expr.Lambda lam)
    {
        var names = new List<string> { lam.ParamName };
        var body = lam.Body;
        while (body is Expr.Lambda inner)
        {
            names.Add(inner.ParamName);
            body = inner.Body;
        }
        return names;
    }

    private static Expr GetInnermostBody(Expr.Lambda lam)
    {
        var body = lam.Body;
        while (body is Expr.Lambda inner)
        {
            body = inner.Body;
        }
        return body;
    }

    /// <summary>Check if an expression has any tail-position calls to the named function with the expected arg count.</summary>
    private static bool HasTailSelfCalls(Expr body, string selfName, int paramCount)
    {
        return body switch
        {
            Expr.If iff => HasTailSelfCalls(iff.Then, selfName, paramCount) || HasTailSelfCalls(iff.Else, selfName, paramCount),
            Expr.Match m => m.Cases.Any(c => HasTailSelfCalls(c.Body, selfName, paramCount)),
            Expr.Let l => HasTailSelfCalls(l.Body, selfName, paramCount),
            Expr.LetResult l => HasTailSelfCalls(l.Body, selfName, paramCount),
            Expr.LetRec l => HasTailSelfCalls(l.Body, selfName, paramCount),
            Expr.Call c => IsSelfCallChain(c, selfName, paramCount),
            _ => false
        };
    }

    /// <summary>Check if a call expression is a full self-call chain: f(a1)(a2)...(aN)</summary>
    private static bool IsSelfCallChain(Expr.Call call, string selfName, int expectedArgs)
    {
        var args = new List<Expr>();
        var root = CollectCallArgs(call, args);
        return root is Expr.Var v && string.Equals(v.Name, selfName, StringComparison.Ordinal) && args.Count == expectedArgs;
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
        var reuseTokensAtIf = new List<(int, int)>(_reuseTokens);

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

    private (int, TypeRef) LowerLambdaCore(Expr.Lambda lam, string? selfName, TypeRef? selfType, bool stackAllocateClosure, IReadOnlyList<string>? selfAliases = null, RecGroupContext? recGroup = null, string? forcedLabel = null)
    {
        _usesClosures = true;

        // Create type variables for param and return
        var paramTy = NewTypeVar();
        var retTy = NewTypeVar();
        var funTy = new TypeRef.TFun(paramTy, retTy);

        // Monomorphize a reuse specialization: bind this curried parameter to the concrete type from
        // the routed call, so the body (and the heap-field key materialization) sees concrete types.
        if (_specializationConcreteParamTypes is { } concreteParamTypes
            && _specializationParamCursor < concreteParamTypes.Count)
        {
            Unify(paramTy, concreteParamTypes[_specializationParamCursor]);
            _specializationParamCursor++;
        }

        // Compute free variables for capture
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
        var captures = recGroup is not null
            ? recGroup.SharedCaptures
            : free.Where(n => Lookup(n) is Binding.Local or Binding.Env or Binding.EnvScheme or Binding.Self or Binding.Scheme).Distinct().ToList();

        // At lambda creation site: allocate env if needed
        int envPtrTemp;
        if (recGroup is not null)
        {
            // The group's shared env was already allocated and filled once at the group site.
            envPtrTemp = recGroup.SharedEnvPtrTemp;
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

        // Create lambda function label
        string label = forcedLabel ?? $"lambda_{_nextLambdaId++}";

        // Build function body IR in isolation
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

        var savedLocalNames = new Dictionary<int, string>(_localNames);
        var savedLocalTypes = new Dictionary<int, TypeRef>(_localTypes);
        // In-place reuse state is per-frame: a nested lambda must not see this frame's reuse
        // tokens (frame-local temps) or linear accumulators, and vice versa.
        var savedLinearReuseNames = new HashSet<string>(_linearReuseNames, StringComparer.Ordinal);
        var savedReuseTokens = new List<(int, int)>(_reuseTokens);
        var savedSpecAccumulators = new HashSet<string>(_linearSpecializationAccumulators, StringComparer.Ordinal);
        var savedResetSafe = new HashSet<string>(_resetSafeAccumulators, StringComparer.Ordinal);
        var savedReuseResultTemps = new HashSet<int>(_reuseResultTemps);
        _linearReuseNames.Clear();
        _reuseTokens.Clear();
        _linearSpecializationAccumulators.Clear();
        _resetSafeAccumulators.Clear();
        _reuseResultTemps.Clear();

        // new function state
        _inst.Clear();
        _nextTempSlot = 0;
        _nextLocalSlot = 0;
        _localNames.Clear();
        _localTypes.Clear();

        // Lambda function gets implicit locals for env and arg at slots 0 and 1
        int envSlot = NewLocal(); // 0
        int argSlot = NewLocal(); // 1
        RecordLocalDebugInfo(argSlot, lam.ParamName, paramTy);

        // Bind param name as local slot
        var scope = new Dictionary<string, Binding>(StringComparer.Ordinal);
        if (_hasAshesIO)
        {
            AddStdIOBindings(scope);
        }
        var paramSpan = AstSpans.GetLambdaParameterOrDefault(lam);
        RecordHoverType(paramSpan, lam.ParamName, paramTy);
        scope[lam.ParamName] = new Binding.Local(argSlot, paramTy, paramSpan);
        // Reuse specialization: treat this parameter as a linear reuse root so a match-then-rebuild
        // on it overwrites the node in place. Consume the request so nested lambdas don't inherit it.
        if (_specializingLinearParam == lam.ParamName)
        {
            _linearReuseNames.Add(lam.ParamName);
            _specializingReuseLabel = label;
            _specializingLinearParam = null;
        }

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
        if (recGroup is not null)
        {
            // Bind every group member (this one and its siblings) so each resolves to its own IR
            // function. Reconstructing a sibling's closure uses this member's env (LoadLocal 0), which
            // is correct precisely because the whole group shares one identical environment layout.
            foreach (var member in recGroup.Members)
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

        // Reuse specialization: a stdlib helper this body references (Ashes_Map_makeNode, ...) is a
        // top-level function not present in the generation-site scope (we are deep inside a loop body).
        // Bind each such free reference to its top-level Binding.Self — a direct global reference that
        // needs no env capture (the helper is inlined at its call sites, or called by label). Added to
        // the scope only, never to `captures`, so the closure construction does not try to fill it.
        if (_inSpecialization && _topLevelScopeStack.Length > 0)
        {
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

        _scopes.Clear();
        _scopes.Push(scope);

        // In function prologue, backend will store RDI(env) to envSlot and RSI(arg) to argSlot.
        // Our LoadEnv instruction implicitly uses envSlot; backend knows envSlot is 0.
        // We'll enforce envSlot==0.
        if (envSlot != 0)
        {
            throw new InvalidOperationException("envSlot must be 0");
        }

        // TCO: For the innermost lambda in a recursive chain, create local copies
        // of captured params and emit a loop start label so tail self-calls can jump back.
        // A lambda only belongs to the recursive chain while we are still descending the binding's
        // curried lambda chain. A nested let-bound lambda inside the body (e.g.
        // `let rec f n = let helper x = x + n in ...`) is a separate frame: if treated as the
        // innermost chain lambda it would emit the loop label into its own frame while the outer
        // self-call jumps to a label that frame never contains (KeyNotFoundException in codegen).
        bool wasDescendingChain = _tcoCtx?.DescendingChain ?? false;
        bool isChainLambda = wasDescendingChain;
        var isInnermostTco = isChainLambda && lam.Body is not Expr.Lambda;
        // In-place reuse: accumulators to deep-copy once at loop entry. The copy IR is
        // emitted only after the body is lowered (so HM has resolved the accumulator's type), then
        // spliced in at reuseInsertIndex (before the loop body label). See below.
        var reuseDefensiveCopy = new List<(int Slot, TypeRef TypeRef)>();
        // Slots whose defensive copy is for DIRECT in-place reuse (the body itself rebuilds the
        // accumulator). These are only needed if reuse actually fired (an AllocReusing was emitted);
        // a pure traversal that matches but never rebuilds (e.g. a tree lookup) must NOT copy, or every
        // call becomes O(size) instead of O(depth). Specialization copies (the reuse is in a $reuse
        // clone) are not gated this way.
        var directReuseSlots = new HashSet<int>();
        // Spec-path reuse accumulators (by name) whose entry deep-copy was elided this frame — i.e. the
        // accumulator is threaded into its reuse specialization without a relocating copy, so its address
        // is preserved. Used after the body is lowered to decide whether this fold is address-stable.
        var specElidedAccs = new HashSet<string>(StringComparer.Ordinal);
        int reuseInsertIndex = -1;
        if (isInnermostTco)
        {
            var tco = _tcoCtx!;
            tco.ParamSlots.Clear();

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

            // In-place reuse: mark accumulators that are deconstructed in the loop body as
            // linear (so the body's match→construct lowering reuses their nodes in place) and record
            // them for a one-time deep copy at loop entry. The copy makes the loop-local accumulator
            // region uniquely owned regardless of whether the caller still holds the initial value —
            // which is what makes the per-iteration in-place reuse sound (no runtime refcounting;
            // Ground Rule 6). The copy IR is generated after the body (resolved types) and spliced in
            // here. Type comes from the matched constructor — the param's own type var isn't unified
            // until the body is lowered.
            _linearReuseNames.Clear();
            var reuseScan = new Dictionary<string, string>(StringComparer.Ordinal);
            var reuseParamNames = new HashSet<string>(tco.ParamNames, StringComparer.Ordinal) { lam.ParamName };
            CollectCtorMatchedScrutinees(lam.Body, reuseParamNames, reuseScan);
            reuseInsertIndex = _inst.Count;
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
                        && IsReuseAccumulatorMoveSafe(tco.SelfName, accName);
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

            // Indirect reuse: an accumulator passed to a specializable recursive function f(acc) is
            // also deep-copied once here (so f$reuse can rewrite it in place) and tracked so the call
            // is routed to f$reuse. Eligibility from f's parameter type (a non-resource recursive ADT).
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
                        && IsReuseAccumulatorMoveSafe(tco.SelfName, accName);
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

            tco.InTailPosition = true;
        }

        // Decide what TCO context the body sees:
        //  - a chain link whose body is the next curried lambda keeps descending,
        //  - the chain's innermost lambda stops descending so nested lambdas in the body don't
        //    re-trigger TCO,
        //  - a non-chain nested lambda suspends the outer TCO entirely (it is a separate frame, and
        //    tail-call back-edges can't cross frames).
        var outerTcoCtx = _tcoCtx;
        if (isChainLambda)
        {
            _tcoCtx!.DescendingChain = lam.Body is Expr.Lambda;
        }
        else if (_tcoCtx is not null)
        {
            _tcoCtx = null;
        }

        var savedTcoCtx = isInnermostTco ? outerTcoCtx : null;
        bool paramShadowsInlinable = PushInlinableShadow(lam.ParamName);
        var (bodyTemp, bodyType) = LowerExpr(lam.Body);
        if (paramShadowsInlinable) PopInlinableShadow(lam.ParamName);
        if (isInnermostTco && savedTcoCtx is not null)
        {
            savedTcoCtx.InTailPosition = false;
        }

        // In-place reuse: now that the body is lowered and the accumulators' types are
        // resolved, generate the one-time defensive deep copies and splice them in at loop entry
        // (before the body label, recorded as reuseInsertIndex). Generated at the end of _inst, then
        // moved up — the block is self-contained (loads the slot, deep-copies, stores it back).
        // Run when there is any copy to emit, or any direct-reuse slot whose copy was elided by the
        // move analysis (directReuseSlots without a matching reuseDefensiveCopy entry) — the latter
        // still needs the non-structural-reuse revert below to protect a move-safe pure reader.
        if ((reuseDefensiveCopy.Count > 0 || directReuseSlots.Count > 0) && reuseInsertIndex >= 0)
        {
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

        _tcoCtx = outerTcoCtx;
        if (isChainLambda)
        {
            _tcoCtx!.DescendingChain = wasDescendingChain;
        }

        Unify(bodyType, retTy);
        Emit(new IrInst.Return(bodyTemp));

        var func = new IrFunction(
            Label: label,
            Instructions: new List<IrInst>(_inst),
            LocalCount: _nextLocalSlot,
            TempCount: _nextTempSlot,
            HasEnvAndArgParams: true,
            LocalNames: new Dictionary<int, string>(_localNames),
            LocalTypes: SnapshotLocalTypes()
        );

        // restore state
        _funcs.Add(func);

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

        _lambdaDepth--;

        _linearReuseNames.Clear();
        foreach (var n in savedLinearReuseNames) _linearReuseNames.Add(n);
        _linearSpecializationAccumulators.Clear();
        foreach (var n in savedSpecAccumulators) _linearSpecializationAccumulators.Add(n);
        _resetSafeAccumulators.Clear();
        foreach (var n in savedResetSafe) _resetSafeAccumulators.Add(n);
        _reuseResultTemps.Clear();
        foreach (var t in savedReuseResultTemps) _reuseResultTemps.Add(t);
        _reuseTokens.Clear();
        _reuseTokens.AddRange(savedReuseTokens);

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
            if (owned is not null && owned.IsResource && owned.Type is not null)
            {
                resourceCaptures.Add((ci * 8, ResolveOwnershipAlias(captures[ci]), owned.Type));
            }
        }

        if (resourceCaptures.Count > 0)
        {
            _closureResourceCaptures[closureTemp] = resourceCaptures;
        }

        return (closureTemp, funTy);
    }

    // Walk a left-recursive call chain and collect args in application order.
    // Handles multi-argument constructor calls desugared by the parser:
    //   Pair(1, 2) → Call(Call(Var("Pair"), 1), 2) → root=Var("Pair"), args=[1, 2]
    private static Expr CollectCallArgs(Expr expr, List<Expr> args)
    {
        if (expr is Expr.Call c)
        {
            var root = CollectCallArgs(c.Func, args);
            args.Add(c.Arg);
            return root;
        }

        return expr;
    }

    // Whether <paramref name="expr"/> evaluates to a loop accumulator threaded in place at a stable
    // address — one below the loop watermark that a plain arena reset (reclaiming everything above the
    // watermark) keeps live. <paramref name="isAcc"/> decides whether a bare Var is that accumulator:
    // at a back edge it is a live-scope slot check; inside a fold's stability recording it is name
    // equality against the (unshadowed) accumulator param. <paramref name="selfSpan"/>/<paramref
    // name="selfParamCount"/>, when set during recording, let a self back-edge count as a stable fold
    // before it is itself recorded. Conservative: an unrecognized shape (including a let-bound name) is
    // not stable, which only loses the reset (sound), never keeps an unsafe one.
    private bool IsStableAccumulatorExpr(Expr expr, Func<string, bool> isAcc, TextSpan? selfSpan = null, int selfParamCount = 0)
    {
        switch (expr)
        {
            case Expr.Var v:
                return isAcc(v.Name);
            case Expr.Call:
                {
                    var args = new List<Expr>();
                    var root = CollectCallArgs(expr, args);
                    if (args.Count == 0)
                    {
                        return false;
                    }

                    // A reuse specialization rewrites its last argument's tree in place and returns it —
                    // stable exactly when that argument is.
                    if (_inPlaceReuseCallExprs.Contains(expr))
                    {
                        return IsStableAccumulatorExpr(args[^1], isAcc, selfSpan, selfParamCount);
                    }

                    if (root is not Expr.Var f || Lookup(f.Name)?.DefinitionSpan is not { } span)
                    {
                        return false;
                    }

                    // A call to a fold proven address-stable (or a self back-edge during recording) threads
                    // its accumulator (last arg) through at a stable address.
                    bool isSelf = selfSpan is { } s && span.Equals(s) && args.Count == selfParamCount;
                    bool isStableFold = _accStableFolds.TryGetValue(span, out var pc) && pc == args.Count;
                    if (isSelf || isStableFold)
                    {
                        return IsStableAccumulatorExpr(args[^1], isAcc, selfSpan, selfParamCount);
                    }

                    return false;
                }
            default:
                return false;
        }
    }

    // Whether every tail leaf of a fold body preserves the accumulator's address, so calling the fold
    // returns it at a stable address. Walks the tail spine (If arms, Match case bodies, Let bodies),
    // tracking binders that shadow the accumulator name; each leaf must satisfy IsStableAccumulatorExpr
    // under the shadow set (a rebound accumulator name is no longer the accumulator). Used only when
    // recording a fold's stability, keyed by name — the caller side uses live-scope slots instead.
    // The params that are passed as their own unchanged Var at every tail self-call, so they are
    // loop-invariant (they only ever hold the value passed into the loop, allocated below the arena
    // watermark) and survive a plain per-iteration reset. Computed from the raw AST before lowering, so
    // self-calls are matched by name with shadow tracking (a rebound param name is no longer the param).
    private static HashSet<string> CollectLoopInvariantParams(Expr body, IReadOnlyList<string> paramNames, string selfName)
    {
        var candidates = new HashSet<string>(paramNames, StringComparer.Ordinal);
        bool sawSelfCall = false;

        void Walk(Expr e, HashSet<string> shadowed)
        {
            switch (e)
            {
                case Expr.If iff:
                    Walk(iff.Then, shadowed);
                    Walk(iff.Else, shadowed);
                    break;
                case Expr.Match m:
                    foreach (var c in m.Cases)
                    {
                        var caseShadow = shadowed;
                        var binders = new HashSet<string>(StringComparer.Ordinal);
                        CollectPatternBinders(c.Pattern, binders);
                        if (binders.Count > 0)
                        {
                            caseShadow = new HashSet<string>(shadowed, StringComparer.Ordinal);
                            caseShadow.UnionWith(binders);
                        }

                        Walk(c.Body, caseShadow);
                    }

                    break;
                case Expr.Let let:
                    var bodyShadow = shadowed.Contains(let.Name)
                        ? shadowed
                        : new HashSet<string>(shadowed, StringComparer.Ordinal) { let.Name };
                    Walk(let.Body, bodyShadow);
                    break;
                case Expr.Call:
                    var args = new List<Expr>();
                    var root = CollectCallArgs(e, args);
                    if (root is Expr.Var f
                        && string.Equals(f.Name, selfName, StringComparison.Ordinal)
                        && !shadowed.Contains(selfName)
                        && args.Count == paramNames.Count)
                    {
                        sawSelfCall = true;
                        for (int i = 0; i < paramNames.Count; i++)
                        {
                            bool unchanged = args[i] is Expr.Var av
                                && string.Equals(av.Name, paramNames[i], StringComparison.Ordinal)
                                && !shadowed.Contains(paramNames[i]);
                            if (!unchanged)
                            {
                                candidates.Remove(paramNames[i]);
                            }
                        }
                    }

                    break;
            }
        }

        Walk(body, new HashSet<string>(StringComparer.Ordinal));
        return sawSelfCall ? candidates : new HashSet<string>(StringComparer.Ordinal);
    }

    private bool TailLeavesStable(Expr body, string accName, TextSpan selfSpan, int selfParamCount, HashSet<string> shadowed)
    {
        switch (body)
        {
            case Expr.If iff:
                return TailLeavesStable(iff.Then, accName, selfSpan, selfParamCount, shadowed)
                    && TailLeavesStable(iff.Else, accName, selfSpan, selfParamCount, shadowed);
            case Expr.Match m:
                foreach (var c in m.Cases)
                {
                    var caseShadow = shadowed;
                    var binders = new HashSet<string>(StringComparer.Ordinal);
                    CollectPatternBinders(c.Pattern, binders);
                    if (binders.Count > 0)
                    {
                        caseShadow = new HashSet<string>(shadowed, StringComparer.Ordinal);
                        caseShadow.UnionWith(binders);
                    }

                    if (!TailLeavesStable(c.Body, accName, selfSpan, selfParamCount, caseShadow))
                    {
                        return false;
                    }
                }

                return true;
            case Expr.Let let:
                {
                    var bodyShadow = shadowed;
                    if (!shadowed.Contains(let.Name))
                    {
                        bodyShadow = new HashSet<string>(shadowed, StringComparer.Ordinal) { let.Name };
                    }

                    return TailLeavesStable(let.Body, accName, selfSpan, selfParamCount, bodyShadow);
                }
            default:
                return IsStableAccumulatorExpr(
                    body,
                    name => !shadowed.Contains(name) && string.Equals(name, accName, StringComparison.Ordinal),
                    selfSpan,
                    selfParamCount);
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
        }
    }

    private bool TryGetExactFunctionArity(TypeRef type, out int arity)
    {
        arity = 0;
        var current = Prune(type);

        while (current is TypeRef.TFun fun)
        {
            arity++;
            current = Prune(fun.Ret);
        }

        if (current is TypeRef.TVar resultVar)
        {
            // A '+'-constrained var is Int/Float/Str (a scalar, never a function), so the arity is
            // exact even though it is not yet a concrete type. This keeps oversaturated-call
            // detection working for functions like `add a b = a + b`.
            if (ConstrainedAddVarRepIds().Contains(resultVar.Id))
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
        if (rootExpr is Expr.Var varCtor && _constructorSymbols.TryGetValue(varCtor.Name, out var ctorSym))
        {
            return LowerConstructorApplication(ctorSym, collectedArgs);
        }

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
        // f may be a plain Var or a qualified stdlib reference (Ashes.Map.set → Ashes_Map_set).
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

        // In-place reuse: inside a reuse arm (a dead-cell token is live), a saturated call to a
        // non-recursive top-level helper is inlined, so the helper's constructor becomes local to
        // this arm and can reuse the token (e.g. loop(...)(mk(l)(v+n)(r)) where mk rebuilds a node).
        // Only when the callee name resolves to that top-level function (not shadowed by a local).
        // Inline a saturated helper call when a reuse token is live, OR unconditionally inside a
        // specialization (so every helper folds down to constructors and never leaves a call to a
        // top-level function the specialization didn't capture).
        if (Environment.GetEnvironmentVariable("ASH_DBG_REUSE") is not null
            && rootExpr is Expr.Var dbgFn && _inlinableFunctions.TryGetValue(dbgFn.Name, out var dbgInl)
            && dbgFn.Name.Contains("Map", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[reuse] call {dbgFn.Name}: inSpec={_inSpecialization} tokens={_reuseTokens.Count} shadowed={_shadowedInlinables.ContainsKey(dbgFn.Name)} inProgress={_inliningInProgress.Contains(dbgFn.Name)} params={dbgInl.Params.Count} args={collectedArgs.Count}");
        }

        // The callee may be a plain Var (module code, where the stitcher already rewrote member
        // references to flat names) or a qualified stdlib reference from user code
        // (Ashes.Map.makeNode → Ashes_Map_makeNode) — the latter matters inside a specialization
        // generated FOR a user function, whose body keeps its QualifiedVar nodes but lowers in an
        // isolated scope where only inline/by-label resolution works.
        if ((_reuseTokens.Count > 0 || _inSpecialization)
            && ResolveSpecializableCalleeName(rootExpr) is { } inlineName
            && (rootExpr is not Expr.Var vRoot || !_shadowedInlinables.ContainsKey(vRoot.Name))
            && !_inliningInProgress.Contains(inlineName)
            && _inlinableFunctions.TryGetValue(inlineName, out var inlinable)
            && inlinable.Params.Count == collectedArgs.Count)
        {
            return InlineCall(inlineName, inlinable.Params, inlinable.Body, collectedArgs);
        }

        // TCO: detect tail-position self-call chain: f(a1)(a2)...(aN)
        if (_tcoCtx is { InTailPosition: true } tco
            && rootExpr is Expr.Var selfVar
            && string.Equals(selfVar.Name, tco.SelfName, StringComparison.Ordinal)
            && collectedArgs.Count == tco.ParamCount)
        {
            // Evaluate all new arg values first (into temps), BEFORE storing any
            var savedTail = tco.InTailPosition;
            tco.InTailPosition = false;

            var newArgTemps = new int[collectedArgs.Count];
            var newArgTypes = new TypeRef[collectedArgs.Count];
            // Type-check: resolve self binding and unify arg types with param types
            var selfBinding = Lookup(tco.SelfName);
            var curType = selfBinding is not null ? Prune(selfBinding.Type) : null;
            for (int i = 0; i < collectedArgs.Count; i++)
            {
                var (argTemp, argType) = LowerExpr(collectedArgs[i]);
                newArgTemps[i] = argTemp;
                newArgTypes[i] = argType;
                if (curType is TypeRef.TFun funType)
                {
                    Unify(funType.Arg, argType);
                    curType = Prune(funType.Ret);
                }
            }

            // Store new values into TCO param slots
            for (int i = 0; i < tco.ParamSlots.Count; i++)
            {
                Emit(new IrInst.StoreLocal(tco.ParamSlots[i], newArgTemps[i]));
            }

            // An owned value passed by name as a self-call argument moves to the next iteration —
            // it must not be dropped at this back-edge (a resource would be closed, a closure with a
            // dropper would close its captured resource). Mark it consumed so
            // EmitTcoBackEdgeResourceDrops (and the dead-code arm Drops after the jump) skip it.
            foreach (var arg in collectedArgs)
            {
                if (arg is Expr.Var argVar && LookupOwnedValue(argVar.Name) is { IsDropped: false } movedOwned)
                {
                    movedOwned.IsDropped = true;
                }
            }

            // Close iteration-local resources (open files/sockets/processes bound this iteration)
            // before the arena reset and back-edge jump. Without this the per-arm Drop is emitted
            // after the jump as dead code and the resource leaks every iteration.
            EmitTcoBackEdgeResourceDrops(tco);

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
                int tcoPreRestoreEndSlot = NewLocal();

                // An arg needs no copy-out at the reset if it's a copy type (inline), a resource
                // handle (a scalar fd/HANDLE — no heap reference, and a reset never Drops it), OR a
                // fully-reusing specialized accumulator — rewritten in place below the watermark, it
                // already survives a plain reset, which then reclaims the iteration's scaffolding.
                bool ArgResetSafe(int i) => CanArenaReset(newArgTypes[i])
                    || IsResourceHandleType(newArgTypes[i])
                    // A loop-invariant param (passed unchanged as its own Var at every tail self-call)
                    // holds the value passed into the loop — below the watermark — so it survives a plain
                    // reset even when it is a heap type (e.g. a Bytes threaded unchanged through a fold).
                    || (i < tco.ParamNames.Count
                        && tco.LoopInvariantParams.Contains(tco.ParamNames[i])
                        && collectedArgs[i] is Expr.Var invVar
                        && string.Equals(invVar.Name, tco.ParamNames[i], StringComparison.Ordinal))
                    || (i < tco.ParamNames.Count
                        && _resetSafeAccumulators.Contains(tco.ParamNames[i])
                        && IsStableAccumulatorExpr(
                            collectedArgs[i],
                            name => Lookup(name) is Binding.Local local && local.Slot == tco.ParamSlots[i]));

                if (Enumerable.Range(0, newArgTypes.Length).All(ArgResetSafe))
                {
                    // All copy types and/or in-place-reused accumulators: plain reset.
                    Emit(new IrInst.RestoreArenaState(tco.ArenaCursorSlot, tco.ArenaEndSlot, tcoPreRestoreEndSlot));
                    Emit(new IrInst.ReclaimArenaChunks(tco.ArenaEndSlot, tcoPreRestoreEndSlot));
                }
                else
                {
                    // Check whether every heap-type arg can be copy-outed.
                    bool allCopyable = true;
                    for (int i = 0; i < newArgTypes.Length; i++)
                    {
                        if (!CanArenaReset(newArgTypes[i])
                            && GetTcoCopyOutKind(newArgTypes[i], out _, out _) == CopyOutKind.None)
                        {
                            allCopyable = false;
                            break;
                        }
                    }

                    if (allCopyable)
                    {
                        // Two-pass copy-out. Carrying TWO+ freshly heap-allocated args
                        // across the back-edge cannot be done with a single round of
                        // copy-outs to the watermark W: each copy-out compacts its arg
                        // *down* to W, but a copy whose destination block [W, …) reaches
                        // high enough overwrites a later arg's still-unread source bytes
                        // (e.g. startsWith(textTail)(prefixTail): copying textTail to W
                        // clobbers prefixTail's source once the string exceeds ~11 bytes,
                        // corrupting the second arg → the rt_sigsuspend deadlock).
                        //
                        // Phase A (BEFORE the reset): copy every heap arg UP to a fresh
                        // alloc above the current cursor. Sources are all below the cursor,
                        // destinations above it → disjoint, overlap-free regardless of
                        // order. Phase B (AFTER the reset): copy each up-copy DOWN to W.
                        // The destination block [W, …) lies entirely below every up-copy
                        // source (which sits above the pre-reset cursor), so these copies
                        // are also disjoint and order-independent.
                        var upCopyTemps = new int[newArgTypes.Length];
                        for (int i = 0; i < newArgTypes.Length; i++)
                        {
                            if (CanArenaReset(newArgTypes[i]))
                            {
                                upCopyTemps[i] = -1;
                                continue;
                            }

                            var kind = GetTcoCopyOutKind(newArgTypes[i], out int sizeBytes, out var headCopy);
                            if (kind == CopyOutKind.None)
                            {
                                upCopyTemps[i] = -1;
                                continue;
                            }

                            upCopyTemps[i] = NewTemp();
                            EmitTcoCopyOut(kind, upCopyTemps[i], newArgTemps[i], sizeBytes, headCopy);
                        }

                        // Reset (pointer reset only, no chunk freeing): cursor → W.
                        Emit(new IrInst.RestoreArenaState(tco.ArenaCursorSlot, tco.ArenaEndSlot, tcoPreRestoreEndSlot));

                        // Phase B: copy each up-copy down to W and store into the slot.
                        for (int i = 0; i < newArgTypes.Length; i++)
                        {
                            if (upCopyTemps[i] < 0)
                                continue;

                            var kind = GetTcoCopyOutKind(newArgTypes[i], out int sizeBytes, out var headCopy);
                            int copyDest = NewTemp();
                            EmitTcoCopyOut(kind, copyDest, upCopyTemps[i], sizeBytes, headCopy);
                            Emit(new IrInst.StoreLocal(tco.ParamSlots[i], copyDest));
                        }

                        // Free the chunks abandoned above W (including the Phase A
                        // up-copies, now fully consumed by Phase B).
                        Emit(new IrInst.ReclaimArenaChunks(tco.ArenaEndSlot, tcoPreRestoreEndSlot));
                    }
                    // else: complex heap types — no arena reset.
                }
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

            tco.InTailPosition = savedTail;

            // Return a dummy value — this code path won't execute at runtime
            int dummy = NewTemp();
            Emit(new IrInst.LoadConstInt(dummy, 0));
            return (dummy, NewTypeVar());
        }

        if (rootExpr is Expr.Var varFunc && Lookup(varFunc.Name) is Binding.Intrinsic intrinsic)
        {
            int expectedArity = GetIntrinsicArity(intrinsic.Kind);
            if (collectedArgs.Count != expectedArity)
            {
                return ReportArityMismatch(rootExpr, expectedArity, collectedArgs.Count);
            }

            return intrinsic.Kind switch
            {
                IntrinsicKind.Print => LowerPrint(collectedArgs[0]),
                IntrinsicKind.Write => LowerWrite(collectedArgs[0], appendNewline: false),
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
                IntrinsicKind.FileWriteText => LowerFileWriteText(collectedArgs[0], collectedArgs[1]),
                IntrinsicKind.FileExists => LowerFileExists(collectedArgs[0]),
                IntrinsicKind.TextUncons => LowerTextUncons(collectedArgs[0]),
                IntrinsicKind.TextParseInt => LowerTextParseInt(collectedArgs[0]),
                IntrinsicKind.TextParseFloat => LowerTextParseFloat(collectedArgs[0]),
                IntrinsicKind.TextFromInt => LowerTextFromInt(collectedArgs[0]),
                IntrinsicKind.TextFromFloat => LowerTextFromFloat(collectedArgs[0]),
                IntrinsicKind.TextFormatFloat => LowerTextFormatFloat(collectedArgs[0], collectedArgs[1]),
                IntrinsicKind.TextToHex => LowerTextToHex(collectedArgs[0]),
                IntrinsicKind.HttpGet => LowerHttpGet(collectedArgs[0]),
                IntrinsicKind.HttpPost => LowerHttpPost(collectedArgs[0], collectedArgs[1]),
                IntrinsicKind.NetTcpConnect => LowerNetTcpConnect(collectedArgs[0], collectedArgs[1]),
                IntrinsicKind.NetTcpSend => LowerNetTcpSend(collectedArgs[0], collectedArgs[1]),
                IntrinsicKind.NetTcpReceive => LowerNetTcpReceive(collectedArgs[0], collectedArgs[1]),
                IntrinsicKind.NetTcpClose => LowerNetTcpClose(collectedArgs[0]),
                IntrinsicKind.NetTlsConnect => LowerNetTlsConnect(collectedArgs[0], collectedArgs[1]),
                IntrinsicKind.NetTlsSend => LowerNetTlsSend(collectedArgs[0], collectedArgs[1]),
                IntrinsicKind.NetTlsReceive => LowerNetTlsReceive(collectedArgs[0], collectedArgs[1]),
                IntrinsicKind.NetTlsClose => LowerNetTlsClose(collectedArgs[0]),
                IntrinsicKind.Panic => LowerPanic(collectedArgs[0]),
                IntrinsicKind.AsyncRun => LowerAsyncRun(collectedArgs[0]),
                IntrinsicKind.AsyncTask => LowerAsyncTask(collectedArgs[0]),
                IntrinsicKind.AsyncFromResult => LowerAsyncFromResult(collectedArgs[0]),
                IntrinsicKind.AsyncSleep => LowerAsyncSleep(collectedArgs[0]),
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
                IntrinsicKind.UIntToInt => LowerUIntToInt(collectedArgs[0]),
                IntrinsicKind.MathToFloat => LowerMathToFloat(collectedArgs[0]),
                IntrinsicKind.MathSqrt => LowerMathFloatUnary(collectedArgs[0], "Ashes.Math.sqrt", "llvm.sqrt.f64"),
                IntrinsicKind.MathFloor => LowerMathFloatUnary(collectedArgs[0], "Ashes.Math.floor", "llvm.floor.f64"),
                IntrinsicKind.MathCeil => LowerMathFloatUnary(collectedArgs[0], "Ashes.Math.ceil", "llvm.ceil.f64"),
                IntrinsicKind.MathRound => LowerMathFloatUnary(collectedArgs[0], "Ashes.Math.round", "llvm.round.f64"),
                IntrinsicKind.MathTrunc => LowerMathFloatUnary(collectedArgs[0], "Ashes.Math.trunc", "llvm.trunc.f64"),
                IntrinsicKind.MathFloorToInt => LowerMathFloatToInt(collectedArgs[0], "Ashes.Math.floorToInt", "llvm.floor.f64"),
                IntrinsicKind.MathRoundToInt => LowerMathFloatToInt(collectedArgs[0], "Ashes.Math.roundToInt", "llvm.round.f64"),
                IntrinsicKind.MathTruncToInt => LowerMathFloatToInt(collectedArgs[0], "Ashes.Math.truncToInt", null),
                IntrinsicKind k when LibmIntrinsics.ContainsKey(k) => LowerLibm(k, collectedArgs),
                IntrinsicKind.FileWriteBytes => LowerFileWriteBytes(collectedArgs[0], collectedArgs[1]),
                IntrinsicKind.ReadExact => LowerReadExact(collectedArgs[0]),
                IntrinsicKind.TextByteLength => LowerTextByteLength(collectedArgs[0]),
                IntrinsicKind.SpawnProcess => LowerSpawnProcess(collectedArgs[0], collectedArgs[1]),
                IntrinsicKind.ProcessWriteStdin => LowerProcessWriteStdin(collectedArgs[0], collectedArgs[1]),
                IntrinsicKind.ProcessReadStdoutLine => LowerProcessReadStdoutLine(collectedArgs[0]),
                IntrinsicKind.ProcessReadStderrLine => LowerProcessReadStderrLine(collectedArgs[0]),
                IntrinsicKind.ProcessWaitForExit => LowerProcessWaitForExit(collectedArgs[0]),
                IntrinsicKind.ProcessKill => LowerProcessKill(collectedArgs[0]),
                _ => throw new NotSupportedException($"Unknown intrinsic: {intrinsic.Kind}")
            };
        }

        if (rootExpr is Expr.Var externVar && Lookup(externVar.Name) is Binding.ExternFunction externFunction)
        {
            return LowerExternCall(rootExpr, externFunction.Function, collectedArgs);
        }

        // Qualified intrinsic call: Ashes.IO.print(...), Ashes.IO.panic(...)
        if (rootExpr is Expr.QualifiedVar qv)
        {
            var resolvedModule = ResolveModuleAlias(qv.Module);
            if (BuiltinRegistry.TryGetModule(resolvedModule, out var builtinModule)
                && builtinModule.Members.TryGetValue(qv.Name, out var builtinMember))
            {
                if (!builtinMember.IsCallable)
                {
                    ReportDiagnostic(GetSpan(qv), $"'{resolvedModule}.{qv.Name}' is not callable.");
                    return ReturnNeverWithDummyTemp();
                }

                if (collectedArgs.Count != builtinMember.Arity)
                {
                    return ReportArityMismatch(rootExpr, builtinMember.Arity, collectedArgs.Count);
                }

                return builtinMember.Kind switch
                {
                    BuiltinRegistry.BuiltinValueKind.Print => LowerPrint(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.Panic => LowerPanic(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.Write => LowerWrite(collectedArgs[0], appendNewline: false),
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
                    BuiltinRegistry.BuiltinValueKind.FileWriteText => LowerFileWriteText(collectedArgs[0], collectedArgs[1]),
                    BuiltinRegistry.BuiltinValueKind.FileExists => LowerFileExists(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.TextUncons => LowerTextUncons(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.TextParseInt => LowerTextParseInt(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.TextParseFloat => LowerTextParseFloat(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.TextFromInt => LowerTextFromInt(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.TextFromFloat => LowerTextFromFloat(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.TextFormatFloat => LowerTextFormatFloat(collectedArgs[0], collectedArgs[1]),
                    BuiltinRegistry.BuiltinValueKind.TextToHex => LowerTextToHex(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.HttpGet => LowerHttpGet(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.HttpPost => LowerHttpPost(collectedArgs[0], collectedArgs[1]),
                    BuiltinRegistry.BuiltinValueKind.NetTcpConnect => LowerNetTcpConnect(collectedArgs[0], collectedArgs[1]),
                    BuiltinRegistry.BuiltinValueKind.NetTcpSend => LowerNetTcpSend(collectedArgs[0], collectedArgs[1]),
                    BuiltinRegistry.BuiltinValueKind.NetTcpReceive => LowerNetTcpReceive(collectedArgs[0], collectedArgs[1]),
                    BuiltinRegistry.BuiltinValueKind.NetTcpClose => LowerNetTcpClose(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.NetTlsConnect => LowerNetTlsConnect(collectedArgs[0], collectedArgs[1]),
                    BuiltinRegistry.BuiltinValueKind.NetTlsSend => LowerNetTlsSend(collectedArgs[0], collectedArgs[1]),
                    BuiltinRegistry.BuiltinValueKind.NetTlsReceive => LowerNetTlsReceive(collectedArgs[0], collectedArgs[1]),
                    BuiltinRegistry.BuiltinValueKind.NetTlsClose => LowerNetTlsClose(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.AsyncRun => LowerAsyncRun(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.AsyncTask => LowerAsyncTask(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.AsyncFromResult => LowerAsyncFromResult(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.AsyncSleep => LowerAsyncSleep(collectedArgs[0]),
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
                    BuiltinRegistry.BuiltinValueKind.UIntToInt => LowerUIntToInt(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.MathToFloat => LowerMathToFloat(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.MathSqrt => LowerMathFloatUnary(collectedArgs[0], "Ashes.Math.sqrt", "llvm.sqrt.f64"),
                    BuiltinRegistry.BuiltinValueKind.MathFloor => LowerMathFloatUnary(collectedArgs[0], "Ashes.Math.floor", "llvm.floor.f64"),
                    BuiltinRegistry.BuiltinValueKind.MathCeil => LowerMathFloatUnary(collectedArgs[0], "Ashes.Math.ceil", "llvm.ceil.f64"),
                    BuiltinRegistry.BuiltinValueKind.MathRound => LowerMathFloatUnary(collectedArgs[0], "Ashes.Math.round", "llvm.round.f64"),
                    BuiltinRegistry.BuiltinValueKind.MathTrunc => LowerMathFloatUnary(collectedArgs[0], "Ashes.Math.trunc", "llvm.trunc.f64"),
                    BuiltinRegistry.BuiltinValueKind.MathFloorToInt => LowerMathFloatToInt(collectedArgs[0], "Ashes.Math.floorToInt", "llvm.floor.f64"),
                    BuiltinRegistry.BuiltinValueKind.MathRoundToInt => LowerMathFloatToInt(collectedArgs[0], "Ashes.Math.roundToInt", "llvm.round.f64"),
                    BuiltinRegistry.BuiltinValueKind.MathTruncToInt => LowerMathFloatToInt(collectedArgs[0], "Ashes.Math.truncToInt", null),
                    BuiltinRegistry.BuiltinValueKind k when LibmBuiltinKinds.TryGetValue(k, out var libmKind) => LowerLibm(libmKind, collectedArgs),
                    BuiltinRegistry.BuiltinValueKind.FileWriteBytes => LowerFileWriteBytes(collectedArgs[0], collectedArgs[1]),
                    BuiltinRegistry.BuiltinValueKind.IoReadExact => LowerReadExact(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.TextByteLength => LowerTextByteLength(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.SpawnProcess => LowerSpawnProcess(collectedArgs[0], collectedArgs[1]),
                    BuiltinRegistry.BuiltinValueKind.ProcessWriteStdin => LowerProcessWriteStdin(collectedArgs[0], collectedArgs[1]),
                    BuiltinRegistry.BuiltinValueKind.ProcessReadStdoutLine => LowerProcessReadStdoutLine(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.ProcessReadStderrLine => LowerProcessReadStderrLine(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.ProcessWaitForExit => LowerProcessWaitForExit(collectedArgs[0]),
                    BuiltinRegistry.BuiltinValueKind.ProcessKill => LowerProcessKill(collectedArgs[0]),
                    _ => StdMemberNotFound(resolvedModule, qv.Name)
                };
            }
        }

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
                // so that the occurs check can fire if the argument is the same variable.
                Unify(currentType, new TypeRef.TFun(NewTypeVar(), NewTypeVar()));
                currentType = Prune(currentType);
            }

            if (currentType is not TypeRef.TFun fun)
            {
                return ReportNonFunctionCall(rootExpr, currentType, i + 1);
            }

            var calleeName = TryGetCalleeDisplayName(rootExpr);
            var callContext = calleeName is not null
                ? $"in argument #{i + 1} of call to '{calleeName}'"
                : $"in argument #{i + 1} of function call";
            using (PushDiagnosticContext(callContext))
            {
                Unify(fun.Arg, argType);
            }

            int target = NewTemp();
            Emit(new IrInst.CallClosure(target, currentTemp, argTemp));
            currentTemp = target;
            currentType = Prune(fun.Ret);
        }

        // Restore arena after the call chain completes.
        // - Copy-type result (Int, Float, Bool): all allocations from the call
        //   chain are unreachable → reclaim via RestoreArenaState + ReclaimArenaChunks.
        // - Self-contained heap result (String, List with safe element, Closure,
        //   ADT with copy-type fields): restore pointer → copy-out → reclaim chunks
        //   (source stays readable until ReclaimArenaChunks frees the old OS chunks).
        var callResultType = Prune(currentType);
        int callPreRestoreEndSlot = NewLocal();
        if (CanArenaReset(callResultType))
        {
            Emit(new IrInst.RestoreArenaState(callWmCursorSlot, callWmEndSlot, callPreRestoreEndSlot));
            Emit(new IrInst.ReclaimArenaChunks(callWmEndSlot, callPreRestoreEndSlot));
        }
        else
        {
            var callCopyOutKind = GetCopyOutKind(callResultType, out int callCopySize);
            if (callCopyOutKind != CopyOutKind.None)
            {
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
                currentTemp = copyDest;
            }
        }

        return (currentTemp, currentType);
    }

    private (int, TypeRef) LowerExternCall(Expr rootExpr, IrExternFunction externFunction, List<Expr> args)
    {
        if (args.Count != externFunction.ParameterTypes.Count)
        {
            return ReportArityMismatch(rootExpr, externFunction.ParameterTypes.Count, args.Count);
        }

        var loweredArgTemps = new List<int>(args.Count);
        for (int i = 0; i < args.Count; i++)
        {
            var (argTemp, argType) = LowerExpr(args[i]);
            var expectedType = FromFfiType(externFunction.ParameterTypes[i]);
            using (PushDiagnosticContext($"in argument #{i + 1} of extern call to '{externFunction.Name}'"))
            {
                Unify(expectedType, argType);
            }

            if (externFunction.ParameterTypes[i] is FfiType.Str)
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
        Emit(new IrInst.CallExtern(target, externFunction.SymbolName, externFunction.LibraryName, loweredArgTemps, externFunction.ParameterTypes, externFunction.ReturnType));
        if (externFunction.ReturnType is FfiType.Void)
        {
            return LowerUnitValue();
        }

        return (target, FromFfiType(externFunction.ReturnType));
    }

    /// <summary>
    /// Synthesizes wrapper <see cref="IrFunction"/>s so that an extern function can be used as
    /// a first-class closure value. For an extern with N parameters, N curried wrapper functions
    /// are generated: the outermost accumulates one argument per call and the innermost ultimately
    /// issues the <see cref="IrInst.CallExtern"/> instruction with all collected arguments.
    ///
    /// For a 0-parameter extern a meaningful compile error is emitted because a nullary function
    /// cannot be represented as a closure that takes an argument.
    /// </summary>
    private (int, TypeRef) EmitExternFunctionThunk(IrExternFunction externFunc, TypeRef closureType, TextSpan referenceSpan)
    {
        int n = externFunc.ParameterTypes.Count;
        if (n == 0)
        {
            int errTemp = NewTemp();
            ReportDiagnostic(referenceSpan, $"Extern function '{externFunc.Name}' has no parameters and cannot be used as a first-class function value.");
            Emit(new IrInst.LoadConstInt(errTemp, 0));
            return (errTemp, closureType);
        }

        _usesClosures = true;

        // Assign a stable id to this thunk family so labels never collide.
        int lambdaId = _nextLambdaId++;
        var layerLabels = new string[n];
        for (int i = 0; i < n; i++)
        {
            layerLabels[i] = $"extern_{externFunc.Name}_thunk_{i}_{lambdaId}";
        }

        // Save outer compilation state so we can build sub-functions in isolation.
        var savedInst = new List<IrInst>(_inst);
        var savedTemp = _nextTempSlot;
        var savedLocal = _nextLocalSlot;
        var savedScopes = _scopes.ToArray();
        var savedLocalNames = new Dictionary<int, string>(_localNames);
        var savedLocalTypes = new Dictionary<int, TypeRef>(_localTypes);

        // Build from innermost layer (n-1) outward to layer 0 so each layer can reference the
        // label of the next-inner layer.
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
                // Innermost: load all previously captured args from env then call the extern.
                var callArgTemps = new List<int>(n);

                for (int j = 0; j < layer; j++)
                {
                    int envArgTemp = NewTemp();
                    Emit(new IrInst.LoadEnv(envArgTemp, j));
                    if (externFunc.ParameterTypes[j] is FfiType.Str)
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
                if (externFunc.ParameterTypes[layer] is FfiType.Str)
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
                Emit(new IrInst.CallExtern(callResultTemp, externFunc.SymbolName, externFunc.LibraryName, callArgTemps, externFunc.ParameterTypes, externFunc.ReturnType));

                int retTemp;
                if (externFunc.ReturnType is FfiType.Void)
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
            else
            {
                // Outer layer: pack current arg together with args captured from the outer env,
                // then return a closure pointing at the next inner layer.
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

    private static TypeRef FromFfiType(FfiType ffiType)
    {
        return ffiType switch
        {
            FfiType.Int => new TypeRef.TInt(),
            FfiType.UInt => new TypeRef.TInt(),
            FfiType.Float => new TypeRef.TFloat(),
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
        MarkResourceArgMoved(cons.Head);
        MarkResourceArgMoved(cons.Tail);

        if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;

        return LowerConsCell(headTemp, tailTemp, headType, tailType);
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

        void Visit(Expr ex, HashSet<string> bnd)
        {
            switch (ex)
            {
                case Expr.IntLit:
                case Expr.UIntLit:
                case Expr.FloatLit:
                case Expr.StrLit:
                case Expr.BoolLit:
                    return;
                case Expr.Var v:
                    if (!bnd.Contains(v.Name))
                    {
                        res.Add(v.Name);
                    }

                    return;
                case Expr.QualifiedVar qv:
                    {
                        var resolvedModule = ResolveModuleAlias(qv.Module);

                        // An intrinsic member (Ashes.IO.print, Ashes.Text.uncons, ...) lowers directly
                        // to a builtin and introduces no free variable. A SHIPPED-helper or user-module
                        // member (Ashes.String.indexOf, Ashes.Map.get, ...) lowers to a stitched
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
                        }

                        return;
                    }
                case Expr.Add a:
                    Visit(a.Left, bnd);
                    Visit(a.Right, bnd);
                    return;
                case Expr.Subtract sub:
                    Visit(sub.Left, bnd);
                    Visit(sub.Right, bnd);
                    return;
                case Expr.Multiply mul:
                    Visit(mul.Left, bnd);
                    Visit(mul.Right, bnd);
                    return;
                case Expr.Divide div:
                    Visit(div.Left, bnd);
                    Visit(div.Right, bnd);
                    return;
                case Expr.BitwiseAnd bitAnd:
                    Visit(bitAnd.Left, bnd);
                    Visit(bitAnd.Right, bnd);
                    return;
                case Expr.BitwiseOr bitOr:
                    Visit(bitOr.Left, bnd);
                    Visit(bitOr.Right, bnd);
                    return;
                case Expr.BitwiseXor bitXor:
                    Visit(bitXor.Left, bnd);
                    Visit(bitXor.Right, bnd);
                    return;
                case Expr.ShiftLeft shiftLeft:
                    Visit(shiftLeft.Left, bnd);
                    Visit(shiftLeft.Right, bnd);
                    return;
                case Expr.ShiftRight shiftRight:
                    Visit(shiftRight.Left, bnd);
                    Visit(shiftRight.Right, bnd);
                    return;
                case Expr.BitwiseNot bitwiseNot:
                    Visit(bitwiseNot.Operand, bnd);
                    return;
                case Expr.GreaterThan gt:
                    Visit(gt.Left, bnd);
                    Visit(gt.Right, bnd);
                    return;
                case Expr.GreaterOrEqual ge:
                    Visit(ge.Left, bnd);
                    Visit(ge.Right, bnd);
                    return;
                case Expr.LessThan lt:
                    Visit(lt.Left, bnd);
                    Visit(lt.Right, bnd);
                    return;
                case Expr.LessOrEqual le:
                    Visit(le.Left, bnd);
                    Visit(le.Right, bnd);
                    return;
                case Expr.Equal eq:
                    Visit(eq.Left, bnd);
                    Visit(eq.Right, bnd);
                    return;
                case Expr.NotEqual ne:
                    Visit(ne.Left, bnd);
                    Visit(ne.Right, bnd);
                    return;
                case Expr.ResultPipe pipe:
                    Visit(pipe.Left, bnd);
                    Visit(pipe.Right, bnd);
                    return;
                case Expr.ResultMapErrorPipe pipe:
                    Visit(pipe.Left, bnd);
                    Visit(pipe.Right, bnd);
                    return;
                case Expr.Call c:
                    Visit(c.Func, bnd);
                    Visit(c.Arg, bnd);
                    return;
                case Expr.TupleLit tuple:
                    foreach (var elem in tuple.Elements)
                    {
                        Visit(elem, bnd);
                    }
                    return;
                case Expr.ListLit list:
                    foreach (var e in list.Elements)
                    {
                        Visit(e, bnd);
                    }

                    return;
                case Expr.Cons c:
                    Visit(c.Head, bnd);
                    Visit(c.Tail, bnd);
                    return;
                case Expr.Match m:
                    Visit(m.Value, bnd);
                    foreach (var mc in m.Cases)
                    {
                        var bndCase = new HashSet<string>(bnd, StringComparer.Ordinal);
                        foreach (var name in PatternBindings(mc.Pattern))
                        {
                            bndCase.Add(name);
                        }

                        if (mc.Guard is not null)
                        {
                            Visit(mc.Guard, bndCase);
                        }
                        Visit(mc.Body, bndCase);
                    }
                    return;
                case Expr.If iff:
                    Visit(iff.Cond, bnd);
                    Visit(iff.Then, bnd);
                    Visit(iff.Else, bnd);
                    return;
                case Expr.Let l:
                    Visit(l.Value, bnd);
                    var boundWithLetVar = new HashSet<string>(bnd, StringComparer.Ordinal) { l.Name };
                    Visit(l.Body, boundWithLetVar);
                    return;
                case Expr.LetResult l:
                    Visit(l.Value, bnd);
                    var boundWithResultVar = new HashSet<string>(bnd, StringComparer.Ordinal) { l.Name };
                    Visit(l.Body, boundWithResultVar);
                    return;
                case Expr.LetRec l:
                    var boundWithRecVar = new HashSet<string>(bnd, StringComparer.Ordinal) { l.Name };
                    Visit(l.Value, boundWithRecVar);
                    Visit(l.Body, boundWithRecVar);
                    return;
                case Expr.Lambda lam:
                    var boundWithParam = new HashSet<string>(bnd, StringComparer.Ordinal) { lam.ParamName };
                    Visit(lam.Body, boundWithParam);
                    return;
                case Expr.Await awaitExpr:
                    Visit(awaitExpr.Task, bnd);
                    return;
                default:
                    throw new NotSupportedException(ex.GetType().Name);
            }
        }

        Visit(e, bound);
        return res;
    }

    /// <summary>
    /// Peels leading intra-module alias bindings (<c>let helper = Ashes_Mod_helper in ...</c>, which the
    /// import stitcher injects so a module body's unqualified references resolve) and substitutes those
    /// alias names with their stitched targets throughout the remaining expression. The result is a
    /// clean lambda that references top-level stitched names directly — which the reuse-specialization /
    /// inlining registries are keyed by — so stdlib functions become eligible for in-place reuse.
    /// Only used to build the registry copy; the original declaration value is left intact for normal
    /// lowering. Throws on an unhandled Expr shape so the caller can skip registration (never miscompile).
    /// </summary>
    private static Expr StripModuleAliasPrefix(Expr value)
    {
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        Expr body = value;
        while (body is Expr.Let { Value: Expr.Var aliasTarget } aliasLet)
        {
            renames[aliasLet.Name] = aliasTarget.Name;
            body = aliasLet.Body;
        }

        return renames.Count == 0 ? value : SubstituteVars(body, renames);
    }

    private static Expr SubstituteVars(Expr e, Dictionary<string, string> renames)
    {
        if (renames.Count == 0)
        {
            return e;
        }

        Expr S(Expr x) => SubstituteVars(x, renames);

        // A binder shadows a renamed name within its scope: drop it from the rename set for the subtree.
        T WithShadowed<T>(IEnumerable<string> bound, Func<Dictionary<string, string>, T> build)
        {
            var sub = renames.Where(kv => !bound.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            return build(sub);
        }

        switch (e)
        {
            case Expr.IntLit or Expr.UIntLit or Expr.FloatLit or Expr.StrLit or Expr.BoolLit or Expr.QualifiedVar:
                return e;
            case Expr.Var v:
                return renames.TryGetValue(v.Name, out var tgt) ? new Expr.Var(tgt) : e;
            case Expr.Add b: return new Expr.Add(S(b.Left), S(b.Right));
            case Expr.Subtract b: return new Expr.Subtract(S(b.Left), S(b.Right));
            case Expr.Multiply b: return new Expr.Multiply(S(b.Left), S(b.Right));
            case Expr.Divide b: return new Expr.Divide(S(b.Left), S(b.Right));
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
            case Expr.Lambda lam:
                return WithShadowed([lam.ParamName], sub => new Expr.Lambda(lam.ParamName, SubstituteVars(lam.Body, sub)));
            case Expr.Let l:
                return new Expr.Let(l.Name, S(l.Value), WithShadowed([l.Name], sub => SubstituteVars(l.Body, sub)));
            case Expr.LetResult l:
                return new Expr.LetResult(l.Name, S(l.Value), WithShadowed([l.Name], sub => SubstituteVars(l.Body, sub)));
            case Expr.LetRec l:
                return WithShadowed([l.Name], sub => new Expr.LetRec(l.Name, SubstituteVars(l.Value, sub), SubstituteVars(l.Body, sub)));
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

    private static bool UsesNameOnlyAsDirectCallee(Expr expr, string targetName, bool shadowed = false, bool allowDirectCallee = false)
    {
        switch (expr)
        {
            case Expr.IntLit:
            case Expr.UIntLit:
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
            case Expr.Let let:
                return UsesNameOnlyAsDirectCallee(let.Value, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(let.Body, targetName, shadowed || string.Equals(let.Name, targetName, StringComparison.Ordinal));
            case Expr.LetResult letResult:
                return UsesNameOnlyAsDirectCallee(letResult.Value, targetName, shadowed)
                    && UsesNameOnlyAsDirectCallee(letResult.Body, targetName, shadowed || string.Equals(letResult.Name, targetName, StringComparison.Ordinal));
            case Expr.LetRec letRec:
                {
                    bool nextShadowed = shadowed || string.Equals(letRec.Name, targetName, StringComparison.Ordinal);
                    return UsesNameOnlyAsDirectCallee(letRec.Value, targetName, nextShadowed)
                        && UsesNameOnlyAsDirectCallee(letRec.Body, targetName, nextShadowed);
                }
            case Expr.Lambda lam:
                return UsesNameOnlyAsDirectCallee(lam.Body, targetName, shadowed || string.Equals(lam.ParamName, targetName, StringComparison.Ordinal));
            case Expr.Match match:
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
            case Expr.Await awaitExpr:
                return UsesNameOnlyAsDirectCallee(awaitExpr.Task, targetName, shadowed);
            case Expr.RecordLit rl:
                return rl.Fields.All(f => UsesNameOnlyAsDirectCallee(f.Value, targetName, shadowed));
            case Expr.RecordUpdate ru:
                return UsesNameOnlyAsDirectCallee(ru.Target, targetName, shadowed)
                    && ru.Updates.All(u => UsesNameOnlyAsDirectCallee(u.Value, targetName, shadowed));
            default:
                throw new NotSupportedException(expr.GetType().Name);
        }
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
            case Expr.LetRec lr:
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

    /// <summary>
    /// Finds accumulator parameters that are passed as the sole argument to a specializable recursive
    /// function — <c>f(acc)</c> where <c>f</c> is in <see cref="_specializableFunctions"/> and
    /// <c>acc</c> is a loop accumulator. These accumulators are deep-copied once at loop entry so the
    /// call can be routed to <c>f$reuse</c>. Walks calls + the if/let/match spine.
    /// </summary>
    /// <summary>
    /// Resolves a call root to the binding name used by the reuse registries: a plain <c>Var</c> yields
    /// its name; a qualified stdlib/user-module reference (<c>Ashes.Map.set</c>) yields its stitched
    /// top-level name (<c>Ashes_Map_set</c>). Returns null for intrinsics and non-name roots.
    /// </summary>
    private string? ResolveSpecializableCalleeName(Expr root)
    {
        switch (root)
        {
            case Expr.Var v:
                return v.Name;
            case Expr.QualifiedVar qv:
                var resolvedModule = ResolveModuleAlias(qv.Module);
                if (BuiltinRegistry.TryGetModule(resolvedModule, out var module) && module.Members.ContainsKey(qv.Name))
                {
                    return null; // intrinsic member — lowered directly, not a stitched function
                }

                return ProjectSupport.SanitizeModuleBindingName(resolvedModule) + "_" + qv.Name;
            default:
                return null;
        }
    }

    private void CollectSpecializableCallArgs(Expr e, HashSet<string> paramNames, Dictionary<string, string> result)
    {
        switch (e)
        {
            case Expr.Call call:
                var callArgs = new List<Expr>();
                var callRoot = CollectCallArgs(call, callArgs);
                if (ResolveSpecializableCalleeName(callRoot) is { } fnName
                    && _specializableFunctions.TryGetValue(fnName, out var fnSpec)
                    && callArgs.Count == fnSpec.ArgCount
                    && callArgs[^1] is Expr.Var arg
                    && paramNames.Contains(arg.Name))
                {
                    result[arg.Name] = fnName;
                }

                CollectSpecializableCallArgs(call.Func, paramNames, result);
                CollectSpecializableCallArgs(call.Arg, paramNames, result);
                break;
            case Expr.If i:
                CollectSpecializableCallArgs(i.Cond, paramNames, result);
                CollectSpecializableCallArgs(i.Then, paramNames, result);
                CollectSpecializableCallArgs(i.Else, paramNames, result);
                break;
            case Expr.Let l:
                CollectSpecializableCallArgs(l.Value, paramNames, result);
                CollectSpecializableCallArgs(l.Body, paramNames, result);
                break;
            case Expr.LetRec lr:
                CollectSpecializableCallArgs(lr.Value, paramNames, result);
                CollectSpecializableCallArgs(lr.Body, paramNames, result);
                break;
            case Expr.Match m:
                CollectSpecializableCallArgs(m.Value, paramNames, result);
                foreach (var c in m.Cases)
                {
                    CollectSpecializableCallArgs(c.Body, paramNames, result);
                }

                break;
        }
    }

    /// <summary>
    /// In-place reuse: consumes an available reuse token whose cell has exactly
    /// <paramref name="fieldCount"/> fields (so it is the same size as the constructor being built).
    /// Returns the dead cell's address temp to overwrite, or false if no matching token is available.
    /// </summary>
    private bool TryConsumeReuseToken(int fieldCount, out int tokenTemp)
    {
        for (int i = _reuseTokens.Count - 1; i >= 0; i--)
        {
            if (_reuseTokens[i].FieldCount == fieldCount)
            {
                tokenTemp = _reuseTokens[i].Temp;
                _reuseTokens.RemoveAt(i);
                return true;
            }
        }

        tokenTemp = -1;
        return false;
    }

    /// <summary>
    /// Inlines a saturated call to a non-recursive top-level helper: evaluates the arguments in the
    /// current scope (so they can't be captured by the helper's own parameter names), binds the
    /// parameters to those values, and lowers the helper body in place. Lowering it here means any
    /// constructor in the body can consume a live reuse token from the enclosing arm.
    /// </summary>
    private (int, TypeRef) InlineCall(string fnName, IReadOnlyList<string> paramNames, Expr body, List<Expr> args)
    {
        var argSlots = new int[paramNames.Count];
        var argTypes = new TypeRef[paramNames.Count];
        // Params whose argument was built by in-place reuse are themselves linear: a match-then-rebuild
        // on them in the helper body reuses the same cell (intermediate-value linearity).
        var linearParams = new List<string>();
        // Params bound to a FRESH heap input of the enclosing specialization (e.g. makeNode's key/value
        // param bound to the spec's newKey/newValue) inherit that fresh-input status, so a constructor
        // field in the inlined body still materializes them into the persistent blob.
        var freshParams = new List<string>();
        for (int i = 0; i < paramNames.Count; i++)
        {
            // A param is a fresh heap input when its argument names a fresh input of the enclosing
            // specialization OR is a non-variable expression (a value computed in the arm — e.g. an
            // upsert's onHit(value) — which lives in per-iteration arena scratch). See the matching
            // materialization rule in LowerConstructorApplication.
            if (_specFreshInputNames is not null && !_specFreshInputNames.Contains(paramNames[i])
                && (args[i] is not Expr.Var fv || _specFreshInputNames.Contains(fv.Name)))
            {
                freshParams.Add(paramNames[i]);
            }

            var (argTemp, argType) = LowerExpr(args[i]);
            int slot = NewLocal();
            Emit(new IrInst.StoreLocal(slot, argTemp));
            argSlots[i] = slot;
            argTypes[i] = argType;
            if (_reuseResultTemps.Contains(argTemp) && !_linearReuseNames.Contains(paramNames[i]))
            {
                linearParams.Add(paramNames[i]);
            }
        }

        var inlineScope = new Dictionary<string, Binding>(_scopes.Peek(), StringComparer.Ordinal);
        for (int i = 0; i < paramNames.Count; i++)
        {
            inlineScope[paramNames[i]] = new Binding.Local(argSlots[i], argTypes[i]);
        }

        _scopes.Push(inlineScope);
        _inliningInProgress.Add(fnName);
        foreach (var p in linearParams) _linearReuseNames.Add(p);
        if (_specFreshInputNames is not null) foreach (var p in freshParams) _specFreshInputNames.Add(p);
        var result = LowerExpr(body);
        if (_specFreshInputNames is not null) foreach (var p in freshParams) _specFreshInputNames.Remove(p);
        foreach (var p in linearParams) _linearReuseNames.Remove(p);
        _inliningInProgress.Remove(fnName);
        _scopes.Pop();
        return result;
    }

    /// <summary>
    /// Generates (once, cached) an in-place-reuse specialization <c>f$reuse</c> of a single-parameter
    /// recursive function <paramref name="name"/>: the same body lowered with its parameter treated
    /// as a linear (uniquely-owned) reuse root, and self-calls redirected to <c>f$reuse</c> (so the
    /// recursion reuses the unique subtrees). Returns the function label to call. The caller must only
    /// route a provably-unique argument here.
    /// </summary>
    private string GetOrCreateReuseSpecialization(string name, TypeRef funcType, IReadOnlyList<TypeRef> concreteParamTypes)
    {
        // Cache per concrete instantiation: the spec is monomorphized to the call's argument types, so a
        // function used at two element types gets two specializations.
        string cacheKey = name + "|" + string.Join(",", concreteParamTypes.Select(t => Pretty(Prune(t))));
        if (_reuseSpecializations.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var spec = _specializableFunctions[name];
        string label = _reuseSpecializations.Count == 0 ? $"{name}__reuse" : $"{name}__reuse${_reuseSpecializations.Count}";
        _reuseSpecializations[cacheKey] = label;

        var savedLinear = _specializingLinearParam;
        var savedReuseLabel = _specializingReuseLabel;
        var savedInSpec = _inSpecialization;
        var savedConcrete = _specializationConcreteParamTypes;
        var savedCursor = _specializationParamCursor;
        var savedFreshInputs = _specFreshInputNames;
        _specializingLinearParam = spec.LinearParam;
        _specializingReuseLabel = null;
        _inSpecialization = true;
        _specializationConcreteParamTypes = concreteParamTypes;
        _specializationParamCursor = 0;
        _specFreshInputNames = new HashSet<string>(CollectLambdaParams(spec.Lambda), StringComparer.Ordinal);
        // forcedLabel + selfName=name make recursive calls resolve to Binding.Self(label) → f$reuse.
        // LowerLambdaCore adds the function to _funcs and then emits an incidental closure into the
        // current _inst; we don't need that closure (we build our own at each call site), so discard
        // the emitted instructions after the function is registered.
        int instBefore = _inst.Count;
        LowerLambdaCore(lam: spec.Lambda, selfName: name, selfType: funcType, stackAllocateClosure: false, forcedLabel: label);
        if (_inst.Count > instBefore)
        {
            _inst.RemoveRange(instBefore, _inst.Count - instBefore);
        }

        // The recursive reuse function (f$reuse itself, or the inner go for a nested-rec shape) is the
        // one whose return values must be below the watermark. If it is fully reusing, the whole
        // specialized call's result is below the watermark, so mark the callable label reset-safe.
        var reuseLabel = _specializingReuseLabel;
        _specializingLinearParam = savedLinear;
        _specializingReuseLabel = savedReuseLabel;
        _inSpecialization = savedInSpec;
        _specializationConcreteParamTypes = savedConcrete;
        _specializationParamCursor = savedCursor;
        _specFreshInputNames = savedFreshInputs;

        var recursiveFunc = reuseLabel is not null ? _funcs.LastOrDefault(f => f.Label == reuseLabel) : null;
        bool fullyReusing = recursiveFunc is not null && IsFullyReusing(recursiveFunc, reuseLabel!);
        if (fullyReusing)
        {
            _fullyReusingLabels.Add(label);
        }

        if (Environment.GetEnvironmentVariable("ASH_DBG_REUSE") is not null)
        {
            var rf = recursiveFunc;
            int toSpace = rf?.Instructions.Count(i => i is IrInst.AllocAdtToSpace) ?? -1;
            int allocAdt = rf?.Instructions.Count(i => i is IrInst.AllocAdt) ?? -1;
            int reusing = rf?.Instructions.Count(i => i is IrInst.AllocReusing) ?? -1;
            Console.Error.WriteLine($"[reuse] spec {name} -> {label}, reuseLabel={reuseLabel ?? "<null>"}, fullyReusing={fullyReusing}, AllocAdtToSpace={toSpace} AllocAdt={allocAdt} AllocReusing={reusing} funcType={Pretty(Prune(funcType))}");
            if (rf is not null && Environment.GetEnvironmentVariable("ASH_DBG_REUSE_IR") is not null)
            {
                System.IO.File.WriteAllLines($"/tmp/spec_{rf.Label}.txt", rf.Instructions.Select((ins, idx) => $"{idx}: {ins}"));
                Console.Error.WriteLine($"[reuse] dumped {rf.Instructions.Count} instrs of {rf.Label} to /tmp/spec_{rf.Label}.txt");
            }
        }

        return label;
    }

    /// <summary>
    /// Conservative soundness check for the loop arena reset: returns true only if every value
    /// <paramref name="f"/> returns is guaranteed to lie below the loop watermark. Sufficient
    /// condition: no fresh result-heap allocation at all (no <c>AllocAdt</c>/<c>ConcatStr</c>/copy-out
    /// — every constructor must be an in-place <c>AllocReusing</c>), every raw <c>Alloc</c> is only an
    /// environment for a closure (recursion scaffolding), and every closure is a self-closure
    /// (label = <paramref name="selfLabel"/>) used only as a call target. Under those constraints the
    /// result is built solely from reused cells, scrutinee fields, and recursive self-results — all
    /// below the watermark — while the env/closure scaffolding is dead after the call and reclaimable.
    /// </summary>
    private static bool IsFullyReusing(IrFunction f, string selfLabel)
    {
        foreach (var inst in f.Instructions)
        {
            switch (inst)
            {
                case IrInst.AllocAdt:
                case IrInst.AllocAdtStack:
                case IrInst.AllocStack:
                case IrInst.ConcatStr:
                case IrInst.CopyOutArena:
                case IrInst.CopyOutList:
                case IrInst.CopyOutClosure:
                case IrInst.CopyOutTcoListCell:
                    if (Environment.GetEnvironmentVariable("ASH_DBG_REUSE") is not null)
                    {
                        Console.Error.WriteLine($"[reuse] IsFullyReusing({f.Label}) rejected by instruction kind: {inst}");
                    }

                    return false;
            }
        }

        // Build temp → reading instructions, then require every Alloc to feed only a MakeClosure env
        // and every MakeClosure to feed only a CallClosure callee.
        var readers = new Dictionary<int, List<IrInst>>();
        var buf = new HashSet<int>();
        foreach (var inst in f.Instructions)
        {
            buf.Clear();
            IrOptimizer.CollectUsedTemps(inst, buf);
            foreach (var t in buf)
            {
                if (!readers.TryGetValue(t, out var list))
                {
                    readers[t] = list = new List<IrInst>();
                }

                list.Add(inst);
            }
        }

        // Local-slot dataflow, for values that pass through a let/inlined-arg slot: slot → number
        // of stores, and slot → the temps its loads produce.
        var slotStores = new Dictionary<int, int>();
        var slotLoads = new Dictionary<int, List<int>>();
        foreach (var inst in f.Instructions)
        {
            switch (inst)
            {
                case IrInst.StoreLocal sl:
                    slotStores[sl.Slot] = slotStores.GetValueOrDefault(sl.Slot) + 1;
                    break;
                case IrInst.LoadLocal ll:
                    if (!slotLoads.TryGetValue(ll.Slot, out var loads))
                    {
                        slotLoads[ll.Slot] = loads = new List<int>();
                    }

                    loads.Add(ll.Target);
                    break;
            }
        }

        // A fresh in-arm value (e.g. an upsert's stats tuple) is SAFELY consumed when every reader
        // either writes INTO it, reads a field FROM it, materializes it into persistent storage
        // (CopyFixedInto / CopyOutArenaToSpace), captures it in a closure env (the closure itself is
        // checked below), or moves it through a single-store local slot whose every load is itself
        // safely consumed. Anything else (a SetAdtField storing the raw pointer as a node field, a
        // call, a return) means per-iteration scratch could escape, so the reset is disqualified.
        bool SafelyConsumed(int temp, int depth)
        {
            if (!readers.TryGetValue(temp, out var rs))
            {
                return true;
            }

            foreach (var r in rs)
            {
                switch (r)
                {
                    case IrInst.MakeClosure:
                    case IrInst.MakeClosureStack:
                        continue;
                    case IrInst.SetAdtField sa when sa.Ptr == temp:
                        continue;
                    case IrInst.GetAdtField gf when gf.Ptr == temp:
                        continue;
                    case IrInst.StoreMemOffset sm when sm.BasePtr == temp:
                        continue;
                    case IrInst.LoadMemOffset lm when lm.BasePtr == temp:
                        continue;
                    case IrInst.CopyFixedInto cfi when cfi.SrcTemp == temp:
                        continue;
                    case IrInst.CopyOutArenaToSpace co when co.SrcTemp == temp:
                        continue;
                    case IrInst.Borrow b when b.SourceTemp == temp && depth < 4 && SafelyConsumed(b.Target, depth + 1):
                        continue;
                    case IrInst.StoreLocal sl when depth < 4
                        && slotStores.GetValueOrDefault(sl.Slot) == 1
                        && (!slotLoads.TryGetValue(sl.Slot, out var loads)
                            || loads.All(lt => SafelyConsumed(lt, depth + 1))):
                        continue;
                    default:
                        return false;
                }
            }

            return true;
        }

        foreach (var inst in f.Instructions)
        {
            switch (inst)
            {
                case IrInst.Alloc alloc when !SafelyConsumed(alloc.Target, 0):
                    if (Environment.GetEnvironmentVariable("ASH_DBG_REUSE") is not null)
                    {
                        Console.Error.WriteLine($"[reuse] IsFullyReusing({f.Label}) rejected: {alloc} readers: {string.Join(" | ", readers.GetValueOrDefault(alloc.Target, []).Select(x => x.ToString()![..Math.Min(90, x.ToString()!.Length)]))}");
                    }

                    return false;
                case IrInst.MakeClosure mk when readers.TryGetValue(mk.Target, out var rs)
                    && rs.Any(r => r is not IrInst.CallClosure cc || cc.ClosureTemp != mk.Target):
                    if (Environment.GetEnvironmentVariable("ASH_DBG_REUSE") is not null)
                    {
                        Console.Error.WriteLine($"[reuse] IsFullyReusing({f.Label}) rejected closure: {mk} readers: {string.Join(" | ", rs.Select(x => x.ToString()![..Math.Min(90, x.ToString()!.Length)]))}");
                    }

                    return false;
                case IrInst.MakeClosureStack mks when readers.TryGetValue(mks.Target, out var rs)
                    && rs.Any(r => r is not IrInst.CallClosure cc || cc.ClosureTemp != mks.Target):
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Lowers <c>f(args…)(acc)</c> as a call to the in-place-reuse specialization <c>f$reuse</c>:
    /// builds an empty-env closure for the (no-capture) specialized function and applies it to all
    /// arguments in turn (the last being the uniquely-owned accumulator). <c>f$reuse</c> rewrites that
    /// tree in place.
    /// </summary>
    // Whether a reuse specialization's rebuilt accumulator references only persistent (never-reset) memory,
    // so the loop back-edge may reclaim the main arena each iteration without dangling anything. The tree's
    // nodes are always persistent (to-space / in-place reuse) and its children are too; the only risk is a
    // heap LEAF field pointing into per-iteration scratch. For the stdlib Map (MapTree(K, V)): the key is
    // materialized to the persistent blob on insert and kept from the matched node on update, so a copy or
    // Str/Bytes key is always persistent; the value is freshly produced on every update and only
    // insert-materialized, so it is persistent only when it is a copy type. Other (non-Map) heap
    // accumulators are treated conservatively as not-yet-persistent (correct, just not reset-reclaimed):
    // the general fix is to materialize fresh heap leaf fields on the update path too.
    private bool AccumulatorIsFullyPersistent(TypeRef accType)
    {
        if (Prune(accType) is not TypeRef.TNamedType named)
        {
            return false;
        }

        // Structural check over the accumulator ADT (this used to special-case MapTree): every
        // constructor field must be either the accumulator type itself (a recursive child — the
        // node cells live in to-space / are rewritten in place, so they are persistent) or a leaf
        // type the reuse materialization makes persistent (copy types inline; Str/Bytes and
        // copy-tuples are copied to the blob on insert and overwritten in place on update — see
        // LowerConstructorApplication). Any other field shape (a list, a closure, a foreign ADT)
        // could point into per-iteration scratch, so the loop must not reset.
        var sym = named.Symbol;
        if (sym.Constructors.Count == 0)
        {
            return false;
        }

        Dictionary<TypeParameterSymbol, TypeRef>? typeParamMap = null;
        if (sym.TypeParameters.Count > 0 && named.TypeArgs.Count == sym.TypeParameters.Count)
        {
            typeParamMap = new Dictionary<TypeParameterSymbol, TypeRef>();
            for (int i = 0; i < sym.TypeParameters.Count; i++)
            {
                typeParamMap[sym.TypeParameters[i]] = named.TypeArgs[i];
            }
        }

        foreach (var ctor in sym.Constructors)
        {
            foreach (var fieldType in ctor.ParameterTypes)
            {
                var resolved = Prune(ResolveFieldType(fieldType, typeParamMap));
                bool isSelf = resolved is TypeRef.TNamedType fieldNamed
                    && ReferenceEquals(fieldNamed.Symbol, sym);
                if (!isSelf && !IsReuseMaterializableFieldType(resolved))
                {
                    return false;
                }
            }
        }

        return true;
    }

    // A reuse-node heap leaf field type the materialization can make persistent (blob): copy types (inline,
    // nothing to do), Str/Bytes (dynamic copy), and tuples of copy-type elements (fixed-size shallow copy).
    // Must match the materialization cases in LowerConstructorApplication.
    private bool IsReuseMaterializableFieldType(TypeRef t) =>
        BuiltinRegistry.IsCopyType(t)
        || t is TypeRef.TStr or TypeRef.TBytes
        || (t is TypeRef.TTuple tup && tup.Elements.All(e => BuiltinRegistry.IsCopyType(Prune(e))));

    // True only if a specializable function actually rebuilds its accumulator in place: its result
    // type (after applying all <paramref name="argCount"/> args) is the same named ADT as its last
    // parameter (the accumulator). A rewriter like Map.set (MapTree -> MapTree) qualifies; a pure
    // reader like Map.get (MapTree -> Maybe) does not — routing a reader through the reuse spec would
    // allocate its result cell (e.g. Some(value)) into the never-reset to-space, leaking one cell per
    // call. Readers stay on the normal path, where their result lives in the reclaimed main arena.
    private bool SpecializationRebuildsAccumulator(TypeRef funcType, int argCount)
    {
        var current = Prune(funcType);
        TypeRef? lastParam = null;
        for (int i = 0; i < argCount; i++)
        {
            if (current is not TypeRef.TFun fun)
            {
                return false;
            }

            lastParam = Prune(fun.Arg);
            current = Prune(fun.Ret);
        }

        return lastParam is TypeRef.TNamedType paramNamed
            && current is TypeRef.TNamedType resultNamed
            && string.Equals(paramNamed.Symbol.Name, resultNamed.Symbol.Name, StringComparison.Ordinal);
    }

    private (int, TypeRef) LowerReuseSpecializedCall(string name, TypeRef funcType, List<Expr> args, Expr callExpr)
    {
        // Lower the arguments first to learn their concrete types: the generic funcType does not say
        // whether the key/value are heap (Str) or copy (Int), and the specialization must be
        // monomorphized to those types so heap fields are materialized into the persistent to-space.
        var argTemps = new int[args.Count];
        var concreteParamTypes = new List<TypeRef>(args.Count);
        var curType = Prune(funcType);
        for (int i = 0; i < args.Count; i++)
        {
            var (argTemp, argType) = LowerExpr(args[i]);
            argTemps[i] = argTemp;
            if (curType is TypeRef.TFun tfun)
            {
                Unify(tfun.Arg, argType);
                curType = Prune(tfun.Ret);
            }

            concreteParamTypes.Add(Prune(argType));
        }

        string label = GetOrCreateReuseSpecialization(name, funcType, concreteParamTypes);
        // If the specialization fully reuses, its result is the accumulator (the last argument)
        // rewritten in place — address-stable exactly when that argument was. Record the call node so a
        // back-edge stability check can trace through it, and (when the argument is a bare accumulator
        // name) mark that name reset-safe. The reset itself still requires the back-edge argument to be
        // proven address-stable — this marking is necessary, not sufficient.
        if (_fullyReusingLabels.Contains(label)
            && AccumulatorIsFullyPersistent(concreteParamTypes[^1]))
        {
            _inPlaceReuseCallExprs.Add(callExpr);
            if (args[^1] is Expr.Var accVar)
            {
                _resetSafeAccumulators.Add(accVar.Name);
            }
        }

        int envPtr = NewTemp();
        Emit(new IrInst.Alloc(envPtr, 8));
        int closureTemp = NewTemp();
        Emit(new IrInst.MakeClosure(closureTemp, label, envPtr, 0));

        // Apply the specialized closure to each (already-lowered) argument in turn.
        int current = closureTemp;
        foreach (var argTemp in argTemps)
        {
            int next = NewTemp();
            Emit(new IrInst.CallClosure(next, current, argTemp));
            current = next;
        }

        return (current, curType);
    }

    /// <summary>
    /// Resolves a call whose root is one of the four data-parallel combinators to the grained combinator
    /// it monomorphizes through, plus the full argument list (the `map`/`reduce` wrappers prepend a
    /// literal grain of 1). Returns null when the root is not a saturated combinator call.
    /// </summary>
    private (string GrainedName, Expr.Lambda Lambda, List<Expr> Args)? TryResolveParallelCombinatorCall(Expr rootExpr, List<Expr> collectedArgs)
    {
        if (ResolveSpecializableCalleeName(rootExpr) is not { } calleeName)
        {
            return null;
        }

        // Direct grained call: monomorphize as-is.
        if (_parallelSpecializable.TryGetValue(calleeName, out var grained) && collectedArgs.Count == grained.ArgCount)
        {
            return (calleeName, grained.Lambda, collectedArgs);
        }

        // grain-1 wrapper (`map`/`reduce`): route to the grained combinator with grain = 1 prepended.
        string? grainedName =
            string.Equals(calleeName, ParallelMapName, StringComparison.Ordinal) && collectedArgs.Count == 2 ? ParallelMapGrainedName
            : string.Equals(calleeName, ParallelReduceName, StringComparison.Ordinal) && collectedArgs.Count == 4 ? ParallelReduceGrainedName
            : null;
        if (grainedName is not null && _parallelSpecializable.TryGetValue(grainedName, out var grainedTarget))
        {
            var args = new List<Expr>(collectedArgs.Count + 1) { new Expr.IntLit(1) };
            args.AddRange(collectedArgs);
            return (grainedName, grainedTarget.Lambda, args);
        }

        return null;
    }

    // The polymorphic signatures of the grained data-parallel combinators, rebuilt with fresh type
    // variables so that lowering the arguments against them propagates concrete element types (e.g. an
    // identity mapper's element type is fixed by the list argument, not by the lambda alone). Returns the
    // full curried function type and the combinator's result type (List b for map, the accumulator for
    // reduce). The leading `grain : Int` parameter is present on both.
    private (TypeRef FuncType, TypeRef ResultType) BuildParallelCombinatorType(string name)
    {
        if (string.Equals(name, ParallelMapGrainedName, StringComparison.Ordinal))
        {
            // mapGrained : Int -> (a -> b) -> List a -> List b
            var a = NewTypeVar();
            var b = NewTypeVar();
            var func = new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TFun(new TypeRef.TFun(a, b), new TypeRef.TFun(new TypeRef.TList(a), new TypeRef.TList(b))));
            return (func, new TypeRef.TList(b));
        }

        // reduceGrained : Int -> (c -> c -> c) -> c -> (e -> c) -> List e -> c
        var acc = NewTypeVar();
        var elem = NewTypeVar();
        var combine = new TypeRef.TFun(acc, new TypeRef.TFun(acc, acc));
        var mapFn = new TypeRef.TFun(elem, acc);
        var reduceFunc = new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TFun(combine, new TypeRef.TFun(acc, new TypeRef.TFun(mapFn, new TypeRef.TFun(new TypeRef.TList(elem), acc)))));
        return (reduceFunc, acc);
    }

    /// <summary>
    /// Data-parallel map/reduce. Lowers the arguments once (against a reconstructed generic signature so
    /// element types propagate), then — when the concrete result type is one a worker's arena-isolated
    /// result can be safely lifted back from, so <c>both</c> will genuinely fork — routes to a monomorphic
    /// self-recursive specialization whose recursive halves split through <c>both</c>. Otherwise it lowers
    /// a plain sequential call to the polymorphic combinator (an identical result). Never falls through, so
    /// the arguments are lowered exactly once.
    /// </summary>
    private (int, TypeRef) TryLowerParallelSpecializedCall(string name, Expr.Lambda lambda, List<Expr> args)
    {
        var (funcType, resultType) = BuildParallelCombinatorType(name);

        var argTemps = new int[args.Count];
        var concreteParamTypes = new List<TypeRef>(args.Count);
        var curType = Prune(funcType);
        for (int i = 0; i < args.Count; i++)
        {
            var (argTemp, argType) = LowerExpr(args[i]);
            argTemps[i] = argTemp;
            if (curType is TypeRef.TFun tfun)
            {
                Unify(tfun.Arg, argType);
                curType = Prune(tfun.Ret);
            }

            concreteParamTypes.Add(Prune(argType));
        }

        resultType = Prune(resultType);

        // Only specialize when the worker's result can be lifted into the parent arena — otherwise `both`
        // would fall back to sequential inside the body anyway, so a call to the polymorphic combinator is
        // equivalent and avoids emitting a dead specialization.
        if (CanRunRightOnWorker(resultType))
        {
            string label = GetOrCreateParallelSpecialization(name, lambda, Prune(funcType), concreteParamTypes);
            // Invoke with a null env, exactly like a top-level empty-env function: the specialization
            // captures nothing, and its self-closure is rebuilt from this env pointer, which must not be a
            // live arena allocation that a forked worker could observe dangling after an arena reset.
            int envPtr = NewTemp();
            Emit(new IrInst.LoadConstInt(envPtr, 0));
            int closureTemp = NewTemp();
            Emit(new IrInst.MakeClosure(closureTemp, label, envPtr, 0));
            return ApplyLoweredArgs(closureTemp, argTemps, resultType);
        }

        var (combinatorTemp, _) = LowerVar(new Expr.Var(name));
        return ApplyLoweredArgs(combinatorTemp, argTemps, resultType);
    }

    // Applies a sequence of already-lowered argument temps to a callable closure temp, returning the
    // final result temp paired with the supplied result type.
    private (int, TypeRef) ApplyLoweredArgs(int closureTemp, IReadOnlyList<int> argTemps, TypeRef resultType)
    {
        int current = closureTemp;
        foreach (var argTemp in argTemps)
        {
            int next = NewTemp();
            Emit(new IrInst.CallClosure(next, current, argTemp));
            current = next;
        }

        return (current, resultType);
    }

    /// <summary>
    /// Work-conserving lowering of a saturated <c>Parallel.reduce(combine)(identity)(f)(xs)</c> call.
    /// Lowers the arguments once (against the combinator's reconstructed generic signature so element
    /// types propagate), then — when the concrete result type is one a worker's arena-isolated result
    /// can be deep-copied back from — emits the runtime chunk queue: workers pull element indexes from
    /// a shared atomic counter, publish <c>f(element)</c> per index, and merge the results pairwise
    /// through <c>combine</c> in the fixed shape determined by the element count (deterministic no
    /// matter which worker computed what; equivalent to the fork-tree shape under reduce's
    /// associative-combine contract). This caller awaits only the merge root and deep-copies it out.
    /// An empty list yields <c>identity</c>; a singleton yields <c>f(x)</c> alone, exactly like the
    /// sequential combinator. Otherwise falls back to a plain sequential call to the polymorphic
    /// combinator (an identical result). Never falls through, so the arguments are lowered exactly
    /// once.
    /// </summary>
    private (int, TypeRef) LowerParallelReduceQueued(List<Expr> args)
    {
        // reduce : (c -> c -> c) -> c -> (e -> c) -> List e -> c
        var acc = NewTypeVar();
        var elem = NewTypeVar();
        var combineType = new TypeRef.TFun(acc, new TypeRef.TFun(acc, acc));
        var mapFnType = new TypeRef.TFun(elem, acc);
        TypeRef curType = new TypeRef.TFun(combineType, new TypeRef.TFun(acc, new TypeRef.TFun(mapFnType, new TypeRef.TFun(new TypeRef.TList(elem), acc))));

        var savedTailPos = _tcoCtx?.InTailPosition ?? false;
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        var argTemps = new int[args.Count];
        for (int i = 0; i < args.Count; i++)
        {
            var (argTemp, argType) = LowerExpr(args[i]);
            argTemps[i] = argTemp;
            if (Prune(curType) is TypeRef.TFun tfun)
            {
                Unify(tfun.Arg, argType);
                curType = Prune(tfun.Ret);
            }
        }

        var resultType = Prune(acc);
        int combineTemp = argTemps[0];
        int identityTemp = argTemps[1];
        int fTemp = argTemps[2];
        int listTemp = argTemps[3];

        // The workers' raw results live in worker arenas, so the merge must deep-copy each one at
        // the concrete result type — unavailable for an abstract result, which (like `both`) degrades
        // to the sequential polymorphic combinator.
        if (!CanRunRightOnWorker(resultType))
        {
            if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;
            var (combinatorTemp, _) = LowerVar(new Expr.Var(ParallelReduceName));
            return ApplyLoweredArgs(combinatorTemp, argTemps, resultType);
        }

        int descTemp = NewTemp();
        Emit(new IrInst.ParallelQueueStart(descTemp, fTemp, combineTemp, listTemp));
        // Element count, published by the queue-start runtime at descriptor offset 8.
        int countTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(countTemp, descTemp, 8));

        int accSlot = NewLocal();
        string emptyLabel = NewLabel("parq_empty");
        string doneLabel = NewLabel("parq_done");

        int zeroTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(zeroTemp, 0));
        int nonEmptyTemp = NewTemp();
        Emit(new IrInst.CmpIntGt(nonEmptyTemp, countTemp, zeroTemp));
        Emit(new IrInst.JumpIfFalse(nonEmptyTemp, emptyLabel));

        // Non-empty: the workers fold and pairwise-merge everything; await the merge root.
        int rootRawTemp = NewTemp();
        Emit(new IrInst.ParallelQueueAwait(rootRawTemp, descTemp));
        int rootTemp = EmitDeepCopy(rootRawTemp, resultType);
        Emit(new IrInst.StoreLocal(accSlot, rootTemp));
        Emit(new IrInst.Jump(doneLabel));

        Emit(new IrInst.Label(emptyLabel));
        Emit(new IrInst.StoreLocal(accSlot, identityTemp));

        Emit(new IrInst.Label(doneLabel));
        Emit(new IrInst.ParallelQueueCleanup(descTemp));
        int resultTemp = NewTemp();
        Emit(new IrInst.LoadLocal(resultTemp, accSlot));

        if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;
        return (resultTemp, resultType);
    }

    /// <summary>
    /// Generates (once per concrete instantiation) a monomorphic self-recursive specialization of a
    /// parallel combinator. The body is the combinator's own source, lowered with its parameters pinned to
    /// the call's concrete types, so the recursive <c>both</c> splits see a concrete result type and fork.
    /// Recursive self-calls resolve to this label (Binding.Self); stitched list helpers resolve by-label
    /// from the captured top-level scope (the <c>_inSpecialization</c> path). Not a linear-reuse spec.
    /// </summary>
    private string GetOrCreateParallelSpecialization(string name, Expr.Lambda lambda, TypeRef funcType, IReadOnlyList<TypeRef> concreteParamTypes)
    {
        string cacheKey = name + "|" + string.Join(",", concreteParamTypes.Select(t => Pretty(Prune(t))));
        if (_parallelSpecializations.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        string label = _parallelSpecializations.Count == 0 ? $"{name}__par" : $"{name}__par${_parallelSpecializations.Count}";
        _parallelSpecializations[cacheKey] = label;

        // Generate the body in an isolated scope: its only free names are the module's top-level list
        // helpers (resolved by-label as static globals via _topLevelFunctionRefs — never captured, so no
        // arena closure crosses a fork), the self reference (Binding.Self on this label), and the qualified
        // `both` primitive (module-resolved, scope-independent). Emptying the scope means the helpers are
        // NOT found by Lookup and fall through to by-label resolution; a captured helper would otherwise
        // become an arena closure a worker thread could read while the parent resets its arena — a race.
        // The bumped lambda depth keeps LowerLambdaCore from treating this as a top-level declaration and
        // snapshotting the emptied scope over the real one. Pin the concrete parameter types so the body
        // monomorphizes and `both` sees a concrete result.
        var savedInParSpec = _inParallelSpecialization;
        var savedConcrete = _specializationConcreteParamTypes;
        var savedCursor = _specializationParamCursor;
        var savedScopes = _scopes.ToArray();
        var savedLambdaDepth = _lambdaDepth;
        _inParallelSpecialization = true;
        _specializationConcreteParamTypes = concreteParamTypes;
        _specializationParamCursor = 0;
        _scopes.Clear();
        _scopes.Push(new Dictionary<string, Binding>(StringComparer.Ordinal));
        _lambdaDepth = savedLambdaDepth == 0 ? 1 : savedLambdaDepth;

        int instBefore = _inst.Count;
        LowerLambdaCore(lam: lambda, selfName: name, selfType: funcType, stackAllocateClosure: false, forcedLabel: label);
        if (_inst.Count > instBefore)
        {
            _inst.RemoveRange(instBefore, _inst.Count - instBefore);
        }

        _inParallelSpecialization = savedInParSpec;
        _specializationConcreteParamTypes = savedConcrete;
        _specializationParamCursor = savedCursor;
        _lambdaDepth = savedLambdaDepth;
        _scopes.Clear();
        for (int i = savedScopes.Length - 1; i >= 0; i--)
        {
            _scopes.Push(savedScopes[i]);
        }

        return label;
    }

    /// <summary>
    /// Marks/unmarks an inlinable-function name as shadowed by a more-local binding so a call to it
    /// is not mistaken for the top-level helper. No-op unless the name is actually a registered
    /// inlinable function. Returns whether a mark was added (so the caller can balance the unmark).
    /// </summary>
    private bool PushInlinableShadow(string name)
    {
        if (!_inlinableFunctions.ContainsKey(name))
        {
            return false;
        }

        _shadowedInlinables[name] = _shadowedInlinables.GetValueOrDefault(name) + 1;
        return true;
    }

    private void PopInlinableShadow(string name)
    {
        int remaining = _shadowedInlinables.GetValueOrDefault(name) - 1;
        if (remaining <= 0)
        {
            _shadowedInlinables.Remove(name);
        }
        else
        {
            _shadowedInlinables[name] = remaining;
        }
    }

    private static bool ExprReferencesName(Expr expr, string targetName, bool shadowed = false)
    {
        switch (expr)
        {
            case Expr.IntLit:
            case Expr.UIntLit:
            case Expr.FloatLit:
            case Expr.StrLit:
            case Expr.BoolLit:
            case Expr.QualifiedVar:
                return false;

            case Expr.Var v:
                return !shadowed && string.Equals(v.Name, targetName, StringComparison.Ordinal);

            case Expr.Add add:
                return ExprReferencesName(add.Left, targetName, shadowed) || ExprReferencesName(add.Right, targetName, shadowed);
            case Expr.Subtract sub:
                return ExprReferencesName(sub.Left, targetName, shadowed) || ExprReferencesName(sub.Right, targetName, shadowed);
            case Expr.Multiply mul:
                return ExprReferencesName(mul.Left, targetName, shadowed) || ExprReferencesName(mul.Right, targetName, shadowed);
            case Expr.Divide div:
                return ExprReferencesName(div.Left, targetName, shadowed) || ExprReferencesName(div.Right, targetName, shadowed);
            case Expr.BitwiseAnd bitAnd:
                return ExprReferencesName(bitAnd.Left, targetName, shadowed) || ExprReferencesName(bitAnd.Right, targetName, shadowed);
            case Expr.BitwiseOr bitOr:
                return ExprReferencesName(bitOr.Left, targetName, shadowed) || ExprReferencesName(bitOr.Right, targetName, shadowed);
            case Expr.BitwiseXor bitXor:
                return ExprReferencesName(bitXor.Left, targetName, shadowed) || ExprReferencesName(bitXor.Right, targetName, shadowed);
            case Expr.ShiftLeft shiftLeft:
                return ExprReferencesName(shiftLeft.Left, targetName, shadowed) || ExprReferencesName(shiftLeft.Right, targetName, shadowed);
            case Expr.ShiftRight shiftRight:
                return ExprReferencesName(shiftRight.Left, targetName, shadowed) || ExprReferencesName(shiftRight.Right, targetName, shadowed);
            case Expr.BitwiseNot bitwiseNot:
                return ExprReferencesName(bitwiseNot.Operand, targetName, shadowed);
            case Expr.GreaterThan gt:
                return ExprReferencesName(gt.Left, targetName, shadowed) || ExprReferencesName(gt.Right, targetName, shadowed);
            case Expr.GreaterOrEqual ge:
                return ExprReferencesName(ge.Left, targetName, shadowed) || ExprReferencesName(ge.Right, targetName, shadowed);
            case Expr.LessThan lt:
                return ExprReferencesName(lt.Left, targetName, shadowed) || ExprReferencesName(lt.Right, targetName, shadowed);
            case Expr.LessOrEqual le:
                return ExprReferencesName(le.Left, targetName, shadowed) || ExprReferencesName(le.Right, targetName, shadowed);
            case Expr.Equal eq:
                return ExprReferencesName(eq.Left, targetName, shadowed) || ExprReferencesName(eq.Right, targetName, shadowed);
            case Expr.NotEqual ne:
                return ExprReferencesName(ne.Left, targetName, shadowed) || ExprReferencesName(ne.Right, targetName, shadowed);
            case Expr.ResultPipe pipe:
                return ExprReferencesName(pipe.Left, targetName, shadowed) || ExprReferencesName(pipe.Right, targetName, shadowed);
            case Expr.ResultMapErrorPipe pipe:
                return ExprReferencesName(pipe.Left, targetName, shadowed) || ExprReferencesName(pipe.Right, targetName, shadowed);
            case Expr.Call call:
                return ExprReferencesName(call.Func, targetName, shadowed) || ExprReferencesName(call.Arg, targetName, shadowed);
            case Expr.TupleLit tuple:
                return tuple.Elements.Any(elem => ExprReferencesName(elem, targetName, shadowed));
            case Expr.ListLit list:
                return list.Elements.Any(elem => ExprReferencesName(elem, targetName, shadowed));
            case Expr.Cons cons:
                return ExprReferencesName(cons.Head, targetName, shadowed) || ExprReferencesName(cons.Tail, targetName, shadowed);
            case Expr.If iff:
                return ExprReferencesName(iff.Cond, targetName, shadowed)
                    || ExprReferencesName(iff.Then, targetName, shadowed)
                    || ExprReferencesName(iff.Else, targetName, shadowed);
            case Expr.Let let:
                return ExprReferencesName(let.Value, targetName, shadowed)
                    || ExprReferencesName(let.Body, targetName, shadowed || string.Equals(let.Name, targetName, StringComparison.Ordinal));
            case Expr.LetResult letResult:
                return ExprReferencesName(letResult.Value, targetName, shadowed)
                    || ExprReferencesName(letResult.Body, targetName, shadowed || string.Equals(letResult.Name, targetName, StringComparison.Ordinal));
            case Expr.LetRec letRec:
                {
                    bool nextShadowed = shadowed || string.Equals(letRec.Name, targetName, StringComparison.Ordinal);
                    return ExprReferencesName(letRec.Value, targetName, nextShadowed)
                        || ExprReferencesName(letRec.Body, targetName, nextShadowed);
                }
            case Expr.Lambda lam:
                return ExprReferencesName(lam.Body, targetName, shadowed || string.Equals(lam.ParamName, targetName, StringComparison.Ordinal));
            case Expr.Match match:
                if (ExprReferencesName(match.Value, targetName, shadowed))
                {
                    return true;
                }

                foreach (var matchCase in match.Cases)
                {
                    bool caseShadowed = shadowed || PatternBindings(matchCase.Pattern).Any(boundName => string.Equals(boundName, targetName, StringComparison.Ordinal));
                    if ((matchCase.Guard is not null && ExprReferencesName(matchCase.Guard, targetName, caseShadowed))
                        || ExprReferencesName(matchCase.Body, targetName, caseShadowed))
                    {
                        return true;
                    }
                }

                return false;
            case Expr.Await awaitExpr:
                return ExprReferencesName(awaitExpr.Task, targetName, shadowed);
            case Expr.RecordLit rl:
                return rl.Fields.Any(f => ExprReferencesName(f.Value, targetName, shadowed));
            case Expr.RecordUpdate ru:
                return ExprReferencesName(ru.Target, targetName, shadowed)
                    || ru.Updates.Any(u => ExprReferencesName(u.Value, targetName, shadowed));
            default:
                throw new NotSupportedException(expr.GetType().Name);
        }
    }




























































}
