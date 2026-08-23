import Ashes.IO
import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.ExternalAbi
import AshesCompiler.Semantics.ExternalTyping
import AshesCompiler.Semantics.ProgramInference
import AshesCompiler.Semantics.StandardTraits
import AshesCompiler.Semantics.TypeResolution
import AshesCompiler.Semantics.Types
let assertEqualNamed message expected actual =
    if expected == actual
    then Unit
    else test.fail(message)

let inferExternalAbi items =
    inferProgramFromPackage("external-abi-tests")(
        standardTraitEnvironment(Unit)
    )(ProgramSyntax(items = items, body = Some(ExprInt(0))))

let expectExternalAbi items =
    match inferExternalAbi(items) with
        | ProgramInferenceResult { externalAbi = Some(metadata), error = None } -> metadata
        | ProgramInferenceResult { error = Some(error) } ->
            test.fail(
                "external ABI program failed: " + Ashes.Trait.Show.show(error)
            )
        | _ -> test.fail("external ABI program should validate")

let appendShownExternalAbiError error prefix = prefix + Ashes.Trait.Show.show(error)

let expectExternalAbiError expected items =
    match inferExternalAbi(items) with
        | ProgramInferenceResult { error = Some(ProgramExternalAbiError(actual)) } ->
            if expected == actual
            then Unit
            else
                "external ABI error mismatch: expected "
                |> appendShownExternalAbiError(expected)
                |> (given (message) -> message + ", actual ")
                |> appendShownExternalAbiError(actual)
                |> test.fail
        | _ -> test.fail("external ABI program should fail validation")

let noCapabilities = Some(NeedsRowSyntax(capabilities = [], tailVariable = None))

let recursive unspecified count =
    if count <= 0
    then []
    else ExternalOwnershipUnspecified :: unspecified(count - 1)

let recursive findFunction name functions =
    match functions with
        | [] -> test.fail("external function metadata should exist")
        | (ExternalFunctionAbi { name = candidate } as function) :: tail ->
            if name == candidate
            then function
            else findFunction(name)(tail)

let completeHandleType = ExternalOpaqueType("Handle")(None)

let completeResourceType = ExternalOpaqueType("Resource")(Some("closeResource"))

let completeCloseResource =
    ExternalFunction(
        "closeResource",
        [
            ParsedNamed("Resource")
        ],
        ParsedNamed("void"),
        Some("resource_close@libresource.so"),
        [
            ExternalOwnershipConsume
        ],
        None
    )

let completeDisposeText =
    ExternalFunction(
        "disposeText",
        [
            ParsedPointer(ParsedNamed("u8"))
        ],
        ParsedNamed("void"),
        Some("text_dispose@libtext.so"),
        [
            ExternalOwnershipUnspecified
        ],
        noCapabilities
    )

let completeInspectCapabilities =
    Some(NeedsRowSyntax(capabilities = [
        CapabilityRefSyntax(name = "FileRead", args = []),
        CapabilityRefSyntax(name = "Entropy", args = [])
    ], tailVariable = None))

let completeInspect =
    ExternalFunction(
        "inspect",
        [
            ParsedNamed("f32"),
            ParsedBuffer(ParsedNamed("Handle")),
            ParsedOut(ParsedNamed("Resource"))
        ],
        ParsedNativeString(false)(FfiStringOwned)(Some("disposeText")),
        Some("inspect_native@libapi.so"),
        unspecified(3),
        completeInspectCapabilities
    )

let completeExternalDeclarations =
    [
        TopLevelExternal(completeHandleType),
        TopLevelExternal(completeResourceType),
        TopLevelExternal(completeCloseResource),
        TopLevelExternal(completeDisposeText),
        TopLevelExternal(completeInspect)
    ]

let assertSymbol expectedName expectedLibrary symbol =
    match symbol with
        | ExternalSymbolReference { symbolName = symbolName, libraryName = libraryName } ->
            symbolName
            |> assertEqualNamed("external symbol should retain its native name")(
                expectedName
            )
            |> (given (_) ->
                assertEqualNamed("external symbol should retain its native library")(
                    expectedLibrary
                )(libraryName))

let assertCompleteResource resources =
    match resources with
        | ExternalResourceAbi { name = resourceName, destructor = destructor } :: [] ->
            resourceName
            |> assertEqualNamed("resource metadata should retain its type name")(
                "Resource"
            )
            |> (given (_) ->
                match destructor with
                    | ExternalFunctionAbi { symbol = symbol } ->
                        assertSymbol("resource_close")(
                            Some("libresource.so")
                        )(symbol))
            |> (given (_) ->
                match destructor with
                    | ExternalFunctionAbi { destructorForResource = owner } ->
                        assertEqualNamed(
                            "resource destructor should identify its resource"
                        )(
                            Some("Resource")
                        )(owner))
            |> (given (_) ->
                match destructor with
                    | ExternalFunctionAbi { runtimeCapabilities = capabilities } ->
                        assertEqualNamed(
                            "resource destructor should default to possession-only authority"
                        )(
                            []
                        )(capabilities))
        | _ -> test.fail("resource destructor metadata should be complete")

