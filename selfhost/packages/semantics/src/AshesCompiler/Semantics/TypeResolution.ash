import AshesCompiler.Frontend.Syntax.TypeExpr
import AshesCompiler.Semantics.Types
import Ashes.Collection.List.reverse
export (
    type TypeDefinition(..),
    type TypeResolutionContext(..),
    type TypeResolutionError(..),
    type TypeResolutionResult(..),
    type TypeResolutionPreparationResult(..),
    type TypeListResolutionResult(..),
    value emptyTypeResolutionContext,
    value addTypeParameter,
    value addTypeDefinition,
    value prepareTypeResolutionContext,
    value resolveTypeExpression,
)

type TypeDefinition =
    | symbolId: Int
    | name: Str
    | arity: Int

type TypeResolutionContext =
    | parameters: List((Str, SemanticType))
    | definitions: List(TypeDefinition)

type TypeResolutionError =
    | UnknownTypeName(Str)
    | TypeNameArityMismatch(Str, Int, Int)
    deriving {Eq, Show}

type TypeResolutionResult =
    | semanticType: SemanticType
    | error: Maybe(TypeResolutionError)
    deriving {Eq, Show}

type TypeResolutionPreparationResult =
    | context: TypeResolutionContext
    | supply: TypeVariableSupply

type TypeListResolutionResult =
    | semanticTypes: List(SemanticType)
    | error: Maybe(TypeResolutionError)

let emptyTypeResolutionContext unit = TypeResolutionContext(parameters = [], definitions = [])

let addTypeParameter name semanticType context =
    match context with
        | TypeResolutionContext { parameters = parameters, definitions = definitions } -> TypeResolutionContext(parameters = (name, semanticType) :: parameters, definitions = definitions)

let addTypeDefinition symbolId name arity context =
    match context with
        | TypeResolutionContext { parameters = parameters, definitions = definitions } -> TypeResolutionContext(parameters = parameters, definitions = TypeDefinition(symbolId = symbolId, name = name, arity = arity) :: definitions)

let recursive typeListLength values =
    match values with
        | [] -> 0
        | _head :: tail -> 1 + typeListLength(tail)

let recursive findTypeParameter name parameters =
    match parameters with
        | [] -> None
        | (candidateName, semanticType) :: tail ->
            if name == candidateName
            then Some(semanticType)
            else findTypeParameter(name)(tail)

let recursive findTypeDefinition name definitions =
    match definitions with
        | [] -> None
        | definition :: tail ->
            match definition with
                | TypeDefinition { symbolId = _symbolId, name = candidateName, arity = _arity } ->
                    if name == candidateName
                    then Some(definition)
                    else findTypeDefinition(name)(tail)

let resolvePrimitive name =
    match name with
        | "Int" -> Some(SemInt)
        | "Bool" -> Some(SemBool)
        | "Str" -> Some(SemString)
        | "Rune" -> Some(SemRune)
        | "Bytes" -> Some(SemBytes)
        | "Float" -> Some(SemFloat)
        | "BigInt" -> Some(SemBigInt)
        | "Never" -> Some(SemNever)
        | "u8" -> Some(SemUInt(8))
        | "u16" -> Some(SemUInt(16))
        | "u32" -> Some(SemUInt(32))
        | "u64" -> Some(SemUInt(64))
        | _ -> None

let recursive resolveNamed name arguments context =
    match context with
        | TypeResolutionContext { parameters = parameters, definitions = definitions } ->
            match (arguments, resolvePrimitive(name)) with
                | ([], Some(primitive)) -> TypeResolutionResult(semanticType = primitive, error = None)
                | (_, Some(_primitive)) -> TypeResolutionResult(semanticType = SemNever, error = Some(TypeNameArityMismatch(name)(0)(typeListLength(arguments))))
                | _ ->
                    match (name, arguments) with
                        | ("List", element :: []) -> TypeResolutionResult(semanticType = SemList(element), error = None)
                        | ("List", _) -> TypeResolutionResult(semanticType = SemNever, error = Some(TypeNameArityMismatch(name)(1)(typeListLength(arguments))))
                        | ("Ptr", pointee :: []) -> TypeResolutionResult(semanticType = SemPointer(pointee), error = None)
                        | ("Ptr", _) -> TypeResolutionResult(semanticType = SemNever, error = Some(TypeNameArityMismatch(name)(1)(typeListLength(arguments))))
                        | (_, []) ->
                            match findTypeParameter(name)(parameters) with
                                | Some(parameter) -> TypeResolutionResult(semanticType = parameter, error = None)
                                | None -> resolveTypeDefinition(name)(arguments)(definitions)
                        | _ -> resolveTypeDefinition(name)(arguments)(definitions)
