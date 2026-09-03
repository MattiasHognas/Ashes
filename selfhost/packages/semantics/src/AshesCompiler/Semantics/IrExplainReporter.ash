// Builds the reportable view of a compilation from the decisions lowering recorded and the final
// semantic IR.
//
// Invariants:
// - The reference-count and concurrency reports read the optimized `IrProgram` handed to the
//   backend, so operation counts describe the code that ships rather than what lowering first emitted.
// - Every other section comes from the decision snapshot: those decisions were captured where they
//   were taken and cannot be recovered from instructions.
// - Reading a snapshot or a program cannot affect what was compiled.

import Ashes.Collection.List.append
import Ashes.Collection.List.filter
import Ashes.Collection.List.foldLeft
import Ashes.Collection.List.map
import AshesCompiler.Semantics.DecisionSnapshot
import AshesCompiler.Semantics.ExplainReport
import AshesCompiler.Semantics.HoverTypeInfo
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrFunctionSelection
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.OwnershipSummary
import AshesCompiler.Semantics.ReuseDecision
export (
    value buildExplainReport,
)

let recursive containsName (names: List(Str)) (name: Str) =
    match names with
        | [] -> false
        | head :: rest ->
            if head == name
            then true
            else containsName(rest)(name)

let containsIgnoringAsciiCase (candidate: Str) (selector: Str) =
    selector
    |> Ashes.Text.asciiLower
    |> Ashes.Text.contains(Ashes.Text.asciiLower(candidate))

// Borrowed and consumed partition the parameter list, so one lookup settles the other; a
// parameter the analysis proved safe to move is exactly one it reports as consumed.
let parameterReport (borrowed: List(Str)) (unique: List(Str)) (name: Str) =
    (let isBorrowed = containsName(borrowed)(name)
    in
        OwnershipParameterReport(
            name = name,
            ownership = if isBorrowed
            then Borrowed
            else Consumed,
            unique = containsName(unique)(name),
            moveSafe = isBorrowed == false
        ))

let ownershipReport (record: FunctionOwnershipRecord) =
    match record with
        | FunctionOwnershipRecord { origin = origin, functionName = name, parameters = parameters, borrowedParameters = borrowed, uniqueParameters = unique, capturedValues = captured, resultAliases = aliases, resultFresh = fresh, resultPoisoned = poisoned } ->
            OwnershipFunctionReport(
                functionName = name,
                origin = origin,
                parameters = map(parameterReport(borrowed)(unique))(parameters),
                resultAliases = aliases,
                resultFresh = fresh,
                resultPoisoned = poisoned,
                capturedValues = captured
            )

let ownershipRecordMatches (selector: Maybe(Str)) (record: FunctionOwnershipRecord) =
    match record with
        | FunctionOwnershipRecord { origin = origin, functionName = name } -> matchesSourceFunction(Some(origin))(name)(selector)

let buildOwnership (snapshot: CompilationDecisionSnapshot) (selector: Maybe(Str)) =
    match snapshot with
        | CompilationDecisionSnapshot { functionOwnership = records } ->
            records
            |> filter(ownershipRecordMatches(selector))
            |> map(ownershipReport)

let emptyRcReport (function: IrFunction) =
    match function with
        | IrFunction { label = label, origin = origin } ->
            RcFunctionReport(
                label = label,
                origin = origin,
                dups = 0,
                drops = 0,
                uniquenessChecks = 0,
                allocations = 0,
                reusedAllocations = 0,
                reuseTokens = 0,
                copies = 0
            )

let countRcOperation (report: RcFunctionReport) (instruction: IrInstruction) =
    match instruction with
        | IrInstruction { instruction = RcDup(_, _, _, _) } -> report with dups = report.dups + 1
        | IrInstruction { instruction = RcDrop(_, _, _, _, _, _) } -> report with drops = report.drops + 1
        | IrInstruction { instruction = RcIsUnique(_, _) } -> report with uniquenessChecks = report.uniquenessChecks + 1
        | IrInstruction { instruction = AllocReusing(_, _, _, _, _, _) } -> report with reusedAllocations = report.reusedAllocations + 1
        | IrInstruction { instruction = DropReuse(_, _, _, _) } -> report with reuseTokens = report.reuseTokens + 1
        | IrInstruction { instruction = Alloc(_, _, _) } -> report with allocations = report.allocations + 1
        | IrInstruction { instruction = AllocAdt(_, _, _, _) } -> report with allocations = report.allocations + 1
        | IrInstruction { instruction = CopyOutArena(_, _, _, _, _, _) } -> report with copies = report.copies + 1
        | IrInstruction { instruction = CopyOutList(_, _, _, _, _) } -> report with copies = report.copies + 1
        | _ -> report

