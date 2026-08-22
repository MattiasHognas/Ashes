import Ashes.Test as test
import Ashes.Internal.deepCopy as deepCopy
import AshesCompiler.Frontend.ImportHeader
import AshesCompiler.Frontend.ImportResolution
import AshesCompiler.Frontend.ModulePlan
import AshesCompiler.Frontend.ModuleSource
let assertNamed (name: Str) expected actual =
    if expected == actual
    then Unit
    else test.fail("module plan assertion failed: " + name)

let entry p s a l w = ImportHeaderEntry(modulePath = p, selector = s, alias = a, sourceLine = l, written = w)

let importModule (name: Str) line = entry(name)(None)(None)(line)("import " + name)

let importValue (moduleName: Str) name line =
    ImportHeaderEntry(modulePath = moduleName, selector = Some(
        name
    ), alias = None, sourceLine = line, written = "import " + moduleName + "." + name)

let mkTestUnit n s i m d = deepCopy(ModulePlanUnit(name = n, source = s, imports = i, interface = m, dependencies = d))

let planUnitWith (name: Str) imports exports dependencies source =
    (let moduleInterface = ModuleImportInterface(name = name, exports = exports)
    in mkTestUnit(name)(source)(imports)(moduleInterface)(dependencies))

let projectSource name = ProjectModuleSource("/project/" + name + ".ash")

let planUnit (name: Str) imports exports =
    name
    |> projectSource
    |> planUnitWith(name)(imports)(exports)([])

let inlinePlanUnit (name: Str) imports exports dependencies =
    ""
    |> InlineModuleSource("/project/Parent.ash#" + name)
    |> planUnitWith(name)(imports)(exports)(dependencies)

let recursive names (modules: List(PlannedModule)) =
    match modules with
        | [] -> []
        | PlannedModule { name = name, source = _source, imports = _imports, interface = _interface } :: rest ->
            name :: names(
                rest
            )

let checkDependencyOrder unit =
    [planUnit(
        "App",
        [importValue("Util")("value")(1), importModule("Geometry")(2)],
        []
    ), planUnit(
        "Geometry",
        [],
        [ImportTypeExport("Point")]
    ), planUnit("Unused")([])([]), planUnit("Util")([])([ImportValueExport("value")])]
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

let checkIntrinsicDependencyOrder unit =
    [
        "Parent"
        |> projectSource
        |> planUnitWith("Parent")([])([ImportModuleExport("Child")])(["Parent.Child"]),
        inlinePlanUnit("Parent.Child")([])([])([])
    ]
    |> buildModulePlan("Parent")
    |> (given (result) ->
        match result with
            | Error(_error) -> test.fail("intrinsic module dependency should plan")
            | Ok(modules) ->
                modules
                |> names
                |> assertNamed("intrinsic dependency order")(["Parent.Child", "Parent"]))

let checkHiddenInlineModule unit =
    [
        planUnit("App")([importModule("Parent.Child")(1)])([]),
        planUnit("Parent")([])([]),
        inlinePlanUnit("Parent.Child")([])([])([])
    ]
    |> buildModulePlan("App")
    |> assertNamed("hidden inline module")(
        "Child"
        |> UnexportedNestedModule("App")("Parent")
        |> Error
    )

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
    [ModulePlanUnit(name = "App", source = ProjectModuleSource(
        "/project/App.ash"
    ), imports = [], interface = ModuleImportInterface(name = "Wrong", exports = []), dependencies = [])]
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
    |> checkIntrinsicDependencyOrder
    |> checkHiddenInlineModule
    |> checkUnknownImport
    |> checkDuplicateModule
    |> checkInterfaceName
    |> checkUnknownEntry