and resolveTypeDefinition name arguments definitions =
    match findTypeDefinition(name)(definitions) with
        | None -> TypeResolutionResult(semanticType = SemNever, error = Some(UnknownTypeName(name)))
        | Some(TypeDefinition { symbolId = symbolId, name = definitionName, arity = arity }) ->
            let actualArity = typeListLength(arguments)
            in
                if actualArity == arity
                then TypeResolutionResult(semanticType = SemNamed(symbolId)(definitionName)(arguments), error = None)
                else TypeResolutionResult(semanticType = SemNever, error = Some(TypeNameArityMismatch(name)(arity)(actualArity)))

let recursive resolveTypeExpressions expressions context reversed =
    match expressions with
        | [] -> TypeListResolutionResult(semanticTypes = reversed, error = None)
        | head :: tail ->
            match resolveTypeExpression(head)(context) with
                | TypeResolutionResult { semanticType = semanticType, error = None } -> resolveTypeExpressions(tail)(context)(semanticType :: reversed)
                | TypeResolutionResult { semanticType = _semanticType, error = Some(error) } -> TypeListResolutionResult(semanticTypes = [], error = Some(error))
and resolveCapabilities capabilities context reversed =
    match capabilities with
        | [] -> TypeListResolutionResult(semanticTypes = reversed, error = None)
        | (name, arguments) :: tail ->
            match resolveTypeExpressions(arguments)(context)([]) with
                | TypeListResolutionResult { semanticTypes = reversedArguments, error = None } -> resolveCapabilities(tail)(context)(SemCapability(name)(reverse(reversedArguments)) :: reversed)
                | TypeListResolutionResult { semanticTypes = _semanticTypes, error = Some(error) } -> TypeListResolutionResult(semanticTypes = [], error = Some(error))
and resolveTypeExpression typeExpression context =
    match typeExpression with
        | TypeAt(_span, inner) -> resolveTypeExpression(inner)(context)
        | TypeNamed(name) -> resolveNamed(name)([])(context)
        | TypeApplied(name, arguments) ->
            match resolveTypeExpressions(arguments)(context)([]) with
                | TypeListResolutionResult { semanticTypes = reversedArguments, error = None } -> resolveNamed(name)(reverse(reversedArguments))(context)
                | TypeListResolutionResult { semanticTypes = _semanticTypes, error = Some(error) } -> TypeResolutionResult(semanticType = SemNever, error = Some(error))
        | TypeArrow(argument, result, capabilities, tailName) ->
            match resolveTypeExpression(argument)(context) with
                | TypeResolutionResult { semanticType = argumentType, error = None } ->
                    match resolveTypeExpression(result)(context) with
                        | TypeResolutionResult { semanticType = resultType, error = None } ->
                            match resolveCapabilities(capabilities)(context)([]) with
                                | TypeListResolutionResult { semanticTypes = reversedCapabilities, error = None } ->
                                    let resolvedCapabilities = reverse(reversedCapabilities)
                                    in
                                        let resolvedTail =
                                            match tailName with
                                                | None -> TypeResolutionResult(semanticType = SemRow([])(None), error = None)
                                                | Some(name) -> resolveNamed(name)([])(context)
                                        in
                                            match resolvedTail with
                                                | TypeResolutionResult { semanticType = tailType, error = None } ->
                                                    let row =
                                                        match (resolvedCapabilities, tailName) with
                                                            | ([], None) -> None
                                                            | (_, None) -> Some(SemRow(resolvedCapabilities)(None))
                                                            | (_, Some(_)) -> Some(SemRow(resolvedCapabilities)(Some(tailType)))
                                                    in TypeResolutionResult(semanticType = SemFunction(argumentType)(resultType)(row), error = None)
                                                | TypeResolutionResult { semanticType = _tailType, error = Some(error) } -> TypeResolutionResult(semanticType = SemNever, error = Some(error))
                                | TypeListResolutionResult { semanticTypes = _semanticTypes, error = Some(error) } -> TypeResolutionResult(semanticType = SemNever, error = Some(error))
                        | TypeResolutionResult { semanticType = _resultType, error = Some(error) } -> TypeResolutionResult(semanticType = SemNever, error = Some(error))
                | TypeResolutionResult { semanticType = _argumentType, error = Some(error) } -> TypeResolutionResult(semanticType = SemNever, error = Some(error))
        | TypeTuple(elements) ->
            match resolveTypeExpressions(elements)(context)([]) with
                | TypeListResolutionResult { semanticTypes = reversedElements, error = None } -> TypeResolutionResult(semanticType = SemTuple(reverse(reversedElements)), error = None)
                | TypeListResolutionResult { semanticTypes = _semanticTypes, error = Some(error) } -> TypeResolutionResult(semanticType = SemNever, error = Some(error))
        | TypeUnit -> TypeResolutionResult(semanticType = SemTuple([]), error = None)

