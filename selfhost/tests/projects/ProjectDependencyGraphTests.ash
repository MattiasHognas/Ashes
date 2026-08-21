import Ashes.Test as test
import Ashes.IO.Path
import AshesCompiler.Frontend.ModulePlan
import AshesCompiler.Semantics.ProjectCompilationPlanning
import AshesCompiler.Semantics.ProjectDependencyGraph
import AshesCompiler.Semantics.ProjectDiscovery
let requireUnit (name: Str) result =
    match result with
        | Ok(Unit) -> Unit
        | Error(error) -> test.fail(name + " failed: " + error)

let requirePath (name: Str) result =
    match result with
        | Ok(path) -> path
        | Error(_error) -> test.fail(name + " failed")

let writeFile (root: Str) (relativePath: Str) (contents: Str) =
    contents
    |> Ashes.IO.File.writeText(join(Unix)(root)(relativePath))
    |> requireUnit("write " + relativePath)

let createDirectory (root: Str) (relativePath: Str) =
    relativePath
    |> join(Unix)(root)
    |> Ashes.IO.Directory.createAll
    |> requireUnit("create " + relativePath)

let recursive dependencySummary (dependencies: List(ResolvedProjectDependency)) =
    match dependencies with
        | [] -> []
        | ResolvedProjectDependency { name = name, namespace = namespace, sourceRoots = _sourceRoots, projectDirectory = _projectDirectory, entryPath = _entryPath, isDev = isDev } :: rest ->
            if isDev
            then name + "|" + namespace + "|dev" :: dependencySummary(rest)
            else name + "|" + namespace + "|normal" :: dependencySummary(rest)

let recursive plannedNames modules =
    match modules with
        | [] -> []
        | PlannedModule { name = name, source = _source, imports = _imports, interface = _interface } :: rest ->
            name :: plannedNames(
                rest
            )

let typedGraphResult (result: Result(ProjectDependencyGraphError, ProjectDependencyGraph)) = result

let graphErrorText (error: ProjectDependencyGraphError) = Ashes.Trait.Show.show(error)

let loadGraph (manifestPath: Str) =
    match loadProject(Unix)(manifestPath) with
        | Error(_error) -> test.fail("root project should load")
        | Ok(layout) ->
            layout
            |> resolveProjectDependencyGraph(Unix)
            |> typedGraphResult

let loadGraphFromCache cacheRoot manifestPath =
    match loadProject(Unix)(manifestPath) with
        | Error(_error) -> test.fail("root project should load")
        | Ok(layout) ->
            layout
            |> resolveProjectDependencyGraphFromCache(Unix)(cacheRoot)
            |> typedGraphResult

let prepareResolvedGraph root =
    root
    |> Ashes.IO.Directory.removeTree
    |> requireUnit("remove stale resolved graph")
    |> (given (_) -> createDirectory(root)("app/src"))
    |> (given (_) -> createDirectory(root)("mid/src/Mid"))
    |> (given (_) -> createDirectory(root)("base/src/Base"))
    |> (given (_) -> createDirectory(root)("helper/src/Testing"))
    |> (given (_) ->
        writeFile(
            root,
            "app/src/Main.ash",
            "import Mid.Library\nimport Base.Library\nimport Testing.Library\n0"
        ))
    |> (given (_) -> writeFile(root)("mid/src/Mid.ash")("0"))
    |> (given (_) -> writeFile(root)("mid/src/Mid/Library.ash")("let value = 1"))
    |> (given (_) -> writeFile(root)("base/src/Base.ash")("0"))
    |> (given (_) -> writeFile(root)("base/src/Base/Library.ash")("let value = 2"))
    |> (given (_) -> writeFile(root)("helper/src/Testing.ash")("0"))
    |> (given (_) -> writeFile(root)("helper/src/Testing/Library.ash")("let value = 3"))
    |> (given (_) ->
        writeFile(
            root,
            "base/ashes.json",
            "{\"name\":\"base\",\"entry\":\"src/Base.ash\",\"sourceRoots\":[\"src\"]}"
        ))
    |> (given (_) ->
        writeFile(
            root,
            "mid/ashes.json",
            "{\"name\":\"mid\",\"entry\":\"src/Mid.ash\",\"sourceRoots\":[\"src\"],\"dependencies\":{\"base\":{\"path\":\"../base\"}}}"
        ))
    |> (given (_) ->
        writeFile(
            root,
            "helper/ashes.json",
            "{\"name\":\"helper\",\"namespace\":\"Testing\",\"entry\":\"src/Testing.ash\",\"sourceRoots\":[\"src\"]}"
        ))
    |> (given (_) ->
        writeFile(
            root,
            "app/ashes.json",
            "{\"entry\":\"src/Main.ash\",\"sourceRoots\":[\"src\"],\"dependencies\":{\"middle-package\":{\"path\":\"../mid\",\"namespace\":\"Mid\"}},\"devDependencies\":{\"helper\":{\"path\":\"../helper\"}}}"
        ))

