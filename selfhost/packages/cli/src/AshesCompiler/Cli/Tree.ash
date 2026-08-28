// The `ashes tree` command: render the resolved dependency tree (project root -> direct
// dependencies -> their transitive dependencies) from the selected project's lock file.
//
// Invariants:
// - Matches stage 0's observable contract (docs/md/reference/cli.md#ashes-tree): the tree is built
//   over the SELECTED project's lock file, rooted at its direct dependencies (both `dependencies`
//   and `devDependencies`, in that order) — never by re-running dependency resolution.
// - Reuses the already-ported project/lock-file infrastructure
//   (`AshesCompiler.Semantics.ProjectDiscovery`/`ProjectDependencyGraph`/`ProjectLockFile`) for
//   project discovery, root-dependency namespace resolution, and lock-file parsing rather than
//   re-implementing any of it.
// - Stage 0 renders through Spectre.Console's `Tree` widget; this port emits the same plain-text
//   shape (root line, then `--`-style guide-connected child lines) without any of Spectre's color
//   markup, matching the CLI reference's own example rendering.

import Ashes.Collection.List.append
import Ashes.Collection.List.map
import Ashes.IO.Path
import AshesCompiler.Semantics.ProjectDependencyGraph
import AshesCompiler.Semantics.ProjectDiscovery
import AshesCompiler.Semantics.ProjectLockFile
import AshesCompiler.Semantics.ProjectManifest
export (
    type TreeArguments(..),
    type TreeParse(..),
    type TreeOutcome(..),
    value parseTreeArguments,
    value renderDependencyTree,
    value runTreeInProject,
    value runTreeWithArguments,
    value runTree,
)

type TreeArguments =
    | projectOption: Maybe(Str)

type TreeParse =
    | TreeHelpRequested
    | TreeParsedArguments(TreeArguments)

type TreeOutcome =
    | TreeRendered(Str)
    | TreeFailed(Str)

let recursive collectTreeProjectOption args projectOption =
    match args with
        | [] -> projectOption
        | "--project" :: value :: rest -> collectTreeProjectOption(rest)(Some(value))
        | _ :: rest -> collectTreeProjectOption(rest)(projectOption)

// Mirrors stage 0's `RunTree` (`src/Ashes.Cli/Program.cs`): a bare `--help`/`-h` short-circuits;
// otherwise `--project <manifest>` is optional and every other argument is ignored, matching
// stage 0's own generic option scanner (`tree` never reports a usage error).
let parseTreeArguments args =
    match args with
        | "--help" :: [] -> TreeHelpRequested
        | "-h" :: [] -> TreeHelpRequested
        | _ -> TreeParsedArguments(TreeArguments(projectOption = collectTreeProjectOption(args)(None)))

let recursive treeContainsNamespace (namespace: Str) (list: List(Str)) =
    match list with
        | [] -> false
        | head :: rest ->
            if head == namespace
            then true
            else treeContainsNamespace(namespace)(rest)

let recursive treeEdgesFor (namespace: Str) (edges: List((Str, List(Str)))) =
    match edges with
        | [] -> []
        | (candidate, dependencies) :: rest ->
            if candidate == namespace
            then dependencies
            else treeEdgesFor(namespace)(rest)

let recursive treeLookupVersion (namespace: Str) (versions: List((Str, Str))) =
    match versions with
        | [] -> None
        | (candidate, version) :: rest ->
            if candidate == namespace
            then Some(version)
            else treeLookupVersion(namespace)(rest)

let versionLabel namespace versions =
    match treeLookupVersion(namespace)(versions) with
        | Some(version) -> version
        | None -> "?"

let treeConnector isLast =
    if isLast
    then "└── "
    else "├── "

let treeContinuation isLast =
    if isLast
    then "    "
    else "│   "

// Renders one child line plus, unless it closes a cycle back to an ancestor on the SAME path,
// its own descendants (mirrors stage 0's `AddLockChildren`: a namespace shared by two different
// branches is expanded in both, only a cycle along a single path is cut).
let recursive renderDependencyChild prefix ancestors namespace edges versions isLast =
    (let connector = treeConnector(isLast)
    in
        let continuation = treeContinuation(isLast)
        in
            if treeContainsNamespace(namespace)(ancestors)
            then [prefix + connector + namespace + " (cycle)"]
            else
                let line = prefix + connector + namespace + " " + versionLabel(namespace)(versions)
                in
                    let childLines = renderDependencyChildren(prefix + continuation)(namespace :: ancestors)(namespace)(edges)(versions)
                    in line :: childLines)
