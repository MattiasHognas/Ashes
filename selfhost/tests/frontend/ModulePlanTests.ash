import Ashes.Test as test
import AshesCompiler.Frontend.ImportHeader
import AshesCompiler.Frontend.ImportResolution
import AshesCompiler.Frontend.ModulePlan
import AshesCompiler.Frontend.ModuleSource
let assertNamed (name: Str) expected actual =
    if expected == actual
    then Unit
    else test.fail("module plan assertion failed: " + name)

let importModule (name: Str) line = ImportHeaderEntry(modulePath = name, selector = None, alias = None, sourceLine = line, written = "import " + name)

let importValue (moduleName: Str) name line = ImportHeaderEntry(modulePath = moduleName, selector = Some(name), alias = None, sourceLine = line, written = "import " + moduleName + "." + name)

let planUnit (name: Str) imports exports = ModulePlanUnit(name = name, source = ProjectModuleSource("/project/" + name + ".ash"), imports = imports, interface = ModuleImportInterface(name = name, exports = exports))

let recursive names (modules: List(PlannedModule)) =
    match modules with
        | [] -> []
        | PlannedModule { name = name, source = _source, imports = _imports, interface = _interface } :: rest -> name :: names(rest)

let checkDependencyOrder unit =
    [planUnit("App")([importValue("Util")("value")(1), importModule("Geometry")(2)])([]), planUnit("Geometry")([])([ImportTypeExport("Point")]), planUnit("Unused")([])([]), planUnit("Util")([])([ImportValueExport("value")])]
    |> buildModulePlan("App")
    |> (given (result) ->
        match result with
            | Ok(modules) ->
                modules
                |> names
                |> assertNamed("dependency order")(["Util", "Geometry", "App"])
            | Error(_error) -> test.fail("module plan unexpectedly failed"))

let checkCycle unit =
    [planUnit("A")([importModule("B")(1)])([]), planUnit("B")([importModule("A")(1)])([])]
    |> buildModulePlan("A")
    |> assertNamed("cycle")(Error(ModuleImportCycle(["A", "B", "A"])))

let checkUnknownImport unit =
    [planUnit("App")([importModule("Missing")(7)])([])]
    |> buildModulePlan("App")
    |> assertNamed("unknown import")("Missing"
    |> UnknownImportModule(7)
    |> ModulePlanImportError("App")
    |> Error)

let checkDuplicateModule unit =
    [planUnit("App")([])([]), planUnit("App")([])([])]
    |> buildModulePlan("App")
    |> assertNamed("duplicate module")(Error(DuplicatePlanModule("App")))

let checkInterfaceName unit =
    [ModulePlanUnit(name = "App", source = ProjectModuleSource("/project/App.ash"), imports = [], interface = ModuleImportInterface(name = "Wrong", exports = []))]
    |> buildModulePlan("App")
    |> assertNamed("interface name")("Wrong"
    |> ModuleInterfaceNameMismatch("App")
    |> Error)

let checkUnknownEntry unit =
    []
    |> buildModulePlan("Missing")
    |> assertNamed("unknown entry")(Error(UnknownPlanEntry("Missing")))

let run unit =
    unit
    |> checkDependencyOrder
    |> checkCycle
    |> checkUnknownImport
    |> checkDuplicateModule
    |> checkInterfaceName
    |> checkUnknownEntry
