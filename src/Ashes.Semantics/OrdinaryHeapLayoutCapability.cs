namespace Ashes.Semantics;

/// <summary>The structural copy operation supported by an ordinary value graph.</summary>
internal enum OrdinaryHeapStructuralCopyKind
{
    None = 0,
    Inline,
    Shallow,
    Deep,
}

/// <summary>The type-directed release operation required for one owned heap child.</summary>
internal enum OrdinaryHeapChildDropKind
{
    None = 0,
    String,
    Bytes,
    BigInt,
    List,
    Tuple,
    Adt,
    Unsupported,
}

/// <summary>Stable reasons why an ordinary layout capability is conservative.</summary>
[Flags]
internal enum OrdinaryHeapLayoutRejection
{
    None = 0,
    ResourceOrBorrowedViewContainment = 1 << 0,
    UnsupportedChildDropLayout = 1 << 1,
    UnresolvedType = 1 << 2,
    UnsupportedOuterCellReuse = 1 << 3,
}

/// <summary>
/// One constructor field, tuple element, or list payload word whose ownership operation is
/// described by the enclosing layout capability.
/// </summary>
internal sealed record OrdinaryHeapLayoutChild(
    string? ConstructorName,
    int Index,
    int OffsetBytes,
    TypeRef Type,
    OrdinaryHeapChildDropKind DropKind);

/// <summary>
/// Immutable, type-resolved capability of one ordinary value layout. Syntax-specific freshness and
/// TCO profitability deliberately remain outside this result.
/// </summary>
internal sealed record OrdinaryHeapLayoutCapability(
    LoweredTempLayoutKind OuterLayout,
    OrdinaryHeapStructuralCopyKind StructuralCopy,
    bool ArenaDeepCopySupported,
    int? StaticCopySizeBytes,
    bool OwnedChildrenDroppable,
    bool ContainsOwnedChild,
    bool ContainsResourceOrBorrowedView,
    bool RuntimeOuterCellReuseSupported,
    bool RuntimeCopyAdtSupported,
    bool RuntimeRecordAdtSupported,
    bool RuntimeOwnedChildAdtSupported,
    bool RuntimeTcoOwnedChildAdtSupported,
    bool RuntimeTcoListElementSupported,
    IReadOnlyList<OrdinaryHeapLayoutChild> Children,
    OrdinaryHeapLayoutRejection Rejections)
{
    public OrdinaryHeapLayoutCapability AsBorrowedView()
    {
        return this with
        {
            ContainsResourceOrBorrowedView = true,
            RuntimeOuterCellReuseSupported = false,
            Rejections = Rejections
                | OrdinaryHeapLayoutRejection.ResourceOrBorrowedViewContainment
                | OrdinaryHeapLayoutRejection.UnsupportedOuterCellReuse,
        };
    }
}
