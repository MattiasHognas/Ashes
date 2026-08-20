import Ashes.Collection.List.append as appendList
import Ashes.Collection.List.foldLeft
import Ashes.IO.Path
import Ashes.Internal.deepCopy as deepCopy
import AshesCompiler.Semantics.ProjectDiscovery
import AshesCompiler.Semantics.ProjectLockFile
import AshesCompiler.Semantics.ProjectManifest
import AshesCompiler.Semantics.ProjectSourceEnumeration
export (
    type ResolvedProjectDependency(..),
    type ProjectDependencyGraph(..),
    type ProjectDependencyGraphError(..),
    value resolveProjectDependencyGraph,
    value resolveProjectDependencyGraphFromCache,
)

type ResolvedProjectDependency =
    | name: Str
    | namespace: Str
    | sourceRoots: List(Str)
    | projectDirectory: Str
    | entryPath: Str
    | isDev: Bool
    deriving {Eq, Show}

type ProjectDependencyGraph =
    | dependencies: List(ResolvedProjectDependency)
    deriving {Eq, Show}

type ProjectDependencyGraphError =
    | ProjectDependencyPathNotFound(Str, Str)
    | ProjectDependencyManifestNotFound(Str, Str)
    | ProjectDependencyProjectError(Str, ProjectDiscoveryError)
    | ProjectDependencySourceEnumerationError(Str, ProjectSourceEnumerationError)
    | ProjectDependencyCycle(Str, Str)
    | ProjectDependencyNamespaceConflict(Str, Str, Str)
    | ProjectDependencyModuleOutsideNamespace(Str, Str, Str)
    | ProjectLockPathProbeError(Str, Str)
    | ProjectLockReadError(Str, Str)
    | ProjectLockInvalid(Str, ProjectLockFileError)
    | ProjectLockedPackageMissing(Str, Str, Str)
    | ProjectLockedPackageProjectError(Str, ProjectDiscoveryError)
    | ProjectCacheDirectoryError(Str)
    deriving {Eq, Show}

type ProjectDependencyGraphState =
    | dependencies: List(ResolvedProjectDependency)
    | visitedDirectories: List(Str)

let recursive containsText (value: Str) (values: List(Str)) =
    match values with
        | [] -> false
        | candidate :: rest ->
            if candidate == value
            then true
            else containsText(value)(rest)

let upperAscii value =
    match value with
        | "a" -> "A"
        | "b" -> "B"
        | "c" -> "C"
        | "d" -> "D"
        | "e" -> "E"
        | "f" -> "F"
        | "g" -> "G"
        | "h" -> "H"
        | "i" -> "I"
        | "j" -> "J"
        | "k" -> "K"
        | "l" -> "L"
        | "m" -> "M"
        | "n" -> "N"
        | "o" -> "O"
        | "p" -> "P"
        | "q" -> "Q"
        | "r" -> "R"
        | "s" -> "S"
        | "t" -> "T"
        | "u" -> "U"
        | "v" -> "V"
        | "w" -> "W"
        | "x" -> "X"
        | "y" -> "Y"
        | "z" -> "Z"
        | _ -> value

let isAsciiLetterOrDigit value =
    match value with
        | "A" -> true
        | "B" -> true
        | "C" -> true
        | "D" -> true
        | "E" -> true
        | "F" -> true
        | "G" -> true
        | "H" -> true
        | "I" -> true
        | "J" -> true
        | "K" -> true
        | "L" -> true
        | "M" -> true
        | "N" -> true
        | "O" -> true
        | "P" -> true
        | "Q" -> true
        | "R" -> true
        | "S" -> true
        | "T" -> true
        | "U" -> true
        | "V" -> true
        | "W" -> true
        | "X" -> true
        | "Y" -> true
        | "Z" -> true
        | "a" -> true
        | "b" -> true
        | "c" -> true
        | "d" -> true
        | "e" -> true
        | "f" -> true
        | "g" -> true
        | "h" -> true
        | "i" -> true
        | "j" -> true
        | "k" -> true
        | "l" -> true
        | "m" -> true
        | "n" -> true
        | "o" -> true
        | "p" -> true
        | "q" -> true
        | "r" -> true
        | "s" -> true
        | "t" -> true
        | "u" -> true
        | "v" -> true
        | "w" -> true
        | "x" -> true
        | "y" -> true
        | "z" -> true
        | "0" -> true
        | "1" -> true
        | "2" -> true
        | "3" -> true
        | "4" -> true
        | "5" -> true
        | "6" -> true
        | "7" -> true
        | "8" -> true
        | "9" -> true
        | _ -> false

