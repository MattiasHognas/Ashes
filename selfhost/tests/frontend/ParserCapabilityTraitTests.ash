import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import ParserProgramTests
let checkDeclarations unit =
    (let source = "capability State(a) =\n    | get : Unit -> a\n    | put : a -> Unit\nprovide State(Int) =\n    | get = 0\n    | put = given value -> Unit\ntrait Display(a) requires {Eq(a)} =\n    | display : a -> Str\n    | fallback : a -> Str = given value -> \"?\"\nimplement Display(Int) =\n    | display = given value -> \"int\"\n0"
    in
        match parseProgram(source) with
            | ProgramParseResult { program = ProgramSyntax { items = capabilityItem :: providerItem :: traitItem :: implementationItem :: [], body = Some(_body) }, diagnostics = diagnostics } ->
                diagnostics
                |> test.assertEqual([])
                |> (given (_) ->
                    match ParserProgramTests.unspanTopLevel(capabilityItem) with
                        | TopLevelCapability(CapabilityDecl { name = "State", typeParameters = TypeParameter { name = "a" } :: [], operations = CapabilityOperation { name = "get", signature = Some(_getType) } :: CapabilityOperation { name = "put", signature = Some(_putType) } :: [] }) -> Unit
                        | _ -> test.fail("expected capability"))
                |> (given (_) ->
                    match ParserProgramTests.unspanTopLevel(providerItem) with
                        | TopLevelProvide(ProvideDecl { capabilityName = "State", typeArguments = _argument :: [], bindings = ProvideBinding { operationName = "get", implementation = _get } :: ProvideBinding { operationName = "put", implementation = _put } :: [] }) -> Unit
                        | _ -> test.fail("expected provider"))
                |> (given (_) ->
                    match ParserProgramTests.unspanTopLevel(traitItem) with
                        | TopLevelTrait(TraitDecl { name = "Display", typeParameters = _parameter :: [], supertraits = TraitConstraintSyntax { traitName = "Eq", typeArguments = _superArgument :: [] } :: [], methods = TraitMethodDecl { name = "display", signature = _displayType, defaultImplementation = None } :: TraitMethodDecl { name = "fallback", signature = _fallbackType, defaultImplementation = Some(_fallback) } :: [] }) -> Unit
                        | _ -> test.fail("expected trait"))
                |> (given (_) ->
                    match ParserProgramTests.unspanTopLevel(implementationItem) with
                        | TopLevelImplementation(TraitImplementationDecl { traitName = "Display", typeArguments = _argument :: [], requirements = [], bindings = TraitImplementationMethodBinding { methodName = "display", implementation = _implementation } :: [] }) -> Unit
                        | _ -> test.fail("expected implementation"))
            | _ -> test.fail("expected capability and trait declarations"))

let checkLiteralProviderBindings unit =
    match parseProgram("provide State =\n    | get = 0\n    | put = 1\n0") with
        | ProgramParseResult { program = ProgramSyntax { items = item :: [], body = Some(_body) }, diagnostics = diagnostics } ->
            diagnostics
            |> test.assertEqual([])
            |> (given (_) ->
                match ParserProgramTests.unspanTopLevel(item) with
                    | TopLevelProvide(ProvideDecl { capabilityName = "State", typeArguments = [], bindings = _first :: _second :: [] }) -> Unit
                    | _ -> test.fail("expected two literal provider bindings"))
        | _ -> test.fail("expected literal provider")

let expectDiagnostics source =
    match parseProgram(source) with
        | ProgramParseResult { program = _program, diagnostics = _diagnostic :: _tail } -> Unit
        | _ -> test.fail("expected declaration diagnostic")

let run unit =
    unit
    |> checkDeclarations
    |> checkLiteralProviderBindings
    |> (given (_) -> expectDiagnostics("capability Empty =\n0"))
    |> (given (_) -> expectDiagnostics("provide Empty =\n0"))
    |> (given (_) -> expectDiagnostics("trait Empty =\n0"))
    |> (given (_) -> expectDiagnostics("implement Empty(Int) =\n0"))
