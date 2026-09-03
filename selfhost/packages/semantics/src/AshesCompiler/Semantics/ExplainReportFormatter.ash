// Renders a compilation explain report as text lines, one per output line.
//
// Invariants:
// - Formatting lives here rather than in the passes that produce the facts, so no semantic pass
//   writes prose or reaches a console.
// - Output is deterministic and snapshot-stable: section and function order follow the report's
//   own order, and nothing carries a timestamp, an address, or a hash-ordered value.
// - Enum names render as spaced lower case, so reason codes read as prose without the enums
//   themselves carrying formatted text.

import Ashes.Collection.List.append
import Ashes.Collection.List.filter
import Ashes.Collection.List.map
import AshesCompiler.Semantics.DecisionSnapshot
import AshesCompiler.Semantics.ExplainReport
import AshesCompiler.Semantics.HoverTypeInfo
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.ReuseDecision
export (
    value formatExplainReport,
)

let countColumn = 26

let recursive flatMap f values =
    match values with
        | [] -> []
        | head :: rest ->
            rest
            |> flatMap(f)
            |> append(f(head))

let recursive repeatText (text: Str) (count: Int) =
    if count <= 0
    then ""
    else text + repeatText(text)(count - 1)

let padRight (text: Str) (width: Int) = text + repeatText(" ")(width - Ashes.Text.length(text))

let counted (indent: Int) (label: Str) (value: Int) = repeatText(" ")(indent) + padRight(label + ":")(countColumn - indent) + Ashes.Text.fromInt(value)

let yesNo (value: Bool) =
    if value
    then "yes"
    else "no"

let isAsciiUpper (letter: Str) =
    if Ashes.Text.asciiLower(letter) == letter
    then false
    else true

let spacedChar (first: Bool) (letter: Str) =
    if first == false && isAsciiUpper(letter)
    then " " + Ashes.Text.asciiLower(letter)
    else Ashes.Text.asciiLower(letter)

let recursive spacedFrom (first: Bool) (name: Str) =
    match Ashes.Text.unconsText(name) with
        | None -> ""
        | Some((head, tail)) -> spacedChar(first)(head) + spacedFrom(false)(tail)

// Turns a PascalCase constructor name into spaced lower case.
let spaced (name: Str) = spacedFrom(true)(name)

let lowerName value =
    value
    |> Ashes.Trait.Show.show
    |> Ashes.Text.asciiLower

let spacedName value =
    value
    |> Ashes.Trait.Show.show
    |> spaced

let describeSource (origin: SourceFunctionOrigin) (fallback: Str) =
    match origin with
        | SourceFunctionOrigin { functionQualifiedName = Some(qualified) } ->
            if qualified == ""
            then fallback
            else qualified
        | _ -> fallback

// A generated function reads as its source function alone when it is that function's own lifted
// body, and as `source [label]` for every helper generated from it.
let describeIr (origin: Maybe(IrFunctionOrigin)) (fallback: Str) =
    match origin with
        | Some(IrFunctionOrigin { generatedLabel = label, originKind = kind, sourceOrigin = Some(SourceFunctionOrigin { functionSourceName = sourceName }) }) ->
            if sourceName == ""
            then fallback
            else
                if label == fallback && kind == SourceFunctionOriginKind
                then sourceName
                else sourceName + " [" + fallback + "]"
        | _ -> fallback

let describeLocation (location: IrSourceLocation) =
    match location with
        | IrSourceLocation { filePath = path, line = line, column = column } -> path + ":" + Ashes.Text.fromInt(line) + ":" + Ashes.Text.fromInt(column)

// Appends a titled section; a section after the first is separated by a blank line.
let withHeading (lines: List(Str)) (title: Str) (body: List(Str)) =
    (let separator =
        match lines with
            | [] -> []
            | _ -> [""]
    in
        [title, title
        |> Ashes.Text.length
        |> repeatText("="), ""]
        |> append(separator)
        |> append(lines)
        |> (given (headed) -> append(headed)(body)))

