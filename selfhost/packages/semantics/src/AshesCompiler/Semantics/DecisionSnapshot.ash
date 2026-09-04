// Represents compilation decision snapshots and explanation metadata.
//
// Invariants:
// - Snapshots are immutable, deterministic, and retain no inference state or AST subgraphs.
// - Each record carries an explicit ordinal so consumers can impose a total order.
// - Reason codes and categories remain structural enums rather than formatted text.
// - Capturing a snapshot reads the parsed program and the lowered IR and changes neither.

import Ashes.Collection.List.append
import Ashes.Collection.List.filter
import Ashes.Collection.List.length
import Ashes.Collection.List.map
import Ashes.Collection.List.reverse
import Ashes.Collection.List.sortBy
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoroutineFrame
import AshesCompiler.Semantics.FunctionOrigins
import AshesCompiler.Semantics.HoverTypeInfo
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.OwnershipInference
import AshesCompiler.Semantics.OwnershipSummary
import AshesCompiler.Semantics.ReuseDecision
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
    value emptyDecisionSnapshot,
    value captureDecisionSnapshot,
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
    | reuseDecisions: List(ReuseDecision)
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
                | FunctionOwnershipRecord { origin = origin } ->
                    if origin == targetOrigin
                    then filterOwnershipForAux(targetOrigin)(tail)(head :: acc)
                    else filterOwnershipForAux(targetOrigin)(tail)(acc)

let filterOwnershipFor (targetOrigin: SourceFunctionOrigin) (snapshot: CompilationDecisionSnapshot) =
    match snapshot with
        | CompilationDecisionSnapshot { functionOwnership = ownerships } -> filterOwnershipForAux(targetOrigin)(ownerships)([])

let recursive filterPlacementsInAux (generatedLabel: Str) (records: List(ValuePlacementRecord)) (acc: List(ValuePlacementRecord)) =
    match records with
        | [] -> reverse(acc)
        | head :: tail ->
            match head with
                | ValuePlacementRecord { functionOrigin = Some(IrFunctionOrigin { generatedLabel = label }) } ->
                    if label == generatedLabel
                    then filterPlacementsInAux(generatedLabel)(tail)(head :: acc)
                    else filterPlacementsInAux(generatedLabel)(tail)(acc)
                | ValuePlacementRecord { functionOrigin = None } -> filterPlacementsInAux(generatedLabel)(tail)(acc)

let filterPlacementsIn (generatedLabel: Str) (snapshot: CompilationDecisionSnapshot) =
    match snapshot with
        | CompilationDecisionSnapshot { valuePlacements = placements } -> filterPlacementsInAux(generatedLabel)(placements)([])

let emptyDecisionSnapshot =
    CompilationDecisionSnapshot(
        functionOwnership = [],
        valuePlacements = [],
        reuseDecisions = [],
        coroutineRepresentations = [],
        patternBindings = [],
        externalResources = [],
        publicAuthority = [],
        externalAuthority = []
    )

// The source origin lowering assigned to the lifted lambda of a let-bound function, so an ownership
// record correlates with the generated functions that carry the same origin.
let recursive loweredSourceOrigin (name: Str) (functions: List(IrFunction)) =
    match functions with
        | [] -> None
        | IrFunction { origin = Some(IrFunctionOrigin { originKind = SourceFunctionOriginKind, sourceOrigin = Some(origin) }) } :: rest ->
            match origin with
                | SourceFunctionOrigin { functionSourceName = sourceName } ->
                    if sourceName == name
                    then Some(origin)
                    else loweredSourceOrigin(name)(rest)
        | _ :: rest -> loweredSourceOrigin(name)(rest)

let sourceOriginFor qualifiedNameOf (lowered: IrProgram) (name: Str) =
    match loweredSourceOrigin(name)(lowered.functions) with
        | Some(origin) -> origin with functionQualifiedName = qualifiedNameOf(name)
        | None ->
            createSourceFunctionOrigin(name)(qualifiedNameOf(name))(None)(0)

let hasParameters (entry: (Str, List(Str), Expr)) =
    match entry with
        | (_name, [], _body) -> false
        | _ -> true

