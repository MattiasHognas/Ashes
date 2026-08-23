import Ashes.IO
import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.ExternalTyping
import AshesCompiler.Semantics.ProgramInference
import AshesCompiler.Semantics.StandardTraits
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.TypeResolution
import AshesCompiler.Semantics.Types
let assertEqualNamed message expected actual =
    if expected == actual
    then Unit
    else test.fail(message)

let recursive addExternalTypes declarations environment =
    match declarations with
        | [] -> environment
        | ExternalOpaqueType(name, destructor) :: tail ->
            environment
            |> addInferenceExternalType(name)(destructor)
            |> addExternalTypes(tail)
        | _head :: tail -> addExternalTypes(tail)(environment)

let typeFunction declarations function =
    (let context =
        Unit
        |> standardTraitEnvironment
        |> addExternalTypes(declarations)
        |> inferenceTypeResolutionContext
    in
        match typeExternalFunction(function)(declarations)(context) with
            | ExternalFunctionTypingResult { typing = Some(typing), error = None } -> typing
            | ExternalFunctionTypingResult { error = Some(error) } ->
                error
                |> Ashes.Trait.Show.show
                |> (given (shown) -> "external function should type: " + shown)
                |> test.fail)

let expectTypingError expected declarations function =
    (let context =
        Unit
        |> standardTraitEnvironment
        |> addExternalTypes(declarations)
        |> inferenceTypeResolutionContext
    in
        match typeExternalFunction(function)(declarations)(context) with
            | ExternalFunctionTypingResult { typing = None, error = Some(actual) } -> test.assertEqual(expected)(actual)
            | _ -> test.fail("external function should fail typing"))

let functionCall name argument =
    ExprCall(
        ExprVar(name),
        argument,
        false,
        callArgumentsInline
    )

let inferExternalProgram items body =
    inferProgramFromPackage("external-tests")(
        standardTraitEnvironment(Unit)
    )(ProgramSyntax(items = items, body = Some(body)))

let closedCapabilities capabilities = NeedsRowSyntax(capabilities = capabilities, tailVariable = None)

let supportType name arguments =
    match Unit
    |> standardTraitEnvironment
    |> inferenceTypeResolutionContext
    |> resolveSemanticTypeApplication(name)(arguments) with
        | TypeResolutionResult { semanticType = semanticType, error = None } -> semanticType
        | _ -> test.fail("standard support type should resolve")

let recursive innermostCapabilityRow semanticType =
    match semanticType with
        | SemFunction(_argument, SemFunction(nextArgument, nextResult, nextRow), _row) ->
            nextRow
            |> SemFunction(nextArgument)(nextResult)
            |> innermostCapabilityRow
        | SemFunction(_argument, _result, row) -> row
        | _ -> None

let expectProgramType expected items body =
    match inferExternalProgram(items)(body) with
        | ProgramInferenceResult { semanticType = semanticType, substitution = substitution, error = None } ->
            semanticType
            |> applySubstitution(substitution)
            |> test.assertEqual(expected)
        | ProgramInferenceResult { error = Some(_error) } -> test.fail("external program should infer")

