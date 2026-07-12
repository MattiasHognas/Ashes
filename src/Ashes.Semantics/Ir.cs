namespace Ashes.Semantics;

// Named type variable used in type scheme quantifiers (forall a. body).
// Id is the original TVar ID used for instantiation; Name is kept for display.
public sealed record TypeVar(int Id, string Name);

// Type scheme: forall [Quantified]. Body (polytype representation for let-polymorphism).
public sealed record TypeScheme(IReadOnlyList<TypeVar> Quantified, TypeRef Body);

public abstract record TypeRef
{
    public sealed record TInt : TypeRef;
    // Unsigned integer: Bits ∈ {8, 16, 32, 64}. Values are stored as i64 internally
    // but wrap at their declared bit width for arithmetic, matching C unsigned semantics.
    public sealed record TUInt(int Bits) : TypeRef;
    public sealed record TFloat : TypeRef;
    // Arbitrary-precision signed integer. Native heap value:
    // pointer to { i64 header = (negFlag<<32)|limbCount, i64 limb[...] }, sign-magnitude, base 2^64,
    // normalized (zero = header 0, no limbs). Immutable; each op allocates a fresh value. The
    // arithmetic is emitted as LLVM-IR runtime helpers by the backend, on demand.
    public sealed record TBigInt : TypeRef;
    public sealed record TStr : TypeRef;
    // Immutable byte buffer: layout is identical to TStr → {length:i64, data:u8[length]}.
    public sealed record TBytes : TypeRef;
    public sealed record TBool : TypeRef;
    public sealed record TNever : TypeRef;
    public sealed record TList(TypeRef Element) : TypeRef;
    public sealed record TTuple(IReadOnlyList<TypeRef> Elements) : TypeRef;
    public sealed record TFun(TypeRef Arg, TypeRef Ret) : TypeRef
    {
        /// <summary>
        /// The arrow's capability row: a <see cref="TRow"/> (or a <see cref="TVar"/> row variable), or
        /// null for the pure closed empty row. Kept as an init-only property so the ubiquitous
        /// two-argument construction stays pure by default.
        /// </summary>
        public TypeRef? Row { get; init; }
    }

    public sealed record TVar(int Id) : TypeRef;

    /// <summary>
    /// One capability instance inside a row: the declared capability plus its type arguments
    /// (e.g. <c>Clock</c> or <c>State(Int)</c>). Only ever appears inside <see cref="TRow"/>.
    /// </summary>
    public sealed record TCapability(CapabilitySymbol Symbol, IReadOnlyList<TypeRef> Args) : TypeRef;

    /// <summary>
    /// An capability row: a set of capabilities plus a tail. <see cref="Tail"/> is a <see cref="TVar"/>
    /// row variable (open row), another <see cref="TRow"/> produced by substitution (flattened on
    /// normalization), or null (closed row).
    /// </summary>
    public sealed record TRow(IReadOnlyList<TCapability> Capabilities, TypeRef? Tail) : TypeRef;
    public sealed record TNamedType(TypeSymbol Symbol, IReadOnlyList<TypeRef> TypeArgs) : TypeRef;
    public sealed record TTypeParam(TypeParameterSymbol Symbol) : TypeRef;
    public sealed record TOpaque(string Name) : TypeRef;
    public sealed record TPtr(TypeRef Pointee) : TypeRef;
}

public readonly record struct SourceLocation(string FilePath, int Line, int Column);

public abstract record IrInst
{
    /// <summary>
    /// Optional source location for debug info emission (DWARF).
    /// Init-only so that Location is set once (via <c>with</c>) before the
    /// instruction is added to the IR list, keeping record equality stable.
    /// </summary>
    public SourceLocation? Location { get; init; }

    public sealed record LoadConstInt(int Target, long Value) : IrInst;
    public sealed record LoadConstFloat(int Target, double Value) : IrInst;
    public sealed record LoadConstBool(int Target, bool Value) : IrInst;
    public sealed record LoadConstStr(int Target, string StrLabel) : IrInst;
    public sealed record LoadProgramArgs(int Target) : IrInst;

    public sealed record LoadLocal(int Target, int Slot) : IrInst;
    public sealed record StoreLocal(int Slot, int Source) : IrInst;

    public sealed record LoadEnv(int Target, int Index) : IrInst; // uses env ptr implicit in function
    public sealed record StoreMemOffset(int BasePtr, int OffsetBytes, int Source) : IrInst; // [base+off]=src
    public sealed record LoadMemOffset(int Target, int BasePtr, int OffsetBytes) : IrInst;  // tgt=[base+off]

    // DeferredType is non-null only for a provisional '+' whose operand type was still unresolved at
    // lowering time; ResolveDeferredAdds patches such adds to ConcatStr/AddFloat (or a plain AddInt)
    // once inference finishes. It is carried on the record so the TCO `with`-based remap preserves it.
    public sealed record AddInt(int Target, int Left, int Right, TypeRef? DeferredType = null) : IrInst;
    public sealed record SubInt(int Target, int Left, int Right) : IrInst;
    // DeferredType mirrors AddInt: non-null only for a provisional '*' whose operand type was still
    // unresolved at lowering time; ResolveDeferredMuls patches such muls to MulFloat / BigIntBinary
    // (or a plain MulInt) once inference finishes.
    public sealed record MulInt(int Target, int Left, int Right, TypeRef? DeferredType = null) : IrInst;
    public sealed record DivInt(int Target, int Left, int Right) : IrInst;
    public sealed record DivUInt(int Target, int Left, int Right) : IrInst;
    public sealed record AndInt(int Target, int Left, int Right) : IrInst;
    public sealed record OrInt(int Target, int Left, int Right) : IrInst;
    public sealed record XorInt(int Target, int Left, int Right) : IrInst;
    public sealed record ShlInt(int Target, int Left, int Right) : IrInst;
    public sealed record ShrInt(int Target, int Left, int Right) : IrInst;
    public sealed record AddFloat(int Target, int Left, int Right) : IrInst;
    public sealed record SubFloat(int Target, int Left, int Right) : IrInst;
    public sealed record MulFloat(int Target, int Left, int Right) : IrInst;
    public sealed record DivFloat(int Target, int Left, int Right) : IrInst;
    public sealed record CmpIntGt(int Target, int Left, int Right) : IrInst;
    public sealed record CmpIntGe(int Target, int Left, int Right) : IrInst;
    public sealed record CmpIntLt(int Target, int Left, int Right) : IrInst;
    public sealed record CmpIntLe(int Target, int Left, int Right) : IrInst;
    public sealed record CmpUIntGt(int Target, int Left, int Right) : IrInst;
    public sealed record CmpUIntGe(int Target, int Left, int Right) : IrInst;
    public sealed record CmpUIntLt(int Target, int Left, int Right) : IrInst;
    public sealed record CmpUIntLe(int Target, int Left, int Right) : IrInst;
    // DeferredType is non-null only for a provisional '==' / '!=' whose operand type was still
    // unresolved at lowering time; ResolveDeferredEqs patches such comparisons to CmpStrEq/CmpStrNe
    // or CmpFloatEq/CmpFloatNe (or leaves a plain CmpIntEq/CmpIntNe) once inference finishes.
    public sealed record CmpIntEq(int Target, int Left, int Right, TypeRef? DeferredType = null) : IrInst;
    public sealed record CmpIntNe(int Target, int Left, int Right, TypeRef? DeferredType = null) : IrInst;
    public sealed record CmpFloatGt(int Target, int Left, int Right) : IrInst;
    public sealed record CmpFloatGe(int Target, int Left, int Right) : IrInst;
    public sealed record CmpFloatLt(int Target, int Left, int Right) : IrInst;
    public sealed record CmpFloatLe(int Target, int Left, int Right) : IrInst;
    public sealed record CmpFloatEq(int Target, int Left, int Right) : IrInst;
    public sealed record CmpFloatNe(int Target, int Left, int Right) : IrInst;