let parameterLines (parameter: OwnershipParameterReport) =
    match parameter with
        | OwnershipParameterReport { name = name, ownership = ownership, unique = unique, moveSafe = moveSafe } ->
            [
                "    " + name,
                "      ownership: " + lowerName(ownership),
                "      move-safe: " + yesNo(moveSafe),
                "      unique:    " + yesNo(unique)
            ]

let parametersLines (parameters: List(OwnershipParameterReport)) =
    match parameters with
        | [] -> ["    (none)"]
        | _ -> flatMap(parameterLines)(parameters)

let aliasLines (aliases: List(Str)) =
    match aliases with
        | [] -> ["    aliases:  (none)"]
        | _ ->
            "    aliases:" :: map(given (alias) -> "      - " + alias)(aliases)

let capturedLines (captured: List(Str)) =
    match captured with
        | [] -> []
        | _ ->
            "  Captured" :: map(given (name) -> "    - " + name)(captured)

let ownershipFunctionLines (report: OwnershipFunctionReport) =
    match report with
        | OwnershipFunctionReport { functionName = name, origin = origin, parameters = parameters, resultAliases = aliases, resultFresh = fresh, resultPoisoned = poisoned, capturedValues = captured } ->
            ["Function: " + describeSource(origin)(name), "  Parameters"]
            |> (given (lines) ->
                parameters
                |> parametersLines
                |> append(lines))
            |> (given (lines) -> append(lines)(["  Result", "    fresh:    " + yesNo(fresh), "    poisoned: " + yesNo(poisoned)]))
            |> (given (lines) ->
                aliases
                |> aliasLines
                |> append(lines))
            |> (given (lines) ->
                captured
                |> capturedLines
                |> append(lines))
            |> (given (lines) -> append(lines)([""]))

let externalParameterLine (parameter: ExternalResourceParameterRecord) =
    match parameter with
        | ExternalResourceParameterRecord { functionName = name, parameterIndex = index, ownership = ownership } -> "  " + name + " parameter #" + Ashes.Text.fromInt(index + 1) + ": " + Ashes.Text.asciiLower(ownership)

let externalResourceLines (resource: ExternalResourceOwnershipRecord) =
    match resource with
        | ExternalResourceOwnershipRecord { typeName = typeName, destructor = destructor, parameters = parameters } ->
            ["External resource: " + typeName, "  destructor: " + destructor]
            |> (given (lines) ->
                parameters
                |> map(externalParameterLine)
                |> append(lines))
            |> (given (lines) -> append(lines)([""]))

let ownershipSection (reports: List(OwnershipFunctionReport)) (resources: List(ExternalResourceOwnershipRecord)) =
    match (reports, resources) with
        | ([], []) -> ["  (no functions matched)"]
        | _ ->
            reports
            |> flatMap(ownershipFunctionLines)
            |> append(flatMap(externalResourceLines)(resources))

let rcOperationLines (indent: Int) (report: RcFunctionReport) =
    match report with
        | RcFunctionReport { dups = dups, drops = drops, uniquenessChecks = uniqueness } -> [counted(indent)("dup")(dups), counted(indent)("drop")(drops), counted(indent)("uniqueness checks")(uniqueness)]

let rcFunctionLines (report: RcFunctionReport) =
    match report with
        | RcFunctionReport { label = label, origin = origin, allocations = allocations, reusedAllocations = reused, reuseTokens = tokens, copies = copies } ->
            ["Function: " + describeIr(origin)(label), "  Operations"]
            |> (given (lines) ->
                report
                |> rcOperationLines(4)
                |> append(lines))
            |> (given (lines) ->
                append(lines)([
                    counted(4)("allocations")(allocations),
                    counted(4)("reused allocations")(reused),
                    counted(4)("reuse tokens")(tokens),
                    counted(4)("copies")(copies),
                    ""
                ]))

let rcSection (reports: List(RcFunctionReport)) =
    match reports with
        | [] -> ["  (no functions matched)"]
        | _ -> flatMap(rcFunctionLines)(reports)

let reuseFunctionName (report: ReuseFunctionReport) =
    match report with
        | ReuseFunctionReport { functionName = name, origin = origin } -> describeIr(Some(origin))(name)

