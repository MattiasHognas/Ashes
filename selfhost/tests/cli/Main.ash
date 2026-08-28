import Ashes.Test as test
import Ashes.Collection.List.length
import AshesCompiler.Cli.Fmt
let testParseFmtArgumentsHelp unit =
    match parseFmtArguments(["--help"]) with
        | FmtHelpRequested -> test.assertEqual(true)(true)
        | _ -> test.fail("expected --help to request help")

let testParseFmtArgumentsShortHelp unit =
    match parseFmtArguments(["-h"]) with
        | FmtHelpRequested -> test.assertEqual(true)(true)
        | _ -> test.fail("expected -h to request help")

let testParseFmtArgumentsMissingPath unit =
    match parseFmtArguments([]) with
        | FmtMissingPath(message) -> test.assertEqual("Missing file or directory.")(message)
        | _ -> test.fail("expected zero arguments to be a missing-path error")

let testParseFmtArgumentsAmbiguousTargets unit =
    match parseFmtArguments(["a.ash", "b.ash"]) with
        | FmtUsageError(message) -> test.assertEqual("Provide exactly one file or directory.")(message)
        | _ -> test.fail("expected two targets to be a usage error")

let testParseFmtArgumentsWriteOnlyIsAmbiguous unit =
    match parseFmtArguments(["-w"]) with
        | FmtUsageError(message) -> test.assertEqual("Provide exactly one file or directory.")(message)
        | _ -> test.fail("expected a bare -w with no path to be a usage error")

let testParseFmtArgumentsPlainTarget unit =
    match parseFmtArguments(["examples/hello.ash"]) with
        | FmtParsedArguments(FmtArguments { writeInPlace = writeInPlace, target = target }) ->
            writeInPlace
            |> test.assertEqual(false)
            |> (given (_) -> test.assertEqual("examples/hello.ash")(target))
        | _ -> test.fail("expected a single target to parse")

let testParseFmtArgumentsWriteFlagBeforeTarget unit =
    match parseFmtArguments(["-w", "examples"]) with
        | FmtParsedArguments(FmtArguments { writeInPlace = writeInPlace, target = target }) ->
            writeInPlace
            |> test.assertEqual(true)
            |> (given (_) -> test.assertEqual("examples")(target))
        | _ -> test.fail("expected -w before the target to parse")

let testParseFmtArgumentsWriteFlagAfterTarget unit =
    match parseFmtArguments(["examples", "--write"]) with
        | FmtParsedArguments(FmtArguments { writeInPlace = writeInPlace, target = target }) ->
            writeInPlace
            |> test.assertEqual(true)
            |> (given (_) -> test.assertEqual("examples")(target))
        | _ -> test.fail("expected --write after the target to parse")

let testFormatSingleFileFormatsValidSource unit =
    match formatSingleFile("in-memory.ash")("let   x=1\nAshes.IO.print(x)\n") with
        | Ok(FormattedFile { text = text }) -> test.assertEqual("let x = 1\n\nAshes.IO.print(x)\n")(text)
        | Error(message) -> test.fail("expected formatting to succeed: " + message)

let testFormatSingleFileSkipsInlineModule unit =
    (let source = "module Inner =\n    let x = 1\nAshes.IO.print(Inner.x)\n"
    in
        match formatSingleFile("in-memory.ash")(source) with
            | Ok(FormattedFile { text = text }) -> test.assertEqual(source)(text)
            | Error(message) -> test.fail("expected an inline-module file to be left untouched: " + message))

let testFormatSingleFileReportsParseFailure unit =
    match formatSingleFile("in-memory.ash")("let x = ") with
        | Error(_) -> test.assertEqual(true)(true)
        | Ok(_) -> test.fail("expected a malformed file to fail to format")

// End-to-end tests below exercise the real filesystem, matching tests/io_directory_operations.ash's
// own fixed-scratch-directory pattern: create a small tree, run the command, assert on the result,
// then remove the tree regardless of outcome.
let scratchRoot = "cli-fmt-scratch"

let recursive removeScratch unit =
    match Ashes.IO.Directory.removeTree(scratchRoot) with
        | Ok(_) -> Unit
        | Error(message) -> test.fail("failed to clean up scratch directory: " + message)

let writeScratchFile relativePath content =
    match Ashes.IO.File.writeText(scratchRoot + "/" + relativePath)(content) with
        | Ok(_) -> Unit
        | Error(message) -> test.fail("failed to write scratch file: " + message)

