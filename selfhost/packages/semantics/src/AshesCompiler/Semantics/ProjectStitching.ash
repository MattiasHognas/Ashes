// Stitches a whole project (its own modules, its dependencies' modules, and the shipped
// standard-library modules they reach) into one syntax program, the multi-file counterpart of
// `stitchWithShippedModules`.
//
// Invariants:
// - The compilation plan is the single source of module order, resolved imports, interfaces, and
//   parsed programs; nothing is read or parsed here.
// - Package identity follows stage 0's `WithPackageIdentity`: a shipped module belongs to
//   `ashes-core`, a module under a resolved dependency's source root to that dependency's name, and
//   every other module to the project itself (its manifest name, or the manifest path when the
//   manifest names nothing), so trait orphan and coherence rules see the same package boundaries.
// - Exactly the layout's entry module is the entry; every other module contributes declarations
//   only.

import Ashes.IO.Path
import AshesCompiler.Frontend.ModulePlan
import AshesCompiler.Frontend.ModuleSource
import AshesCompiler.Semantics.ModuleSemanticStitching
import AshesCompiler.Semantics.ProjectCompilationPlanning
import AshesCompiler.Semantics.ProjectDependencyGraph
import AshesCompiler.Semantics.ProjectDiscovery
import AshesCompiler.Semantics.ProjectManifest
import AshesCompiler.Semantics.ProjectSyntaxStitching
import AshesCompiler.Semantics.ShippedModuleStitching
export (
    type ProjectStitchError(..),
    value shippedPackageIdentity,
    value projectPackageIdentity,
    value stitchProject,
)

type ProjectStitchError =
    | ProjectStitchPlanError(ProjectCompilationError)
    | ProjectStitchSyntaxError(ProjectSyntaxStitchError)
    | ProjectStitchMissingProgram(Str)
    deriving {Eq, Show}

let shippedPackageIdentity = "ashes-core"

// The project's own package identity: its manifest name, or the manifest path when unnamed.
let projectPackageIdentity (layout: ProjectLayout) =
    match layout with
        | ProjectLayout { projectFilePath = projectFilePath, manifest = ProjectManifest { name = name } } ->
            match name with
                | Some(text) ->
                    if Ashes.Text.trim(text) == ""
                    then projectFilePath
                    else text
                | None -> projectFilePath

let trimTrailingSeparator (style: Style) (path: Str) =
    if Ashes.Text.length(path) > 1 && Ashes.Text.substring(path)(Ashes.Text.length(path) - 1)(1) == separator(style)
    then Ashes.Text.take(path)(Ashes.Text.length(path) - 1)
    else path

let isPathWithin (style: Style) (path: Str) (directory: Str) =
    (let normalizedPath = normalize(style)(path)
    in
        let normalizedDirectory =
            directory
            |> normalize(style)
            |> trimTrailingSeparator(style)
        in
            if normalizedPath == normalizedDirectory
            then true
            else Ashes.Text.startsWith(normalizedPath)(normalizedDirectory + separator(style)))

let recursive isPathWithinAny (style: Style) (path: Str) (directories: List(Str)) =
    match directories with
        | [] -> false
        | directory :: rest ->
            if isPathWithin(style)(path)(directory)
            then true
            else isPathWithinAny(style)(path)(rest)

let recursive dependencyPackageIdentity (style: Style) (path: Str) (dependencies: List(ResolvedProjectDependency)) =
    match dependencies with
        | [] -> None
        | ResolvedProjectDependency { name = name, sourceRoots = sourceRoots } :: rest ->
            if isPathWithinAny(style)(path)(sourceRoots)
            then Some(name)
            else dependencyPackageIdentity(style)(path)(rest)

// An inline module's planned path is `<file>#<module>`; its package is its file's.
let inlineModuleFilePath (path: Str) =
    (let index = Ashes.Text.indexOf(path)("#")
    in
        if index < 0
        then path
        else Ashes.Text.take(path)(index))

let packageIdentityForPath (style: Style) (layout: ProjectLayout) dependencies (path: Str) =
    match dependencyPackageIdentity(style)(path)(dependencies) with
        | Some(name) -> name
        | None -> projectPackageIdentity(layout)

let modulePackageIdentity (style: Style) (layout: ProjectLayout) dependencies (source: ResolvedModuleSource) =
    match source with
        | ShippedModuleSource(_path) -> shippedPackageIdentity
        | ProjectModuleSource(path) -> packageIdentityForPath(style)(layout)(dependencies)(path)
        | InlineModuleSource(path, _text) ->
            path
            |> inlineModuleFilePath
            |> packageIdentityForPath(style)(layout)(dependencies)

let plannedSourcePath (source: ResolvedModuleSource) =
    match source with
        | ProjectModuleSource(path) -> path
        | ShippedModuleSource(path) -> path
        | InlineModuleSource(path, _text) -> path

let recursive stitchUnits (style: Style) (layout: ProjectLayout) dependencies programs (modules: List(PlannedModule)) =
    match modules with
        | [] -> Ok([])
        | PlannedModule { name = name, source = source, imports = imports, interface = interface } :: rest ->
            match plannedProgram(name)(programs) with
                | None -> Error(ProjectStitchMissingProgram(name))
                | Some(program) ->
                    match stitchUnits(style)(layout)(dependencies)(programs)(rest) with
                        | Error(error) -> Error(error)
                        | Ok(units) ->
                            Ok(SemanticStitchUnit(
                                name = name,
                                packageId = modulePackageIdentity(style)(layout)(dependencies)(source),
                                sourcePath = plannedSourcePath(source),
                                imports = imports,
                                interface = interface,
                                program = program,
                                isEntry = name == layout.entryModuleName
                            ) :: units)

let stitchPlannedProject (style: Style) (layout: ProjectLayout) (plan: ProjectCompilationPlan) =
    match plan with
        | ProjectCompilationPlan { modules = modules, programs = programs, dependencies = dependencies } ->
            match stitchUnits(style)(layout)(dependencies)(programs)(modules) with
                | Error(error) -> Error(error)
                | Ok(units) ->
                    match stitchProjectSyntax(units) with
                        | Error(error) -> Error(ProjectStitchSyntaxError(error))
                        | Ok(project) -> Ok(project)

// The project at `layout` plus every shipped module it reaches (resolved against `shipped`), as
// one stitched project whose `program` lowers like any single program.
let stitchProject (style: Style) (layout: ProjectLayout) (shipped: List(ShippedModuleText)) =
    match buildProjectCompilationPlanWithShipped(style)(layout)(shipped) with
        | Error(error) -> Error(ProjectStitchPlanError(error))
        | Ok(plan) -> stitchPlannedProject(style)(layout)(plan)
