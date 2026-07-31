using Ashes.Frontend;

namespace Ashes.Semantics;

[Flags]
internal enum PatternBindingOwnershipUse
{
    None = 0,
    StructuralInspection = 1 << 0,
    OrdinaryCallBorrow = 1 << 1,
    SameParameterTransfer = 1 << 2,
    EmbeddedInOwner = 1 << 3,
    IndependentEscape = 1 << 4,
    CapturedByClosure = 1 << 5,
    ConservativeUnknown = 1 << 6,
}

internal enum PatternBindingOwnershipKind
{
    BorrowedOnly,
    TransferredToSameParameter,
    EmbeddedInOwner,
    EscapesIndependently,
    ConservativeUnknown,
}

/// <summary>
/// Stable ownership classification for one pattern-extracted reference before lowering assigns a
/// local slot. <paramref name="Binder"/> is compared by reference identity only and is retained as the
/// short-lived handoff to pattern emission; reporting consumes the ordinal, name, lineage, and source
/// location instead of the AST node.
/// </summary>
internal sealed record PatternBindingOwnershipFact(
    Pattern.Var Binder,
    SourceFunctionOrigin Function,
    int BindingOrdinal,
    string BindingName,
    int RootParameterOrdinal,
    string RootParameterName,
    int? ParentBindingOrdinal,
    int ExtractionDepth,
    PatternBindingOwnershipUse Uses,
    PatternBindingOwnershipKind Ownership,
    SourceLocation? Location)
{
    public bool RequiresProtectiveDup => Ownership is
        PatternBindingOwnershipKind.EmbeddedInOwner
        or PatternBindingOwnershipKind.EscapesIndependently
        or PatternBindingOwnershipKind.ConservativeUnknown;
}

internal enum PatternBindingShadowComparison
{
    Agrees,
    OwnershipMoreConservative,
    LegacyMoreConservative,
    MissingOwnershipFact,
}

internal enum PatternBindingPlacementOutcome
{
    ProtectiveDupInserted,
    CopyType,
    AlreadyProtectedByRuntimeAlias,
    LegacyEscapeRejected,
    RootNotRuntimeManaged,
}

/// <summary>
/// Final correlation between a pre-lowering pattern ownership fact, its emitted local slot, and the
/// legacy nested-TCO alias decision that remains authoritative during the shadow stage.
/// </summary>
internal sealed record PatternBindingOwnershipDecision(
    SourceFunctionOrigin? Function,
    int BindingOrdinal,
    string BindingName,
    int LocalSlot,
    int RootParameterOrdinal,
    string RootParameterName,
    int? ParentBindingOrdinal,
    int ExtractionDepth,
    PatternBindingOwnershipUse Uses,
    PatternBindingOwnershipKind Ownership,
    SourceLocation? Location,
    bool LegacyRequiresProtectiveDup,
    PatternBindingShadowComparison ShadowComparison,
    PatternBindingPlacementOutcome PlacementOutcome);
