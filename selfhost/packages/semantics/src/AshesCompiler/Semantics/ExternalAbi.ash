// Validates external ABI contracts and publishes metadata for later compiler phases.
//
// Invariants:
// - Source types and native representations remain separate, including Float versus f32.
// - Resource and owned-string destructors are resolved before metadata can escape validation.
// - Parameters retain declaration order, source call shape, and boundary ownership.
// - Symbol, library, authority, and tooling views are derived from one canonical function record.

import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeResolution
import AshesCompiler.Semantics.ExternalTyping
import Ashes.Collection.List.reverse
import Ashes.Collection.List.sortBy
import Ashes.Text.compare as compareText
export (
    type ExternalAbiType(..),
    type ExternalNativeStringOwnership(..),
    type ExternalSymbolReference(..),
    type ExternalParameterAbi(..),
    type ExternalFunctionAbi(..),
    type ExternalResourceAbi(..),
    type ExternalLibraryImport(..),
    type ExternalAuthorityMetadata(..),
    type ExternalProgramAbi(..),
    type ExternalAbiError(..),
    type ExternalAbiValidation(..),
    value validateExternalProgramAbi,
)

type ExternalNativeStringOwnership =
    | ExternalNativeStringBorrowed
    | ExternalNativeStringOwned
    deriving {Eq, Show}

type ExternalSymbolReference =
    | symbolName: Str
    | libraryName: Maybe(Str)
    deriving {Eq, Show}

type ExternalAbiType =
    | ExternalAbiInt
    | ExternalAbiUInt(Int)
    | ExternalAbiFloat64
    | ExternalAbiFloat32
    | ExternalAbiBool
    | ExternalAbiString
    | ExternalAbiOpaque(Str)
    | ExternalAbiPointer(ExternalAbiType)
    | ExternalAbiBuffer(ExternalAbiType)
    | ExternalAbiOut(ExternalAbiType)
    | ExternalAbiNativeString(Bool, ExternalNativeStringOwnership, Maybe(ExternalSymbolReference))
    | ExternalAbiVoid
    deriving {Eq, Show}

type ExternalParameterAbi =
    | parameterIndex: Int
    | abiType: ExternalAbiType
    | source: ExternalParameterTyping
    deriving {Eq, Show}

type ExternalFunctionAbi =
    | name: Str
    | symbol: ExternalSymbolReference
    | parameters: List(ExternalParameterAbi)
    | returnType: ExternalAbiType
    | sourceTyping: ExternalFunctionTyping
    | destructorForResource: Maybe(Str)
    | runtimeCapabilities: List(Str)
    | directOnly: Bool
    deriving {Eq, Show}

type ExternalResourceAbi =
    | name: Str
    | destructor: ExternalFunctionAbi
    deriving {Eq, Show}

type ExternalLibraryImport =
    | symbolName: Str
    | libraryName: Str
    deriving {Eq, Show}

type ExternalAuthorityMetadata =
    | functionName: Str
    | runtimeCapabilities: List(Str)
    deriving {Eq, Show}

type ExternalProgramAbi =
    | opaqueTypes: List(Str)
    | resources: List(ExternalResourceAbi)
    | functions: List(ExternalFunctionAbi)
    | libraryImports: List(ExternalLibraryImport)
    | externalAuthority: List(ExternalAuthorityMetadata)
    deriving {Eq, Show}

type ExternalAbiError =
    | ExternalAbiSourceTypingError(ExternalTypingError)
    | UnsupportedExternalAbiRepresentation(Str)
    | InvalidExternalResourceDestructor(Str, Str)
    | SharedExternalResourceDestructor(Str)
    | InvalidExternalNativeStringDestructor(Str)
    deriving {Eq, Show}

type ExternalAbiValidation =
    | metadata: Maybe(ExternalProgramAbi)
    | error: Maybe(ExternalAbiError)
    deriving {Eq, Show}

type ExternalAbiTypeResolution =
    | abiType: Maybe(ExternalAbiType)
    | error: Maybe(ExternalAbiError)

type ExternalParameterAbiCollection =
    | parameters: List(ExternalParameterAbi)
    | error: Maybe(ExternalAbiError)

type ExternalFunctionAbiCollection =
    | functions: List(ExternalFunctionAbi)
    | error: Maybe(ExternalAbiError)

type ExternalResourceAbiCollection =
    | functions: List(ExternalFunctionAbi)
    | resources: List(ExternalResourceAbi)
    | error: Maybe(ExternalAbiError)