let recursive pascalCaseCharacters remaining capitalize result =
    match Ashes.Text.unconsText(remaining) with
        | None -> result
        | Some((head, tail)) ->
            if isAsciiLetterOrDigit(head)
            then continuePascalCase(tail)(capitalize)(result)(head)
            else pascalCaseCharacters(tail)(true)(result)
and continuePascalCase (tail: Str) (capitalize: Bool) (result: Str) (head: Str) =
    if capitalize
    then pascalCaseCharacters(tail)(false)(result + upperAscii(head))
    else pascalCaseCharacters(tail)(false)(result + head)

let pascalCase value =
    match pascalCaseCharacters(value)(true)("") with
        | "" -> value
        | converted -> converted

let manifestFallbackName dependencyName (manifest: ProjectManifest) =
    match manifest with
        | ProjectManifest { entry = _entry, name = Some(name), namespace = _namespace, sourceRoots = _sourceRoots, includeRoots = _includeRoots, outDir = _outDir, target = _target, defaults = _defaults, dependencies = _dependencies, devDependencies = _devDependencies } -> name
        | ProjectManifest { entry = _entry, name = None, namespace = _namespace, sourceRoots = _sourceRoots, includeRoots = _includeRoots, outDir = _outDir, target = _target, defaults = _defaults, dependencies = _dependencies, devDependencies = _devDependencies } -> dependencyName

let manifestNamespace (dependencyName: Str) (manifest: ProjectManifest) =
    match manifest with
        | ProjectManifest { entry = _entry, name = _name, namespace = Some(namespace), sourceRoots = _sourceRoots, includeRoots = _includeRoots, outDir = _outDir, target = _target, defaults = _defaults, dependencies = _dependencies, devDependencies = _devDependencies } -> namespace
        | ProjectManifest { entry = _entry, name = _name, namespace = None, sourceRoots = _sourceRoots, includeRoots = _includeRoots, outDir = _outDir, target = _target, defaults = _defaults, dependencies = _dependencies, devDependencies = _devDependencies } ->
            manifest
            |> manifestFallbackName(dependencyName)
            |> pascalCase

let dependencyNamespace dependencyName namespaceOverride (manifest: ProjectManifest) =
    match namespaceOverride with
        | Some(namespace) -> namespace
        | None -> manifestNamespace(dependencyName)(manifest)

let recursive namespaceOwner (namespace: Str) (dependencies: List(ResolvedProjectDependency)) =
    match dependencies with
        | [] -> None
        | ResolvedProjectDependency { name = name, namespace = candidate, sourceRoots = _sourceRoots, projectDirectory = _projectDirectory, entryPath = _entryPath, isDev = _isDev } :: rest ->
            if candidate == namespace
            then Some(name)
            else namespaceOwner(namespace)(rest)

let isOutsideRoot (style: Style) relative =
    if relative == ".."
    then true
    else Ashes.Text.startsWith(relative)(".." + separator(style))

let recursive relativeSourcePath (style: Style) roots path =
    match roots with
        | [] -> None
        | root :: rest ->
            path
            |> deepCopy
            |> relativeTo(style)(root)
            |> continueRelativeSourcePath(style)(rest)(path)
and continueRelativeSourcePath (style: Style) (rest: List(Str)) (path: Str) (relative: Str) =
    if isOutsideRoot(style)(relative)
    then relativeSourcePath(style)(rest)(path)
    else Some(relative)

let moduleNameFromRelative (style: Style) relative =
    match Ashes.Text.take(deepCopy(relative))(Ashes.Text.length(relative) - 4) with
        | withoutExtension ->
            style
            |> separator
            |> Ashes.Text.split(withoutExtension)
            |> Ashes.Text.join(".")