let isLowerTypeName name =
    (let bytes = Ashes.Byte.fromText(name)
    in
        if Ashes.Byte.length(bytes) <= 0
        then false
        else
            let first = Ashes.Number.UInt.toInt(Ashes.Byte.get(bytes)(0))
            in
                if first >= 97
                then first <= 122
                else false)

let prepareNamedParameter name context supply =
    match (resolvePrimitive(name), context) with
        | (Some(_primitive), _) -> TypeResolutionPreparationResult(context = context, supply = supply)
        | (None, TypeResolutionContext { parameters = parameters, definitions = _definitions }) ->
            match findTypeParameter(name)(parameters) with
                | Some(_parameter) -> TypeResolutionPreparationResult(context = context, supply = supply)
                | None ->
                    if isLowerTypeName(name)
                    then
                        match freshTypeVariable(supply) with
                            | (parameterType, nextSupply) -> TypeResolutionPreparationResult(context = addTypeParameter(name)(parameterType)(context), supply = nextSupply)
                    else TypeResolutionPreparationResult(context = context, supply = supply)

let recursive prepareTypeExpressions expressions context supply =
    match expressions with
        | [] -> TypeResolutionPreparationResult(context = context, supply = supply)
        | head :: tail ->
            match prepareTypeResolutionContext(head)(context)(supply) with
                | TypeResolutionPreparationResult { context = headContext, supply = headSupply } -> prepareTypeExpressions(tail)(headContext)(headSupply)
and prepareCapabilities capabilities context supply =
    match capabilities with
        | [] -> TypeResolutionPreparationResult(context = context, supply = supply)
        | (_name, arguments) :: tail ->
            match prepareTypeExpressions(arguments)(context)(supply) with
                | TypeResolutionPreparationResult { context = argumentContext, supply = argumentSupply } -> prepareCapabilities(tail)(argumentContext)(argumentSupply)
and prepareTypeResolutionContext typeExpression context supply =
    match typeExpression with
        | TypeAt(_span, inner) -> prepareTypeResolutionContext(inner)(context)(supply)
        | TypeNamed(name) -> prepareNamedParameter(name)(context)(supply)
        | TypeApplied(_name, arguments) -> prepareTypeExpressions(arguments)(context)(supply)
        | TypeArrow(argument, result, capabilities, tailName) ->
            match prepareTypeResolutionContext(argument)(context)(supply) with
                | TypeResolutionPreparationResult { context = argumentContext, supply = argumentSupply } ->
                    match prepareTypeResolutionContext(result)(argumentContext)(argumentSupply) with
                        | TypeResolutionPreparationResult { context = resultContext, supply = resultSupply } ->
                            match prepareCapabilities(capabilities)(resultContext)(resultSupply) with
                                | TypeResolutionPreparationResult { context = capabilityContext, supply = capabilitySupply } ->
                                    match tailName with
                                        | None -> TypeResolutionPreparationResult(context = capabilityContext, supply = capabilitySupply)
                                        | Some(name) -> prepareNamedParameter(name)(capabilityContext)(capabilitySupply)
        | TypeTuple(elements) -> prepareTypeExpressions(elements)(context)(supply)
        | TypeUnit -> TypeResolutionPreparationResult(context = context, supply = supply)
