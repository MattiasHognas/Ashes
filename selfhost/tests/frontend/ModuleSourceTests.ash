import Ashes.Test as test
import AshesCompiler.Frontend.ModuleSource
let assertNamed (name: Str) expected actual =
    if expected == actual
    then Unit
    else test.fail("module source assertion failed: " + name)

let roots shippedRoot = ModuleSourceRoots(sourceRoots = ["/project/src"], includeRoots = ["/project/vendor"], dependencyRoots = ["/cache/geometry/src"], shippedRoot = shippedRoot)

let repeatedRoots = ModuleSourceRoots(sourceRoots = ["/project/src"], includeRoots = ["/project/src"], dependencyRoots = [], shippedRoot = None)

let checkRelativePath unit =
    "Geometry.Shape.Circle"
    |> moduleRelativePath
    |> assertNamed("relative path")("Geometry/Shape/Circle.ash")

let checkProjectSourceWins unit =
    ["/project/src/Geometry/Shape.ash", "/compiler/lib/Geometry/Shape.ash"]
    |> resolveModuleSource("Geometry.Shape")(roots(Some("/compiler/lib")))
    |> assertNamed("project source wins")(Ok(ProjectModuleSource("/project/src/Geometry/Shape.ash")))

let checkIncludeRoot unit =
    ["/project/vendor/Codec/Json.ash"]
    |> resolveModuleSource("Codec.Json")(roots(None))
    |> assertNamed("include root")(Ok(ProjectModuleSource("/project/vendor/Codec/Json.ash")))

let checkDependencyRoot unit =
    ["/cache/geometry/src/Geometry/Point.ash"]
    |> resolveModuleSource("Geometry.Point")(roots(None))
    |> assertNamed("dependency root")(Ok(ProjectModuleSource("/cache/geometry/src/Geometry/Point.ash")))

let checkDuplicateRootDoesNotCreateAmbiguity unit =
    ["/project/src/Geometry/Point.ash"]
    |> resolveModuleSource("Geometry.Point")(repeatedRoots)
    |> assertNamed("duplicate root")(Ok(ProjectModuleSource("/project/src/Geometry/Point.ash")))

let checkAmbiguousProjectSource unit =
    ["/project/src/Geometry/Point.ash", "/project/vendor/Geometry/Point.ash"]
    |> resolveModuleSource("Geometry.Point")(roots(None))
    |> assertNamed("ambiguous source")(["/project/src/Geometry/Point.ash", "/project/vendor/Geometry/Point.ash"]
    |> AmbiguousModuleSource("Geometry.Point")
    |> Error)

let checkShippedFallback unit =
    ["/compiler/lib/Geometry/Shape.ash"]
    |> resolveModuleSource("Geometry.Shape")(roots(Some("/compiler/lib")))
    |> assertNamed("shipped fallback")(Ok(ShippedModuleSource("/compiler/lib/Geometry/Shape.ash")))

let checkMissingModule unit =
    []
    |> resolveModuleSource("Geometry.Missing")(roots(Some("/compiler/lib")))
    |> assertNamed(
        "missing source",
        [
            "/project/src/Geometry/Missing.ash",
            "/project/vendor/Geometry/Missing.ash",
            "/cache/geometry/src/Geometry/Missing.ash",
            "/compiler/lib/Geometry/Missing.ash"
        ]
        |> MissingModuleSource("Geometry.Missing")
        |> Error
    )

let checkReservedNamespace unit =
    assertNamed(
        "reserved namespace",
        (Error(ReservedModuleSource("Ashes")), Ok(ShippedModuleSource("/compiler/lib/Ashes/IO.ash"))),
        (resolveModuleSource("Ashes")(roots(Some("/compiler/lib")))(["/compiler/lib/Ashes.ash"]), resolveModuleSource(
            "Ashes.IO",
            roots(Some("/compiler/lib")),
            ["/project/src/Ashes/IO.ash", "/compiler/lib/Ashes/IO.ash"]
        ))
    )

let run unit =
    unit
    |> checkRelativePath
    |> checkProjectSourceWins
    |> checkIncludeRoot
    |> checkDependencyRoot
    |> checkDuplicateRootDoesNotCreateAmbiguity
    |> checkAmbiguousProjectSource
    |> checkShippedFallback
    |> checkMissingModule
    |> checkReservedNamespace