let assertParameterIndex expected parameter =
    match parameter with
        | ExternalParameterAbi { parameterIndex = actual } -> test.assertEqual(expected)(actual)

let assertParameterAbi expected parameter =
    match parameter with
        | ExternalParameterAbi { abiType = actual } -> test.assertEqual(expected)(actual)

let assertHasSourceArgument parameter =
    match parameter with
        | ExternalParameterAbi { source = source } ->
            match source with
                | ExternalParameterTyping { sourceArgument = Some(_) } -> Unit
                | _ -> test.fail("buffer parameter should retain its source argument")

let assertNoSourceArgument parameter =
    match parameter with
        | ExternalParameterAbi { source = source } ->
            match source with
                | ExternalParameterTyping { sourceArgument = None } -> Unit
                | _ -> test.fail("out parameter should not expose a source argument")

let assertHasSourceOutput parameter =
    match parameter with
        | ExternalParameterAbi { source = source } ->
            match source with
                | ExternalParameterTyping { sourceOutput = Some(_) } -> Unit
                | _ -> test.fail("out parameter should retain its source output")

let assertFirstInspectParameter parameter =
    parameter
    |> assertParameterIndex(0)
    |> (given (_) -> assertParameterAbi(ExternalAbiFloat32)(parameter))

let assertSecondInspectParameter parameter =
    parameter
    |> assertParameterIndex(1)
    |> (given (_) ->
        assertParameterAbi(
            ExternalAbiBuffer(ExternalAbiOpaque("Handle"))
        )(parameter))
    |> (given (_) -> assertHasSourceArgument(parameter))

let assertThirdInspectParameter parameter =
    parameter
    |> assertParameterIndex(2)
    |> (given (_) ->
        assertParameterAbi(
            ExternalAbiOut(ExternalAbiOpaque("Resource"))
        )(parameter))
    |> (given (_) -> assertNoSourceArgument(parameter))
    |> (given (_) -> assertHasSourceOutput(parameter))

let assertInspectParameters parameters =
    match parameters with
        | first :: second :: third :: [] ->
            first
            |> assertFirstInspectParameter
            |> (given (_) -> assertSecondInspectParameter(second))
            |> (given (_) -> assertThirdInspectParameter(third))
        | _ -> test.fail("parameter ABI and source shapes should remain aligned")

let assertInspectReturn returnType =
    match returnType with
        | ExternalAbiNativeString(false, ExternalNativeStringOwned, Some(destructor)) ->
            assertSymbol("text_dispose")(
                Some("libtext.so")
            )(destructor)
        | _ -> test.fail("owned native strings should retain destructor metadata")

let assertInspectSymbol function =
    match function with
        | ExternalFunctionAbi { symbol = symbol } ->
            assertSymbol("inspect_native")(
                Some("libapi.so")
            )(symbol)

let assertInspectFunction functions =
    functions
    |> findFunction("inspect")
    |> (given (function) ->
        function
        |> assertInspectSymbol
        |> (given (_) ->
            match function with
                | ExternalFunctionAbi { parameters = parameters } -> assertInspectParameters(parameters))
        |> (given (_) ->
            match function with
                | ExternalFunctionAbi { returnType = returnType } -> assertInspectReturn(returnType))
        |> (given (_) ->
            match function with
                | ExternalFunctionAbi { runtimeCapabilities = capabilities } ->
                    test.assertEqual([
                        "Entropy",
                        "FileRead"
                    ])(capabilities))
        |> (given (_) ->
            match function with
                | ExternalFunctionAbi { directOnly = directOnly } -> test.assertEqual(true)(directOnly)))

let expectedLibraryImports =
    [
        ExternalLibraryImport(symbolName = "resource_close", libraryName = "libresource.so"),
        ExternalLibraryImport(symbolName = "text_dispose", libraryName = "libtext.so"),
        ExternalLibraryImport(symbolName = "inspect_native", libraryName = "libapi.so")
    ]

let expectedAuthority =
    [
        ExternalAuthorityMetadata(functionName = "closeResource", runtimeCapabilities = []),
        ExternalAuthorityMetadata(functionName = "disposeText", runtimeCapabilities = []),
        ExternalAuthorityMetadata(functionName = "inspect", runtimeCapabilities = [
            "Entropy",
            "FileRead"
        ])
    ]

let assertExternalOpaqueTypes metadata =
    match metadata with
        | ExternalProgramAbi { opaqueTypes = opaqueTypes } ->
            test.assertEqual([
                "Handle",
                "Resource"
            ])(opaqueTypes)

let assertExternalLibraryImports metadata = test.assertEqual(expectedLibraryImports)(metadata.libraryImports)

