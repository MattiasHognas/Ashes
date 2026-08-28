// The `ashes add` command: add a dependency to the selected project manifest, or to the nearest
// `ashes.json` when `--project` is omitted.
//
// Invariants:
// - Matches stage 0's observable contract (docs/md/reference/cli.md#ashes-add): a `--path <dir>`
//   dependency writes `{"path": "<dir>"}`, otherwise a registry dependency writes the literal
//   SemVer string `"*"`; `--dev` targets `devDependencies` instead of `dependencies`; an existing
//   entry for the same package is overwritten in place, every other field of the manifest
//   (including the OTHER dependency field) is preserved untouched.
// - Works on the manifest's raw JSON (`Ashes.Text.Json`), not the typed `ProjectManifest` model,
//   so unknown/forward-compatible fields survive the round trip exactly like stage 0's own
//   `Dictionary<string, object?>` rebuild in `RunAdd`/`ReadProjectJson`/`WriteProjectJson`
//   (`src/Ashes.Cli/Program.cs`) — but by updating the parsed key/value list in place rather than
//   stripping and re-appending a field, this port does NOT reproduce stage 0's incidental quirk of
//   relocating `dependencies`/`devDependencies` to the end of the object on every write (see
//   `docs/md/future/SELF_HOSTING.md`'s `add`/`remove` entry for why that's a deliberate deviation,
//   not a gap).
// - Re-serializes with a private 2-space indented writer matching `System.Text.Json`'s
//   `WriteIndented = true` default (used by stage 0's `WriteProjectJson`): compact `{}`/`[]` for
//   empty collections, one member per line otherwise. Deliberately deferred: `System.Text.Json`'s
//   default encoder additionally escapes HTML-sensitive characters and non-ASCII as `\uXXXX`; this
//   port only escapes `"`, `\`, and the C0 control codes `Ashes.Text.Json.escStr` already handles,
//   matching that shared stdlib helper rather than stage 0's stricter default encoder.

import Ashes.Collection.List.append
import Ashes.IO.Path
import AshesCompiler.Semantics.ProjectDiscovery
import Ashes.Text.Json
export (
    type AddArguments(..),
    type AddParse(..),
    type AddOutcome(..),
    value parseAddArguments,
    value dependencyValue,
    value setJsonObjectField,
    value addPackageToManifest,
    value stringifyIndented,
    value runAddInProject,
    value runAddWithArguments,
    value runAdd,
)

type alias AddJson = Json(Bool, Int, Float, Str)

type AddArguments =
    | packageName: Str
    | pathOption: Maybe(Str)
    | isDev: Bool
    | projectOption: Maybe(Str)

type AddParse =
    | AddHelpRequested
    | AddMissingPackageName
    | AddParsedArguments(AddArguments)

type AddOutcome =
    | AddSucceeded(Str, Str)
    | AddFailed(Str)

let recursive collectAddArgs args positionals pathOption isDev projectOption =
    match args with
        | [] -> (positionals, pathOption, isDev, projectOption)
        | "--path" :: value :: rest -> collectAddArgs(rest)(positionals)(Some(value))(isDev)(projectOption)
        | "--dev" :: rest -> collectAddArgs(rest)(positionals)(pathOption)(true)(projectOption)
        | "--project" :: value :: rest -> collectAddArgs(rest)(positionals)(pathOption)(isDev)(Some(value))
        | other :: rest -> collectAddArgs(rest)(append(positionals)([other]))(pathOption)(isDev)(projectOption)

// Mirrors stage 0's `RunAdd` (`src/Ashes.Cli/Program.cs`): a bare `--help`/`-h` short-circuits;
// otherwise the first positional argument is the package name (used verbatim, never PascalCased —
// unlike `why`'s namespace target), and `--path`/`--dev`/`--project` are optional and order-
// independent. No positional argument at all is a user error, not a usage error (stage 0 throws
// `CliUserException`, exit code 1).
let parseAddArguments args =
    match args with
        | "--help" :: [] -> AddHelpRequested
        | "-h" :: [] -> AddHelpRequested
        | _ ->
            match collectAddArgs(args)([])(None)(false)(None) with
                | ([], _, _, _) -> AddMissingPackageName
                | (packageName :: _rest, pathOption, isDev, projectOption) -> AddParsedArguments(AddArguments(packageName = packageName, pathOption = pathOption, isDev = isDev, projectOption = projectOption))

