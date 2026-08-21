// Expands deriving clauses into ordinary implementation declarations before inference.
//
// Invariants:
// - Generated implementations retain declaration order and use ordinary trait calls.
// - Only payload-contributing type parameters become implementation requirements.
// - Validation uses a program-global declaration context, so opaque/resource/capability fields and
//   aliases to them are rejected independently of declaration order before coherence.

import AshesCompiler.Frontend.Syntax
import Ashes.Collection.List.append as appendList
import Ashes.Collection.List.reverse
import Ashes.Text
import Ashes.Byte
import Ashes.Number.UInt
export (
    type DerivingExpansionError(..),
    type DerivingTypeContext(..),
    value emptyDerivingTypeContext,
    value addDerivingItemsToContext,
    value expandDerivedImplementationsFrom,
    value expandDerivedImplementations,
)

type DerivingExpansionError =
    | UnsupportedDerivedTrait(Str, Str)
    | DuplicateDerivedTrait(Str, Str)
    | UnsupportedDerivedField(Str, Str)
    deriving {Eq, Show}

type DerivingExpansionResult =
    | items: List(TopLevelItem)
    | error: Maybe(DerivingExpansionError)

type DerivedPattern =
    | pattern: Pattern
    | fields: List(Str)

type DerivedTypeDefinition =
    | DerivedOpaqueType(Str)
    | DerivedResourceType(Str)
    | DerivedCapabilityType(Str)
    | DerivedTypeAlias(Str, List(Str), TypeExpr)

type DerivingTypeContext =
    | derivedDefinitions: List(DerivedTypeDefinition)

let emptyDerivingTypeContext unit = DerivingTypeContext(derivedDefinitions = [])

let recursive derivedParameterNames parameters =
    match parameters with
        | [] -> []
        | TypeParameter { name = name } :: tail -> name :: derivedParameterNames(tail)

let derivedExternalTypeDefinition name resource =
    match resource with
        | Some(_releaseFunction) -> DerivedResourceType(name)
        | None -> DerivedOpaqueType(name)

let recursive addDerivingItemToContext item context =
    match (item, context) with
        | (TopLevelAt(_span, inner), _) -> addDerivingItemToContext(inner)(context)
        | (TopLevelExternal(ExternalOpaqueType(name, resource)), DerivingTypeContext { derivedDefinitions = definitions }) ->
            DerivingTypeContext(derivedDefinitions = derivedExternalTypeDefinition(
                name,
                resource
            ) :: definitions)
        | (TopLevelCapability(CapabilityDecl { name = name }), DerivingTypeContext { derivedDefinitions = definitions }) ->
            DerivingTypeContext(derivedDefinitions = DerivedCapabilityType(
                name
            ) :: definitions)
        | (TopLevelTypeAlias(TypeAliasDecl { name = name, typeParameters = parameters, target = target }), DerivingTypeContext { derivedDefinitions = definitions }) ->
            DerivingTypeContext(derivedDefinitions = DerivedTypeAlias(
                name,
                derivedParameterNames(parameters),
                target
            ) :: definitions)
        | _ -> context

let recursive addDerivingItemsToContext items context =
    match items with
        | [] -> context
        | head :: tail ->
            context
            |> addDerivingItemToContext(head)
            |> addDerivingItemsToContext(tail)

let recursive lastDerivedNamePart parts =
    match parts with
        | [] -> ""
        | name :: [] -> name
        | _head :: tail -> lastDerivedNamePart(tail)

let derivedTraitLeafName name =
    "."
    |> Ashes.Text.split(name)
    |> lastDerivedNamePart

let sameText left right =
    Ashes.Byte.compare(Ashes.Byte.fromText(left))(Ashes.Byte.fromText(right)) == 0

let derivedTraitIsSupported (name: Str) =
    match name with
        | "Eq" -> true
        | "Ord" -> true
        | "Show" -> true
        | "Hash" -> true
        | _ -> false

let recursive textExists (name: Str) (names: List(Str)) =
    match names with
        | [] -> false
        | head :: tail ->
            if sameText(name)(head)
            then true
            else textExists(name)(tail)

let recursive parameterNames parameters =
    match parameters with
        | [] -> []
        | TypeParameter { name = name } :: tail -> name :: parameterNames(tail)

