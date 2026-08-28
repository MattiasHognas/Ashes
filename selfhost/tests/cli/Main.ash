import Ashes.Test as test
import Ashes.Collection.List.length
import Ashes.IO.Path
import Ashes.Text.Json
import AshesCompiler.Cli.Add
import AshesCompiler.Cli.Fmt
import AshesCompiler.Cli.Init
import AshesCompiler.Cli.Remove
import AshesCompiler.Cli.Tree
import AshesCompiler.Cli.Why
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

let testParseInitArgumentsAcceptsNoArguments unit =
    match parseInitArguments([]) with
        | InitParsedArguments -> test.assertEqual(true)(true)
        | _ -> test.fail("expected no arguments to parse")

let testParseInitArgumentsAcceptsHelp unit =
    match parseInitArguments(["--help"]) with
        | InitHelpRequested -> test.assertEqual(true)(true)
        | _ -> test.fail("expected --help to request help")

let testParseInitArgumentsRejectsExtraArguments unit =
    match parseInitArguments(["unexpected"]) with
        | InitUsageError(message) -> test.assertEqual("Unknown argument.")(message)
        | _ -> test.fail("expected an unexpected argument to be a usage error")

let testInitProjectJsonMatchesStage0Format unit =
    "myapp"
    |> initProjectJson
    |> test.assertEqual("{\n  \"name\": \"myapp\",\n  \"entry\": \"src/Main.ash\",\n  \"sourceRoots\": [\n    \"src\"\n  ]\n}\n")

// End-to-end tests below exercise the real filesystem, matching the fmt tests' own scratch-
// directory pattern above (a separate scratch root, so the two test groups never interfere).
let initScratchRoot = "cli-init-scratch"

let removeInitScratch unit =
    match Ashes.IO.Directory.removeTree(initScratchRoot) with
        | Ok(_) -> Unit
        | Error(message) -> test.fail("failed to clean up scratch directory: " + message)

let testRunInitInDirectoryScaffoldsProject unit =
    (let _ = removeInitScratch(Unit)
    in
        let _ =
            match Ashes.IO.Directory.createAll(initScratchRoot) with
                | Ok(_) -> Unit
                | Error(message) -> test.fail("failed to create scratch directory: " + message)
        in
            let result =
                match runInitInDirectory(initScratchRoot) with
                    | InitSucceeded ->
                        match Ashes.IO.File.readText(initScratchRoot + "/ashes.json") with
                            | Error(message) -> test.fail("expected ashes.json to be readable: " + message)
                            | Ok(manifestText) ->
                                manifestText
                                |> test.assertEqual(initProjectJson(initScratchRoot))
                                |> (given (_) ->
                                    match Ashes.IO.File.readText(initScratchRoot + "/src/Main.ash") with
                                        | Error(message) -> test.fail("expected src/Main.ash to be readable: " + message)
                                        | Ok(mainText) -> test.assertEqual("Ashes.IO.print(\"hello, ashes!\")\n")(mainText))
                    | InitFailed(message) -> test.fail("expected scaffolding to succeed: " + message)
            in
                let _ = removeInitScratch(Unit)
                in result)

let testRunInitInDirectoryFailsWhenManifestExists unit =
    (let _ = removeInitScratch(Unit)
    in
        let _ =
            match Ashes.IO.Directory.createAll(initScratchRoot) with
                | Ok(_) -> Unit
                | Error(message) -> test.fail("failed to create scratch directory: " + message)
        in
            let _ =
                match Ashes.IO.File.writeText(initScratchRoot + "/ashes.json")("{}") with
                    | Ok(_) -> Unit
                    | Error(message) -> test.fail("failed to write scratch manifest: " + message)
            in
                let result =
                    match runInitInDirectory(initScratchRoot) with
                        | InitFailed(message) -> test.assertEqual("ashes.json already exists in this directory.")(message)
                        | InitSucceeded -> test.fail("expected an existing manifest to fail init")
                in
                    let _ = removeInitScratch(Unit)
                    in result)

let testRunInitInDirectoryPreservesExistingMain unit =
    (let _ = removeInitScratch(Unit)
    in
        let _ =
            match Ashes.IO.Directory.createAll(initScratchRoot + "/src") with
                | Ok(_) -> Unit
                | Error(message) -> test.fail("failed to create scratch directory: " + message)
        in
            let _ =
                match Ashes.IO.File.writeText(initScratchRoot + "/src/Main.ash")("Ashes.IO.print(\"custom\")\n") with
                    | Ok(_) -> Unit
                    | Error(message) -> test.fail("failed to write scratch Main.ash: " + message)
            in
                let result =
                    match runInitInDirectory(initScratchRoot) with
                        | InitFailed(message) -> test.fail("expected scaffolding to succeed: " + message)
                        | InitSucceeded ->
                            match Ashes.IO.File.readText(initScratchRoot + "/src/Main.ash") with
                                | Error(message) -> test.fail("expected src/Main.ash to be readable: " + message)
                                | Ok(mainText) -> test.assertEqual("Ashes.IO.print(\"custom\")\n")(mainText)
                in
                    let _ = removeInitScratch(Unit)
                    in result)

let testParseWhyArgumentsHelp unit =
    match parseWhyArguments(["--help"]) with
        | WhyHelpRequested -> test.assertEqual(true)(true)
        | _ -> test.fail("expected --help to request help")

let testParseWhyArgumentsShortHelp unit =
    match parseWhyArguments(["-h"]) with
        | WhyHelpRequested -> test.assertEqual(true)(true)
        | _ -> test.fail("expected -h to request help")