let checkResolvedGraph root =
    "app/ashes.json"
    |> join(Unix)(root)
    |> loadGraph
    |> (given (result) ->
        match result with
            | Error(error) -> test.fail("path graph should resolve: " + graphErrorText(error))
            | Ok(ProjectDependencyGraph { dependencies = dependencies }) ->
                dependencies
                |> dependencySummary
                |> test.assertEqual(["middle-package|Mid|normal", "base|Base|normal", "helper|Testing|dev"]))

let checkCompilationPlanResult result =
    match result with
        | Error(error) -> test.fail("path dependency planning should succeed: " + Ashes.Trait.Show.show(error))
        | Ok(ProjectCompilationPlan { sourceFiles = _sourceFiles, modules = modules }) ->
            modules
            |> plannedNames
            |> test.assertEqual(["Mid.Library", "Base.Library", "Testing.Library", "Main"])

let continueCompilationPlan loaded =
    match loaded with
        | Error(_error) -> test.fail("planning root project should load")
        | Ok(layout) -> buildProjectCompilationPlan(Unix)(layout)

let checkCompilationPlan root =
    "app/ashes.json"
    |> join(Unix)(root)
    |> loadProject(Unix)
    |> continueCompilationPlan
    |> checkCompilationPlanResult

let prepareDottedDependencyNamespace root =
    root
    |> Ashes.IO.Directory.removeTree
    |> requireUnit("remove stale dotted dependency namespace")
    |> (given (_) -> createDirectory(root)("app/src"))
    |> (given (_) -> createDirectory(root)("dep/src/AshesCompiler/Frontend"))
    |> (given (_) ->
        writeFile(
            root,
            "app/src/Main.ash",
            "import AshesCompiler.Frontend.Value\nAshesCompiler.Frontend.Value.value"
        ))
    |> (given (_) -> writeFile(root)("dep/Package.ash")("Unit"))
    |> (given (_) -> writeFile(root)("dep/src/AshesCompiler/Frontend/Value.ash")("let value = 42"))
    |> (given (_) -> writeFile(root)("dep/ashes.json")("{\"entry\":\"Package.ash\",\"sourceRoots\":[\"src\"]}"))
    |> (given (_) ->
        writeFile(
            root,
            "app/ashes.json",
            "{\"entry\":\"src/Main.ash\",\"dependencies\":{\"ashes-compiler.frontend\":{\"path\":\"../dep\"}}}"
        ))

let checkDottedDependencyNamespace root =
    "app/ashes.json"
    |> join(Unix)(root)
    |> loadGraph
    |> (given (result) ->
        match result with
            | Error(error) -> test.fail("dotted dependency namespace should resolve: " + graphErrorText(error))
            | Ok(ProjectDependencyGraph { dependencies = dependencies }) ->
                dependencies
                |> dependencySummary
                |> test.assertEqual(["ashes-compiler.frontend|AshesCompiler.Frontend|normal"]))

let prepareMissingPath root =
    root
    |> Ashes.IO.Directory.removeTree
    |> requireUnit("remove stale missing path")
    |> (given (_) -> createDirectory(root)("app/src"))
    |> (given (_) -> writeFile(root)("app/src/Main.ash")("0"))
    |> (given (_) ->
        writeFile(
            root,
            "app/ashes.json",
            "{\"entry\":\"src/Main.ash\",\"dependencies\":{\"ghost\":{\"path\":\"../ghost\"}}}"
        ))

let checkMissingPath root =
    "app/ashes.json"
    |> join(Unix)(root)
    |> loadGraph
    |> (given (result) ->
        match result with
            | Error(ProjectDependencyPathNotFound("ghost", _path)) -> Unit
            | Error(_error) -> test.fail("unexpected missing-path result")
            | Ok(_) -> test.fail("missing dependency path should fail"))