    // Ashes.Math numeric conversions and Float unary intrinsics (Layer 1).
    // IntToFloat is sitofp; FloatToInt is fptosi (truncates toward zero). FloatUnaryIntrinsic
    // lowers to a call to the named LLVM math intrinsic (e.g. "llvm.sqrt.f64").
    public sealed record IntToFloat(int Target, int ValueTemp) : IrInst;
    public sealed record FloatToInt(int Target, int ValueTemp) : IrInst;
    public sealed record FloatUnaryIntrinsic(int Target, int ValueTemp, string LlvmIntrinsic) : IrInst;

    // Ashes.Math Layer-2 transcendental: a call to an openlibm symbol (e.g. "sin", "pow"). All
    // arguments and the result are Float (f64). The openlibm bitcode is linked into the module when
    // the program uses any of these (ProgramUsesMathRuntimeAbi), so the symbol resolves internally.
    public sealed record CallLibm(int Target, string Symbol, IReadOnlyList<int> Args) : IrInst;

    // Ashes.BigInt operations, backed by emitted LLVM-IR runtime helpers.
    // BigInt values are heap pointers (i64). The codegen pre-sizes result buffers from operand limb
    // counts and calls the allocation-free C helpers.
    public sealed record BigIntFromInt(int Target, int ValueTemp) : IrInst;      // Int -> BigInt
    public sealed record BigIntToString(int Target, int ValueTemp) : IrInst;     // BigInt -> Str
    public sealed record BigIntToInt(int Target, int ValueTemp) : IrInst;        // BigInt -> Result(Str, Int)
    public sealed record BigIntFromString(int Target, int ValueTemp) : IrInst;   // Str -> Result(Str, BigInt)
    // Op ∈ { "add", "sub", "mul", "div", "mod" }: BigInt -> BigInt -> BigInt.
    public sealed record BigIntBinary(int Target, int Left, int Right, string Op) : IrInst;
    public sealed record BigIntCompare(int Target, int Left, int Right) : IrInst; // BigInt -> BigInt -> Int

    public sealed record CmpStrEq(int Target, int Left, int Right) : IrInst;
    public sealed record CmpStrNe(int Target, int Left, int Right) : IrInst;
    public sealed record ConcatStr(int Target, int Left, int Right) : IrInst;

    // Ashes.Regex (PCRE2) intrinsics. The 8-bit PCRE2 bitcode is linked into the module when the
    // program uses any of these (ProgramUsesRegexRuntimeAbi), so the pcre2_* symbols resolve
    // internally. A compiled pattern (pcre2_code*) lives in a persistent mmap-backed region that the
    // arena never relocates, so a Regex value is a stable i64 handle to it. Per-match scratch is
    // bracketed by a region save/restore inside the match/substitute emitters, keeping streaming
    // matches memory-bounded. PCRE2's malloc/free route to that region; memcpy/memset are the
    // module's own builtins.
    public sealed record RegexCompile(int Target, int Pattern) : IrInst;          // Str -> Int (pcre2_code*, 0 on error)
    public sealed record RegexCompileError(int Target, int Pattern) : IrInst;     // Str -> Str ("" if valid, else message)
    public sealed record RegexFind(int Target, int Code, int Subject, int Start) : IrInst;      // -> Option((Int, Int))
    public sealed record RegexCaptures(int Target, int Code, int Subject, int Start) : IrInst;  // -> Option(List(Option(Str)))
    public sealed record RegexSubstitute(int Target, int Code, int Subject, int Replacement) : IrInst; // -> Str

    public sealed record MakeClosure(int Target, string FuncLabel, int EnvPtrTemp, int EnvSizeBytes) : IrInst; // alloc 32 bytes: {code, env, env_size, dropper}
    public sealed record MakeClosureStack(int Target, string FuncLabel, int EnvPtrTemp, int EnvSizeBytes) : IrInst; // stack alloc 32 bytes: {code, env, env_size, dropper}

    /// <summary>Loads the address of a lifted function as an i64. Used to store a resource dropper
    /// into an escaping closure's dropper slot, so a resource a closure captured is closed
    /// deterministically when the closure is dropped.</summary>
    public sealed record LoadFuncAddr(int Target, string FuncLabel) : IrInst;
    public sealed record CallClosure(int Target, int ClosureTemp, int ArgTemp) : IrInst;
    // Devirtualized closure call: the callee label is statically known (the closure temp was
    // produced by a MakeClosure with this label), so codegen emits a direct call the LLVM
    // inliner can see through. Produced only by IrOptimizer.DevirtualizeKnownClosureCalls.
    public sealed record CallKnown(int Target, string FuncLabel, int EnvTemp, int ArgTemp) : IrInst;

    public sealed record Alloc(int Target, int SizeBytes) : IrInst;
    public sealed record AllocStack(int Target, int SizeBytes) : IrInst;

    // ADT heap cell: layout is [tag:i64, field0:u64, field1:u64, ..., fieldN:u64]
    // AllocAdt allocates (1 + FieldCount) * 8 bytes and stores Tag at offset 0.
    public sealed record AllocAdt(int Target, int Tag, int FieldCount) : IrInst;
    public sealed record AllocAdtStack(int Target, int Tag, int FieldCount) : IrInst;

    /// <summary>
    /// Like <see cref="AllocAdt"/> but allocates in the persistent "to-space" arena instead of the
    /// main per-iteration arena. Emitted for a genuinely-new cell (no reuse token) inside an in-place
    /// reuse specialization — e.g. the fresh node a <c>Map.set</c> creates for a new key. The main
    /// arena's TCO back-edge reset never reclaims to-space, so the new cell survives the reset while the
    /// reset still reclaims the iteration's scaffolding/scratch. To-space is never reset during the loop
    /// (it holds part of the live accumulator); it is bounded by the number of genuinely-new cells
    /// (≈distinct keys), not by iterations. See <see cref="IrInst.AllocAdt"/>.
    /// </summary>
    public sealed record AllocAdtToSpace(int Target, int Tag, int FieldCount) : IrInst;

