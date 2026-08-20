import Ashes.Test as test
import AshesCompiler.Semantics.ProjectManifest
let assertNamed (name: Str) (expected: Result(ProjectManifestError, ProjectManifest)) (actual: Result(ProjectManifestError, ProjectManifest)) =
    if expected == actual
    then Unit
    else test.fail("project manifest assertion failed: " + name)

let emptyDefaults = ProjectDefaults(optimize = None)

let checkDefaults unit =
    "{\"entry\":\"src/Main.ash\",\"unknown\":123}"
    |> parseProjectManifest
    |> assertNamed("defaults")(Ok(ProjectManifest(entry = "src/Main.ash", name = None, namespace = None, version = None, sourceRoots = ["."], includeRoots = [], outDir = "out", target = None, defaults = emptyDefaults, dependencies = [], devDependencies = [], overrides = [])))

let checkCompleteManifest unit =
    "{\"entry\":\"Main.ash\",\"name\":\"demo\",\"namespace\":\"Demo\",\"version\":\"1.2.3\",\"sourceRoots\":[\"src\",\"generated\"],\"include\":[\"vendor\"],\"outDir\":\"build\",\"target\":\"linux-x64\",\"defaults\":{\"optimize\":true,\"future\":1},\"dependencies\":{\"json\":\"^1.2.0\",\"local\":{\"path\":\"../local\",\"namespace\":\"LocalApi\"}},\"devDependencies\":{\"checks\":{\"path\":\"../checks\"}},\"overrides\":{\"json\":{\"path\":\"../json\"},\"broken\":\"../broken\"}}"
    |> parseProjectManifest
    |> assertNamed("complete")(Ok(ProjectManifest(entry = "Main.ash", name = Some("demo"), namespace = Some("Demo"), version = Some("1.2.3"), sourceRoots = ["src", "generated"], includeRoots = ["vendor"], outDir = "build", target = Some("linux-x64"), defaults = ProjectDefaults(optimize = Some(true)), dependencies = [ProjectDependency(name = "json", source = RegistryDependency("^1.2.0")), ProjectDependency(name = "local", source = PathDependency("../local")(Some("LocalApi")))], devDependencies = [ProjectDependency(name = "checks", source = PathDependency("../checks")(None))], overrides = [ProjectOverride(name = "json", path = Some("../json")), ProjectOverride(name = "broken", path = None)])))

let checkPermissiveArrays unit =
    "{\"entry\":\"Main.ash\",\"sourceRoots\":[\"\",1,\"src\"],\"include\":false,\"outDir\":4,\"target\":null}"
    |> parseProjectManifest
    |> assertNamed("permissive arrays")(Ok(ProjectManifest(entry = "Main.ash", name = None, namespace = None, version = None, sourceRoots = ["src"], includeRoots = [], outDir = "out", target = None, defaults = emptyDefaults, dependencies = [], devDependencies = [], overrides = [])))

let checkInvalidJson unit =
    ""
    |> parseProjectManifest
    |> assertNamed("invalid json")(Error(ProjectJsonParseError("unexpected end of input")))

let checkNonObject unit =
    "[]"
    |> parseProjectManifest
    |> assertNamed("non-object")(Error(ProjectManifestMustBeObject))

let checkMissingEntry unit =
    "{}"
    |> parseProjectManifest
    |> assertNamed("missing entry")(Error(MissingProjectEntry))

let checkInvalidEntry unit =
    "{\"entry\":\"Main.txt\"}"
    |> parseProjectManifest
    |> assertNamed("invalid entry")(Error(InvalidProjectEntry("Main.txt")))

let checkIgnoredUnsupportedDependency unit =
    "{\"entry\":\"Main.ASH\",\"dependencies\":{\"broken\":{\"namespace\":\"Broken\"}}}"
    |> parseProjectManifest
    |> assertNamed("ignored unsupported dependency")(Ok(ProjectManifest(entry = "Main.ASH", name = None, namespace = None, version = None, sourceRoots = ["."], includeRoots = [], outDir = "out", target = None, defaults = emptyDefaults, dependencies = [], devDependencies = [], overrides = [])))

let run unit =
    unit
    |> checkDefaults
    |> checkCompleteManifest
    |> checkPermissiveArrays
    |> checkInvalidJson
    |> checkNonObject
    |> checkMissingEntry
    |> checkInvalidEntry
    |> checkIgnoredUnsupportedDependency
