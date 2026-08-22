// Types external declarations at their Ashes call boundary.
//
// Invariants:
// - Opaque external types retain nominal names while resource ownership stays declaration metadata.
// - Compiler-owned out parameters consume no Ashes argument and append their values to the result.
// - Runtime capabilities decorate only the innermost call arrow.
// - Call-scoped, native-string, resource, and nullary declarations cannot escape as function values.

import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.TypeResolution
import Ashes.Collection.List.reverse
import Ashes.Collection.List.sortBy
import Ashes.Text.compare as compareText
export (
    type ExternalTypingError(..),
    type ExternalParameterTyping(..),
    type ExternalFunctionTyping(..),
    type ExternalFunctionTypingResult(..),
    value splitExternalSymbol,
    value typeExternalFunction,
)

type ExternalTypingError =
    | UnsupportedExternalType(Str)
    | VoidExternalParameter
    | InvalidExternalTypePosition(Str)
    | InvalidExternalBufferElement(Str)
    | InvalidExternalOutElement(Str)
    | InvalidExternalOwnershipMarker(Int)
    | InvalidExternalCapabilityRow(Str)
    | MissingExternalSupportType(Str)
    deriving {Eq, Show}

type ExternalParameterTyping =
    | syntax: ParsedType
    | sourceArgument: Maybe(SemanticType)
    | sourceOutput: Maybe(SemanticType)
    | ownership: ExternalParameterOwnership
    | directOnly: Bool
    deriving {Eq, Show}

type ExternalFunctionTyping =
    | name: Str
    | symbolName: Str
    | libraryName: Maybe(Str)
    | parameters: List(ExternalParameterTyping)
    | returnSyntax: ParsedType
    | sourceResult: SemanticType
    | directType: SemanticType
    | firstClassType: Maybe(SemanticType)
    | runtimeCapabilities: List(Str)
    deriving {Eq, Show}

type ExternalFunctionTypingResult =
    | typing: Maybe(ExternalFunctionTyping)
    | error: Maybe(ExternalTypingError)
    deriving {Eq, Show}

type ExternalValueTypeResult =
    | semanticType: SemanticType
    | error: Maybe(ExternalTypingError)

type ExternalReturnTyping =
    | semanticType: SemanticType
    | contributesResult: Bool
    | directOnly: Bool
    | error: Maybe(ExternalTypingError)

type ExternalParameterCollection =
    | parameters: List(ExternalParameterTyping)
    | arguments: List(SemanticType)
    | outputs: List(SemanticType)
    | directOnly: Bool
    | error: Maybe(ExternalTypingError)

type ExternalCapabilityTyping =
    | capabilities: List(Str)
    | error: Maybe(ExternalTypingError)

let externalTypingFailure error = ExternalFunctionTypingResult(typing = None, error = Some(error))

let emptyExternalParameters directOnly error = ExternalParameterCollection(parameters = [], arguments = [], outputs = [], directOnly = directOnly, error = error)

let singletonExternalParameter syntax argument output ownership directOnly =
    ExternalParameterCollection(parameters = [
        ExternalParameterTyping(syntax = syntax, sourceArgument = argument, sourceOutput = output, ownership = ownership, directOnly = directOnly)
    ], arguments = match argument with
        | Some(argumentType) -> [argumentType]
        | None -> [], outputs = match output with
        | Some(outputType) -> [outputType]
        | None -> [], directOnly = directOnly, error = None)

let externalReturnSuccess semanticType contributesResult directOnly = ExternalReturnTyping(semanticType = semanticType, contributesResult = contributesResult, directOnly = directOnly, error = None)

let externalReturnFailure directOnly error = ExternalReturnTyping(semanticType = SemNever, contributesResult = false, directOnly = directOnly, error = Some(error))

