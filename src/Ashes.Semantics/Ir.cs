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

    public sealed record MakeClosure(int Target, string FuncLabel, int EnvPtrTemp) : IrInst; // alloc 16 bytes
    public sealed record CallClosure(int Target, int ClosureTemp, int ArgTemp) : IrInst;

    public sealed record Alloc(int Target, int SizeBytes) : IrInst;

    // ADT heap cell: layout is [tag:i64, field0:u64, field1:u64, ..., fieldN:u64]
    // AllocAdt allocates (1 + FieldCount) * 8 bytes and stores Tag at offset 0.
    public sealed record AllocAdt(int Target, int Tag, int FieldCount) : IrInst;
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
    /// the current linear allocator — placeholder for future free().
    /// </summary>
    public sealed record Drop(int SourceTemp, string TypeName) : IrInst;

    /// <summary>
    /// Borrow instruction for compiler-inferred borrowing (Phase 3).
    /// Produces a non-owning reference to the owned value held in SourceTemp.
    /// The borrowed reference carries no drop responsibility — the owning scope
    /// still drops the original.
    /// In the current linear allocator this is a simple value copy (pointer pass-through).
    /// </summary>
    public sealed record Borrow(int Target, int SourceTemp) : IrInst;

    /// <summary>
    /// Creates a Task value by allocating a task/state struct and storing
    /// the coroutine function pointer and captured environment.
    /// Phase B: the task struct holds [state_index, coroutine_fn, result, awaited_task, captures...].
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
    /// Returns a Task(Str, Unit) that suspends and resumes after the timeout.
    /// Used by Ashes.Async.sleep.
    /// </summary>
    public sealed record AsyncSleep(int Target, int MillisecondsTemp) : IrInst;

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
    public const int NextTask = 32;        // queue linked list pointer (i64) — Phase C event loop
    public const int SleepDeadlineNs = 40; // absolute nanosecond deadline for sleep (i64) — Phase C
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
    CoroutineInfo? Coroutine = null // non-null for async coroutine functions
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