let isInsideNamespace (moduleName: Str) (namespace: Str) =
    if moduleName == namespace
    then true
    else Ashes.Text.startsWith(moduleName)(namespace + ".")

let validateDependencyModuleName dependencyName namespace moduleName =
    if isInsideNamespace(moduleName)(namespace)
    then Ok(Unit)
    else
        namespace
        |> ProjectDependencyModuleOutsideNamespace(dependencyName)(moduleName)
        |> Error

let validateRelativeDependencyModule style dependencyName namespace relative =
    match relative with
        | None -> Ok(Unit)
        | Some(path) ->
            path
            |> moduleNameFromRelative(style)
            |> validateDependencyModuleName(dependencyName)(namespace)

let validateDependencyModulePath style dependencyName namespace roots entryPath path =
    if path == entryPath
    then Ok(Unit)
    else
        path
        |> deepCopy
        |> relativeSourcePath(style)(roots)
        |> validateRelativeDependencyModule(style)(dependencyName)(namespace)

let recursive validateDependencyModules style dependencyName namespace roots entryPath paths =
    match paths with
        | [] -> Ok(Unit)
        | path :: rest ->
            match validateDependencyModulePath(style)(dependencyName)(namespace)(roots)(entryPath)(path) with
                | Error(error) -> Error(error)
                | Ok(Unit) -> validateDependencyModules(style)(dependencyName)(namespace)(roots)(entryPath)(rest)

let validateDependencyNamespace style dependencyName namespace roots entryPath =
    match roots
    |> deepCopy
    |> enumerateProjectSourceFiles(style) with
        | Error(error) ->
            error
            |> ProjectDependencySourceEnumerationError(dependencyName)
            |> Error
        | Ok(paths) -> validateDependencyModules(style)(dependencyName)(namespace)(roots)(entryPath)(paths)

let dependencyRecord dependencyName namespace isDev (layout: ProjectLayout) =
    match layout with
        | ProjectLayout { projectFilePath = _projectFilePath, projectDirectory = projectDirectory, entryPath = entryPath, entryModuleName = _entryModuleName, sourceRoots = sourceRoots, includeRoots = _includeRoots, outDir = _outDir, manifest = _manifest } -> ResolvedProjectDependency(name = dependencyName, namespace = namespace, sourceRoots = sourceRoots, projectDirectory = projectDirectory, entryPath = entryPath, isDev = isDev)

let layoutDirectory (layout: ProjectLayout) =
    match layout with
        | ProjectLayout { projectFilePath = _projectFilePath, projectDirectory = directory, entryPath = _entryPath, entryModuleName = _entryModuleName, sourceRoots = _sourceRoots, includeRoots = _includeRoots, outDir = _outDir, manifest = _manifest } -> directory

let layoutEntryPath (layout: ProjectLayout) =
    match layout with
        | ProjectLayout { projectFilePath = _projectFilePath, projectDirectory = _directory, entryPath = entryPath, entryModuleName = _entryModuleName, sourceRoots = _sourceRoots, includeRoots = _includeRoots, outDir = _outDir, manifest = _manifest } -> entryPath

let layoutSourceRoots (layout: ProjectLayout) =
    match layout with
        | ProjectLayout { projectFilePath = _projectFilePath, projectDirectory = _directory, entryPath = _entryPath, entryModuleName = _entryModuleName, sourceRoots = sourceRoots, includeRoots = _includeRoots, outDir = _outDir, manifest = _manifest } -> sourceRoots

let layoutManifest (layout: ProjectLayout) =
    match layout with
        | ProjectLayout { projectFilePath = _projectFilePath, projectDirectory = _directory, entryPath = _entryPath, entryModuleName = _entryModuleName, sourceRoots = _sourceRoots, includeRoots = _includeRoots, outDir = _outDir, manifest = manifest } -> manifest

let manifestDependencies (manifest: ProjectManifest) =
    match manifest with
        | ProjectManifest { entry = _entry, name = _name, namespace = _namespace, sourceRoots = _sourceRoots, includeRoots = _includeRoots, outDir = _outDir, target = _target, defaults = _defaults, dependencies = dependencies, devDependencies = _devDependencies } -> dependencies