let assertCompleteExternalMetadata metadata =
    metadata
    |> assertExternalOpaqueTypes
    |> (given (_) ->
        match metadata with
            | ExternalProgramAbi { resources = resources } -> assertCompleteResource(resources))
    |> (given (_) ->
        match metadata with
            | ExternalProgramAbi { functions = functions } -> assertInspectFunction(functions))
    |> (given (_) -> assertExternalLibraryImports(metadata))
    |> (given (_) ->
        match metadata with
            | ExternalProgramAbi { externalAuthority = authority } -> test.assertEqual(expectedAuthority)(authority))

let expectCompleteExternalMetadata unit =
    completeExternalDeclarations
    |> expectExternalAbi
    |> assertCompleteExternalMetadata

let declaredAlias = TypeAliasDecl(name = "NativeCount", typeParameters = [], target = TypeNamed("u32"))

let declaredUserId =
    ZeroCostTypeDecl(name = "UserId", typeParameters = [], constructor = TypeConstructor(name = "UserId", parameters = [
        TypeNamed("Int")
    ], fieldNames = []), derivingTraits = [])

let declaredTranslate =
    ExternalFunction(
        "translate",
        [
            ParsedNamed("NativeCount"),
            ParsedNamed("UserId")
        ],
        ParsedNamed("UserId"),
        None,
        unspecified(2),
        noCapabilities
    )

let declaredRepresentations =
    [
        TopLevelTypeAlias(declaredAlias),
        TopLevelZeroCostType(declaredUserId),
        TopLevelExternal(declaredTranslate)
    ]

let assertTranslateParameters parameters =
    match parameters with
        | first :: second :: [] ->
            first
            |> assertParameterAbi(ExternalAbiUInt(32))
            |> (given (_) -> assertParameterAbi(ExternalAbiInt)(second))
        | _ -> test.fail("translate should preserve two parameters")

let assertTranslateSource function =
    match function with
        | ExternalFunctionAbi { sourceTyping = sourceTyping } ->
            match sourceTyping with
                | ExternalFunctionTyping { sourceResult = SemNamed(_, "UserId", []) } -> Unit
                | _ -> test.fail("translate should retain its zero-cost source result")

let assertTranslateFunction function =
    match function with
        | ExternalFunctionAbi { parameters = parameters, returnType = ExternalAbiInt } ->
            function
            |> assertTranslateSource
            |> (given (_) -> assertTranslateParameters(parameters))
        | _ -> test.fail("declared return representation should erase to its ABI")

let expectDeclaredRepresentations unit =
    declaredRepresentations
    |> expectExternalAbi
    |> (given (metadata) ->
        match metadata with
            | ExternalProgramAbi { functions = functions } ->
                functions
                |> findFunction("translate")
                |> assertTranslateFunction)

let invalidResource = ExternalOpaqueType("Resource")(Some("closeResource"))

let invalidClose =
    ExternalFunction(
        "closeResource",
        [
            ParsedNamed("Resource")
        ],
        ParsedNamed("void"),
        None,
        [
            ExternalOwnershipBorrow
        ],
        None
    )

let expectInvalidResourceDestructors unit =
    expectExternalAbiError(
        InvalidExternalResourceDestructor("Resource")("closeResource")
    )([
        TopLevelExternal(invalidResource),
        TopLevelExternal(invalidClose)
    ])

let sharedFirst = ExternalOpaqueType("First")(Some("close"))

let sharedSecond = ExternalOpaqueType("Second")(Some("close"))

let sharedClose =
    ExternalFunction(
        "close",
        [
            ParsedNamed("First")
        ],
        ParsedNamed("void"),
        None,
        [
            ExternalOwnershipConsume
        ],
        None
    )

let expectSharedResourceDestructor unit =
    expectExternalAbiError(
        SharedExternalResourceDestructor("close")
    )([
        TopLevelExternal(sharedFirst),
        TopLevelExternal(sharedSecond),
        TopLevelExternal(sharedClose)
    ])

let invalidDispose =
    ExternalFunction(
        "disposeText",
        [
            ParsedNamed("Int")
        ],
        ParsedNamed("void"),
        None,
        [
            ExternalOwnershipUnspecified
        ],
        noCapabilities
    )

let invalidNativeText =
    ExternalFunction(
        "nativeText",
        [],
        ParsedNativeString(false)(FfiStringOwned)(Some("disposeText")),
        None,
        [],
        noCapabilities
    )

let expectInvalidNativeStringDestructor unit =
    expectExternalAbiError(
        InvalidExternalNativeStringDestructor("disposeText")
    )([
        TopLevelExternal(invalidDispose),
        TopLevelExternal(invalidNativeText)
    ])

let runExternalAbiTests unit =
    unit
    |> expectCompleteExternalMetadata
    |> expectDeclaredRepresentations
    |> expectInvalidResourceDestructors
    |> expectSharedResourceDestructor
    |> expectInvalidNativeStringDestructor
    |> (given (_) -> Ashes.IO.print("all self-hosted external ABI tests passed"))
