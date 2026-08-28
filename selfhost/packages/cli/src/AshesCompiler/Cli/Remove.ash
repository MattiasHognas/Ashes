// The `ashes remove` command: remove a dependency from the selected project manifest, or from the
// nearest `ashes.json` when `--project` is omitted.
//
// Invariants:
// - Matches stage 0's observable contract (docs/md/reference/cli.md#ashes-remove): removes
//   `packageName` from BOTH `dependencies` and `devDependencies` (whichever it's actually in —
//   stage 0's `RunRemove` checks both, not just `dependencies`, despite the CLI reference's prose
//   only mentioning `dependencies`), failing with "Package '<name>' is not a dependency." when it
//   was in neither. Either field is omitted entirely from the written manifest once it becomes
//   empty, matching stage 0's `RunRemove` exactly for both fields.
// - Shares `AshesCompiler.Cli.Add`'s raw-JSON model and its private indented writer
//   (`stringifyIndented`) rather than duplicating a second copy of that pretty-printer — unlike
//   `why`/`tree`'s small linear-search helpers, which stayed independently duplicated per file,
//   this is real serialization logic the two commands must agree on to keep manifest formatting
//   consistent across `add` and `remove`.

import Ashes.Collection.List.append
import Ashes.IO.Path
import AshesCompiler.Cli.Add
import AshesCompiler.Semantics.ProjectDiscovery
import Ashes.Text.Json
export (
    type RemoveArguments(..),
    type RemoveParse(..),
    type RemoveOutcome(..),
    value parseRemoveArguments,
    value removeObjectField,
    value removePackageFromManifest,
    value runRemoveInProject,
    value runRemoveWithArguments,
    value runRemove,
)

type alias RemoveJson = Json(Bool, Int, Float, Str)

type RemoveArguments =
    | packageName: Str
    | projectOption: Maybe(Str)

type RemoveParse =
    | RemoveHelpRequested
    | RemoveMissingPackageName
    | RemoveParsedArguments(RemoveArguments)

type RemoveOutcome =
    | RemoveSucceeded(Str)
    | RemoveNotADependency(Str)
    | RemoveFailed(Str)

let recursive collectRemoveArgs args positionals projectOption =
    match args with
        | [] -> (positionals, projectOption)
        | "--project" :: value :: rest -> collectRemoveArgs(rest)(positionals)(Some(value))
        | other :: rest -> collectRemoveArgs(rest)(append(positionals)([other]))(projectOption)

// Mirrors stage 0's `RunRemove` (`src/Ashes.Cli/Program.cs`): a bare `--help`/`-h` short-circuits;
// otherwise the first positional argument is the package name and `--project <manifest>` is
// optional. No positional argument at all is a user error, not a usage error (stage 0 throws
// `CliUserException`, exit code 1).
let parseRemoveArguments args =
    match args with
        | "--help" :: [] -> RemoveHelpRequested
        | "-h" :: [] -> RemoveHelpRequested
        | _ ->
            match collectRemoveArgs(args)([])(None) with
                | ([], _) -> RemoveMissingPackageName
                | (packageName :: _rest, projectOption) -> RemoveParsedArguments(RemoveArguments(packageName = packageName, projectOption = projectOption))

let recursive getObjectField (key: Str) (obj: RemoveJson) =
    match obj with
        | JsonObjectEnd -> None
        | JsonObject(k, v, rest) ->
            if k == key
            then Some(v)
            else getObjectField(key)(rest)
        | _ -> None

let isJsonObjectShaped (json: RemoveJson) =
    match json with
        | JsonObject(_, _, _) -> true
        | JsonObjectEnd -> true
        | _ -> false

let asJsonObjectOrEmpty (json: RemoveJson) =
    if isJsonObjectShaped(json)
    then json
    else JsonObjectEnd

let isEmptyObject (json: RemoveJson) =
    match json with
        | JsonObjectEnd -> true
        | _ -> false

// Removes `key` from `obj` if present, returning the updated object alongside whether it was
// found. Every other entry keeps its original position.
let recursive removeObjectField (key: Str) (obj: RemoveJson) =
    match obj with
        | JsonObjectEnd -> (JsonObjectEnd, false)
        | JsonObject(k, v, rest) ->
            if k == key
            then (rest, true)
            else
                match removeObjectField(key)(rest) with
                    | (updatedRest, removed) -> (JsonObject(k)(v)(updatedRest), removed)
        | other -> (other, false)

