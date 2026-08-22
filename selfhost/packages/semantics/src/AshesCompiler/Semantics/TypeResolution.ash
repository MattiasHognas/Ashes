// Resolves source type syntax into semantic types within a declaration context.
//
// Invariants:
// - Nominal types retain stable symbol identity while aliases expand transparently.
// - Alias cycles are rejected before they can create an infinite semantic type.
// - Capability rows are resolved as unordered structures with an optional open tail.

import AshesCompiler.Frontend.Syntax.TypeExpr
import AshesCompiler.Semantics.Types
import Ashes.Collection.List.reverse
export (
    type DeclarationProvenance(..),
    type TypeDefinition(..),
    type TypeResolutionContext(..),
    type TypeResolutionError(..),
    type TypeResolutionResult(..),
    type TypeResolutionPreparationResult(..),
    type TypeListResolutionResult(..),
    value emptyTypeResolutionContext,
    value addTypeParameter,
    value addTypeDefinition,
    value addTypeDefinitionWithProvenance,
    value addZeroCostTypeDefinitionWithProvenance,
    value addExternalTypeDefinition,
    value addTypeAliasDefinition,
    value nominalTypeProvenance,
    value nextTypeDefinitionSymbolId,
    value prepareTypeResolutionContext,
    value resolveSemanticTypeApplication,
    value resolveTypeExpression,
    value semanticRuntimeRepresentation,
)

type DeclarationProvenance =
    | packageId: Str
    deriving {Eq, Show}

type TypeDefinition =
    | NominalTypeDefinition(Int, Str, Int, DeclarationProvenance)
    | ZeroCostTypeDefinition(Int, Str, List(Int), SemanticType, DeclarationProvenance)
    | AliasTypeDefinition(Str, List(Int), SemanticType)
    | ExternalTypeDefinition(Str, Maybe(Str))

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

let addTypeDefinitionWithProvenance symbolId name arity provenance context =
    match context with
        | TypeResolutionContext { parameters = parameters, definitions = definitions } ->
            TypeResolutionContext(parameters = parameters, definitions = NominalTypeDefinition(
                symbolId,
                name,
                arity,
                provenance
            ) :: definitions)

let addTypeDefinition symbolId name arity context =
    addTypeDefinitionWithProvenance(
        symbolId,
        name,
        arity,
        DeclarationProvenance(packageId = "standalone"),
        context
    )

let addZeroCostTypeDefinitionWithProvenance symbolId name parameterIds representation provenance context =
    match context with
        | TypeResolutionContext { parameters = parameters, definitions = definitions } ->
            TypeResolutionContext(parameters = parameters, definitions = ZeroCostTypeDefinition(
                symbolId,
                name,
                parameterIds,
                representation,
                provenance
            ) :: definitions)

let addTypeAliasDefinition name parameterIds target context =
    match context with
        | TypeResolutionContext { parameters = parameters, definitions = definitions } ->
            TypeResolutionContext(parameters = parameters, definitions = AliasTypeDefinition(
                name,
                parameterIds,
                target
            ) :: definitions)

let addExternalTypeDefinition name destructor context =
    match context with
        | TypeResolutionContext { parameters = parameters, definitions = definitions } -> TypeResolutionContext(parameters = parameters, definitions = ExternalTypeDefinition(name)(destructor) :: definitions)

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
                | NominalTypeDefinition(_symbolId, candidateName, _arity, _provenance) ->
                    if name == candidateName
                    then Some(definition)
                    else findTypeDefinition(name)(tail)
                | ZeroCostTypeDefinition(_symbolId, candidateName, _parameterIds, _representation, _provenance) ->
                    if name == candidateName
                    then Some(definition)
                    else findTypeDefinition(name)(tail)
                | AliasTypeDefinition(candidateName, _parameterIds, _target) ->
                    if name == candidateName
                    then Some(definition)
                    else findTypeDefinition(name)(tail)
                | ExternalTypeDefinition(candidateName, _destructor) ->
                    if name == candidateName
                    then Some(definition)
                    else findTypeDefinition(name)(tail)

let recursive findNominalTypeProvenance symbolId definitions =
    match definitions with
        | [] -> None
        | NominalTypeDefinition(candidateId, _name, _arity, provenance) :: tail ->
            if symbolId == candidateId
            then Some(provenance)
            else findNominalTypeProvenance(symbolId)(tail)
        | ZeroCostTypeDefinition(candidateId, _name, _parameterIds, _representation, provenance) :: tail ->
            if symbolId == candidateId
            then Some(provenance)
            else findNominalTypeProvenance(symbolId)(tail)
        | AliasTypeDefinition(_name, _parameterIds, _target) :: tail -> findNominalTypeProvenance(symbolId)(tail)
        | ExternalTypeDefinition(_name, _destructor) :: tail -> findNominalTypeProvenance(symbolId)(tail)