let stateWithDependency dependency directory (state: ProjectDependencyGraphState) =
    match state with
        | ProjectDependencyGraphState { dependencies = dependencies, visitedDirectories = visitedDirectories } -> ProjectDependencyGraphState(dependencies = appendList(dependencies)([dependency]), visitedDirectories = directory :: visitedDirectories)

let dependencyManifestPath style directory = join(style)(directory)("ashes.json")

let layoutProjectFilePath (layout: ProjectLayout) =
    match layout with
        | ProjectLayout { projectFilePath = path, projectDirectory = _projectDirectory, entryPath = _entryPath, entryModuleName = _entryModuleName, sourceRoots = _sourceRoots, includeRoots = _includeRoots, outDir = _outDir, manifest = _manifest } -> path

let recursive resolveDependencyList style dependencies manifestDirectory isDev chain state =
    match dependencies with
        | [] -> Ok(state)
        | ProjectDependency { name = _name, source = RegistryDependency(_constraint) } :: rest -> resolveDependencyList(style)(rest)(manifestDirectory)(isDev)(chain)(state)
        | ProjectDependency { name = name, source = PathDependency(path, namespaceOverride) } :: rest ->
            path
            |> join(style)(deepCopy(manifestDirectory))
            |> normalize(style)
            |> dependencyManifestPath(style)
            |> loadProject(style)
            |> continueLoadedDependency(style)(name)(namespaceOverride)(rest)(manifestDirectory)(isDev)(chain)(state)
and continueLoadedDependency style name namespaceOverride remaining manifestDirectory isDev chain state loaded =
    match loaded with
        | Error(ProjectReadError(manifestPath, _error)) -> classifyMissingDependency(style)(name)(manifestPath)
        | Error(error) ->
            error
            |> ProjectDependencyProjectError(name)
            |> Error
        | Ok(layout) -> continueLoadedLayout(style)(name)(namespaceOverride)(remaining)(manifestDirectory)(isDev)(chain)(state)(layout)
and classifyMissingDependency style name manifestPath =
    Ashes.Text.length(manifestPath) - Ashes.Text.length(separator(style) + "ashes.json")
    |> Ashes.Text.take(deepCopy(manifestPath))
    |> classifyDependencyDirectory(name)(manifestPath)
and classifyDependencyDirectory name manifestPath dependencyDirectory =
    match dependencyDirectory
    |> deepCopy
    |> Ashes.IO.Directory.entries with
        | Error(_error) ->
            dependencyDirectory
            |> ProjectDependencyPathNotFound(name)
            |> Error
        | Ok(_entries) ->
            manifestPath
            |> ProjectDependencyManifestNotFound(name)
            |> Error
and continueLoadedLayout style name namespaceOverride remaining manifestDirectory isDev chain state (layout: ProjectLayout) =
    layout
    |> layoutDirectory
    |> rejectDependencyCycle(name)(chain)
    |> continueAcyclicDependency(style)(name)(namespaceOverride)(remaining)(manifestDirectory)(isDev)(chain)(state)(layout)
and rejectDependencyCycle name chain dependencyDirectory =
    if containsText(dependencyDirectory)(chain)
    then
        dependencyDirectory
        |> ProjectDependencyCycle(name)
        |> Error
    else Ok(dependencyDirectory)
and continueAcyclicDependency style name namespaceOverride remaining manifestDirectory isDev chain state layout directoryResult =
    match directoryResult with
        | Error(error) -> Error(error)
        | Ok(dependencyDirectory) -> continueUnvisitedDependency(style)(name)(namespaceOverride)(remaining)(manifestDirectory)(isDev)(chain)(state)(layout)(dependencyDirectory)
and continueUnvisitedDependency style name namespaceOverride remaining manifestDirectory isDev chain (state: ProjectDependencyGraphState) layout dependencyDirectory =
    match state with
        | ProjectDependencyGraphState { dependencies = _dependencies, visitedDirectories = visitedDirectories } ->
            if containsText(dependencyDirectory)(visitedDirectories)
            then resolveDependencyList(style)(remaining)(manifestDirectory)(isDev)(chain)(state)
            else prepareResolvedDependency(style)(name)(namespaceOverride)(remaining)(manifestDirectory)(isDev)(chain)(state)(layout)(dependencyDirectory)