let testParseWhyArgumentsMissingTarget unit =
    match parseWhyArguments([]) with
        | WhyUsageError(message) -> test.assertEqual("Usage: ashes why <namespace>")(message)
        | _ -> test.fail("expected zero arguments to be a usage error")

let testParseWhyArgumentsPascalCasesTarget unit =
    match parseWhyArguments(["some-package"]) with
        | WhyParsedArguments(WhyArguments { target = target, projectOption = projectOption }) ->
            target
            |> test.assertEqual("SomePackage")
            |> (given (_) -> test.assertEqual(None)(projectOption))
        | _ -> test.fail("expected a single target to parse")

let testParseWhyArgumentsAcceptsProjectOption unit =
    match parseWhyArguments(["--project", "other/ashes.json", "base"]) with
        | WhyParsedArguments(WhyArguments { target = target, projectOption = projectOption }) ->
            target
            |> test.assertEqual("Base")
            |> (given (_) -> test.assertEqual(Some("other/ashes.json"))(projectOption))
        | _ -> test.fail("expected --project to be captured alongside the target")

let testFindDependencyPathReturnsDirectRoot unit =
    [("Mid", ["Base"])]
    |> findDependencyPath(["Mid", "Helper"])("Helper")
    |> test.assertEqual(Some(["Helper"]))

let testFindDependencyPathReturnsTransitivePath unit =
    [("Mid", ["Base"]), ("Helper", [])]
    |> findDependencyPath(["Mid", "Helper"])("Base")
    |> test.assertEqual(Some(["Mid", "Base"]))

let testFindDependencyPathReturnsNoneWhenMissing unit =
    [("Mid", ["Base"]), ("Base", [])]
    |> findDependencyPath(["Mid"])("Missing")
    |> test.assertEqual(None)

let testFindDependencyPathTerminatesOnCycle unit =
    [("A", ["B"]), ("B", ["A"])]
    |> findDependencyPath(["A"])("Missing")
    |> test.assertEqual(None)

// End-to-end tests below build a real registry-style dependency graph on disk, matching how the
// selfhost packages themselves declare dependencies (a `dependencies`/`devDependencies` registry
// entry paired with a same-named `overrides` path, e.g. selfhost/packages/cli/ashes.json): `why`
// reads root namespaces from the manifest and edges from the lock file, so `app` directly overrides
// `Mid` and (as a devDependency) `Testing`, and the hand-written lock records that `Mid` itself
// depends on `Base` — a namespace that never needs to exist on disk, since BFS only ever consults
// lock-recorded edges (mirroring stage 0's own `ReadLockGraph`/`FindPath`, never live resolution).
let whyScratchRoot = "cli-why-scratch"

let removeWhyScratch unit =
    match Ashes.IO.Directory.removeTree(whyScratchRoot) with
        | Ok(_) -> Unit
        | Error(message) -> test.fail("failed to clean up scratch directory: " + message)

let requireWhyUnit name result =
    match result with
        | Ok(_) -> Unit
        | Error(error) -> test.fail(name + " failed: " + error)

let writeWhyFile relativePath contents =
    contents
    |> Ashes.IO.File.writeText(whyScratchRoot + "/" + relativePath)
    |> requireWhyUnit("write " + relativePath)

let createWhyDirectory relativePath =
    whyScratchRoot + "/" + relativePath
    |> Ashes.IO.Directory.createAll
    |> requireWhyUnit("create " + relativePath)

let prepareWhyFixture unit =
    Unit
    |> removeWhyScratch
    |> (given (_) -> createWhyDirectory("app/src"))
    |> (given (_) -> createWhyDirectory("mid/src"))
    |> (given (_) -> createWhyDirectory("helper/src"))
    |> (given (_) -> writeWhyFile("app/src/Main.ash")("0"))
    |> (given (_) -> writeWhyFile("mid/src/Mid.ash")("0"))
    |> (given (_) -> writeWhyFile("helper/src/Testing.ash")("0"))
    |> (given (_) -> writeWhyFile("mid/ashes.json")("{\"name\":\"mid\",\"namespace\":\"Mid\",\"version\":\"0.1.0\",\"entry\":\"src/Mid.ash\",\"sourceRoots\":[\"src\"]}"))
    |> (given (_) -> writeWhyFile("helper/ashes.json")("{\"name\":\"helper\",\"namespace\":\"Testing\",\"version\":\"0.1.0\",\"entry\":\"src/Testing.ash\",\"sourceRoots\":[\"src\"]}"))
    |> (given (_) -> writeWhyFile("app/ashes.json")("{\"entry\":\"src/Main.ash\",\"sourceRoots\":[\"src\"],\"dependencies\":{\"Mid\":\"=0.1.0\"},\"devDependencies\":{\"Testing\":\"=0.1.0\"},\"overrides\":{\"Mid\":{\"path\":\"../mid\"},\"Testing\":{\"path\":\"../helper\"}}}"))
    |> (given (_) -> writeWhyFile("app/ashes.lock")("{\"version\":1,\"package\":[{\"namespace\":\"Mid\",\"version\":\"0.1.0\",\"source\":\"registry+https://pkg.ashes-lang.org\",\"hash\":\"ash1:0000000000000000000000000000000000000000000000000000000000000\",\"dependencies\":[\"Base\"]},{\"namespace\":\"Testing\",\"version\":\"0.1.0\",\"source\":\"registry+https://pkg.ashes-lang.org\",\"hash\":\"ash1:0000000000000000000000000000000000000000000000000000000000001\",\"dependencies\":[]}]}"))