let externalAbiTypeSuccess abiType = ExternalAbiTypeResolution(abiType = Some(abiType), error = None)

let externalAbiTypeFailure error = ExternalAbiTypeResolution(abiType = None, error = Some(error))

let unsupportedExternalAbiType semanticType =
    externalAbiTypeFailure(semanticType
    |> formatSemanticType
    |> UnsupportedExternalAbiRepresentation)

let mapExternalAbiType constructor resolution =
    match resolution with
        | ExternalAbiTypeResolution { abiType = Some(abiType), error = None } ->
            abiType
            |> constructor
            |> externalAbiTypeSuccess
        | failure -> failure

let recursive externalAbiTypeForSemantic semanticType context =
    match semanticRuntimeRepresentation(semanticType)(context) with
        | SemInt -> externalAbiTypeSuccess(ExternalAbiInt)
        | SemUInt(bits) -> externalAbiTypeSuccess(ExternalAbiUInt(bits))
        | SemFloat -> externalAbiTypeSuccess(ExternalAbiFloat64)
        | SemBool -> externalAbiTypeSuccess(ExternalAbiBool)
        | SemString -> externalAbiTypeSuccess(ExternalAbiString)
        | SemOpaque(name) -> externalAbiTypeSuccess(ExternalAbiOpaque(name))
        | SemPointer(pointee) ->
            context
            |> externalAbiTypeForSemantic(pointee)
            |> mapExternalAbiType(ExternalAbiPointer)
        | unsupported -> unsupportedExternalAbiType(unsupported)

let resolvedSemanticTypeToAbi name context resolution =
    match resolution with
        | TypeResolutionResult { semanticType = resolved, error = None } ->
            externalAbiTypeForSemantic(
                resolved
            )(
                context
            )
        | _ -> externalAbiTypeFailure(UnsupportedExternalAbiRepresentation(name))

let externalNamedAbiType name context =
    match name with
        | "void" -> externalAbiTypeSuccess(ExternalAbiVoid)
        | "f32" -> externalAbiTypeSuccess(ExternalAbiFloat32)
        | _ ->
            context
            |> resolveSemanticTypeApplication(name)([])
            |> resolvedSemanticTypeToAbi(name)(context)

let recursive allOwnershipUnspecified ownerships =
    match ownerships with
        | [] -> true
        | ExternalOwnershipUnspecified :: tail -> allOwnershipUnspecified(tail)
        | _ -> false

let validNativeStringDestructor declaration =
    match declaration with
        | ExternalFunction(_name, parameters, returnType, _symbol, ownerships, _capabilities) ->
            match (parameters, returnType) with
                | (ParsedPointer(ParsedNamed("u8")) :: [], ParsedNamed("void")) -> allOwnershipUnspecified(ownerships)
                | _ -> false
        | _ -> false

let recursive findExternalFunctionDeclaration name declarations =
    match declarations with
        | [] -> None
        | declaration :: tail ->
            match declaration with
                | ExternalFunction(candidateName, _parameters, _returnType, _symbol, _ownerships, _capabilities) ->
                    if name == candidateName
                    then Some(declaration)
                    else findExternalFunctionDeclaration(name)(tail)
                | _ -> findExternalFunctionDeclaration(name)(tail)

let nativeStringDestructorSuccess functionName symbol =
    match splitExternalSymbol(functionName)(symbol) with
        | (symbolName, libraryName) ->
            Some(ExternalSymbolReference(symbolName = symbolName, libraryName = libraryName))
            |> ExternalAbiNativeString(false)(ExternalNativeStringOwned)
            |> externalAbiTypeSuccess

let nativeStringDestructorFromValidDeclaration name declaration =
    match declaration with
        | ExternalFunction(functionName, _, _, symbol, _, _) -> nativeStringDestructorSuccess(functionName)(symbol)
        | _ -> externalAbiTypeFailure(InvalidExternalNativeStringDestructor(name))

let nativeStringDestructorFromDeclaration name declaration =
    if validNativeStringDestructor(declaration)
    then nativeStringDestructorFromValidDeclaration(name)(declaration)
    else externalAbiTypeFailure(InvalidExternalNativeStringDestructor(name))

let nativeStringDestructorByName name declarations =
    match findExternalFunctionDeclaration(name)(declarations) with
        | Some(declaration) -> nativeStringDestructorFromDeclaration(name)(declaration)
        | None -> externalAbiTypeFailure(InvalidExternalNativeStringDestructor(name))