let nominalTypeProvenance symbolId context =
    match context with
        | TypeResolutionContext { parameters = _parameters, definitions = definitions } ->
            findNominalTypeProvenance(
                symbolId,
                definitions
            )

let recursive maximumTypeDefinitionSymbolId definitions maximum =
    match definitions with
        | [] -> maximum
        | NominalTypeDefinition(symbolId, _name, _arity, _provenance) :: tail ->
            if symbolId > maximum
            then maximumTypeDefinitionSymbolId(tail)(symbolId)
            else maximumTypeDefinitionSymbolId(tail)(maximum)
        | ZeroCostTypeDefinition(symbolId, _name, _parameterIds, _representation, _provenance) :: tail ->
            if symbolId > maximum
            then maximumTypeDefinitionSymbolId(tail)(symbolId)
            else maximumTypeDefinitionSymbolId(tail)(maximum)
        | AliasTypeDefinition(_name, _parameterIds, _target) :: tail -> maximumTypeDefinitionSymbolId(tail)(maximum)
        | ExternalTypeDefinition(_name, _destructor) :: tail -> maximumTypeDefinitionSymbolId(tail)(maximum)

let nextTypeDefinitionSymbolId context =
    match context with
        | TypeResolutionContext { parameters = _parameters, definitions = definitions } ->
            maximumTypeDefinitionSymbolId(
                definitions,
                -1
            ) + 1

let recursive findAliasArgument parameterId parameterIds arguments =
    match (parameterIds, arguments) with
        | (candidateId :: idTail, argument :: argumentTail) ->
            if parameterId == candidateId
            then Some(argument)
            else findAliasArgument(parameterId)(idTail)(argumentTail)
        | _ -> None

let recursive substituteAliasType parameterIds arguments semanticType =
    match semanticType with
        | SemParameter(parameterId, _name) ->
            match findAliasArgument(parameterId)(parameterIds)(arguments) with
                | Some(argument) -> argument
                | None -> semanticType
        | SemList(element) -> SemList(substituteAliasType(parameterIds)(arguments)(element))
        | SemTuple(elements) -> SemTuple(substituteAliasTypes(parameterIds)(arguments)(elements))
        | SemFunction(argument, result, capabilityRow) ->
            let substitutedArgument = substituteAliasType(parameterIds)(arguments)(argument)
            in
                let substitutedResult = substituteAliasType(parameterIds)(arguments)(result)
                in
                    let substitutedRow =
                        match capabilityRow with
                            | None -> None
                            | Some(row) -> Some(substituteAliasType(parameterIds)(arguments)(row))
                    in SemFunction(substitutedArgument)(substitutedResult)(substitutedRow)
        | SemCapability(name, capabilityArguments) ->
            SemCapability(
                name,
                substituteAliasTypes(parameterIds)(arguments)(capabilityArguments)
            )
        | SemRow(capabilities, tail) ->
            let substitutedCapabilities = substituteAliasTypes(parameterIds)(arguments)(capabilities)
            in
                let substitutedTail =
                    match tail with
                        | None -> None
                        | Some(tailType) -> Some(substituteAliasType(parameterIds)(arguments)(tailType))
                in SemRow(substitutedCapabilities)(substitutedTail)
        | SemNamed(symbolId, name, typeArguments) ->
            SemNamed(
                symbolId,
                name,
                substituteAliasTypes(parameterIds)(arguments)(typeArguments)
            )
        | SemPointer(pointee) -> SemPointer(substituteAliasType(parameterIds)(arguments)(pointee))
        | _ -> semanticType
and substituteAliasTypes parameterIds arguments semanticTypes =
    match semanticTypes with
        | [] -> []
        | head :: tail ->
            let substitutedHead = substituteAliasType(parameterIds)(arguments)(head)
            in
                let substitutedTail = substituteAliasTypes(parameterIds)(arguments)(tail)
                in substitutedHead :: substitutedTail

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
                | (_, Some(_primitive)) ->
                    TypeResolutionResult(semanticType = SemNever, error = Some(
                        TypeNameArityMismatch(name)(0)(typeListLength(arguments))
                    ))
                | _ ->
                    match (name, arguments) with
                        | ("List", element :: []) -> TypeResolutionResult(semanticType = SemList(element), error = None)
                        | ("List", _) ->
                            TypeResolutionResult(semanticType = SemNever, error = Some(
                                TypeNameArityMismatch(name)(1)(typeListLength(arguments))
                            ))
                        | ("Ptr", pointee :: []) ->
                            TypeResolutionResult(semanticType = SemPointer(
                                pointee
                            ), error = None)
                        | ("Ptr", _) ->
                            TypeResolutionResult(semanticType = SemNever, error = Some(
                                TypeNameArityMismatch(name)(1)(typeListLength(arguments))
                            ))
                        | (_, []) ->
                            match findTypeParameter(name)(parameters) with
                                | Some(parameter) -> TypeResolutionResult(semanticType = parameter, error = None)
                                | None -> resolveTypeDefinition(name)(arguments)(definitions)
                        | _ -> resolveTypeDefinition(name)(arguments)(definitions)