let functionSignature qualifiedNameOf (lowered: IrProgram) (entry: (Str, List(Str), Expr)) =
    match entry with
        | (name, parameters, body) ->
            FunctionSignature(
                name = name,
                origin = sourceOriginFor(qualifiedNameOf)(lowered)(name),
                parameters = parameters,
                body = body
            )

let textBefore (left: Str) (right: Str) = Ashes.Text.compare(left)(right) <= 0

let qualifiedNameText (origin: SourceFunctionOrigin) =
    match origin with
        | SourceFunctionOrigin { functionQualifiedName = Some(qualified) } -> qualified
        | SourceFunctionOrigin { functionQualifiedName = None } -> ""

// Summaries order by qualified name, then source name, then declaration offset, so two compilations
// of one program list functions identically.
let summaryBefore (left: FunctionOwnershipSummary) (right: FunctionOwnershipSummary) =
    match (left, right) with
        | (FunctionOwnershipSummary { origin = leftOrigin }, FunctionOwnershipSummary { origin = rightOrigin }) ->
            match (leftOrigin, rightOrigin) with
                | (SourceFunctionOrigin { functionSourceName = leftName, declarationOffset = leftOffset }, SourceFunctionOrigin { functionSourceName = rightName, declarationOffset = rightOffset }) ->
                    match rightOrigin
                    |> qualifiedNameText
                    |> Ashes.Text.compare(qualifiedNameText(leftOrigin)) with
                        | 0 ->
                            match Ashes.Text.compare(leftName)(rightName) with
                                | 0 -> leftOffset <= rightOffset
                                | order -> order < 0
                        | order -> order < 0

let recursive moveSafeNames (proofs: List((Str, ParameterMoveSafetyProof))) =
    match proofs with
        | [] -> []
        | (name, ParameterMoveSafetyProof { isMoveSafe = true }) :: rest -> name :: moveSafeNames(rest)
        | _ :: rest -> moveSafeNames(rest)

// The move-safe parameters are the unique ones, listed by name so the order never depends on the
// analysis' own.
let uniqueParametersOf (proofs: List((Str, ParameterMoveSafetyProof))) =
    proofs
    |> moveSafeNames
    |> sortBy(textBefore)

let ownershipRecord (ordinal: Int) (summary: FunctionOwnershipSummary) =
    match summary with
        | FunctionOwnershipSummary { functionName = name, origin = origin, parameters = parameters, borrowedParameters = borrowed, consumedParameters = consumed, parameterMoveSafety = proofs, capturedValues = captured, resultReachFacts = facts, resultProvenance = provenance, mayExecuteUnderLiveHandlerPost = live } ->
            FunctionOwnershipRecord(
                ordinal = ordinal,
                origin = origin,
                functionName = name,
                parameters = parameters,
                borrowedParameters = borrowed,
                consumedParameters = consumed,
                uniqueParameters = uniqueParametersOf(proofs),
                capturedValues = captured,
                resultProvenance = Ashes.Trait.Show.show(provenance),
                resultAliases = filter(resultReachesParameter(facts))(parameters),
                resultFresh = isResultFresh(facts),
                resultPoisoned = isResultPoisoned(facts),
                mayExecuteUnderLiveHandlerPost = live
            )

let recursive ownershipRecords (ordinal: Int) (summaries: List(FunctionOwnershipSummary)) =
    match summaries with
        | [] -> []
        | summary :: rest -> ownershipRecord(ordinal)(summary) :: ownershipRecords(ordinal + 1)(rest)

// A bare constructor reference (`Some`, `Dot`, ...) parses as an ordinary variable read, so the
// free-variable walk behind `capturedValues` cannot itself tell it apart from a genuine closure
// capture over an enclosing module-level value — nor can a program-scanned constructor list, since
// a builtin type's constructors (`Maybe`'s `Some`/`None`) never appear as a `TopLevelItem` in a
// single-file lowering. A capture is only ever a reference to another top-level *value* binding
// (a parameterless `let`), so keeping just the names the program actually declares that way is
// both the narrower and the correct fix.
let recursive topLevelValueNames (entries: List((Str, List(Str), Expr))) =
    match entries with
        | [] -> []
        | (name, [], _body) :: rest -> name :: topLevelValueNames(rest)
        | _ :: rest -> topLevelValueNames(rest)

