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

    // TCO (tail call optimization) state
    private sealed class TcoContext
    {
        public string SelfName { get; init; } = "";
        public string BodyLabel { get; set; } = "";
        public int ParamCount { get; init; }
        public List<string> ParamNames { get; init; } = [];
        public List<int> ParamSlots { get; init; } = [];
        public bool InTailPosition { get; set; }

        // Params passed as their own unchanged Var at EVERY tail self-call — loop-invariant, so they
        // never hold a value allocated inside the loop and always point below the arena watermark. A
        // plain per-iteration reset therefore leaves them valid, even when they are heap types (e.g. a
        // Bytes threaded unchanged through a fold). Empty when not computed (conservative).
        public HashSet<string> LoopInvariantParams { get; init; } = new(System.StringComparer.Ordinal);

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

        // Params proven AFFINE across the loop (consumed at most once along every loop-continuing
        // path, and only as the leftmost leaf of the `+` chain producing their own tail-call
        // argument). The affine property guarantees the loop holds no other reference, licensing
        // in-place reservation growth (ConcatStrTip). Empty when not computed (conservative).
        public HashSet<string> AffineStrParams { get; init; } = new(System.StringComparer.Ordinal);

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
    // Tracks owned bindings and their drop/borrow state.
    // Key: binding name, Value: ownership info (slot, type name, whether dropped, active borrows).
    // Copy types (Int, Float, Bool) are never tracked.
    // Owned types (String, List, ADTs, Closures, resource types) are tracked.
    private sealed class OwnershipInfo(int slot, string typeName, bool isResource, TextSpan? definitionSpan, TypeRef? type = null, bool isResourceBearing = false)
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

        public bool IsDropped { get; set; }
        /// <summary>
        /// Number of live borrows of this value. The compiler infers borrows when
        /// an owned value is used without consuming ownership. By scope structure,
        /// all borrows are consumed before the owning scope exits and emits Drop —
        /// this count is informational for future optimization passes.
        /// </summary>
        public int ActiveBorrows { get; set; }
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
        /// <summary>Closure struct + env copy.</summary>
        Closure,
        /// <summary>TCO-specific: copy one cons cell + copy/deep-copy its head value.</summary>
        TcoListCell,
        /// <summary>Recursive deep copy of a pointer-bearing ADT (fields deep-copied) via a synthesized
        /// copier. Self-contained result, so a fixed-shape ADT accumulator can reset.</summary>
        DeepAdt,
    }
}