let whyAppManifestPath = whyScratchRoot + "/app/ashes.json"

let testRunWhyInProjectFindsTransitivePath unit =
    (let _ = prepareWhyFixture(Unit)
    in
        let result =
            match runWhyInProject(Unix)(whyAppManifestPath)("Base") with
                | WhyFound(path) -> test.assertEqual(["Mid", "Base"])(path)
                | WhyNotFound(target) -> test.fail("expected Base to be found via Mid, got not-found for " + target)
                | WhyFailed(message) -> test.fail("expected why to succeed: " + message)
        in
            let _ = removeWhyScratch(Unit)
            in result)

let testRunWhyInProjectFindsDirectDevDependency unit =
    (let _ = prepareWhyFixture(Unit)
    in
        let result =
            match runWhyInProject(Unix)(whyAppManifestPath)("Testing") with
                | WhyFound(path) -> test.assertEqual(["Testing"])(path)
                | WhyNotFound(target) -> test.fail("expected Testing to be found directly, got not-found for " + target)
                | WhyFailed(message) -> test.fail("expected why to succeed: " + message)
        in
            let _ = removeWhyScratch(Unit)
            in result)

let testRunWhyInProjectReportsNotFoundForUnrelatedNamespace unit =
    (let _ = prepareWhyFixture(Unit)
    in
        let result =
            match runWhyInProject(Unix)(whyAppManifestPath)("Nowhere") with
                | WhyFound(path) -> test.fail("expected Nowhere not to be a dependency, found path " + Ashes.Text.join(" -> ")(path))
                | WhyNotFound(target) -> test.assertEqual("Nowhere")(target)
                | WhyFailed(message) -> test.fail("expected why to succeed: " + message)
        in
            let _ = removeWhyScratch(Unit)
            in result)

let testRunWhyInProjectFailsWhenManifestMissing unit =
    (let _ = removeWhyScratch(Unit)
    in
        let result =
            match runWhyInProject(Unix)(whyAppManifestPath)("Base") with
                | WhyFailed(_message) -> test.assertEqual(true)(true)
                | _ -> test.fail("expected a missing manifest to fail")
        in
            let _ = removeWhyScratch(Unit)
            in result)

let testParseTreeArgumentsHelp unit =
    match parseTreeArguments(["--help"]) with
        | TreeHelpRequested -> test.assertEqual(true)(true)
        | _ -> test.fail("expected --help to request help")

let testParseTreeArgumentsShortHelp unit =
    match parseTreeArguments(["-h"]) with
        | TreeHelpRequested -> test.assertEqual(true)(true)
        | _ -> test.fail("expected -h to request help")

let testParseTreeArgumentsNoArguments unit =
    match parseTreeArguments([]) with
        | TreeParsedArguments(TreeArguments { projectOption = projectOption }) -> test.assertEqual(None)(projectOption)
        | _ -> test.fail("expected zero arguments to parse with no project override")

let testParseTreeArgumentsAcceptsProjectOption unit =
    match parseTreeArguments(["--project", "other/ashes.json"]) with
        | TreeParsedArguments(TreeArguments { projectOption = projectOption }) -> test.assertEqual(Some("other/ashes.json"))(projectOption)
        | _ -> test.fail("expected --project to be captured")

let testRenderDependencyTreeRendersRootOnlyWithNoDependencies unit =
    []
    |> renderDependencyTree("app")([])([])
    |> test.assertEqual(["app"])

let testRenderDependencyTreeRendersDirectAndTransitiveDependencies unit =
    [("Json", "1.2.0"), ("Utf8", "0.4.3")]
    |> renderDependencyTree("app")([("Json", false)])([("Json", ["Utf8"])])
    |> test.assertEqual(["app", "└── Json 1.2.0", "    └── Utf8 0.4.3"])

let testRenderDependencyTreeMarksPathDependenciesAndSeparatesSiblings unit =
    [("Testing", "0.1.0")]
    |> renderDependencyTree("app")([("Mid", true), ("Testing", false)])([("Mid", ["Base"])])
    |> test.assertEqual(["app", "├── Mid (path)", "│   └── Base ?", "└── Testing 0.1.0"])

let testRenderDependencyTreeCutsCyclesAlongTheSamePath unit =
    []
    |> renderDependencyTree("app")([("A", false)])([("A", ["B"]), ("B", ["A"])])
    |> test.assertEqual(["app", "└── A ?", "    └── B ?", "        └── A (cycle)"])

// End-to-end tests below build a real registry-style dependency graph on disk, mirroring the
// `why` fixture above (`app` overrides a registry dependency `Mid` and a devDependency
// `Testing`, and the hand-written lock records that `Mid` itself depends on `Base`, a namespace
// that never needs to exist on disk since the tree is rendered purely from lock-recorded edges).
let treeScratchRoot = "cli-tree-scratch"

let removeTreeScratch unit =
    match Ashes.IO.Directory.removeTree(treeScratchRoot) with
        | Ok(_) -> Unit
        | Error(message) -> test.fail("failed to clean up scratch directory: " + message)

let requireTreeUnit name result =
    match result with
        | Ok(_) -> Unit
        | Error(error) -> test.fail(name + " failed: " + error)

let writeTreeFile relativePath contents =
    contents
    |> Ashes.IO.File.writeText(treeScratchRoot + "/" + relativePath)
    |> requireTreeUnit("write " + relativePath)

