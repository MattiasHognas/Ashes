// TCO parameter placement, profitability, and cost signals.
//
// Invariants:
// - Parameter placement (Arena vs RuntimeRc vs Stack) is evaluated per-parameter.
// - Profitability signals ensure that if any sibling parameter permanently fails reset/RC
//   exemptions, promoting a candidate parameter does not reclaim memory per iteration and
//   is demoted as NotProfitable.
// - Placement snapshots retain history traces for diagnostics and compiler reports.

import Ashes.Collection.List.append
import Ashes.Collection.List.filter
import Ashes.Collection.List.length
import Ashes.Collection.List.map
import Ashes.Collection.List.reverse
import AshesCompiler.Semantics.Types
export (
    type TcoPromotionVerdict(..),
    type TcoPlacementRepresentation(..),
    type TcoPlacementReason(..),
    type TcoRcEligibilityReason(..),
    type TcoRuntimeManagedKind(..),
    type TcoPlacementTransitionKind(..),
    type TcoPlacementResolutionPoint(..),
    type TcoRcEligibility(..),
    type TcoParamStaticFacts(..),
    type TcoParamPlacementDecision(..),
    type TcoParamPlacementTrace(..),
    type TcoFunctionPlacementSnapshot(..),
    value evaluateTcoRcEligibility,
    value evaluateTcoPlacementProfitability,
    value findBlockingSiblingForCandidate,
    value getFinalTcoPlacementReason,
    value getTcoPlacementTransition,
    value formatTcoPromotionVerdict,
    value formatTcoPlacementReason,
)

type TcoPromotionVerdict =
    | TcoVerdictProfitable
    | TcoVerdictNotProfitable
    deriving {Eq, Show}

type TcoPlacementRepresentation =
    | TcoRepArena
    | TcoRepRuntimeRc
    | TcoRepStack
    deriving {Eq, Show}

type TcoPlacementReason =
    | TcoReasonEligible
    | TcoReasonNotEligible
    | TcoReasonEarlierPlacementRetained
    | TcoReasonShadowedBinding
    | TcoReasonAsyncBoundary
    | TcoReasonCoroutineBoundary
    | TcoReasonDynamicCapabilityBoundary
    | TcoReasonReuseAccumulator
    | TcoReasonSpecializationReuseAccumulator
    | TcoReasonStableReuseAccumulator
    | TcoReasonBlockingSiblingNotProfitable
    deriving {Eq, Show}

type TcoRcEligibilityReason =
    | TcoRcUnresolvedType
    | TcoRcScalarTupleOrAdtLayout
    | TcoRcFreshListRebuild
    | TcoRcAffineConsList
    | TcoRcConsumedListTail
    | TcoRcMissingListOwnershipShape
    | TcoRcUnsupportedListElementLayout
    | TcoRcFreshClosureRebuild
    | TcoRcLateClosureDeferred
    | TcoRcFreshClosureNotProven
    | TcoRcUnsupportedLayout
    deriving {Eq, Show}

type TcoRuntimeManagedKind =
    | TcoManagedNone
    | TcoManagedOrdinary
    | TcoManagedList
    | TcoManagedClosure
    deriving {Eq, Show}

type TcoPlacementTransitionKind =
    | TcoTransitionInitial
    | TcoTransitionPromotedAfterResolution
    | TcoTransitionRePromotedAfterResolution
    | TcoTransitionDemotedByFrameProfitability
    | TcoTransitionRetained
    deriving {Eq, Show}

type TcoPlacementResolutionPoint =
    | TcoPointEntry
    | TcoPointBody
    | TcoPointFinal
    deriving {Eq, Show}

type TcoRcEligibility =
    | ownershipShapeEligible: Bool
    | resolvedLayoutEligible: Bool
    | kind: TcoRuntimeManagedKind
    | reason: TcoRcEligibilityReason
    deriving {Eq, Show}

type TcoParamStaticFacts =
    | paramOrdinal: Int
    | paramName: Str
    | slot: Int
    | hasVisibleBinding: Bool
    | loopInvariant: Bool
    | freshRebuiltList: Bool
    | freshClosureRebuild: Bool
    | bytesProvenanceSafeListRebuild: Bool
    | affineConsList: Bool
    | consumedListTail: Bool
    | borrowInspectOnly: Bool
    | affineSelfAppendOnly: Bool
    deriving {Eq, Show}