let nativeStringDestructorReference ownership destructorName declarations =
    match ownership with
        | FfiStringBorrowed -> ExternalAbiTypeResolution(abiType = None, error = None)
        | FfiStringOwned ->
            match destructorName with
                | None -> externalAbiTypeFailure(InvalidExternalNativeStringDestructor(""))
                | Some(name) -> nativeStringDestructorByName(name)(declarations)

let nativeStringWithNullability nullable resolution =
    match resolution with
        | ExternalAbiTypeResolution { abiType = Some(abiType), error = None } ->
            match abiType with
                | ExternalAbiNativeString(_ignoredNullable, ownership, destructor) ->
                    destructor
                    |> ExternalAbiNativeString(nullable)(ownership)
                    |> externalAbiTypeSuccess
                | _ -> resolution
        | failure -> failure

let nativeStringAbi nullable ownership destructorName declarations =
    match ownership with
        | FfiStringBorrowed ->
            None
            |> ExternalAbiNativeString(
                nullable,
                ExternalNativeStringBorrowed
            )
            |> externalAbiTypeSuccess
        | FfiStringOwned ->
            declarations
            |> nativeStringDestructorReference(ownership)(destructorName)
            |> nativeStringWithNullability(nullable)

let recursive externalAbiTypeForParsed parsedType context declarations =
    match parsedType with
        | ParsedNamed(name) -> externalNamedAbiType(name)(context)
        | ParsedPointer(pointee) ->
            declarations
            |> externalAbiTypeForParsed(pointee)(context)
            |> mapExternalAbiType(ExternalAbiPointer)
        | ParsedBuffer(element) ->
            declarations
            |> externalAbiTypeForParsed(element)(context)
            |> mapExternalAbiType(ExternalAbiBuffer)
        | ParsedOut(element) ->
            declarations
            |> externalAbiTypeForParsed(element)(context)
            |> mapExternalAbiType(ExternalAbiOut)
        | ParsedNativeString(nullable, owner, disposer) -> nativeStringAbi(nullable)(owner)(disposer)(declarations)

let recursive collectExternalParameterAbi typedParameters parameterIndex context declarations reversed =
    match typedParameters with
        | [] -> ExternalParameterAbiCollection(parameters = reverse(reversed), error = None)
        | (ExternalParameterTyping { syntax = syntax } as source) :: tail ->
            match externalAbiTypeForParsed(syntax)(context)(declarations) with
                | ExternalAbiTypeResolution { abiType = Some(abiType), error = None } ->
                    collectExternalParameterAbi(
                        tail,
                        parameterIndex + 1,
                        context,
                        declarations
                    )(ExternalParameterAbi(
                        parameterIndex = parameterIndex,
                        abiType = abiType,
                        source = source
                    ) :: reversed)
                | ExternalAbiTypeResolution { error = Some(error) } ->
                    ExternalParameterAbiCollection(
                        parameters = reverse(reversed),
                        error = Some(error)
                    )

let externalFunctionDirectOnly typing =
    match typing with
        | ExternalFunctionTyping { firstClassType = None } -> true
        | ExternalFunctionTyping { firstClassType = Some(_type) } -> false

let externalFunctionAbiFailure error =
    ExternalFunctionAbiCollection(
        functions = [],
        error = Some(error)
    )

let singleExternalFunctionAbi function =
    ExternalFunctionAbiCollection(
        functions = [
            function
        ],
        error = None
    )

let finishFunctionAbi (typing: ExternalFunctionTyping) parameters returnType =
    singleExternalFunctionAbi(ExternalFunctionAbi(
        name = typing.name,
        symbol = ExternalSymbolReference(
            symbolName = typing.symbolName,
            libraryName = typing.libraryName
        ),
        parameters = parameters,
        returnType = returnType,
        sourceTyping = typing,
        destructorForResource = None,
        runtimeCapabilities = typing.runtimeCapabilities,
        directOnly = externalFunctionDirectOnly(typing)
    ))

let finishExternalFunctionReturn typing parameters resolution =
    match resolution with
        | ExternalAbiTypeResolution { abiType = Some(returnType) } -> finishFunctionAbi(typing)(parameters)(returnType)
        | ExternalAbiTypeResolution { error = Some(error) } -> externalFunctionAbiFailure(error)

