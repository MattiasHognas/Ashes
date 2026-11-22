import Ashes.Test as test
import AshesCompiler.Frontend.ImportResolution
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Frontend.Token
import AshesCompiler.Semantics.ModuleSemanticStitching
import AshesCompiler.Semantics.ModuleReferenceRewriting
export (
    value runModuleReferenceRewritingTests,
)

let binding name value = LetBindingSyntax(name = name, value = value, sugarParameters = [], typeAnnotation = None, requirements = [], declarationStart = -1)

let renderTrait =
    TraitDecl(name = "Render", typeParameters = [TypeParameter(name = "a")], supertraits = [], methods = [TraitMethodDecl(name = "render", signature = TypeArrow(
        TypeNamed("a"),
        TypeNamed("Str"),
        [],
        None
    ), defaultImplementation = "render"
    |> ExprQualifiedVar("Render")
    |> Some)])

let loggingCapability =
    CapabilityDecl(name = "Logging", typeParameters = [], operations = [CapabilityOperation(name = "write", signature = None
    |> TypeArrow(TypeNamed("Str"))(TypeNamed("Logging"))([])
    |> Some)])

let utilProgram =
    ProgramSyntax(items = [TopLevelType(
        TypeDecl(name = "Tree", typeParameters = [], constructors = [TypeConstructor(name = "Node", parameters = [TypeApplied(
            "Tree",
            []
        )], fieldNames = [])], isRecord = false, derivingTraits = [])
    ), TopLevelTrait(renderTrait), TopLevelCapability(loggingCapability), TopLevelLet(
        binding("value")(ExprInt(42)),
        false
    ), TopLevelLet(
        binding("hidden")(ExprVar("value")),
        false
    ), TopLevelLet(binding("loop")(ExprVar("loop")))(true)], body = None)

let utilInterface =
    ModuleImportInterface(name = "Foo.Util", exports = [ImportTypeExport(
        "Tree"
    ), ImportConstructorExport(
        "Node"
    ), ImportTypeExport(
        "Render"
    ), ImportTypeExport(
        "Logging"
    ), ImportValueExport("value")])

let utilUnit = SemanticStitchUnit(name = "Foo.Util", packageId = "dep", sourcePath = "/dep/Foo/Util.ash", imports = [], interface = utilInterface, program = utilProgram, isEntry = false)

let mainProgram =
    ProgramSyntax(items = [TopLevelLet(binding("before")(ExprVar("later")))(false), false
    |> TopLevelLet(binding("later")(ExprVar("value")))
    |> TopLevelAt(TextSpan(start = 20, end = 40)), TopLevelLet(None
    |> ExprLambda("value")(ExprVar("value"))
    |> binding("shadow"))(false), TopLevelLet("value"
    |> ExprQualifiedVar("Util")
    |> binding("qualified"))(false), TopLevelLet(None
    |> ExprMatch(ExprVar("Node"))([(PatternConstructor("Node")([]), ExprVar("value"), None), (PatternVar("Node"), ExprVar("value"), None), (PatternVar("value"), ExprVar("value"), None)])
    |> binding("matched"))(false), TopLevelLet("render"
    |> ExprQualifiedVar("Util.Render")
    |> binding("method"))(false), TopLevelLet([(Some(
        "Util.Logging"
    ), "write", [PatternVar("message")], ExprVar("message"))]
    |> ExprHandle(ExprQualifiedVar("Util.Logging")("write"))
    |> binding("handled"))(false), TopLevelTypeAlias(
        TypeAliasDecl(name = "Local", typeParameters = [], target = TypeNamed("Foo.Util.Tree"))
    ), TopLevelLet(binding("copied")(ExprVar("copy")))(false)], body = Some(ExprTuple([ExprVar("before"), ExprVar("later")])))

// An intrinsic builtin module plans as an empty program whose interface names its members.
let intrinsicUnit = SemanticStitchUnit(name = "Ashes.Internal", packageId = "ashes-core", sourcePath = "<builtin>", imports = [], interface = ModuleImportInterface(name = "Ashes.Internal", exports = [ImportValueExport("deepCopy")]), program = ProgramSyntax(items = [], body = None), isEntry = false)