let createTreeDirectory relativePath =
    treeScratchRoot + "/" + relativePath
    |> Ashes.IO.Directory.createAll
    |> requireTreeUnit("create " + relativePath)

let prepareTreeFixture unit =
    Unit
    |> removeTreeScratch
    |> (given (_) -> createTreeDirectory("app/src"))
    |> (given (_) -> createTreeDirectory("mid/src"))
    |> (given (_) -> createTreeDirectory("helper/src"))
    |> (given (_) -> writeTreeFile("app/src/Main.ash")("0"))
    |> (given (_) -> writeTreeFile("mid/src/Mid.ash")("0"))
    |> (given (_) -> writeTreeFile("helper/src/Testing.ash")("0"))
    |> (given (_) -> writeTreeFile("mid/ashes.json")("{\"name\":\"mid\",\"namespace\":\"Mid\",\"version\":\"0.1.0\",\"entry\":\"src/Mid.ash\",\"sourceRoots\":[\"src\"]}"))
    |> (given (_) -> writeTreeFile("helper/ashes.json")("{\"name\":\"helper\",\"namespace\":\"Testing\",\"version\":\"0.1.0\",\"entry\":\"src/Testing.ash\",\"sourceRoots\":[\"src\"]}"))
    |> (given (_) -> writeTreeFile("app/ashes.json")("{\"name\":\"app\",\"entry\":\"src/Main.ash\",\"sourceRoots\":[\"src\"],\"dependencies\":{\"Mid\":\"=0.1.0\"},\"devDependencies\":{\"Testing\":\"=0.1.0\"},\"overrides\":{\"Mid\":{\"path\":\"../mid\"},\"Testing\":{\"path\":\"../helper\"}}}"))
    |> (given (_) -> writeTreeFile("app/ashes.lock")("{\"version\":1,\"package\":[{\"namespace\":\"Mid\",\"version\":\"0.1.0\",\"source\":\"registry+https://pkg.ashes-lang.org\",\"hash\":\"ash1:0000000000000000000000000000000000000000000000000000000000000\",\"dependencies\":[\"Base\"]},{\"namespace\":\"Testing\",\"version\":\"0.1.0\",\"source\":\"registry+https://pkg.ashes-lang.org\",\"hash\":\"ash1:0000000000000000000000000000000000000000000000000000000000001\",\"dependencies\":[]}]}"))

let treeAppManifestPath = treeScratchRoot + "/app/ashes.json"

let testRunTreeInProjectRendersDirectDevAndTransitiveDependencies unit =
    (let _ = prepareTreeFixture(Unit)
    in
        let result =
            match runTreeInProject(Unix)(treeAppManifestPath) with
                | TreeRendered(text) -> test.assertEqual("app\n├── Mid 0.1.0\n│   └── Base ?\n└── Testing 0.1.0")(text)
                | TreeFailed(message) -> test.fail("expected tree to succeed: " + message)
        in
            let _ = removeTreeScratch(Unit)
            in result)

let testRunTreeInProjectFailsWhenManifestMissing unit =
    (let _ = removeTreeScratch(Unit)
    in
        let result =
            match runTreeInProject(Unix)(treeAppManifestPath) with
                | TreeFailed(_message) -> test.assertEqual(true)(true)
                | TreeRendered(_text) -> test.fail("expected a missing manifest to fail")
        in
            let _ = removeTreeScratch(Unit)
            in result)

let testParseAddArgumentsHelp unit =
    match parseAddArguments(["--help"]) with
        | AddHelpRequested -> test.assertEqual(true)(true)
        | _ -> test.fail("expected --help to request help")

let testParseAddArgumentsShortHelp unit =
    match parseAddArguments(["-h"]) with
        | AddHelpRequested -> test.assertEqual(true)(true)
        | _ -> test.fail("expected -h to request help")

let testParseAddArgumentsMissingPackageName unit =
    match parseAddArguments([]) with
        | AddMissingPackageName -> test.assertEqual(true)(true)
        | _ -> test.fail("expected zero arguments to report a missing package name")

let testParseAddArgumentsPlainPackageName unit =
    match parseAddArguments(["json-parser"]) with
        | AddParsedArguments(AddArguments { packageName = packageName, pathOption = pathOption, isDev = isDev, projectOption = projectOption }) ->
            packageName
            |> test.assertEqual("json-parser")
            |> (given (_) -> test.assertEqual(None)(pathOption))
            |> (given (_) -> test.assertEqual(false)(isDev))
            |> (given (_) -> test.assertEqual(None)(projectOption))
        | _ -> test.fail("expected a single package name to parse")

let testParseAddArgumentsCapturesPathDevAndProject unit =
    match parseAddArguments(["json-parser", "--path", "../dep", "--dev", "--project", "other/ashes.json"]) with
        | AddParsedArguments(AddArguments { packageName = packageName, pathOption = pathOption, isDev = isDev, projectOption = projectOption }) ->
            packageName
            |> test.assertEqual("json-parser")
            |> (given (_) -> test.assertEqual(Some("../dep"))(pathOption))
            |> (given (_) -> test.assertEqual(true)(isDev))
            |> (given (_) -> test.assertEqual(Some("other/ashes.json"))(projectOption))
        | _ -> test.fail("expected path/dev/project to be captured")

let testDependencyValueDefaultsToWildcardVersion unit =
    None
    |> dependencyValue
    |> stringify
    |> test.assertEqual("\"*\"")