let recursive exactTypeParameterArguments (expected: List(Str)) (arguments: List(TypeExpr)) =
    match (expected, arguments) with
        | ([], []) -> true
        | (name :: nameTail, TypeNamed(argumentName) :: argumentTail) ->
            if sameText(name)(argumentName)
            then exactTypeParameterArguments(nameTail)(argumentTail)
            else false
        | _ -> false

let recursive findDerivedTypeDefinition name definitions =
    match definitions with
        | [] -> None
        | (DerivedOpaqueType(candidate) as definition) :: tail ->
            if sameText(name)(candidate)
            then Some(definition)
            else findDerivedTypeDefinition(name)(tail)
        | (DerivedResourceType(candidate) as definition) :: tail ->
            if sameText(name)(candidate)
            then Some(definition)
            else findDerivedTypeDefinition(name)(tail)
        | (DerivedCapabilityType(candidate) as definition) :: tail ->
            if sameText(name)(candidate)
            then Some(definition)
            else findDerivedTypeDefinition(name)(tail)
        | (DerivedTypeAlias(candidate, _parameters, _target) as definition) :: tail ->
            if sameText(name)(candidate)
            then Some(definition)
            else findDerivedTypeDefinition(name)(tail)

let resolveDerivedTypeDefinition name context =
    match context with
        | DerivingTypeContext { derivedDefinitions = definitions } -> findDerivedTypeDefinition(name)(definitions)

let recursive findDerivedAliasArgument name parameters arguments =
    match (parameters, arguments) with
        | (parameter :: parameterTail, argument :: argumentTail) ->
            if sameText(name)(parameter)
            then Some(argument)
            else findDerivedAliasArgument(name)(parameterTail)(argumentTail)
        | _ -> None

let recursive substituteDerivedAliasType parameters arguments typeExpression =
    match typeExpression with
        | TypeAt(span, inner) ->
            inner
            |> substituteDerivedAliasType(parameters)(arguments)
            |> TypeAt(span)
        | TypeNamed(name) ->
            match findDerivedAliasArgument(name)(parameters)(arguments) with
                | Some(argument) -> argument
                | None -> typeExpression
        | TypeApplied(name, typeArguments) ->
            typeArguments
            |> substituteDerivedAliasTypes(parameters)(arguments)
            |> TypeApplied(name)
        | TypeArrow(argument, result, capabilities, tail) ->
            TypeArrow(
                substituteDerivedAliasType(parameters)(arguments)(argument),
                substituteDerivedAliasType(parameters)(arguments)(result),
                substituteDerivedAliasCapabilities(parameters)(arguments)(capabilities),
                tail
            )
        | TypeTuple(elements) ->
            elements
            |> substituteDerivedAliasTypes(parameters)(arguments)
            |> TypeTuple
        | TypeUnit -> TypeUnit
and substituteDerivedAliasTypes parameters arguments types =
    match types with
        | [] -> []
        | head :: tail ->
            substituteDerivedAliasType(parameters)(arguments)(head) :: substituteDerivedAliasTypes(
                parameters,
                arguments,
                tail
            )
and substituteDerivedAliasCapabilities parameters arguments capabilities =
    match capabilities with
        | [] -> []
        | (name, typeArguments) :: tail ->
            (name, substituteDerivedAliasTypes(
                parameters,
                arguments,
                typeArguments
            )) :: substituteDerivedAliasCapabilities(parameters)(arguments)(tail)

let derivedBuiltinFieldIsUnsupported name =
    match name with
        | "Task" -> true
        | "Socket" -> true
        | "TlsSocket" -> true
        | "Process" -> true
        | "FileHandle" -> true
        | "JoinHandle" -> true
        | _ -> false

let recursive derivedTypeCount values =
    match values with
        | [] -> 0
        | _head :: tail -> 1 + derivedTypeCount(tail)

let derivedNamedVariableIsSupported name declaredParameters =
    (let bytes = Ashes.Byte.fromText(name)
    in
        if Ashes.Byte.length(bytes) <= 0
        then false
        else
            let first =
                0
                |> Ashes.Byte.get(bytes)
                |> Ashes.Number.UInt.toInt
            in
                match (first >= 97, first <= 122) with
                    | (true, true) -> textExists(name)(declaredParameters)
                    | _ -> true)

