import Ashes.Test as test
import Ashes.IO.Path
import AshesCompiler.Semantics.ProjectDiscovery
let requireUnit name result =
    match result with
        | Ok(Unit) -> Unit
        | Error(error) -> test.fail(name + " failed: " + error)

let requirePath name result =
    match result with
        | Ok(value) -> value
        | Error(_error) -> test.fail(name + " failed")

let expectDiscovered expected result =
    match result with
        | Ok(Some(actual)) ->
            if actual == expected
            then Unit
            else test.fail("unexpected project path: expected " + expected + ", actual " + actual)
        | _ -> test.fail("project should be discovered")

let expectMissing result =
    match result with
        | Ok(None) -> Unit
        | _ -> test.fail("project should not be discovered")

let expectLayout root (result: Result(ProjectDiscoveryError, ProjectLayout)) =
    match result with
        | Ok(layout) ->
            (if layout.projectFilePath == join(Unix)(root)("ashes.json")
            then Unit
            else test.fail("unexpected project file path: " + layout.projectFilePath))
            |> (given (_) ->
                if layout.projectDirectory == root
                then Unit
                else test.fail("unexpected project directory"))
            |> (given (_) ->
                if layout.entryPath == join(Unix)(root)("src/Main.ash")
                then Unit
                else test.fail("unexpected project entry path: " + layout.entryPath))
            |> (given (_) ->
                if layout.entryModuleName == "Main"
                then Unit
                else test.fail("unexpected project entry module"))
            |> (given (_) ->
                if layout.sourceRoots == [join(Unix)(root)("src")]
                then Unit
                else test.fail("unexpected project source roots"))
            |> (given (_) ->
                if layout.includeRoots == [join(Unix)(root)("vendor")]
                then Unit
                else test.fail("unexpected project include roots"))
            |> (given (_) ->
                if layout.outDir == join(Unix)(root)("build")
                then Unit
                else test.fail("unexpected project output directory"))
        | Error(error) -> test.fail("project should load: " + Ashes.Trait.Show.show(error))

let prepareFixture root =
    root
    |> Ashes.IO.Directory.removeTree
    |> requireUnit("remove stale fixture")
    |> (given (_) ->
        "src/nested"
        |> join(Unix)(root)
        |> Ashes.IO.Directory.createAll
        |> requireUnit("create source tree"))
    |> (given (_) ->
        "vendor"
        |> join(Unix)(root)
        |> Ashes.IO.Directory.createAll
        |> requireUnit("create include root"))
    |> (given (_) ->
        "Unit"
        |> Ashes.IO.File.writeText(join(Unix)(root)("src/Main.ash"))
        |> requireUnit("write entry"))
    |> (given (_) ->
        "{\"entry\":\"src/Main.ash\",\"sourceRoots\":[\"src\"],\"include\":[\"vendor\"],\"outDir\":\"build\"}"
        |> Ashes.IO.File.writeText(join(Unix)(root)("ashes.json"))
        |> requireUnit("write manifest"))

let checkDiscovery root =
    "src/nested"
    |> join(Unix)(root)
    |> discoverProjectFile(Unix)
    |> expectDiscovered(join(Unix)(root)("ashes.json"))
    |> (given (_) ->
        "ashes-selfhost-no-project/nested"
        |> join(Unix)(parent(Unix)(root))
        |> discoverProjectFile(Unix)
        |> expectMissing)

let checkExplicitSelection root =
    Some("../ashes.json")
    |> selectProjectFile(Unix)(join(Unix)(root)("src"))
    |> expectDiscovered(join(Unix)(root)("ashes.json"))

let checkLoading root =
    "ashes.json"
    |> join(Unix)(root)
    |> loadProject(Unix)
    |> expectLayout(root)

let runProjectDiscoveryTests unit =
    Unit
    |> Ashes.IO.Environment.temporaryDirectory
    |> requirePath("temporary directory")
    |> (given (temporary) -> join(Unix)(temporary)("ashes-selfhost-project-discovery"))
    |> (given (root) ->
        root
        |> prepareFixture
        |> (given (_) -> root))
    |> (given (root) ->
        root
        |> checkDiscovery
        |> (given (_) -> root))
    |> (given (root) ->
        root
        |> checkExplicitSelection
        |> (given (_) -> root))
    |> (given (root) ->
        root
        |> checkLoading
        |> (given (_) -> root))
    |> (given (root) ->
        root
        |> Ashes.IO.Directory.removeTree
        |> requireUnit("remove fixture"))