    /// <summary>
    /// In-place reuse: writes <c>Tag</c> into the cell at <c>TokenTemp</c>'s address and yields that
    /// address as <c>Target</c>, instead of bump-allocating. Emitted only when the token is a
    /// provably-dead, uniquely-owned ADT cell of the same size (1 + FieldCount words) — e.g. the node
    /// a linear TCO accumulator was just deconstructed from. The fields are written afterwards exactly
    /// like <see cref="AllocAdt"/>.
    /// </summary>
    public sealed record AllocReusing(int Target, int Tag, int FieldCount, int TokenTemp) : IrInst;
    // SetAdtField: *(Ptr + 8 + FieldIndex*8) = Source
    public sealed record SetAdtField(int Ptr, int FieldIndex, int Source) : IrInst;
    // Save the current stack pointer into a local slot at a TCO loop header; RestoreStackPointer resets to
    // it at each back-edge so dynamic stack allocations in the loop body (e.g. per-iteration string/syscall
    // scratch buffers) are freed every iteration instead of accumulating until the stack overflows.
    public sealed record SaveStackPointer(int Slot) : IrInst;
    public sealed record RestoreStackPointer(int Slot) : IrInst;
    // GetAdtTag: Target = *(Ptr + 0)
    public sealed record GetAdtTag(int Target, int Ptr) : IrInst;
    // GetAdtField: Target = *(Ptr + 8 + FieldIndex*8)
    public sealed record GetAdtField(int Target, int Ptr, int FieldIndex) : IrInst;

    public sealed record PrintInt(int Source) : IrInst;
    public sealed record PrintStr(int Source) : IrInst;
    public sealed record PrintBool(int Source) : IrInst;
    public sealed record WriteStr(int Source) : IrInst;
    public sealed record ReadLine(int Target) : IrInst;
    public sealed record ReadExact(int Target, int CountTemp) : IrInst;
    public sealed record TextByteLength(int Target, int TextTemp) : IrInst;
    public sealed record FileReadText(int Target, int PathTemp) : IrInst;

    public sealed record FileReadAllBytes(int Target, int PathTemp) : IrInst;

    public sealed record FileMmap(int Target, int PathTemp) : IrInst;
    public sealed record FileWriteText(int Target, int PathTemp, int TextTemp) : IrInst;
    public sealed record FileExists(int Target, int PathTemp) : IrInst;
    public sealed record FileOpen(int Target, int PathTemp) : IrInst;
    public sealed record FileReadChunk(int Target, int HandleTemp, int CountTemp) : IrInst;
    public sealed record FileReadLine(int Target, int HandleTemp) : IrInst;
    public sealed record FileClose(int Target, int HandleTemp) : IrInst;
    public sealed record TextUncons(int Target, int TextTemp) : IrInst;
    public sealed record TextParseInt(int Target, int TextTemp) : IrInst;
    public sealed record TextParseFloat(int Target, int TextTemp) : IrInst;
    public sealed record TextFromInt(int Target, int ValueTemp) : IrInst;
    public sealed record TextFromFloat(int Target, int ValueTemp) : IrInst;
    public sealed record TextFormatFloat(int Target, int ValueTemp, int DecimalsTemp) : IrInst;
    public sealed record TextToHex(int Target, int ValueTemp) : IrInst;
    // ASCII-only case map (a-z <-> A-Z by flipping bit 0x20); multibyte UTF-8 (>= 0x80) untouched.
    public sealed record TextAsciiCase(int Target, int SourceTemp, bool Upper) : IrInst;
    public sealed record HttpGet(int Target, int UrlTemp) : IrInst;
    public sealed record HttpPost(int Target, int UrlTemp, int BodyTemp) : IrInst;
    public sealed record NetTcpConnect(int Target, int HostTemp, int PortTemp) : IrInst;
    public sealed record NetTcpSend(int Target, int SocketTemp, int TextTemp) : IrInst;
    public sealed record NetTcpReceive(int Target, int SocketTemp, int MaxBytesTemp) : IrInst;
    public sealed record NetTcpClose(int Target, int SocketTemp) : IrInst;
    public sealed record NetTcpListen(int Target, int PortTemp) : IrInst;
    public sealed record NetTcpAccept(int Target, int SocketTemp) : IrInst;

    // Ashes.Bytes operations.  TBytes layout: {length:i64, data:u8[length]} — identical to TStr.
    public sealed record BytesEmpty(int Target) : IrInst;
    public sealed record BytesSingleton(int Target, int ByteTemp) : IrInst;
    public sealed record BytesLength(int Target, int BytesTemp) : IrInst;
    public sealed record BytesGet(int Target, int BytesTemp, int IndexTemp) : IrInst;
    public sealed record BytesIndexOf(int Target, int BytesTemp, int NeedleTemp, int FromTemp) : IrInst;
    public sealed record BytesCompare(int Target, int LeftTemp, int RightTemp) : IrInst;
    public sealed record BytesScanHash(int Target, int BytesTemp, int NeedleTemp, int FromTemp) : IrInst;
    public sealed record BytesSubText(int Target, int BytesTemp, int StartTemp, int LenTemp) : IrInst;
    public sealed record BytesSubView(int Target, int BytesTemp, int StartTemp, int LenTemp) : IrInst;
    public sealed record BytesAppend(int Target, int LeftTemp, int RightTemp) : IrInst;
    public sealed record BytesAppendByte(int Target, int BytesTemp, int ByteTemp) : IrInst;
    public sealed record BytesFromList(int Target, int ListTemp) : IrInst;
    public sealed record BytesHash(int Target, int BytesTemp) : IrInst;
    public sealed record BytesU16Le(int Target, int ValueTemp) : IrInst;
    public sealed record BytesU32Le(int Target, int ValueTemp) : IrInst;
    public sealed record BytesU64Le(int Target, int ValueTemp) : IrInst;
    public sealed record BytesGetU16Le(int Target, int BytesTemp, int OffsetTemp) : IrInst;
    public sealed record BytesGetU32Le(int Target, int BytesTemp, int OffsetTemp) : IrInst;
    public sealed record BytesGetU64Le(int Target, int BytesTemp, int OffsetTemp) : IrInst;
    public sealed record FileWriteBytes(int Target, int PathTemp, int BytesTemp) : IrInst;

    // Ashes.Process operations. ProcessRef is a pointer to {stdin_fd, stdout_fd, stderr_fd, pid} (32 bytes).
    public sealed record SpawnProcess(int Target, int ExeTemp, int ArgsTemp) : IrInst;
    public sealed record ProcessWriteStdin(int Target, int ProcessTemp, int TextTemp) : IrInst;
    public sealed record ProcessReadStdoutLine(int Target, int ProcessTemp) : IrInst;
    public sealed record ProcessReadStderrLine(int Target, int ProcessTemp) : IrInst;
    public sealed record ProcessWaitForExit(int Target, int ProcessTemp) : IrInst;
    public sealed record ProcessKill(int Target, int ProcessTemp) : IrInst;