let finishExternalFunctionParameters typing context declarations collection =
    match collection with
        | ExternalParameterAbiCollection { parameters = parameters, error = None } ->
            match typing with
                | ExternalFunctionTyping { returnSyntax = returnSyntax } ->
                    declarations
                    |> externalAbiTypeForParsed(returnSyntax)(context)
                    |> finishExternalFunctionReturn(typing)(parameters)
        | ExternalParameterAbiCollection { error = Some(error) } -> externalFunctionAbiFailure(error)

let externalSourceTypingFailure error = externalFunctionAbiFailure(ExternalAbiSourceTypingError(error))

let externalTypingToFunctionAbi declarations context result =
    match result with
        | ExternalFunctionTypingResult { typing = Some(typing), error = None } ->
            match typing with
                | ExternalFunctionTyping { parameters = parameters } ->
                    []
                    |> collectExternalParameterAbi(parameters)(0)(context)(declarations)
                    |> finishExternalFunctionParameters(typing)(context)(declarations)
        | ExternalFunctionTypingResult { error = Some(error) } -> externalSourceTypingFailure(error)

let buildExternalFunctionAbi declaration declarations context =
    context
    |> typeExternalFunction(declaration)(declarations)
    |> externalTypingToFunctionAbi(declarations)(context)

let recursive appendExternalFunctions left right =
    match left with
        | [] -> right
        | head :: tail -> head :: appendExternalFunctions(tail)(right)

let combineExternalFunctionAbi headFunctions tailCollection =
    match tailCollection with
        | ExternalFunctionAbiCollection { functions = tailFunctions, error = None } ->
            tailFunctions
            |> appendExternalFunctions(headFunctions)
            |> (given (functions) -> ExternalFunctionAbiCollection(functions = functions, error = None))
        | failure -> failure

let recursive collectExternalFunctionAbi declarations allDeclarations context =
    match declarations with
        | [] -> ExternalFunctionAbiCollection(functions = [], error = None)
        | (ExternalFunction(_, _, _, _, _, _) as declaration) :: tail ->
            match buildExternalFunctionAbi(declaration)(allDeclarations)(context) with
                | ExternalFunctionAbiCollection { functions = headFunctions, error = None } ->
                    context
                    |> collectExternalFunctionAbi(tail)(allDeclarations)
                    |> (given (tailCollection) -> combineExternalFunctionAbi(headFunctions)(tailCollection))
                | failure -> failure
        | _head :: tail -> collectExternalFunctionAbi(tail)(allDeclarations)(context)

let recursive destructorReferenceCount destructorName declarations =
    match declarations with
        | [] -> 0
        | ExternalOpaqueType(_name, Some(candidate)) :: tail ->
            (if destructorName == candidate
            then 1
            else 0) + destructorReferenceCount(
                destructorName
            )(tail)
        | _head :: tail -> destructorReferenceCount(destructorName)(tail)

let recursive findExternalFunctionAbi name functions =
    match functions with
        | [] -> None
        | (ExternalFunctionAbi { name = candidate } as function) :: tail ->
            if name == candidate
            then Some(function)
            else findExternalFunctionAbi(name)(tail)

let externalParameterTargetsResource resourceName parameter =
    match parameter with
        | ExternalParameterAbi { abiType = ExternalAbiOpaque(parameterType) } -> resourceName == parameterType
        | _ -> false

let externalParameterConsumesResource parameter =
    match parameter with
        | ExternalParameterAbi { source = ExternalParameterTyping { ownership = ExternalOwnershipConsume } } -> true
        | _ -> false

let validResourceDestructor resourceName function =
    match function with
        | ExternalFunctionAbi { parameters = parameter :: [], returnType = ExternalAbiVoid } ->
            if externalParameterTargetsResource(resourceName)(parameter)
            then externalParameterConsumesResource(parameter)
            else false
        | _ -> false

let markFunctionAsResourceDestructor resourceName destructorName function =
    match function with
        | ExternalFunctionAbi { name = name } ->
            if name == destructorName
            then function with destructorForResource = Some(resourceName)
            else function

let recursive markResourceDestructor resourceName destructorName functions =
    match functions with
        | [] -> []
        | function :: tail ->
            function
            |> markFunctionAsResourceDestructor(resourceName)(destructorName)
            |> (given (updated) -> updated :: markResourceDestructor(resourceName)(destructorName)(tail))

let externalResourceFailure functions error =
    ExternalResourceAbiCollection(
        functions = functions,
        resources = [],
        error = Some(error)
    )

let invalidExternalResource functions resourceName destructorName =
    destructorName
    |> InvalidExternalResourceDestructor(resourceName)
    |> externalResourceFailure(functions)

