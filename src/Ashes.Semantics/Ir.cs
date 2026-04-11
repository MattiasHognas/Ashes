namespace Ashes.Semantics;

// Named type variable used in type scheme quantifiers (forall a. body).
// Id is the original TVar ID used for instantiation; Name is kept for display.
public sealed record TypeVar(int Id, string Name);

// Type scheme: forall [Quantified]. Body (polytype representation for let-polymorphism).
public sealed record TypeScheme(IReadOnlyList<TypeVar> Quantified, TypeRef Body);

public abstract record TypeRef
{
    public sealed record TInt : TypeRef;
    public sealed record TFloat : TypeRef;
    public sealed record TStr : TypeRef;
    public sealed record TBool : TypeRef;
    public sealed record TNever : TypeRef;
    public sealed record TList(TypeRef Element) : TypeRef;
    public sealed record TTuple(IReadOnlyList<TypeRef> Elements) : TypeRef;
    public sealed record TFun(TypeRef Arg, TypeRef Ret) : TypeRef;
    public sealed record TVar(int Id) : TypeRef;
    public sealed record TNamedType(TypeSymbol Symbol, IReadOnlyList<TypeRef> TypeArgs) : TypeRef;
    public sealed record TTypeParam(TypeParameterSymbol Symbol) : TypeRef;
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

    public sealed record AddInt(int Target, int Left, int Right) : IrInst;
    public sealed record SubInt(int Target, int Left, int Right) : IrInst;
    public sealed record MulInt(int Target, int Left, int Right) : IrInst;
    public sealed record DivInt(int Target, int Left, int Right) : IrInst;
    public sealed record AddFloat(int Target, int Left, int Right) : IrInst;
    public sealed record SubFloat(int Target, int Left, int Right) : IrInst;
    public sealed record MulFloat(int Target, int Left, int Right) : IrInst;
    public sealed record DivFloat(int Target, int Left, int Right) : IrInst;
    public sealed record CmpIntGe(int Target, int Left, int Right) : IrInst;
    public sealed record CmpIntLe(int Target, int Left, int Right) : IrInst;
    public sealed record CmpIntEq(int Target, int Left, int Right) : IrInst;
    public sealed record CmpIntNe(int Target, int Left, int Right) : IrInst;
    public sealed record CmpFloatGe(int Target, int Left, int Right) : IrInst;
    public sealed record CmpFloatLe(int Target, int Left, int Right) : IrInst;
    public sealed record CmpFloatEq(int Target, int Left, int Right) : IrInst;
    public sealed record CmpFloatNe(int Target, int Left, int Right) : IrInst;
    public sealed record CmpStrEq(int Target, int Left, int Right) : IrInst;
    public sealed record CmpStrNe(int Target, int Left, int Right) : IrInst;
    public sealed record ConcatStr(int Target, int Left, int Right) : IrInst;

    public sealed record MakeClosure(int Target, string FuncLabel, int EnvPtrTemp, int EnvSizeBytes) : IrInst; // alloc 24 bytes: {code, env, env_size}
    public sealed record MakeClosureStack(int Target, string FuncLabel, int EnvPtrTemp, int EnvSizeBytes) : IrInst; // stack alloc 24 bytes: {code, env, env_size}
    public sealed record CallClosure(int Target, int ClosureTemp, int ArgTemp) : IrInst;

    public sealed record Alloc(int Target, int SizeBytes) : IrInst;
    public sealed record AllocStack(int Target, int SizeBytes) : IrInst;