let mainInterface =
    ModuleImportInterface(name = "Main", exports = [ImportValueExport(
        "before"
    ), ImportValueExport("later"), ImportValueExport(
        "shadow"
    ), ImportValueExport(
        "qualified"
    ), ImportValueExport("matched"), ImportValueExport("method"), ImportValueExport("handled"), ImportTypeExport("Local"), ImportValueExport("copied")])

let mainUnit =
    SemanticStitchUnit(name = "Main", packageId = "app", sourcePath = "/app/Main.ash", imports = [ResolvedModuleImport(
        "Foo.Util",
        None,
        1,
        "import Foo.Util"
    ), ResolvedValueImport("Ashes.Internal")("deepCopy")("copy")(2)("import Ashes.Internal.deepCopy as copy")], interface = mainInterface, program = mainProgram, isEntry = true)

let requireProject result =
    match result with
        | Ok(project) -> project
        | Error(_error) -> test.fail("module semantic project should build")

let recursive findRewrittenProgram name units =
    match units with
        | [] -> None
        | SemanticStitchUnit { name = candidate, packageId = _packageId, sourcePath = _sourcePath, imports = _imports, interface = _interface, program = program, isEntry = _isEntry } :: rest ->
            if candidate == name
            then Some(program)
            else findRewrittenProgram(name)(rest)

let requireProgram name units =
    match findRewrittenProgram(name)(units) with
        | Some(program) -> program
        | None -> test.fail("rewritten module should exist")

let expectTreeDeclaration item =
    match item with
        | TopLevelType(TypeDecl { name = "Tree", constructors = TypeConstructor { name = "Node", parameters = TypeApplied("Tree", []) :: [] } :: [] }) -> Unit
        | _ -> test.fail("type declarations and recursive constructor fields should use stitched names")

let expectTraitDeclaration item =
    match item with
        | TopLevelTrait(TraitDecl { name = "Foo_Util_Render", methods = TraitMethodDecl { defaultImplementation = Some(ExprQualifiedVar("Foo_Util_Render", "render")) } :: [] }) -> Unit
        | _ -> test.fail("trait declarations and method references should use stitched names")

let expectCapabilityDeclaration item =
    match item with
        | TopLevelCapability(CapabilityDecl { name = "Logging", operations = CapabilityOperation { signature = Some(TypeArrow(_argument, TypeNamed("Logging"), _capabilities, _tail)) } :: [] }) -> Unit
        | _ -> test.fail("capability declarations and recursive signatures should use stitched names")

let expectUtilRewriting units =
    match requireProgram("Foo.Util")(units) with
        | ProgramSyntax { items = tree :: traitItem :: capabilityItem :: TopLevelLet(LetBindingSyntax { name = "Foo_Util_value", value = ExprInt(42) }, false) :: TopLevelLet(LetBindingSyntax { name = "__ashes_private_value_Foo_Util_hidden", value = ExprVar("Foo_Util_value") }, false) :: TopLevelLet(LetBindingSyntax { name = "__ashes_private_value_Foo_Util_loop", value = ExprVar("__ashes_private_value_Foo_Util_loop") }, true) :: [], body = None } ->
            tree
            |> expectTreeDeclaration
            |> (given (_) -> expectTraitDeclaration(traitItem))
            |> (given (_) -> expectCapabilityDeclaration(capabilityItem))
            |> (given (_) -> units)
        | _ -> test.fail("module declarations and self references should use stitched compiler names")

// A bare constructor name in pattern position is rewritten like the constructor pattern; a bare
// name that is a stitched VALUE stays the arm's own local, in the pattern and in the body.
let expectMatchedBinding item =
    match item with
        | TopLevelLet(LetBindingSyntax { name = "matched", value = ExprMatch(ExprVar("Node"), (PatternConstructor("Node", []), ExprVar("Foo_Util_value"), None) :: (PatternVar("Node"), ExprVar("Foo_Util_value"), None) :: (PatternVar("value"), ExprVar("value"), None) :: [], None) }, false) -> Unit
        | _ -> test.fail("constructor expressions and patterns should use stitched names")

let expectHandlerBinding item =
    match item with
        | TopLevelLet(LetBindingSyntax { name = "handled", value = ExprHandle(ExprQualifiedVar("Logging", "write"), (Some("Logging"), "write", PatternVar("message") :: [], ExprVar("message")) :: []) }, false) -> Unit
        | _ -> test.fail("capability operations and handler arms should use stitched names while preserving arm locals")