let recursive externalTypeDestructor name declarations =
    match declarations with
        | [] -> None
        | ExternalOpaqueType(candidateName, destructor) :: tail ->
            if name == candidateName
            then destructor
            else externalTypeDestructor(name)(tail)
        | _head :: tail -> externalTypeDestructor(name)(tail)

let isExternalResourceType semanticType declarations =
    match semanticType with
        | SemOpaque(name) ->
            match externalTypeDestructor(name)(declarations) with
                | Some(_) -> true
                | None -> false
        | _ -> false

let externalFunctionIsDestructor name declarations =
    (let recursive find remaining =
        match remaining with
            | [] -> false
            | ExternalOpaqueType(_typeName, Some(destructor)) :: tail ->
                if name == destructor
                then true
                else find(tail)
            | _head :: tail -> find(tail)
    in find(declarations))

let externalValueTypeSuccess semanticType = ExternalValueTypeResult(semanticType = semanticType, error = None)

let externalValueTypeFailure name =
    ExternalValueTypeResult(semanticType = SemNever, error = Some(
        UnsupportedExternalType(name)
    ))

let externalRuntimeRepresentationSupported semanticType context =
    match semanticRuntimeRepresentation(semanticType)(context) with
        | SemInt -> true
        | SemUInt(8) -> true
        | SemUInt(16) -> true
        | SemUInt(32) -> true
        | SemUInt(64) -> true
        | SemFloat -> true
        | SemBool -> true
        | SemString -> true
        | SemOpaque(_) -> true
        | SemPointer(_) -> true
        | _ -> false

let resolvedExternalValueType name context =
    match name with
        | "f32" -> externalValueTypeSuccess(SemFloat)
        | "void" ->
            ExternalValueTypeResult(semanticType = SemNever, error = Some(
                VoidExternalParameter
            ))
        | _ ->
            match resolveSemanticTypeApplication(name)([])(context) with
                | TypeResolutionResult { semanticType = semanticType, error = None } ->
                    if externalRuntimeRepresentationSupported(semanticType)(context)
                    then externalValueTypeSuccess(semanticType)
                    else externalValueTypeFailure(name)
                | TypeResolutionResult { error = Some(_error) } -> externalValueTypeFailure(name)

let recursive resolveExternalValueType parsedType context =
    match parsedType with
        | ParsedNamed(name) -> resolvedExternalValueType(name)(context)
        | ParsedPointer(pointee) ->
            match resolveExternalValueType(pointee)(context) with
                | ExternalValueTypeResult { semanticType = semanticType, error = None } ->
                    externalValueTypeSuccess(
                        SemPointer(semanticType)
                    )
                | failure -> failure
        | ParsedBuffer(_) ->
            ExternalValueTypeResult(semanticType = SemNever, error = Some(
                InvalidExternalTypePosition("FfiBuffer")
            ))
        | ParsedOut(_) ->
            ExternalValueTypeResult(semanticType = SemNever, error = Some(
                InvalidExternalTypePosition("out")
            ))
        | ParsedNativeString(_, _, _) ->
            ExternalValueTypeResult(semanticType = SemNever, error = Some(
                InvalidExternalTypePosition("FfiStr")
            ))

let resolveSupportType name arguments context =
    match resolveSemanticTypeApplication(name)(arguments)(context) with
        | TypeResolutionResult { semanticType = semanticType, error = None } -> externalValueTypeSuccess(semanticType)
        | TypeResolutionResult { error = Some(_error) } ->
            ExternalValueTypeResult(semanticType = SemNever, error = Some(
                MissingExternalSupportType(name)
            ))

let nativeStringSourceType nullable context =
    (let success =
        if nullable
        then resolveSupportType("Maybe")([SemString])(context)
        else externalValueTypeSuccess(SemString)
    in
        match success with
            | ExternalValueTypeResult { semanticType = successType, error = None } ->
                resolveSupportType("Result")(
                    [SemString, successType]
                )(context)
            | failure -> failure)

