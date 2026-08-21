// Turns a resolved project graph into a dependency-ordered module compilation plan.
//
// Invariants:
// - Project, include, dependency, and shipped sources retain their namespace boundaries.
// - Imports are parsed and resolved against built interfaces before modules are ordered.
// - Ambiguous, missing, or cyclic modules fail planning rather than selecting an arbitrary source.

import Ashes.Collection.List.append as appendList
import Ashes.Collection.List.reverse as reverseList
import Ashes.IO.Path
import Ashes.Internal.deepCopy as deepCopy
import AshesCompiler.Frontend.ImportHeader
import AshesCompiler.Frontend.ImportHeader.ImportHeaderError
import AshesCompiler.Frontend.ModuleInterface
import AshesCompiler.Frontend.ModuleInterface.ModuleInterfaceBuildError
import AshesCompiler.Frontend.ModulePlan
import AshesCompiler.Frontend.ModulePlan.ModulePlanError
import AshesCompiler.Frontend.ModulePlan.ModulePlanUnit
import AshesCompiler.Frontend.ModulePlan.PlannedModule
import AshesCompiler.Frontend.ModuleSource
import AshesCompiler.Frontend.Parser
import AshesCompiler.Semantics.ProjectDependencyGraph
import AshesCompiler.Semantics.ProjectDiscovery
import AshesCompiler.Semantics.ProjectSourceEnumeration
import AshesCompiler.Semantics.ProjectSourceEnumeration.ProjectSourceEnumerationError
export (
    type ProjectCompilationPlan(..),
    type ProjectCompilationError(..),
    value buildProjectCompilationPlan,
)

type ProjectCompilationPlan =
    | sourceFiles: List(Str)
    | modules: List(PlannedModule)
    deriving {Eq, Show}

type ProjectCompilationError =
    | ProjectCompilationDependencyGraphError(ProjectDependencyGraphError)
    | ProjectCompilationSourceEnumerationError(ProjectSourceEnumerationError)
    | ProjectCompilationUnmappedSource(Str)
    | ProjectCompilationMissingModule(Str, List(Str))
    | ProjectCompilationAmbiguousModule(Str, List(Str))
    | ProjectCompilationReadError(Str, Str)
    | ProjectCompilationImportHeaderError(Str, ImportHeaderError)
    | ProjectCompilationParseError(Str)
    | ProjectCompilationInterfaceError(Str, ModuleInterfaceBuildError)
    | ProjectCompilationModulePlanError(ModulePlanError)
    deriving {Eq, Show}

type IndexedProjectSource =
    | name: Str
    | path: Str

type LoadedProjectModule =
    | unit: ModulePlanUnit
    | dependencies: List(Str)

let projectEntryPath (layout: ProjectLayout) =
    match layout with
        | ProjectLayout { projectFilePath = _projectFilePath, projectDirectory = _projectDirectory, entryPath = entryPath, entryModuleName = _entryModuleName, sourceRoots = _sourceRoots, includeRoots = _includeRoots, outDir = _outDir, manifest = _manifest } -> entryPath

let projectEntryModuleName (layout: ProjectLayout) =
    match layout with
        | ProjectLayout { projectFilePath = _projectFilePath, projectDirectory = _projectDirectory, entryPath = _entryPath, entryModuleName = entryModuleName, sourceRoots = _sourceRoots, includeRoots = _includeRoots, outDir = _outDir, manifest = _manifest } -> entryModuleName

let recursive containsText (value: Str) (values: List(Str)) =
    match values with
        | [] -> false
        | candidate :: rest ->
            if candidate == value
            then true
            else containsText(value)(rest)

let recursive pathsForModule (name: Str) (sources: List(IndexedProjectSource)) =
    match sources with
        | [] -> []
        | IndexedProjectSource { name = candidate, path = path } :: rest ->
            if candidate == name
            then deepCopy(path) :: pathsForModule(name)(rest)
            else pathsForModule(name)(rest)

let recursive indexedModuleNames (sources: List(IndexedProjectSource)) =
    match sources with
        | [] -> []
        | IndexedProjectSource { name = name, path = _path } :: rest -> name :: indexedModuleNames(rest)

let isOutsideRoot (style: Style) (relative: Str) =
    if relative == ".."
    then true
    else Ashes.Text.startsWith(relative)(".." + separator(style))

