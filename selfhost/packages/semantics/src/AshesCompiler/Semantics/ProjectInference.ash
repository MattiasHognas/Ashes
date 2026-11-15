// Infers a stitched project without erasing the package ownership of its modules.
//
// Invariants:
// - Module regions cover the combined syntax exactly once in dependency order.
// - Deriving expansion happens independently inside each module before inference, so generated
//   implementations inherit that module's package for orphan and coherence validation.
// - Only the entry module contributes a trailing expression, inferred after every module declaration.

import Ashes.Collection.List.reverse
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.ProgramInference
import AshesCompiler.Semantics.ProjectSyntaxStitching
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.Types
export (
    type IdentifiedStitchedProject(..),
    value identifyStitchedProject,
    value inferIdentifiedStitchedProjectFrom,
    value inferStitchedProjectFrom,
    value inferStitchedProject,
)

type ProjectItemSplit =
    | selected: List(TopLevelItem)
    | remaining: List(TopLevelItem)
    | complete: Bool

type ProjectInferenceUnits =
    | units: List(ProgramInferenceUnit)
    | body: Maybe(Expr)
    | entryPackageId: Maybe(Str)
    | error: Maybe(ProgramInferenceError)

let recursive takeProjectItems count items reversedItems =
    if count <= 0
    then ProjectItemSplit(selected = reverse(reversedItems), remaining = items, complete = true)
    else
        match items with
            | [] -> ProjectItemSplit(selected = reverse(reversedItems), remaining = [], complete = false)
            | head :: tail -> takeProjectItems(count - 1)(tail)(head :: reversedItems)

let invalidProject body entryPackageId message =
    ProjectInferenceUnits(units = [], body = body, entryPackageId = entryPackageId, error = Some(
        InvalidStitchedProgram(message)
    ))

let selectEntryPackage packageId isEntry current body =
    if isEntry
    then
        match current with
            | None -> ProjectInferenceUnits(units = [], body = body, entryPackageId = Some(packageId), error = None)
            | Some(_existing) -> invalidProject(body)(current)("multiple entry module regions")
    else ProjectInferenceUnits(units = [], body = body, entryPackageId = current, error = None)

let recursive assembleInferenceUnits regions remainingItems expectedStart reversedUnits entryPackageId body =
    match regions with
        | [] ->
            match (remainingItems, entryPackageId) with
                | ([], Some(packageId)) ->
                    ProjectInferenceUnits(units = reverse(
                        reversedUnits
                    ), body = body, entryPackageId = Some(packageId), error = None)
                | ([], None) -> invalidProject(body)(None)("missing entry module region")
                | (_remaining, _) ->
                    invalidProject(
                        body,
                        entryPackageId,
                        "module regions do not cover every stitched item"
                    )
        | StitchedModuleRegion { packageId = packageId, itemStart = itemStart, itemEnd = itemEnd, isEntry = isEntry } :: tail ->
            if itemStart != expectedStart
            then invalidProject(body)(entryPackageId)("module regions are not contiguous")
            else
                if itemEnd < itemStart
                then invalidProject(body)(entryPackageId)("module region has a negative length")
                else
                    match takeProjectItems(itemEnd - itemStart)(remainingItems)([]) with
                        | ProjectItemSplit { selected = _selected, remaining = _remaining, complete = false } ->
                            invalidProject(
                                body,
                                entryPackageId,
                                "module region extends beyond the stitched program"
                            )
                        | ProjectItemSplit { selected = selected, remaining = remaining, complete = true } ->
                            match selectEntryPackage(packageId)(isEntry)(entryPackageId)(body) with
                                | ProjectInferenceUnits { error = Some(error) } ->
                                    ProjectInferenceUnits(units = [], body = body, entryPackageId = entryPackageId, error = Some(
                                        error
                                    ))
                                | ProjectInferenceUnits { entryPackageId = nextEntryPackageId, error = None } ->
                                    assembleInferenceUnits(
                                        tail,
                                        remaining,
                                        itemEnd,
                                        ProgramInferenceUnit(packageId = packageId, program = ProgramSyntax(items = selected, body = None)) :: reversedUnits,
                                        nextEntryPackageId,
                                        body
                                    )

let recursive entryRegion regions =
    match regions with
        | [] -> None
        | region :: tail ->
            match region with
                | StitchedModuleRegion { isEntry = true } -> Some(region)
                | _ -> entryRegion(tail)

let recursive regionAt index regions =
    match regions with
        | [] -> None
        | region :: tail ->
            if index == 0
            then Some(region)
            else regionAt(index - 1)(tail)

let errorRegion regions (location: InferenceErrorLocation) =
    if location.unitIndex < 0
    then entryRegion(regions)
    else regionAt(location.unitIndex)(regions)

// Names the module an error was raised in: units are inferred in region order, so the failing
// unit's index selects its region, and the trailing expression belongs to the entry module.
let withErrorModule regions (result: ProgramInferenceResult) =
    match result with
        | ProgramInferenceResult { errorLocation = Some(location) } ->
            match errorRegion(regions)(location) with
                | Some(StitchedModuleRegion { moduleName = moduleName, sourcePath = sourcePath }) ->
                    result with errorLocation = Some(
                        location with moduleName = Some(moduleName), sourcePath = Some(sourcePath)
                    )
                | None -> result
        | _ -> result

// A stitched project split into its modules, with `deriving` expanded and every expression node
// identified. It is what inference records against, and what a reader of the recorded types walks.
type IdentifiedStitchedProject =
    | program: IdentifiedProgramUnits
    | entryPackageId: Str

let unidentifiedProject body error = IdentifiedStitchedProject(program = IdentifiedProgramUnits(units = [], body = body, nodeCount = 0, error = Some(error)), entryPackageId = "")

let identifyStitchedProject project =
    match project with
        | StitchedSyntaxProject { program = ProgramSyntax { items = items, body = body }, moduleRegions = regions } ->
            match assembleInferenceUnits(regions)(items)(0)([])(None)(body) with
                | ProjectInferenceUnits { units = _units, body = _body, entryPackageId = _entryPackageId, error = Some(error) } -> unidentifiedProject(body)(error)
                | ProjectInferenceUnits { units = units, body = unitBody, entryPackageId = Some(entryPackageId), error = None } -> IdentifiedStitchedProject(program = identifyProgramUnits(units)(unitBody), entryPackageId = entryPackageId)
                | ProjectInferenceUnits { units = _units, body = _body, entryPackageId = None, error = None } -> unidentifiedProject(body)(InvalidStitchedProgram("missing entry package"))

let inferIdentifiedStitchedProjectFrom baseEnvironment project (identified: IdentifiedStitchedProject) =
    match project with
        | StitchedSyntaxProject { moduleRegions = regions } ->
            identified.program
            |> inferIdentifiedProgramUnitsFrom(baseEnvironment)(identified.entryPackageId)
            |> withErrorModule(regions)

let inferStitchedProjectFrom baseEnvironment project =
    project
    |> identifyStitchedProject
    |> inferIdentifiedStitchedProjectFrom(baseEnvironment)(project)

let inferStitchedProject project =
    inferStitchedProjectFrom(emptyTypeEnvironmentForPackage("standalone"))(project)
