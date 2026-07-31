namespace Ashes.Semantics;

/// <summary>The reuse decision made by semantic lowering.</summary>
internal enum ReuseDecisionKind
{
    /// <summary>A monomorphic in-place-reuse specialization was generated.</summary>
    SpecializationGeneration,
    /// <summary>A generated specialization was checked for loop-arena reset safety.</summary>
    ResetSafetyQualification,
    /// <summary>The one-time copy used to establish a unique reuse root was finalized.</summary>
    EntryCopy,
    /// <summary>A reuse candidate was classified as statically unique or requiring a runtime check.</summary>
    RuntimeUniquenessCheck,
}

/// <summary>The reuse mechanism whose decision is being described.</summary>
internal enum ReuseDecisionMechanism
{
    DirectInPlace,
    Specialization,
    ReuseToken,
}

/// <summary>The kind of source or IR value considered for reuse.</summary>
internal enum ReuseCandidateKind
{
    Parameter,
    Value,
    Token,
}

/// <summary>The stable outcome of a reuse decision.</summary>
internal enum ReuseDecisionOutcome
{
    Generated,
    Accepted,
    Rejected,
    Elided,
    Retained,
    Omitted,
    Required,
}

/// <summary>
/// The concrete reason for a reuse decision. Values describe compiler facts rather than
/// human-facing prose so later reporting can format them without reverse-engineering generated IR.
/// </summary>
internal enum ReuseDecisionReason
{
    SpecializableCall,
    NoResetInvalidatingAllocation,
    RecursiveReuseFunctionMissing,
    FreshAdtAllocation,
    FreshStackAdtAllocation,
    FreshStackAllocation,
    StringConcatenationAllocation,
    ArenaCopyOut,
    ListCopyOut,
    ClosureCopyOut,
    TcoListCellCopyOut,
    RawAllocationMayEscape,
    ClosureMayEscape,
    StackClosureMayEscape,
    OwnershipMoveSafe,
    OwnershipMoveSafetyRejected,
    NoStructuralReuse,
    RuntimeManagedReuseCandidate,
    StaticallyUniqueReuseCandidate,
}

/// <summary>A stable source name plus optional lowering identities for a reuse candidate.</summary>
/// <param name="Kind">Whether the candidate is a parameter, ordinary value, or reuse token.</param>
/// <param name="SourceName">The source-level parameter or value name when one exists.</param>
/// <param name="LocalSlot">The lowering local slot when the candidate is stored in one.</param>
/// <param name="Temp">The semantic IR temp when the decision is site-specific.</param>
internal sealed record ReuseDecisionCandidate(
    ReuseCandidateKind Kind,
    string? SourceName,
    int? LocalSlot = null,
    int? Temp = null);

/// <summary>
/// One immutable reuse decision retained by lowering. <paramref name="Function"/> identifies the
/// generated function and its source lineage; <paramref name="RelatedGeneratedLabel"/> identifies a
/// nested generated function inspected by the decision when it differs from that function.
/// </summary>
/// <param name="Function">The function whose reuse behavior the decision describes.</param>
/// <param name="Decision">The kind of reuse decision.</param>
/// <param name="Mechanism">The direct, specialization, or reuse-token path.</param>
/// <param name="Outcome">The stable decision outcome.</param>
/// <param name="Reason">The concrete fact that produced the outcome.</param>
/// <param name="Candidate">The parameter, value, or token considered for reuse.</param>
/// <param name="RelatedGeneratedLabel">A nested generated function inspected by the decision.</param>
/// <param name="Location">The call site or rejecting instruction location when available.</param>
/// <param name="MoveSafetyCauses">The ownership proof's conservative causes for entry-copy decisions.</param>
internal sealed record ReuseDecision(
    IrFunctionOrigin Function,
    ReuseDecisionKind Decision,
    ReuseDecisionMechanism Mechanism,
    ReuseDecisionOutcome Outcome,
    ReuseDecisionReason Reason,
    ReuseDecisionCandidate? Candidate,
    string? RelatedGeneratedLabel,
    SourceLocation? Location,
    ParameterMoveSafetyCause MoveSafetyCauses = ParameterMoveSafetyCause.None);