let rcReport (function: IrFunction) =
    match function with
        | IrFunction { instructions = instructions } ->
            foldLeft(countRcOperation)(emptyRcReport(function))(instructions)

let functionMatches (selector: Maybe(Str)) (function: IrFunction) =
    match function with
        | IrFunction { label = label, origin = origin } -> matchesIrFunction(origin)(label)(selector)

let hasRcOperations (report: RcFunctionReport) = rcReportTotal(report) > 0

// Lifted functions first and the entry last, the order the program lists them in.
let buildRc (program: IrProgram) (selector: Maybe(Str)) =
    match program with
        | IrProgram { entryFunction = entry, functions = functions } ->
            [entry]
            |> append(functions)
            |> filter(functionMatches(selector))
            |> map(rcReport)
            |> filter(hasRcOperations)

let reuseDecisionMatches (selector: Maybe(Str)) (decision: ReuseDecision) =
    match decision with
        | ReuseDecision { functionOrigin = origin } ->
            match origin with
                | IrFunctionOrigin { generatedLabel = label } -> matchesIrFunction(Some(origin))(label)(selector)

let reuseFunctionName (origin: IrFunctionOrigin) =
    match origin with
        | IrFunctionOrigin { sourceOrigin = Some(SourceFunctionOrigin { functionSourceName = sourceName }) } -> sourceName
        | IrFunctionOrigin { generatedLabel = label } -> label

let reuseReport (decision: ReuseDecision) =
    match decision with
        | ReuseDecision { functionOrigin = origin, decision = kind, outcome = outcome, reason = reason, candidate = candidate, location = location } ->
            ReuseFunctionReport(
                functionName = reuseFunctionName(origin),
                origin = origin,
                decision = kind,
                outcome = outcome,
                reason = reason,
                candidate = candidate,
                location = location
            )

let buildReuse (snapshot: CompilationDecisionSnapshot) (selector: Maybe(Str)) =
    match snapshot with
        | CompilationDecisionSnapshot { reuseDecisions = decisions } ->
            decisions
            |> filter(reuseDecisionMatches(selector))
            |> map(reuseReport)

let placementLabel (origin: Maybe(IrFunctionOrigin)) =
    match origin with
        | Some(IrFunctionOrigin { generatedLabel = label }) -> label
        | None -> "<program>"

let recursive countCategory (category: ValuePlacementCategory) (counts: List((ValuePlacementCategory, Int))) =
    match counts with
        | [] -> [(category, 1)]
        | (existing, count) :: rest ->
            if existing == category
            then (existing, count + 1) :: rest
            else (existing, count) :: countCategory(category)(rest)

// Reports are kept in first-seen order and the matching one gains the placement.
let recursive countPlacement (reports: List(RepresentationFunctionReport)) (origin: Maybe(IrFunctionOrigin)) (category: ValuePlacementCategory) =
    (let label = placementLabel(origin)
    in
        match reports with
            | [] -> [RepresentationFunctionReport(label = label, origin = origin, placements = [(category, 1)])]
            | (RepresentationFunctionReport { label = existing, placements = placements } as report) :: rest ->
                if existing == label
                then (report with placements = countCategory(category)(placements)) :: rest
                else report :: countPlacement(rest)(origin)(category))

let addPlacement (selector: Maybe(Str)) (reports: List(RepresentationFunctionReport)) (record: ValuePlacementRecord) =
    match record with
        | ValuePlacementRecord { functionOrigin = origin, placement = category } ->
            if matchesIrFunction(origin)(placementLabel(origin))(selector)
            then countPlacement(reports)(origin)(category)
            else reports

let buildRepresentation (snapshot: CompilationDecisionSnapshot) (selector: Maybe(Str)) =
    match snapshot with
        | CompilationDecisionSnapshot { valuePlacements = placements } ->
            foldLeft(addPlacement(selector))([])(placements)

let countConcurrency (report: ConcurrencyFunctionReport) (instruction: IrInstruction) =
    match instruction with
        | IrInstruction { instruction = CreateScopedTask(_, _, _) } -> report with scopes = report.scopes + 1
        | IrInstruction { instruction = ForkScopedTask(_, _, _) } -> report with forks = report.forks + 1
        | IrInstruction { instruction = JoinScopedTask(_, _) } -> report with joins = report.joins + 1
        | IrInstruction { instruction = SpawnTask(_, _) } -> report with detachedSpawns = report.detachedSpawns + 1
        | _ -> report

let concurrencyReport (function: IrFunction) =
    match function with
        | IrFunction { label = label, origin = origin, instructions = instructions } ->
            foldLeft(countConcurrency)(
                ConcurrencyFunctionReport(label = label, origin = origin, scopes = 0, forks = 0, joins = 0, detachedSpawns = 0)
            )(
                instructions
            )