// Drops `fieldName` from `root` entirely once its own value has become an empty object, matching
// stage 0's own field-omission rule for both `dependencies` and `devDependencies`.
let dropFieldIfEmpty fieldName root =
    match getObjectField(fieldName)(root) with
        | Some(fieldValue) ->
            if isEmptyObject(asJsonObjectOrEmpty(fieldValue))
            then
                match removeObjectField(fieldName)(root) with
                    | (updatedRoot, _removed) -> updatedRoot
            else root
        | None -> root

// Removes `packageName` from `root`'s `fieldName` object if it's there, dropping `fieldName`
// itself when that empties it out. Returns the updated root alongside whether anything changed.
let removePackageFromField fieldName packageName root =
    match getObjectField(fieldName)(root) with
        | Some(fieldValue) ->
            match removeObjectField(packageName)(asJsonObjectOrEmpty(fieldValue)) with
                | (updatedField, removed) ->
                    if removed
                    then (dropFieldIfEmpty(fieldName)(setJsonObjectField(fieldName)(updatedField)(root)), true)
                    else (root, false)
        | None -> (root, false)

// The stable, testable core of `ashes remove`: removes `packageName` from BOTH `dependencies` and
// `devDependencies` (mirroring stage 0's own check of both fields), returning the updated
// manifest and whether the package was found in either.
let removePackageFromManifest packageName root =
    match removePackageFromField("dependencies")(packageName)(root) with
        | (rootAfterDependencies, removedFromDependencies) ->
            match removePackageFromField("devDependencies")(packageName)(rootAfterDependencies) with
                | (rootAfterDevDependencies, removedFromDevDependencies) ->
                    if removedFromDependencies
                    then (rootAfterDevDependencies, true)
                    else (rootAfterDevDependencies, removedFromDevDependencies)

// Resolves the project at `manifestPath`'s raw JSON, removes `packageName`, and writes the result
// back. Fails only on a missing/malformed manifest, the package not being a dependency, or a
// write error.
let runRemoveInProject manifestPath packageName =
    match Ashes.IO.File.readText(manifestPath) with
        | Error(_) -> RemoveFailed("Project file not found: " + manifestPath)
        | Ok(source) ->
            match parse(source) with
                | Error(message) -> RemoveFailed("Invalid ashes.json (" + manifestPath + "): " + message)
                | Ok(root) ->
                    if isJsonObjectShaped(root)
                    then
                        match removePackageFromManifest(packageName)(root) with
                            | (_unchangedRoot, false) -> RemoveNotADependency(packageName)
                            | (updatedRoot, true) ->
                                match Ashes.IO.File.writeText(manifestPath)(stringifyIndented(0)(updatedRoot) + "\n") with
                                    | Ok(_) -> RemoveSucceeded(packageName)
                                    | Error(message) -> RemoveFailed("Failed to write " + manifestPath + ": " + message)
                    else RemoveFailed("Invalid ashes.json (" + manifestPath + "): root must be a JSON object.")

// Resolves the manifest to use (an explicit `--project`, or discovery upward from the current
// directory) and runs `runRemoveInProject` against it.
let runRemoveWithArguments arguments =
    match arguments with
        | RemoveArguments { packageName = packageName, projectOption = projectOption } ->
            let currentDirectory =
                match Ashes.IO.Environment.currentDirectory(Unit) with
                    | Ok(directory) -> directory
                    | Error(message) -> Ashes.IO.panic(message)
            in
                match selectProjectFile(Ashes.IO.Path.Unix)(currentDirectory)(projectOption) with
                    | Error(_) -> RemoveFailed("No ashes.json found. Run 'ashes init' first.")
                    | Ok(None) -> RemoveFailed("No ashes.json found. Run 'ashes init' first.")
                    | Ok(Some(manifestPath)) -> runRemoveInProject(manifestPath)(packageName)

// The full `ashes remove` entry point: parses `args`, prints stage 0's own messages, and returns
// the process exit code (0 on success, 1 a missing package name, a package that isn't a
// dependency, or a project/manifest failure).
let runRemove args =
    match parseRemoveArguments(args) with
        | RemoveHelpRequested ->
            let _ = Ashes.IO.writeLine("Usage: ashes remove <package> [--project <manifest>]")
            in 0
        | RemoveMissingPackageName ->
            let _ = Ashes.IO.writeErrorLine("Missing package name.")
            in 1
        | RemoveParsedArguments(arguments) ->
            match runRemoveWithArguments(arguments) with
                | RemoveSucceeded(packageName) ->
                    let _ = Ashes.IO.print("Removed " + packageName + ".")
                    in 0
                | RemoveNotADependency(packageName) ->
                    let _ = Ashes.IO.writeErrorLine("Package '" + packageName + "' is not a dependency.")
                    in 1
                | RemoveFailed(message) ->
                    let _ = Ashes.IO.writeErrorLine(message)
                    in 1