let testDependencyValueWithPathNormalizesSeparators unit =
    Some("..\\dep")
    |> dependencyValue
    |> stringify
    |> test.assertEqual("{\"path\":\"../dep\"}")

let testSetJsonObjectFieldUpdatesExistingKeyInPlace unit =
    JsonObjectEnd
    |> JsonObject("b")(JsonStr("2"))
    |> JsonObject("a")(JsonStr("1"))
    |> setJsonObjectField("a")(JsonStr("updated"))
    |> stringify
    |> test.assertEqual("{\"a\":\"updated\",\"b\":\"2\"}")

let testSetJsonObjectFieldAppendsNewKeyAtEnd unit =
    JsonObjectEnd
    |> JsonObject("a")(JsonStr("1"))
    |> setJsonObjectField("b")(JsonStr("2"))
    |> stringify
    |> test.assertEqual("{\"a\":\"1\",\"b\":\"2\"}")

let testAddPackageToManifestCreatesFieldWhenMissing unit =
    JsonObjectEnd
    |> JsonObject("name")(JsonStr("app"))
    |> addPackageToManifest("dependencies")("json-parser")(JsonStr("*"))
    |> stringify
    |> test.assertEqual("{\"name\":\"app\",\"dependencies\":{\"json-parser\":\"*\"}}")

let testAddPackageToManifestPreservesOtherDependencyField unit =
    JsonObjectEnd
    |> JsonObject("dependencies")(JsonObject("json-parser")(JsonStr("*"))(JsonObjectEnd))
    |> addPackageToManifest("devDependencies")("test-helper")(JsonStr("*"))
    |> stringify
    |> test.assertEqual("{\"dependencies\":{\"json-parser\":\"*\"},\"devDependencies\":{\"test-helper\":\"*\"}}")

let testAddPackageToManifestOverwritesExistingPackageEntry unit =
    JsonObjectEnd
    |> JsonObject("dependencies")(JsonObject("json-parser")(JsonStr("1.0.0"))(JsonObjectEnd))
    |> addPackageToManifest("dependencies")("json-parser")(JsonStr("*"))
    |> stringify
    |> test.assertEqual("{\"dependencies\":{\"json-parser\":\"*\"}}")

let testStringifyIndentedMatchesInitSampleShape unit =
    JsonObjectEnd
    |> JsonObject("sourceRoots")(JsonArray(JsonStr("src"))(JsonArrayEnd))
    |> JsonObject("entry")(JsonStr("src/Main.ash"))
    |> JsonObject("name")(JsonStr("myapp"))
    |> stringifyIndented(0)
    |> test.assertEqual("{\n  \"name\": \"myapp\",\n  \"entry\": \"src/Main.ash\",\n  \"sourceRoots\": [\n    \"src\"\n  ]\n}")

let testStringifyIndentedRendersEmptyCollectionsCompactly unit =
    JsonObjectEnd
    |> JsonObject("tags")(JsonArrayEnd)
    |> JsonObject("dependencies")(JsonObjectEnd)
    |> stringifyIndented(0)
    |> test.assertEqual("{\n  \"dependencies\": {},\n  \"tags\": []\n}")

// End-to-end tests below exercise the real filesystem, matching the other commands' own scratch-
// directory pattern above.
let addScratchRoot = "cli-add-scratch"

let removeAddScratch unit =
    match Ashes.IO.Directory.removeTree(addScratchRoot) with
        | Ok(_) -> Unit
        | Error(message) -> test.fail("failed to clean up scratch directory: " + message)

let writeAddScratchFile relativePath content =
    match Ashes.IO.File.writeText(addScratchRoot + "/" + relativePath)(content) with
        | Ok(_) -> Unit
        | Error(message) -> test.fail("failed to write scratch file: " + message)

let addManifestPath = addScratchRoot + "/ashes.json"

let testRunAddInProjectCreatesDependenciesFieldWhenMissing unit =
    (let _ = removeAddScratch(Unit)
    in
        let _ =
            match Ashes.IO.Directory.createAll(addScratchRoot) with
                | Ok(_) -> Unit
                | Error(message) -> test.fail("failed to create scratch directory: " + message)
        in
            let _ = writeAddScratchFile("ashes.json")("{\"name\":\"app\",\"entry\":\"src/Main.ash\",\"sourceRoots\":[\"src\"]}")
            in
                let result =
                    match runAddInProject(addManifestPath)("json-parser")(None)(false) with
                        | AddSucceeded(packageName, field) ->
                            packageName
                            |> test.assertEqual("json-parser")
                            |> (given (_) ->
                                field
                                |> test.assertEqual("dependencies")
                                |> (given (_) ->
                                    match Ashes.IO.File.readText(addManifestPath) with
                                        | Ok(text) -> test.assertEqual("{\n  \"name\": \"app\",\n  \"entry\": \"src/Main.ash\",\n  \"sourceRoots\": [\n    \"src\"\n  ],\n  \"dependencies\": {\n    \"json-parser\": \"*\"\n  }\n}\n")(text)
                                        | Error(message) -> test.fail("expected the rewritten manifest to be readable: " + message)))
                        | AddFailed(message) -> test.fail("expected add to succeed: " + message)
                in
                    let _ = removeAddScratch(Unit)
                    in result)

