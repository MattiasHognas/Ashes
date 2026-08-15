import Ashes.Test as test
import AshesCompiler.Frontend.ImportResolution
import AshesCompiler.Frontend.ModuleInterface
import AshesCompiler.Frontend.Syntax
let assertNamed name expected actual =
    if expected == actual
    then Unit
    else test.fail("module interface assertion failed: " + name)

let makeBinding name = LetBindingSyntax(name = name, value = ExprInt(0), sugarParameters = [], typeAnnotation = None, requirements = [])

let makeConstructor name = TypeConstructor(name = name, parameters = [], fieldNames = [])

let boxType = TypeDecl(name = "Box", typeParameters = [], constructors = [makeConstructor("Wrap"), makeConstructor("Empty")], isRecord = false, derivingTraits = [])

let compatibilityProgram =
    ProgramSyntax(items = [TopLevelLet(makeBinding("run"))(false), TopLevelRecursiveGroup([makeBinding("even"), makeBinding("odd")]), TopLevelType(boxType), TopLevelTypeAlias(TypeAliasDecl(name = "Alias", typeParameters = [], target = TypeNamed("Int"))), TopLevelZeroCostType(ZeroCostTypeDecl(name = "UserId", typeParameters = [], constructor = makeConstructor("UserId"), derivingTraits = [])), TopLevelTrait(TraitDecl(name = "Display", typeParameters = [], supertraits = [], methods = [])), None
    |> ExternalOpaqueType("Native")
    |> TopLevelExternal], body = Some(ExprVar("notExported")))

let checkCompatibilityInterface unit =
    compatibilityProgram
    |> buildModuleInterface("Example")([])
    |> assertNamed("compatibility")(Ok(ModuleImportInterface(name = "Example", exports = [ImportValueExport("run"), ImportValueExport("even"), ImportValueExport("odd"), ImportTypeExport("Box"), ImportConstructorExport("Wrap"), ImportConstructorExport("Empty"), ImportTypeExport("Alias"), ImportTypeExport("UserId"), ImportConstructorExport("UserId"), ImportTypeExport("Display")])))

let explicitProgram =
    ProgramSyntax(items = [TopLevelExport(ExportDecl(items = [ExportValue("run"), ExportType("Box")(ExportConstructorsSelected(["Wrap"])), ExportModule("Internal")])), TopLevelLet(makeBinding("run"))(false), TopLevelLet(makeBinding("privateValue"))(false), TopLevelType(boxType)], body = None)

let checkExplicitInterface unit =
    explicitProgram
    |> buildModuleInterface("Example")(["Internal"])
    |> assertNamed("explicit")(Ok(ModuleImportInterface(name = "Example", exports = [ImportValueExport("run"), ImportTypeExport("Box"), ImportConstructorExport("Wrap"), ImportModuleExport("Internal")])))

let checkExportPosition unit =
    ProgramSyntax(items = [TopLevelLet(makeBinding("run"))(false), TopLevelExport(ExportDecl(items = [ExportValue("run")]))], body = None)
    |> buildModuleInterface("Example")([])
    |> assertNamed("position")(Error(InvalidExportDeclaration("Example")))

let checkUnknownExport unit =
    ProgramSyntax(items = [TopLevelExport(ExportDecl(items = [ExportValue("missing")]))], body = None)
    |> buildModuleInterface("Example")([])
    |> assertNamed("unknown export")("missing"
    |> UnknownModuleExport("Example")
    |> Error)

let checkUnknownConstructor unit =
    ProgramSyntax(items = [TopLevelExport(ExportDecl(items = [ExportType("Box")(ExportConstructorsSelected(["Missing"]))])), TopLevelType(boxType)], body = None)
    |> buildModuleInterface("Example")([])
    |> assertNamed("unknown constructor")("Missing"
    |> UnknownModuleExport("Example")
    |> Error)

let checkDuplicateExport unit =
    ProgramSyntax(items = [TopLevelExport(ExportDecl(items = [ExportValue("run"), ExportValue("run")])), TopLevelLet(makeBinding("run"))(false)], body = None)
    |> buildModuleInterface("Example")([])
    |> assertNamed("duplicate")("value:run"
    |> DuplicateModuleExport("Example")
    |> Error)

let run unit =
    unit
    |> checkCompatibilityInterface
    |> checkExplicitInterface
    |> checkExportPosition
    |> checkUnknownExport
    |> checkUnknownConstructor
    |> checkDuplicateExport
