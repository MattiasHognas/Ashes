// The `ashes init` command: scaffold a new project's `ashes.json` manifest and starter
// `src/Main.ash` entry file in the current directory.
//
// Invariants:
// - Matches stage 0's observable contract (docs/md/reference/cli.md#ashes-init) byte-for-byte:
//   the exact `ashes.json` text (2-space indent, trailing newline) and hello-world source.
// - Never overwrites an existing `ashes.json` (fails) or an existing `src/Main.ash` (silently
//   left alone, matching stage 0).

import Ashes.IO.Path
export (
    type InitParse(..),
    type InitOutcome(..),
    value parseInitArguments,
    value initProjectJson,
    value runInitInDirectory,
    value runInit,
)

type InitParse =
    | InitHelpRequested
    | InitUsageError(Str)
    | InitParsedArguments

type InitOutcome =
    | InitSucceeded
    | InitFailed(Str)

// Mirrors stage 0's `RunInit` (`src/Ashes.Cli/Program.cs`): `init` takes no arguments at all
// other than a bare `--help`/`-h`; anything else is a usage error.
let parseInitArguments args =
    match args with
        | [] -> InitParsedArguments
        | "--help" :: [] -> InitHelpRequested
        | "-h" :: [] -> InitHelpRequested
        | _ -> InitUsageError("Unknown argument.")
// The exact `ashes.json` text stage 0 writes: two-space indent, `sourceRoots` as a one-element
// array with its own lines, and a trailing newline after the closing brace.

let initProjectJson projectName = "{\n  \"name\": \"" + projectName + "\",\n  \"entry\": \"src/Main.ash\",\n  \"sourceRoots\": [\n    \"src\"\n  ]\n}\n"

let helloWorldSource = "Ashes.IO.print(\"hello, ashes!\")\n"

let writeMainIfAbsent mainPath =
    match Ashes.IO.File.exists(mainPath) with
        | Error(message) -> InitFailed(message)
        | Ok(true) -> InitSucceeded
        | Ok(false) ->
            match Ashes.IO.File.writeText(mainPath)(helloWorldSource) with
                | Error(message) -> InitFailed(message)
                | Ok(_) ->
                    let _ = Ashes.IO.print("Created src/Main.ash")
                    in InitSucceeded

let ensureSourceTree srcDir =
    match Ashes.IO.Directory.createAll(srcDir) with
        | Error(message) -> InitFailed(message)
        | Ok(_) ->
            "Main.ash"
            |> Ashes.IO.Path.join(Ashes.IO.Path.Unix)(srcDir)
            |> writeMainIfAbsent

let writeManifestAndSource directory =
    (let projectName = Ashes.IO.Path.basename(Ashes.IO.Path.Unix)(directory)
    in
        match projectName
        |> initProjectJson
        |> Ashes.IO.File.writeText(Ashes.IO.Path.join(Ashes.IO.Path.Unix)(directory)("ashes.json")) with
            | Error(message) -> InitFailed(message)
            | Ok(_) ->
                let _ = Ashes.IO.print("Created ashes.json")
                in
                    "src"
                    |> Ashes.IO.Path.join(Ashes.IO.Path.Unix)(directory)
                    |> ensureSourceTree)

// The stable, testable core of `ashes init`: given the directory to scaffold (so tests can point
// it at a scratch directory instead of the real process CWD), creates `ashes.json` and
// `src/Main.ash` there. Fails without writing anything if `ashes.json` already exists; leaves an
// existing `src/Main.ash` untouched otherwise.
let runInitInDirectory directory =
    match "ashes.json"
    |> Ashes.IO.Path.join(Ashes.IO.Path.Unix)(directory)
    |> Ashes.IO.File.exists with
        | Error(message) -> InitFailed(message)
        | Ok(true) -> InitFailed("ashes.json already exists in this directory.")
        | Ok(false) -> writeManifestAndSource(directory)

let unwrapCurrentDirectory result =
    match result with
        | Ok(directory) -> directory
        | Error(message) -> Ashes.IO.panic(message)

// The full `ashes init` entry point: parses `args`, prints stage 0's own usage/failure messages,
// and returns the process exit code (0 success or help, 1 an existing manifest or I/O error, 2
// usage).
let runInit args =
    match parseInitArguments(args) with
        | InitHelpRequested ->
            let _ = Ashes.IO.writeLine("Usage: ashes init")
            in 0
        | InitUsageError(message) ->
            let _ = Ashes.IO.writeErrorLine(message)
            in 2
        | InitParsedArguments ->
            match Unit
            |> Ashes.IO.Environment.currentDirectory
            |> unwrapCurrentDirectory
            |> runInitInDirectory with
                | InitSucceeded -> 0
                | InitFailed(message) ->
                    let _ = Ashes.IO.writeErrorLine(message)
                    in 1
