import Ashes.Test as test
import Ashes.IO.Path
import AshesCompiler.Frontend.ImportResolution
import AshesCompiler.Frontend.ModulePlan
import AshesCompiler.Semantics.ProjectCompilationPlanning
import AshesCompiler.Semantics.ProjectDiagnostics
import AshesCompiler.Semantics.ProjectDiscovery
let requireUnit name result =
    match result with
        | Ok(Unit) -> Unit
        | Error(error) -> test.fail(name + " failed: " + error)

let requirePath name result =
    match result with
        | Ok(value) -> value
        | Error(_error) -> test.fail(name + " failed")

let writeFile root relativePath contents =
    contents
    |> Ashes.IO.File.writeText(join(Unix)(root)(relativePath))
    |> requireUnit("write " + relativePath)

let recursive plannedNames modules =
    match modules with
        | [] -> []
        | PlannedModule { name = name, source = _source, imports = _imports, interface = _interface } :: rest ->
            name :: plannedNames(
                rest
            )

let recursive plannedMainImports modules =
    match modules with
        | [] -> []
        | PlannedModule { name = "Main", imports = imports } :: _rest -> imports
        | _module :: rest -> plannedMainImports(rest)

let recursive plannedInlineSource moduleName modules =
    match modules with
        | [] -> None
        | PlannedModule { name = name, source = InlineModuleSource(path, text) } :: rest ->
            if name == moduleName
            then Some((path, text))
            else plannedInlineSource(moduleName)(rest)
        | _module :: rest -> plannedInlineSource(moduleName)(rest)

let expectedInlineSource root =
    (let path = join(Unix)(root)("src/Geom.ash") + "#Geom.Vec"
    in Some((path, "export (value answer)\nlet answer = 42")))

let prepareReachableFixture root =
    root
    |> Ashes.IO.Directory.removeTree
    |> requireUnit("remove stale reachable fixture")
    |> (given (_) ->
        "src"
        |> join(Unix)(root)
        |> Ashes.IO.Directory.createAll
        |> requireUnit("create reachable source root"))
    |> (given (_) -> writeFile(root)("ashes.json")("{\"entry\":\"src/Main.ash\",\"sourceRoots\":[\"src\"]}"))
    |> (given (_) -> writeFile(root)("src/Main.ash")("import Util.value\nimport Util.Marker\nvalue"))
    |> (given (_) ->
        writeFile(root)("src/Util.ash")(
            "export (value value, type Marker)\ntype Marker = | Marker\nlet value = 42"
        ))
    |> (given (_) -> writeFile(root)("src/Unused.ash")("let = invalid"))

let expectReachablePlan (result: Result(ProjectCompilationError, ProjectCompilationPlan)) =
    match result with
        | Error(error) -> test.fail("reachable source planning should succeed: " + Ashes.Trait.Show.show(error))
        | Ok(ProjectCompilationPlan { sourceFiles = _sourceFiles, modules = modules }) ->
            modules
            |> plannedMainImports
            |> test.assertEqual([
                ResolvedValueImport("Util")("value")("value")(1)("import Util.value"),
                ResolvedTypeImport("Util")("Marker")("Marker")(2)("import Util.Marker")
            ])
            |> (given (_) ->
                modules
                |> plannedNames
                |> test.assertEqual(["Util", "Main"]))

let checkReachablePlanning root =
    "ashes.json"
    |> join(Unix)(root)
    |> loadProject(Unix)
    |> (given (loaded) ->
        match loaded with
            | Error(_error) -> test.fail("reachable project should load")
            | Ok(layout) -> buildProjectCompilationPlan(Unix)(layout))
    |> expectReachablePlan