let testRunAddInProjectDevPreservesExistingDependencies unit =
    (let _ = removeAddScratch(Unit)
    in
        let _ =
            match Ashes.IO.Directory.createAll(addScratchRoot) with
                | Ok(_) -> Unit
                | Error(message) -> test.fail("failed to create scratch directory: " + message)
        in
            let _ = writeAddScratchFile("ashes.json")("{\"name\":\"app\",\"entry\":\"src/Main.ash\",\"sourceRoots\":[\"src\"],\"dependencies\":{\"json-parser\":\"*\"}}")
            in
                let result =
                    match runAddInProject(addManifestPath)("test-helper")(None)(true) with
                        | AddSucceeded(_packageName, field) ->
                            field
                            |> test.assertEqual("devDependencies")
                            |> (given (_) ->
                                match Ashes.IO.File.readText(addManifestPath) with
                                    | Ok(text) -> test.assertEqual("{\n  \"name\": \"app\",\n  \"entry\": \"src/Main.ash\",\n  \"sourceRoots\": [\n    \"src\"\n  ],\n  \"dependencies\": {\n    \"json-parser\": \"*\"\n  },\n  \"devDependencies\": {\n    \"test-helper\": \"*\"\n  }\n}\n")(text)
                                    | Error(message) -> test.fail("expected the rewritten manifest to be readable: " + message))
                        | AddFailed(message) -> test.fail("expected add to succeed: " + message)
                in
                    let _ = removeAddScratch(Unit)
                    in result)

let testRunAddInProjectWithPathNormalizesSeparators unit =
    (let _ = removeAddScratch(Unit)
    in
        let _ =
            match Ashes.IO.Directory.createAll(addScratchRoot) with
                | Ok(_) -> Unit
                | Error(message) -> test.fail("failed to create scratch directory: " + message)
        in
            let _ = writeAddScratchFile("ashes.json")("{\"entry\":\"src/Main.ash\",\"sourceRoots\":[\"src\"]}")
            in
                let result =
                    match runAddInProject(addManifestPath)("greet")(Some("..\\dep"))(false) with
                        | AddSucceeded(_packageName, _field) ->
                            match Ashes.IO.File.readText(addManifestPath) with
                                | Ok(text) -> test.assertEqual("{\n  \"entry\": \"src/Main.ash\",\n  \"sourceRoots\": [\n    \"src\"\n  ],\n  \"dependencies\": {\n    \"greet\": {\n      \"path\": \"../dep\"\n    }\n  }\n}\n")(text)
                                | Error(message) -> test.fail("expected the rewritten manifest to be readable: " + message)
                        | AddFailed(message) -> test.fail("expected add to succeed: " + message)
                in
                    let _ = removeAddScratch(Unit)
                    in result)

let testRunAddInProjectFailsWhenManifestMissing unit =
    (let _ = removeAddScratch(Unit)
    in
        let result =
            match runAddInProject(addManifestPath)("json-parser")(None)(false) with
                | AddFailed(_message) -> test.assertEqual(true)(true)
                | AddSucceeded(_packageName, _field) -> test.fail("expected a missing manifest to fail")
        in
            let _ = removeAddScratch(Unit)
            in result)

let testParseRemoveArgumentsHelp unit =
    match parseRemoveArguments(["--help"]) with
        | RemoveHelpRequested -> test.assertEqual(true)(true)
        | _ -> test.fail("expected --help to request help")

let testParseRemoveArgumentsShortHelp unit =
    match parseRemoveArguments(["-h"]) with
        | RemoveHelpRequested -> test.assertEqual(true)(true)
        | _ -> test.fail("expected -h to request help")

let testParseRemoveArgumentsMissingPackageName unit =
    match parseRemoveArguments([]) with
        | RemoveMissingPackageName -> test.assertEqual(true)(true)
        | _ -> test.fail("expected zero arguments to report a missing package name")

let testParseRemoveArgumentsPlainPackageName unit =
    match parseRemoveArguments(["json-parser"]) with
        | RemoveParsedArguments(RemoveArguments { packageName = packageName, projectOption = projectOption }) ->
            packageName
            |> test.assertEqual("json-parser")
            |> (given (_) -> test.assertEqual(None)(projectOption))
        | _ -> test.fail("expected a single package name to parse")

let testParseRemoveArgumentsAcceptsProjectOption unit =
    match parseRemoveArguments(["--project", "other/ashes.json", "json-parser"]) with
        | RemoveParsedArguments(RemoveArguments { packageName = packageName, projectOption = projectOption }) ->
            packageName
            |> test.assertEqual("json-parser")
            |> (given (_) -> test.assertEqual(Some("other/ashes.json"))(projectOption))
        | _ -> test.fail("expected --project to be captured alongside the package name")

let testRemoveObjectFieldRemovesExistingKey unit =
    match JsonObjectEnd
    |> JsonObject("b")(JsonStr("2"))
    |> JsonObject("a")(JsonStr("1"))
    |> removeObjectField("a") with
        | (updated, removed) ->
            removed
            |> test.assertEqual(true)
            |> (given (_) ->
                updated
                |> stringify
                |> test.assertEqual("{\"b\":\"2\"}"))

let testRemoveObjectFieldReturnsUnchangedWhenKeyMissing unit =
    match JsonObjectEnd
    |> JsonObject("a")(JsonStr("1"))
    |> removeObjectField("missing") with
        | (updated, removed) ->
            removed
            |> test.assertEqual(false)
            |> (given (_) ->
                updated
                |> stringify
                |> test.assertEqual("{\"a\":\"1\"}"))