    /// <summary>
    /// Drop instruction for deterministic cleanup of owned values.
    /// Emitted by the compiler at scope exit for owned bindings.
    /// SourceTemp is the temp holding the owned value to clean up.
    /// For resource types (Socket), routes to platform-specific cleanup.
    /// For other owned types (String, List, ADTs, Closures), a no-op in
    /// the current bump allocator — actual deallocation is handled by
    /// RestoreArenaState which resets the heap cursor for copy-type scopes.
    /// </summary>
    public sealed record Drop(int SourceTemp, string TypeName) : IrInst;

    /// <summary>
    /// Borrow instruction for compiler-inferred borrowing.
    /// Produces a non-owning reference to the owned value held in SourceTemp.
    /// The borrowed reference carries no drop responsibility — the owning scope
    /// still drops the original.
    /// In the current linear allocator this is a simple value copy (pointer pass-through).
    /// </summary>
    public sealed record Borrow(int Target, int SourceTemp) : IrInst;

    /// <summary>
    /// Saves the current heap allocator state (cursor and end pointers) into two
    /// local slots. Emitted at ownership scope entry so that arena-based
    /// deallocation can restore the cursor at scope exit.
    /// </summary>
    /// <remarks>
    /// <see cref="CoroutineLoop"/> marks the save/restore/reclaim group emitted at an async
    /// tail-recursive loop's restart back-edge (inside a coroutine). The backend gates such a group:
    /// it is a no-op under the legacy task driver, and under the run-queue scheduler the restore and
    /// reclaim run only while the task's <c>LoopResetOk</c> header flag is set (cleared when a
    /// composite ancestor shares the arena, where a stale-watermark reset could free a sibling's
    /// live allocations).
    /// </remarks>
    public sealed record SaveArenaState(int CursorLocalSlot, int EndLocalSlot) : IrInst
    {
        public bool CoroutineLoop { get; init; }
    }

    /// <summary>
    /// Restores the heap allocator state (cursor and end pointers) from two local
    /// slots previously saved by <see cref="SaveArenaState"/>. Resets the bump
    /// pointer to the saved watermark, but does NOT free OS chunks — that is handled
    /// separately by <see cref="ReclaimArenaChunks"/>.
    ///
    /// <para>
    /// Before resetting, the current heap end pointer is saved to
    /// <see cref="PreRestoreEndSlot"/> so that a subsequent
    /// <see cref="ReclaimArenaChunks"/> can determine which chunks to free.
    /// </para>
    /// </summary>
    public sealed record RestoreArenaState(int CursorLocalSlot, int EndLocalSlot, int PreRestoreEndSlot) : IrInst
    {
        /// <summary>See <see cref="SaveArenaState.CoroutineLoop"/>.</summary>
        public bool CoroutineLoop { get; init; }
    }

    /// <summary>
    /// Frees OS chunks that were allocated between the saved watermark and the
    /// pre-restore heap state. Emitted AFTER <see cref="RestoreArenaState"/> and
    /// any <see cref="CopyOutArena"/> instructions, ensuring that copy-out reads
    /// complete before source chunks are unmapped.
    ///
    /// <para>
    /// Compares the saved end (<see cref="SavedEndSlot"/>) with the pre-restore end
    /// (<see cref="PreRestoreEndSlot"/>). If they differ, walks the chunk linked
    /// list from the pre-restore chunk back to the saved chunk, calling
    /// <c>munmap</c> (Linux) or <c>VirtualFree</c> (Windows) on each intermediate chunk.
    /// </para>
    /// </summary>
    public sealed record ReclaimArenaChunks(int SavedEndSlot, int PreRestoreEndSlot) : IrInst
    {
        /// <summary>See <see cref="SaveArenaState.CoroutineLoop"/>.</summary>
        public bool CoroutineLoop { get; init; }
    }

    /// <summary>
    /// Copies a heap object out of the arena to a fresh allocation, emitted AFTER
    /// <see cref="RestoreArenaState"/> but BEFORE <see cref="ReclaimArenaChunks"/>.
    /// The arena cursor has been reset to the scope-entry watermark W, but OS chunks
    /// have not yet been freed, so the source bytes at <paramref name="SrcTemp"/>
    /// are still physically readable. The copy is allocated starting from the reset
    /// cursor (at or below the source address), so a forward memcpy is always safe:
    /// dest ≤ src, no destructive overlap.
    ///
    /// <para>
    /// <b>String (<see cref="StaticSizeBytes"/> == -1):</b>
    /// The total size is read at runtime from the source object's length field
    /// (8 bytes at offset 0): <c>total = 8 + *src</c>. Allocates that many bytes
    /// and memcpy's the entire string (length word + inline bytes).
    /// </para>
    /// <para>
    /// <b>BigInt (<see cref="StaticSizeBytes"/> == <see cref="CopyOutArena.BigIntSize"/>):</b>
    /// The total size is read at runtime from the source object's header limb count
    /// (<c>total = 8 + (header &amp; 0xFFFFFFFF) * 8</c>). A BigInt is a self-contained
    /// <c>{ header, limb… }</c> buffer with no internal pointers, so a flat memcpy of the
    /// normalized prefix is a valid independent value — this is what lets a BigInt accumulator
    /// survive the TCO back-edge arena reset so the per-iteration BigInt garbage is reclaimed.
    /// </para>
    /// <para>
    /// <b>Fixed-size objects (<see cref="StaticSizeBytes"/> &gt; 0):</b>
    /// Allocates exactly <see cref="StaticSizeBytes"/> bytes and memcpy's them.
    /// Used for cons cells (16 bytes: head + tail) when the head is a copy type,
    /// ensuring the tail pointer to a pre-watermark cell is preserved.
    /// </para>
    /// </summary>
    public sealed record CopyOutArena(int DestTemp, int SrcTemp, int StaticSizeBytes) : IrInst
    {
        /// <summary>Sentinel <see cref="StaticSizeBytes"/> selecting the BigInt copy mode (size from
        /// the header limb count). Distinct from -1 (string, size from the length word).</summary>
        public const int BigIntSize = -2;
    }

    /// <summary>
    /// Like <see cref="CopyOutArena"/> but the fresh copy is allocated in the persistent to-space
    /// (see <see cref="AllocAdtToSpace"/>) instead of the main arena. Used to materialize a heap-typed
    /// value (e.g. a String map key) that an in-place reuse specialization stores into the accumulator,
    /// so it survives the loop's per-iteration reset alongside the reused/to-space node.
    /// </summary>
    public sealed record CopyOutArenaToSpace(int DestTemp, int SrcTemp, int StaticSizeBytes) : IrInst;
    // In-place value-cell reuse: memcpy SizeBytes from SrcTemp into the (already-persistent, same-size) cell
    // at DestTemp. Used on the reuse/update path so a fresh fixed-size heap value (e.g. a Map tuple value)
    // overwrites the superseded value's blob cell instead of allocating a new one — keeps the blob bounded.
    public sealed record CopyFixedInto(int DestTemp, int SrcTemp, int SizeBytes) : IrInst;