let recursive containsValueName (valueNames: List(Str)) (name: Str) =
    match valueNames with
        | [] -> false
        | head :: rest ->
            if head == name
            then true
            else containsValueName(rest)(name)

let restrictCapturesToTopLevelValues (valueNames: List(Str)) (record: FunctionOwnershipRecord) =
    match record with
        | FunctionOwnershipRecord { capturedValues = captured } ->
            record with capturedValues = filter(containsValueName(valueNames))(captured)

// Every data constructor the program declares, paired with its field count — the move-safety call
// census's seed for recognizing a saturated constructor application as a fully-fresh value.
let recursive constructorArityEntries (constructors: List(TypeConstructor)) =
    match constructors with
        | [] -> []
        | TypeConstructor { name = name, parameters = parameters } :: rest -> (name, length(parameters)) :: constructorArityEntries(rest)

let recursive declaredConstructorArities (item: TopLevelItem) =
    match item with
        | TopLevelAt(_span, inner) -> declaredConstructorArities(inner)
        | TopLevelType(TypeDecl { constructors = constructors }) -> constructorArityEntries(constructors)
        | TopLevelZeroCostType(ZeroCostTypeDecl { constructor = TypeConstructor { name = name, parameters = parameters } }) -> [(name, length(parameters))]
        | _ -> []

let recursive programConstructorArities (items: List(TopLevelItem)) =
    match items with
        | [] -> []
        | item :: rest ->
            rest
            |> programConstructorArities
            |> append(declaredConstructorArities(item))

// The read-only record of what a compilation decided: every let-bound function of the parsed
// program with its inferred ownership, correlated with the lowered IR through the source origins
// lowering assigned. `qualifiedNameOf` maps a binding name to its module-qualified name, or `None`
// when the program has no module context. Reuse decisions, coroutine representations, and pattern
// bindings are not retained by the self-hosted lowering yet.
// --- Representation walk over the lowered IR (arena/runtime-managed, for the `memory` report) ---
//
// `recordValuePlacement` (in CoreLowering.ash) already records every lowered value's function
// origin and whether its resolved type is copy-typed — enough, via `classifyValuePlacement`, to
// report every scalar correctly regardless of how it was produced. What remains is Region vs
// RuntimeRc vs ConservativeUnknown for the non-scalar values, which this reads straight off the
// already-emitted instructions rather than re-deriving during lowering: most heap-producing
// instructions carry their own runtime-managed bit (the same one IrText.ash renders as
// `RuntimeManaged=...`), and the rest — a slot reload, a borrow, a runtime-managed duplicate —
// only need to forward whichever representation their source temp already carries. A temp this
// walk cannot place (an indirect closure call's result, an environment capture) stays
// ConservativeUnknown, exactly like an unplaced temp in stage 0's own default fact.
let recursive lookupTempRepr (temp: Int) (reprs: List((Int, Bool, Bool))) =
    match reprs with
        | [] -> (false, false)
        | (t, isArena, isRc) :: rest ->
            if t == temp
            then (isArena, isRc)
            else lookupTempRepr(temp)(rest)

let recursive lookupSlotRepr (slot: Int) (slots: List((Int, Bool, Bool))) =
    match slots with
        | [] -> (false, false)
        | (s, isArena, isRc) :: rest ->
            if s == slot
            then (isArena, isRc)
            else lookupSlotRepr(slot)(rest)

let setTempRepr (temp: Int) (isArena: Bool) (isRc: Bool) (reprs: List((Int, Bool, Bool))) = (temp, isArena, isRc) :: reprs

let setSlotRepr (slot: Int) (isArena: Bool) (isRc: Bool) (slots: List((Int, Bool, Bool))) = (slot, isArena, isRc) :: slots