let prepareInlineFixture root =
    root
    |> Ashes.IO.Directory.removeTree
    |> requireUnit("remove stale inline fixture")
    |> (given (_) ->
        "src"
        |> join(Unix)(root)
        |> Ashes.IO.Directory.createAll
        |> requireUnit("create inline source root"))
    |> (given (_) -> writeFile(root)("ashes.json")("{\"entry\":\"src/Main.ash\",\"sourceRoots\":[\"src\"]}"))
    |> (given (_) ->
        writeFile(root)("src/Main.ash")(
            "import Geom.Vec as V\nimport Geom.Vec.answer as answer\nanswer"
        ))
    |> (given (_) ->
        writeFile(root)("src/Geom.ash")(
            "export (module Vec)\nmodule Vec =\n    export (value answer)\n    let answer = 42"
        ))

let expectInlinePlan root (result: Result(ProjectCompilationError, ProjectCompilationPlan)) =
    match result with
        | Error(error) -> test.fail("inline source planning should succeed: " + Ashes.Trait.Show.show(error))
        | Ok(ProjectCompilationPlan { sourceFiles = _sourceFiles, modules = modules }) ->
            modules
            |> plannedMainImports
            |> test.assertEqual(
                [
                    ResolvedModuleImport("Geom.Vec")(Some("V"))(1)("import Geom.Vec as V"),
                    ResolvedValueImport("Geom.Vec")("answer")("answer")(2)("import Geom.Vec.answer as answer")
                ]
            )
            |> (given (_) ->
                modules
                |> plannedNames
                |> test.assertEqual(["Geom.Vec", "Main"]))
            |> (given (_) ->
                modules
                |> plannedInlineSource("Geom.Vec")
                |> test.assertEqual(expectedInlineSource(root)))

let checkInlinePlanning root =
    "ashes.json"
    |> join(Unix)(root)
    |> loadProject(Unix)
    |> (given (loaded) ->
        match loaded with
            | Error(_error) -> test.fail("inline project should load")
            | Ok(layout) -> buildProjectCompilationPlan(Unix)(layout))
    |> expectInlinePlan(root)

let prepareHiddenInlineFixture root =
    root
    |> Ashes.IO.Directory.removeTree
    |> requireUnit("remove stale hidden inline fixture")
    |> (given (_) ->
        "src"
        |> join(Unix)(root)
        |> Ashes.IO.Directory.createAll
        |> requireUnit("create hidden inline source root"))
    |> (given (_) -> writeFile(root)("ashes.json")("{\"entry\":\"src/Main.ash\",\"sourceRoots\":[\"src\"]}"))
    |> (given (_) -> writeFile(root)("src/Main.ash")("import Geom.Vec\n0"))
    |> (given (_) ->
        writeFile(root)("src/Geom.ash")(
            "export (value visible)\nlet visible = 1\nmodule Vec =\n    let answer = 42"
        ))

let expectHiddenInlineError result =
    match result with
        | Error(ProjectCompilationModulePlanError(UnexportedNestedModule("Main", "Geom", "Vec"))) -> Unit
        | Error(error) -> test.fail("unexpected hidden inline error: " + Ashes.Trait.Show.show(error))
        | Ok(_) -> test.fail("hidden inline module should not be importable")

let checkHiddenInlinePlanning root =
    "ashes.json"
    |> join(Unix)(root)
    |> loadProject(Unix)
    |> (given (loaded) ->
        match loaded with
            | Error(_error) -> test.fail("hidden inline project should load")
            | Ok(layout) -> buildProjectCompilationPlan(Unix)(layout))
    |> expectHiddenInlineError

let prepareInlineCollisionFixture root =
    root
    |> Ashes.IO.Directory.removeTree
    |> requireUnit("remove stale inline collision fixture")
    |> (given (_) ->
        "src/Geom"
        |> join(Unix)(root)
        |> Ashes.IO.Directory.createAll
        |> requireUnit("create inline collision source root"))
    |> (given (_) -> writeFile(root)("ashes.json")("{\"entry\":\"src/Main.ash\",\"sourceRoots\":[\"src\"]}"))
    |> (given (_) -> writeFile(root)("src/Main.ash")("import Geom\n0"))
    |> (given (_) -> writeFile(root)("src/Geom.ash")("module Vec =\n    let inlineValue = 1"))
    |> (given (_) -> writeFile(root)("src/Geom/Vec.ash")("let fileValue = 2"))

