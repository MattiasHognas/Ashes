using System.Diagnostics;
using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    private sealed class DiagnosticContextScope(List<string> diagnosticContext) : IDisposable
    {
        public void Dispose()
        {
            diagnosticContext.RemoveAt(diagnosticContext.Count - 1);
        }
    }

    private sealed class DiagnosticSpanScope(Stack<TextSpan> diagnosticSpans) : IDisposable
    {
        public void Dispose()
        {
            diagnosticSpans.Pop();
        }
    }

    private sealed class DiagnosticCodeScope(Stack<string> diagnosticCodes) : IDisposable
    {
        public void Dispose()
        {
            diagnosticCodes.Pop();
        }
    }

    // One TCO loop parameter's ownership facts: the static, AST-derived usage-shape signals a
    // parameter can be classified by before its type is even resolved, plus the runtime-managed (RC)
    // representation verdict that is decided once types are known and can be revised later by the
    // frame-wide profitability veto. Replaces what used to be twelve separate name/slot-keyed sets and
    // maps on TcoContext (see that class's own remarks) with one entry per parameter, keyed by slot.
    private sealed class TcoParamOwnership
    {
        public required string ParamName { get; init; }
        public required int Slot { get; init; }

        // Param passed as its own unchanged Var at EVERY tail self-call — loop-invariant, so it
        // never holds a value allocated inside the loop and always points below the arena watermark.
        // A plain per-iteration reset therefore leaves it valid, even when it is a heap type (e.g. a
        // Bytes threaded unchanged through a fold).
        public bool LoopInvariant { get; init; }

        // Param whose argument is an arena-self-contained whole-list rebuild at every tail
        // self-call. This is not reference freshness: a helper result may retain an input tail but
        // still be independent of the helper's callee arena. Such parameters may use whole-spine
        // runtime-RC normalization; cons-growing/shared-spine params must use the separate
        // ownership-transfer path instead.
        public bool FreshRebuiltList { get; init; }
        public bool AffineConsList { get; init; }
        public bool ConsumedListTail { get; init; }

        // True when ConsumedListTail also holds AND the recursive body only INSPECTS the param: every
        // head and tail bound from it is used solely as a match scrutinee (heads destructured into
        // inline-copy scalar fields) or re-passed in the same tail position of the self-call, never
        // returned, stored into a constructed value, or handed to another function in an owning
        // position. Such a list can be BORROWED from the caller's graph — no per-entry RC
        // normalization/dup/drop — because nothing derived from it escapes the traversal (the
        // pointer-bearing analogue of the inline-element borrowed cursor). Gated additionally on an
        // all-inline-copy-field record element type at the runtime-managed decision site.
        public bool BorrowableConsumedList { get; init; }
        public bool FreshClosure { get; init; }

        // Param proven AFFINE across the loop (consumed at most once along every loop-continuing
        // path, and only as the leftmost leaf of the `+` chain producing its own tail-call argument).
        // The affine property guarantees the loop holds no other reference, licensing in-place
        // reservation growth (ConcatStrTip). False when not computed (conservative).
        public bool AffineStr { get; init; }

        // The runtime-managed (RC) representation verdict and its resolved type/shape. Mutated at the
        // same points in the lowering pipeline the four separate collections this replaces used to be
        // written: the loop-entry and post-body classifier-A passes, the resolved-argument-type
        // promotion at each back-edge call site, and the frame-wide profitability veto's demotion. See
        // TcoContext.MarkRuntimeManaged/ClearRuntimeManaged, the only writers.
        public bool RuntimeManaged { get; set; }
        public bool RuntimeManagedList { get; set; }
        public bool RuntimeManagedClosure { get; set; }
        public TypeRef? RuntimeManagedType { get; set; }
    }

    // TCO (tail call optimization) state
    private sealed class TcoContext
    {
        public string SelfName { get; init; }
        public string SelfLabel { get; set; } = "";
        public int? SelfSlot { get; set; }
        public FuncKey? OwnershipFunction { get; init; }
        public string BodyLabel { get; set; } = "";
        public int ParamCount { get; init; }
        public List<string> ParamNames { get; init; }
        public Dictionary<string, string> ParamLabels { get; } = new(System.StringComparer.Ordinal);
        public List<int> ParamSlots { get; init; } = [];

        // One ownership record per parameter, keyed by slot — the single source of truth every
        // consumer should read instead of testing membership in a scattered set. Populated once
        // ParamSlots is known (see BuildParamOwnership) by joining ParamNames against the raw
        // Collect*-derived facts below; the RuntimeManaged* fields on each entry are then written and
        // revised in place by MarkRuntimeManaged/ClearRuntimeManaged as the lowering pipeline resolves
        // types and evaluates profitability.
        public Dictionary<int, TcoParamOwnership> ParamOwnership { get; } = [];

        // Preserves the exact legacy enumeration order of the two now-collapsed per-slot membership
        // sets this replaces (the old RuntimeManagedParamSlots/RuntimeManagedClosureParamSlots
        // HashSet<int> fields), because a handful of codegen passes iterate "every currently
        // runtime-managed parameter" and allocate a fresh local slot or emit an instruction per one
        // visited — changing that order would not change program behavior, but could change the exact
        // sequence of locals/instructions the backend emits. Kept as private, order-preserving backing
        // state rather than folded into ParamOwnership's own (fixed, ParamSlots-order) iteration order.
        private readonly HashSet<int> _runtimeManagedOrder = [];
        private readonly HashSet<int> _runtimeManagedClosureOrder = [];

        public IEnumerable<int> RuntimeManagedSlotsInOrder => _runtimeManagedOrder;
        public IEnumerable<int> RuntimeManagedClosureSlotsInOrder => _runtimeManagedClosureOrder;
        public int RuntimeManagedSlotCount => _runtimeManagedOrder.Count;

        public bool IsRuntimeManagedSlot(int slot) =>
            ParamOwnership.TryGetValue(slot, out var ownership) && ownership.RuntimeManaged;

        public bool IsRuntimeManagedListSlot(int slot) =>
            ParamOwnership.TryGetValue(slot, out var ownership) && ownership.RuntimeManagedList;

        public bool IsRuntimeManagedClosureSlot(int slot) =>
            ParamOwnership.TryGetValue(slot, out var ownership) && ownership.RuntimeManagedClosure;

        // Records that `slot` independently licenses runtime-managed representation as `type`
        // (classifier A, at whichever pass currently has the most resolved view of the type available
        // — loop entry, post-body refresh, or a specific back-edge call site). Monotonic like the
        // HashSet.Add calls it replaces: never turns an already-true List/Closure flag back off just
        // because this particular call's type doesn't match that branch.
        public void MarkRuntimeManaged(int slot, TypeRef type)
        {
            if (!ParamOwnership.TryGetValue(slot, out var ownership))
            {
                return;
            }

            if (!ownership.RuntimeManaged)
            {
                _runtimeManagedOrder.Add(slot);
            }

            ownership.RuntimeManaged = true;
            ownership.RuntimeManagedType = type;
            if (type is TypeRef.TList)
            {
                ownership.RuntimeManagedList = true;
            }
            else if (type is TypeRef.TFun)
            {
                if (!ownership.RuntimeManagedClosure)
                {
                    _runtimeManagedClosureOrder.Add(slot);
                }

                ownership.RuntimeManagedClosure = true;
            }
        }

        // The frame-wide profitability veto's demotion: undoes MarkRuntimeManaged for one parameter.
        public void ClearRuntimeManaged(int slot)
        {
            if (ParamOwnership.TryGetValue(slot, out var ownership))
            {
                ownership.RuntimeManaged = false;
                ownership.RuntimeManagedList = false;
                ownership.RuntimeManagedClosure = false;
                ownership.RuntimeManagedType = null;
            }

            _runtimeManagedOrder.Remove(slot);
            _runtimeManagedClosureOrder.Remove(slot);
        }

        public Dictionary<int, int> RuntimeManagedParamActiveSlots { get; } = [];
        public Dictionary<int, int> RuntimeManagedClosureActiveSlots { get; } = [];
        public bool InTailPosition { get; set; }

        // Pre-slot facts stashed at construction time (before ParamSlots is known) and consumed once
        // by BuildParamOwnership below. Loop invariance, whole-list rebuild, growing-cons, and
        // consumed-tail shape are ordinal-keyed because they now come from the binding-identity-aware
        // ownership summary; the fresh-closure category awaiting its own 2.2 cutover remains
        // name-keyed until then. No other consumer reads these transport fields.
        private readonly IReadOnlySet<int> _loopInvariantParamOrdinals;
        private readonly IReadOnlySet<int> _freshRebuiltListParamOrdinals;
        private readonly IReadOnlySet<int> _affineConsListParamOrdinals;
        private readonly IReadOnlySet<int> _consumedListTailParamOrdinals;
        private readonly IReadOnlySet<int> _borrowableConsumedListParamOrdinals;
        private readonly IReadOnlySet<string> _freshClosureParams;
        private readonly IReadOnlySet<string> _affineStrParams;

        // Every affine-string param's own name, in CollectAffineAccumulators's own AST-walk order —
        // exposed directly (not re-derived from ParamOwnership's fixed ParamSlots-order iteration)
        // because the one consumer (reservation-slot setup at loop entry) allocates a fresh pair of
        // locals per name visited, so it must keep visiting them in this collection's original order.
        public IEnumerable<string> AffineStrParamNames => _affineStrParams;

        // Pattern-bound names extracted directly (one pattern level) off a declared TCO parameter
        // whose only appearances elsewhere in the body are NOT limited to (a) the scrutinee of a
        // further nested match on the same name, or (b) the bare, unchanged argument at that same
        // parameter's own slot in a tail self-call. Both of those shapes are already handled without
        // this table's help — (a) by whatever separate protection the nested match's own bindings
        // get, (b) by the ordinary per-parameter back-edge argument installation — so a name limited
        // to just those is left alone. A name with any OTHER appearance (embedded in a
        // returned/constructed value, passed to a different parameter's slot, handed to another
        // function, ...) genuinely escapes the current iteration independently and needs its own
        // protective dup; see ResolvePendingNestedTcoPatternAliasSites, the only consumer. Keyed by
        // the EXTRACTED BINDING's own name (e.g. a pattern-matched "rest", not the parent parameter's
        // own name), a distinct key space from ParamOwnership's per-parameter entries, so this stays a
        // freestanding set rather than a field on TcoParamOwnership. Computed once, structurally, from
        // the pre-lowering AST, so it is available before types are resolved. Empty when not computed
        // (conservative — nothing extra gets protected).
        public IReadOnlySet<string> EscapingDirectPatternBindings { get; }

        private static readonly IReadOnlySet<string> EmptyStaticFacts = new HashSet<string>(System.StringComparer.Ordinal);
        private static readonly IReadOnlySet<int> EmptyParameterOrdinals = new HashSet<int>();

        // Used by the two call sites that build a TcoContext without first running the Collect*
        // family over a real recursive body — a synthesized mutual-recursion dispatch lambda (whose
        // own body gets scanned once IT is lowered through the ordinary path, same as any other
        // recursive binding) and an async helper coroutine's restart loop (whose classifier-A/D
        // passes never run at all — see the _usesAsync/_inCoroutineBody gate on
        // LowerLambdaCoreIdentifyRuntimeManagedTcoParams). Every static fact defaults to false, the
        // same default the twelve original fields' own empty-collection initializers gave both sites.
        public TcoContext(string selfName, int paramCount, List<string> paramNames)
            : this(
                selfName,
                paramCount,
                paramNames,
                EmptyParameterOrdinals,
                EmptyParameterOrdinals,
                EmptyParameterOrdinals,
                EmptyParameterOrdinals,
                EmptyParameterOrdinals,
                EmptyStaticFacts,
                EmptyStaticFacts,
                EmptyStaticFacts)
        {
        }

        public TcoContext(
            string selfName,
            int paramCount,
            List<string> paramNames,
            IReadOnlySet<int> loopInvariantParamOrdinals,
            IReadOnlySet<int> freshRebuiltListParamOrdinals,
            IReadOnlySet<int> affineConsListParamOrdinals,
            IReadOnlySet<int> consumedListTailParamOrdinals,
            IReadOnlySet<int> borrowableConsumedListParamOrdinals,
            IReadOnlySet<string> freshClosureParams,
            IReadOnlySet<string> affineStrParams,
            IReadOnlySet<string> escapingDirectPatternBindings)
        {
            SelfName = selfName;
            ParamCount = paramCount;
            ParamNames = paramNames;
            _loopInvariantParamOrdinals = loopInvariantParamOrdinals;
            _freshRebuiltListParamOrdinals = freshRebuiltListParamOrdinals;
            _affineConsListParamOrdinals = affineConsListParamOrdinals;
            _consumedListTailParamOrdinals = consumedListTailParamOrdinals;
            _borrowableConsumedListParamOrdinals = borrowableConsumedListParamOrdinals;
            _freshClosureParams = freshClosureParams;
            _affineStrParams = affineStrParams;
            EscapingDirectPatternBindings = escapingDirectPatternBindings;
        }

        // Joins ParamNames against ParamSlots (only available once LowerLambdaCoreBindTcoParamSlots
        // has run) with the raw static facts stashed at construction, producing one entry per
        // parameter, keyed by its local slot. Called exactly once, right after ParamSlots is built.
        public void BuildParamOwnership()
        {
            ParamOwnership.Clear();
            int count = ParamNames.Count < ParamSlots.Count ? ParamNames.Count : ParamSlots.Count;
            for (int i = 0; i < count; i++)
            {
                string name = ParamNames[i];
                int slot = ParamSlots[i];
                ParamOwnership[slot] = new TcoParamOwnership
                {
                    ParamName = name,
                    Slot = slot,
                    LoopInvariant = _loopInvariantParamOrdinals.Contains(i),
                    FreshRebuiltList = _freshRebuiltListParamOrdinals.Contains(i),
                    AffineConsList = _affineConsListParamOrdinals.Contains(i),
                    ConsumedListTail = _consumedListTailParamOrdinals.Contains(i),
                    BorrowableConsumedList = _borrowableConsumedListParamOrdinals.Contains(i),
                    FreshClosure = _freshClosureParams.Contains(name),
                    AffineStr = _affineStrParams.Contains(name),
                };
            }
        }

        // True only while we are still descending the recursive binding's curried lambda chain
        // (given a -> given b -> body). The chain's innermost lambda owns the tail-call loop label; a
        // nested let-bound lambda inside the body is a separate frame and must not be mistaken for it.
        public bool DescendingChain { get; set; } = true;

        // Arena watermark for per-iteration reset in TCO loops.
        // Saved right after the loop body label; restored before jumping back
        // when all tail-call arguments are copy types (no heap pointers escape).
        public int ArenaCursorSlot { get; set; } = -1;
        public int ArenaEndSlot { get; set; } = -1;

        // Fixed loop-ENTRY arena watermark, saved ONCE before the loop label (not re-saved per
        // iteration like ArenaCursorSlot). When every threaded accumulator is a non-sharing whole-value
        // type (copy type, resource handle, String, or BigInt) — never a cons-list, whose shared tail
        // must stay below an ADVANCING watermark — the back-edge resets to this fixed mark instead. The
        // per-iteration mark advances past the accumulator each iteration, stranding the previous
        // iteration's whole-value copy below itself forever (the O(N^2) growing-accumulator leak); the
        // fixed mark reclaims that old copy and re-materializes the new one at the same spot, so a
        // growing String/BigInt accumulator stays O(current size) instead of O(sum of sizes).
        public int FixedCursorSlot { get; set; } = -1;
        public int FixedEndSlot { get; set; } = -1;

        // Per affine param: the reservation start/end local slots (zeroed at loop entry, written by
        // ConcatStrTip's fallback, zeroed again by the compaction that reclaims the reservation).
        public Dictionary<string, (int Start, int End)> AffineResvSlots { get; } = new(System.StringComparer.Ordinal);

        // Live accumulator size (cursor - W) recorded after the last fixed-watermark compaction,
        // zero-initialized at loop entry. The back-edge skips the whole copy-out + reset while the
        // arena has grown less than 2x this size (+ slack) since W — so a growing accumulator is
        // copied only when the garbage since the last compaction is at least as large as the live
        // data, making the total copy work LINEAR in bytes allocated (the doubling amortization)
        // instead of one full copy per iteration (O(N^2) time), while memory stays O(live).
        public int CompactionSizeSlot { get; set; } = -1;

        // Stack pointer saved at the loop body label; restored at each back-edge so per-iteration dynamic
        // stack allocations in the loop body are freed instead of accumulating until the stack overflows.
        public int StackPtrSlot { get; set; } = -1;

        // True for the restart loop of an async tail-recursive helper coroutine: the back-edge's arena
        // save/restore/reclaim are emitted with their CoroutineLoop flag set, so the backend can gate
        // them (no-op on the legacy driver; runtime LoopResetOk check under the scheduler). Stack
        // restore stays disabled in this mode — a stack pointer saved before a suspend belongs to a
        // dead C frame.
        public bool CoroutineLoopReset { get; set; }

        // Ownership-scope stack depth at the loop body start. Scopes pushed above this during the
        // iteration hold iteration-local resources that must be closed at the back-edge (else the
        // per-arm Drop becomes dead code after the jump and the resource leaks each iteration).
        public int OwnershipDepthAtEntry { get; set; } = -1;
    }

    private enum IntrinsicKind
    {
        Print,
        Write,
        WriteBytes,
        WriteLine,
        ReadLine,
        FileReadText,
        FileReadAllBytes,
        FileMmap,
        FileOpen,
        FileReadChunk,
        FileReadLine,
        FileClose,
        InternalDeepCopy,
        ParallelBoth,
        ParallelWithWorkers,
        FileWriteText,
        FileExists,
        TextUncons,
        TextParseInt,
        TextParseFloat,
        TextFromInt,
        TextFromFloat,
        TextFormatFloat,
        TextToHex,
        TextAsciiUpper,
        TextAsciiLower,
        RegexCompile,
        RegexCompileError,
        RegexFind,
        RegexCaptures,
        RegexSubstitute,
        BigIntFromInt,
        BigIntToString,
        BigIntToInt,
        BigIntFromString,
        BigIntAdd,
        BigIntSub,
        BigIntMul,
        BigIntDiv,
        BigIntMod,
        BigIntCompare,
        HttpGet,
        HttpPost,
        NetTcpConnect,
        NetTcpSend,
        NetTcpReceive,
        NetTcpClose,
        NetTcpListen,
        NetTcpAccept,
        NetForkWorkers,
        NetSetDrainTimeout,
        NetTlsConnect,
        NetTlsSend,
        NetTlsReceive,
        NetTlsClose,
        NetTlsServerHandshake,
        Panic,
        AsyncRun,
        AsyncTask,
        AsyncFromResult,
        AsyncSleep,
        AsyncAll,
        AsyncSpawn,
        AsyncRace,
        BytesEmpty,
        BytesSingleton,
        BytesLength,
        BytesGet,
        BytesIndexOf,
        BytesCompare,
        BytesScanHash,
        BytesSubText,
        BytesSubView,
        BytesAppend,
        BytesAppendByte,
        BytesFromList,
        BytesFromText,
        BytesHash,
        BytesU16Le,
        BytesU32Le,
        BytesU64Le,
        BytesGetU16Le,
        BytesGetU32Le,
        BytesGetU64Le,
        UIntToInt,
        UIntFromInt,
        MathToFloat,
        MathSqrt,
        MathFloor,
        MathCeil,
        MathRound,
        MathTrunc,
        MathFloorToInt,
        MathRoundToInt,
        MathTruncToInt,
        MathSin,
        MathCos,
        MathTan,
        MathAsin,
        MathAcos,
        MathAtan,
        MathSinh,
        MathCosh,
        MathTanh,
        MathExp,
        MathExpm1,
        MathLn,
        MathLog2,
        MathLog10,
        MathLog1p,
        MathCbrt,
        MathPowF,
        MathAtan2,
        MathHypot,
        MathFmod,
        FileWriteBytes,
        ReadExact,
        ConsoleEnableRaw,
        ConsoleRestore,
        ConsolePoll,
        ConsoleMonotonicMillis,
        TextByteLength,
        SpawnProcess,
        ProcessWriteStdin,
        ProcessReadStdoutLine,
        ProcessReadStderrLine,
        ProcessWaitForExit,
        ProcessKill
    }

    private enum PreludeValueKind
    {
        Args
    }

    // Binding kinds: local slot or captured env index
    private abstract record Binding(TypeRef Type)
    {
        public virtual TextSpan? DefinitionSpan => null;

        public sealed record Local(int Slot, TypeRef T, TextSpan? Span = null) : Binding(T)
        {
            public override TextSpan? DefinitionSpan => Span;
        }

        public sealed record Env(int Index, TypeRef T, TextSpan? Span = null) : Binding(T)
        {
            public override TextSpan? DefinitionSpan => Span;
        }

        public sealed record EnvScheme(int Index, TypeScheme S, TextSpan? Span = null) : Binding(S.Body)
        {
            public override TextSpan? DefinitionSpan => Span;
        }

        public sealed record Self(string FuncLabel, TypeRef T, int EnvSizeBytes, TextSpan? Span = null) : Binding(T)
        {
            public override TextSpan? DefinitionSpan => Span;
        }

        public sealed record Intrinsic(IntrinsicKind Kind, TypeScheme S) : Binding(S.Body);
        public sealed record ExternalFunction(IrExternalFunction Function, TypeRef T) : Binding(T);
        public sealed record PreludeValue(PreludeValueKind Kind, TypeScheme S) : Binding(S.Body);

        public sealed record Scheme(int Slot, TypeScheme S, TextSpan? Span = null) : Binding(S.Body)
        {
            public override TextSpan? DefinitionSpan => Span;
        }
    }

    // --- Ownership tracking ---
    // Why a resource binding stopped being usable. Single source of truth for release state; the
    // OwnershipInfo.IsDropped flag is derived from it. Drives diagnostic selection (ASH006 vs ASH008).
    private enum ResourceReleaseKind
    {
        // Still live and this scope's responsibility.
        None,

        // Released by an explicit close/release call — a later use is use-after-close (ASH006).
        Closed,

        // Ownership transferred (passed to a function/handler/spawn, stored in an aggregate, consumed
        // by a match, or captured by an escaping closure) — a later use is use-after-move (ASH008).
        Moved,

        // Released by a compiler-inserted drop at scope exit or a TCO back-edge. No user-visible
        // transfer point; a later use (should not occur by construction) reports use-after-close.
        AutoDropped,
    }

    // Tracks owned bindings and their drop/borrow state.
    // Key: binding name, Value: ownership info (slot, type name, whether dropped, active borrows).
    // Copy types (Int, Float, Bool) are never tracked.
    // Owned types (String, List, ADTs, Closures, resource types) are tracked.
    private sealed class OwnershipInfo(
        int slot,
        string typeName,
        bool isResource,
        TextSpan? definitionSpan,
        TypeRef? type = null,
        bool isResourceBearing = false,
        bool runtimeManaged = false,
        ConstructorSymbol? runtimeConstructor = null,
        bool runtimeDeepUnique = false,
        IReadOnlySet<int>? excludedDropFieldIndices = null)
    {
        public int Slot { get; } = slot;
        public string TypeName { get; } = typeName;
        public bool IsResource { get; } = isResource;
        public TextSpan? DefinitionSpan { get; } = definitionSpan;

        /// <summary>The binding's pruned type, used for type-directed recursive resource drop.</summary>
        public TypeRef? Type { get; } = type;

        /// <summary>
        /// True if the type is, or transitively contains, a resource type (e.g. Result(_, FileHandle)).
        /// Such an aggregate, if still owned at scope exit, is dropped by walking it for nested resources.
        /// </summary>
        public bool IsResourceBearing { get; } = isResourceBearing;

        public bool RuntimeManaged { get; } = runtimeManaged;

        /// <summary>
        /// Statically known constructor for a directly-bound runtime-managed ADT value. Allows its
        /// root drop to bypass generic tag dispatch; null means the constructor is not proven.
        /// </summary>
        public ConstructorSymbol? RuntimeConstructor { get; } = runtimeConstructor;

        /// <summary>
        /// True while this binding owns a fully fresh runtime-managed tree or list spine. Explicit
        /// sharing clears the fact before drop generation.
        /// </summary>
        public bool RuntimeDeepUnique { get; set; } = runtimeDeepUnique;

        /// <summary>
        /// Constructor field indices this value's own recursive drop must never touch, because a
        /// pattern match already extracted those specific fields into their own independently tracked
        /// bindings — <see cref="EmitConstructorFieldBindings"/> extracts a field by loading its stored
        /// pointer without duplicating it, so the moment a field gets its own binding and its own
        /// <see cref="OwnershipInfo"/> entry, ownership of that one field has already transferred away
        /// from this value; recursing into it here as well, on top of whatever that field's own binding
        /// later does with it, would double-release the same allocation. Null or empty means every
        /// runtime-managed field is still this value's own responsibility, exactly as for any other
        /// tracked value.
        /// </summary>
        public IReadOnlySet<int>? ExcludedDropFieldIndices { get; } = excludedDropFieldIndices;

        /// <summary>
        /// True once this resource (or resource-bearing) binding has been captured by a closure. The
        /// closure may escape the owning scope directly, nested in an aggregate, or through a chain of
        /// other closures — routes the type cannot reveal (the escaping value's type hides the
        /// resource). Rather than prove non-escape, the scope transfers ownership to the closure at
        /// exit instead of closing the resource underneath a value that still references it (which
        /// would be a use-after-close). The direct-result-closure case is handled earlier by
        /// <see cref="SkipDropsForResourcesEscapingViaResult"/> and keeps its deterministic dropper.
        /// </summary>
        public bool CapturedByClosure { get; set; }

        /// <summary>
        /// Why this owned value stopped being usable by this binding, and the single source of truth
        /// for whether it has been released. Distinguishes an explicit <c>close</c>
        /// (<see cref="ResourceReleaseKind.Closed"/>) from an ownership transfer
        /// (<see cref="ResourceReleaseKind.Moved"/>) and a compiler scope-exit drop
        /// (<see cref="ResourceReleaseKind.AutoDropped"/>), so a subsequent use is reported as
        /// use-after-close (ASH006) or use-after-move (ASH008) as appropriate. Only the reason is
        /// resource-specific; the derived <see cref="IsDropped"/> flag applies to every owned value.
        /// </summary>
        public ResourceReleaseKind ReleaseKind { get; set; }

        /// <summary>
        /// True once this value has been released by any means (closed, moved, or scope-exit dropped).
        /// Derived from <see cref="ReleaseKind"/> — there is no independent dropped flag to keep in
        /// sync. A live value is <see cref="ResourceReleaseKind.None"/>.
        /// </summary>
        public bool IsDropped => ReleaseKind != ResourceReleaseKind.None;

        /// <summary>
        /// Number of live borrows of this value. The compiler infers borrows when
        /// an owned value is used without consuming ownership. By scope structure,
        /// all borrows are consumed before the owning scope exits and emits Drop —
        /// this count is informational for future optimization passes.
        /// </summary>
        public int ActiveBorrows { get; set; }
    }

    private sealed record RuntimeReuseCleanup(
        TypeRef.TNamedType Type,
        ConstructorSymbol Constructor,
        IReadOnlyDictionary<string, int> TransferableFields);

    private sealed record ReuseToken(
        int Temp,
        int FieldCount,
        RuntimeReuseCleanup? RuntimeCleanup,
        bool ListCell = false)
    {
        public bool RuntimeManaged => RuntimeCleanup is not null;
    }

    /// <summary>
    /// Describes the kind of arena copy-out to emit for a given result type.
    /// </summary>
    private enum CopyOutKind
    {
        /// <summary>Not eligible for copy-out.</summary>
        None,
        /// <summary>Shallow memcpy of a fixed or dynamic-size object (String, ADT, single cons cell).</summary>
        Shallow,
        /// <summary>Deep cons-chain walk for lists.</summary>
        List,
        /// <summary>TCO-specific: copy one cons cell + copy/deep-copy its head value.</summary>
        TcoListCell,
        /// <summary>Recursive deep copy of a pointer-bearing ADT (fields deep-copied) via a synthesized
        /// copier. Self-contained result, so a fixed-shape ADT accumulator can reset.</summary>
        DeepAdt,
    }
}