let prepareMissingManifest root =
    root
    |> Ashes.IO.Directory.removeTree
    |> requireUnit("remove stale missing manifest")
    |> (given (_) -> createDirectory(root)("app/src"))
    |> (given (_) -> createDirectory(root)("empty"))
    |> (given (_) -> writeFile(root)("app/src/Main.ash")("0"))
    |> (given (_) ->
        writeFile(
            root,
            "app/ashes.json",
            "{\"entry\":\"src/Main.ash\",\"dependencies\":{\"empty\":{\"path\":\"../empty\"}}}"
        ))

let checkMissingManifest root =
    "app/ashes.json"
    |> join(Unix)(root)
    |> loadGraph
    |> (given (result) ->
        match result with
            | Error(ProjectDependencyManifestNotFound("empty", _path)) -> Unit
            | Error(error) -> test.fail("unexpected missing-manifest result: " + graphErrorText(error))
            | Ok(_) -> test.fail("missing dependency manifest should fail"))

let prepareDiamond root =
    root
    |> Ashes.IO.Directory.removeTree
    |> requireUnit("remove stale diamond")
    |> (given (_) -> createDirectory(root)("app/src"))
    |> (given (_) -> createDirectory(root)("left/src"))
    |> (given (_) -> createDirectory(root)("right/src"))
    |> (given (_) -> createDirectory(root)("shared/src"))
    |> (given (_) -> writeFile(root)("app/src/Main.ash")("0"))
    |> (given (_) -> writeFile(root)("left/src/Left.ash")("0"))
    |> (given (_) -> writeFile(root)("right/src/Right.ash")("0"))
    |> (given (_) -> writeFile(root)("shared/src/Shared.ash")("0"))
    |> (given (_) -> writeFile(root)("shared/ashes.json")("{\"entry\":\"src/Shared.ash\"}"))
    |> (given (_) ->
        writeFile(
            root,
            "left/ashes.json",
            "{\"entry\":\"src/Left.ash\",\"dependencies\":{\"shared\":{\"path\":\"../shared\"}}}"
        ))
    |> (given (_) ->
        writeFile(
            root,
            "right/ashes.json",
            "{\"entry\":\"src/Right.ash\",\"dependencies\":{\"shared\":{\"path\":\"../shared\"}}}"
        ))
    |> (given (_) ->
        writeFile(
            root,
            "app/ashes.json",
            "{\"entry\":\"src/Main.ash\",\"dependencies\":{\"left\":{\"path\":\"../left\"},\"right\":{\"path\":\"../right\"}}}"
        ))

let checkDiamond root =
    "app/ashes.json"
    |> join(Unix)(root)
    |> loadGraph
    |> (given (result) ->
        match result with
            | Error(error) -> test.fail("diamond graph should resolve: " + graphErrorText(error))
            | Ok(ProjectDependencyGraph { dependencies = dependencies }) ->
                dependencies
                |> dependencySummary
                |> test.assertEqual(["left|Left|normal", "shared|Shared|normal", "right|Right|normal"]))

let prepareCycle root =
    root
    |> Ashes.IO.Directory.removeTree
    |> requireUnit("remove stale cycle")
    |> (given (_) -> createDirectory(root)("a/src"))
    |> (given (_) -> createDirectory(root)("b/src"))
    |> (given (_) -> writeFile(root)("a/src/A.ash")("0"))
    |> (given (_) -> writeFile(root)("b/src/B.ash")("0"))
    |> (given (_) ->
        writeFile(
            root,
            "a/ashes.json",
            "{\"entry\":\"src/A.ash\",\"dependencies\":{\"b\":{\"path\":\"../b\"}}}"
        ))
    |> (given (_) ->
        writeFile(
            root,
            "b/ashes.json",
            "{\"entry\":\"src/B.ash\",\"dependencies\":{\"a\":{\"path\":\"../a\"}}}"
        ))

let checkCycle root =
    "a/ashes.json"
    |> join(Unix)(root)
    |> loadGraph
    |> (given (result) ->
        match result with
            | Error(ProjectDependencyCycle("b", _path)) -> Unit
            | Error(_error) -> test.fail("unexpected cycle result")
            | Ok(_) -> test.fail("dependency cycle should fail"))