let recursive derivedTypeIsSupported (declarationName: Str) (declaredParameters: List(Str)) (context: DerivingTypeContext) (visitedAliases: List(Str)) (typeExpression: TypeExpr) =
    match typeExpression with
        | TypeAt(_span, inner) ->
            derivedTypeIsSupported(
                declarationName,
                declaredParameters,
                context,
                visitedAliases,
                inner
            )
        | TypeArrow(_argument, _result, _capabilities, _tail) -> false
        | TypeTuple(elements) ->
            derivedTypesAreSupported(
                declarationName,
                declaredParameters,
                context,
                visitedAliases,
                elements
            )
        | TypeApplied(name, arguments) ->
            match (sameText(name)(declarationName), sameText(name)("Ptr"), derivedBuiltinFieldIsUnsupported(name)) with
                | (true, _, _) -> exactTypeParameterArguments(declaredParameters)(arguments)
                | (_, true, _) -> false
                | (_, _, true) -> false
                | _ ->
                    match resolveDerivedTypeDefinition(name)(context) with
                        | Some(DerivedOpaqueType(_name)) -> false
                        | Some(DerivedResourceType(_name)) -> false
                        | Some(DerivedCapabilityType(_name)) -> false
                        | Some(DerivedTypeAlias(aliasName, parameters, target)) ->
                            derivedAliasIsSupported(
                                declarationName,
                                declaredParameters,
                                aliasName,
                                parameters,
                                arguments,
                                target,
                                context,
                                visitedAliases
                            )
                        | None ->
                            derivedTypesAreSupported(
                                declarationName,
                                declaredParameters,
                                context,
                                visitedAliases,
                                arguments
                            )
        | TypeNamed(name) ->
            match (sameText(name)(declarationName), derivedBuiltinFieldIsUnsupported(name)) with
                | (true, _) ->
                    match declaredParameters with
                        | [] -> true
                        | _ -> false
                | (_, true) -> false
                | _ ->
                    match resolveDerivedTypeDefinition(name)(context) with
                        | Some(DerivedOpaqueType(_name)) -> false
                        | Some(DerivedResourceType(_name)) -> false
                        | Some(DerivedCapabilityType(_name)) -> false
                        | Some(DerivedTypeAlias(aliasName, parameters, target)) ->
                            derivedAliasIsSupported(
                                declarationName,
                                declaredParameters,
                                aliasName,
                                parameters,
                                [],
                                target,
                                context,
                                visitedAliases
                            )
                        | None -> derivedNamedVariableIsSupported(name)(declaredParameters)
        | TypeUnit -> true
and derivedTypesAreSupported declarationName declaredParameters context visitedAliases types =
    match types with
        | [] -> true
        | head :: tail ->
            if derivedTypeIsSupported(declarationName)(declaredParameters)(context)(visitedAliases)(head)
            then derivedTypesAreSupported(declarationName)(declaredParameters)(context)(visitedAliases)(tail)
            else false
and derivedAliasIsSupported declarationName declaredParameters aliasName parameters arguments target context visitedAliases =
    match (textExists(aliasName)(visitedAliases), derivedTypeCount(parameters) == derivedTypeCount(arguments)) with
        | (true, _) -> true
        | (_, false) -> true
        | _ ->
            target
            |> substituteDerivedAliasType(parameters)(arguments)
            |> derivedTypeIsSupported(declarationName)(declaredParameters)(context)(aliasName :: visitedAliases)

let recursive derivedConstructorsAreSupported declarationName declaredParameters context constructors =
    match constructors with
        | [] -> true
        | TypeConstructor { name = _name, parameters = fields, fieldNames = _fieldNames } :: tail ->
            if derivedTypesAreSupported(declarationName)(declaredParameters)(context)([])(fields)
            then derivedConstructorsAreSupported(declarationName)(declaredParameters)(context)(tail)
            else false

let recursive typeMentionsParameter (parameter: Str) (typeExpression: TypeExpr) =
    match typeExpression with
        | TypeAt(_span, inner) -> typeMentionsParameter(parameter)(inner)
        | TypeNamed(name) -> sameText(name)(parameter)
        | TypeApplied(_name, arguments) -> typesMentionParameter(parameter)(arguments)
        | TypeArrow(argument, result, capabilities, tail) ->
            if typeMentionsParameter(parameter)(argument)
            then true
            else
                if typeMentionsParameter(parameter)(result)
                then true
                else
                    if capabilityTypesMentionParameter(parameter)(capabilities)
                    then true
                    else
                        match tail with
                            | Some(name) -> sameText(name)(parameter)
                            | None -> false
        | TypeTuple(elements) -> typesMentionParameter(parameter)(elements)
        | TypeUnit -> false