let candidateSuffix (candidate: Maybe(Str)) =
    match candidate with
        | None -> ""
        | Some(name) -> " [" + name + "]"

let locationSuffix (location: Maybe(IrSourceLocation)) =
    match location with
        | None -> ""
        | Some(site) -> " (" + describeLocation(site) + ")"

let reuseDecisionLines (report: ReuseFunctionReport) =
    match report with
        | ReuseFunctionReport { decision = decision, outcome = outcome, reason = reason, candidate = candidate, location = location } ->
            [
                "  " + spaced(reuseDecisionKindName(decision)) + ": " + spaced(reuseDecisionOutcomeName(outcome)) + candidateSuffix(candidate),
                "    reason: " + spaced(reuseDecisionReasonName(reason)) + locationSuffix(location)
            ]

// Consecutive decisions of one function share a `Function:` line; a change of function starts a
// new block after a blank line.
let recursive reuseLines (current: Maybe(Str)) (reports: List(ReuseFunctionReport)) =
    match reports with
        | [] -> [""]
        | report :: rest ->
            let function = reuseFunctionName(report)
            in
                if current == Some(function)
                then
                    rest
                    |> reuseLines(current)
                    |> append(reuseDecisionLines(report))
                else
                    (match current with
                        | None -> []
                        | Some(_) -> [""])
                    |> (given (lines) -> append(lines)("Function: " + function :: reuseDecisionLines(report)))
                    |> (given (lines) ->
                        rest
                        |> reuseLines(Some(function))
                        |> append(lines))

let reuseSection (reports: List(ReuseFunctionReport)) =
    match reports with
        | [] -> ["  (no functions matched)"]
        | _ -> reuseLines(None)(reports)

let namesOrNone (names: List(Str)) =
    match names with
        | [] -> "(none)"
        | _ -> Ashes.Text.join(", ")(names)

let dictionaryParameterLines (parameter: TraitDictionaryAbiAnnotation) =
    match parameter with
        | TraitDictionaryAbiAnnotation { functionName = name, functionSource = source, functionOffset = offset, parameterIndex = index, traitName = traitLabel, methods = methods, supertraits = supertraits } ->
            [
                "Function: " + name + " (" + source + ":" + Ashes.Text.fromInt(offset) + ")",
                "  dictionary parameter " + Ashes.Text.fromInt(index) + ": " + traitLabel,
                "    methods: " + Ashes.Text.join(", ")(methods),
                "    supertraits: " + namesOrNone(supertraits)
            ]

let resolutionLines (resolution: TraitResolutionAnnotation) =
    match resolution with
        | TraitResolutionAnnotation { requirement = requirement, implementationModule = module_, implementationSource = source, implementationOffset = offset } ->
            [
                "Resolved: " + requirement,
                "  implementation: " + module_ + " (" + source + ":" + Ashes.Text.fromInt(offset) + ")"
            ]

let hasTraitEvidence (evidence: TraitEvidenceAnnotations) =
    match evidence with
        | TraitEvidenceAnnotations { dictionaryParameters = [], resolvedImplementations = [] } -> false
        | _ -> true

let traitsSection (evidence: TraitEvidenceAnnotations) =
    match evidence with
        | TraitEvidenceAnnotations { dictionaryParameters = [], resolvedImplementations = [] } -> ["  (no trait evidence)"]
        | TraitEvidenceAnnotations { dictionaryParameters = parameters, resolvedImplementations = resolutions } ->
            parameters
            |> flatMap(dictionaryParameterLines)
            |> (given (lines) ->
                resolutions
                |> flatMap(resolutionLines)
                |> append(lines))
            |> (given (lines) -> append(lines)([""]))

let formatCapabilities (capabilities: List(Str)) =
    match capabilities with
        | [] -> "{}"
        | _ -> "{" + Ashes.Text.join(", ")(capabilities) + "}"

let publicAuthorityLines (record: PublicAuthorityRecord) =
    match record with
        | PublicAuthorityRecord { bindingName = name, capabilities = capabilities } -> ["Public binding: " + name, "  needs: " + formatCapabilities(capabilities)]