let expectScalarPointerAndSymbolTyping unit =
    (let handleType = ExternalOpaqueType("Handle")(None)
    in
        let function =
            ExternalFunction(
                "transform",
                [
                    ParsedNamed("Int"),
                    ParsedNamed("u32"),
                    ParsedNamed("f32"),
                    ParsedPointer(ParsedNamed("Handle"))
                ],
                ParsedNamed("Bool"),
                Some("pkg@native_transform@libnative.so"),
                [
                    ExternalOwnershipUnspecified,
                    ExternalOwnershipUnspecified,
                    ExternalOwnershipUnspecified,
                    ExternalOwnershipUnspecified
                ],
                [
                    CapabilityRefSyntax(name = "FileRead", args = []),
                    CapabilityRefSyntax(name = "Entropy", args = [])
                ]
                |> closedCapabilities
                |> Some
            )
        in
            match typeFunction([handleType, function])(function) with
                | ExternalFunctionTyping { symbolName = symbolName, libraryName = libraryName, sourceResult = sourceResult, directType = directType, firstClassType = firstClassType, runtimeCapabilities = runtimeCapabilities } ->
                    symbolName
                    |> assertEqualNamed("external symbol should split at the last @")(
                        "pkg@native_transform"
                    )
                    |> (given (_) ->
                        assertEqualNamed("external library should follow the last @")(
                            Some("libnative.so")
                        )(libraryName))
                    |> (given (_) ->
                        assertEqualNamed(
                            "external scalar return should type as Bool",
                            SemBool,
                            sourceResult
                        ))
                    |> (given (_) ->
                        match firstClassType with
                            | Some(_) -> Unit
                            | None -> test.fail("ordinary external should have a first-class type"))
                    |> (given (_) ->
                        assertEqualNamed("external capabilities should be canonical")([
                            "Entropy",
                            "FileRead"
                        ])(runtimeCapabilities))
                    |> (given (_) ->
                        directType
                        |> innermostCapabilityRow
                        |> (given (row) ->
                            match row with
                                | Some(SemRow(capabilities, tail)) ->
                                    match capabilities with
                                        | SemCapability("Entropy", []) :: SemCapability("FileRead", []) :: [] ->
                                            assertEqualNamed("external capability row should be closed")(
                                                None
                                            )(tail)
                                        | [] -> test.fail("innermost external capability row should not be empty")
                                        | SemCapability("FileRead", []) :: SemCapability("Entropy", []) :: [] ->
                                            test.fail(
                                                "innermost external capabilities should be canonical"
                                            )
                                        | _ -> test.fail("innermost external capability entries should match")
                                | Some(_) -> test.fail("innermost external row should be a capability row")
                                | None -> test.fail("capabilities should decorate the innermost arrow"))))

let expectBufferOutAndNativeStringTyping unit =
    (let handleType = ExternalOpaqueType("Handle")(None)
    in
        let function =
            ExternalFunction(
                "resolve",
                [ParsedBuffer(ParsedNamed("Handle")), ParsedOut(ParsedNamed("Handle"))],
                ParsedNativeString(false)(FfiStringOwned)(Some("disposeText")),
                None,
                [ExternalOwnershipUnspecified, ExternalOwnershipUnspecified],
                None
            )
        in
            match typeFunction([handleType, function])(function) with
                | ExternalFunctionTyping { parameters = parameters, sourceResult = sourceResult, firstClassType = None } ->
                    let maybeHandle = supportType("Maybe")([SemOpaque("Handle")])
                    in
                        parameters
                        |> assertEqualNamed("external parameter source shapes should match")([
                            ExternalParameterTyping(syntax = ParsedBuffer(ParsedNamed("Handle")), sourceArgument = Some(SemList(SemOpaque("Handle"))), sourceOutput = None, ownership = ExternalOwnershipUnspecified, directOnly = true),
                            ExternalParameterTyping(syntax = ParsedOut(ParsedNamed("Handle")), sourceArgument = None, sourceOutput = Some(maybeHandle), ownership = ExternalOwnershipUnspecified, directOnly = true)
                        ])
                        |> (given (_) ->
                            match sourceResult with
                                | SemTuple(SemNamed(_, "Result", SemString :: SemString :: []) :: output :: []) ->
                                    assertEqualNamed("external out result should follow the return")(
                                        maybeHandle
                                    )(output)
                                | _ -> test.fail("external return and out results should compose"))
                | _ -> test.fail("buffer, out, and native string source shapes should be retained"))