and prepareResolvedDependency style name namespaceOverride remaining manifestDirectory isDev chain (state: ProjectDependencyGraphState) (layout: ProjectLayout) dependencyDirectory =
    layout
    |> layoutManifest
    |> dependencyNamespace(name)(namespaceOverride)
    |> checkDependencyNamespace(name)(state)
    |> continueUniqueNamespace(style)(name)(remaining)(manifestDirectory)(isDev)(chain)(state)(layout)(dependencyDirectory)
and checkDependencyNamespace name (state: ProjectDependencyGraphState) namespace =
    match state with
        | ProjectDependencyGraphState { dependencies = dependencies, visitedDirectories = _visitedDirectories } ->
            dependencies
            |> namespaceOwner(namespace)
            |> checkNamespaceOwner(name)(namespace)
and checkNamespaceOwner name namespace owner =
    match owner with
        | Some(existingName) ->
            name
            |> ProjectDependencyNamespaceConflict(namespace)(existingName)
            |> Error
        | None -> Ok(namespace)
and continueUniqueNamespace style name remaining manifestDirectory isDev chain state (layout: ProjectLayout) dependencyDirectory namespaceResult =
    match namespaceResult with
        | Error(error) -> Error(error)
        | Ok(namespace) ->
            layout
            |> layoutEntryPath
            |> validateDependencyNamespace(style)(name)(namespace)(layoutSourceRoots(layout))
            |> continueValidDependency(style)(name)(namespace)(remaining)(manifestDirectory)(isDev)(chain)(state)(layout)(dependencyDirectory)
and continueValidDependency style name namespace remaining manifestDirectory isDev chain state layout dependencyDirectory validation =
    match validation with
        | Error(error) -> Error(error)
        | Ok(Unit) ->
            state
            |> stateWithDependency(dependencyRecord(name)(namespace)(isDev)(layout))(dependencyDirectory)
            |> resolveDependencyChildren(style)(layout)(dependencyDirectory)(remaining)(manifestDirectory)(isDev)(chain)
and resolveDependencyChildren style (layout: ProjectLayout) dependencyDirectory remaining manifestDirectory isDev chain state =
    layout
    |> layoutManifest
    |> manifestDependencies
    |> resolveDependencyListFrom(style)(dependencyDirectory)(isDev)(dependencyDirectory :: chain)(state)
    |> continueRemainingDependencies(style)(remaining)(manifestDirectory)(isDev)(chain)
and resolveDependencyListFrom style manifestDirectory isDev chain state dependencies = resolveDependencyList(style)(dependencies)(manifestDirectory)(isDev)(chain)(state)
and continueRemainingDependencies style remaining manifestDirectory isDev chain childrenResult =
    match childrenResult with
        | Error(error) -> Error(error)
        | Ok(state) -> resolveDependencyList(style)(remaining)(manifestDirectory)(isDev)(chain)(state)

let continueRootDevDependencies style projectDirectory devDependencies dependencyResult =
    match dependencyResult with
        | Error(error) -> Error(error)
        | Ok(state) -> resolveDependencyList(style)(devDependencies)(projectDirectory)(true)([])(state)

let resolveRootDependencies style (layout: ProjectLayout) =
    match layout with
        | ProjectLayout { projectFilePath = _projectFilePath, projectDirectory = projectDirectory, entryPath = _entryPath, entryModuleName = _entryModuleName, sourceRoots = _sourceRoots, includeRoots = _includeRoots, outDir = _outDir, manifest = ProjectManifest { entry = _entry, name = _name, namespace = _namespace, sourceRoots = _manifestSourceRoots, includeRoots = _manifestIncludeRoots, outDir = _manifestOutDir, target = _target, defaults = _defaults, dependencies = dependencies, devDependencies = devDependencies } } ->
            dependencies
            |> resolveDependencyListFrom(style)(projectDirectory)(false)([])(ProjectDependencyGraphState(dependencies = [], visitedDirectories = []))
            |> continueRootDevDependencies(style)(projectDirectory)(devDependencies)