let prepareNamespaceConflict root =
    root
    |> Ashes.IO.Directory.removeTree
    |> requireUnit("remove stale namespace conflict")
    |> (given (_) -> createDirectory(root)("app/src"))
    |> (given (_) -> createDirectory(root)("one/src/Shared"))
    |> (given (_) -> createDirectory(root)("two/src/Shared"))
    |> (given (_) -> writeFile(root)("app/src/Main.ash")("0"))
    |> (given (_) -> writeFile(root)("one/src/One.ash")("0"))
    |> (given (_) -> writeFile(root)("one/src/Shared/One.ash")("0"))
    |> (given (_) -> writeFile(root)("two/src/Two.ash")("0"))
    |> (given (_) -> writeFile(root)("two/src/Shared/Two.ash")("0"))
    |> (given (_) ->
        writeFile(
            root,
            "one/ashes.json",
            "{\"namespace\":\"Shared\",\"entry\":\"src/One.ash\",\"sourceRoots\":[\"src\"]}"
        ))
    |> (given (_) ->
        writeFile(
            root,
            "two/ashes.json",
            "{\"namespace\":\"Shared\",\"entry\":\"src/Two.ash\",\"sourceRoots\":[\"src\"]}"
        ))
    |> (given (_) ->
        writeFile(
            root,
            "app/ashes.json",
            "{\"entry\":\"src/Main.ash\",\"dependencies\":{\"one\":{\"path\":\"../one\"},\"two\":{\"path\":\"../two\"}}}"
        ))

let checkNamespaceConflict root =
    "app/ashes.json"
    |> join(Unix)(root)
    |> loadGraph
    |> (given (result) ->
        match result with
            | Error(ProjectDependencyNamespaceConflict("Shared", _first, _second)) -> Unit
            | Error(_error) -> test.fail("unexpected namespace result")
            | Ok(_) -> test.fail("duplicate dependency namespace should fail"))

let prepareOutsideNamespace root =
    root
    |> Ashes.IO.Directory.removeTree
    |> requireUnit("remove stale namespace violation")
    |> (given (_) -> createDirectory(root)("app/src"))
    |> (given (_) -> createDirectory(root)("dep/src/Greet"))
    |> (given (_) -> writeFile(root)("app/src/Main.ash")("0"))
    |> (given (_) -> writeFile(root)("dep/src/Greet.ash")("0"))
    |> (given (_) -> writeFile(root)("dep/src/Stray.ash")("let value = 1"))
    |> (given (_) ->
        writeFile(
            root,
            "dep/ashes.json",
            "{\"name\":\"greet\",\"entry\":\"src/Greet.ash\",\"sourceRoots\":[\"src\"]}"
        ))
    |> (given (_) ->
        writeFile(
            root,
            "app/ashes.json",
            "{\"entry\":\"src/Main.ash\",\"dependencies\":{\"greet\":{\"path\":\"../dep\"}}}"
        ))

let checkOutsideNamespace root =
    "app/ashes.json"
    |> join(Unix)(root)
    |> loadGraph
    |> (given (result) ->
        match result with
            | Error(ProjectDependencyModuleOutsideNamespace("greet", "Stray", "Greet")) -> Unit
            | Error(_error) -> test.fail("unexpected module namespace result")
            | Ok(_) -> test.fail("module outside dependency namespace should fail"))

let prepareLockedPackage root =
    root
    |> Ashes.IO.Directory.removeTree
    |> requireUnit("remove stale locked package")
    |> (given (_) -> createDirectory(root)("app/src"))
    |> (given (_) -> createDirectory(root)("cache/pkg/Json/1.2.3/deadbeef/src/Json"))
    |> (given (_) -> writeFile(root)("app/src/Main.ash")("import Json.Codec\nJson.Codec.value"))
    |> (given (_) ->
        writeFile(
            root,
            "app/ashes-test.json",
            "{\"entry\":\"src/Main.ash\",\"dependencies\":{\"json\":\"^1.2.0\"}}"
        ))
    |> (given (_) ->
        writeFile(
            root,
            "app/ashes-test.lock",
            "{\"version\":1,\"package\":[{\"namespace\":\"Json\",\"version\":\"1.2.3\",\"source\":\"registry+https://pkg.example\",\"hash\":\"ash1:deadbeef\",\"dependencies\":[]}]}"
        ))
    |> (given (_) ->
        writeFile(
            root,
            "cache/pkg/Json/1.2.3/deadbeef/ashes.json",
            "{\"name\":\"ignored-manifest-name\",\"entry\":\"src/Json.ash\",\"sourceRoots\":[\"src\"]}"
        ))
    |> (given (_) -> writeFile(root)("cache/pkg/Json/1.2.3/deadbeef/src/Json.ash")("0"))
    |> (given (_) -> writeFile(root)("cache/pkg/Json/1.2.3/deadbeef/src/Json/Codec.ash")("let value = 42"))