and renderDependencyChildrenList prefix ancestors children edges versions =
    match children with
        | [] -> []
        | namespace :: [] -> renderDependencyChild(prefix)(ancestors)(namespace)(edges)(versions)(true)
        | namespace :: rest -> append(renderDependencyChild(prefix)(ancestors)(namespace)(edges)(versions)(false))(renderDependencyChildrenList(prefix)(ancestors)(rest)(edges)(versions))
and renderDependencyChildren prefix ancestors namespace edges versions = renderDependencyChildrenList(prefix)(ancestors)(treeEdgesFor(namespace)(edges))(edges)(versions)

let recursive renderRootEntries entries edges versions =
    match entries with
        | [] -> []
        | (namespace, isPath) :: [] -> renderRootEntry(namespace)(isPath)(edges)(versions)(true)
        | (namespace, isPath) :: rest -> append(renderRootEntry(namespace)(isPath)(edges)(versions)(false))(renderRootEntries(rest)(edges)(versions))
and renderRootEntry namespace isPath edges versions isLast =
    (let connector = treeConnector(isLast)
    in
        let continuation = treeContinuation(isLast)
        in
            let suffix =
                if isPath
                then "(path)"
                else versionLabel(namespace)(versions)
            in
                let line = connector + namespace + " " + suffix
                in
                    let childLines = renderDependencyChildren(continuation)([namespace])(namespace)(edges)(versions)
                    in line :: childLines)

// The stable, testable core of `ashes tree`: given the root's own label, its direct-dependency
// root entries (namespace, isPath), and the lock file's edges/versions (both already resolved by
// the caller), renders every line of the tree, root first.
let renderDependencyTree rootLabel entries edges versions = rootLabel :: renderRootEntries(entries)(edges)(versions)

// Mirrors stage 0's `namespaceByDir`: maps each RESOLVED (direct or transitive) dependency's
// project directory to its final namespace, so a root path-dependency can be resolved to the same
// namespace the compiler itself would use, without re-deriving it.
let treeNamespaceByDirEntry resolved =
    match resolved with
        | ResolvedProjectDependency { name = _name, namespace = namespace, sourceRoots = _sourceRoots, projectDirectory = projectDirectory, entryPath = _entryPath, isDev = _isDev } -> (projectDirectory, namespace)

let treeNamespaceByDirFor resolvedDependencies = map(treeNamespaceByDirEntry)(resolvedDependencies)

let recursive treeLookupNamespaceByDir (directory: Str) (table: List((Str, Str))) =
    match table with
        | [] -> None
        | (candidateDirectory, namespace) :: rest ->
            if candidateDirectory == directory
            then Some(namespace)
            else treeLookupNamespaceByDir(directory)(rest)

// Mirrors stage 0's `DirectDependencyNamespaces`: a path dependency's namespace comes from the
// resolved graph (by matching its target directory) and is always flagged `isPath`; a registry
// dependency's namespace is always its PascalCased manifest key and is never flagged `isPath`,
// regardless of whether either resolves.
let dependencyTreeEntry style projectDirectory namespaceByDir dependency =
    match dependency with
        | ProjectDependency { name = name, source = PathDependency(path, _versionConstraint) } ->
            let dependencyDirectory = normalize(style)(join(style)(projectDirectory)(path))
            in
                match treeLookupNamespaceByDir(dependencyDirectory)(namespaceByDir) with
                    | Some(namespace) -> (namespace, true)
                    | None -> (pascalCase(name), true)
        | ProjectDependency { name = name, source = RegistryDependency(_versionConstraint) } -> (pascalCase(name), false)

// The root project's OWN `dependencies` + `devDependencies` (never transitive ones), each resolved
// to a namespace and path/registry flag — these are the tree's root entries, matching stage 0
// exactly.
let directDependencyTreeEntries style projectDirectory namespaceByDir manifest =
    match manifest with
        | ProjectManifest { entry = _entry, name = _name, namespace = _namespace, version = _version, sourceRoots = _sourceRoots, includeRoots = _includeRoots, outDir = _outDir, target = _target, defaults = _defaults, dependencies = dependencies, devDependencies = devDependencies, overrides = _overrides } -> append(map(dependencyTreeEntry(style)(projectDirectory)(namespaceByDir))(dependencies))(map(dependencyTreeEntry(style)(projectDirectory)(namespaceByDir))(devDependencies))