// A `target,...,runtimeManaged` instruction's representation follows the flag directly: arena when
// clear, runtime-managed when set.
let reprOfFlag (runtimeManaged: Bool) = (runtimeManaged == false, runtimeManaged)

let recursive classifyInstructionRepr (kind: IrInstructionKind) (reprs: List((Int, Bool, Bool))) (slots: List((Int, Bool, Bool))) =
    match kind with
        | LoadConstStr(target, _value) -> (setTempRepr(target)(false)(false)(reprs), slots)
        | Alloc(target, _sizeBytes, runtimeManaged) ->
            match reprOfFlag(runtimeManaged) with
                | (isArena, isRc) -> (setTempRepr(target)(isArena)(isRc)(reprs), slots)
        | AllocStack(target, _sizeBytes) -> (setTempRepr(target)(true)(false)(reprs), slots)
        | AllocAdt(target, _tag, _fieldCount, runtimeManaged, _tagless) ->
            match reprOfFlag(runtimeManaged) with
                | (isArena, isRc) -> (setTempRepr(target)(isArena)(isRc)(reprs), slots)
        | AllocAdtStack(target, _tag, _fieldCount, _tagless) -> (setTempRepr(target)(true)(false)(reprs), slots)
        | AllocAdtToSpace(target, _tag, _fieldCount, _tagless) -> (setTempRepr(target)(true)(false)(reprs), slots)
        | MakeClosure(target, _label, _env, _size, runtimeManaged, _returnsRm, _acceptsRm) ->
            match reprOfFlag(runtimeManaged) with
                | (isArena, isRc) -> (setTempRepr(target)(isArena)(isRc)(reprs), slots)
        | MakeClosureStack(target, _label, _env, _size, _returnsRm, _acceptsRm) -> (setTempRepr(target)(true)(false)(reprs), slots)
        | RcDup(target, source, runtimeManaged, _mayBeEmpty) ->
            if runtimeManaged
            then (setTempRepr(target)(false)(true)(reprs), slots)
            else
                match lookupTempRepr(source)(reprs) with
                    | (isArena, isRc) -> (setTempRepr(target)(isArena)(isRc)(reprs), slots)
        | Borrow(target, source) ->
            match lookupTempRepr(source)(reprs) with
                | (isArena, isRc) -> (setTempRepr(target)(isArena)(isRc)(reprs), slots)
        | LoadLocal(target, slot) ->
            match lookupSlotRepr(slot)(slots) with
                | (isArena, isRc) -> (setTempRepr(target)(isArena)(isRc)(reprs), slots)
        | StoreLocal(slot, source) ->
            match lookupTempRepr(source)(reprs) with
                | (isArena, isRc) -> (reprs, setSlotRepr(slot)(isArena)(isRc)(slots))
        | TextFromInt(target, _value, runtimeManaged) ->
            match reprOfFlag(runtimeManaged) with
                | (isArena, isRc) -> (setTempRepr(target)(isArena)(isRc)(reprs), slots)
        | TextFromFloat(target, _value, runtimeManaged) ->
            match reprOfFlag(runtimeManaged) with
                | (isArena, isRc) -> (setTempRepr(target)(isArena)(isRc)(reprs), slots)
        | TextParseInt(target, _value, runtimeManaged) ->
            match reprOfFlag(runtimeManaged) with
                | (isArena, isRc) -> (setTempRepr(target)(isArena)(isRc)(reprs), slots)
        | TextParseFloat(target, _value, runtimeManaged) ->
            match reprOfFlag(runtimeManaged) with
                | (isArena, isRc) -> (setTempRepr(target)(isArena)(isRc)(reprs), slots)
        | TextToHex(target, _value, runtimeManaged) ->
            match reprOfFlag(runtimeManaged) with
                | (isArena, isRc) -> (setTempRepr(target)(isArena)(isRc)(reprs), slots)
        | RuneToText(target, _value, runtimeManaged) ->
            match reprOfFlag(runtimeManaged) with
                | (isArena, isRc) -> (setTempRepr(target)(isArena)(isRc)(reprs), slots)
        | BigIntFromInt(target, _value, runtimeManaged) ->
            match reprOfFlag(runtimeManaged) with
                | (isArena, isRc) -> (setTempRepr(target)(isArena)(isRc)(reprs), slots)
        | BigIntToString(target, _value, runtimeManaged) ->
            match reprOfFlag(runtimeManaged) with
                | (isArena, isRc) -> (setTempRepr(target)(isArena)(isRc)(reprs), slots)
        | BigIntFromString(target, _value, runtimeManaged) ->
            match reprOfFlag(runtimeManaged) with
                | (isArena, isRc) -> (setTempRepr(target)(isArena)(isRc)(reprs), slots)
        | BigIntBinary(target, _left, _right, _op, runtimeManaged) ->
            match reprOfFlag(runtimeManaged) with
                | (isArena, isRc) -> (setTempRepr(target)(isArena)(isRc)(reprs), slots)
        | ConcatStr(target, _left, _right, runtimeManaged) ->
            match reprOfFlag(runtimeManaged) with
                | (isArena, isRc) -> (setTempRepr(target)(isArena)(isRc)(reprs), slots)
        | ConcatStrTip(target, _left, _right, _cursor, _end, runtimeManaged) ->
            match reprOfFlag(runtimeManaged) with
                | (isArena, isRc) -> (setTempRepr(target)(isArena)(isRc)(reprs), slots)
        | ConcatStrN(target, _parts, runtimeManaged) ->
            match reprOfFlag(runtimeManaged) with
                | (isArena, isRc) -> (setTempRepr(target)(isArena)(isRc)(reprs), slots)
        | BytesEmpty(target, runtimeManaged) ->
            match reprOfFlag(runtimeManaged) with
                | (isArena, isRc) -> (setTempRepr(target)(isArena)(isRc)(reprs), slots)
        | BytesSingleton(target, _value, runtimeManaged) ->
            match reprOfFlag(runtimeManaged) with
                | (isArena, isRc) -> (setTempRepr(target)(isArena)(isRc)(reprs), slots)
        | BytesAppend(target, _bytes, _value, runtimeManaged) ->
            match reprOfFlag(runtimeManaged) with
                | (isArena, isRc) -> (setTempRepr(target)(isArena)(isRc)(reprs), slots)
        | BytesAllocate(target, _size, runtimeManaged) ->
            match reprOfFlag(runtimeManaged) with
                | (isArena, isRc) -> (setTempRepr(target)(isArena)(isRc)(reprs), slots)
        | BytesFromList(target, _list, runtimeManaged) ->
            match reprOfFlag(runtimeManaged) with
                | (isArena, isRc) -> (setTempRepr(target)(isArena)(isRc)(reprs), slots)
        | CopyOutArena(destTemp, _srcTemp, _staticSizeBytes, runtimeManaged, _purpose, _deferredElementType) ->
            match reprOfFlag(runtimeManaged) with
                | (isArena, isRc) -> (setTempRepr(destTemp)(isArena)(isRc)(reprs), slots)
        | CopyOutList(destTemp, _srcTemp, _headCopy, runtimeManaged, _purpose) ->
            match reprOfFlag(runtimeManaged) with
                | (isArena, isRc) -> (setTempRepr(destTemp)(isArena)(isRc)(reprs), slots)
        | CopyOutClosure(destTemp, _srcTemp, runtimeManaged, _purpose) ->
            match reprOfFlag(runtimeManaged) with
                | (isArena, isRc) -> (setTempRepr(destTemp)(isArena)(isRc)(reprs), slots)
        | CallKnown(target, _name, _a1, _a2, _a3, runtimeManaged) ->
            match reprOfFlag(runtimeManaged) with
                | (isArena, isRc) -> (setTempRepr(target)(isArena)(isRc)(reprs), slots)
        | _ -> (reprs, slots)