let expectMainRewriting units =
    match requireProgram("Main")(units) with
        | ProgramSyntax { items = TopLevelLet(LetBindingSyntax { name = "before", value = ExprVar("later") }, false) :: TopLevelAt(TextSpan { start = 20, end = 40 }, TopLevelLet(LetBindingSyntax { name = "later", value = ExprVar("Foo_Util_value") }, false)) :: TopLevelLet(LetBindingSyntax { name = "shadow", value = ExprLambda("value", ExprVar("value"), None) }, false) :: TopLevelLet(LetBindingSyntax { name = "qualified", value = ExprVar("Foo_Util_value") }, false) :: matched :: TopLevelLet(LetBindingSyntax { name = "method", value = ExprQualifiedVar("Foo_Util_Render", "render") }, false) :: handler :: TopLevelTypeAlias(TypeAliasDecl { name = "Local", target = TypeNamed("Tree") }) :: TopLevelLet(LetBindingSyntax { name = "copied", value = ExprQualifiedVar("Ashes.Internal", "deepCopy") }, false) :: [], body = Some(ExprTuple(ExprVar("before") :: ExprVar("later") :: [])) } ->
            matched
            |> expectMatchedBinding
            |> (given (_) -> expectHandlerBinding(handler))
            |> (given (_) -> units)
        | _ -> test.fail("imports, qualifiers, types, spans, and lexical shadows should rewrite deterministically")

// A private record type keeps different compiler names for its type and its constructor; a
// record literal and a record pattern name the constructor, which the lowering looks up.
let privateProgram =
    ProgramSyntax(items = [TopLevelType(
        TypeDecl(name = "Cfg", typeParameters = [], constructors = [TypeConstructor(name = "Cfg", parameters = [TypeNamed("Int")], fieldNames = ["width"])], isRecord = true, derivingTraits = ["Render"])
    ), TopLevelLet(false
    |> ExprRecord("Cfg")([("width", ExprInt(1))])
    |> binding("make"))(false), TopLevelLet(
        None
        |> ExprMatch(ExprVar("make"))([(PatternRecord("Cfg")([("width", PatternVar("w"))]), ExprVar("w"), None)])
        |> binding("widthOf"),
        false
    )], body = None)

let privateUnit = SemanticStitchUnit(name = "Priv", packageId = "dep", sourcePath = "/dep/Priv.ash", imports = [], interface = ModuleImportInterface(name = "Priv", exports = [ImportValueExport("make"), ImportValueExport("widthOf")]), program = privateProgram, isEntry = false)

let expectPrivateRecordRewriting units =
    match requireProgram("Priv")(units) with
        | ProgramSyntax { items = TopLevelType(TypeDecl { name = "AshesPrivateType_Priv_Cfg", derivingTraits = "Foo_Util_Render" :: [], constructors = TypeConstructor { name = "AshesPrivateConstructor_Priv_Cfg" } :: [] }) :: TopLevelLet(LetBindingSyntax { value = ExprRecord("AshesPrivateConstructor_Priv_Cfg", _fields, _multiline) }, false) :: TopLevelLet(LetBindingSyntax { value = ExprMatch(_scrutinee, (PatternRecord("AshesPrivateConstructor_Priv_Cfg", _patternFields), _body, None) :: [], _fallback) }, false) :: [] } -> units
        | _ -> test.fail("a private record literal and pattern should name the private constructor")

// A capability keeps its declared name in every module: the interface exports no capability, so
// a module naming one it reaches through an import, or does not import at all, names it bare,
// beside a type of the same name, and a second module declaring it is left to the inference.
let storeCapability =
    CapabilityDecl(name = "Store", typeParameters = [], operations = [CapabilityOperation(name = "load", signature = None
    |> TypeArrow(TypeUnit)(TypeNamed("Str"))([])
    |> Some)])

let storeUnit =
    SemanticStitchUnit(name = "Caps", packageId = "app", sourcePath = "/app/Caps.ash", imports = [], interface = ModuleImportInterface(name = "Caps", exports = [ImportValueExport("helper")]), program = ProgramSyntax(items = [TopLevelCapability(
        storeCapability
    ), TopLevelLet(binding("helper")(ExprInt(1)))(false)], body = None), isEntry = false)