let lockedPackageEntry locked =
    match locked with
        | LockedPackage { namespace = namespace, version = version, source = _source, hash = _hash, dependencies = dependencies } -> (namespace, version, dependencies)

let readLockEntries style projectFilePath =
    match Ashes.IO.File.readText(lockFilePath(style)(projectFilePath)) with
        | Error(_) -> []
        | Ok(source) ->
            match parseProjectLockFile(source) with
                | Error(_) -> []
                | Ok(ProjectLockFile { version = _version, packages = packages }) -> map(lockedPackageEntry)(packages)

let lockEdgeOf entry =
    match entry with
        | (namespace, _version, dependencies) -> (namespace, dependencies)

let lockVersionOf entry =
    match entry with
        | (namespace, version, _dependencies) -> (namespace, version)

let lockEdges entries = map(lockEdgeOf)(entries)

let lockVersions entries = map(lockVersionOf)(entries)

let manifestRootLabel manifest =
    match manifest with
        | ProjectManifest { entry = _entry, name = name, namespace = _namespace, version = _version, sourceRoots = _sourceRoots, includeRoots = _includeRoots, outDir = _outDir, target = _target, defaults = _defaults, dependencies = _dependencies, devDependencies = _devDependencies, overrides = _overrides } ->
            match name with
                | Some(projectName) -> projectName
                | None -> "project"

// Resolves the project at `manifestPath`, its direct-dependency root entries, and its lock
// file's edges/versions, then renders the full tree. Fails only on a genuine project-loading
// error; a missing or unreadable lock file just yields an empty graph rather than an error,
// matching stage 0's own tolerant `ReadLockGraph`.
let runTreeInProject style manifestPath =
    match loadProject(style)(manifestPath) with
        | Error(_) -> TreeFailed("Project file not found: " + manifestPath)
        | Ok(layout) ->
            match layout with
                | ProjectLayout { projectFilePath = _projectFilePath, projectDirectory = projectDirectory, entryPath = _entryPath, entryModuleName = _entryModuleName, sourceRoots = _sourceRoots, includeRoots = _includeRoots, outDir = _outDir, manifest = manifest } ->
                    match resolveProjectDependencyGraph(style)(layout) with
                        | Error(_) -> TreeFailed("Failed to resolve the project's dependency graph.")
                        | Ok(ProjectDependencyGraph { dependencies = resolvedDependencies }) ->
                            let entries = directDependencyTreeEntries(style)(projectDirectory)(treeNamespaceByDirFor(resolvedDependencies))(manifest)
                            in
                                let lockEntries = readLockEntries(style)(manifestPath)
                                in
                                    let lines = renderDependencyTree(manifestRootLabel(manifest))(entries)(lockEdges(lockEntries))(lockVersions(lockEntries))
                                    in TreeRendered(Ashes.Text.join("\n")(lines))

// Resolves the manifest to use (an explicit `--project`, or discovery upward from the current
// directory) and runs `runTreeInProject` against it.
let runTreeWithArguments arguments =
    match arguments with
        | TreeArguments { projectOption = projectOption } ->
            let currentDirectory =
                match Ashes.IO.Environment.currentDirectory(Unit) with
                    | Ok(directory) -> directory
                    | Error(message) -> Ashes.IO.panic(message)
            in
                match selectProjectFile(Ashes.IO.Path.Unix)(currentDirectory)(projectOption) with
                    | Error(_) -> TreeFailed("No ashes.json found. Run 'ashes init' first.")
                    | Ok(None) -> TreeFailed("No ashes.json found. Run 'ashes init' first.")
                    | Ok(Some(manifestPath)) -> runTreeInProject(Ashes.IO.Path.Unix)(manifestPath)

// The full `ashes tree` entry point: parses `args`, prints stage 0's own messages, and returns the
// process exit code (0 on success, 1 a project/graph failure).
let runTree args =
    match parseTreeArguments(args) with
        | TreeHelpRequested ->
            let _ = Ashes.IO.writeLine("Usage: ashes tree [--project <manifest>]")
            in 0
        | TreeParsedArguments(arguments) ->
            match runTreeWithArguments(arguments) with
                | TreeRendered(text) ->
                    let _ = Ashes.IO.print(text)
                    in 0
                | TreeFailed(message) ->
                    let _ = Ashes.IO.writeErrorLine(message)
                    in 1
