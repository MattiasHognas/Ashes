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
            match relativeTo(style)(root)(deepCopy(path)) with
                | relative ->
                    if isOutsideRoot(style)(relative)
                    then relativeSourcePath(style)(rest)(path)
                    else Some(relative)

let moduleNameFromRelative (style: Style) (relative: Str) =
    match Ashes.Text.take(deepCopy(relative))(Ashes.Text.length(relative) - 4) with
        | withoutExtension -> Ashes.Text.join(".")(Ashes.Text.split(withoutExtension)(separator(style)))

let indexedSource (style: Style) (roots: List(Str)) (entryPath: Str) (entryModuleName: Str) (path: Str) =
    if path == entryPath
    then Ok(IndexedProjectSource(name = deepCopy(entryModuleName), path = path))
    else
        match relativeSourcePath(style)(roots)(deepCopy(path)) with
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
    if containsText(layout.entryPath)(paths)
    then paths
    else layout.entryPath :: paths

let recursive dropLast (parts: List(Str)) =
    match parts with
        | [] -> []
        | _last :: [] -> []
        | head :: rest -> head :: dropLast(rest)

let parentModuleName (name: Str) =
    match dropLast(Ashes.Text.split(name)(".")) with
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
                        | Error(error) -> Error(ProjectCompilationInterfaceError(path)(error))
                        | Ok(moduleInterface) ->
                            match dependencyModuleNames(sources)(deepCopy(imports)) with
                                | dependencies -> Ok(LoadedProjectModule(unit = ModulePlanUnit(name = name, source = ProjectModuleSource(path), imports = imports, interface = moduleInterface), dependencies = dependencies))

let parseLoadedModule (name: Str) (path: Str) (sources: List(IndexedProjectSource)) (source: Str) =
    match parseImportHeader(source) with
        | Error(error) -> Error(ProjectCompilationImportHeaderError(path)(error))
        | Ok(header) ->
            match header with
                | ParsedImportHeader { imports = imports, sourceWithoutImports = sourceWithoutImports } ->
                    match deepCopy(imports) with
                        | retainedImports ->
                            match parseProgram(sourceWithoutImports) with
                                | parsed -> finishLoadedModule(name)(path)(sources)(retainedImports)(parsed)

let readIndexedModule (name: Str) (path: Str) (sources: List(IndexedProjectSource)) =
    match Ashes.IO.File.readText(path) with
        | Error(error) -> Error(ProjectCompilationReadError(path)(error))
        | Ok(source) -> parseLoadedModule(name)(path)(sources)(source)

let loadNamedModule (name: Str) (sources: List(IndexedProjectSource)) =
    match pathsForModule(name)(sources) with
        | [] -> Error(ProjectCompilationMissingModule(name)(indexedModuleNames(sources)))
        | path :: [] -> readIndexedModule(name)(path)(sources)
        | paths -> Error(ProjectCompilationAmbiguousModule(name)(paths))

let recursive loadReachableModules (pending: List(Str)) (loaded: List(Str)) (reversedUnits: List(ModulePlanUnit)) (sources: List(IndexedProjectSource)) =
    match pending with
        | [] -> Ok(reverseList(reversedUnits))
        | name :: rest ->
            if containsText(name)(loaded)
            then loadReachableModules(rest)(loaded)(reversedUnits)(sources)
            else
                match loadNamedModule(name)(sources) with
                    | Error(error) -> Error(error)
                    | Ok(loadedModule) -> loadReachableModules(appendList(loadedModule.dependencies)(rest))(name :: loaded)(loadedModule.unit :: reversedUnits)(sources)

let planIndexedSources (layout: ProjectLayout) (paths: List(Str)) (sources: List(IndexedProjectSource)) =
    match loadReachableModules([layout.entryModuleName])([])([])(sources) with
        | Error(error) -> Error(error)
        | Ok(units) ->
            match buildModulePlan(layout.entryModuleName)(units) with
                | Error(error) -> Error(ProjectCompilationModulePlanError(error))
                | Ok(modules) -> Ok(ProjectCompilationPlan(sourceFiles = paths, modules = modules))

let indexEnumeratedSources (style: Style) (layout: ProjectLayout) (roots: List(Str)) (paths: List(Str)) =
    match indexSources(style)(roots)(layout.entryPath)(layout.entryModuleName)(paths) with
        | Error(error) -> Error(error)
        | Ok(sources) -> planIndexedSources(layout)(paths)(sources)

let projectSourceRoots (layout: ProjectLayout) =
    match layout with
        | ProjectLayout { projectFilePath = _projectFilePath, projectDirectory = _projectDirectory, entryPath = _entryPath, entryModuleName = _entryModuleName, sourceRoots = sourceRoots, includeRoots = includeRoots, outDir = _outDir, manifest = _manifest } -> appendList(sourceRoots)(includeRoots)

let buildProjectCompilationPlan (style: Style) (layout: ProjectLayout) =
    match projectSourceRoots(layout) with
        | roots ->
            match enumerateProjectSourceFiles(style)(roots) with
                | Error(error) -> Error(ProjectCompilationSourceEnumerationError(error))
                | Ok(enumerated) ->
                    match ensureEntrySource(layout)(enumerated) with
                        | paths -> indexEnumeratedSources(style)(layout)(roots)(paths)
