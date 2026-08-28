// The `ashes why` command: show a path from a project root dependency to a named package,
// explaining why it is in the dependency graph.
//
// Invariants:
// - Matches stage 0's observable contract (docs/md/reference/cli.md#ashes-why): the path is
//   found over the SELECTED project's lock file, starting from its direct dependencies (both
//   `dependencies` and `devDependencies`) — never by re-running dependency resolution.
// - Reuses the already-ported project/lock-file infrastructure
//   (`AshesCompiler.Semantics.ProjectDiscovery`/`ProjectDependencyGraph`/`ProjectLockFile`) for
//   project discovery, root-dependency namespace resolution, and lock-file parsing rather than
//   re-implementing any of it.

import Ashes.Collection.List.append
import Ashes.Collection.List.map
import Ashes.IO.Path
import AshesCompiler.Semantics.ProjectDependencyGraph
import AshesCompiler.Semantics.ProjectDiscovery
import AshesCompiler.Semantics.ProjectLockFile
import AshesCompiler.Semantics.ProjectManifest
export (
    type WhyArguments(..),
    type WhyParse(..),
    type WhyOutcome(..),
    value parseWhyArguments,
    value findDependencyPath,
    value runWhyInProject,
    value runWhyWithArguments,
    value runWhy,
)

type WhyArguments =
    | target: Str
    | projectOption: Maybe(Str)

type WhyParse =
    | WhyHelpRequested
    | WhyUsageError(Str)
    | WhyParsedArguments(WhyArguments)

type WhyOutcome =
    | WhyFound(List(Str))
    | WhyNotFound(Str)
    | WhyFailed(Str)

let recursive collectWhyArgs args positionals projectOption =
    match args with
        | [] -> (positionals, projectOption)
        | "--project" :: value :: rest -> collectWhyArgs(rest)(positionals)(Some(value))
        | other :: rest -> collectWhyArgs(rest)(append(positionals)([other]))(projectOption)

// Mirrors stage 0's `RunWhy` (`src/Ashes.Cli/Program.cs`): a bare `--help`/`-h` short-circuits;
// otherwise the first positional argument is the target namespace (PascalCased, same as stage 0
// normalizes the CLI argument before searching), and `--project <manifest>` is optional. No
// positional argument at all is a usage error.
let parseWhyArguments args =
    match args with
        | "--help" :: [] -> WhyHelpRequested
        | "-h" :: [] -> WhyHelpRequested
        | _ ->
            match collectWhyArgs(args)([])(None) with
                | ([], _) -> WhyUsageError("Usage: ashes why <namespace>")
                | (target :: _rest, projectOption) -> WhyParsedArguments(WhyArguments(target = pascalCase(target), projectOption = projectOption))

let recursive lastOf list =
    match list with
        | last :: [] -> last
        | _ :: rest -> lastOf(rest)
        | [] -> ""

let recursive containsNamespace namespace list =
    match list with
        | [] -> false
        | head :: rest ->
            if head == namespace
            then true
            else containsNamespace(namespace)(rest)

let recursive edgesFor namespace edges =
    match edges with
        | [] -> []
        | (candidate, dependencies) :: rest ->
            if candidate == namespace
            then dependencies
            else edgesFor(namespace)(rest)

// Breadth-first search over the lock graph's edges, starting from every root simultaneously
// (mirroring stage 0's `FindPath`): each queue entry is a full root-to-here path, in order, so
// the first path reaching `target` is returned directly with no further reconstruction needed. A
// namespace already dequeued once is never expanded again, breaking cycles.
let recursive bfsWhy queue seen target edges =
    match queue with
        | [] -> None
        | path :: restQueue ->
            let current = lastOf(path)
            in
                if current == target
                then Some(path)
                else
                    if containsNamespace(current)(seen)
                    then bfsWhy(restQueue)(seen)(target)(edges)
                    else
                        let children = edgesFor(current)(edges)
                        in
                            let extended =
                                map(given (child) -> append(path)([child]))(children)
                            in bfsWhy(append(restQueue)(extended))(current :: seen)(target)(edges)

// The stable, testable core of `ashes why`: given the direct-dependency root namespaces and the
// lock file's own dependency edges (both already resolved by the caller), finds a path from any
// root to `target`.
let findDependencyPath roots target edges =
    match bfsWhy(map(given (root) -> [root])(roots))([])(target)(edges) with
        | Some(path) -> Some(path)
        | None -> None

// Mirrors stage 0's `namespaceByDir`: maps each RESOLVED (direct or transitive) dependency's
// project directory to its final namespace, so a root path-dependency can be resolved to the same
// namespace the compiler itself would use, without re-deriving it.
let namespaceByDirEntry resolved =
    match resolved with
        | ResolvedProjectDependency { name = _name, namespace = namespace, sourceRoots = _sourceRoots, projectDirectory = projectDirectory, entryPath = _entryPath, isDev = _isDev } -> (projectDirectory, namespace)

let namespaceByDirFor resolvedDependencies = map(namespaceByDirEntry)(resolvedDependencies)

let recursive lookupNamespaceByDir directory table =
    match table with
        | [] -> None
        | (candidateDirectory, namespace) :: rest ->
            if candidateDirectory == directory
            then Some(namespace)
            else lookupNamespaceByDir(directory)(rest)