and typesMentionParameter parameter types =
    match types with
        | [] -> false
        | head :: tail ->
            if typeMentionsParameter(parameter)(head)
            then true
            else typesMentionParameter(parameter)(tail)
and capabilityTypesMentionParameter parameter capabilities =
    match capabilities with
        | [] -> false
        | (_name, arguments) :: tail ->
            if typesMentionParameter(parameter)(arguments)
            then true
            else capabilityTypesMentionParameter(parameter)(tail)

let recursive constructorFieldsMentionParameter parameter constructors =
    match constructors with
        | [] -> false
        | TypeConstructor { name = _name, parameters = fields, fieldNames = _fieldNames } :: tail ->
            if typesMentionParameter(parameter)(fields)
            then true
            else constructorFieldsMentionParameter(parameter)(tail)

let recursive derivedRequirements traitName parameters constructors =
    match parameters with
        | [] -> []
        | TypeParameter { name = name } :: tail ->
            let rest = derivedRequirements(traitName)(tail)(constructors)
            in
                if constructorFieldsMentionParameter(name)(constructors)
                then TraitConstraintSyntax(traitName = traitName, typeArguments = [TypeNamed(name)]) :: rest
                else rest

let recursive generatedFieldNames prefix count index =
    if index >= count
    then []
    else prefix + "_" + Ashes.Text.fromInt(index) :: generatedFieldNames(prefix)(count)(index + 1)

let recursive wildcardPatterns count =
    if count <= 0
    then []
    else PatternWildcard :: wildcardPatterns(count - 1)

let recursive variablePatterns names =
    match names with
        | [] -> []
        | name :: tail -> PatternVar(name) :: variablePatterns(tail)

let recursive typeCount values =
    match values with
        | [] -> 0
        | _head :: tail -> 1 + typeCount(tail)

let derivedConstructorPattern constructor prefix bindFields =
    match constructor with
        | TypeConstructor { name = name, parameters = parameters, fieldNames = _fieldNames } ->
            if bindFields
            then
                let fields =
                    generatedFieldNames(prefix)(typeCount(parameters))(0)
                in
                    DerivedPattern(pattern = fields
                    |> variablePatterns
                    |> PatternConstructor(name), fields = fields)
            else
                DerivedPattern(pattern = parameters
                |> typeCount
                |> wildcardPatterns
                |> PatternConstructor(name), fields = [])

let recursive applyDerivedTraitCall expression arguments =
    match arguments with
        | [] -> expression
        | head :: tail ->
            applyDerivedTraitCall(ExprCall(expression)(head)(false)(callArgumentsInline))(tail)

let derivedTraitCall traitName methodName arguments =
    applyDerivedTraitCall(ExprQualifiedVar(traitName)(methodName))(arguments)

let recursive buildDerivedEquality fieldsLeft fieldsRight result =
    match (fieldsLeft, fieldsRight) with
        | ([], []) -> result
        | (left :: leftTail, right :: rightTail) ->
            ExprIf(
                derivedTraitCall("Eq")("equal")([ExprVar(left), ExprVar(right)]),
                buildDerivedEquality(leftTail)(rightTail)(result),
                ExprBool(false)
            )
        | _ -> ExprBool(false)

let recursive derivedEqCases constructors index =
    match constructors with
        | [] -> [(PatternWildcard, ExprBool(false), None)]
        | constructor :: tail ->
            match derivedConstructorPattern(constructor)("__derived_left_" + Ashes.Text.fromInt(index))(true) with
                | DerivedPattern { pattern = leftPattern, fields = leftFields } ->
                    match derivedConstructorPattern(
                        constructor,
                        "__derived_right_" + Ashes.Text.fromInt(index),
                        true
                    ) with
                        | DerivedPattern { pattern = rightPattern, fields = rightFields } ->
                            (PatternTuple(
                                [leftPattern, rightPattern]
                            ), buildDerivedEquality(
                                leftFields,
                                rightFields,
                                ExprBool(true)
                            ), None) :: derivedEqCases(tail)(index + 1)

let derivedEqBody constructors =
    ExprLambda(
        "__derived_left",
        ExprLambda(
            "__derived_right",
            ExprMatch(
                ExprTuple([ExprVar("__derived_left"), ExprVar("__derived_right")]),
                derivedEqCases(constructors)(0),
                None
            ),
            None
        ),
        None
    )

