import Ashes.Test as test
import AshesCompiler.Frontend.ImportHeader
import AshesCompiler.Frontend.ImportResolution
let moduleInterface name exports = ModuleImportInterface(name = name, exports = exports)

let importEntry modulePath selector alias sourceLine = ImportHeaderEntry(modulePath = modulePath, selector = selector, alias = alias, sourceLine = sourceLine, written = "import " + modulePath)

let checkLongestModuleMatch unit =
    [importEntry("Geom.Vec")(None)(Some("V"))(1)]
    |> resolveImports([moduleInterface("Geom")([ImportTypeExport("Vec")]), moduleInterface("Geom.Vec")([ImportValueExport("make")])])
    |> test.assertEqual(Ok([ResolvedModuleImport("Geom.Vec")(Some("V"))(1)("import Geom.Vec")]))

let checkTypeSelectorFallback unit =
    [importEntry("Geom.Vec")(None)(Some("V"))(2)]
    |> resolveImports([moduleInterface("Geom")([ImportTypeExport("Vec")])])
    |> test.assertEqual(Ok([ResolvedTypeImport("Geom")("Vec")("V")(2)("import Geom.Vec")]))

let checkValueSelector unit =
    [importEntry("Geom")(Some("origin"))(Some("zero"))(3)]
    |> resolveImports([moduleInterface("Geom")([ImportValueExport("origin"), ImportTypeExport("Point")])])
    |> test.assertEqual(Ok([ResolvedValueImport("Geom")("origin")("zero")(3)("import Geom")]))

let checkUnknownModule unit =
    [importEntry("Missing.Module")(None)(None)(4)]
    |> resolveImports([])
    |> test.assertEqual("Missing.Module"
    |> UnknownImportModule(4)
    |> Error)

let checkUnknownExport unit =
    [importEntry("Geom")(Some("missing"))(None)(5)]
    |> resolveImports([moduleInterface("Geom")([ImportValueExport("origin")])])
    |> test.assertEqual("missing"
    |> UnknownImportExport(5)("Geom")
    |> Error)

let checkResolvedCollision unit =
    [importEntry("Geom")(Some("origin"))(Some("V"))(6), importEntry("Types.V")(None)(None)(7)]
    |> resolveImports([moduleInterface("Geom")([ImportValueExport("origin")]), moduleInterface("Types")([ImportTypeExport("V")])])
    |> test.assertEqual("V"
    |> ConflictingResolvedImport(7)
    |> Error)

let run unit =
    unit
    |> checkLongestModuleMatch
    |> checkTypeSelectorFallback
    |> checkValueSelector
    |> checkUnknownModule
    |> checkUnknownExport
    |> checkResolvedCollision
