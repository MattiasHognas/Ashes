import Ashes.Test as test
import AshesCompiler.Frontend.ImportHeader
let expectParsed result =
    match result with
        | Ok(parsed) -> parsed
        | Error(InvalidImportSyntax(_line, written)) -> test.fail("unexpected invalid import: " + written)
        | Error(ReservedImportAlias(_line, alias)) -> test.fail("unexpected reserved alias: " + alias)
        | Error(DuplicateImportAlias(_line, alias, _existing)) -> test.fail("unexpected duplicate alias: " + alias)
        | Error(ConflictingImportSelector(_line, name)) -> test.fail("unexpected selector conflict: " + name)

let writtenImportSource = "// café\n\nimport Ashes.IO\nimport Ashes.Collection.List as Lists\nimport Ashes.IO.print\nimport Ashes.Collection.List.map as listMap\nlet value = 1\nvalue"

let checkWrittenImportResult parsed =
    parsed.imports
    |> test.assertEqual(
        [ImportHeaderEntry(modulePath = "Ashes.IO", selector = None, alias = None, sourceLine = 3, written = "import Ashes.IO"), ImportHeaderEntry(modulePath = "Ashes.Collection.List", selector = None, alias = Some(
            "Lists"
        ), sourceLine = 4, written = "import Ashes.Collection.List as Lists"), ImportHeaderEntry(modulePath = "Ashes.IO", selector = Some(
            "print"
        ), alias = None, sourceLine = 5, written = "import Ashes.IO.print"), ImportHeaderEntry(modulePath = "Ashes.Collection.List", selector = Some(
            "map"
        ), alias = Some(
            "listMap"
        ), sourceLine = 6, written = "import Ashes.Collection.List.map as listMap")]
    )
    |> (given (_) -> test.assertEqual("// café\n\n\n\n\n\nlet value = 1\nvalue")(parsed.sourceWithoutImports))
    |> (given (_) -> test.assertEqual(130)(parsed.bodyStartByteOffset))

let checkWrittenImports unit =
    writtenImportSource
    |> parseImportHeader
    |> expectParsed
    |> checkWrittenImportResult

let checkHeaderOnly unit =
    "import Ashes.IO\n"
    |> parseImportHeader
    |> expectParsed
    |> (given (parsed) -> test.assertEqual(16)(parsed.bodyStartByteOffset))

let expectError expected result =
    match result with
        | Ok(_) ->
            match expected with
                | InvalidImportSyntax(_, _) -> test.fail("expected invalid import syntax")
                | ReservedImportAlias(_, _) -> test.fail("expected reserved import alias")
                | DuplicateImportAlias(_, _, _) -> test.fail("expected duplicate import alias")
                | ConflictingImportSelector(_, _) -> test.fail("expected conflicting import selector")
        | Error(actual) -> test.assertEqual(expected)(actual)

let checkInvalidSyntax unit =
    "import ashes.IO\nUnit"
    |> parseImportHeader
    |> expectError(InvalidImportSyntax(1)("import ashes.IO"))

let checkReservedAliases unit =
    "import Ashes.IO as match\nUnit"
    |> parseImportHeader
    |> expectError(ReservedImportAlias(1)("match"))

let checkDuplicateAliases unit =
    "import Ashes.IO as A\nimport Ashes.Text as A\nUnit"
    |> parseImportHeader
    |> expectError(DuplicateImportAlias(2)("A")("Ashes.IO"))

let checkSelectorCollisions unit =
    "import Ashes.IO.print as use\nimport Ashes.Text.trim as use\nUnit"
    |> parseImportHeader
    |> expectError(ConflictingImportSelector(2)("use"))

let checkCommentsEndWithBody unit =
    "// import Ashes.Fake\nlet value = 1\nimport Ashes.IO"
    |> parseImportHeader
    |> expectParsed
    |> (given (parsed) ->
        parsed.imports
        |> test.assertEqual([])
        |> (given (_) -> test.assertEqual(21)(parsed.bodyStartByteOffset))
        |> (given (_) ->
            test.assertEqual(
                "// import Ashes.Fake\nlet value = 1\nimport Ashes.IO",
                parsed.sourceWithoutImports
            )))

let run unit =
    unit
    |> checkWrittenImports
    |> checkHeaderOnly
    |> checkInvalidSyntax
    |> checkReservedAliases
    |> checkDuplicateAliases
    |> checkSelectorCollisions
    |> checkCommentsEndWithBody