// Mirrors stage 0's `DirectDependencyNamespaces`: a path dependency's namespace comes from the
// resolved graph (by matching its target directory); a registry dependency's namespace is always
// its PascalCased manifest key — never looked up in the resolved graph.
let directDependencyNamespace style projectDirectory namespaceByDir dependency =
    match dependency with
        | ProjectDependency { name = name, source = PathDependency(path, _versionConstraint) } ->
            let dependencyDirectory = normalize(style)(join(style)(projectDirectory)(path))
            in
                match lookupNamespaceByDir(dependencyDirectory)(namespaceByDir) with
                    | Some(namespace) -> namespace
                    | None -> pascalCase(name)
        | ProjectDependency { name = name, source = RegistryDependency(_versionConstraint) } -> pascalCase(name)

// The root project's OWN `dependencies` + `devDependencies` (never transitive ones), each resolved
// to a namespace — these are the BFS roots for `why`, matching stage 0 exactly.
let directDependencyNamespaces style projectDirectory namespaceByDir manifest =
    match manifest with
        | ProjectManifest { entry = _entry, name = _name, namespace = _namespace, version = _version, sourceRoots = _sourceRoots, includeRoots = _includeRoots, outDir = _outDir, target = _target, defaults = _defaults, dependencies = dependencies, devDependencies = devDependencies, overrides = _overrides } -> append(map(directDependencyNamespace(style)(projectDirectory)(namespaceByDir))(dependencies))(map(directDependencyNamespace(style)(projectDirectory)(namespaceByDir))(devDependencies))

let lockedPackageEdge locked =
    match locked with
        | LockedPackage { namespace = namespace, dependencies = dependencies } -> (namespace, dependencies)

let readLockEdges style projectFilePath =
    match Ashes.IO.File.readText(lockFilePath(style)(projectFilePath)) with
        | Error(_) -> []
        | Ok(source) ->
            match parseProjectLockFile(source) with
                | Error(_) -> []
                | Ok(ProjectLockFile { packages = packages }) -> map(lockedPackageEdge)(packages)

// Resolves the project at `manifestPath`, its direct-dependency root namespaces, and its lock
// file's edges, then searches for `target`. Fails only on a genuine project-loading error;
// nothing lower in the graph (a missing or unreadable lock file, an edge lookup miss) is treated
// as fatal, matching stage 0's own tolerant `ReadLockGraph` (an absent/unreadable lock file just
// yields an empty graph rather than an error).
let runWhyInProject style manifestPath target =
    match loadProject(style)(manifestPath) with
        | Error(_) -> WhyFailed("Project file not found: " + manifestPath)
        | Ok(layout) ->
            match layout with
                | ProjectLayout { projectFilePath = _projectFilePath, projectDirectory = projectDirectory, entryPath = _entryPath, entryModuleName = _entryModuleName, sourceRoots = _sourceRoots, includeRoots = _includeRoots, outDir = _outDir, manifest = manifest } ->
                    match resolveProjectDependencyGraph(style)(layout) with
                        | Error(_) -> WhyFailed("Failed to resolve the project's dependency graph.")
                        | Ok(ProjectDependencyGraph { dependencies = resolvedDependencies }) ->
                            let roots = directDependencyNamespaces(style)(projectDirectory)(namespaceByDirFor(resolvedDependencies))(manifest)
                            in
                                let edges = readLockEdges(style)(manifestPath)
                                in
                                    match findDependencyPath(roots)(target)(edges) with
                                        | Some(path) -> WhyFound(path)
                                        | None -> WhyNotFound(target)

// Resolves the manifest to use (an explicit `--project`, or discovery upward from the current
// directory) and runs `runWhyInProject` against it.
let runWhyWithArguments arguments =
    match arguments with
        | WhyArguments { target = target, projectOption = projectOption } ->
            let currentDirectory =
                match Ashes.IO.Environment.currentDirectory(Unit) with
                    | Ok(directory) -> directory
                    | Error(message) -> Ashes.IO.panic(message)
            in
                match selectProjectFile(Ashes.IO.Path.Unix)(currentDirectory)(projectOption) with
                    | Error(_) -> WhyFailed("No ashes.json found. Run 'ashes init' first.")
                    | Ok(None) -> WhyFailed("No ashes.json found. Run 'ashes init' first.")
                    | Ok(Some(manifestPath)) -> runWhyInProject(Ashes.IO.Path.Unix)(manifestPath)(target)

// The full `ashes why` entry point: parses `args`, prints stage 0's own messages, and returns the
// process exit code (0 whether or not a path is found — a missing dependency is reported, not an
// error — 1 a project/graph failure, 2 usage).
let runWhy args =
    match parseWhyArguments(args) with
        | WhyHelpRequested ->
            let _ = Ashes.IO.writeLine("Usage: ashes why <namespace> [--project <manifest>]")
            in 0
        | WhyUsageError(message) ->
            let _ = Ashes.IO.writeErrorLine(message)
            in 2
        | WhyParsedArguments(arguments) ->
            match runWhyWithArguments(arguments) with
                | WhyFound(path) ->
                    let _ = Ashes.IO.print(Ashes.Text.join(" -> ")(path))
                    in 0
                | WhyNotFound(target) ->
                    let _ = Ashes.IO.print("'" + target + "' is not a dependency of this project.")
                    in 0
                | WhyFailed(message) ->
                    let _ = Ashes.IO.writeErrorLine(message)
                    in 1
