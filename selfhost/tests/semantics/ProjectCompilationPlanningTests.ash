import Ashes.Test as test
import Ashes.IO.Path
import AshesCompiler.Frontend.ImportResolution
import AshesCompiler.Frontend.ModulePlan
import AshesCompiler.Semantics.ProjectCompilationPlanning
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
        | PlannedModule { name = name, source = _source, imports = _imports, interface = _interface } :: rest -> name :: plannedNames(rest)

let recursive plannedMainImports modules =
    match modules with
        | [] -> []
        | PlannedModule { name = "Main", source = _source, imports = imports, interface = _interface } :: _rest -> imports
        | _module :: rest -> plannedMainImports(rest)

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
    |> (given (_) -> writeFile(root)("src/Main.ash")("import Util.value\nvalue"))
    |> (given (_) -> writeFile(root)("src/Util.ash")("export (value value)\nlet value = 42"))
    |> (given (_) -> writeFile(root)("src/Unused.ash")("let = invalid"))

let expectReachablePlan result =
    match result with
        | Error(error) -> test.fail("reachable source planning should succeed: " + Ashes.Trait.Show.show(error))
        | Ok(plan) ->
            plan.modules
            |> plannedMainImports
            |> test.assertEqual([ResolvedValueImport("Util")("value")("value")(1)("import Util.value")])
            |> (given (_) ->
                plan.modules
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
        |> prepareAmbiguousFixture
        |> (given (_) -> root))
    |> (given (root) ->
        root
        |> checkAmbiguousPlanning
        |> (given (_) -> root))
    |> (given (root) ->
        root
        |> Ashes.IO.Directory.removeTree
        |> requireUnit("remove fixture"))