let recursive walkFunctionInstructions (instructions: List(IrInstruction)) (reprs: List((Int, Bool, Bool))) (slots: List((Int, Bool, Bool))) =
    match instructions with
        | [] -> reprs
        | IrInstruction { instruction = kind } :: rest ->
            match classifyInstructionRepr(kind)(reprs)(slots) with
                | (nextReprs, nextSlots) -> walkFunctionInstructions(rest)(nextReprs)(nextSlots)

// Every function's temp -> (isArena, isRc) map, keyed by generated label so it can be looked up
// per value-placement entry.
let functionRepr (function: IrFunction) =
    match function with
        | IrFunction { label = label, instructions = instructions } -> (label, walkFunctionInstructions(instructions)([])([]))

let recursive programRepr (functions: List(IrFunction)) =
    match functions with
        | [] -> []
        | function :: rest -> functionRepr(function) :: programRepr(rest)

let recursive lookupFunctionRepr (label: Str) (reprs: List((Str, List((Int, Bool, Bool))))) =
    match reprs with
        | [] -> []
        | (candidate, temps) :: rest ->
            if candidate == label
            then temps
            else lookupFunctionRepr(label)(rest)

let originGeneratedLabel (origin: Maybe(IrFunctionOrigin)) =
    match origin with
        | Some(IrFunctionOrigin { generatedLabel = label }) -> Some(label)
        | None -> None