let expectInlineCollision result =
    match result with
        | Error(ProjectCompilationInlineFileCollision("Geom.Vec", _path)) -> Unit
        | Error(error) -> test.fail("unexpected inline collision error: " + Ashes.Trait.Show.show(error))
        | Ok(_) -> test.fail("inline and file module collision should fail")

let checkInlineCollisionPlanning root =
    "ashes.json"
    |> join(Unix)(root)
    |> loadProject(Unix)
    |> (given (loaded) ->
        match loaded with
            | Error(_error) -> test.fail("inline collision project should load")
            | Ok(layout) -> buildProjectCompilationPlan(Unix)(layout))
    |> expectInlineCollision

let prepareEntryInlineFixture root =
    root
    |> Ashes.IO.Directory.removeTree
    |> requireUnit("remove stale entry inline fixture")
    |> (given (_) ->
        "src"
        |> join(Unix)(root)
        |> Ashes.IO.Directory.createAll
        |> requireUnit("create entry inline source root"))
    |> (given (_) -> writeFile(root)("ashes.json")("{\"entry\":\"src/Main.ash\",\"sourceRoots\":[\"src\"]}"))
    |> (given (_) ->
        writeFile(root)("src/Main.ash")(
            "module Outer =\n    module Inner =\n        let value = 1\n    let outer = 2\n0"
        ))

let expectEntryInlinePlan result =
    match result with
        | Error(error) -> test.fail("entry inline planning should succeed: " + Ashes.Trait.Show.show(error))
        | Ok(ProjectCompilationPlan { sourceFiles = _sourceFiles, modules = modules }) ->
            modules
            |> plannedNames
            |> test.assertEqual(["Outer.Inner", "Outer", "Main"])

let checkEntryInlinePlanning root =
    "ashes.json"
    |> join(Unix)(root)
    |> loadProject(Unix)
    |> (given (loaded) ->
        match loaded with
            | Error(_error) -> test.fail("entry inline project should load")
            | Ok(layout) -> buildProjectCompilationPlan(Unix)(layout))
    |> expectEntryInlinePlan

let prepareAmbiguousFixture root =
    root
    |> Ashes.IO.Directory.removeTree
    |> requireUnit("remove stale ambiguous fixture")
    |> (given (_) ->
        "src"
        |> join(Unix)(root)
        |> Ashes.IO.Directory.createAll
        |> requireUnit("create primary source root"))
    |> (given (_) ->
        "vendor"
        |> join(Unix)(root)
        |> Ashes.IO.Directory.createAll
        |> requireUnit("create secondary source root"))
    |> (given (_) -> writeFile(root)("ashes.json")("{\"entry\":\"src/Main.ash\",\"sourceRoots\":[\"src\",\"vendor\"]}"))
    |> (given (_) -> writeFile(root)("src/Main.ash")("import Util\n0"))
    |> (given (_) -> writeFile(root)("src/Util.ash")("let value = 1"))
    |> (given (_) -> writeFile(root)("vendor/Util.ash")("let value = 2"))

let checkAmbiguousPlanning root =
    "ashes.json"
    |> join(Unix)(root)
    |> loadProject(Unix)
    |> (given (loaded) ->
        match loaded with
            | Error(_error) -> test.fail("ambiguous project should load")
            | Ok(layout) -> buildProjectCompilationPlan(Unix)(layout))
    |> (given (result) ->
        match result with
            | Error(ProjectCompilationAmbiguousModule("Util", _paths)) -> Unit
            | Error(_) -> test.fail("unexpected ambiguous-module error")
            | Ok(_) -> test.fail("ambiguous module should fail planning"))