and resolveTypeDefinition name arguments definitions =
    match findTypeDefinition(name)(definitions) with
        | None -> TypeResolutionResult(semanticType = SemNever, error = Some(UnknownTypeName(name)))
        | Some(NominalTypeDefinition(symbolId, definitionName, arity, _provenance)) ->
            let actualArity = typeListLength(arguments)
            in
                if actualArity == arity
                then TypeResolutionResult(semanticType = SemNamed(symbolId)(definitionName)(arguments), error = None)
                else
                    TypeResolutionResult(semanticType = SemNever, error = Some(
                        TypeNameArityMismatch(name)(arity)(actualArity)
                    ))
        | Some(ZeroCostTypeDefinition(symbolId, definitionName, parameterIds, _representation, _provenance)) ->
            if typeListLength(arguments) == typeListLength(parameterIds)
            then
                TypeResolutionResult(
                    semanticType = SemNamed(symbolId)(definitionName)(arguments),
                    error = None
                )
            else
                TypeResolutionResult(
                    semanticType = SemNever,
                    error = Some(TypeNameArityMismatch(name)(typeListLength(parameterIds))(typeListLength(arguments)))
                )
        | Some(AliasTypeDefinition(_definitionName, parameterIds, target)) ->
            let expectedArity = typeListLength(parameterIds)
            in
                let actualArity = typeListLength(arguments)
                in
                    if actualArity == expectedArity
                    then
                        TypeResolutionResult(semanticType = substituteAliasType(
                            parameterIds,
                            arguments,
                            target
                        ), error = None)
                    else
                        TypeResolutionResult(semanticType = SemNever, error = Some(
                            TypeNameArityMismatch(name)(expectedArity)(actualArity)
                        ))
        | Some(ExternalTypeDefinition(definitionName, _destructor)) ->
            if arguments == []
            then TypeResolutionResult(semanticType = SemOpaque(definitionName), error = None)
            else
                TypeResolutionResult(semanticType = SemNever, error = Some(
                    TypeNameArityMismatch(name)(0)(typeListLength(arguments))
                ))

let resolveSemanticTypeApplication name arguments context = resolveNamed(name)(arguments)(context)

let recursive semanticRuntimeRepresentation semanticType context =
    match (semanticType, context) with
        | (SemNamed(symbolId, _name, arguments), TypeResolutionContext { definitions = definitions }) ->
            match findZeroCostTypeDefinition(symbolId)(definitions) with
                | Some(ZeroCostTypeDefinition(_definitionId, _definitionName, parameterIds, representation, _provenance)) ->
                    semanticRuntimeRepresentation(
                        substituteAliasType(parameterIds)(arguments)(representation)
                    )(context)
                | Some(_ordinaryDefinition) -> semanticType
                | None -> semanticType
        | (SemPointer(pointee), _) -> SemPointer(semanticRuntimeRepresentation(pointee)(context))
        | _ -> semanticType
and findZeroCostTypeDefinition symbolId definitions =
    match definitions with
        | [] -> None
        | (ZeroCostTypeDefinition(candidateId, _name, _parameterIds, _representation, _provenance) as definition) :: tail ->
            if symbolId == candidateId
            then Some(definition)
            else findZeroCostTypeDefinition(symbolId)(tail)
        | _head :: tail -> findZeroCostTypeDefinition(symbolId)(tail)

let recursive resolveTypeExpressions expressions context reversed =
    match expressions with
        | [] -> TypeListResolutionResult(semanticTypes = reversed, error = None)
        | head :: tail ->
            match resolveTypeExpression(head)(context) with
                | TypeResolutionResult { semanticType = semanticType, error = None } ->
                    resolveTypeExpressions(
                        tail,
                        context,
                        semanticType :: reversed
                    )
                | TypeResolutionResult { semanticType = _semanticType, error = Some(error) } ->
                    TypeListResolutionResult(semanticTypes = [], error = Some(
                        error
                    ))