let testCollectAshFilesWalksDirectoryRecursively unit =
    (let _ = removeScratch(Unit)
    in
        let _ =
            match Ashes.IO.Directory.createAll(scratchRoot + "/nested") with
                | Ok(_) -> Unit
                | Error(message) -> test.fail("failed to create scratch directory: " + message)
        in
            let _ = writeScratchFile("a.ash")("Ashes.IO.print(1)\n")
            in
                let _ = writeScratchFile("nested/b.ash")("Ashes.IO.print(2)\n")
                in
                    let _ = writeScratchFile("notes.txt")("not an ash file\n")
                    in
                        let result =
                            match collectAshFiles(scratchRoot) with
                                | Ok(files) -> test.assertEqual([scratchRoot + "/a.ash", scratchRoot + "/nested/b.ash"])(files)
                                | Error(message) -> test.fail("expected discovery to succeed: " + message)
                        in
                            let _ = removeScratch(Unit)
                            in result)

let testCollectAshFilesReportsMissingPath unit =
    (let _ = removeScratch(Unit)
    in
        match collectAshFiles(scratchRoot) with
            | Error(message) -> test.assertEqual("Path not found: " + scratchRoot)(message)
            | Ok(_) -> test.fail("expected a missing path to be an error"))

let testCollectAshFilesRejectsNonAshFile unit =
    (let _ = removeScratch(Unit)
    in
        let _ =
            match Ashes.IO.Directory.createAll(scratchRoot) with
                | Ok(_) -> Unit
                | Error(message) -> test.fail("failed to create scratch directory: " + message)
        in
            let _ = writeScratchFile("notes.txt")("not an ash file\n")
            in
                let result =
                    match collectAshFiles(scratchRoot + "/notes.txt") with
                        | Error(message) -> test.assertEqual("Input file must be .ash")(message)
                        | Ok(_) -> test.fail("expected a non-.ash file target to be rejected")
                in
                    let _ = removeScratch(Unit)
                    in result)

let testRunFmtWritesInPlaceOnlyWhenChanged unit =
    (let _ = removeScratch(Unit)
    in
        let _ =
            match Ashes.IO.Directory.createAll(scratchRoot) with
                | Ok(_) -> Unit
                | Error(message) -> test.fail("failed to create scratch directory: " + message)
        in
            let _ = writeScratchFile("messy.ash")("let   x=1\nAshes.IO.print(x)\n")
            in
                let exitCode = runFmt([scratchRoot + "/messy.ash", "-w"])
                in
                    let result =
                        exitCode
                        |> test.assertEqual(0)
                        |> (given (_) ->
                            match Ashes.IO.File.readText(scratchRoot + "/messy.ash") with
                                | Ok(text) -> test.assertEqual("let x = 1\n\nAshes.IO.print(x)\n")(text)
                                | Error(message) -> test.fail("expected the rewritten file to be readable: " + message))
                    in
                        let _ = removeScratch(Unit)
                        in result)

let testRunFmtOnEmptyDirectorySucceeds unit =
    (let _ = removeScratch(Unit)
    in
        let _ =
            match Ashes.IO.Directory.createAll(scratchRoot) with
                | Ok(_) -> Unit
                | Error(message) -> test.fail("failed to create scratch directory: " + message)
        in
            let result =
                [scratchRoot]
                |> runFmt
                |> test.assertEqual(0)
            in
                let _ = removeScratch(Unit)
                in result)

let testRunFmtReportsUsageErrorExitCode unit =
    []
    |> runFmt
    |> test.assertEqual(1)

let run unit =
    Unit
    |> testParseFmtArgumentsHelp
    |> testParseFmtArgumentsShortHelp
    |> testParseFmtArgumentsMissingPath
    |> testParseFmtArgumentsAmbiguousTargets
    |> testParseFmtArgumentsWriteOnlyIsAmbiguous
    |> testParseFmtArgumentsPlainTarget
    |> testParseFmtArgumentsWriteFlagBeforeTarget
    |> testParseFmtArgumentsWriteFlagAfterTarget
    |> testFormatSingleFileFormatsValidSource
    |> testFormatSingleFileSkipsInlineModule
    |> testFormatSingleFileReportsParseFailure
    |> testCollectAshFilesWalksDirectoryRecursively
    |> testCollectAshFilesReportsMissingPath
    |> testCollectAshFilesRejectsNonAshFile
    |> testRunFmtWritesInPlaceOnlyWhenChanged
    |> testRunFmtOnEmptyDirectorySucceeds
    |> testRunFmtReportsUsageErrorExitCode
    |> (given (_) -> Ashes.IO.print("all self-hosted cli fmt tests passed"))

run(Unit)
