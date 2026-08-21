import Ashes.Test as test
import Ashes.IO.Path
import AshesCompiler.Semantics.ProjectSourceEnumeration
let requireUnit name result =
    match result with
        | Ok(Unit) -> Unit
        | Error(error) -> test.fail(name + " failed: " + error)

let requirePath name result =
    match result with
        | Ok(value) -> value
        | Error(_error) -> test.fail(name + " failed")

let writeFixtureFile root relativePath contents =
    contents
    |> Ashes.IO.File.writeText(join(Unix)(root)(relativePath))
    |> requireUnit("write " + relativePath)

let prepareFixture root =
    root
    |> Ashes.IO.Directory.removeTree
    |> requireUnit("remove stale fixture")
    |> (given (_) ->
        "src/Nested"
        |> join(Unix)(root)
        |> Ashes.IO.Directory.createAll
        |> requireUnit("create nested source root"))
    |> (given (_) ->
        "vendor/Vendor"
        |> join(Unix)(root)
        |> Ashes.IO.Directory.createAll
        |> requireUnit("create include root"))
    |> (given (_) -> writeFixtureFile(root)("src/Main.ash")("0"))
    |> (given (_) -> writeFixtureFile(root)("src/Nested/Alpha.ash")("1"))
    |> (given (_) -> writeFixtureFile(root)("src/Zeta.ash")("2"))
    |> (given (_) -> writeFixtureFile(root)("src/notes.txt")("ignored"))
    |> (given (_) -> writeFixtureFile(root)("vendor/Vendor/Beta.ash")("3"))

let expectDeterministicSources root result =
    match result with
        | Error(_error) -> test.fail("source enumeration should succeed")
        | Ok(paths) ->
            if paths == [join(Unix)(root)("src/Main.ash"), join(
                Unix,
                root,
                "src/Nested/Alpha.ash"
            ), join(Unix)(root)("src/Zeta.ash"), join(
                Unix,
                root,
                "vendor/Vendor/Beta.ash"
            )]
            then Unit
            else test.fail("unexpected deterministic sources: " + Ashes.Trait.Show.show(paths))

let checkDeterministicEnumeration root =
    [join(Unix)(root)("src"), join(Unix)(root)("vendor")]
    |> enumerateProjectSourceFiles(Unix)
    |> expectDeterministicSources(root)

let checkOverlappingRoots root =
    [root, join(Unix)(root)("src")]
    |> enumerateProjectSourceFiles(Unix)
    |> (given (result) ->
        match result with
            | Error(_error) -> test.fail("overlapping roots should succeed")
            | Ok(paths) ->
                if paths == [join(Unix)(root)("src/Main.ash"), join(
                    Unix,
                    root,
                    "src/Nested/Alpha.ash"
                ), join(Unix)(root)("src/Zeta.ash"), join(
                    Unix,
                    root,
                    "vendor/Vendor/Beta.ash"
                )]
                then Unit
                else test.fail("unexpected overlapping-root sources: " + Ashes.Trait.Show.show(paths)))

let checkMissingRoot root =
    [join(Unix)(root)("missing")]
    |> enumerateProjectSourceFiles(Unix)
    |> (given (result) ->
        match result with
            | Error(ProjectSourceRootReadError(path, _message)) ->
                test.assertEqual(join(Unix)(root)("missing"))(path)
            | Ok(_) -> test.fail("a missing source root should fail"))

let runProjectSourceEnumerationTests unit =
    Unit
    |> Ashes.IO.Environment.temporaryDirectory
    |> requirePath("temporary directory")
    |> (given (temporary) -> join(Unix)(temporary)("ashes-selfhost-project-sources"))
    |> (given (root) ->
        root
        |> prepareFixture
        |> (given (_) -> root))
    |> (given (root) ->
        root
        |> checkDeterministicEnumeration
        |> (given (_) -> root))
    |> (given (root) ->
        root
        |> checkOverlappingRoots
        |> (given (_) -> root))
    |> (given (root) ->
        root
        |> checkMissingRoot
        |> (given (_) -> root))
    |> (given (root) ->
        root
        |> Ashes.IO.Directory.removeTree
        |> requireUnit("remove fixture"))
