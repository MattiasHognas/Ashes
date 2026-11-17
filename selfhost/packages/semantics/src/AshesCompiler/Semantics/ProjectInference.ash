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

// An implementation site's file, from the region of the unit it was declared in.
let siteWithPath regions (site: Maybe(TraitImplementationSite)) =
    match site with
        | Some(TraitImplementationSite { unitIndex = unitIndex } as known) ->
            match regionAt(unitIndex)(regions) with
                | Some(StitchedModuleRegion { sourcePath = sourcePath }) -> Some((known with sourcePath = Some(sourcePath)))
                | None -> site
        | None -> None

// The files of the implementations an overlap error names.
let withImplementationPaths regions (error: ProgramInferenceError) =
    match error with
        | OverlappingTraitImplementations(traitName, first, conflicting) ->
            conflicting
            |> siteWithPath(regions)
            |> OverlappingTraitImplementations(traitName)(siteWithPath(regions)(first))
        | other -> other

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

let withErrorImplementationPaths regions (result: ProgramInferenceResult) =
    match result.error with
        | Some(error) ->
            result with error = Some(withImplementationPaths(regions)(error))
        | None -> result

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

// Each module region with its unit, in region order.
let recursive regionUnits (regions: List(StitchedModuleRegion)) (units: List(ProgramInferenceUnit)) =
    match (regions, units) with
        | (region :: restRegions, unit :: restUnits) -> (region, unit) :: regionUnits(restRegions)(restUnits)
        | _ -> []

let isStandardTraitRegion (region: StitchedModuleRegion) = region.moduleName == "Ashes.Trait"

let recursive pairCount (pairs: List((StitchedModuleRegion, ProgramInferenceUnit))) =
    match pairs with
        | [] -> 0
        | _head :: rest -> 1 + pairCount(rest)

let recursive unitCount (units: List(ProgramInferenceUnit)) =
    match units with
        | [] -> 0
        | _head :: rest -> 1 + unitCount(rest)

let recursive regionUnitsNamed (wanted: Bool) (pairs: List((StitchedModuleRegion, ProgramInferenceUnit))) =
    match pairs with
        | [] -> []
        | (region, unit) :: rest ->
            if isStandardTraitRegion(region) == wanted
            then (region, unit) :: regionUnitsNamed(wanted)(rest)
            else regionUnitsNamed(wanted)(rest)

let recursive appendRegionUnits (left: List((StitchedModuleRegion, ProgramInferenceUnit))) (right: List((StitchedModuleRegion, ProgramInferenceUnit))) =
    match left with
        | [] -> right
        | head :: tail -> head :: appendRegionUnits(tail)(right)

let recursive unitsOf (pairs: List((StitchedModuleRegion, ProgramInferenceUnit))) =
    match pairs with
        | [] -> []
        | (_region, unit) :: rest -> unit :: unitsOf(rest)

let recursive regionsOf (pairs: List((StitchedModuleRegion, ProgramInferenceUnit))) =
    match pairs with
        | [] -> []
        | (region, _unit) :: rest -> region :: regionsOf(rest)

// `Ashes.Trait` holds the standard implementations, and every implementation in a program is
// visible to resolution wherever it is declared, so its unit is inferred first: a module the plan
// orders ahead of it (an inline module's dependency) still sees the implementations when its
// bindings' requirements are settled. The regions are reordered with the units, so an error still
// names its own module. Node identities are not affected.
let standardTraitsFirst (regions: List(StitchedModuleRegion)) (units: List(ProgramInferenceUnit)) =
    (let pairs = regionUnits(regions)(units)
    in
        if pairCount(pairs) == unitCount(units)
        then
            let ordered =
                pairs
                |> regionUnitsNamed(false)
                |> appendRegionUnits(regionUnitsNamed(true)(pairs))
            in (regionsOf(ordered), unitsOf(ordered))
        else (regions, units))

let inferIdentifiedStitchedProjectFrom baseEnvironment project (identified: IdentifiedStitchedProject) =
    match project with
        | StitchedSyntaxProject { moduleRegions = regions } ->
            let program = identified.program
            in
                match standardTraitsFirst(regions)(program.units) with
                    | (orderedRegions, orderedUnits) ->
                        (program with units = orderedUnits)
                        |> inferIdentifiedProgramUnitsFrom(baseEnvironment)(identified.entryPackageId)
                        |> withErrorModule(orderedRegions)
                        |> withErrorImplementationPaths(orderedRegions)

let inferStitchedProjectFrom baseEnvironment project =
    project
    |> identifyStitchedProject
    |> inferIdentifiedStitchedProjectFrom(baseEnvironment)(project)

let inferStitchedProject project =
    inferStitchedProjectFrom(emptyTypeEnvironmentForPackage("standalone"))(project)
