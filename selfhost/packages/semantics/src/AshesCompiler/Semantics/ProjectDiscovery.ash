import Ashes.IO.Path
import Ashes.Internal.deepCopy as deepCopy
import AshesCompiler.Semantics.ProjectManifest
export (
    type ProjectLayout(..),
    type ProjectDiscoveryError(..),
    value discoverProjectFile,
    value selectProjectFile,
    value resolveProjectLayout,
    value loadProject,
)

type ProjectLayout =
    | projectFilePath: Str
    | projectDirectory: Str
    | entryPath: Str
    | entryModuleName: Str
    | sourceRoots: List(Str)
    | includeRoots: List(Str)
    | outDir: Str
    | manifest: ProjectManifest
    deriving {Eq, Show}

type ProjectDiscoveryError =
    | ProjectPathProbeError(Str, Str)
    | ProjectReadError(Str, Str)
    | ProjectManifestInvalid(Str, ProjectManifestError)
    | ProjectEntryNotFound(Str)
    deriving {Eq, Show}

type ProjectLayoutBuilder =
    | BuildingProjectLayout(Str, Str, Str, Str, List(Str), List(Str), Str, ProjectManifest)

let recursive discoverNormalized style directory =
    match "ashes.json"
    |> join(style)(directory)
    |> Ashes.IO.File.exists with
        | Error(error) ->
            error
            |> ProjectPathProbeError(join(style)(directory)("ashes.json"))
            |> Error
        | Ok(true) ->
            Ok("ashes.json"
            |> join(style)(directory)
            |> Some)
        | Ok(false) ->
            match parent(style)(directory) with
                | parentDirectory ->
                    if parentDirectory == directory
                    then Ok(None)
                    else discoverNormalized(style)(parentDirectory)

let discoverProjectFile style startDirectory =
    startDirectory
    |> normalize(style)
    |> discoverNormalized(style)

let selectProjectFile style currentDirectory explicitProject =
    match explicitProject with
        | Some(projectPath) ->
            Ok(projectPath
            |> join(style)(currentDirectory)
            |> Some)
        | None -> discoverProjectFile(style)(currentDirectory)

let recursive resolvePaths style directory paths =
    match paths with
        | [] -> []
        | path :: rest -> join(style)(directory)(path) :: resolvePaths(style)(directory)(rest)

let moduleName style entryPath =
    match basename(style)(entryPath) with
        | name ->
            Ashes.Text.take(deepCopy(name))(Ashes.Text.length(name) - 4)

let beginProjectLayout style file directory manifest entry sourceRoots includeRoots outDir entryPath =
    BuildingProjectLayout(file)(directory)(entryPath)(moduleName(style)(entry))(sourceRoots)(includeRoots)(outDir)(manifest)

let resolveProjectSourceRoots style (builder: ProjectLayoutBuilder) =
    match builder with
        | BuildingProjectLayout(f, d, e, n, s, i, o, m) ->
            BuildingProjectLayout(f)(d)(e)(n)(resolvePaths(style)(deepCopy(d))(s))(i)(o)(m)

let resolveProjectIncludeRoots style (builder: ProjectLayoutBuilder) =
    match builder with
        | BuildingProjectLayout(f, d, e, n, s, i, o, m) ->
            BuildingProjectLayout(f)(d)(e)(n)(s)(resolvePaths(style)(deepCopy(d))(i))(o)(m)

let resolveProjectOutput style (builder: ProjectLayoutBuilder) =
    match builder with
        | BuildingProjectLayout(f, d, e, n, s, i, o, m) ->
            BuildingProjectLayout(f)(d)(e)(n)(s)(i)(join(style)(deepCopy(d))(o))(m)

let completeProjectLayout (builder: ProjectLayoutBuilder) =
    match builder with
        | BuildingProjectLayout(f, d, e, n, s, i, o, m) -> ProjectLayout(projectFilePath = f, projectDirectory = d, entryPath = e, entryModuleName = n, sourceRoots = s, includeRoots = i, outDir = o, manifest = m)

let finishProjectLayout style projectFilePath projectDirectory manifest entry sourceRoots includeRoots outDir entryPath =
    entryPath
    |> beginProjectLayout(style)(projectFilePath)(projectDirectory)(manifest)(deepCopy(entry))(sourceRoots)(includeRoots)(outDir)
    |> resolveProjectSourceRoots(style)
    |> resolveProjectIncludeRoots(style)
    |> resolveProjectOutput(style)
    |> completeProjectLayout

let projectLayout style projectFilePath projectDirectory (manifest: ProjectManifest) =
    match deepCopy(manifest) with
        | ProjectManifest { entry = entry, name = _name, namespace = _namespace, version = _version, sourceRoots = sourceRoots, includeRoots = includeRoots, outDir = outDir, target = _target, defaults = _defaults, dependencies = _dependencies, devDependencies = _devDependencies, overrides = _overrides } ->
            entry
            |> deepCopy
            |> join(style)(deepCopy(projectDirectory))
            |> finishProjectLayout(style)(projectFilePath)(projectDirectory)(manifest)(deepCopy(entry))(sourceRoots)(includeRoots)(outDir)

let validateEntry style projectDirectory projectFilePath manifest =
    match projectLayout(style)(projectFilePath)(projectDirectory)(manifest) with
        | layout ->
            match Ashes.IO.File.exists(layout.entryPath) with
                | Error(error) ->
                    error
                    |> ProjectPathProbeError(layout.entryPath)
                    |> Error
                | Ok(true) -> Ok(layout)
                | Ok(false) -> Error(ProjectEntryNotFound(layout.entryPath))

let resolveProjectLayout style projectFilePath manifest =
    match normalize(style)(projectFilePath) with
        | normalizedProjectFile ->
            validateEntry(style)(normalizedProjectFile
            |> deepCopy
            |> parent(style))(normalizedProjectFile)(manifest)

let parseLoadedProject style projectFilePath source =
    match parseProjectManifest(source) with
        | Error(error) ->
            error
            |> ProjectManifestInvalid(projectFilePath)
            |> Error
        | Ok(manifest) -> resolveProjectLayout(style)(projectFilePath)(manifest)

let loadProject style projectFilePath =
    match Ashes.IO.File.readText(projectFilePath) with
        | Error(error) ->
            error
            |> ProjectReadError(projectFilePath)
            |> Error
        | Ok(source) -> parseLoadedProject(style)(projectFilePath)(source)
