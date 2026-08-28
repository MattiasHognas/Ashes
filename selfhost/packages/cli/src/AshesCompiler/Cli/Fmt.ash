// The `ashes fmt` command: format one file or every `.ash` file under a directory, either
// previewing to stdout or rewriting in place.
//
// Invariants:
// - Matches stage 0's observable contract (docs/md/reference/cli.md#ashes-fmt): discovery,
//   preview/write modes, malformed-file handling, and exit codes 0/1/2.
// - A file containing an inline `module Name = ...` block is left untouched (the formatter has
//   no AST node for that compile-time stitching construct), matching stage 0's own carve-out.
// - Deliberately narrower than stage 0 for now: no `.editorconfig` resolution (always the
//   4-space/`\n` defaults `formatSource` already applies) and no elapsed-time in the write-mode
//   summary (no monotonic clock capability is shipped yet). Both are noted as open follow-ups.

import Ashes.Collection.List.append
import Ashes.Collection.List.length
import Ashes.Collection.List.sort as sortList
import Ashes.IO.Path
import AshesCompiler.Frontend.InlineModules.containsInlineModule
import AshesCompiler.Frontend.Token.DiagnosticEntry
import AshesCompiler.Formatter.SourceFormatting
export (
    type FmtArguments(..),
    type FmtParse(..),
    type FmtOutcome(..),
    type FormattedFile(..),
    value parseFmtArguments,
    value collectAshFiles,
    value formatSingleFile,
    value runFmtWithArguments,
    value runFmt,
)

type FmtArguments =
    | writeInPlace: Bool
    | target: Str

type FmtParse =
    | FmtHelpRequested
    | FmtMissingPath(Str)
    | FmtUsageError(Str)
    | FmtParsedArguments(FmtArguments)

type FmtOutcome =
    | FmtSucceeded(Int)
    | FmtFailed(Str)

let recursive partitionFmtFlags args writeInPlace targets =
    match args with
        | [] -> (writeInPlace, targets)
        | "-w" :: rest -> partitionFmtFlags(rest)(true)(targets)
        | "--write" :: rest -> partitionFmtFlags(rest)(true)(targets)
        | other :: rest -> partitionFmtFlags(rest)(writeInPlace)(append(targets)([other]))

// Mirrors stage 0's `ParseFmtArguments` (`src/Ashes.Cli/Program.cs`): a bare `--help`/`-h`
// short-circuits before any other parsing; zero arguments overall is a user error (exit 1, a
// missing path is not itself an ambiguous invocation); anything other than exactly one
// non-flag target after removing `-w`/`--write` is a usage error (exit 2).
let parseFmtArguments args =
    match args with
        | "--help" :: [] -> FmtHelpRequested
        | "-h" :: [] -> FmtHelpRequested
        | [] -> FmtMissingPath("Missing file or directory.")
        | _ ->
            match partitionFmtFlags(args)(false)([]) with
                | (writeInPlace, target :: []) -> FmtParsedArguments(FmtArguments(writeInPlace = writeInPlace, target = target))
                | (_, _) -> FmtUsageError("Provide exactly one file or directory.")

let hasAshExtension path = Ashes.Text.asciiLower(Ashes.IO.Path.extension(Ashes.IO.Path.Unix)(path)) == ".ash"

// `Ashes.IO.Directory.entries` errors on any non-directory path (a plain file, a missing path, or
// a symlink), so attempting it on a known-existing child is this stdlib's only available "is this
// a directory" signal — there is no dedicated `isDirectory`/`stat` capability yet.
let recursive collectAshFilesFromDirectory dir names =
    match names with
        | [] -> Ok([])
        | name :: rest ->
            let fullPath = Ashes.IO.Path.join(Ashes.IO.Path.Unix)(dir)(name)
            in
                match Ashes.IO.Directory.entries(fullPath) with
                    | Ok(nestedNames) ->
                        match collectAshFilesFromDirectory(fullPath)(nestedNames) with
                            | Error(message) -> Error(message)
                            | Ok(nested) ->
                                match collectAshFilesFromDirectory(dir)(rest) with
                                    | Error(message) -> Error(message)
                                    | Ok(remaining) -> Ok(append(nested)(remaining))
                    | Error(_) ->
                        match collectAshFilesFromDirectory(dir)(rest) with
                            | Error(message) -> Error(message)
                            | Ok(remaining) ->
                                if hasAshExtension(fullPath)
                                then Ok(fullPath :: remaining)
                                else Ok(remaining)

// Resolves the target to a sorted, deterministic file list. Mirrors stage 0's file/directory
// branches in `ParseFmtArguments` (a file must end `.ash`; a directory is walked recursively for
// every `.ash` file) plus its "path not found" case, folded into one `Result` here since the
// stdlib's directory/file probes are themselves fallible.
let collectAshFiles path =
    match Ashes.IO.File.exists(path) with
        | Error(message) -> Error(message)
        | Ok(false) -> Error("Path not found: " + path)
        | Ok(true) ->
            match Ashes.IO.Directory.entries(path) with
                | Ok(names) ->
                    match collectAshFilesFromDirectory(path)(names) with
                        | Error(message) -> Error(message)
                        | Ok(files) -> Ok(sortList(files))
                | Error(_) ->
                    if hasAshExtension(path)
                    then Ok([path])
                    else Error("Input file must be .ash")

