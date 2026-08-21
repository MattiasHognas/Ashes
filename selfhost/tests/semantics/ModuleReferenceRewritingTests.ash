import Ashes.Test as test
import AshesCompiler.Frontend.ImportResolution
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Frontend.Token
import AshesCompiler.Semantics.ModuleSemanticStitching
import AshesCompiler.Semantics.ModuleReferenceRewriting
export (
    value runModuleReferenceRewritingTests,
)

let binding name value = LetBindingSyntax(name = name, value = value, sugarParameters = [], typeAnnotation = None, requirements = [])

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
    |> ExprMatch(ExprVar("Node"))([(PatternConstructor("Node")([]), ExprVar("value"), None)])
    |> binding("matched"))(false), TopLevelLet("render"
    |> ExprQualifiedVar("Util.Render")
    |> binding("method"))(false), TopLevelLet([(Some(
        "Util.Logging"
    ), "write", [PatternVar("message")], ExprVar("message"))]
    |> ExprHandle(ExprQualifiedVar("Util.Logging")("write"))
    |> binding("handled"))(false), TopLevelTypeAlias(
        TypeAliasDecl(name = "Local", typeParameters = [], target = TypeNamed("Foo.Util.Tree"))
    )], body = Some(ExprTuple([ExprVar("before"), ExprVar("later")])))

let mainInterface =
    ModuleImportInterface(name = "Main", exports = [ImportValueExport(
        "before"
    ), ImportValueExport("later"), ImportValueExport(
        "shadow"
    ), ImportValueExport(
        "qualified"
    ), ImportValueExport("matched"), ImportValueExport("method"), ImportValueExport("handled"), ImportTypeExport("Local")])

let mainUnit =
    SemanticStitchUnit(name = "Main", packageId = "app", sourcePath = "/app/Main.ash", imports = [ResolvedModuleImport(
        "Foo.Util",
        None,
        1,
        "import Foo.Util"
    )], interface = mainInterface, program = mainProgram, isEntry = true)

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
        | TopLevelType(TypeDecl { name = "Foo_Util_Tree", constructors = TypeConstructor { name = "Foo_Util_Node", parameters = TypeApplied("Foo_Util_Tree", []) :: [] } :: [] }) -> Unit
        | _ -> test.fail("type declarations and recursive constructor fields should use stitched names")

let expectTraitDeclaration item =
    match item with
        | TopLevelTrait(TraitDecl { name = "Foo_Util_Render", methods = TraitMethodDecl { defaultImplementation = Some(ExprQualifiedVar("Foo_Util_Render", "render")) } :: [] }) -> Unit
        | _ -> test.fail("trait declarations and method references should use stitched names")

let expectCapabilityDeclaration item =
    match item with
        | TopLevelCapability(CapabilityDecl { name = "Foo_Util_Logging", operations = CapabilityOperation { signature = Some(TypeArrow(_argument, TypeNamed("Foo_Util_Logging"), _capabilities, _tail)) } :: [] }) -> Unit
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

let expectMatchedBinding item =
    match item with
        | TopLevelLet(LetBindingSyntax { name = "matched", value = ExprMatch(ExprVar("Foo_Util_Node"), (PatternConstructor("Foo_Util_Node", []), ExprVar("Foo_Util_value"), None) :: [], None) }, false) -> Unit
        | _ -> test.fail("constructor expressions and patterns should use stitched names")

let expectHandlerBinding item =
    match item with
        | TopLevelLet(LetBindingSyntax { name = "handled", value = ExprHandle(ExprQualifiedVar("Foo_Util_Logging", "write"), (Some("Foo_Util_Logging"), "write", PatternVar("message") :: [], ExprVar("message")) :: []) }, false) -> Unit
        | _ -> test.fail("capability operations and handler arms should use stitched names while preserving arm locals")

let expectMainRewriting units =
    match requireProgram("Main")(units) with
        | ProgramSyntax { items = TopLevelLet(LetBindingSyntax { name = "before", value = ExprVar("later") }, false) :: TopLevelAt(TextSpan { start = 20, end = 40 }, TopLevelLet(LetBindingSyntax { name = "later", value = ExprVar("Foo_Util_value") }, false)) :: TopLevelLet(LetBindingSyntax { name = "shadow", value = ExprLambda("value", ExprVar("value"), None) }, false) :: TopLevelLet(LetBindingSyntax { name = "qualified", value = ExprVar("Foo_Util_value") }, false) :: matched :: TopLevelLet(LetBindingSyntax { name = "method", value = ExprQualifiedVar("Foo_Util_Render", "render") }, false) :: handler :: TopLevelTypeAlias(TypeAliasDecl { name = "Local", target = TypeNamed("Foo_Util_Tree") }) :: [], body = Some(ExprTuple(ExprVar("before") :: ExprVar("later") :: [])) } ->
            matched
            |> expectMatchedBinding
            |> (given (_) -> expectHandlerBinding(handler))
            |> (given (_) -> units)
        | _ -> test.fail("imports, qualifiers, types, spans, and lexical shadows should rewrite deterministically")

let runModuleReferenceRewritingTests unit =
    [utilUnit, mainUnit]
    |> buildStitchedSemanticProject
    |> requireProject
    |> (given (project) -> rewriteStitchedProjectReferences(project)([utilUnit, mainUnit]))
    |> expectUtilRewriting
    |> (given (units) -> expectMainRewriting(units))