let expectResourceOwnershipTyping unit =
    (let resource = ExternalOpaqueType("Resource")(Some("closeResource"))
    in
        let close =
            ExternalFunction(
                "closeResource",
                [ParsedNamed("Resource")],
                ParsedNamed("void"),
                None,
                [ExternalOwnershipConsume],
                None
            )
        in
            let inspect =
                ExternalFunction(
                    "inspectResource",
                    [ParsedNamed("Resource")],
                    ParsedNamed("Int"),
                    None,
                    [ExternalOwnershipBorrow],
                    None
                )
            in
                close
                |> typeFunction([resource, close, inspect])
                |> (given (typing) ->
                    match typing with
                        | ExternalFunctionTyping { firstClassType = None, runtimeCapabilities = [] } -> Unit
                        | _ -> test.fail("resource destructor should be direct and possession-only"))
                |> (given (_) ->
                    inspect
                    |> typeFunction([resource, close, inspect])
                    |> (given (typing) ->
                        match typing with
                            | ExternalFunctionTyping { firstClassType = None, runtimeCapabilities = capabilities } ->
                                test.assertEqual(
                                    ["UnsafeFfi"],
                                    capabilities
                                )
                            | _ -> test.fail("borrowed resource call should remain direct")))
                |> (given (_) ->
                    expectTypingError(
                        InvalidExternalOwnershipMarker(1),
                        [resource, close],
                        ExternalFunction(
                            "bad",
                            [ParsedNamed("Resource")],
                            ParsedNamed("Int"),
                            None,
                            [ExternalOwnershipUnspecified],
                            None
                        )
                    )))

let expectProgramIntegration unit =
    (let make =
        ExternalFunction(
            "makeHandle",
            [ParsedNamed("Int")],
            ParsedNamed("Handle"),
            None,
            [ExternalOwnershipUnspecified],
            Some(NeedsRowSyntax(capabilities = [], tailVariable = None))
        )
    in
        let handleType = ExternalOpaqueType("Handle")(None)
        in
            expectProgramType(
                SemOpaque("Handle"),
                [TopLevelExternal(make), TopLevelExternal(handleType)],
                functionCall("makeHandle")(ExprInt(42))
            ))

let expectNullaryAndOutCallTyping unit =
    (let getPid =
        ExternalFunction(
            "getPid",
            [],
            ParsedNamed("Int"),
            None,
            [],
            Some(NeedsRowSyntax(capabilities = [], tailVariable = None))
        )
    in
        let handleType = ExternalOpaqueType("Handle")(None)
        in
            let create =
                ExternalFunction(
                    "create",
                    [ParsedOut(ParsedNamed("Handle"))],
                    ParsedNamed("void"),
                    None,
                    [ExternalOwnershipUnspecified],
                    Some(NeedsRowSyntax(capabilities = [], tailVariable = None))
                )
            in
                ExprTuple([])
                |> functionCall("getPid")
                |> expectProgramType(
                    SemInt,
                    [TopLevelExternal(getPid)]
                )
                |> (given (_) ->
                    match inferExternalProgram([TopLevelExternal(getPid)])(ExprVar("getPid")) with
                        | ProgramInferenceResult { error = Some(actual) } ->
                            test.assertEqual(ProgramExpressionError(
                                ExternalFunctionRequiresDirectCall("getPid")
                            ))(actual)
                        | _ -> test.fail("nullary external should reject first-class use"))
                |> (given (_) ->
                    expectProgramType(
                        SemNamed(1)("Maybe")([SemOpaque("Handle")]),
                        [TopLevelExternal(handleType), TopLevelExternal(create)],
                        functionCall("create")(ExprTuple([]))
                    )))

let expectInvalidCapabilityRows unit =
    (let invalid =
        ExternalFunction(
            "invalid",
            [ParsedNamed("Int")],
            ParsedNamed("Int"),
            None,
            [ExternalOwnershipUnspecified],
            [
                CapabilityRefSyntax(name = "UserCapability", args = [])
            ]
            |> closedCapabilities
            |> Some
        )
    in
        expectTypingError(
            InvalidExternalCapabilityRow("UserCapability"),
            [invalid],
            invalid
        ))

let runExternalTypingTests unit =
    unit
    |> expectScalarPointerAndSymbolTyping
    |> expectBufferOutAndNativeStringTyping
    |> expectResourceOwnershipTyping
    |> expectProgramIntegration
    |> expectNullaryAndOutCallTyping
    |> expectInvalidCapabilityRows
    |> (given (_) -> Ashes.IO.print("all self-hosted external typing tests passed"))
