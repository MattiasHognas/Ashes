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
        | name -> Ashes.Text.take(name)(Ashes.Text.length(name) - 4)

let projectLayout style projectFilePath projectDirectory manifest = ProjectLayout(projectFilePath = projectFilePath, projectDirectory = projectDirectory, entryPath = join(style)(projectDirectory)(manifest.entry), entryModuleName = moduleName(style)(manifest.entry), sourceRoots = resolvePaths(style)(projectDirectory)(manifest.sourceRoots), includeRoots = resolvePaths(style)(projectDirectory)(manifest.includeRoots), outDir = join(style)(projectDirectory)(manifest.outDir), manifest = manifest)

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