let externalAuthorityLines (record: ExternalAuthorityRecord) =
    match record with
        | ExternalAuthorityRecord { functionName = name, runtimeCapabilities = capabilities } -> ["External function: " + name, "  needs: " + formatCapabilities(capabilities)]

let authoritySection (bindings: List(PublicAuthorityRecord)) (externals: List(ExternalAuthorityRecord)) =
    match (bindings, externals) with
        | ([], []) -> ["  (no functions matched)"]
        | _ ->
            bindings
            |> flatMap(publicAuthorityLines)
            |> (given (lines) ->
                externals
                |> flatMap(externalAuthorityLines)
                |> append(lines))
            |> (given (lines) -> append(lines)([""]))

let concurrencyFunctionLines (report: ConcurrencyFunctionReport) =
    match report with
        | ConcurrencyFunctionReport { label = label, origin = origin, scopes = scopes, forks = forks, joins = joins, detachedSpawns = spawns } ->
            [
                "Function: " + describeIr(origin)(label),
                counted(4)("structured scopes")(scopes),
                counted(4)("forks")(forks),
                counted(4)("joins")(joins),
                counted(4)("detached spawns")(spawns)
            ]

let concurrencySection (reports: List(ConcurrencyFunctionReport)) =
    match reports with
        | [] -> ["  (no task concurrency matched)"]
        | _ ->
            append(flatMap(concurrencyFunctionLines)(reports))([""])

// Whether a generated function was produced for the given source function. The lowered origins
// carry no qualified name, so the correlation compares the source name, location, and offset.
let belongsTo (source: SourceFunctionOrigin) (generated: Maybe(IrFunctionOrigin)) =
    match (generated, source) with
        | (Some(IrFunctionOrigin { sourceOrigin = Some(SourceFunctionOrigin { functionSourceName = generatedName, declarationLocation = generatedLocation, declarationOffset = generatedOffset }) }), SourceFunctionOrigin { functionSourceName = sourceName, declarationLocation = sourceLocation, declarationOffset = sourceOffset }) -> generatedName == sourceName && generatedLocation == sourceLocation && generatedOffset == sourceOffset
        | _ -> false

let rcBelongsTo (source: SourceFunctionOrigin) (report: RcFunctionReport) =
    match report with
        | RcFunctionReport { origin = origin } -> belongsTo(source)(origin)

let reuseBelongsTo (source: SourceFunctionOrigin) (report: ReuseFunctionReport) =
    match report with
        | ReuseFunctionReport { origin = origin } -> belongsTo(source)(Some(origin))

let representationBelongsTo (source: SourceFunctionOrigin) (report: RepresentationFunctionReport) =
    match report with
        | RepresentationFunctionReport { origin = origin } -> belongsTo(source)(origin)

let memoryParameterLine (parameter: OwnershipParameterReport) =
    match parameter with
        | OwnershipParameterReport { name = name, ownership = ownership, moveSafe = moveSafe } -> "    " + name + ": " + lowerName(ownership) + ", move-safe " + yesNo(moveSafe)

let memoryRcLines (report: RcFunctionReport) =
    match report with
        | RcFunctionReport { label = label } -> "  perceus [" + label + "]" :: rcOperationLines(4)(report)

let memoryReuseLine (report: ReuseFunctionReport) =
    match report with
        | ReuseFunctionReport { decision = decision, outcome = outcome } ->
            "  reuse: " + spaced(reuseDecisionKindName(decision)) + " " + spaced(reuseDecisionOutcomeName(outcome))

let placementCategories = [ConservativeUnknown, Region, RuntimeRc, BorrowedView, TaskFrameOwned, WorkerTransfer, CopyValue]

let recursive placementCount (category: ValuePlacementCategory) (placements: List((ValuePlacementCategory, Int))) =
    match placements with
        | [] -> 0
        | (existing, count) :: rest ->
            if existing == category
            then count
            else placementCount(category)(rest)

let placementLine (placements: List((ValuePlacementCategory, Int))) (category: ValuePlacementCategory) =
    match placementCount(category)(placements) with
        | 0 -> []
        | count ->
            [counted(4)(spacedName(category))(count)]