let recursive relativeSourcePath (style: Style) (roots: List(Str)) (path: Str) =
    match roots with
        | [] -> None
        | root :: rest ->
            match path
            |> deepCopy
            |> relativeTo(style)(root) with
                | relative ->
                    if isOutsideRoot(style)(relative)
                    then relativeSourcePath(style)(rest)(path)
                    else Some(relative)

let moduleNameFromRelative (style: Style) (relative: Str) =
    match Ashes.Text.take(deepCopy(relative))(Ashes.Text.length(relative) - 4) with
        | withoutExtension ->
            style
            |> separator
            |> Ashes.Text.split(withoutExtension)
            |> Ashes.Text.join(".")

let indexedSource (style: Style) (roots: List(Str)) (entryPath: Str) (entryModuleName: Str) (path: Str) =
    if path == entryPath
    then Ok(IndexedProjectSource(name = deepCopy(entryModuleName), path = path))
    else
        match path
        |> deepCopy
        |> relativeSourcePath(style)(roots) with
            | None -> Error(ProjectCompilationUnmappedSource(path))
            | Some(relative) -> Ok(IndexedProjectSource(name = moduleNameFromRelative(style)(relative), path = path))

let recursive indexSources (style: Style) (roots: List(Str)) (entryPath: Str) (entryModuleName: Str) (paths: List(Str)) =
    match paths with
        | [] -> Ok([])
        | path :: rest ->
            match indexedSource(style)(roots)(entryPath)(entryModuleName)(path) with
                | Error(error) -> Error(error)
                | Ok(source) ->
                    match indexSources(style)(roots)(entryPath)(entryModuleName)(rest) with
                        | Error(error) -> Error(error)
                        | Ok(sources) -> Ok(source :: sources)

let ensureEntrySource (layout: ProjectLayout) (paths: List(Str)) =
    if containsText(projectEntryPath(layout))(paths)
    then paths
    else projectEntryPath(layout) :: paths

let recursive dropLast (parts: List(Str)) =
    match parts with
        | [] -> []
        | _last :: [] -> []
        | head :: rest -> head :: dropLast(rest)

let parentModuleName (name: Str) =
    match "."
    |> Ashes.Text.split(name)
    |> dropLast with
        | parts ->
            match Ashes.Text.join(".")(parts) with
                | "" -> None
                | parent -> Some(parent)

let hasIndexedModule (name: Str) (sources: List(IndexedProjectSource)) =
    match pathsForModule(name)(sources) with
        | [] -> false
        | _ -> true

let dependencyModuleName (sources: List(IndexedProjectSource)) (entry: ImportHeaderEntry) =
    match entry.selector with
        | Some(_selector) -> entry.modulePath
        | None ->
            if hasIndexedModule(entry.modulePath)(sources)
            then entry.modulePath
            else
                match parentModuleName(entry.modulePath) with
                    | Some(parent) ->
                        if hasIndexedModule(parent)(sources)
                        then parent
                        else entry.modulePath
                    | None -> entry.modulePath

let recursive dependencyModuleNames (sources: List(IndexedProjectSource)) (imports: List(ImportHeaderEntry)) =
    match imports with
        | [] -> []
        | entry :: rest -> dependencyModuleName(sources)(entry) :: dependencyModuleNames(sources)(rest)

let finishLoadedModule (name: Str) (path: Str) (sources: List(IndexedProjectSource)) (imports: List(ImportHeaderEntry)) (parsed: ProgramParseResult) =
    match parsed with
        | ProgramParseResult { program = program, diagnostics = diagnostics } ->
            match diagnostics with
                | _diagnostic :: _ -> Error(ProjectCompilationParseError(path))
                | [] ->
                    match buildModuleInterface(name)([])(program) with
                        | Error(error) ->
                            error
                            |> ProjectCompilationInterfaceError(path)
                            |> Error
                        | Ok(moduleInterface) ->
                            match imports
                            |> deepCopy
                            |> dependencyModuleNames(sources) with
                                | dependencies ->
                                    Ok(
                                        LoadedProjectModule(unit = ModulePlanUnit(name = name, source = ProjectModuleSource(
                                            path
                                        ), imports = imports, interface = moduleInterface), dependencies = dependencies)
                                    )

let parseLoadedModule (name: Str) (path: Str) (sources: List(IndexedProjectSource)) (source: Str) =
    match parseImportHeader(source) with
        | Error(error) ->
            error
            |> ProjectCompilationImportHeaderError(path)
            |> Error
        | Ok(header) ->
            match header with
                | ParsedImportHeader { imports = imports, sourceWithoutImports = sourceWithoutImports } ->
                    match deepCopy(imports) with
                        | retainedImports ->
                            match parseProgram(sourceWithoutImports) with
                                | parsed -> finishLoadedModule(name)(path)(sources)(retainedImports)(parsed)