let outNativeStringSourceType context =
    match resolveSupportType("Maybe")([SemString])(context) with
        | ExternalValueTypeResult { semanticType = maybeString, error = None } ->
            resolveSupportType("Result")(
                [SemString, maybeString]
            )(context)
        | failure -> failure

let isDirectResourceArgument argument declarations =
    match argument with
        | Some(semanticType) -> isExternalResourceType(semanticType)(declarations)
        | None -> false

let ownershipIsWritten ownership =
    match ownership with
        | ExternalOwnershipUnspecified -> false
        | ExternalOwnershipBorrow -> true
        | ExternalOwnershipConsume -> true

let validateExternalOwnership index argument ownership declarations =
    if isDirectResourceArgument(argument)(declarations) == ownershipIsWritten(ownership)
    then None
    else Some(InvalidExternalOwnershipMarker(index))

let typeExternalBuffer element ownership index context declarations =
    match resolveExternalValueType(element)(context) with
        | ExternalValueTypeResult { semanticType = SemOpaque(name), error = None } ->
            match (isExternalResourceType(SemOpaque(name))(declarations), ownershipIsWritten(ownership)) with
                | (true, _) -> externalTypingFailure(InvalidExternalBufferElement(name))
                | (_, true) -> externalTypingFailure(InvalidExternalOwnershipMarker(index))
                | _ -> ExternalFunctionTypingResult(typing = None, error = None)
        | ExternalValueTypeResult { semanticType = semanticType, error = None } ->
            externalTypingFailure(semanticType
            |> formatSemanticType
            |> InvalidExternalBufferElement)
        | ExternalValueTypeResult { error = Some(error) } -> externalTypingFailure(error)

let recursive typeExternalParameter parsedType ownership index context declarations =
    match parsedType with
        | ParsedBuffer(element) ->
            match typeExternalBuffer(element)(ownership)(index)(context)(declarations) with
                | ExternalFunctionTypingResult { error = Some(error) } -> emptyExternalParameters(true)(Some(error))
                | _ ->
                    match resolveExternalValueType(element)(context) with
                        | ExternalValueTypeResult { semanticType = elementType, error = None } ->
                            singletonExternalParameter(
                                parsedType,
                                Some(SemList(elementType)),
                                None,
                                ownership,
                                true
                            )
                        | ExternalValueTypeResult { error = Some(error) } -> emptyExternalParameters(true)(Some(error))
        | ParsedOut(ParsedNativeString(nullable, _stringOwnership, _destructor)) ->
            if nullable
            then
                emptyExternalParameters(true)(Some(
                    InvalidExternalTypePosition("nullable out FfiStr")
                ))
            else
                match validateExternalOwnership(index)(None)(ownership)(declarations) with
                    | Some(error) -> emptyExternalParameters(true)(Some(error))
                    | None ->
                        match outNativeStringSourceType(context) with
                            | ExternalValueTypeResult { semanticType = outputType, error = None } ->
                                singletonExternalParameter(
                                    parsedType,
                                    None,
                                    Some(outputType),
                                    ownership,
                                    true
                                )
                            | ExternalValueTypeResult { error = Some(error) } ->
                                emptyExternalParameters(
                                    true,
                                    Some(error)
                                )
        | ParsedOut(element) ->
            match resolveExternalValueType(element)(context) with
                | ExternalValueTypeResult { semanticType = elementType, error = None } ->
                    match elementType with
                        | SemOpaque(_) ->
                            typeExternalOut(
                                parsedType,
                                elementType,
                                ownership,
                                index,
                                context,
                                declarations
                            )
                        | SemPointer(_) ->
                            typeExternalOut(
                                parsedType,
                                elementType,
                                ownership,
                                index,
                                context,
                                declarations
                            )
                        | _ ->
                            emptyExternalParameters(true)(Some(elementType
                            |> formatSemanticType
                            |> InvalidExternalOutElement))
                | ExternalValueTypeResult { error = Some(error) } -> emptyExternalParameters(true)(Some(error))
        | ParsedNativeString(_, _, _) ->
            emptyExternalParameters(true)(Some(
                InvalidExternalTypePosition("FfiStr parameter")
            ))
        | _ ->
            match resolveExternalValueType(parsedType)(context) with
                | ExternalValueTypeResult { semanticType = argumentType, error = None } ->
                    match validateExternalOwnership(index)(Some(argumentType))(ownership)(declarations) with
                        | Some(error) -> emptyExternalParameters(false)(Some(error))
                        | None ->
                            let directOnly = ownershipIsWritten(ownership)
                            in
                                singletonExternalParameter(
                                    parsedType,
                                    Some(argumentType),
                                    None,
                                    ownership,
                                    directOnly
                                )
                | ExternalValueTypeResult { error = Some(error) } -> emptyExternalParameters(false)(Some(error))
