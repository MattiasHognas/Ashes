// Domain models for function ownership summaries, result reachability, parameter borrow/consume
// classifications, move-safety proofs, TCO structural facts, and result provenance.
//
// Invariants:
// - All summary types are pure, deterministic, and immutable.
// - Parameter ownership differentiates borrowed (caller-owned) from consumed (callee-owned).
// - FunctionResultReach tracks may-alias parameter flow with explicit poisoning causes.
// - ResultProvenance captures RC-eligibility, forwarding targets, and byte storage provenance.

import AshesCompiler.Semantics.IrOrigins
import Ashes.Collection.List.append
export (
    type ParameterOwnership(..),
    type FunctionCallCensusCause(..),
    type FunctionCallCensus(..),
    type ParameterMoveSafetyCause(..),
    type ParameterMoveSafetyProof(..),
    type ResultReachCause(..),
    type ParameterReachEntry(..),
    type FunctionResultReachFacts(..),
    type BytesOwnershipProvenance(..),
    type FunctionResultProvenance(..),
    type TcoSelfCallArgumentShape(..),
    type TcoParamUseMode(..),
    type TcoParamReuseAffinity(..),
    type TcoParamStructuralFacts(..),
    type FunctionOwnershipSummary(..),
    value isCallCensusComplete,
    value isResultFresh,
    value isResultPoisoned,
    value resultReachesParameter,
    value resultReachesParameterWhole,
    value getBorrowedParameters,
    value getConsumedParameters,
)

type ParameterOwnership =
    | Borrowed
    | Consumed
    deriving {Eq, Show}

type FunctionCallCensusCause =
    | CensusCauseNone
    | EscapedAsValue
    | IncompleteApplication
    | AmbiguousResolution
    | UnknownResolution
    deriving {Eq, Show}

type FunctionCallCensus =
    | directCallCount: Int
    | causes: List(FunctionCallCensusCause)
    deriving {Eq, Show}

type ParameterMoveSafetyCause =
    | MoveCauseNone
    | FunctionEscaped
    | IncompleteCallCensus
    | NoDirectCallSites
    | NoExternalCallSites
    | CallArityMismatch
    | SeedNotSafe
    | MoveLinearity
    | CapturedByClosure
    | TransitiveParameterUnsafe
    | ResultAliasUnsafe
    | AmbiguousResolutionCause
    | ProofCycle
    | ConservativeUnknownCause
    deriving {Eq, Show}

type ParameterMoveSafetyProof =
    | isMoveSafe: Bool
    | causes: List(ParameterMoveSafetyCause)
    deriving {Eq, Show}

type ResultReachCause =
    | ReachCauseNone
    | GlobalOrTopLevelReach
    | UnmodelledReach
    | InternalSharing
    | ConservativeUnknownReach
    deriving {Eq, Show}

type ParameterReachEntry =
    | parameterName: Str
    | reachCount: Int
    deriving {Eq, Show}

// `wholeParameterReach` lists the parameters the result may alias as themselves — embedded whole
// in a constructor field or returned — as opposed to only through a destructured component (a
// list's head cell, a record field): a callee that stores its parameter into the value it
// returns keeps it whole; one that rebuilds a value from the parameter's parts does not. A
// caller passing a fresh value to such a callee must transfer ownership only in the former case.
type FunctionResultReachFacts =
    | parameterReach: List(ParameterReachEntry)
    | causes: List(ResultReachCause)
    | isPoisoned: Bool
    | wholeParameterReach: List(Str)
    deriving {Eq, Show}

type BytesOwnershipProvenance =
    | BytesProvenanceUnknown
    | BytesProvenanceFreshOwnedBuffer
    | BytesProvenanceBorrowedView
    | BytesProvenanceProgramLifetimeView
    deriving {Eq, Show}

type FunctionResultProvenance =
    | rcEligible: Bool
    | forwardsTo: Maybe(Str)
    | bytesProvenance: BytesOwnershipProvenance
    deriving {Eq, Show}

