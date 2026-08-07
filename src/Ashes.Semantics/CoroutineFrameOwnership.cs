namespace Ashes.Semantics;

/// <summary>What a task-frame slot holds.</summary>
internal enum CoroutineFrameSlotKind
{
    /// <summary>A value captured by the async block and copied into the frame at task creation.</summary>
    Capture = 1,

    /// <summary>A body temp saved across a suspend.</summary>
    SavedTemp,

    /// <summary>A body local slot saved across a suspend.</summary>
    SavedLocal,
}

/// <summary>Who owns the ordinary heap value a task-frame slot refers to.</summary>
internal enum CoroutineFrameSlotOwnership
{
    /// <summary>Not an ordinary owned heap value: a scalar, or a value with no drop obligation.</summary>
    NotOwned = 0,

    /// <summary>Runtime reference-counted and owned by the frame; the frame dropper releases it.</summary>
    FrameOwnedRuntimeRc,

    /// <summary>Region-backed; the region reclaims it and the frame dropper must leave it alone.</summary>
    RegionOwned,

    /// <summary>A resource or borrowed view, released by its own deterministic cleanup path.</summary>
    ResourceOrBorrowedView,

    /// <summary>Representation not established; treated conservatively as not frame-owned.</summary>
    ConservativeUnknown,
}

/// <summary>Stable reason a task-frame slot reached its ownership classification.</summary>
internal enum CoroutineFrameSlotReason
{
    Unknown = 0,

    /// <summary>The value's resolved type is a copy type with no heap ownership.</summary>
    CopyTypedValue,

    /// <summary>The lowered value is runtime reference-counted and its layout is structurally droppable.</summary>
    RuntimeRcDroppableLayout,

    /// <summary>The lowered value is runtime reference-counted but its layout has no supported drop.</summary>
    RuntimeRcUndroppableLayout,

    /// <summary>The value is region-backed, so the region reclaims it.</summary>
    RegionBackedValue,

    /// <summary>The value is a resource or a borrowed view over storage it does not own.</summary>
    ResourceOrBorrowedStorage,

    /// <summary>A local slot receives values whose representations disagree.</summary>
    ConflictingSlotStores,

    /// <summary>
    /// The word holds a value another frame word already owns, so releasing it here would release
    /// one reference twice.
    /// </summary>
    AliasesOwnedFrameWord,

    /// <summary>No ownership fact was established for the value.</summary>
    NoOwnershipFact,
}

/// <summary>
/// One task-frame word and the ownership obligation it carries. Slots are the frame's canonical
/// teardown description: the generated frame dropper releases exactly the
/// <see cref="CoroutineFrameSlotOwnership.FrameOwnedRuntimeRc"/> slots and clears them, so
/// completion, cancellation, and reaping cannot release the same reference twice.
/// </summary>
/// <param name="OffsetBytes">Byte offset of the word inside the task/state struct.</param>
/// <param name="Kind">Whether the word holds a capture, a saved temp, or a saved local.</param>
/// <param name="SourceIndex">Capture index, body temp, or local slot, per <paramref name="Kind"/>.</param>
/// <param name="Ownership">Who releases the value.</param>
/// <param name="DropKind">The type-directed release operation for a frame-owned value.</param>
/// <param name="Type">Resolved type of the value, used to emit its type-directed release.</param>
/// <param name="TypeName">Reportable owned-type name, when one is known.</param>
/// <param name="MayBeEmpty">The value's type admits the empty-list representation (the null pointer).</param>
/// <param name="Reason">Stable reason for the classification.</param>
/// <param name="Location">Source location of the value, when known.</param>
internal sealed record CoroutineFrameSlot(
    int OffsetBytes,
    CoroutineFrameSlotKind Kind,
    int SourceIndex,
    CoroutineFrameSlotOwnership Ownership,
    OrdinaryHeapChildDropKind DropKind,
    TypeRef? Type,
    string? TypeName,
    bool MayBeEmpty,
    CoroutineFrameSlotReason Reason,
    SourceLocation? Location);

/// <summary>
/// Why a coroutine value received the representation it did. Distinct from ordinary runtime-RC and
/// region placement: these values answer the async-specific question of what can cross a suspend or
/// a worker boundary.
/// </summary>
internal enum CoroutineValueRepresentationDecision
{
    Unknown = 0,

    /// <summary>Held in a task frame across a suspend with an owned reference and a drop descriptor.</summary>
    SavedInTaskFrame,

    /// <summary>Dead before the first suspend, so ordinary placement applies.</summary>
    DeadBeforeSuspend,

    /// <summary>Region-backed because the suspend or call graph reaching it is not known.</summary>
    RegionBecauseSuspendGraphUnknown,

    /// <summary>Copied because the value crosses a worker boundary.</summary>
    CopiedAcrossWorkerBoundary,
}

/// <summary>
/// A retained coroutine representation decision, kept for the compiler-report handoff. Recording it
/// is observational and does not affect lowering.
/// </summary>
internal sealed record CoroutineRepresentationRecord(
    IrFunctionOrigin? Function,
    string CoroutineLabel,
    CoroutineFrameSlotKind Kind,
    int SourceIndex,
    CoroutineValueRepresentationDecision Decision,
    CoroutineFrameSlotReason Reason,
    SourceLocation? Location);