let storeProvide =
    TopLevelProvide(
        ProvideDecl(capabilityName = "Store", typeArguments = [], bindings = [ProvideBinding(operationName = "load", implementation = None |> ExprLambda("u")(ExprString("provided")))])
    )

let storeHandler =
    [(Some("Store"), "load", [PatternVar("u")], ExprString("handled"))] |> ExprHandle("load"
    |> ExprQualifiedVar("Store")
    |> ExprPerform)

let storeReader =
    LetBindingSyntax(name = "read", value = storeHandler, sugarParameters = [], typeAnnotation = None
    |> TypeArrow(TypeUnit)(TypeNamed("Str"))([("Store", [])])
    |> Some, requirements = [], declarationStart = -1)

let storeShadowUnit =
    SemanticStitchUnit(name = "Shadow", packageId = "app", sourcePath = "/app/Shadow.ash", imports = [], interface = ModuleImportInterface(name = "Shadow", exports = []), program = ProgramSyntax(items = [TopLevelType(
        TypeDecl(name = "Store", typeParameters = [], constructors = [TypeConstructor(name = "Box", parameters = [TypeNamed("Int")], fieldNames = [])], isRecord = false, derivingTraits = [])
    ), storeProvide], body = None), isEntry = false)

let storeMainUnit = SemanticStitchUnit(name = "Main", packageId = "app", sourcePath = "/app/Main.ash", imports = [ResolvedModuleImport("Caps")(None)(1)("import Caps")], interface = ModuleImportInterface(name = "Main", exports = [ImportValueExport("read")]), program = ProgramSyntax(items = [storeProvide, TopLevelLet(storeReader)(false)], body = None), isEntry = true)

let storeAgainUnit = SemanticStitchUnit(name = "Again", packageId = "app", sourcePath = "/app/Again.ash", imports = [], interface = ModuleImportInterface(name = "Again", exports = []), program = ProgramSyntax(items = [TopLevelCapability(storeCapability)], body = None), isEntry = false)

let storeUnits = [storeUnit, storeShadowUnit, storeAgainUnit, storeMainUnit]

let expectStoreProvide item =
    match item with
        | TopLevelProvide(ProvideDecl { capabilityName = "Store" }) -> Unit
        | _ -> test.fail("a provide should name an imported capability by its declared name")

let expectStoreCapabilityRewriting units =
    match (requireProgram("Caps")(units), requireProgram("Shadow")(units), requireProgram("Main")(units)) with
        | (ProgramSyntax { items = TopLevelCapability(CapabilityDecl { name = "Store" }) :: _rest }, ProgramSyntax { items = TopLevelType(TypeDecl { name = "AshesPrivateType_Shadow_Store" }) :: shadowProvide :: [] }, ProgramSyntax { items = mainProvide :: TopLevelLet(LetBindingSyntax { name = "read", value = ExprHandle(ExprPerform(ExprQualifiedVar("Store", "load")), (Some("Store"), "load", PatternVar("u") :: [], ExprString("handled")) :: []), typeAnnotation = Some(TypeArrow(TypeUnit, TypeNamed("Str"), ("Store", []) :: [], None)) }, false) :: [] }) ->
            shadowProvide
            |> expectStoreProvide
            |> (given (_) -> expectStoreProvide(mainProvide))
        | _ -> test.fail("capability declarations, providers, handler arms, performs and needs rows should keep the declared capability name")

let runCapabilityNameTests unit =
    storeUnits
    |> buildStitchedSemanticProject
    |> requireProject
    |> (given (project) -> rewriteStitchedProjectReferences(project)(storeUnits))
    |> expectStoreCapabilityRewriting

let runModuleReferenceRewritingTests unit =
    [utilUnit, intrinsicUnit, mainUnit, privateUnit]
    |> buildStitchedSemanticProject
    |> requireProject
    |> (given (project) -> rewriteStitchedProjectReferences(project)([utilUnit, intrinsicUnit, mainUnit, privateUnit]))
    |> expectUtilRewriting
    |> (given (units) -> expectMainRewriting(units))
    |> (given (units) -> expectPrivateRecordRewriting(units))
    |> (given (_) -> runCapabilityNameTests(Unit))