    /// <summary>
    /// Describes how head values are handled during deep list copy-out.
    /// </summary>
    public enum ListHeadCopyKind
    {
        /// <summary>Head is an inline copy-type value (Int, Float, Bool). No head copy needed.</summary>
        Inline = 0,
        /// <summary>Head is a string pointer. Each string is dynamically copied (8 + length bytes).</summary>
        String = 1,
        /// <summary>Head is an inner list pointer (with copy-type elements). Each inner list is deep-copied.</summary>
        InnerList = 2,
    }

    /// <summary>
    /// Deep-copies an entire cons-cell chain out of the arena to fresh allocations.
    /// Each cons cell is 16 bytes: {head:i64, tail:i64}. The copy walks the tail
    /// pointers, allocating and copying each cell until a nil (0) tail is reached.
    /// <para>
    /// The <see cref="HeadCopy"/> parameter controls how head values are handled:
    /// <list type="bullet">
    ///   <item><b>Inline:</b> Head is a copy-type value stored directly in the cell's
    ///     head word. No additional copy needed.</item>
    ///   <item><b>String:</b> Head is a pointer to a string ({length, bytes}) that also
    ///     resides in arena memory. Each string is copied to a fresh allocation.</item>
    ///   <item><b>InnerList:</b> Head is a pointer to an inner cons-cell chain (with
    ///     copy-type elements). Each inner list is deep-copied recursively.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Emitted AFTER <see cref="RestoreArenaState"/> and BEFORE
    /// <see cref="ReclaimArenaChunks"/>, so old arena chunks are still readable.
    /// </para>
    /// </summary>
    public sealed record CopyOutList(int DestTemp, int SrcTemp, ListHeadCopyKind HeadCopy = ListHeadCopyKind.Inline) : IrInst;

    /// <summary>
    /// Copies a closure (24 bytes: {code:i64, env:i64, env_size:i64}) and its
    /// environment out of the arena to a fresh allocation. Reads the env_size field
    /// from the source closure at offset 16, allocates env_size bytes for the env
    /// copy, then allocates 24 bytes for the closure copy. Relinks the env pointer
    /// in the new closure to point to the new env copy.
    /// <para>
    /// If the env pointer is 0 (no captures), only the 24-byte closure struct is
    /// copied (no env allocation needed).
    /// </para>
    /// <para>
    /// Emitted AFTER <see cref="RestoreArenaState"/> and BEFORE
    /// <see cref="ReclaimArenaChunks"/>, so old arena chunks are still readable.
    /// </para>
    /// </summary>
    public sealed record CopyOutClosure(int DestTemp, int SrcTemp) : IrInst;

    /// <summary>
    /// TCO-specific: copies a single cons cell (16 bytes) out of the arena and also
    /// copies the head value according to <see cref="HeadCopy"/>. Used for TCO
    /// accumulators of type <c>TList(TStr)</c> or <c>TList(TList(copy-type))</c>.
    /// <para>
    /// Only the top cons cell (created in the current TCO iteration) needs copying —
    /// the tail pointer already references cells from previous iterations that are
    /// safely below the arena watermark.
    /// </para>
    /// <para>
    /// <b>String head (<see cref="ListHeadCopyKind.String"/>):</b> Copies the string
    /// ({length, bytes}) to a fresh allocation, then copies the 16-byte cons cell
    /// and updates its head field to point to the new string.
    /// </para>
    /// <para>
    /// <b>InnerList head (<see cref="ListHeadCopyKind.InnerList"/>):</b> Deep-copies
    /// the inner cons-cell chain (with copy-type elements) to fresh allocations, then
    /// copies the 16-byte outer cons cell and updates its head to point to the new chain.
    /// </para>
    /// <para>
    /// Emitted AFTER <see cref="RestoreArenaState"/> and BEFORE
    /// <see cref="ReclaimArenaChunks"/>, so old arena chunks are still readable.
    /// </para>
    /// </summary>
    public sealed record CopyOutTcoListCell(int DestTemp, int SrcTemp, ListHeadCopyKind HeadCopy) : IrInst;

    public sealed record ToCString(int Target, int StrTemp) : IrInst;
    public sealed record CallExternal(int Target, string SymbolName, string? LibraryName, IReadOnlyList<int> ArgTemps, IReadOnlyList<FfiType> ParameterTypes, FfiType ReturnType) : IrInst;

    /// <summary>
    /// Creates a Task value by allocating a task/state struct and storing
    /// the coroutine function pointer and captured environment.
    /// The task struct holds [state_index, coroutine_fn, result, awaited_task, captures...].
    /// StateStructSize includes the header, captures, and live variable slots.
    /// CaptureCount is the number of captured environment variables to copy.
    /// </summary>
    public sealed record CreateTask(int Target, int ClosureTemp, int StateStructSize, int CaptureCount) : IrInst
    {
        /// <summary>
        /// True for an async tail-recursive loop coroutine that emits a flagged arena reset at its
        /// restart back-edge; the backend stamps the task's <c>LoopResetOk</c> header flag so the
        /// scheduler can veto the reset when a composite ancestor shares the arena.
        /// </summary>
        public bool LoopResetEligible { get; init; }
    }

    /// <summary>
    /// Creates an already-completed Task value. Used by Ashes.Async.fromResult
    /// to wrap a value in a task without needing a coroutine function.
    /// The task struct has state_index = -1 (COMPLETED) and the result stored directly.
    /// </summary>
    public sealed record CreateCompletedTask(int Target, int ResultTemp) : IrInst;

    /// <summary>
    /// Awaits a Task value inside a coroutine. The state machine transform
    /// rewrites this into Suspend/Resume sequences at each await point.
    /// </summary>
    public sealed record AwaitTask(int Target, int TaskTemp) : IrInst;

    /// <summary>
    /// Synchronously runs a task to completion, returning the result value.
    /// Used by Ashes.Async.run to drive a coroutine outside an async context.
    /// </summary>
    public sealed record RunTask(int Target, int TaskTemp) : IrInst;

    /// <summary>
    /// Detaches a task for fire-and-forget cooperative execution (Ashes.Async.spawn).
    /// The task frame is copied into a fresh private arena chunk and appended to the runtime's
    /// detached-task list; the scheduler steps detached tasks (with their private arena installed)
    /// whenever a driver blocks waiting for a pending leaf, and frees the private arena when the
    /// task completes. The result value is dropped. Target receives Unit.
    /// </summary>
    public sealed record SpawnTask(int Target, int TaskTemp) : IrInst;