and typeExternalOut parsedType elementType ownership index _context declarations =
    match validateExternalOwnership(index)(None)(ownership)(declarations) with
        | Some(error) -> emptyExternalParameters(true)(Some(error))
        | None ->
            match resolveSupportType("Maybe")([elementType])(_context) with
                | ExternalValueTypeResult { semanticType = outputType, error = None } ->
                    singletonExternalParameter(
                        parsedType,
                        None,
                        Some(outputType),
                        ownership,
                        true
                    )
                | ExternalValueTypeResult { error = Some(error) } -> emptyExternalParameters(true)(Some(error))

let appendSemanticTypes left right =
    (let recursive append remaining =
        match remaining with
            | [] -> right
            | head :: tail -> head :: append(tail)
    in append(left))

let appendExternalParameters left right =
    (let recursive append remaining =
        match remaining with
            | [] -> right
            | head :: tail -> head :: append(tail)
    in append(left))

let recursive collectExternalParameters parameterTypes ownerships index context declarations =
    match parameterTypes with
        | [] -> emptyExternalParameters(false)(None)
        | head :: tail ->
            let ownership =
                match ownerships with
                    | [] -> ExternalOwnershipUnspecified
                    | candidate :: _rest -> candidate
            in
                let remainingOwnerships =
                    match ownerships with
                        | [] -> []
                        | _candidate :: rest -> rest
                in
                    match typeExternalParameter(head)(ownership)(index)(context)(declarations) with
                        | ExternalParameterCollection { error = Some(error) } ->
                            emptyExternalParameters(
                                false,
                                Some(error)
                            )
                        | ExternalParameterCollection { parameters = headParameters, arguments = headArguments, outputs = headOutputs, directOnly = headDirectOnly, error = None } ->
                            match collectExternalParameters(
                                tail,
                                remainingOwnerships,
                                index + 1,
                                context,
                                declarations
                            ) with
                                | ExternalParameterCollection { parameters = tailParameters, arguments = tailArguments, outputs = tailOutputs, directOnly = tailDirectOnly, error = None } ->
                                    ExternalParameterCollection(parameters = appendExternalParameters(
                                        headParameters
                                    )(tailParameters), arguments = appendSemanticTypes(
                                        headArguments
                                    )(tailArguments), outputs = appendSemanticTypes(
                                        headOutputs
                                    )(tailOutputs), directOnly = match headDirectOnly with
                                        | true -> true
                                        | false -> tailDirectOnly, error = None)
                                | failure -> failure