    // ADT heap cell: layout is [tag:i64, field0:u64, field1:u64, ..., fieldN:u64]
    // AllocAdt allocates (1 + FieldCount) * 8 bytes and stores Tag at offset 0.
    public sealed record AllocAdt(int Target, int Tag, int FieldCount) : IrInst;
    public sealed record AllocAdtStack(int Target, int Tag, int FieldCount) : IrInst;
    // SetAdtField: *(Ptr + 8 + FieldIndex*8) = Source
    public sealed record SetAdtField(int Ptr, int FieldIndex, int Source) : IrInst;
    // GetAdtTag: Target = *(Ptr + 0)
    public sealed record GetAdtTag(int Target, int Ptr) : IrInst;
    // GetAdtField: Target = *(Ptr + 8 + FieldIndex*8)
    public sealed record GetAdtField(int Target, int Ptr, int FieldIndex) : IrInst;

    public sealed record PrintInt(int Source) : IrInst;
    public sealed record PrintStr(int Source) : IrInst;
    public sealed record PrintBool(int Source) : IrInst;
    public sealed record WriteStr(int Source) : IrInst;
    public sealed record ReadLine(int Target) : IrInst;
    public sealed record FileReadText(int Target, int PathTemp) : IrInst;
    public sealed record FileWriteText(int Target, int PathTemp, int TextTemp) : IrInst;
    public sealed record FileExists(int Target, int PathTemp) : IrInst;
    public sealed record HttpGet(int Target, int UrlTemp) : IrInst;
    public sealed record HttpPost(int Target, int UrlTemp, int BodyTemp) : IrInst;
    public sealed record NetTcpConnect(int Target, int HostTemp, int PortTemp) : IrInst;
    public sealed record NetTcpSend(int Target, int SocketTemp, int TextTemp) : IrInst;
    public sealed record NetTcpReceive(int Target, int SocketTemp, int MaxBytesTemp) : IrInst;
    public sealed record NetTcpClose(int Target, int SocketTemp) : IrInst;

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
    public sealed record SaveArenaState(int CursorLocalSlot, int EndLocalSlot) : IrInst;

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
    public sealed record RestoreArenaState(int CursorLocalSlot, int EndLocalSlot, int PreRestoreEndSlot) : IrInst;

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
    public sealed record ReclaimArenaChunks(int SavedEndSlot, int PreRestoreEndSlot) : IrInst;

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
    /// <b>Fixed-size objects (<see cref="StaticSizeBytes"/> &gt; 0):</b>
    /// Allocates exactly <see cref="StaticSizeBytes"/> bytes and memcpy's them.
    /// Used for cons cells (16 bytes: head + tail) when the head is a copy type,
    /// ensuring the tail pointer to a pre-watermark cell is preserved.
    /// </para>
    /// </summary>
    public sealed record CopyOutArena(int DestTemp, int SrcTemp, int StaticSizeBytes) : IrInst;

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

    /// <summary>
    /// Creates a Task value by allocating a task/state struct and storing
    /// the coroutine function pointer and captured environment.
    /// The task struct holds [state_index, coroutine_fn, result, awaited_task, captures...].
    /// StateStructSize includes the header, captures, and live variable slots.
    /// CaptureCount is the number of captured environment variables to copy.
    /// </summary>
    public sealed record CreateTask(int Target, int ClosureTemp, int StateStructSize, int CaptureCount) : IrInst;

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

    public sealed record Label(string Name) : IrInst;
    public sealed record Jump(string Target) : IrInst;
    public sealed record JumpIfFalse(int CondTemp, string Target) : IrInst;

    public sealed record Return(int Source) : IrInst;
}

public sealed record IrStringLiteral(string Label, string Value);

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
    public const int HeaderSize = 48;      // total header size in bytes
    // Captures follow at [HeaderSize + i*8]
    // Live variable slots follow captures

    /// <summary>State index value indicating the task has completed.</summary>
    public const long StateCompleted = -1;
    /// <summary>State index value indicating the task is sleeping (timer-based suspend).</summary>
    public const long StateSleeping = -2;
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
    bool UsesPrintInt,
    bool UsesPrintStr,
    bool UsesPrintBool,
    bool UsesConcatStr,
    bool UsesClosures,
    bool UsesAsync
);
