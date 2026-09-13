import Ashes.Test as test
import Ashes.IO.Path
import AshesCompiler.Frontend.ModulePlan
import AshesCompiler.Frontend.ModuleSource
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.ProjectCompilationPlanning
import AshesCompiler.Semantics.ProjectDiscovery
import AshesCompiler.Semantics.ProjectStitching
import AshesCompiler.Semantics.ProjectSyntaxStitching
import AshesCompiler.Semantics.ShippedModuleStitching
export (
    value runProjectStitchingTests,
)

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

let createDirectory root relativePath =
    relativePath
    |> join(Unix)(root)
    |> Ashes.IO.Directory.createAll
    |> requireUnit("create " + relativePath)

// A project with a sibling module, a path dependency, a shipped selector import, an intrinsic
// whole-module import, a shipped module that itself imports another shipped module, and a
// shipped module the sibling reaches only through a bare qualified reference.
let prepareStitchFixture root =
    root
    |> Ashes.IO.Directory.removeTree
    |> requireUnit("remove stale stitch fixture")
    |> (given (_) -> createDirectory(root)("app/src"))
    |> (given (_) -> createDirectory(root)("helper/src/Helper"))
    |> (given (_) ->
        writeFile(root)("app/ashes.json")(
            "{\"name\":\"stitch-app\",\"entry\":\"src/Main.ash\",\"sourceRoots\":[\"src\"],\"dependencies\":{\"helper\":{\"path\":\"../helper\"}}}"
        ))
    |> (given (_) ->
        writeFile(root)("app/src/Main.ash")(
            "import Ashes.Collection.List.append\nimport Helper.Greet.greeting\nimport Ashes.IO\nimport Util.twice\nAshes.IO.print(greeting + Ashes.Text.fromInt(twice(1)))"
        ))
    |> (given (_) -> writeFile(root)("app/src/Util.ash")("export (value twice)\nlet twice n = n * 2 + Ashes.Collection.Extra.zero"))
    |> (given (_) ->
        writeFile(root)("helper/ashes.json")(
            "{\"name\":\"helper\",\"namespace\":\"Helper\",\"entry\":\"src/Helper.ash\",\"sourceRoots\":[\"src\"]}"
        ))
    |> (given (_) -> writeFile(root)("helper/src/Helper.ash")("export (value unused)\nlet unused = 0"))
    |> (given (_) -> writeFile(root)("helper/src/Helper/Greet.ash")("export (value greeting)\nlet greeting = \"hi\""))

let shippedTexts unit =
    [
        ShippedModuleText(
            moduleName = "Ashes.Collection.List",
            sourcePath = "<shipped>/Collection.List.ash",
            source = "import Ashes.Collection.Count.count\nexport (value append, value length)\nlet recursive append xs ys =\n    match xs with\n        | [] -> ys\n        | head :: tail -> head :: append(tail)(ys)\nlet length xs = count(xs)\n"
        ),
        ShippedModuleText(
            moduleName = "Ashes.Collection.Count",
            sourcePath = "<shipped>/Collection.Count.ash",
            source = "export (value count)\nlet recursive count xs =\n    match xs with\n        | [] -> 0\n        | _ :: tail -> 1 + count(tail)\n"
        ),
        ShippedModuleText(
            moduleName = "Ashes.Collection.Extra",
            sourcePath = "<shipped>/Collection.Extra.ash",
            source = "export (value zero)\nlet zero = 0\n"
        )
    ]

let recursive plannedNames modules =
    match modules with
        | [] -> []
        | PlannedModule { name = name } :: rest -> name :: plannedNames(rest)

let recursive plannedSources modules =
    match modules with
        | [] -> []
        | PlannedModule { name = name, source = ShippedModuleSource(path) } :: rest -> name + " <- shipped " + path :: plannedSources(rest)
        | PlannedModule { name = name, source = ProjectModuleSource(_path) } :: rest -> name + " <- project" :: plannedSources(rest)
        | PlannedModule { name = name, source = InlineModuleSource(_path, _text) } :: rest -> name + " <- inline" :: plannedSources(rest)

let recursive programNames (programs: List(PlannedModuleProgram)) =
    match programs with
        | [] -> []
        | PlannedModuleProgram { name = name } :: rest -> name :: programNames(rest)