let testRemovePackageFromManifestOmitsFieldWhenEmpty unit =
    match JsonObjectEnd
    |> JsonObject("dependencies")(JsonObject("json-parser")(JsonStr("*"))(JsonObjectEnd))
    |> removePackageFromManifest("json-parser") with
        | (updated, removed) ->
            removed
            |> test.assertEqual(true)
            |> (given (_) ->
                updated
                |> stringify
                |> test.assertEqual("{}"))

let testRemovePackageFromManifestKeepsFieldWithRemainingEntries unit =
    match JsonObjectEnd
    |> JsonObject("dependencies")(JsonObjectEnd
    |> JsonObject("other-pkg")(JsonStr("*"))
    |> JsonObject("json-parser")(JsonStr("*")))
    |> removePackageFromManifest("json-parser") with
        | (updated, removed) ->
            removed
            |> test.assertEqual(true)
            |> (given (_) ->
                updated
                |> stringify
                |> test.assertEqual("{\"dependencies\":{\"other-pkg\":\"*\"}}"))

let testRemovePackageFromManifestChecksDevDependenciesToo unit =
    match JsonObjectEnd
    |> JsonObject("devDependencies")(JsonObject("test-helper")(JsonStr("*"))(JsonObjectEnd))
    |> removePackageFromManifest("test-helper") with
        | (updated, removed) ->
            removed
            |> test.assertEqual(true)
            |> (given (_) ->
                updated
                |> stringify
                |> test.assertEqual("{}"))

let testRemovePackageFromManifestReturnsUnchangedWhenNotADependency unit =
    match JsonObjectEnd
    |> JsonObject("dependencies")(JsonObject("json-parser")(JsonStr("*"))(JsonObjectEnd))
    |> removePackageFromManifest("missing") with
        | (updated, removed) ->
            removed
            |> test.assertEqual(false)
            |> (given (_) ->
                updated
                |> stringify
                |> test.assertEqual("{\"dependencies\":{\"json-parser\":\"*\"}}"))

// End-to-end tests below exercise the real filesystem, matching the other commands' own scratch-
// directory pattern above.
let removeScratchRoot = "cli-remove-scratch"

let removeRemoveScratch unit =
    match Ashes.IO.Directory.removeTree(removeScratchRoot) with
        | Ok(_) -> Unit
        | Error(message) -> test.fail("failed to clean up scratch directory: " + message)

let writeRemoveScratchFile relativePath content =
    match Ashes.IO.File.writeText(removeScratchRoot + "/" + relativePath)(content) with
        | Ok(_) -> Unit
        | Error(message) -> test.fail("failed to write scratch file: " + message)

let removeManifestPath = removeScratchRoot + "/ashes.json"

let testRunRemoveInProjectOmitsFieldWhenLastDependencyRemoved unit =
    (let _ = removeRemoveScratch(Unit)
    in
        let _ =
            match Ashes.IO.Directory.createAll(removeScratchRoot) with
                | Ok(_) -> Unit
                | Error(message) -> test.fail("failed to create scratch directory: " + message)
        in
            let _ = writeRemoveScratchFile("ashes.json")("{\"name\":\"app\",\"entry\":\"src/Main.ash\",\"sourceRoots\":[\"src\"],\"dependencies\":{\"json-parser\":\"*\"}}")
            in
                let result =
                    match runRemoveInProject(removeManifestPath)("json-parser") with
                        | RemoveSucceeded(packageName) ->
                            packageName
                            |> test.assertEqual("json-parser")
                            |> (given (_) ->
                                match Ashes.IO.File.readText(removeManifestPath) with
                                    | Ok(text) -> test.assertEqual("{\n  \"name\": \"app\",\n  \"entry\": \"src/Main.ash\",\n  \"sourceRoots\": [\n    \"src\"\n  ]\n}\n")(text)
                                    | Error(message) -> test.fail("expected the rewritten manifest to be readable: " + message))
                        | RemoveNotADependency(_) -> test.fail("expected json-parser to be found")
                        | RemoveFailed(message) -> test.fail("expected remove to succeed: " + message)
                in
                    let _ = removeRemoveScratch(Unit)
                    in result)

let testRunRemoveInProjectKeepsOtherDependencies unit =
    (let _ = removeRemoveScratch(Unit)
    in
        let _ =
            match Ashes.IO.Directory.createAll(removeScratchRoot) with
                | Ok(_) -> Unit
                | Error(message) -> test.fail("failed to create scratch directory: " + message)
        in
            let _ = writeRemoveScratchFile("ashes.json")("{\"entry\":\"src/Main.ash\",\"sourceRoots\":[\"src\"],\"dependencies\":{\"json-parser\":\"*\",\"other-pkg\":\"*\"}}")
            in
                let result =
                    match runRemoveInProject(removeManifestPath)("json-parser") with
                        | RemoveSucceeded(_packageName) ->
                            match Ashes.IO.File.readText(removeManifestPath) with
                                | Ok(text) -> test.assertEqual("{\n  \"entry\": \"src/Main.ash\",\n  \"sourceRoots\": [\n    \"src\"\n  ],\n  \"dependencies\": {\n    \"other-pkg\": \"*\"\n  }\n}\n")(text)
                                | Error(message) -> test.fail("expected the rewritten manifest to be readable: " + message)
                        | RemoveNotADependency(_) -> test.fail("expected json-parser to be found")
                        | RemoveFailed(message) -> test.fail("expected remove to succeed: " + message)
                in
                    let _ = removeRemoveScratch(Unit)
                    in result)