let hasConcurrency (report: ConcurrencyFunctionReport) =
    match report with
        | ConcurrencyFunctionReport { scopes = scopes, forks = forks, joins = joins, detachedSpawns = spawns } -> scopes + forks + joins + spawns > 0

// The entry first, then the lifted functions.
let buildConcurrency (program: IrProgram) (selector: Maybe(Str)) =
    match program with
        | IrProgram { entryFunction = entry, functions = functions } ->
            functions
            |> append([entry])
            |> filter(functionMatches(selector))
            |> map(concurrencyReport)
            |> filter(hasConcurrency)

let publicAuthorityMatches (selector: Str) (record: PublicAuthorityRecord) =
    match record with
        | PublicAuthorityRecord { bindingName = name } -> containsIgnoringAsciiCase(name)(selector)

let filterPublicAuthority (records: List(PublicAuthorityRecord)) (selector: Maybe(Str)) =
    match selector with
        | None -> records
        | Some(text) ->
            filter(publicAuthorityMatches(text))(records)

let externalAuthorityMatches (selector: Str) (record: ExternalAuthorityRecord) =
    match record with
        | ExternalAuthorityRecord { functionName = name } -> containsIgnoringAsciiCase(name)(selector)

let filterExternalAuthority (records: List(ExternalAuthorityRecord)) (selector: Maybe(Str)) =
    match selector with
        | None -> records
        | Some(text) ->
            filter(externalAuthorityMatches(text))(records)

let recursive anyParameterFunctionIs (selector: Str) (parameters: List(ExternalResourceParameterRecord)) =
    match parameters with
        | [] -> false
        | ExternalResourceParameterRecord { functionName = name } :: rest ->
            if name == selector
            then true
            else anyParameterFunctionIs(selector)(rest)

let externalResourceMatches (selector: Str) (resource: ExternalResourceOwnershipRecord) =
    match resource with
        | ExternalResourceOwnershipRecord { destructor = destructor, parameters = parameters } -> destructor == selector || anyParameterFunctionIs(selector)(parameters)

let filterExternalResources (resources: List(ExternalResourceOwnershipRecord)) (selector: Maybe(Str)) =
    match selector with
        | None -> resources
        | Some(text) ->
            filter(externalResourceMatches(text))(resources)

let recursive wantsAnyOf (kinds: List(ExplainKind)) (request: ExplainRequest) =
    match kinds with
        | [] -> false
        | kind :: rest -> explainRequestIncludes(kind)(request) || wantsAnyOf(rest)(request)

let selectIf (wanted: Bool) build =
    if wanted
    then build(Unit)
    else []

// Everything the requested reports need, built once. The memory report correlates ownership,
// reference counting, reuse, representation, and trait evidence, so asking for it gathers them all.
let buildExplainReport (snapshot: CompilationDecisionSnapshot) (finalIr: IrProgram) (request: ExplainRequest) =
    match request with
        | ExplainRequest { kinds = [] } -> emptyExplainReport
        | ExplainRequest { functionFilter = selector } ->
            match snapshot with
                | CompilationDecisionSnapshot { externalResources = resources, publicAuthority = publicAuthority, externalAuthority = externalAuthority } ->
                    CompilationExplainReport(
                        ownership = selectIf(wantsAnyOf([ExplainOwnership, ExplainMemory])(request))(given (_) -> buildOwnership(snapshot)(selector)),
                        rc = selectIf(wantsAnyOf([ExplainRc, ExplainMemory])(request))(given (_) -> buildRc(finalIr)(selector)),
                        reuse = selectIf(wantsAnyOf([ExplainReuse, ExplainMemory])(request))(given (_) -> buildReuse(snapshot)(selector)),
                        representation = selectIf(explainRequestIncludes(ExplainMemory)(request))(given (_) -> buildRepresentation(snapshot)(selector)),
                        traitEvidence = if wantsAnyOf([ExplainTraits, ExplainMemory])(request)
                        then finalIr.traitEvidence
                        else emptyTraitEvidenceAnnotations,
                        externalResources = selectIf(wantsAnyOf([ExplainOwnership, ExplainMemory])(request))(given (_) -> filterExternalResources(resources)(selector)),
                        authority = selectIf(explainRequestIncludes(ExplainAuthority)(request))(given (_) -> filterPublicAuthority(publicAuthority)(selector)),
                        externalAuthority = selectIf(explainRequestIncludes(ExplainAuthority)(request))(given (_) -> filterExternalAuthority(externalAuthority)(selector)),
                        concurrency = selectIf(explainRequestIncludes(ExplainConcurrency)(request))(given (_) -> buildConcurrency(finalIr)(selector))
                    )