    /// <summary>
    /// Structured parallelism (Ashes.Parallel.both). Spawns a worker thread to evaluate the
    /// <c>RightClosureTemp</c> thunk (applied to Unit) in its own per-thread arena, or — when
    /// the worker budget is exhausted — evaluates it inline. <c>DescTarget</c> receives a
    /// pointer to a heap-allocated task descriptor used by the matching <see cref="ParallelJoin"/>
    /// and <see cref="ParallelCleanup"/>. Only emitted at concrete result types (see
    /// LowerParallelBoth); polymorphic <c>both</c> lowers to a sequential pair instead.
    /// </summary>
    public sealed record ParallelFork(int DescTarget, int RightClosureTemp) : IrInst;

    /// <summary>
    /// Waits for the worker spawned by the matching <see cref="ParallelFork"/> to finish and
    /// yields its raw result pointer (in the worker's arena). The caller deep-copies that
    /// result into the parent arena before <see cref="ParallelCleanup"/> frees the worker arena.
    /// </summary>
    public sealed record ParallelJoin(int ResultTarget, int DescTemp) : IrInst;

    /// <summary>
    /// Releases the worker resources (stack, TCB, and arena chunks) for a finished
    /// <see cref="ParallelFork"/>; a no-op for the inline (un-spawned) case. Must run after the
    /// worker result has been deep-copied out.
    /// </summary>
    public sealed record ParallelCleanup(int DescTemp) : IrInst;

    /// <summary>
    /// Loads the current dynamically-scoped worker override (the runtime global set by
    /// <c>Ashes.Parallel.withWorkers</c>); 0 means "unset — use the compiled max". Used by
    /// <c>withWorkers</c> lowering to save/restore the enclosing scope's value.
    /// </summary>
    public sealed record LoadParallelWorkerOverride(int Target) : IrInst;

    /// <summary>Stores a value into the worker-override global (0 clears it).</summary>
    public sealed record StoreParallelWorkerOverride(int Source) : IrInst;

    /// <summary>
    /// Work-conserving parallel reduce (queued lowering of Ashes.Parallel.reduce). Snapshots the
    /// list elements into a shared queue region, spawns up to the worker-cap worker threads that
    /// pull element indexes from a shared atomic counter, record <c>f(element)</c> per index, and
    /// then merge the results pairwise through <c>combine</c> — adjacent index pairs per round,
    /// an odd trailing item promoting, until a single root remains. The merge shape depends only
    /// on the element count and pairs each left operand with the elements preceding its right
    /// operand, so the result is deterministic under reduce's associative-combine contract no
    /// matter which worker computes what. <c>DescTarget</c> receives the queue descriptor; the
    /// element count is readable at descriptor offset 8. When no worker slot can be claimed, the
    /// caller drains the whole queue — folds and merges — inline (a correct sequential fallback).
    /// Only emitted at concrete result types the caller can deep-copy out of a worker arena (see
    /// LowerParallelReduceQueued).
    /// </summary>
    public sealed record ParallelQueueStart(int DescTarget, int FClosureTemp, int CombineClosureTemp, int ListTemp) : IrInst;

    /// <summary>
    /// Waits until the merge tree's root result has been published and yields its raw pointer
    /// (in some worker's arena, possibly referencing several). The caller deep-copies it into its
    /// own arena before <see cref="ParallelQueueCleanup"/> frees the worker arenas. Must not be
    /// emitted for an empty element list (there is no root; the caller branches to the identity).
    /// </summary>
    public sealed record ParallelQueueAwait(int ResultTarget, int DescTemp) : IrInst;

    /// <summary>
    /// Waits for every spawned queue worker to fully exit, then frees the workers' stacks, TCBs,
    /// and arena chunks plus the queue region itself. Must run after every awaited result has been
    /// deep-copied out.
    /// </summary>
    public sealed record ParallelQueueCleanup(int DescTemp) : IrInst;

    /// <summary>
    /// State machine suspend point: saves live variables to the state struct,
    /// stores the awaited sub-task, sets the next state index, and returns SUSPENDED.
    /// Generated by the state machine transform (not emitted directly by lowering).
    /// </summary>
    public sealed record Suspend(int StateStructTemp, int NextState, int AwaitedTaskTemp,
        IReadOnlyList<(int SlotOffset, int SourceTemp)> SaveVars) : IrInst;

    /// <summary>
    /// State machine resume point: restores live variables from the state struct
    /// and loads the result from the awaited sub-task.
    /// Generated by the state machine transform (not emitted directly by lowering).
    /// </summary>
    public sealed record Resume(int StateStructTemp, int ResultTemp,
        IReadOnlyList<(int SlotOffset, int TargetTemp)> RestoreVars) : IrInst;

    /// <summary>
    /// Creates a sleep task that completes after the given number of milliseconds.
    /// Returns a Task(Str, Int) that suspends and resumes after the timeout.
    /// Used by Ashes.Async.sleep.
    /// </summary>
    public sealed record AsyncSleep(int Target, int MillisecondsTemp) : IrInst;

    /// <summary>
    /// Creates a leaf networking task for TCP connect.
    /// The task is completed by the runtime/task runner rather than a coroutine body.
    /// </summary>
    public sealed record CreateTcpConnectTask(int Target, int HostTemp, int PortTemp) : IrInst;

    /// <summary>
    /// Creates a leaf networking task for TCP send.
    /// The task is completed by the runtime/task runner rather than a coroutine body.
    /// </summary>
    public sealed record CreateTcpSendTask(int Target, int SocketTemp, int TextTemp) : IrInst;

    /// <summary>
    /// Creates a leaf networking task for TCP receive.
    /// The task is completed by the runtime/task runner rather than a coroutine body.
    /// </summary>
    public sealed record CreateTcpReceiveTask(int Target, int SocketTemp, int MaxBytesTemp) : IrInst;

    /// <summary>
    /// Creates a leaf networking task for TCP close.
    /// The task is completed by the runtime/task runner rather than a coroutine body.
    /// </summary>
    public sealed record CreateTcpCloseTask(int Target, int SocketTemp) : IrInst;

    /// <summary>
    /// Creates a leaf networking task for TCP listen (bind + listen on a local port).
    /// The task is completed by the runtime/task runner rather than a coroutine body.
    /// </summary>
    public sealed record CreateTcpListenTask(int Target, int PortTemp) : IrInst;

    /// <summary>
    /// Creates a leaf task that forks (CountTemp - 1) child processes for the fork-based
    /// multi-reactor (serveParallel), so CountTemp processes total each run their own reactor.
    /// Returns this worker's index (0-based). Synchronous — never parks. Linux-only; a single
    /// process on other targets.
    /// </summary>
    public sealed record CreateForkWorkersTask(int Target, int PortTemp, int CountTemp) : IrInst;

    /// <summary>Sets the graceful-shutdown drain bound (ms) for this process; yields unit.</summary>
    public sealed record SetDrainTimeout(int Target, int MsTemp) : IrInst;

