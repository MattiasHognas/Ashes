// Models the in-place-reuse decisions semantic lowering records for later reporting.
//
// Invariants:
// - Decision, outcome, and reason stay structural enums; reporting formats them, nothing here does.
// - A decision names its function by origin so reports correlate it with generated IR.
// - Constructors carry a `Decide`/`Via`/`Outcome`/`Because` prefix so they stay unique across the
//   whole program; the name functions strip it, yielding stage 0's enum member names for reports.

import AshesCompiler.Semantics.IrOrigins
export (
    type ReuseDecisionKind(..),
    type ReuseDecisionMechanism(..),
    type ReuseDecisionOutcome(..),
    type ReuseDecisionReason(..),
    type ReuseDecision(..),
    value reuseDecisionKindName,
    value reuseDecisionMechanismName,
    value reuseDecisionOutcomeName,
    value reuseDecisionReasonName,
)

type ReuseDecisionKind =
    | DecideSpecializationGeneration
    | DecideResetSafetyQualification
    | DecideEntryCopy
    | DecideRuntimeUniquenessCheck
    | DecideSpecializationCandidateQualification
    | DecideConstructorLayoutCompatibility
    | DecideTokenProduction
    | DecideTokenDisposition
    | DecideFallbackAllocation
    deriving {Eq, Show}

type ReuseDecisionMechanism =
    | ViaDirectInPlace
    | ViaSpecialization
    | ViaReuseToken
    deriving {Eq, Show}

type ReuseDecisionOutcome =
    | OutcomeGenerated
    | OutcomeAccepted
    | OutcomeRejected
    | OutcomeElided
    | OutcomeRetained
    | OutcomeOmitted
    | OutcomeRequired
    | OutcomeProduced
    | OutcomeConsumed
    | OutcomeReleased
    | OutcomeDiscarded
    | OutcomeAllocated
    | OutcomeAvailable
    deriving {Eq, Show}

type ReuseDecisionReason =
    | BecauseSpecializableCall
    | BecauseNoResetInvalidatingAllocation
    | BecauseRecursiveReuseFunctionMissing
    | BecauseFreshAdtAllocation
    | BecauseFreshStackAdtAllocation
    | BecauseFreshStackAllocation
    | BecauseStringConcatenationAllocation
    | BecauseArenaCopyOut
    | BecauseListCopyOut
    | BecauseClosureCopyOut
    | BecauseTcoListCellCopyOut
    | BecauseRawAllocationMayEscape
    | BecauseClosureMayEscape
    | BecauseStackClosureMayEscape
    | BecauseOwnershipMoveSafe
    | BecauseOwnershipMoveSafetyRejected
    | BecauseNoStructuralReuse
    | BecauseRuntimeManagedReuseCandidate
    | BecauseStaticallyUniqueReuseCandidate
    | BecauseCalleeBindingUnavailable
    | BecauseResultDoesNotRebuildAccumulator
    | BecauseAccumulatorNotProvenUnique
    | BecauseFreshResultNotProven
    | BecauseFreshAccumulatorLayoutUnsupported
    | BecauseAccumulatorLayoutNotPersistent
    | BecauseCompatibleConstructorLayout
    | BecauseConstructorFieldCountMismatch
    | BecauseConstructorCellKindMismatch
    | BecauseRuntimeManagedTokenNotAllowed
    | BecauseMatchedCellBecameDead
    | BecauseCompatibleTokenConsumed
    | BecauseUnconsumedRuntimeTokenReleased
    | BecauseUnconsumedArenaTokenDiscarded
    | BecauseNoCompatibleReuseToken
    | BecauseNoReuseTokenAvailable
    | BecauseRuntimeUniquenessFallback
    | BecauseHelperUnreachableFromSpecialization
    deriving {Eq, Show}

// `candidate` is the source name of the parameter, value, or token considered for reuse;
// `location` is the call site or rejecting instruction when one is known.
type ReuseDecision =
    | functionOrigin: IrFunctionOrigin
    | decision: ReuseDecisionKind
    | mechanism: ReuseDecisionMechanism
    | outcome: ReuseDecisionOutcome
    | reason: ReuseDecisionReason
    | candidate: Maybe(Str)
    | relatedGeneratedLabel: Maybe(Str)
    | location: Maybe(IrSourceLocation)
    deriving {Eq, Show}

let stripPrefix (prefix: Str) (name: Str) =
    if Ashes.Text.startsWith(name)(prefix)
    then
        prefix
        |> Ashes.Text.length
        |> Ashes.Text.drop(name)
    else name

let reuseDecisionKindName (kind: ReuseDecisionKind) =
    kind
    |> Ashes.Trait.Show.show
    |> stripPrefix("Decide")

let reuseDecisionMechanismName (mechanism: ReuseDecisionMechanism) =
    mechanism
    |> Ashes.Trait.Show.show
    |> stripPrefix("Via")

let reuseDecisionOutcomeName (outcome: ReuseDecisionOutcome) =
    outcome
    |> Ashes.Trait.Show.show
    |> stripPrefix("Outcome")

let reuseDecisionReasonName (reason: ReuseDecisionReason) =
    reason
    |> Ashes.Trait.Show.show
    |> stripPrefix("Because")