type TcoParamPlacementDecision =
    | parameterOrdinal: Int
    | parameterName: Str
    | slot: Int
    | resolutionPoint: TcoPlacementResolutionPoint
    | representation: TcoPlacementRepresentation
    | reason: TcoPlacementReason
    | eligibility: TcoRcEligibility
    | resolvedType: Maybe(Str)
    | dynamicRestricted: Bool
    | profitability: Maybe(TcoPromotionVerdict)
    | decisiveBlocker: Maybe(Int)
    | transition: TcoPlacementTransitionKind
    | firstPromotedAt: Maybe(TcoPlacementResolutionPoint)
    deriving {Eq, Show}

type TcoParamPlacementTrace =
    | current: TcoParamPlacementDecision
    | history: List(TcoParamPlacementDecision)
    deriving {Eq, Show}

type TcoFunctionPlacementSnapshot =
    | functionName: Str
    | functionLabel: Str
    | parameters: List(TcoParamPlacementTrace)
    deriving {Eq, Show}

let notBool b =
    if b
    then false
    else true

let isRcEligibleScalarTupleOrAdt semType =
    match semType with
        | SemInt -> true
        | SemUInt(_) -> true
        | SemFloat -> true
        | SemBool -> true
        | SemRune -> true
        | SemString -> true
        | SemBigInt -> true
        | SemBytes -> true
        | SemTuple(_) -> true
        | SemNamed(_, _, _) -> true
        | _ -> false

let canRuntimeManageListElement elemType =
    match elemType with
        | SemVariable(_) -> false
        | SemFunction(_, _, _) -> false
        | _ -> true

let canArenaResetType semType =
    match semType with
        | SemInt -> true
        | SemUInt(_) -> true
        | SemFloat -> true
        | SemBool -> true
        | SemRune -> true
        | _ -> false

let evaluateTcoRcEligibility facts semTypeOpt includeFreshClosures =
    match semTypeOpt with
        | None ->
            TcoRcEligibility(
                ownershipShapeEligible = false,
                resolvedLayoutEligible = false,
                kind = TcoManagedNone,
                reason = TcoRcUnresolvedType
            )
        | Some(semType) ->
            match semType with
                | SemVariable(_) ->
                    TcoRcEligibility(
                        ownershipShapeEligible = false,
                        resolvedLayoutEligible = false,
                        kind = TcoManagedNone,
                        reason = TcoRcUnresolvedType
                    )
                | SemList(elem) ->
                    let layoutEligible = canRuntimeManageListElement(elem)
                    in
                        let consumedTail =
                            if facts.consumedListTail
                            then
                                if notBool(canArenaResetType(elem))
                                then notBool(facts.borrowInspectOnly)
                                else false
                            else false
                        in
                            let reason =
                                if facts.freshRebuiltList
                                then TcoRcFreshListRebuild
                                else
                                    if facts.affineConsList
                                    then TcoRcAffineConsList
                                    else
                                        if consumedTail
                                        then TcoRcConsumedListTail
                                        else
                                            if layoutEligible
                                            then TcoRcMissingListOwnershipShape
                                            else TcoRcUnsupportedListElementLayout
                            in
                                let shapeEligible =
                                    if facts.freshRebuiltList
                                    then true
                                    else
                                        if facts.affineConsList
                                        then true
                                        else consumedTail
                                in
                                    TcoRcEligibility(
                                        ownershipShapeEligible = shapeEligible,
                                        resolvedLayoutEligible = layoutEligible,
                                        kind = TcoManagedList,
                                        reason = reason
                                    )
                | SemFunction(_, _, _) ->
                    let reason =
                        if facts.freshClosureRebuild
                        then
                            if includeFreshClosures
                            then TcoRcFreshClosureRebuild
                            else TcoRcLateClosureDeferred
                        else TcoRcFreshClosureNotProven
                    in
                        let shapeEligible =
                            if facts.freshClosureRebuild
                            then includeFreshClosures
                            else false
                        in
                            TcoRcEligibility(
                                ownershipShapeEligible = shapeEligible,
                                resolvedLayoutEligible = true,
                                kind = TcoManagedClosure,
                                reason = reason
                            )
                | _ ->
                    if isRcEligibleScalarTupleOrAdt(semType)
                    then
                        TcoRcEligibility(
                            ownershipShapeEligible = true,
                            resolvedLayoutEligible = true,
                            kind = TcoManagedOrdinary,
                            reason = TcoRcScalarTupleOrAdtLayout
                        )
                    else
                        TcoRcEligibility(
                            ownershipShapeEligible = false,
                            resolvedLayoutEligible = false,
                            kind = TcoManagedNone,
                            reason = TcoRcUnsupportedLayout
                        )