    /// <summary>Requests graceful whole-server shutdown (Stop.stop): rides the signal path on
    /// Linux (worker signals the parent, which forwards); sets the shutdown flag on Windows.
    /// No source temps; side-effectful. Yields unit.</summary>
    public sealed record RequestServerStop(int Target) : IrInst;

    /// <summary>
    /// Creates a leaf networking task for TCP accept (accept one connection from a listener).
    /// The task is completed by the runtime/task runner rather than a coroutine body.
    /// </summary>
    public sealed record CreateTcpAcceptTask(int Target, int SocketTemp) : IrInst;

    /// <summary>
    /// Creates a leaf networking task for HTTP GET.
    /// The task is completed by the runtime/task runner rather than a coroutine body.
    /// </summary>
    public sealed record CreateHttpGetTask(int Target, int UrlTemp) : IrInst;

    /// <summary>
    /// Creates a leaf networking task for HTTP POST.
    /// The task is completed by the runtime/task runner rather than a coroutine body.
    /// </summary>
    public sealed record CreateHttpPostTask(int Target, int UrlTemp, int BodyTemp) : IrInst;

    /// <summary>
    /// Creates a staged networking task for TLS connect (TCP connect + TLS handshake).
    /// The task is completed by the runtime/task runner rather than a coroutine body.
    /// </summary>
    public sealed record CreateTlsConnectTask(int Target, int HostTemp, int PortTemp) : IrInst;

    /// <summary>
    /// Creates a leaf networking task for a TLS handshake on top of an existing TCP socket.
    /// The task is completed by the runtime/task runner rather than a coroutine body.
    /// Internal-only: emitted from staged HTTPS/TLS connect lowering, not from a user-visible builtin.
    /// </summary>
    public sealed record CreateTlsHandshakeTask(int Target, int SocketTemp, int HostTemp) : IrInst;

    /// <summary>
    /// Creates a leaf task that runs the SERVER half of a TLS handshake over an accepted TCP
    /// socket. CertTemp/KeyTemp are PEM contents (Str): the certificate chain and private key.
    /// The server config is built once and cached process-wide; the handshake parks on
    /// WaitTlsWantRead/Write and completes with Ok(TlsSocket).
    /// </summary>
    public sealed record CreateTlsServerHandshakeTask(int Target, int SocketTemp, int CertTemp, int KeyTemp) : IrInst;

    /// <summary>
    /// Creates a leaf networking task for sending text over a TLS session.
    /// </summary>
    public sealed record CreateTlsSendTask(int Target, int SslTemp, int TextTemp) : IrInst;

    /// <summary>
    /// Creates a leaf networking task for receiving text over a TLS session.
    /// </summary>
    public sealed record CreateTlsReceiveTask(int Target, int SslTemp, int MaxBytesTemp) : IrInst;

    /// <summary>
    /// Creates a leaf networking task for closing a TLS session (close-notify flush + connection free).
    /// </summary>
    public sealed record CreateTlsCloseTask(int Target, int SslTemp) : IrInst;

    /// <summary>
    /// Runs all tasks in a list to completion and collects results into a list.
    /// Returns a completed Task(E, List(A)) containing all result values.
    /// Used by Ashes.Async.all.
    /// </summary>
    public sealed record AsyncAll(int Target, int TaskListTemp) : IrInst;

    /// <summary>
    /// Runs the first task in a list to completion and returns its result.
    /// Returns a completed Task(E, A) with the first task's result value.
    /// Used by Ashes.Async.race.
    /// </summary>
    public sealed record AsyncRace(int Target, int TaskListTemp) : IrInst;

    public sealed record PanicStr(int Source) : IrInst;

    /// <summary>
    /// Loads the current handler frame pointer for the capability with compile-time index
    /// <see cref="CapabilityIndex"/> from its module global (dynamically-scoped handler evidence).
    /// 0 means no handler is installed.
    /// </summary>
    public sealed record LoadCapabilityHandler(int Target, int CapabilityIndex) : IrInst;

    /// <summary>Stores a handler frame pointer into the capability's module global.</summary>
    public sealed record StoreCapabilityHandler(int CapabilityIndex, int Source) : IrInst;

    public sealed record Label(string Name) : IrInst;
    public sealed record Jump(string Target) : IrInst;
    public sealed record JumpIfFalse(int CondTemp, string Target) : IrInst;

    /// <summary>
    /// Multi-way dispatch on an ADT tag value (decision-tree pattern matching).
    /// Branches to the label paired with the case whose tag equals
    /// <see cref="TagTemp"/>, or to <see cref="DefaultLabel"/> when none match.
    /// Emitted by match lowering in place of a linear chain of tag comparisons when a
    /// match is over many single-ADT constructor arms; lowers to an LLVM <c>switch</c>
    /// (jump table or balanced binary search). A block terminator.
    /// </summary>
    public sealed record SwitchTag(int TagTemp, IReadOnlyList<(long Tag, string Label)> Cases, string DefaultLabel) : IrInst;

    public sealed record Return(int Source) : IrInst;
}

public sealed record IrStringLiteral(string Label, string Value);

public abstract record FfiType
{
    public sealed record Int : FfiType;
    public sealed record UInt(int Bits) : FfiType;
    public sealed record Float : FfiType;
    public sealed record Bool : FfiType;
    public sealed record Str : FfiType;
    public sealed record Opaque(string Name) : FfiType;
    public sealed record Ptr(FfiType Pointee) : FfiType;
    public sealed record Void : FfiType;
}

public sealed record IrExternalFunction(
    string Name,
    string SymbolName,
    IReadOnlyList<FfiType> ParameterTypes,
    FfiType ReturnType,
    string? LibraryName = null);

/// <summary>
/// Metadata for a coroutine function generated from an async block.
/// Describes the state machine layout computed by the state machine transform.
/// </summary>
public sealed record CoroutineInfo(
    int StateCount,         // number of states (N await points = N+1 states)
    int StateStructSize,    // total size of the state struct in bytes
    int CaptureCount        // number of captured environment variables
);