let checkLockedPackage root =
    "app/ashes-test.json"
    |> join(Unix)(root)
    |> loadGraphFromCache(join(Unix)(root)("cache"))
    |> (given (result) ->
        match result with
            | Error(error) -> test.fail("locked package should resolve: " + graphErrorText(error))
            | Ok(ProjectDependencyGraph { dependencies = dependencies }) ->
                let packageDirectory = join(Unix)(root)("cache/pkg/Json/1.2.3/deadbeef")
                in
                    test.assertEqual(
                        [ResolvedProjectDependency(name = "Json", namespace = "Json", sourceRoots = [join(
                            Unix,
                            packageDirectory,
                            "src"
                        )], projectDirectory = packageDirectory, entryPath = join(
                            Unix,
                            packageDirectory,
                            "src/Json.ash"
                        ), isDev = false)],
                        dependencies
                    ))

let prepareMissingLockedPackage root =
    root
    |> Ashes.IO.Directory.removeTree
    |> requireUnit("remove stale missing locked package")
    |> (given (_) -> createDirectory(root)("app/src"))
    |> (given (_) -> writeFile(root)("app/src/Main.ash")("0"))
    |> (given (_) -> writeFile(root)("app/ashes.json")("{\"entry\":\"src/Main.ash\"}"))
    |> (given (_) ->
        writeFile(
            root,
            "app/ashes.lock",
            "{\"version\":1,\"package\":[{\"namespace\":\"Missing\",\"version\":\"2.0.0\",\"source\":\"registry+https://pkg.example\",\"hash\":\"ash1:absent\",\"dependencies\":[]}]}"
        ))

let checkMissingLockedPackage root =
    "app/ashes.json"
    |> join(Unix)(root)
    |> loadGraphFromCache(join(Unix)(root)("cache"))
    |> (given (result) ->
        match result with
            | Error(ProjectLockedPackageMissing("Missing", "2.0.0", _path)) -> Unit
            | Error(error) -> test.fail("unexpected missing locked package result: " + graphErrorText(error))
            | Ok(_) -> test.fail("missing locked package should fail"))

let prepareInvalidLock root =
    root
    |> Ashes.IO.Directory.removeTree
    |> requireUnit("remove stale invalid lock")
    |> (given (_) -> createDirectory(root)("app/src"))
    |> (given (_) -> writeFile(root)("app/src/Main.ash")("0"))
    |> (given (_) -> writeFile(root)("app/ashes.json")("{\"entry\":\"src/Main.ash\"}"))
    |> (given (_) -> writeFile(root)("app/ashes.lock")("{\"version\":2}"))

let checkInvalidLock root =
    "app/ashes.json"
    |> join(Unix)(root)
    |> loadGraphFromCache(join(Unix)(root)("cache"))
    |> (given (result) ->
        match result with
            | Error(ProjectLockInvalid(_path, UnsupportedProjectLockVersion(2))) -> Unit
            | Error(error) -> test.fail("unexpected invalid lock result: " + graphErrorText(error))
            | Ok(_) -> test.fail("invalid lock should fail"))