let recursive buildDerivedOrdering fieldsLeft fieldsRight fieldIndex result =
    match (fieldsLeft, fieldsRight) with
        | ([], []) -> result
        | (left :: leftTail, right :: rightTail) ->
            let orderingName = "__derived_ordering_" + Ashes.Text.fromInt(fieldIndex)
            in
                ExprMatch(
                    derivedTraitCall("Ord")("compare")([ExprVar(left), ExprVar(right)]),
                    [(PatternConstructor("Equal")([]), buildDerivedOrdering(
                        leftTail,
                        rightTail,
                        fieldIndex + 1,
                        result
                    ), None), (PatternVar(orderingName), ExprVar(orderingName), None)],
                    None
                )
        | _ -> result

let derivedOrdPairCase leftConstructor leftIndex rightConstructor rightIndex same differentResult =
    match derivedConstructorPattern(leftConstructor)("__derived_left_" + Ashes.Text.fromInt(leftIndex))(same) with
        | DerivedPattern { pattern = leftPattern, fields = leftFields } ->
            match derivedConstructorPattern(
                rightConstructor,
                "__derived_right_" + Ashes.Text.fromInt(rightIndex),
                same
            ) with
                | DerivedPattern { pattern = rightPattern, fields = rightFields } ->
                    let result =
                        if same
                        then buildDerivedOrdering(leftFields)(rightFields)(0)(ExprVar("Equal"))
                        else differentResult
                    in (PatternTuple([leftPattern, rightPattern]), result, None)

let recursive derivedOrdDifferentCases leftConstructor leftIndex rightConstructors rightIndex result =
    match rightConstructors with
        | [] -> []
        | rightConstructor :: tail ->
            derivedOrdPairCase(
                leftConstructor,
                leftIndex,
                rightConstructor,
                rightIndex,
                false,
                result
            ) :: derivedOrdDifferentCases(leftConstructor)(leftIndex)(tail)(rightIndex + 1)(result)

let recursive derivedOrdCases constructors beforeReversed leftIndex =
    match constructors with
        | [] -> []
        | leftConstructor :: tail ->
            let earlier =
                derivedOrdDifferentCases(leftConstructor)(leftIndex)(reverse(beforeReversed))(0)(ExprVar("Greater"))
            in
                let same =
                    [derivedOrdPairCase(
                        leftConstructor,
                        leftIndex,
                        leftConstructor,
                        leftIndex,
                        true,
                        ExprVar("Equal")
                    )]
                in
                    let later =
                        derivedOrdDifferentCases(
                            leftConstructor,
                            leftIndex,
                            tail,
                            leftIndex + 1,
                            ExprVar("Less")
                        )
                    in
                        leftIndex + 1
                        |> derivedOrdCases(tail)(leftConstructor :: beforeReversed)
                        |> appendList(later)
                        |> appendList(appendList(earlier)(same))

let derivedOrdBody constructors =
    ExprLambda(
        "__derived_left",
        ExprLambda(
            "__derived_right",
            ExprMatch(
                ExprTuple([ExprVar("__derived_left"), ExprVar("__derived_right")]),
                derivedOrdCases(constructors)([])(0),
                None
            ),
            None
        ),
        None
    )

let recursive appendDerivedShowFields fields fieldNames isRecord first result =
    match fields with
        | [] -> ExprAdd(result)(ExprString(")"))
        | field :: tail ->
            let separator =
                if first
                then ""
                else ", "
            in
                let prefix =
                    if isRecord
                    then
                        match fieldNames with
                            | name :: _ -> separator + name + " = "
                            | [] -> separator
                    else separator
                in
                    let remainingNames =
                        match fieldNames with
                            | _head :: rest -> rest
                            | [] -> []
                    in
                        [ExprVar(field)]
                        |> derivedTraitCall("Show")("show")
                        |> ExprAdd(ExprAdd(result)(ExprString(prefix)))
                        |> appendDerivedShowFields(tail)(remainingNames)(isRecord)(false)

let derivedShowCase constructor isRecord index =
    match constructor with
        | TypeConstructor { name = name, parameters = parameters, fieldNames = fieldNames } ->
            match derivedConstructorPattern(constructor)("__derived_field_" + Ashes.Text.fromInt(index))(true) with
                | DerivedPattern { pattern = pattern, fields = fields } ->
                    let result =
                        match parameters with
                            | [] -> ExprString(name)
                            | _ -> appendDerivedShowFields(fields)(fieldNames)(isRecord)(true)(ExprString(name + "("))
                    in (pattern, result, None)