let isPermanentlyBlockingParam facts semTypeOpt requestedRuntime =
    match semTypeOpt with
        | None -> false
        | Some(semType) ->
            if notBool(facts.hasVisibleBinding)
            then false
            else
                match semType with
                    | SemFunction(_, _, _) -> false
                    | _ ->
                        if canArenaResetType(semType)
                        then false
                        else
                            if facts.loopInvariant
                            then false
                            else
                                match semType with
                                    | SemList(elem) ->
                                        if facts.consumedListTail
                                        then
                                            if canArenaResetType(elem)
                                            then false
                                            else
                                                if facts.borrowInspectOnly
                                                then false
                                                else notBool(requestedRuntime)
                                        else notBool(requestedRuntime)
                                    | _ -> notBool(requestedRuntime)

let recursive findBlockingSibling siblings requestedRuntimes candidateOrdinal currentOrdinal =
    match (siblings, requestedRuntimes) with
        | ([], _) -> None
        | (_, []) -> None
        | ((facts, semTypeOpt) :: restSiblings, reqRuntime :: restReq) ->
            if currentOrdinal == candidateOrdinal
            then findBlockingSibling(restSiblings)(restReq)(candidateOrdinal)(currentOrdinal + 1)
            else
                if isPermanentlyBlockingParam(facts)(semTypeOpt)(reqRuntime)
                then Some(currentOrdinal)
                else findBlockingSibling(restSiblings)(restReq)(candidateOrdinal)(currentOrdinal + 1)

let findBlockingSiblingForCandidate siblings requestedRuntimes candidateOrdinal = findBlockingSibling(siblings)(requestedRuntimes)(candidateOrdinal)(0)

let evaluateTcoPlacementProfitability siblings requestedRuntimes candidateOrdinal runtimeRequested =
    if notBool(runtimeRequested)
    then (None, None)
    else
        match findBlockingSiblingForCandidate(siblings)(requestedRuntimes)(candidateOrdinal) with
            | Some(blocker) -> (Some(TcoVerdictNotProfitable), Some(blocker))
            | None -> (Some(TcoVerdictProfitable), None)

let getFinalTcoPlacementReason initialReason eligibility hasBlocker =
    if hasBlocker
    then TcoReasonBlockingSiblingNotProfitable
    else
        match initialReason with
            | TcoReasonEligible ->
                if notBool(eligibility.ownershipShapeEligible)
                then TcoReasonNotEligible
                else
                    if notBool(eligibility.resolvedLayoutEligible)
                    then TcoReasonNotEligible
                    else TcoReasonEligible
            | _ -> initialReason

let recursive historyHasRuntime (history: List(TcoParamPlacementDecision)) =
    match history with
        | [] -> false
        | decision :: rest ->
            match decision.representation with
                | TcoRepRuntimeRc -> true
                | _ -> historyHasRuntime(rest)

let getTcoPlacementTransition previousDecision history wasRuntime isRuntime finalReason =
    match previousDecision with
        | None -> TcoTransitionInitial
        | Some(_) ->
            if notBool(wasRuntime)
            then
                if isRuntime
                then
                    if historyHasRuntime(history)
                    then TcoTransitionRePromotedAfterResolution
                    else TcoTransitionPromotedAfterResolution
                else TcoTransitionRetained
            else
                if notBool(isRuntime)
                then
                    match finalReason with
                        | TcoReasonBlockingSiblingNotProfitable -> TcoTransitionDemotedByFrameProfitability
                        | _ -> TcoTransitionRetained
                else TcoTransitionRetained

let formatTcoPromotionVerdict verdict =
    match verdict with
        | TcoVerdictProfitable -> "Profitable"
        | TcoVerdictNotProfitable -> "NotProfitable"

let formatTcoPlacementReason reason =
    match reason with
        | TcoReasonEligible -> "Eligible"
        | TcoReasonNotEligible -> "NotEligible"
        | TcoReasonEarlierPlacementRetained -> "EarlierPlacementRetained"
        | TcoReasonShadowedBinding -> "ShadowedBinding"
        | TcoReasonAsyncBoundary -> "AsyncBoundary"
        | TcoReasonCoroutineBoundary -> "CoroutineBoundary"
        | TcoReasonDynamicCapabilityBoundary -> "DynamicCapabilityBoundary"
        | TcoReasonReuseAccumulator -> "ReuseAccumulator"
        | TcoReasonSpecializationReuseAccumulator -> "SpecializationReuseAccumulator"
        | TcoReasonStableReuseAccumulator -> "StableReuseAccumulator"
        | TcoReasonBlockingSiblingNotProfitable -> "BlockingSiblingNotProfitable"