let prepareRootOverride root =
    root
    |> Ashes.IO.Directory.removeTree
    |> requireUnit("remove stale root override")
    |> (given (_) -> createDirectory(root)("app/src"))
    |> (given (_) -> createDirectory(root)("local/src/B"))
    |> (given (_) -> writeFile(root)("app/src/Main.ash")("import B.Value\nB.Value.value"))
    |> (given (_) ->
        writeFile(
            root,
            "app/ashes.json",
            "{\"entry\":\"src/Main.ash\",\"dependencies\":{\"B\":\"^1.2.0\"},\"overrides\":{\"B\":{\"path\":\"../local\"}}}"
        ))
    |> (given (_) ->
        writeFile(
            root,
            "app/ashes.lock",
            "{\"version\":1,\"package\":[{\"namespace\":\"B\",\"version\":\"1.2.3\",\"source\":\"registry+https://pkg.example\",\"hash\":\"ash1:absent\",\"dependencies\":[]}]}"
        ))
    |> (given (_) ->
        writeFile(
            root,
            "local/ashes.json",
            "{\"name\":\"B\",\"namespace\":\"B\",\"version\":\"1.2.3\",\"entry\":\"src/B.ash\",\"sourceRoots\":[\"src\"]}"
        ))
    |> (given (_) -> writeFile(root)("local/src/B.ash")("0"))
    |> (given (_) -> writeFile(root)("local/src/B/Value.ash")("let value = 42"))

let checkRootOverride root =
    "app/ashes.json"
    |> join(Unix)(root)
    |> loadGraphFromCache(join(Unix)(root)("cache"))
    |> (given (result) ->
        match result with
            | Error(error) ->
                test.fail(
                    "root override should replace the absent cached package: " + graphErrorText(error)
                )
            | Ok(ProjectDependencyGraph { dependencies = dependencies }) ->
                let localDirectory = join(Unix)(root)("local")
                in
                    test.assertEqual(
                        [ResolvedProjectDependency(name = "B", namespace = "B", sourceRoots = [join(
                            Unix,
                            localDirectory,
                            "src"
                        )], projectDirectory = localDirectory, entryPath = join(
                            Unix,
                            localDirectory,
                            "src/B.ash"
                        ), isDev = false)],
                        dependencies
                    ))

let prepareMismatchedOverrideVersion root =
    root
    |> prepareRootOverride
    |> (given (_) ->
        writeFile(
            root,
            "local/ashes.json",
            "{\"name\":\"B\",\"namespace\":\"B\",\"version\":\"1.2.4\",\"entry\":\"src/B.ash\",\"sourceRoots\":[\"src\"]}"
        ))

let checkMismatchedOverrideVersion root =
    "app/ashes.json"
    |> join(Unix)(root)
    |> loadGraphFromCache(join(Unix)(root)("cache"))
    |> (given (result) ->
        match result with
            | Error(ProjectOverrideVersionMismatch("B", "1.2.3", Some("1.2.4"))) -> Unit
            | Error(error) -> test.fail("unexpected override version result: " + graphErrorText(error))
            | Ok(_) -> test.fail("an override version mismatch should fail"))

let prepareMismatchedOverrideNamespace root =
    root
    |> prepareRootOverride
    |> (given (_) ->
        writeFile(
            root,
            "local/ashes.json",
            "{\"name\":\"B\",\"namespace\":\"Other\",\"version\":\"1.2.3\",\"entry\":\"src/B.ash\",\"sourceRoots\":[\"src\"]}"
        ))

let checkMismatchedOverrideNamespace root =
    "app/ashes.json"
    |> join(Unix)(root)
    |> loadGraphFromCache(join(Unix)(root)("cache"))
    |> (given (result) ->
        match result with
            | Error(ProjectOverrideNamespaceMismatch("B", "Other", "B")) -> Unit
            | Error(error) -> test.fail("unexpected override namespace result: " + graphErrorText(error))
            | Ok(_) -> test.fail("an override namespace mismatch should fail"))

let prepareInvalidOverride root =
    root
    |> prepareRootOverride
    |> (given (_) ->
        writeFile(
            root,
            "app/ashes.json",
            "{\"entry\":\"src/Main.ash\",\"dependencies\":{\"B\":\"^1.2.0\"},\"overrides\":{\"B\":\"../local\"}}"
        ))

let checkInvalidOverride root =
    "app/ashes.json"
    |> join(Unix)(root)
    |> loadGraphFromCache(join(Unix)(root)("cache"))
    |> (given (result) ->
        match result with
            | Error(ProjectOverrideInvalid("B")) -> Unit
            | Error(error) -> test.fail("unexpected invalid override result: " + graphErrorText(error))
            | Ok(_) -> test.fail("an override without a path object should fail"))

let prepareUnlockedOverride root =
    root
    |> prepareRootOverride
    |> (given (_) -> writeFile(root)("app/ashes.lock")("{\"version\":1,\"package\":[]}"))