let recursive normalizePathSeparators (path: Str) = Ashes.Text.join("/")(Ashes.Text.split(path)("\\"))

// A `--path` dependency writes an object value; a registry dependency writes a SemVer string
// (default `*`), matching stage 0's `RunAdd` exactly.
let dependencyValue pathOption =
    match pathOption with
        | Some(path) -> JsonObject("path")(JsonStr(normalizePathSeparators(path)))(JsonObjectEnd)
        | None -> JsonStr("*")

let fieldNameFor isDev =
    if isDev
    then "devDependencies"
    else "dependencies"

let isJsonObjectShaped (json: AddJson) =
    match json with
        | JsonObject(_, _, _) -> true
        | JsonObjectEnd -> true
        | _ -> false

let recursive getObjectField (key: Str) (obj: AddJson) =
    match obj with
        | JsonObjectEnd -> None
        | JsonObject(k, v, rest) ->
            if k == key
            then Some(v)
            else getObjectField(key)(rest)
        | _ -> None

// Sets `key` to `value` in `obj`, updating an existing entry in place (preserving its original
// position) or appending a new one at the end. The stable, testable core of both `add` and
// (for removal, via a `Maybe` result) `remove`.
let recursive setJsonObjectField (key: Str) (value: AddJson) (obj: AddJson) =
    match obj with
        | JsonObjectEnd -> JsonObject(key)(value)(JsonObjectEnd)
        | JsonObject(k, v, rest) ->
            if k == key
            then JsonObject(k)(value)(rest)
            else JsonObject(k)(v)(setJsonObjectField(key)(value)(rest))
        | other -> other

// Mirrors stage 0's tolerant handling of a malformed existing dependency field
// (`TryGetProperty(...) && existingDeps.ValueKind == JsonValueKind.Object`): a non-object value
// under `fieldName` is treated as though the field were absent, rather than propagated or crashed
// on.
let asJsonObjectOrEmpty (json: AddJson) =
    if isJsonObjectShaped(json)
    then json
    else JsonObjectEnd

// Adds (or overwrites) `packageName` -> `value` inside `root`'s `fieldName` object, creating that
// object if it doesn't exist yet. Every other field of `root`, and every other package already
// inside `fieldName`, is left exactly as it was.
let addPackageToManifest fieldName packageName value root =
    (let existingField =
        match getObjectField(fieldName)(root) with
            | Some(existing) -> asJsonObjectOrEmpty(existing)
            | None -> JsonObjectEnd
    in
        let updatedField = setJsonObjectField(packageName)(value)(existingField)
        in setJsonObjectField(fieldName)(updatedField)(root))

let recursive indentString depth =
    if depth <= 0
    then ""
    else "  " + indentString(depth - 1)

// A private 2-space pretty-printer over `Ashes.Text.Json`'s `Json` value, matching
// `System.Text.Json`'s `WriteIndented = true` default shape (compact `{}`/`[]`, one member per
// line otherwise) well enough for a manifest file — see this file's header comment for the
// deliberately deferred escaping differences.
let recursive stringifyIndented depth json =
    match json with
        | JsonNull -> "null"
        | JsonBool(b) ->
            if b
            then "true"
            else "false"
        | JsonInt(n) -> Ashes.Text.fromInt(n)
        | JsonFloat(f) -> Ashes.Text.fromFloat(f)
        | JsonStr(s) -> "\"" + escStr("")(s) + "\""
        | JsonArrayEnd -> "[]"
        | JsonArray(elem, rest) -> "[\n" + stringifyArrayItems(depth + 1)(elem)(rest) + "\n" + indentString(depth) + "]"
        | JsonObjectEnd -> "{}"
        | JsonObject(key, value, rest) -> "{\n" + stringifyObjectItems(depth + 1)(key)(value)(rest) + "\n" + indentString(depth) + "}"