and resolveCapabilities capabilities context reversed =
    match capabilities with
        | [] -> TypeListResolutionResult(semanticTypes = reversed, error = None)
        | (name, arguments) :: tail ->
            match resolveTypeExpressions(arguments)(context)([]) with
                | TypeListResolutionResult { semanticTypes = reversedArguments, error = None } ->
                    resolveCapabilities(
                        tail,
                        context,
                        SemCapability(name)(reverse(reversedArguments)) :: reversed
                    )
                | TypeListResolutionResult { semanticTypes = _semanticTypes, error = Some(error) } ->
                    TypeListResolutionResult(semanticTypes = [], error = Some(
                        error
                    ))
and resolveTypeExpression typeExpression context =
    match typeExpression with
        | TypeAt(_span, inner) -> resolveTypeExpression(inner)(context)
        | TypeNamed(name) -> resolveNamed(name)([])(context)
        | TypeApplied(name, arguments) ->
            match resolveTypeExpressions(arguments)(context)([]) with
                | TypeListResolutionResult { semanticTypes = reversedArguments, error = None } ->
                    resolveNamed(
                        name,
                        reverse(reversedArguments),
                        context
                    )
                | TypeListResolutionResult { semanticTypes = _semanticTypes, error = Some(error) } ->
                    TypeResolutionResult(semanticType = SemNever, error = Some(
                        error
                    ))
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
                                                | None ->
                                                    TypeResolutionResult(semanticType = SemRow(
                                                        [],
                                                        None
                                                    ), error = None)
                                                | Some(name) -> resolveNamed(name)([])(context)
                                        in
                                            match resolvedTail with
                                                | TypeResolutionResult { semanticType = tailType, error = None } ->
                                                    let row =
                                                        match (resolvedCapabilities, tailName) with
                                                            | ([], None) -> None
                                                            | (_, None) -> Some(SemRow(resolvedCapabilities)(None))
                                                            | (_, Some(_)) ->
                                                                Some(
                                                                    SemRow(resolvedCapabilities)(Some(tailType))
                                                                )
                                                    in
                                                        TypeResolutionResult(semanticType = SemFunction(
                                                            argumentType,
                                                            resultType,
                                                            row
                                                        ), error = None)
                                                | TypeResolutionResult { semanticType = _tailType, error = Some(error) } ->
                                                    TypeResolutionResult(semanticType = SemNever, error = Some(
                                                        error
                                                    ))
                                | TypeListResolutionResult { semanticTypes = _semanticTypes, error = Some(error) } ->
                                    TypeResolutionResult(semanticType = SemNever, error = Some(
                                        error
                                    ))
                        | TypeResolutionResult { semanticType = _resultType, error = Some(error) } ->
                            TypeResolutionResult(semanticType = SemNever, error = Some(
                                error
                            ))
                | TypeResolutionResult { semanticType = _argumentType, error = Some(error) } ->
                    TypeResolutionResult(semanticType = SemNever, error = Some(
                        error
                    ))
        | TypeTuple(elements) ->
            match resolveTypeExpressions(elements)(context)([]) with
                | TypeListResolutionResult { semanticTypes = reversedElements, error = None } ->
                    TypeResolutionResult(semanticType = SemTuple(
                        reverse(reversedElements)
                    ), error = None)
                | TypeListResolutionResult { semanticTypes = _semanticTypes, error = Some(error) } ->
                    TypeResolutionResult(semanticType = SemNever, error = Some(
                        error
                    ))
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
                            | (parameterType, nextSupply) ->
                                TypeResolutionPreparationResult(context = addTypeParameter(
                                    name,
                                    parameterType,
                                    context
                                ), supply = nextSupply)
                    else TypeResolutionPreparationResult(context = context, supply = supply)

let recursive prepareTypeExpressions expressions context supply =
    match expressions with
        | [] -> TypeResolutionPreparationResult(context = context, supply = supply)
        | head :: tail ->
            match prepareTypeResolutionContext(head)(context)(supply) with
                | TypeResolutionPreparationResult { context = headContext, supply = headSupply } ->
                    prepareTypeExpressions(
                        tail,
                        headContext,
                        headSupply
                    )
and prepareCapabilities capabilities context supply =
    match capabilities with
        | [] -> TypeResolutionPreparationResult(context = context, supply = supply)
        | (_name, arguments) :: tail ->
            match prepareTypeExpressions(arguments)(context)(supply) with
                | TypeResolutionPreparationResult { context = argumentContext, supply = argumentSupply } ->
                    prepareCapabilities(
                        tail,
                        argumentContext,
                        argumentSupply
                    )
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