let typeExternalReturn parsedType context declarations =
    match parsedType with
        | ParsedNamed("void") -> externalReturnSuccess(SemTuple([]))(false)(false)
        | ParsedNativeString(nullable, _ownership, _destructor) ->
            match nativeStringSourceType(nullable)(context) with
                | ExternalValueTypeResult { semanticType = semanticType, error = None } ->
                    externalReturnSuccess(
                        semanticType,
                        true,
                        true
                    )
                | ExternalValueTypeResult { error = Some(error) } -> externalReturnFailure(true)(error)
        | ParsedBuffer(_) -> externalReturnFailure(true)(InvalidExternalTypePosition("FfiBuffer return"))
        | ParsedOut(_) -> externalReturnFailure(true)(InvalidExternalTypePosition("out return"))
        | _ ->
            match resolveExternalValueType(parsedType)(context) with
                | ExternalValueTypeResult { semanticType = semanticType, error = None } ->
                    externalReturnSuccess(semanticType)(true)(
                        isExternalResourceType(semanticType)(declarations)
                    )
                | ExternalValueTypeResult { error = Some(error) } -> externalReturnFailure(false)(error)

let isBuiltinRuntimeCapability name =
    match name with
        | "ConsoleIO" -> true
        | "FileRead" -> true
        | "FileWrite" -> true
        | "ProcessSpawn" -> true
        | "ProcessExit" -> true
        | "TimeRead" -> true
        | "EnvironmentRead" -> true
        | "Entropy" -> true
        | "UnsafeFfi" -> true
        | "NetListen" -> true
        | "NetConnect" -> true
        | "Stop" -> true
        | _ -> false

let recursive validateRuntimeCapabilities capabilities reversed =
    match capabilities with
        | [] -> ExternalCapabilityTyping(capabilities = reverse(reversed), error = None)
        | CapabilityRefSyntax { name = name, args = [] } :: tail ->
            if isBuiltinRuntimeCapability(name)
            then validateRuntimeCapabilities(tail)(name :: reversed)
            else ExternalCapabilityTyping(capabilities = [], error = Some(InvalidExternalCapabilityRow(name)))
        | CapabilityRefSyntax { name = name, args = _arguments } :: _tail -> ExternalCapabilityTyping(capabilities = [], error = Some(InvalidExternalCapabilityRow(name)))

let recursive removeDuplicateNames names =
    match names with
        | [] -> []
        | head :: tail ->
            let recursive skip remaining =
                match remaining with
                    | candidate :: rest ->
                        if head == candidate
                        then skip(rest)
                        else remaining
                    | [] -> []
            in
                head :: removeDuplicateNames(skip(tail))

let canonicalRuntimeCapabilities capabilities =
    capabilities
    |> sortBy(given (left) ->
        given (right) -> compareText(left)(right) <= 0)
    |> removeDuplicateNames

let typeExternalCapabilities functionName capabilityRow declarations =
    match capabilityRow with
        | None ->
            if externalFunctionIsDestructor(functionName)(declarations)
            then ExternalCapabilityTyping(capabilities = [], error = None)
            else ExternalCapabilityTyping(capabilities = ["UnsafeFfi"], error = None)
        | Some(NeedsRowSyntax { capabilities = capabilities, tailVariable = None }) ->
            match validateRuntimeCapabilities(capabilities)([]) with
                | ExternalCapabilityTyping { capabilities = names, error = None } -> ExternalCapabilityTyping(capabilities = canonicalRuntimeCapabilities(names), error = None)
                | failure -> failure
        | Some(NeedsRowSyntax { tailVariable = Some(tail) }) ->
            ExternalCapabilityTyping(capabilities = [], error = Some(
                InvalidExternalCapabilityRow(tail)
            ))

let externalResultType returnTyping outputs =
    match returnTyping with
        | ExternalReturnTyping { semanticType = returnType, contributesResult = contributesResult } ->
            let components =
                if contributesResult
                then returnType :: outputs
                else outputs
            in
                match components with
                    | [] -> SemTuple([])
                    | head :: [] -> head
                    | _ -> SemTuple(components)

let recursive externalFunctionType arguments result =
    match arguments with
        | [] -> result
        | head :: tail ->
            SemFunction(head)(externalFunctionType(tail)(result))(None)