let recursive derivedShowCases constructors isRecord index =
    match constructors with
        | [] -> []
        | constructor :: tail ->
            derivedShowCase(
                constructor,
                isRecord,
                index
            ) :: derivedShowCases(tail)(isRecord)(index + 1)

let derivedShowBody constructors isRecord =
    ExprLambda(
        "__derived_value",
        ExprMatch(ExprVar("__derived_value"))(derivedShowCases(constructors)(isRecord)(0))(None),
        None
    )

let recursive foldDerivedHash fields result =
    match fields with
        | [] -> result
        | field :: tail ->
            [ExprVar(field)]
            |> derivedTraitCall("Hash")("hash")
            |> ExprAdd(ExprMultiply(result)(ExprInt(16777619)))
            |> foldDerivedHash(tail)

let derivedHashCase constructor index =
    match derivedConstructorPattern(constructor)("__derived_field_" + Ashes.Text.fromInt(index))(true) with
        | DerivedPattern { pattern = pattern, fields = fields } ->
            (pattern, foldDerivedHash(
                fields,
                ExprInt(index + 1)
            ), None)

let recursive derivedHashCases constructors index =
    match constructors with
        | [] -> []
        | constructor :: tail -> derivedHashCase(constructor)(index) :: derivedHashCases(tail)(index + 1)

let derivedHashBody constructors =
    ExprLambda("__derived_value")(ExprMatch(ExprVar("__derived_value"))(derivedHashCases(constructors)(0))(None))(None)

let derivedImplementationHead name parameters =
    match parameters with
        | [] -> TypeNamed(name)
        | _ ->
            let recursive arguments values =
                match values with
                    | [] -> []
                    | TypeParameter { name = parameterName } :: tail -> TypeNamed(parameterName) :: arguments(tail)
            in
                parameters
                |> arguments
                |> TypeApplied(name)

let derivedMethodName traitName =
    match traitName with
        | "Eq" -> "equal"
        | "Ord" -> "compare"
        | "Show" -> "show"
        | "Hash" -> "hash"
        | _ -> ""

let derivedMethodBody traitName constructors isRecord =
    match traitName with
        | "Eq" -> derivedEqBody(constructors)
        | "Ord" -> derivedOrdBody(constructors)
        | "Show" -> derivedShowBody(constructors)(isRecord)
        | "Hash" -> derivedHashBody(constructors)
        | _ -> ExprBool(false)

let createDerivedImplementation declaration traitName =
    match declaration with
        | TypeDecl { name = name, typeParameters = parameters, constructors = constructors, isRecord = isRecord, derivingTraits = _derivingTraits } ->
            let binding =
                TraitImplementationMethodBinding(methodName = derivedMethodName(
                    traitName
                ), implementation = derivedMethodBody(
                    traitName,
                    constructors,
                    isRecord
                ))
            in
                TraitImplementationDecl(traitName = traitName, typeArguments = [derivedImplementationHead(
                    name,
                    parameters
                )], requirements = derivedRequirements(
                    traitName,
                    parameters,
                    constructors
                ), bindings = [binding])

let recursive deriveTypeImplementations context declaration remaining seen reversed =
    match remaining with
        | [] -> DerivingExpansionResult(items = reverse(reversed), error = None)
        | writtenName :: tail ->
            let traitName = derivedTraitLeafName(writtenName)
            in
                match declaration with
                    | TypeDecl { name = declarationName, typeParameters = parameters, constructors = constructors, isRecord = _isRecord, derivingTraits = _derivingTraits } ->
                        match (derivedTraitIsSupported(traitName), textExists(
                            traitName,
                            seen
                        ), derivedConstructorsAreSupported(
                            declarationName,
                            parameterNames(parameters),
                            context,
                            constructors
                        )) with
                            | (false, _, _) ->
                                DerivingExpansionResult(items = [], error = writtenName
                                |> UnsupportedDerivedTrait(declarationName)
                                |> Some)
                            | (_, true, _) ->
                                DerivingExpansionResult(items = [], error = traitName
                                |> DuplicateDerivedTrait(declarationName)
                                |> Some)
                            | (_, _, false) ->
                                DerivingExpansionResult(items = [], error = traitName
                                |> UnsupportedDerivedField(declarationName)
                                |> Some)
                            | _ ->
                                deriveTypeImplementations(
                                    context,
                                    declaration,
                                    tail,
                                    traitName :: seen,
                                    TopLevelImplementation(
                                        createDerivedImplementation(declaration)(traitName)
                                    ) :: reversed
                                )

