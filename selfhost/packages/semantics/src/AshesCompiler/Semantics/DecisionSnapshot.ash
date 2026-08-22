// Represents compilation decision snapshots and explanation metadata.
//
// Invariants:
// - Snapshots are immutable, deterministic, and retain no inference state or AST subgraphs.
// - Each record carries an explicit ordinal so consumers can impose a total order.
// - Reason codes and categories remain structural enums rather than formatted text.

import Ashes.Collection.List.append
import Ashes.Collection.List.reverse
import AshesCompiler.Semantics.CoroutineFrame
import AshesCompiler.Semantics.HoverTypeInfo
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOrigins
export (
    type ValuePlacementCategory(..),
    type LoweredTempOwnershipKind(..),
    type LoweredTempProducerKind(..),
    type LoweredTempDropKind(..),
    type LoweredTempOwnershipReason(..),
    type ValuePlacementRecord(..),
    type FunctionOwnershipRecord(..),
    type PatternBindingRecord(..),
    type ExternalResourceParameterRecord(..),
    type ExternalResourceOwnershipRecord(..),
    type ExternalAuthorityRecord(..),
    type CompilationDecisionSnapshot(..),
    value classifyValuePlacement,
    value filterOwnershipFor,
    value filterPlacementsIn,
)

type ValuePlacementCategory =
    | ConservativeUnknown
    | Region
    | RuntimeRc
    | BorrowedView
    | TaskFrameOwned
    | WorkerTransfer
    | CopyValue
    deriving {Eq, Show}

type LoweredTempOwnershipKind =
    | TempOwned
    | TempBorrowed
    | TempShared
    | TempNone
    deriving {Eq, Show}

type LoweredTempProducerKind =
    | ProducerLiteral
    | ProducerCall
    | ProducerConstructor
    | ProducerTuple
    | ProducerExtract
    | ProducerLoad
    | ProducerOther
    deriving {Eq, Show}

type LoweredTempDropKind =
    | DropRc
    | DropResource
    | DropArena
    | DropNone
    deriving {Eq, Show}

type LoweredTempOwnershipReason =
    | ReasonInitialValue
    | ReasonReturnJoin
    | ReasonMoved
    | ReasonEscapes
    | ReasonBorrowed
    | ReasonDefault
    deriving {Eq, Show}

type ValuePlacementRecord =
    | ordinal: Int
    | functionOrigin: Maybe(IrFunctionOrigin)
    | temp: IrTemp
    | placement: ValuePlacementCategory
    | ownership: LoweredTempOwnershipKind
    | producer: LoweredTempProducerKind
    | dropKind: LoweredTempDropKind
    | reason: LoweredTempOwnershipReason
    | typeName: Maybe(Str)
    | location: Maybe(IrSourceLocation)
    deriving {Eq, Show}

type FunctionOwnershipRecord =
    | ordinal: Int
    | origin: SourceFunctionOrigin
    | functionName: Str
    | parameters: List(Str)
    | borrowedParameters: List(Str)
    | consumedParameters: List(Str)
    | uniqueParameters: List(Str)
    | capturedValues: List(Str)
    | resultProvenance: Str
    | resultAliases: List(Str)
    | resultFresh: Bool
    | resultPoisoned: Bool
    | mayExecuteUnderLiveHandlerPost: Bool
    deriving {Eq, Show}

type PatternBindingRecord =
    | ordinal: Int
    | functionOrigin: Maybe(SourceFunctionOrigin)
    | bindingOrdinal: Int
    | bindingName: Str
    | rootParameterName: Str
    | extractionDepth: Int
    | uses: Str
    | ownership: Str
    | placementOutcome: Str
    | location: Maybe(IrSourceLocation)
    deriving {Eq, Show}

type ExternalResourceParameterRecord =
    | functionName: Str
    | parameterIndex: Int
    | typeName: Str
    | ownership: Str
    deriving {Eq, Show}

type ExternalResourceOwnershipRecord =
    | typeName: Str
    | destructor: Str
    | parameters: List(ExternalResourceParameterRecord)
    deriving {Eq, Show}

type ExternalAuthorityRecord =
    | functionName: Str
    | runtimeCapabilities: List(Str)
    deriving {Eq, Show}

type CompilationDecisionSnapshot =
    | functionOwnership: List(FunctionOwnershipRecord)
    | valuePlacements: List(ValuePlacementRecord)
    | coroutineRepresentations: List(CoroutineRepresentationRecord)
    | patternBindings: List(PatternBindingRecord)
    | externalResources: List(ExternalResourceOwnershipRecord)
    | publicAuthority: List(PublicAuthorityRecord)
    | externalAuthority: List(ExternalAuthorityRecord)
    deriving {Eq, Show}

let classifyValuePlacement (isBorrowed: Bool) (isCopyType: Bool) (isArena: Bool) (isRc: Bool) (isTaskFrame: Bool) (isWorkerTransfer: Bool) =
    if isBorrowed
    then BorrowedView
    else
        if isCopyType
        then CopyValue
        else
            if isTaskFrame
            then TaskFrameOwned
            else
                if isWorkerTransfer
                then WorkerTransfer
                else
                    if isArena
                    then Region
                    else
                        if isRc
                        then RuntimeRc
                        else ConservativeUnknown

let recursive filterOwnershipForAux (targetOrigin: SourceFunctionOrigin) (records: List(FunctionOwnershipRecord)) (acc: List(FunctionOwnershipRecord)) =
    match records with
        | [] -> reverse(acc)
        | head :: tail ->
            match head with
                | FunctionOwnershipRecord(_ord, origin, _f, _p, _b, _c, _u, _cap, _prov, _al, _fr, _poi, _live) ->
                    if origin == targetOrigin
                    then filterOwnershipForAux(targetOrigin)(tail)(head :: acc)
                    else filterOwnershipForAux(targetOrigin)(tail)(acc)

let filterOwnershipFor (targetOrigin: SourceFunctionOrigin) (snapshot: CompilationDecisionSnapshot) =
    match snapshot with
        | CompilationDecisionSnapshot(ownerships, _v, _c, _p, _ext, _pub, _ea) -> filterOwnershipForAux(targetOrigin)(ownerships)([])

let recursive filterPlacementsInAux (generatedLabel: Str) (records: List(ValuePlacementRecord)) (acc: List(ValuePlacementRecord)) =
    match records with
        | [] -> reverse(acc)
        | head :: tail ->
            match head with
                | ValuePlacementRecord(_ord, Some(IrFunctionOrigin(label, _k, _s, _p, _o, _d, _loc)), _t, _pl, _own, _prod, _drop, _r, _tn, _l) ->
                    if label == generatedLabel
                    then filterPlacementsInAux(generatedLabel)(tail)(head :: acc)
                    else filterPlacementsInAux(generatedLabel)(tail)(acc)
                | ValuePlacementRecord(_ord, None, _t, _pl, _own, _prod, _drop, _r, _tn, _l) -> filterPlacementsInAux(generatedLabel)(tail)(acc)

let filterPlacementsIn (generatedLabel: Str) (snapshot: CompilationDecisionSnapshot) =
    match snapshot with
        | CompilationDecisionSnapshot(_fo, placements, _c, _p, _ext, _pub, _ea) -> filterPlacementsInAux(generatedLabel)(placements)([])
