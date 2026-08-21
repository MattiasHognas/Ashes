import Ashes.Test as test
import Ashes.IO.Path
import AshesCompiler.Semantics.ProjectLockFile
let lockErrorText (error: ProjectLockFileError) = Ashes.Trait.Show.show(error)

let checkValidLock unit =
    "{\"version\":1,\"package\":[{\"namespace\":\"Json\",\"version\":\"1.2.3\",\"source\":\"registry+https://pkg.example\",\"hash\":\"ash1:abc\",\"dependencies\":[\"Utf8\"]},{\"namespace\":\"Utf8\",\"version\":\"0.4.3\",\"source\":\"registry+https://pkg.example\",\"hash\":\"ash1:def\",\"dependencies\":[]}]}"
    |> parseProjectLockFile
    |> (given (result) ->
        match result with
            | Error(error) -> test.fail("valid lock should parse: " + lockErrorText(error))
            | Ok(ProjectLockFile { version = version, packages = first :: second :: [] }) ->
                version
                |> test.assertEqual(1)
                |> (given (_) ->
                    test.assertEqual(
                        LockedPackage(namespace = "Json", version = "1.2.3", source = "registry+https://pkg.example", hash = "ash1:abc", dependencies = ["Utf8"]),
                        first
                    ))
                |> (given (_) ->
                    test.assertEqual(
                        LockedPackage(namespace = "Utf8", version = "0.4.3", source = "registry+https://pkg.example", hash = "ash1:def", dependencies = []),
                        second
                    ))
            | Ok(_) -> test.fail("valid lock should preserve both packages"))

let checkDefaults unit =
    "{}"
    |> parseProjectLockFile
    |> test.assertEqual(Ok(ProjectLockFile(version = 1, packages = [])))

let checkLockPaths unit =
    "/work/ashes.json"
    |> lockFilePath(Unix)
    |> test.assertEqual("/work/ashes.lock")
    |> (given (_) -> lockFilePath(Unix)("/work/ashes-test.json"))
    |> test.assertEqual("/work/ashes-test.lock")
    |> (given (_) -> lockFilePath(Windows)("C:\\work\\ashes-test.json"))
    |> test.assertEqual("C:\\work\\ashes-test.lock")

let checkCachePaths unit =
    LockedPackage(namespace = "Json", version = "1.2.3", source = "registry+https://pkg.example", hash = "ash1:abc", dependencies = [])
    |> cachePathFor(Unix)("/cache/ashes")
    |> test.assertEqual("/cache/ashes/pkg/Json/1.2.3/abc")
    |> (given (_) -> LockedPackage(namespace = "Json", version = "1.2.3", source = "registry+https://pkg.example", hash = "legacy", dependencies = []))
    |> cachePathFor(Windows)("C:\\cache\\ashes")
    |> test.assertEqual("C:\\cache\\ashes\\pkg\\Json\\1.2.3\\legacy")

let checkInvalidVersion unit =
    "{\"version\":2,\"package\":[]}"
    |> parseProjectLockFile
    |> test.assertEqual(Error(UnsupportedProjectLockVersion(2)))

let checkInvalidPackage unit =
    "{\"package\":[{\"namespace\":\"Json\",\"version\":\"1.2.3\",\"source\":\"registry+x\",\"dependencies\":[]}]}"
    |> parseProjectLockFile
    |> test.assertEqual(Error(InvalidLockedPackage("hash")))

let run unit =
    unit
    |> checkValidLock
    |> checkDefaults
    |> checkLockPaths
    |> (given (_) -> checkCachePaths(Unit))
    |> checkInvalidVersion
    |> checkInvalidPackage
    |> (given (_) -> Ashes.IO.print("all self-hosted project lock file tests passed"))
