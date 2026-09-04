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
let captureDecisionSnapshot qualifiedNameOf (program: ProgramSyntax) (lowered: IrProgram) =
    (let valueNames =
        program
        |> topLevelFunctions
        |> topLevelValueNames
    in
        let constructorArities = programConstructorArities(program.items)
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
            |> (given (records) -> emptyDecisionSnapshot with functionOwnership = records))