type FormattedFile =
    | path: Str
    | original: Str
    | text: Str

let recursive renderDiagnosticsGo diagnostics rendered =
    match diagnostics with
        | [] -> rendered
        | DiagnosticEntry { message = message } :: rest ->
            renderDiagnosticsGo(rest)(if rendered == ""
            then message
            else rendered + "; " + message)

let renderDiagnostics diagnostics = renderDiagnosticsGo(diagnostics)("")

// Formats one file's already-read source, or leaves it untouched when it contains an inline
// `module` block (matching stage 0's `FormatSingleFileAsync` carve-out).
let formatSingleFile path source =
    if containsInlineModule(source)
    then Ok(FormattedFile(path = path, original = source, text = source))
    else
        match formatSource(source) with
            | Ok(formatted) -> Ok(FormattedFile(path = path, original = source, text = formatted))
            | Error(InvalidImportLine(lineNumber, line)) -> Error(path + ":" + Ashes.Text.fromInt(lineNumber) + " invalid import line: " + line)
            | Error(SourceParseFailure(diagnostics)) -> Error(path + ": " + renderDiagnostics(diagnostics))

let readAndFormat path =
    match Ashes.IO.File.readText(path) with
        | Error(message) -> Error(message)
        | Ok(source) -> formatSingleFile(path)(source)

let recursive readAndFormatAll paths formatted =
    match paths with
        | [] -> Ok(formatted)
        | path :: rest ->
            match readAndFormat(path) with
                | Error(message) -> Error(message)
                | Ok(result) -> readAndFormatAll(rest)(append(formatted)([result]))

let recursive writeFormattedFiles files =
    match files with
        | [] -> Ok(Unit)
        | FormattedFile { path = path, original = original, text = text } :: rest ->
            if original == text
            then writeFormattedFiles(rest)
            else
                match Ashes.IO.File.writeText(path)(text) with
                    | Error(message) -> Error(message)
                    | Ok(_) -> writeFormattedFiles(rest)

let separatorRule path = "// ---- " + path + " ----"

let recursive previewFormattedFiles files multiple =
    match files with
        | [] -> Unit
        | FormattedFile { path = path, text = text } :: rest ->
            let _ =
                if multiple
                then Ashes.IO.writeLine(separatorRule(path))
                else Unit
            in
                let _ = Ashes.IO.write(text)
                in previewFormattedFiles(rest)(multiple)

// The stable, testable core of `ashes fmt`: given already-parsed arguments, discovers the file
// list, formats each one, and either previews (stdout) or rewrites in place — returning the
// process exit code stage 0 documents (0 success, 1 a discovery/format/write failure) without
// itself touching `Ashes.IO.args`/`Ashes.IO.exit`, so tests can drive it directly.
let runFmtWithArguments arguments =
    match arguments with
        | FmtArguments { writeInPlace = writeInPlace, target = target } ->
            match collectAshFiles(target) with
                | Error(message) -> FmtFailed(message)
                | Ok([]) ->
                    let _ = Ashes.IO.print("No .ash files found.")
                    in FmtSucceeded(0)
                | Ok(files) ->
                    match readAndFormatAll(files)([]) with
                        | Error(message) -> FmtFailed(message)
                        | Ok(formatted) ->
                            if writeInPlace
                            then
                                match writeFormattedFiles(formatted) with
                                    | Error(message) -> FmtFailed(message)
                                    | Ok(_) ->
                                        let _ = Ashes.IO.print("OK Formatted " + Ashes.Text.fromInt(length(files)) + " file(s).")
                                        in FmtSucceeded(0)
                            else
                                let _ = previewFormattedFiles(formatted)(length(files) > 1)
                                in FmtSucceeded(0)

// The full `ashes fmt` entry point: parses `args`, prints stage 0's own messages for the help,
// usage-error, empty-selection, and failure cases, and returns the process exit code.
let runFmt args =
    match parseFmtArguments(args) with
        | FmtHelpRequested ->
            let _ = Ashes.IO.writeLine("Usage: ashes fmt <file|dir> [-w]")
            in 0
        | FmtMissingPath(message) ->
            let _ = Ashes.IO.writeErrorLine(message)
            in 1
        | FmtUsageError(message) ->
            let _ = Ashes.IO.writeErrorLine(message)
            in 2
        | FmtParsedArguments(arguments) ->
            match runFmtWithArguments(arguments) with
                | FmtSucceeded(code) -> code
                | FmtFailed(message) ->
                    let _ = Ashes.IO.writeErrorLine(message)
                    in 1