// One placement record per (temp, origin, isCopyType) fact `recordValuePlacement` captured during
// lowering, with its representation looked up from the matching function's instruction walk.
let recursive placementRecordsFrom (ordinal: Int) (functionReprs: List((Str, List((Int, Bool, Bool))))) (entries: List((Int, Maybe(IrFunctionOrigin), Bool))) =
    match entries with
        | [] -> []
        | (temp, origin, isCopyType) :: rest ->
            match match originGeneratedLabel(origin) with
                | Some(label) ->
                    functionReprs
                    |> lookupFunctionRepr(label)
                    |> lookupTempRepr(temp)
                | None -> (false, false) with
                | (isArena, isRc) ->
                    ValuePlacementRecord(
                        ordinal = ordinal,
                        functionOrigin = origin,
                        temp = temp,
                        placement = classifyValuePlacement(false)(isCopyType)(isArena)(isRc)(false)(false),
                        ownership = TempOwned,
                        producer = ProducerOther,
                        dropKind = DropNone,
                        reason = ReasonDefault,
                        typeName = None,
                        location = None
                    ) :: placementRecordsFrom(ordinal + 1)(functionReprs)(rest)

// The read-only record of what a compilation decided: every let-bound function of the parsed
// program with its inferred ownership, correlated with the lowered IR through the source origins
// lowering assigned, plus every value's placement, correlated the same way through the function
// origin `recordValuePlacement` stamped on it during lowering. `qualifiedNameOf` maps a binding
// name to its module-qualified name, or `None` when the program has no module context.
// `valuePlacements` is the `(temp, functionOrigin, isCopyType)` list `CoreLoweringResult` carries
// out of lowering. Reuse decisions, coroutine representations, and pattern bindings are not
// retained by the self-hosted lowering yet.
let captureDecisionSnapshot qualifiedNameOf (program: ProgramSyntax) (lowered: IrProgram) (valuePlacements: List((Int, Maybe(IrFunctionOrigin), Bool))) =
    (let valueNames =
        program
        |> topLevelFunctions
        |> topLevelValueNames
    in
        let constructorArities = programConstructorArities(program.items)
        in
            let functionReprs = programRepr(lowered.functions)
            in
                let placements =
                    valuePlacements
                    |> reverse
                    |> placementRecordsFrom(0)(functionReprs)
                in
                    program
                    |> topLevelFunctions
                    |> filter(hasParameters)
                    |> map(functionSignature(qualifiedNameOf)(lowered))
                    |> (given (signatures) ->
                        program
                        |> topLevelFunctions
                        |> inferProgramOwnership(signatures)([])(constructorArities)(program.body))
                    |> sortBy(summaryBefore)
                    |> ownershipRecords(0)
                    |> map(restrictCapturesToTopLevelValues(valueNames))
                    |> (given (records) -> emptyDecisionSnapshot with functionOwnership = records, valuePlacements = placements))
