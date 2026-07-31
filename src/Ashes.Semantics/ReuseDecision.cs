namespace Ashes.Semantics;

/// <summary>The reuse decision made by semantic lowering.</summary>
internal enum ReuseDecisionKind
{
    /// <summary>A monomorphic in-place-reuse specialization was generated.</summary>
    SpecializationGeneration,
    /// <summary>A generated specialization was checked for loop-arena reset safety.</summary>
    ResetSafetyQualification,
}

/// <summary>The stable outcome of a reuse decision.</summary>
internal enum ReuseDecisionOutcome
{
    Generated,
    Accepted,
    Rejected,
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
}

/// <summary>
/// One immutable reuse decision retained by lowering. <paramref name="Function"/> identifies the
/// generated function and its source lineage; <paramref name="RelatedGeneratedLabel"/> identifies a
/// nested generated function inspected by the decision when it differs from that function.
/// </summary>
/// <param name="Function">The function whose reuse behavior the decision describes.</param>
/// <param name="Decision">The kind of reuse decision.</param>
/// <param name="Outcome">The stable decision outcome.</param>
/// <param name="Reason">The concrete fact that produced the outcome.</param>
/// <param name="CandidateParameter">The source parameter considered as the reuse root.</param>
/// <param name="RelatedGeneratedLabel">A nested generated function inspected by the decision.</param>
/// <param name="Location">The call site or rejecting instruction location when available.</param>
internal sealed record ReuseDecision(
    IrFunctionOrigin Function,
    ReuseDecisionKind Decision,
    ReuseDecisionOutcome Outcome,
    ReuseDecisionReason Reason,
    string? CandidateParameter,
    string? RelatedGeneratedLabel,
    SourceLocation? Location);