let checkUnlockedOverride root =
    "app/ashes.json"
    |> join(Unix)(root)
    |> loadGraphFromCache(join(Unix)(root)("cache"))
    |> (given (result) ->
        match result with
            | Error(ProjectOverrideNotLocked("B", _path)) -> Unit
            | Error(error) -> test.fail("unexpected unlocked override result: " + graphErrorText(error))
            | Ok(_) -> test.fail("an override absent from the lock should fail"))

let prepareDependencyOverride root =
    root
    |> Ashes.IO.Directory.removeTree
    |> requireUnit("remove stale dependency override")
    |> (given (_) -> createDirectory(root)("app/src"))
    |> (given (_) -> createDirectory(root)("dep/src"))
    |> (given (_) -> writeFile(root)("app/src/Main.ash")("0"))
    |> (given (_) ->
        writeFile(
            root,
            "app/ashes.json",
            "{\"entry\":\"src/Main.ash\",\"dependencies\":{\"dep\":{\"path\":\"../dep\"}}}"
        ))
    |> (given (_) -> writeFile(root)("dep/src/Dep.ash")("0"))
    |> (given (_) ->
        writeFile(
            root,
            "dep/ashes.json",
            "{\"entry\":\"src/Dep.ash\",\"overrides\":{\"Missing\":{\"path\":\"../missing\"}}}"
        ))

let checkDependencyOverrideIgnored root =
    "app/ashes.json"
    |> join(Unix)(root)
    |> loadGraph
    |> (given (result) ->
        match result with
            | Error(error) -> test.fail("dependency overrides should be ignored: " + graphErrorText(error))
            | Ok(ProjectDependencyGraph { dependencies = dependencies }) ->
                dependencies
                |> dependencySummary
                |> test.assertEqual(["dep|Dep|normal"]))

let runProjectDependencyGraphTests root =
    root
    |> prepareResolvedGraph
    |> (given (_) -> checkResolvedGraph(root))
    |> (given (_) -> checkCompilationPlan(root))
    |> (given (_) -> prepareDottedDependencyNamespace(root))
    |> (given (_) -> checkDottedDependencyNamespace(root))
    |> (given (_) -> prepareMissingPath(root))
    |> (given (_) -> checkMissingPath(root))
    |> (given (_) -> prepareMissingManifest(root))
    |> (given (_) -> checkMissingManifest(root))
    |> (given (_) -> prepareDiamond(root))
    |> (given (_) -> checkDiamond(root))
    |> (given (_) -> prepareCycle(root))
    |> (given (_) -> checkCycle(root))
    |> (given (_) -> prepareNamespaceConflict(root))
    |> (given (_) -> checkNamespaceConflict(root))
    |> (given (_) -> prepareOutsideNamespace(root))
    |> (given (_) -> checkOutsideNamespace(root))
    |> (given (_) -> prepareLockedPackage(root))
    |> (given (_) -> checkLockedPackage(root))
    |> (given (_) -> prepareMissingLockedPackage(root))
    |> (given (_) -> checkMissingLockedPackage(root))
    |> (given (_) -> prepareInvalidLock(root))
    |> (given (_) -> checkInvalidLock(root))
    |> (given (_) -> prepareRootOverride(root))
    |> (given (_) -> checkRootOverride(root))
    |> (given (_) -> prepareMismatchedOverrideVersion(root))
    |> (given (_) -> checkMismatchedOverrideVersion(root))
    |> (given (_) -> prepareMismatchedOverrideNamespace(root))
    |> (given (_) -> checkMismatchedOverrideNamespace(root))
    |> (given (_) -> prepareInvalidOverride(root))
    |> (given (_) -> checkInvalidOverride(root))
    |> (given (_) -> prepareUnlockedOverride(root))
    |> (given (_) -> checkUnlockedOverride(root))
    |> (given (_) -> prepareDependencyOverride(root))
    |> (given (_) -> checkDependencyOverrideIgnored(root))
    |> (given (_) -> Ashes.IO.Directory.removeTree(root))
    |> requireUnit("remove project graph fixtures")

let run unit =
    Unit
    |> Ashes.IO.Environment.temporaryDirectory
    |> requirePath("temporary directory")
    |> (given (temporary) -> join(Unix)(temporary)("ashes-selfhost-path-dependencies"))
    |> runProjectDependencyGraphTests
    |> (given (_) -> Ashes.IO.print("all self-hosted project dependency graph tests passed"))