let lockedPackageNamespace (package: LockedPackage) =
    match package with
        | LockedPackage { namespace = namespace, version = _version, source = _source, hash = _hash, dependencies = _dependencies } -> namespace

let lockedPackageVersion (package: LockedPackage) =
    match package with
        | LockedPackage { namespace = _namespace, version = version, source = _source, hash = _hash, dependencies = _dependencies } -> version

let loadLockedPackage style cacheRoot state package =
    (let namespace =
        package
        |> deepCopy
        |> lockedPackageNamespace
    in
        let version =
            package
            |> deepCopy
            |> lockedPackageVersion
        in
            let packageDirectory = cachePathFor(style)(cacheRoot)(package)
            in
                let manifestPath =
                    packageDirectory
                    |> deepCopy
                    |> dependencyManifestPath(style)
                in
                    match Ashes.IO.File.exists(manifestPath) with
                        | Error(error) ->
                            error
                            |> ProjectLockPathProbeError(manifestPath)
                            |> Error
                        | Ok(false) ->
                            packageDirectory
                            |> ProjectLockedPackageMissing(namespace)(version)
                            |> Error
                        | Ok(true) ->
                            match loadProject(style)(manifestPath) with
                                | Error(error) ->
                                    error
                                    |> ProjectLockedPackageProjectError(namespace)
                                    |> Error
                                | Ok(layout) ->
                                    match namespace
                                    |> deepCopy
                                    |> checkDependencyNamespace(deepCopy(namespace))(state) with
                                        | Error(error) -> Error(error)
                                        | Ok(_) ->
                                            match layout
                                            |> layoutEntryPath
                                            |> validateDependencyNamespace(style)(deepCopy(namespace))(deepCopy(namespace))(layoutSourceRoots(layout)) with
                                                | Error(error) -> Error(error)
                                                | Ok(Unit) ->
                                                    state
                                                    |> stateWithDependency(dependencyRecord(deepCopy(namespace))(namespace)(false)(layout))(packageDirectory)
                                                    |> Ok)

let addLockedPackage style cacheRoot result package =
    match result with
        | Error(error) -> Error(error)
        | Ok(state) -> loadLockedPackage(style)(cacheRoot)(state)(package)

let resolveLockedPackages style cacheRoot packages state =
    foldLeft(addLockedPackage(style)(cacheRoot))(Ok(state))(packages)

let readProjectLock style cacheRoot path state =
    match Ashes.IO.File.readText(path) with
        | Error(error) ->
            error
            |> ProjectLockReadError(path)
            |> Error
        | Ok(source) ->
            match parseProjectLockFile(source) with
                | Error(error) ->
                    error
                    |> ProjectLockInvalid(path)
                    |> Error
                | Ok(ProjectLockFile { version = _version, packages = packages }) -> resolveLockedPackages(style)(cacheRoot)(packages)(state)

let addLockedDependencies style cacheRoot layout state =
    (let path =
        layout
        |> layoutProjectFilePath
        |> lockFilePath(style)
    in
        match Ashes.IO.File.exists(path) with
            | Error(error) ->
                error
                |> ProjectLockPathProbeError(path)
                |> Error
            | Ok(false) -> Ok(state)
            | Ok(true) -> readProjectLock(style)(cacheRoot)(path)(state))

let finishProjectDependencyGraph result =
    match result with
        | Error(error) -> Error(error)
        | Ok(ProjectDependencyGraphState { dependencies = dependencies, visitedDirectories = _visitedDirectories }) -> Ok(ProjectDependencyGraph(dependencies = dependencies))

let resolveProjectDependencyGraphFromCache style cacheRoot layout =
    match resolveRootDependencies(style)(layout) with
        | Error(error) -> Error(error)
        | Ok(state) ->
            state
            |> addLockedDependencies(style)(cacheRoot)(layout)
            |> finishProjectDependencyGraph

let resolveProjectDependencyGraph style layout =
    match Ashes.IO.Environment.cacheDirectory(Unit) with
        | Error(error) -> Error(ProjectCacheDirectoryError(error))
        | Ok(cacheDirectory) ->
            resolveProjectDependencyGraphFromCache(style)(join(style)(cacheDirectory)("ashes"))(layout)