// Categories render in their declaration order, omitting the ones with no value.
let memoryRepresentationLines (report: RepresentationFunctionReport) =
    match report with
        | RepresentationFunctionReport { label = label, placements = placements } ->
            "  representation [" + label + "]" :: flatMap(placementLine(placements))(placementCategories)

// Correlated by source function: ownership is a source-level fact while reference counting and
// representation are per generated function, so the generated ones are gathered under the source
// function that produced them.
let memoryFunctionLines (report: CompilationExplainReport) (ownership: OwnershipFunctionReport) =
    match (report, ownership) with
        | (CompilationExplainReport { rc = rc, reuse = reuse, representation = representation }, OwnershipFunctionReport { functionName = name, origin = origin, parameters = parameters, resultFresh = fresh }) ->
            ["Function: " + describeSource(origin)(name), "  ownership"]
            |> (given (lines) ->
                parameters
                |> map(memoryParameterLine)
                |> append(lines))
            |> (given (lines) -> append(lines)(["    result fresh: " + yesNo(fresh)]))
            |> (given (lines) ->
                rc
                |> filter(rcBelongsTo(origin))
                |> flatMap(memoryRcLines)
                |> append(lines))
            |> (given (lines) ->
                reuse
                |> filter(reuseBelongsTo(origin))
                |> map(memoryReuseLine)
                |> append(lines))
            |> (given (lines) ->
                representation
                |> filter(representationBelongsTo(origin))
                |> flatMap(memoryRepresentationLines)
                |> append(lines))
            |> (given (lines) -> append(lines)([""]))

let memorySection (lines: List(Str)) (report: CompilationExplainReport) =
    match report with
        | CompilationExplainReport { ownership = [], rc = [], representation = [] } -> withHeading(lines)("Memory report")(["  (no functions matched)"])
        | CompilationExplainReport { ownership = ownership, traitEvidence = evidence } ->
            ownership
            |> flatMap(memoryFunctionLines(report))
            |> withHeading(lines)("Memory report")
            |> (given (headed) ->
                if hasTraitEvidence(evidence)
                then
                    evidence
                    |> traitsSection
                    |> withHeading(headed)("Trait evidence report")
                else headed)

let appendSection (report: CompilationExplainReport) (lines: List(Str)) (kind: ExplainKind) =
    match report with
        | CompilationExplainReport { ownership = ownership, rc = rc, reuse = reuse, traitEvidence = evidence, externalResources = resources, authority = authority, externalAuthority = externalAuthority, concurrency = concurrency } ->
            match kind with
                | ExplainOwnership ->
                    resources
                    |> ownershipSection(ownership)
                    |> withHeading(lines)("Ownership report")
                | ExplainRc ->
                    rc
                    |> rcSection
                    |> withHeading(lines)("RC report")
                | ExplainReuse ->
                    reuse
                    |> reuseSection
                    |> withHeading(lines)("Reuse report")
                | ExplainTraits ->
                    evidence
                    |> traitsSection
                    |> withHeading(lines)("Trait evidence report")
                | ExplainAuthority ->
                    externalAuthority
                    |> authoritySection(authority)
                    |> withHeading(lines)("Authority report")
                | ExplainConcurrency ->
                    concurrency
                    |> concurrencySection
                    |> withHeading(lines)("Concurrency report")
                | ExplainMemory -> memorySection(lines)(report)

let recursive appendSections (report: CompilationExplainReport) (lines: List(Str)) (kinds: List(ExplainKind)) =
    match kinds with
        | [] -> lines
        | kind :: rest ->
            kind
            |> appendSection(report)(lines)
            |> (given (appended) -> appendSections(report)(appended)(rest))

let reportOrder = [ExplainOwnership, ExplainRc, ExplainReuse, ExplainTraits, ExplainAuthority, ExplainConcurrency, ExplainMemory]

// The requested sections in report order, regardless of the order they were asked for in.
let formatExplainReport (report: CompilationExplainReport) (request: ExplainRequest) =
    reportOrder
    |> filter(given (kind) -> explainRequestIncludes(kind)(request))
    |> appendSections(report)([])