let ordinaryTypeWithoutDeriving declaration =
    match declaration with
        | TypeDecl { name = name, typeParameters = parameters, constructors = constructors, isRecord = isRecord, derivingTraits = _derivingTraits } -> TypeDecl(name = name, typeParameters = parameters, constructors = constructors, isRecord = isRecord, derivingTraits = [])

let zeroCostTypeWithoutDeriving declaration =
    match declaration with
        | ZeroCostTypeDecl { name = name, typeParameters = parameters, constructor = constructor, derivingTraits = _derivingTraits } -> ZeroCostTypeDecl(name = name, typeParameters = parameters, constructor = constructor, derivingTraits = [])

let zeroCostAsOrdinary declaration =
    match declaration with
        | ZeroCostTypeDecl { name = name, typeParameters = parameters, constructor = constructor, derivingTraits = derivingTraits } -> TypeDecl(name = name, typeParameters = parameters, constructors = [constructor], isRecord = false, derivingTraits = derivingTraits)

let expandTypeItem context declaration =
    match declaration with
        | TypeDecl { derivingTraits = [] } -> DerivingExpansionResult(items = [TopLevelType(declaration)], error = None)
        | TypeDecl { derivingTraits = derivingTraits } ->
            match deriveTypeImplementations(context)(declaration)(derivingTraits)([])([]) with
                | DerivingExpansionResult { items = implementations, error = None } ->
                    DerivingExpansionResult(items = TopLevelType(
                        ordinaryTypeWithoutDeriving(declaration)
                    ) :: implementations, error = None)
                | failure -> failure

let expandZeroCostTypeItem context declaration =
    match declaration with
        | ZeroCostTypeDecl { derivingTraits = [] } ->
            DerivingExpansionResult(items = [TopLevelZeroCostType(
                declaration
            )], error = None)
        | ZeroCostTypeDecl { derivingTraits = derivingTraits } ->
            match deriveTypeImplementations(context)(zeroCostAsOrdinary(declaration))(derivingTraits)([])([]) with
                | DerivingExpansionResult { items = implementations, error = None } ->
                    DerivingExpansionResult(items = TopLevelZeroCostType(
                        zeroCostTypeWithoutDeriving(declaration)
                    ) :: implementations, error = None)
                | failure -> failure

let spanExpandedItems span items =
    (let recursive wrap values =
        match values with
            | [] -> []
            | head :: tail -> TopLevelAt(span)(head) :: wrap(tail)
    in wrap(items))

let recursive expandTopLevelItem context item =
    match item with
        | TopLevelAt(span, inner) ->
            match expandTopLevelItem(context)(inner) with
                | DerivingExpansionResult { items = items, error = None } ->
                    DerivingExpansionResult(items = spanExpandedItems(
                        span,
                        items
                    ), error = None)
                | failure -> failure
        | TopLevelType(declaration) -> expandTypeItem(context)(declaration)
        | TopLevelZeroCostType(declaration) -> expandZeroCostTypeItem(context)(declaration)
        | _ -> DerivingExpansionResult(items = [item], error = None)

let recursive expandTopLevelItems context remaining reversed =
    match remaining with
        | [] -> DerivingExpansionResult(items = reverse(reversed), error = None)
        | head :: tail ->
            match expandTopLevelItem(context)(head) with
                | DerivingExpansionResult { items = items, error = None } ->
                    reversed
                    |> appendList(reverse(items))
                    |> expandTopLevelItems(context)(tail)
                | failure -> failure

let expandDerivedImplementationsFrom context program =
    match program with
        | ProgramSyntax { items = items, body = body } ->
            match expandTopLevelItems(context)(items)([]) with
                | DerivingExpansionResult { items = expanded, error = None } ->
                    Ok(
                        ProgramSyntax(items = expanded, body = body)
                    )
                | DerivingExpansionResult { items = _items, error = Some(error) } -> Error(error)

let expandDerivedImplementations program =
    match program with
        | ProgramSyntax { items = items } ->
            expandDerivedImplementationsFrom(Unit
            |> emptyDerivingTypeContext
            |> addDerivingItemsToContext(items))(program)