type TcoSelfCallArgumentShape =
    | UnchangedPassthrough
    | FreshRebuilt
    | ConsumedTail
    | GrownCons
    | MixedShape
    deriving {Eq, Show}

type TcoParamUseMode =
    | GeneralOrUnknownUse
    | BorrowInspectOnly
    deriving {Eq, Show}

type TcoParamReuseAffinity =
    | ReuseAffinityGeneral
    | SelfAppendOnly
    deriving {Eq, Show}

type TcoParamStructuralFacts =
    | parameterOrdinal: Int
    | parameterName: Str
    | shape: TcoSelfCallArgumentShape
    | arenaSelfContainedListRebuild: Bool
    | freshClosureRebuild: Bool
    | bytesProvenanceSafeListRebuild: Bool
    | useMode: TcoParamUseMode
    | reuseAffinity: TcoParamReuseAffinity
    deriving {Eq, Show}

type FunctionOwnershipSummary =
    | functionName: Str
    | origin: SourceFunctionOrigin
    | parameters: List(Str)
    | parameterOwnership: List((Str, ParameterOwnership))
    | borrowedParameters: List(Str)
    | consumedParameters: List(Str)
    | uniqueParameters: List(Str)
    | callCensus: FunctionCallCensus
    | parameterMoveSafety: List((Str, ParameterMoveSafetyProof))
    | capturedValues: List(Str)
    | resultReachFacts: FunctionResultReachFacts
    | resultProvenance: FunctionResultProvenance
    | tcoParamFacts: List(TcoParamStructuralFacts)
    | mayExecuteUnderLiveHandlerPost: Bool
    deriving {Eq, Show}

let isCallCensusComplete (census: FunctionCallCensus) =
    match census with
        | FunctionCallCensus { causes = causes } ->
            match causes with
                | [] -> true
                | head :: tail ->
                    match tail with
                        | [] -> head == CensusCauseNone
                        | _ -> false

let isResultPoisoned (facts: FunctionResultReachFacts) =
    match facts with
        | FunctionResultReachFacts { isPoisoned = poisoned } -> poisoned

let isResultFresh (facts: FunctionResultReachFacts) =
    match facts with
        | FunctionResultReachFacts { parameterReach = reach, isPoisoned = poisoned } ->
            if poisoned
            then false
            else
                match reach with
                    | [] -> true
                    | _ -> false

let recursive findParameterReach entries param =
    match entries with
        | [] -> 0
        | entry :: rest ->
            match entry with
                | ParameterReachEntry { parameterName = name, reachCount = count } ->
                    if name == param || Ashes.Text.startsWith(name)(param + "/")
                    then count + findParameterReach(rest)(param)
                    else findParameterReach(rest)(param)

let resultReachesParameter facts param =
    match facts with
        | FunctionResultReachFacts { parameterReach = reach } -> findParameterReach(reach)(param) > 0

let recursive containsParameterName (names: List(Str)) (param: Str) =
    match names with
        | [] -> false
        | name :: rest ->
            if name == param
            then true
            else containsParameterName(rest)(param)

let resultReachesParameterWhole facts param =
    match facts with
        | FunctionResultReachFacts { wholeParameterReach = whole } -> containsParameterName(whole)(param)

let recursive collectByOwnership (target: ParameterOwnership) (pairs: List((Str, ParameterOwnership))) (acc: List(Str)) =
    match pairs with
        | [] -> acc
        | entry :: rest ->
            match entry with
                | (name, own) ->
                    if own == target
                    then collectByOwnership(target)(rest)(append(acc)([name]))
                    else collectByOwnership(target)(rest)(acc)

let getBorrowedParameters (pairs: List((Str, ParameterOwnership))) = collectByOwnership(Borrowed)(pairs)([])

let getConsumedParameters (pairs: List((Str, ParameterOwnership))) = collectByOwnership(Consumed)(pairs)([])