let recursive withInnermostCapabilityRow row semanticType =
    match semanticType with
        | SemFunction(argument, SemFunction(nextArgument, nextResult, nextRow), outerRow) ->
            SemFunction(
                argument,
                nextRow
                |> SemFunction(nextArgument)(nextResult)
                |> withInnermostCapabilityRow(row),
                outerRow
            )
        | SemFunction(argument, result, _existingRow) -> SemFunction(argument)(result)(Some(row))
        | _ -> semanticType

let splitExternalSymbol functionName written =
    (let fullName =
        match written with
            | None -> functionName
            | Some(value) -> value
    in
        match "@"
        |> Ashes.Text.split(fullName)
        |> reverse with
            | [] -> (fullName, None)
            | library :: reversedSymbol ->
                match reversedSymbol with
                    | [] -> (fullName, None)
                    | _ ->
                        let symbol =
                            reversedSymbol
                            |> reverse
                            |> Ashes.Text.join("@")
                        in
                            if library == ""
                            then (symbol, None)
                            else (symbol, Some(library)))

let finishExternalFunctionTyping name symbol returnSyntax parameters returnTyping capabilities =
    match (parameters, returnTyping, capabilities) with
        | (ExternalParameterCollection { parameters = typedParameters, arguments = arguments, outputs = outputs, directOnly = parameterDirectOnly, error = None }, ExternalReturnTyping { directOnly = returnDirectOnly, error = None }, ExternalCapabilityTyping { capabilities = runtimeCapabilities, error = None }) ->
            let sourceResult = externalResultType(returnTyping)(outputs)
            in
                let callArguments =
                    match arguments with
                        | [] -> [SemTuple([])]
                        | _ -> arguments
                in
                    let row =
                        ((given (names) ->
                            let recursive asCapabilities remaining =
                                match remaining with
                                    | [] -> []
                                    | head :: tail -> SemCapability(head)([]) :: asCapabilities(tail)
                            in
                                SemRow(asCapabilities(names))(None)))(runtimeCapabilities)
                    in
                        let directType =
                            sourceResult
                            |> externalFunctionType(callArguments)
                            |> withInnermostCapabilityRow(row)
                        in
                            let directOnly =
                                match (parameterDirectOnly, returnDirectOnly) with
                                    | (true, _) -> true
                                    | (_, true) -> true
                                    | _ -> arguments == []
                            in
                                let firstClassType =
                                    if directOnly
                                    then None
                                    else Some(directType)
                                in
                                    match splitExternalSymbol(name)(symbol) with
                                        | (symbolName, libraryName) ->
                                            ExternalFunctionTypingResult(typing = Some(
                                                ExternalFunctionTyping(name = name, symbolName = symbolName, libraryName = libraryName, parameters = typedParameters, returnSyntax = returnSyntax, sourceResult = sourceResult, directType = directType, firstClassType = firstClassType, runtimeCapabilities = runtimeCapabilities)
                                            ), error = None)
        | (ExternalParameterCollection { error = Some(error) }, _, _) -> externalTypingFailure(error)
        | (_, ExternalReturnTyping { error = Some(error) }, _) -> externalTypingFailure(error)
        | (_, _, ExternalCapabilityTyping { error = Some(error) }) -> externalTypingFailure(error)

let typeExternalFunction declaration allDeclarations context =
    match declaration with
        | ExternalOpaqueType(name, _destructor) -> externalTypingFailure(UnsupportedExternalType(name))
        | ExternalFunction(name, parameterTypes, returnType, symbol, ownerships, capabilityRow) ->
            finishExternalFunctionTyping(
                name,
                symbol,
                returnType,
                collectExternalParameters(
                    parameterTypes,
                    ownerships,
                    1,
                    context,
                    allDeclarations
                ),
                typeExternalReturn(returnType)(context)(allDeclarations),
                typeExternalCapabilities(name)(capabilityRow)(allDeclarations)
            )