let loadFixtureLayout root =
    match "app/ashes.json"
    |> join(Unix)(root)
    |> loadProject(Unix) with
        | Error(_error) -> test.fail("stitch fixture project should load")
        | Ok(layout) -> layout

// Every reachable module is planned dependencies-first in import order, the shipped modules
// (including the one reached only through another shipped module's own import) carry the
// shipped source, the intrinsic module plans without a source, and each planned module has its
// parsed program beside it.
let checkPlanWithShipped root =
    match Unit
    |> shippedTexts
    |> buildProjectCompilationPlanWithShipped(Unix)(loadFixtureLayout(root)) with
        | Error(error) -> test.fail("planning with shipped texts should succeed: " + Ashes.Trait.Show.show(error))
        | Ok(ProjectCompilationPlan { modules = modules, programs = programs }) ->
            modules
            |> plannedSources
            |> test.assertEqual([
                "Ashes.Collection.Count <- shipped <shipped>/Collection.Count.ash",
                "Ashes.Collection.List <- shipped <shipped>/Collection.List.ash",
                "Helper.Greet <- project",
                "Ashes.IO <- shipped <builtin>",
                "Ashes.Collection.Extra <- shipped <shipped>/Collection.Extra.ash",
                "Util <- project",
                "Main <- project"
            ])
            |> (given (_) ->
                programs
                |> programNames
                |> test.assertEqual(["Main", "Ashes.Collection.List", "Ashes.Collection.Count", "Helper.Greet", "Ashes.IO", "Util", "Ashes.Collection.Extra"]))

// Without shipped texts a real shipped import is missing while an intrinsic one still resolves.
let checkPlanWithoutShipped root =
    match root
    |> loadFixtureLayout
    |> buildProjectCompilationPlan(Unix) with
        | Error(ProjectCompilationMissingModule(name, _known)) -> test.assertEqual("Ashes.Collection.List")(name)
        | Error(error) -> test.fail("planning without shipped texts should report the shipped module as missing: " + Ashes.Trait.Show.show(error))
        | Ok(_plan) -> test.fail("planning without shipped texts should not succeed")

let recursive regionSummary (regions: List(StitchedModuleRegion)) =
    match regions with
        | [] -> []
        | StitchedModuleRegion { moduleName = moduleName, packageId = packageId, isEntry = isEntry } :: rest ->
            moduleName + "|" + packageId + (if isEntry
            then "|entry"
            else "") :: regionSummary(rest)

// The stitched project attributes each module to stage 0's package identity (shipped modules to
// `ashes-core`, the dependency's module to the dependency, the project's own modules to its
// manifest name), keeps exactly the entry module as the entry, and carries the entry's body.
let checkStitchedProject root =
    match Unit
    |> shippedTexts
    |> stitchProject(Unix)(loadFixtureLayout(root)) with
        | Error(error) -> test.fail("stitching the fixture project should succeed: " + Ashes.Trait.Show.show(error))
        | Ok(StitchedSyntaxProject { program = ProgramSyntax { body = body }, moduleRegions = regions, entryModuleName = entryModuleName }) ->
            regions
            |> regionSummary
            |> test.assertEqual([
                "Ashes.Collection.Count|ashes-core",
                "Ashes.Collection.List|ashes-core",
                "Helper.Greet|helper",
                "Ashes.IO|ashes-core",
                "Ashes.Collection.Extra|ashes-core",
                "Util|stitch-app",
                "Main|stitch-app|entry"
            ])
            |> (given (_) -> test.assertEqual("Main")(entryModuleName))
            |> (given (_) ->
                match body with
                    | Some(_expression) -> Unit
                    | None -> test.fail("the stitched project should carry the entry module's trailing expression"))

let runProjectStitchingTests unit =
    Unit
    |> Ashes.IO.Environment.temporaryDirectory
    |> requirePath("temporary directory")
    |> (given (temporary) -> join(Unix)(temporary)("ashes-selfhost-project-stitching"))
    |> (given (root) ->
        root
        |> prepareStitchFixture
        |> (given (_) -> root))
    |> (given (root) ->
        root
        |> checkPlanWithShipped
        |> (given (_) -> root))
    |> (given (root) ->
        root
        |> checkPlanWithoutShipped
        |> (given (_) -> root))
    |> (given (root) ->
        root
        |> checkStitchedProject
        |> (given (_) -> root))
    |> (given (root) ->
        root
        |> Ashes.IO.Directory.removeTree
        |> requireUnit("remove stitch fixture"))