let validateExternalResource declarations functions resourceName destructorName =
    if destructorReferenceCount(destructorName)(declarations) > 1
    then externalResourceFailure(functions)(SharedExternalResourceDestructor(destructorName))
    else
        match findExternalFunctionAbi(destructorName)(functions) with
            | Some(destructor) ->
                if validResourceDestructor(resourceName)(destructor)
                then
                    ExternalResourceAbiCollection(
                        functions = markResourceDestructor(resourceName)(destructorName)(functions),
                        resources = [
                            ExternalResourceAbi(
                                name = resourceName,
                                destructor = destructor with destructorForResource = Some(resourceName)
                            )
                        ],
                        error = None
                    )
                else invalidExternalResource(functions)(resourceName)(destructorName)
            | None -> invalidExternalResource(functions)(resourceName)(destructorName)

let recursive validateExternalResources declarations functions reversed =
    match declarations with
        | [] -> ExternalResourceAbiCollection(functions = functions, resources = reverse(reversed), error = None)
        | ExternalOpaqueType(resourceName, Some(destructorName)) :: tail ->
            match validateExternalResource(declarations)(functions)(resourceName)(destructorName) with
                | ExternalResourceAbiCollection { resources = resource :: [], error = None } as success ->
                    validateExternalResources(
                        tail,
                        success.functions,
                        resource :: reversed
                    )
                | failure -> failure
        | _head :: tail -> validateExternalResources(tail)(functions)(reversed)

let recursive collectExternalOpaqueTypes declarations =
    match declarations with
        | [] -> []
        | ExternalOpaqueType(name, _destructor) :: tail -> name :: collectExternalOpaqueTypes(tail)
        | _head :: tail -> collectExternalOpaqueTypes(tail)

let externalLibraryImportForFunction function =
    match function with
        | ExternalFunctionAbi { symbol = symbol } ->
            match symbol with
                | ExternalSymbolReference { symbolName = symbolName, libraryName = Some(libraryName) } ->
                    Some(ExternalLibraryImport(
                        symbolName = symbolName,
                        libraryName = libraryName
                    ))
                | _ -> None

let recursive collectExternalLibraryImports functions =
    match functions with
        | [] -> []
        | function :: tail ->
            match externalLibraryImportForFunction(function) with
                | Some(libraryImport) -> libraryImport :: collectExternalLibraryImports(tail)
                | None -> collectExternalLibraryImports(tail)

let externalAuthorityName authority =
    match authority with
        | ExternalAuthorityMetadata { functionName = functionName } -> functionName

let authorityBefore left right =
    compareText(
        externalAuthorityName(left)
    )(
        externalAuthorityName(right)
    ) <= 0

let externalAuthorityForFunction function =
    match function with
        | ExternalFunctionAbi { name = name, runtimeCapabilities = runtimeCapabilities } ->
            ExternalAuthorityMetadata(
                functionName = name,
                runtimeCapabilities = runtimeCapabilities
            )

let recursive collectExternalAuthority functions =
    match functions with
        | [] -> []
        | function :: tail ->
            function
            |> externalAuthorityForFunction
            |> (given (authority) -> authority :: collectExternalAuthority(tail))

let completeExternalProgramAbi declarations functions resources =
    ExternalAbiValidation(
        metadata = Some(ExternalProgramAbi(
            opaqueTypes = collectExternalOpaqueTypes(declarations),
            resources = resources,
            functions = functions,
            libraryImports = collectExternalLibraryImports(functions),
            externalAuthority = functions
            |> collectExternalAuthority
            |> sortBy(authorityBefore)
        )),
        error = None
    )

let finishExternalProgramAbi declarations resourceValidation =
    match resourceValidation with
        | ExternalResourceAbiCollection { functions = functions, resources = resources, error = None } ->
            completeExternalProgramAbi(
                declarations,
                functions,
                resources
            )
        | ExternalResourceAbiCollection { error = Some(error) } ->
            ExternalAbiValidation(
                metadata = None,
                error = Some(error)
            )

let validateExternalProgramAbi declarations context =
    match collectExternalFunctionAbi(declarations)(declarations)(context) with
        | ExternalFunctionAbiCollection { functions = functions, error = None } ->
            []
            |> validateExternalResources(declarations)(functions)
            |> finishExternalProgramAbi(declarations)
        | ExternalFunctionAbiCollection { error = Some(error) } ->
            ExternalAbiValidation(
                metadata = None,
                error = Some(error)
            )