let testRunRemoveInProjectReportsNotADependency unit =
    (let _ = removeRemoveScratch(Unit)
    in
        let _ =
            match Ashes.IO.Directory.createAll(removeScratchRoot) with
                | Ok(_) -> Unit
                | Error(message) -> test.fail("failed to create scratch directory: " + message)
        in
            let _ = writeRemoveScratchFile("ashes.json")("{\"entry\":\"src/Main.ash\",\"sourceRoots\":[\"src\"]}")
            in
                let result =
                    match runRemoveInProject(removeManifestPath)("missing") with
                        | RemoveNotADependency(packageName) -> test.assertEqual("missing")(packageName)
                        | RemoveSucceeded(_) -> test.fail("expected 'missing' not to be a dependency")
                        | RemoveFailed(message) -> test.fail("expected a not-a-dependency outcome, not a failure: " + message)
                in
                    let _ = removeRemoveScratch(Unit)
                    in result)

let testRunRemoveInProjectFailsWhenManifestMissing unit =
    (let _ = removeRemoveScratch(Unit)
    in
        let result =
            match runRemoveInProject(removeManifestPath)("json-parser") with
                | RemoveFailed(_message) -> test.assertEqual(true)(true)
                | RemoveSucceeded(_) -> test.fail("expected a missing manifest to fail")
                | RemoveNotADependency(_) -> test.fail("expected a missing manifest to fail, not a not-a-dependency outcome")
        in
            let _ = removeRemoveScratch(Unit)
            in result)

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
    |> testParseInitArgumentsAcceptsNoArguments
    |> testParseInitArgumentsAcceptsHelp
    |> testParseInitArgumentsRejectsExtraArguments
    |> testInitProjectJsonMatchesStage0Format
    |> testRunInitInDirectoryScaffoldsProject
    |> testRunInitInDirectoryFailsWhenManifestExists
    |> testRunInitInDirectoryPreservesExistingMain
    |> testParseWhyArgumentsHelp
    |> testParseWhyArgumentsShortHelp
    |> testParseWhyArgumentsMissingTarget
    |> testParseWhyArgumentsPascalCasesTarget
    |> testParseWhyArgumentsAcceptsProjectOption
    |> testFindDependencyPathReturnsDirectRoot
    |> testFindDependencyPathReturnsTransitivePath
    |> testFindDependencyPathReturnsNoneWhenMissing
    |> testFindDependencyPathTerminatesOnCycle
    |> testRunWhyInProjectFindsTransitivePath
    |> testRunWhyInProjectFindsDirectDevDependency
    |> testRunWhyInProjectReportsNotFoundForUnrelatedNamespace
    |> testRunWhyInProjectFailsWhenManifestMissing
    |> testParseTreeArgumentsHelp
    |> testParseTreeArgumentsShortHelp
    |> testParseTreeArgumentsNoArguments
    |> testParseTreeArgumentsAcceptsProjectOption
    |> testRenderDependencyTreeRendersRootOnlyWithNoDependencies
    |> testRenderDependencyTreeRendersDirectAndTransitiveDependencies
    |> testRenderDependencyTreeMarksPathDependenciesAndSeparatesSiblings
    |> testRenderDependencyTreeCutsCyclesAlongTheSamePath
    |> testRunTreeInProjectRendersDirectDevAndTransitiveDependencies
    |> testRunTreeInProjectFailsWhenManifestMissing
    |> testParseAddArgumentsHelp
    |> testParseAddArgumentsShortHelp
    |> testParseAddArgumentsMissingPackageName
    |> testParseAddArgumentsPlainPackageName
    |> testParseAddArgumentsCapturesPathDevAndProject
    |> testDependencyValueDefaultsToWildcardVersion
    |> testDependencyValueWithPathNormalizesSeparators
    |> testSetJsonObjectFieldUpdatesExistingKeyInPlace
    |> testSetJsonObjectFieldAppendsNewKeyAtEnd
    |> testAddPackageToManifestCreatesFieldWhenMissing
    |> testAddPackageToManifestPreservesOtherDependencyField
    |> testAddPackageToManifestOverwritesExistingPackageEntry
    |> testStringifyIndentedMatchesInitSampleShape
    |> testStringifyIndentedRendersEmptyCollectionsCompactly
    |> testRunAddInProjectCreatesDependenciesFieldWhenMissing
    |> testRunAddInProjectDevPreservesExistingDependencies
    |> testRunAddInProjectWithPathNormalizesSeparators
    |> testRunAddInProjectFailsWhenManifestMissing
    |> testParseRemoveArgumentsHelp
    |> testParseRemoveArgumentsShortHelp
    |> testParseRemoveArgumentsMissingPackageName
    |> testParseRemoveArgumentsPlainPackageName
    |> testParseRemoveArgumentsAcceptsProjectOption
    |> testRemoveObjectFieldRemovesExistingKey
    |> testRemoveObjectFieldReturnsUnchangedWhenKeyMissing
    |> testRemovePackageFromManifestOmitsFieldWhenEmpty
    |> testRemovePackageFromManifestKeepsFieldWithRemainingEntries
    |> testRemovePackageFromManifestChecksDevDependenciesToo
    |> testRemovePackageFromManifestReturnsUnchangedWhenNotADependency
    |> testRunRemoveInProjectOmitsFieldWhenLastDependencyRemoved
    |> testRunRemoveInProjectKeepsOtherDependencies
    |> testRunRemoveInProjectReportsNotADependency
    |> testRunRemoveInProjectFailsWhenManifestMissing
    |> (given (_) -> Ashes.IO.print("all self-hosted cli tests passed"))

run(Unit)