let readIndexedModule (name: Str) (path: Str) (sources: List(IndexedProjectSource)) =
    match Ashes.IO.File.readText(path) with
        | Error(error) ->
            error
            |> ProjectCompilationReadError(path)
            |> Error
        | Ok(source) -> parseLoadedModule(name)(path)(sources)(source)

let loadNamedModule (name: Str) (sources: List(IndexedProjectSource)) =
    match pathsForModule(name)(sources) with
        | [] ->
            sources
            |> indexedModuleNames
            |> ProjectCompilationMissingModule(name)
            |> Error
        | path :: [] -> readIndexedModule(name)(path)(sources)
        | paths ->
            paths
            |> ProjectCompilationAmbiguousModule(name)
            |> Error

let recursive loadReachableModules (pending: List(Str)) (loaded: List(Str)) (reversedUnits: List(ModulePlanUnit)) (sources: List(IndexedProjectSource)) =
    match pending with
        | [] ->
            reversedUnits
            |> reverseList
            |> Ok
        | name :: rest ->
            if containsText(name)(loaded)
            then loadReachableModules(rest)(loaded)(reversedUnits)(sources)
            else
                match loadNamedModule(name)(sources) with
                    | Error(error) -> Error(error)
                    | Ok(loadedModule) ->
                        loadReachableModules(
                            appendList(loadedModule.dependencies)(rest),
                            name :: loaded,
                            loadedModule.unit :: reversedUnits,
                            sources
                        )

let planIndexedSources (layout: ProjectLayout) (paths: List(Str)) (sources: List(IndexedProjectSource)) =
    match loadReachableModules([projectEntryModuleName(layout)])([])([])(sources) with
        | Error(error) -> Error(error)
        | Ok(units) ->
            match buildModulePlan(projectEntryModuleName(layout))(units) with
                | Error(error) -> Error(ProjectCompilationModulePlanError(error))
                | Ok(modules) -> Ok(ProjectCompilationPlan(sourceFiles = paths, modules = modules))

let indexEnumeratedSources (style: Style) (layout: ProjectLayout) (roots: List(Str)) (paths: List(Str)) =
    match indexSources(style)(roots)(projectEntryPath(layout))(projectEntryModuleName(layout))(paths) with
        | Error(error) -> Error(error)
        | Ok(sources) -> planIndexedSources(layout)(paths)(sources)

let projectSourceRoots (layout: ProjectLayout) =
    match layout with
        | ProjectLayout { projectFilePath = _projectFilePath, projectDirectory = _projectDirectory, entryPath = _entryPath, entryModuleName = _entryModuleName, sourceRoots = sourceRoots, includeRoots = includeRoots, outDir = _outDir, manifest = _manifest } ->
            appendList(
                sourceRoots,
                includeRoots
            )

let recursive dependencySourceRoots dependencies =
    match dependencies with
        | [] -> []
        | ResolvedProjectDependency { name = _name, namespace = _namespace, sourceRoots = roots, projectDirectory = _projectDirectory, entryPath = _entryPath, isDev = _isDev } :: rest ->
            rest
            |> dependencySourceRoots
            |> appendList(roots)

let compilationSourceRoots (layout: ProjectLayout) (graph: ProjectDependencyGraph) =
    match graph with
        | ProjectDependencyGraph { dependencies = dependencies } ->
            dependencies
            |> dependencySourceRoots
            |> appendList(projectSourceRoots(layout))

let planCompilationRoots style (layout: ProjectLayout) roots =
    match enumerateProjectSourceFiles(style)(roots) with
        | Error(error) -> Error(ProjectCompilationSourceEnumerationError(error))
        | Ok(enumerated) ->
            enumerated
            |> ensureEntrySource(layout)
            |> indexEnumeratedSources(style)(layout)(roots)

let continueProjectDependencyGraph style (layout: ProjectLayout) graphResult =
    match graphResult with
        | Error(error) -> Error(ProjectCompilationDependencyGraphError(error))
        | Ok(graph) ->
            graph
            |> compilationSourceRoots(layout)
            |> planCompilationRoots(style)(layout)

let buildProjectCompilationPlan (style: Style) (layout: ProjectLayout) =
    layout
    |> resolveProjectDependencyGraph(style)
    |> continueProjectDependencyGraph(style)(layout)