let prepareDiagnosticFixture root =
    root
    |> Ashes.IO.Directory.removeTree
    |> requireUnit("remove stale diagnostic fixture")
    |> (given (_) ->
        "src"
        |> join(Unix)(root)
        |> Ashes.IO.Directory.createAll
        |> requireUnit("create diagnostic source root"))
    |> (given (_) -> writeFile(root)("ashes.json")("{\"entry\":\"src/Main.ash\",\"sourceRoots\":[\"src\"]}"))
    |> (given (_) -> writeFile(root)("src/Main.ash")("import First\nimport Second\n0"))
    |> (given (_) -> writeFile(root)("src/First.ash")("let = first"))
    |> (given (_) -> writeFile(root)("src/Second.ash")("let = second"))

let recursive projectDiagnosticPaths diagnostics =
    match diagnostics with
        | [] -> []
        | ProjectDiagnostic { sourcePath = path } :: rest -> path :: projectDiagnosticPaths(rest)

let expectedDiagnosticPaths root =
    [
        join(Unix)(root)("src/First.ash"),
        join(Unix)(root)("src/Second.ash")
    ]

let expectOrderedParseDiagnostics root result =
    match result with
        | Error(ProjectCompilationParseError(diagnostics)) ->
            diagnostics
            |> projectDiagnosticPaths
            |> test.assertEqual(expectedDiagnosticPaths(root))
        | Error(error) -> test.fail("unexpected project parse error: " + Ashes.Trait.Show.show(error))
        | Ok(_) -> test.fail("reachable parse diagnostics should fail project planning")

let checkDiagnosticPlanning root =
    "ashes.json"
    |> join(Unix)(root)
    |> loadProject(Unix)
    |> (given (loaded) ->
        match loaded with
            | Error(_error) -> test.fail("diagnostic project should load")
            | Ok(layout) -> buildProjectCompilationPlan(Unix)(layout))
    |> expectOrderedParseDiagnostics(root)

let runProjectCompilationPlanningTests unit =
    Unit
    |> Ashes.IO.Environment.temporaryDirectory
    |> requirePath("temporary directory")
    |> (given (temporary) -> join(Unix)(temporary)("ashes-selfhost-project-planning"))
    |> (given (root) ->
        root
        |> prepareReachableFixture
        |> (given (_) -> root))
    |> (given (root) ->
        root
        |> checkReachablePlanning
        |> (given (_) -> root))
    |> (given (root) ->
        root
        |> prepareInlineFixture
        |> (given (_) -> root))
    |> (given (root) ->
        root
        |> checkInlinePlanning
        |> (given (_) -> root))
    |> (given (root) ->
        root
        |> prepareHiddenInlineFixture
        |> (given (_) -> root))
    |> (given (root) ->
        root
        |> checkHiddenInlinePlanning
        |> (given (_) -> root))
    |> (given (root) ->
        root
        |> prepareInlineCollisionFixture
        |> (given (_) -> root))
    |> (given (root) ->
        root
        |> checkInlineCollisionPlanning
        |> (given (_) -> root))
    |> (given (root) ->
        root
        |> prepareEntryInlineFixture
        |> (given (_) -> root))
    |> (given (root) ->
        root
        |> checkEntryInlinePlanning
        |> (given (_) -> root))
    |> (given (root) ->
        root
        |> prepareAmbiguousFixture
        |> (given (_) -> root))
    |> (given (root) ->
        root
        |> checkAmbiguousPlanning
        |> (given (_) -> root))
    |> (given (root) ->
        root
        |> prepareDiagnosticFixture
        |> (given (_) -> root))
    |> (given (root) ->
        root
        |> checkDiagnosticPlanning
        |> (given (_) -> root))
    |> (given (root) ->
        root
        |> Ashes.IO.Directory.removeTree
        |> requireUnit("remove fixture"))
