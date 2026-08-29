// The `ashes restore` command: resolve and list a project's dependencies.
//
// Invariants:
// - Reuses the already-ported project/dependency-graph infrastructure
//   (`AshesCompiler.Semantics.ProjectDiscovery`/`ProjectDependencyGraph`/`ProjectManifest`) rather
//   than re-implementing resolution.
// - Covers path dependencies only. A project whose root manifest declares a registry dependency
//   (a `dependencies`/`devDependencies` entry with a version constraint rather than a `path`)
//   refuses cleanly rather than claiming a restore it did not perform: no network access, lock-file
//   writing, or content-hash verification exists yet.

import Ashes.Collection.List.length
import AshesCompiler.Semantics.ProjectDependencyGraph
import AshesCompiler.Semantics.ProjectDiscovery
import AshesCompiler.Semantics.ProjectManifest
export (
    type RestoreArguments(..),
    type RestoreParse(..),
    type RestoreOutcome(..),
    value parseRestoreArguments,
    value runRestoreInProject,
    value runRestoreWithArguments,
    value runRestore,
)

type RestoreArguments =
    | projectOption: Maybe(Str)

type RestoreParse =
    | RestoreHelpRequested
    | RestoreParsedArguments(RestoreArguments)

type RestoreOutcome =
    | RestoreNothingToDo
    | RestoreCompleted(List(ResolvedProjectDependency))
    | RestoreRegistryUnsupported
    | RestoreFailed(Str)

let recursive collectRestoreProjectOption args projectOption =
    match args with
        | [] -> projectOption
        | "--project" :: value :: rest -> collectRestoreProjectOption(rest)(Some(value))
        | _other :: rest -> collectRestoreProjectOption(rest)(projectOption)

// A bare `--help`/`-h` short-circuits; every other flag (`--registry`, `--frozen`, `--offline`) is
// accepted syntactically and ignored, since none of them affect the path-only resolution this
// covers.
let parseRestoreArguments args =
    match args with
        | "--help" :: [] -> RestoreHelpRequested
        | "-h" :: [] -> RestoreHelpRequested
        | _ -> RestoreParsedArguments(RestoreArguments(projectOption = collectRestoreProjectOption(args)(None)))

let recursive overridesHavePathFor name overrides =
    match overrides with
        | [] -> false
        | ProjectOverride { name = candidate, path = Some(_path) } :: rest ->
            if candidate == name
            then true
            else overridesHavePathFor(name)(rest)
        | _other :: rest -> overridesHavePathFor(name)(rest)

// A registry-sourced dependency overridden to a path (the shape every selfhost package's own
// `ashes.json` uses for its registry-named, path-resolved dependencies) does not need registry
// access, matching stage 0's own `PackageRestorePolicy.NeedsRestore`.
let recursive manifestDependenciesNeedRegistry dependencies overrides =
    match dependencies with
        | [] -> false
        | ProjectDependency { name = name, source = RegistryDependency(_constraint) } :: rest ->
            if overridesHavePathFor(name)(overrides)
            then manifestDependenciesNeedRegistry(rest)(overrides)
            else true
        | _other :: rest -> manifestDependenciesNeedRegistry(rest)(overrides)

let manifestNeedsRegistryRestore manifest =
    match manifest with
        | ProjectManifest { dependencies = dependencies, devDependencies = devDependencies, overrides = overrides } ->
            if manifestDependenciesNeedRegistry(dependencies)(overrides)
            then true
            else manifestDependenciesNeedRegistry(devDependencies)(overrides)

let runRestoreInProject style manifestPath =
    match loadProject(style)(manifestPath) with
        | Error(_error) -> RestoreFailed("Project file not found: " + manifestPath)
        | Ok(ProjectLayout { manifest = manifest } as layout) ->
            if manifestNeedsRegistryRestore(manifest)
            then RestoreRegistryUnsupported
            else
                match resolveProjectDependencyGraph(style)(layout) with
                    | Error(_error) -> RestoreFailed("Failed to resolve the project's dependency graph.")
                    | Ok(ProjectDependencyGraph { dependencies = [] }) -> RestoreNothingToDo
                    | Ok(ProjectDependencyGraph { dependencies = resolvedDependencies }) -> RestoreCompleted(resolvedDependencies)

// Resolves the manifest to use (an explicit `--project`, or discovery upward from the current
// directory) and runs `runRestoreInProject` against it.
let runRestoreWithArguments arguments =
    match arguments with
        | RestoreArguments { projectOption = projectOption } ->
            let currentDirectory =
                match Ashes.IO.Environment.currentDirectory(Unit) with
                    | Ok(directory) -> directory
                    | Error(message) -> Ashes.IO.panic(message)
            in
                match selectProjectFile(Ashes.IO.Path.Unix)(currentDirectory)(projectOption) with
                    | Error(_error) -> RestoreFailed("No ashes.json found. Run 'ashes init' first.")
                    | Ok(None) -> RestoreFailed("No ashes.json found. Run 'ashes init' first.")
                    | Ok(Some(manifestPath)) -> runRestoreInProject(Ashes.IO.Path.Unix)(manifestPath)

let formatResolvedDependency dependency =
    match dependency with
        | ResolvedProjectDependency { name = name, namespace = namespace, projectDirectory = projectDirectory, isDev = false } -> "  " + name + " -> " + namespace + " (" + projectDirectory + ")"
        | ResolvedProjectDependency { name = name, namespace = namespace, projectDirectory = projectDirectory, isDev = true } -> "  " + name + " -> " + namespace + " (" + projectDirectory + ") (dev)"

let recursive printResolvedDependencies dependencies =
    match dependencies with
        | [] -> Unit
        | dependency :: rest ->
            let _ = Ashes.IO.print(formatResolvedDependency(dependency))
            in printResolvedDependencies(rest)

let dependencyCountWord count =
    if count == 1
    then "dependency"
    else "dependencies"

// The full `ashes restore` entry point: parses `args`, prints stage 0's own messages for the cases
// this covers, and returns the process exit code (0 for a completed or empty restore, 1 for a
// project/graph failure or an unsupported registry dependency, 2 usage — matching stage 0's
// exit-code contract, never reachable here since every parse succeeds).
let runRestore args =
    match parseRestoreArguments(args) with
        | RestoreHelpRequested ->
            let _ = Ashes.IO.writeLine("Usage: ashes restore [--project <manifest>] [--registry <name-or-url>] [--frozen] [--offline]")
            in 0
        | RestoreParsedArguments(arguments) ->
            match runRestoreWithArguments(arguments) with
                | RestoreNothingToDo ->
                    let _ = Ashes.IO.print("No dependencies to restore.")
                    in 0
                | RestoreCompleted(dependencies) ->
                    let count = length(dependencies)
                    in
                        let _ = Ashes.IO.print("Restored " + Ashes.Text.fromInt(count) + " " + dependencyCountWord(count) + ":")
                        in
                            let _ = printResolvedDependencies(dependencies)
                            in 0
                | RestoreRegistryUnsupported ->
                    let _ = Ashes.IO.writeErrorLine("Registry dependency restore is not yet supported by the self-hosted CLI.")
                    in 1
                | RestoreFailed(message) ->
                    let _ = Ashes.IO.writeErrorLine(message)
                    in 1