and stringifyArrayItems depth elem rest =
    (let itemText = indentString(depth) + stringifyIndented(depth)(elem)
    in
        match rest with
            | JsonArrayEnd -> itemText
            | JsonArray(nextElem, nextRest) -> itemText + ",\n" + stringifyArrayItems(depth)(nextElem)(nextRest)
            | _ -> itemText)
and stringifyObjectItems depth key value rest =
    (let itemText = indentString(depth) + "\"" + escStr("")(key) + "\": " + stringifyIndented(depth)(value)
    in
        match rest with
            | JsonObjectEnd -> itemText
            | JsonObject(nextKey, nextValue, nextRest) -> itemText + ",\n" + stringifyObjectItems(depth)(nextKey)(nextValue)(nextRest)
            | _ -> itemText)

// Resolves the manifest at `manifestPath`'s raw JSON, adds `packageName` to the requested
// dependency field, and writes the result back. Fails only on a missing/malformed manifest or a
// write error — the actual mutation (`addPackageToManifest`) cannot fail.
let runAddInProject manifestPath packageName pathOption isDev =
    match Ashes.IO.File.readText(manifestPath) with
        | Error(_) -> AddFailed("Project file not found: " + manifestPath)
        | Ok(source) ->
            match parse(source) with
                | Error(message) -> AddFailed("Invalid ashes.json (" + manifestPath + "): " + message)
                | Ok(root) ->
                    if isJsonObjectShaped(root)
                    then
                        let fieldName = fieldNameFor(isDev)
                        in
                            let updated = addPackageToManifest(fieldName)(packageName)(dependencyValue(pathOption))(root)
                            in
                                match Ashes.IO.File.writeText(manifestPath)(stringifyIndented(0)(updated) + "\n") with
                                    | Ok(_) -> AddSucceeded(packageName)(fieldName)
                                    | Error(message) -> AddFailed("Failed to write " + manifestPath + ": " + message)
                    else AddFailed("Invalid ashes.json (" + manifestPath + "): root must be a JSON object.")

// Resolves the manifest to use (an explicit `--project`, or discovery upward from the current
// directory) and runs `runAddInProject` against it.
let runAddWithArguments arguments =
    match arguments with
        | AddArguments { packageName = packageName, pathOption = pathOption, isDev = isDev, projectOption = projectOption } ->
            let currentDirectory =
                match Ashes.IO.Environment.currentDirectory(Unit) with
                    | Ok(directory) -> directory
                    | Error(message) -> Ashes.IO.panic(message)
            in
                match selectProjectFile(Ashes.IO.Path.Unix)(currentDirectory)(projectOption) with
                    | Error(_) -> AddFailed("No ashes.json found. Run 'ashes init' first.")
                    | Ok(None) -> AddFailed("No ashes.json found. Run 'ashes init' first.")
                    | Ok(Some(manifestPath)) -> runAddInProject(manifestPath)(packageName)(pathOption)(isDev)

// The full `ashes add` entry point: parses `args`, prints stage 0's own messages, and returns the
// process exit code (0 on success, 1 a missing package name or a project/manifest failure).
let runAdd args =
    match parseAddArguments(args) with
        | AddHelpRequested ->
            let _ = Ashes.IO.writeLine("Usage: ashes add <package> [--project <manifest>] [--path <dir>] [--dev]")
            in 0
        | AddMissingPackageName ->
            let _ = Ashes.IO.writeErrorLine("Missing package name.")
            in 1
        | AddParsedArguments(arguments) ->
            match runAddWithArguments(arguments) with
                | AddSucceeded(packageName, field) ->
                    let _ = Ashes.IO.print("Added " + packageName + " to " + field + ".")
                    in 0
                | AddFailed(message) ->
                    let _ = Ashes.IO.writeErrorLine(message)
                    in 1