/// <summary>
/// Fixed header offsets in the task/state struct (each slot is 8 bytes).
/// </summary>
public static class TaskStructLayout
{
    public const int StateIndex = 0;       // current state number (i64): -1 = COMPLETED, -2 = SLEEPING
    public const int CoroutineFn = 8;      // pointer to coroutine function (i64)
    public const int ResultSlot = 16;      // result value / awaited task result (i64)
    public const int AwaitedTask = 24;     // pointer to sub-task being awaited (i64)
    public const int NextTask = 32;        // queue linked list pointer (i64)
    public const int SleepDurationMs = 40; // sleep duration in milliseconds (i64)
    public const int IoArg0 = 48;          // leaf-task argument slot 0 (i64)
    public const int IoArg1 = 56;          // leaf-task argument slot 1 (i64)
    public const int WaitKind = 64;        // pending wait descriptor kind (i64)
    public const int WaitHandle = 72;      // pending wait handle / socket (i64)
    public const int WaitData0 = 80;       // pending wait scratch slot 0 (i64)
    public const int WaitData1 = 88;       // pending wait scratch slot 1 (i64)
    public const int FrameSizeBytes = 96;  // total task struct size incl. captures + live slots (i64)
    public const int ArenaCursor = 104;    // detached task's private arena cursor; 0 unless spawned (i64)
    public const int ArenaEnd = 112;       // detached task's private arena end; 0 unless spawned (i64)
    public const int ReadyNext = 120;      // run-queue "next ready task" link (i64); run-queue scheduler
    public const int Waiter = 128;         // task blocked on this task's completion, re-enqueued on it (i64)
    public const int ArenaOwner = 136;     // nearest spawned-ancestor whose arena this task shares; 0 = global (i64)
    public const int LoopResetOk = 144;    // 1 = this async-loop coroutine may reset its arena at the restart back-edge (i64)
    public const int HeaderSize = 152;     // total header size in bytes
    // Captures follow at [HeaderSize + i*8]
    // Live variable slots follow captures

    /// <summary>State index value indicating the task has completed.</summary>
    public const long StateCompleted = -1;
    /// <summary>State index value indicating the task is sleeping (timer-based suspend).</summary>
    public const long StateSleeping = -2;
    /// <summary>State index value indicating a leaf TCP connect task.</summary>
    public const long StateTcpConnect = -10;
    /// <summary>State index value indicating a leaf TCP send task.</summary>
    public const long StateTcpSend = -11;
    /// <summary>State index value indicating a leaf TCP receive task.</summary>
    public const long StateTcpReceive = -12;
    /// <summary>State index value indicating a leaf TCP close task.</summary>
    public const long StateTcpClose = -13;
    /// <summary>State index value indicating a leaf TCP listen task.</summary>
    public const long StateTcpListen = -16;
    /// <summary>State index value indicating a leaf TCP accept task.</summary>
    public const long StateTcpAccept = -17;

    public const long StateForkWorkers = -18;
    /// <summary>State index value indicating a leaf HTTP GET task.</summary>
    public const long StateHttpGet = -14;
    /// <summary>State index value indicating a leaf HTTP POST task.</summary>
    public const long StateHttpPost = -15;
    /// <summary>State index value indicating a staged TLS connect task.</summary>
    public const long StateTlsConnect = -19;
    /// <summary>State index value indicating a leaf TLS handshake task.</summary>
    public const long StateTlsHandshake = -20;
    /// <summary>State index value indicating a leaf TLS send task.</summary>
    public const long StateTlsSend = -21;
    /// <summary>State index value indicating a leaf TLS receive task.</summary>
    public const long StateTlsReceive = -22;
    /// <summary>State index value indicating a leaf TLS close task.</summary>
    public const long StateTlsClose = -23;
    /// <summary>State index value indicating a leaf server-side TLS handshake task.</summary>
    public const long StateTlsServerHandshake = -24;
    /// <summary>
    /// Run-queue composite task: <c>Ashes.Async.all</c>. Holds the child task list in <c>IoArg0</c>,
    /// a phase flag in <c>IoArg1</c> (0 = children not yet enqueued, 1 = enqueued), and a pending
    /// child counter in <c>WaitData0</c> (decremented by each child's completion; the composite is
    /// re-enqueued and collects results when it reaches 0).
    /// </summary>
    public const long StateAllComposite = -40;
    /// <summary>
    /// Run-queue composite task: <c>Ashes.Async.race</c>. Holds the child list in <c>IoArg0</c>, a
    /// phase flag in <c>IoArg1</c>, and a resolved flag in <c>WaitData0</c> (0 until the first child
    /// completes, whose result is delivered to the composite's <c>ResultSlot</c> and which re-enqueues
    /// the composite; later child completions are ignored).
    /// </summary>
    public const long StateRaceComposite = -41;

    /// <summary>No pending wait is registered for the task.</summary>
    public const long WaitNone = 0;
    /// <summary>The task is waiting for a socket to become readable.</summary>
    public const long WaitSocketRead = 1;
    /// <summary>The task is waiting for a socket to become writable.</summary>
    public const long WaitSocketWrite = 2;
    /// <summary>The task is waiting for a TLS read path to make progress.</summary>
    public const long WaitTlsWantRead = 3;
    /// <summary>The task is waiting for a TLS write path to make progress.</summary>
    public const long WaitTlsWantWrite = 4;
    /// <summary>The task is waiting for a sleep timer to elapse (cooperative sleep suspension).
    /// The remaining milliseconds live in <see cref="SleepDurationMs"/> of the sleeping leaf task
    /// (the task itself when it is a bare sleep leaf, or its <see cref="AwaitedTask"/> when a coroutine
    /// is suspended on one). The scheduler waits until the earliest such deadline instead of blocking
    /// on each sleep inline, so sibling tasks make progress while one sleeps.</summary>
    public const long WaitTimer = 5;
}

public sealed record IrFunction(
    string Label,
    List<IrInst> Instructions,
    int LocalCount,
    int TempCount,
    bool HasEnvAndArgParams, // true for lambdas (implicit env+arg params)
    CoroutineInfo? Coroutine = null, // non-null for async coroutine functions
    IReadOnlyDictionary<int, string>? LocalNames = null, // slot → source name (debug info)
    IReadOnlyDictionary<int, TypeRef>? LocalTypes = null // slot → inferred type (debug info)
);

public sealed record IrProgram(
    IrFunction EntryFunction,
    List<IrFunction> Functions,
    List<IrStringLiteral> StringLiterals,
    List<IrExternalFunction> ExternalFunctions,
    IReadOnlySet<string> ExternalOpaqueTypes,
    bool UsesPrintInt,
    bool UsesPrintStr,
    bool UsesPrintBool,
    bool UsesConcatStr,
    bool UsesClosures,
    bool UsesAsync
)
{
    /// <summary>
    /// Number of declared capabilities. The backend materializes one module global per capability — the
    /// dynamically-scoped handler-evidence slot holding a pointer to the innermost installed
    /// handler frame for that capability (0 when none).
    /// </summary>
    public int CapabilityHandlerGlobals { get; init; }

    public IrProgram(
        IrFunction EntryFunction,
        List<IrFunction> Functions,
        List<IrStringLiteral> StringLiterals,
        bool UsesPrintInt,
        bool UsesPrintStr,
        bool UsesPrintBool,
        bool UsesConcatStr,
        bool UsesClosures,
        bool UsesAsync)
        : this(EntryFunction, Functions, StringLiterals, [], new HashSet<string>(StringComparer.Ordinal),
            UsesPrintInt, UsesPrintStr, UsesPrintBool, UsesConcatStr, UsesClosures, UsesAsync)
    {
    }
}
