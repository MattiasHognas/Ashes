import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.Pattern
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.Unification
import AshesCompiler.Semantics.TypeSchemes
import AshesCompiler.Semantics.TypeResolution
import Ashes.Collection.List.reverse
export (
    type TypeEnvironment(..),
    type ConstructorInferenceDefinition(..),
    type TypeInferenceError(..),
    type TypeInferenceResult(..),
    type TopLevelBindingInferenceResult(..),
    value emptyTypeEnvironment,
    value addTypeBinding,
    value addInferenceTypeDefinition,
    value addInferenceTypeAlias,
    value addConstructorBinding,
    value inferenceTypeResolutionContext,
    value inferenceEnvironmentSchemes,
    value applyInferenceConstraints,
    value checkInferenceAnnotation,
    value inferExpressionFrom,
    value inferTopLevelBinding,
    value inferExpression,
)

type ConstructorInferenceDefinition =
    | name: Str
    | scheme: TypeScheme
    | fieldNames: List(Str)

type TypeEnvironment =
    | bindings: List((Str, TypeScheme))
    | constructors: List(ConstructorInferenceDefinition)
    | typeResolutionContext: TypeResolutionContext

type TypeInferenceError =
    | UnknownValue(Str)
    | InferenceUnificationError(UnificationError)
    | InferenceTypeResolutionError(TypeResolutionError)
    | DuplicatePatternBinding(Str)
    | UnknownPatternConstructor(Str)
    | ConstructorPatternArityMismatch(Str)
    | UnknownRecordPatternField(Str, Str)
    | DuplicateRecordPatternField(Str)
    | InconsistentOrPatternBindings
    | UnsupportedInferencePattern(Str)
    | UnsupportedInferenceExpression(Str)
    deriving {Eq, Show}

type TypeInferenceResult =
    | semanticType: SemanticType
    | substitution: List((Int, SemanticType))
    | supply: TypeVariableSupply
    | constraints: List(TraitConstraint)
    | error: Maybe(TypeInferenceError)
    deriving {Eq, Show}

type TopLevelBindingInferenceResult =
    | environment: TypeEnvironment
    | semanticType: SemanticType
    | substitution: List((Int, SemanticType))
    | supply: TypeVariableSupply
    | error: Maybe(TypeInferenceError)

type PatternInferenceResult =
    | semanticType: SemanticType
    | environment: TypeEnvironment
    | substitution: List((Int, SemanticType))
    | supply: TypeVariableSupply
    | names: List(Str)
    | error: Maybe(TypeInferenceError)

type ConstructorTypeShape =
    | parameters: List(SemanticType)
    | resultType: SemanticType

let emptyTypeEnvironment unit = TypeEnvironment(bindings = [], constructors = [], typeResolutionContext = emptyTypeResolutionContext(Unit))

let addTypeBinding name scheme environment =
    match environment with
        | TypeEnvironment { bindings = bindings, constructors = constructors, typeResolutionContext = typeResolutionContext } -> TypeEnvironment(bindings = (name, scheme) :: bindings, constructors = constructors, typeResolutionContext = typeResolutionContext)

let addConstructorBinding name scheme fieldNames environment =
    match addTypeBinding(name)(scheme)(environment) with
        | TypeEnvironment { bindings = bindings, constructors = constructors, typeResolutionContext = typeResolutionContext } -> TypeEnvironment(bindings = bindings, constructors = ConstructorInferenceDefinition(name = name, scheme = scheme, fieldNames = fieldNames) :: constructors, typeResolutionContext = typeResolutionContext)

let addInferenceTypeDefinition symbolId name arity environment =
    match environment with
        | TypeEnvironment { bindings = bindings, constructors = constructors, typeResolutionContext = typeResolutionContext } -> TypeEnvironment(bindings = bindings, constructors = constructors, typeResolutionContext = addTypeDefinition(symbolId)(name)(arity)(typeResolutionContext))

let addInferenceTypeAlias name parameterIds target environment =
    match environment with
        | TypeEnvironment { bindings = bindings, constructors = constructors, typeResolutionContext = typeResolutionContext } -> TypeEnvironment(bindings = bindings, constructors = constructors, typeResolutionContext = addTypeAliasDefinition(name)(parameterIds)(target)(typeResolutionContext))

let recursive findTypeBinding name bindings =
    match bindings with
        | [] -> None
        | (candidateName, scheme) :: tail ->
            if name == candidateName
            then Some(scheme)
            else findTypeBinding(name)(tail)

let resolveTypeBinding name environment =
    match environment with
        | TypeEnvironment { bindings = bindings, constructors = _constructors, typeResolutionContext = _typeResolutionContext } -> findTypeBinding(name)(bindings)

let recursive findConstructorBinding : Str -> List(ConstructorInferenceDefinition) -> Maybe(ConstructorInferenceDefinition) =
    given (name) ->
        given (constructors) ->
            match constructors with
                | [] -> None
                | ConstructorInferenceDefinition { name = candidateName, scheme = scheme, fieldNames = fieldNames } :: tail ->
                    if name == candidateName
                    then Some(ConstructorInferenceDefinition(name = candidateName, scheme = scheme, fieldNames = fieldNames))
                    else findConstructorBinding(name)(tail)

let resolveConstructorBinding name environment =
    match environment with
        | TypeEnvironment { bindings = _bindings, constructors = constructors, typeResolutionContext = _typeResolutionContext } -> findConstructorBinding(name)(constructors)

let inferenceTypeResolutionContext environment =
    match environment with
        | TypeEnvironment { bindings = _bindings, constructors = _constructors, typeResolutionContext = typeResolutionContext } -> typeResolutionContext

let inferenceSuccess semanticType substitution supply = TypeInferenceResult(semanticType = semanticType, substitution = substitution, supply = supply, constraints = [], error = None)

let inferenceFailure semanticType substitution supply error = TypeInferenceResult(semanticType = semanticType, substitution = substitution, supply = supply, constraints = [], error = Some(error))

let recursive appendConstraints left right =
    match left with
        | [] -> right
        | head :: tail -> head :: appendConstraints(tail)(right)

let addConstraints additional result =
    match result with
        | TypeInferenceResult { semanticType = semanticType, substitution = substitution, supply = supply, constraints = constraints, error = error } -> TypeInferenceResult(semanticType = semanticType, substitution = substitution, supply = supply, constraints = appendConstraints(additional)(constraints), error = error)

let recursive applyInferenceConstraints substitution constraints =
    match constraints with
        | [] -> []
        | TraitConstraint { traitName = traitName, typeArguments = typeArguments } :: tail ->
            let recursive applyArguments arguments =
                match arguments with
                    | [] -> []
                    | head :: rest ->
                        let resolvedHead = applySubstitution(substitution)(head)
                        in
                            let resolvedRest = applyArguments(rest)
                            in resolvedHead :: resolvedRest
            in
                let resolvedArguments = applyArguments(typeArguments)
                in
                    let resolvedTail = applyInferenceConstraints(substitution)(tail)
                    in TraitConstraint(traitName = traitName, typeArguments = resolvedArguments) :: resolvedTail

let recursive appendSubstitution left right =
    match left with
        | [] -> right
        | head :: tail -> head :: appendSubstitution(tail)(right)

let patternSuccess semanticType environment substitution supply names = PatternInferenceResult(semanticType = semanticType, environment = environment, substitution = substitution, supply = supply, names = names, error = None)

let patternFailure semanticType environment substitution supply names error = PatternInferenceResult(semanticType = semanticType, environment = environment, substitution = substitution, supply = supply, names = names, error = Some(error))

let mergePatternUnification currentSubstitution result supply fallbackType environment names =
    match result with
        | UnificationResult { substitution = unificationSubstitution, error = None } ->
            let combined = appendSubstitution(unificationSubstitution)(currentSubstitution)
            in patternSuccess(applySubstitution(combined)(fallbackType))(environment)(combined)(supply)(names)
        | UnificationResult { substitution = _unificationSubstitution, error = Some(error) } -> patternFailure(fallbackType)(environment)(currentSubstitution)(supply)(names)(InferenceUnificationError(error))

let recursive patternNameExists : Str -> List(Str) -> Bool =
    given (name) ->
        given (names) ->
            match names with
                | [] -> false
                | head :: tail ->
                    if name == head
                    then true
                    else patternNameExists(name)(tail)

let recursive splitConstructorType semanticType reversedParameters =
    match semanticType with
        | SemFunction(parameter, result, None) -> splitConstructorType(result)(parameter :: reversedParameters)
        | _ -> ConstructorTypeShape(parameters = reverse(reversedParameters), resultType = semanticType)

let recursive findRecordFieldType : Str -> List(Str) -> List(SemanticType) -> Maybe(SemanticType) =
    given (fieldName) ->
        given (fieldNames) ->
            given (fieldTypes) ->
                match (fieldNames, fieldTypes) with
                    | (candidateName :: nameTail, fieldType :: typeTail) ->
                        if fieldName == candidateName
                        then Some(fieldType)
                        else findRecordFieldType(fieldName)(nameTail)(typeTail)
                    | _ -> None

let recursive allPatternNamesPresent : List(Str) -> List(Str) -> Bool =
    given (names) ->
        given (candidates) ->
            match names with
                | [] -> true
                | head :: tail ->
                    if patternNameExists(head)(candidates)
                    then allPatternNamesPresent(tail)(candidates)
                    else false

let samePatternNameSets left right =
    if allPatternNamesPresent(left)(right)
    then allPatternNamesPresent(right)(left)
    else false

let mergeUnification currentSubstitution result supply fallbackType =
    match result with
        | UnificationResult { substitution = unificationSubstitution, error = None } ->
            let combined = appendSubstitution(unificationSubstitution)(currentSubstitution)
            in inferenceSuccess(applySubstitution(combined)(fallbackType))(combined)(supply)
        | UnificationResult { substitution = _unificationSubstitution, error = Some(error) } -> inferenceFailure(fallbackType)(currentSubstitution)(supply)(InferenceUnificationError(error))

let checkInferenceAnnotation annotation expectedType environment substitution supply =
    match prepareTypeResolutionContext(annotation)(inferenceTypeResolutionContext(environment))(supply) with
        | TypeResolutionPreparationResult { context = preparedContext, supply = preparedSupply } ->
            match resolveTypeExpression(annotation)(preparedContext) with
                | TypeResolutionResult { semanticType = annotationType, error = None } -> mergeUnification(substitution)(unify(applySubstitution(substitution)(expectedType))(annotationType))(preparedSupply)(expectedType)
                | TypeResolutionResult { semanticType = _annotationType, error = Some(error) } -> inferenceFailure(expectedType)(substitution)(preparedSupply)(InferenceTypeResolutionError(error))

let inferenceEnvironmentSchemes environment =
    match environment with
        | TypeEnvironment { bindings = bindings, constructors = _constructors, typeResolutionContext = _typeResolutionContext } ->
            let recursive schemes values =
                match values with
                    | [] -> []
                    | (_name, scheme) :: tail -> scheme :: schemes(tail)
            in schemes(bindings)

let recursive inferExpressions expressions environment substitution supply reversedTypes =
    match expressions with
        | [] -> inferenceSuccess(SemTuple(reversedTypes))(substitution)(supply)
        | head :: tail ->
            match inferWith(head)(environment)(substitution)(supply) with
                | TypeInferenceResult { semanticType = inferredType, substitution = nextSubstitution, supply = nextSupply, constraints = inferredConstraints, error = None } -> addConstraints(inferredConstraints)(inferExpressions(tail)(environment)(nextSubstitution)(nextSupply)(inferredType :: reversedTypes))
                | failure -> failure
and inferListElements expressions elementType environment substitution supply =
    match expressions with
        | [] -> inferenceSuccess(SemList(applySubstitution(substitution)(elementType)))(substitution)(supply)
        | head :: tail ->
            match inferWith(head)(environment)(substitution)(supply) with
                | TypeInferenceResult { semanticType = inferredType, substitution = nextSubstitution, supply = nextSupply, constraints = inferredConstraints, error = None } ->
                    let unification = unify(applySubstitution(nextSubstitution)(elementType))(applySubstitution(nextSubstitution)(inferredType))
                    in
                        match mergeUnification(nextSubstitution)(unification)(nextSupply)(elementType) with
                            | TypeInferenceResult { semanticType = unifiedElement, substitution = unifiedSubstitution, supply = unifiedSupply, constraints = _unificationConstraints, error = None } -> addConstraints(inferredConstraints)(inferListElements(tail)(unifiedElement)(environment)(unifiedSubstitution)(unifiedSupply))
                            | failure -> failure
                | failure -> failure
and inferPatternList patterns environment substitution supply names reversedTypes =
    match patterns with
        | [] -> patternSuccess(SemTuple(reversedTypes))(environment)(substitution)(supply)(names)
        | head :: tail ->
            match inferPattern(head)(environment)(substitution)(supply)(names) with
                | PatternInferenceResult { semanticType = headType, environment = headEnvironment, substitution = headSubstitution, supply = headSupply, names = headNames, error = None } -> inferPatternList(tail)(headEnvironment)(headSubstitution)(headSupply)(headNames)(headType :: reversedTypes)
                | failure -> failure
and inferConstructorPatternArguments constructorName patterns parameterTypes resultType environment substitution supply names =
    match (patterns, parameterTypes) with
        | ([], []) -> patternSuccess(applySubstitution(substitution)(resultType))(environment)(substitution)(supply)(names)
        | (pattern :: patternTail, parameterType :: parameterTail) ->
            match inferPattern(pattern)(environment)(substitution)(supply)(names) with
                | PatternInferenceResult { semanticType = patternType, environment = patternEnvironment, substitution = patternSubstitution, supply = patternSupply, names = patternNames, error = None } ->
                    match mergePatternUnification(patternSubstitution)(unify(applySubstitution(patternSubstitution)(parameterType))(applySubstitution(patternSubstitution)(patternType)))(patternSupply)(parameterType)(patternEnvironment)(patternNames) with
                        | PatternInferenceResult { semanticType = _unifiedType, environment = unifiedEnvironment, substitution = unifiedSubstitution, supply = unifiedSupply, names = unifiedNames, error = None } -> inferConstructorPatternArguments(constructorName)(patternTail)(parameterTail)(resultType)(unifiedEnvironment)(unifiedSubstitution)(unifiedSupply)(unifiedNames)
                        | failure -> failure
                | failure -> failure
        | _ -> patternFailure(SemNever)(environment)(substitution)(supply)(names)(ConstructorPatternArityMismatch(constructorName))
and inferRecordPatternFields constructorName fields fieldNames fieldTypes resultType environment substitution supply names seenFields =
    match fields with
        | [] -> patternSuccess(applySubstitution(substitution)(resultType))(environment)(substitution)(supply)(names)
        | (fieldName, fieldPattern) :: tail ->
            if patternNameExists(fieldName)(seenFields)
            then patternFailure(SemNever)(environment)(substitution)(supply)(names)(DuplicateRecordPatternField(fieldName))
            else
                match findRecordFieldType(fieldName)(fieldNames)(fieldTypes) with
                    | None -> patternFailure(SemNever)(environment)(substitution)(supply)(names)(UnknownRecordPatternField(constructorName)(fieldName))
                    | Some(fieldType) ->
                        match inferPattern(fieldPattern)(environment)(substitution)(supply)(names) with
                            | PatternInferenceResult { semanticType = patternType, environment = patternEnvironment, substitution = patternSubstitution, supply = patternSupply, names = patternNames, error = None } ->
                                match mergePatternUnification(patternSubstitution)(unify(applySubstitution(patternSubstitution)(fieldType))(applySubstitution(patternSubstitution)(patternType)))(patternSupply)(fieldType)(patternEnvironment)(patternNames) with
                                    | PatternInferenceResult { semanticType = _unifiedType, environment = unifiedEnvironment, substitution = unifiedSubstitution, supply = unifiedSupply, names = unifiedNames, error = None } -> inferRecordPatternFields(constructorName)(tail)(fieldNames)(fieldTypes)(resultType)(unifiedEnvironment)(unifiedSubstitution)(unifiedSupply)(unifiedNames)(fieldName :: seenFields)
                                    | failure -> failure
                            | failure -> failure
and unifyOrPatternBindings names expectedEnvironment actualEnvironment substitution supply resultType =
    match names with
        | [] -> patternSuccess(applySubstitution(substitution)(resultType))(expectedEnvironment)(substitution)(supply)([])
        | name :: tail ->
            match (resolveTypeBinding(name)(expectedEnvironment), resolveTypeBinding(name)(actualEnvironment)) with
                | (Some(TypeScheme { quantified = _expectedQuantified, body = expectedType, constraints = _expectedConstraints }), Some(TypeScheme { quantified = _actualQuantified, body = actualType, constraints = _actualConstraints })) ->
                    match mergePatternUnification(substitution)(unify(applySubstitution(substitution)(expectedType))(applySubstitution(substitution)(actualType)))(supply)(resultType)(expectedEnvironment)(names) with
                        | PatternInferenceResult { semanticType = _unifiedType, environment = _unifiedEnvironment, substitution = unifiedSubstitution, supply = unifiedSupply, names = _unifiedNames, error = None } -> unifyOrPatternBindings(tail)(expectedEnvironment)(actualEnvironment)(unifiedSubstitution)(unifiedSupply)(resultType)
                        | failure -> failure
                | _ -> patternFailure(SemNever)(expectedEnvironment)(substitution)(supply)(names)(InconsistentOrPatternBindings)
and inferOrPatternAlternatives alternatives baseEnvironment expectedEnvironment expectedNames expectedType substitution supply outerNames =
    match alternatives with
        | [] ->
            match (expectedEnvironment, expectedType) with
                | (Some(environment), Some(resultType)) -> patternSuccess(applySubstitution(substitution)(resultType))(environment)(substitution)(supply)(expectedNames)
                | _ -> patternFailure(SemNever)(baseEnvironment)(substitution)(supply)(outerNames)(UnsupportedInferencePattern("or-pattern must contain an alternative"))
        | alternative :: tail ->
            match inferPattern(alternative)(baseEnvironment)(substitution)(supply)(outerNames) with
                | PatternInferenceResult { semanticType = alternativeType, environment = alternativeEnvironment, substitution = alternativeSubstitution, supply = alternativeSupply, names = alternativeNames, error = None } ->
                    match (expectedEnvironment, expectedType) with
                        | (None, None) -> inferOrPatternAlternatives(tail)(baseEnvironment)(Some(alternativeEnvironment))(alternativeNames)(Some(alternativeType))(alternativeSubstitution)(alternativeSupply)(outerNames)
                        | (Some(firstEnvironment), Some(firstType)) ->
                            if samePatternNameSets(expectedNames)(alternativeNames)
                            then
                                match mergePatternUnification(alternativeSubstitution)(unify(applySubstitution(alternativeSubstitution)(firstType))(applySubstitution(alternativeSubstitution)(alternativeType)))(alternativeSupply)(firstType)(alternativeEnvironment)(alternativeNames) with
                                    | PatternInferenceResult { semanticType = unifiedType, environment = _unifiedEnvironment, substitution = unifiedSubstitution, supply = unifiedSupply, names = _unifiedNames, error = None } ->
                                        match unifyOrPatternBindings(expectedNames)(firstEnvironment)(alternativeEnvironment)(unifiedSubstitution)(unifiedSupply)(unifiedType) with
                                            | PatternInferenceResult { semanticType = bindingType, environment = _bindingEnvironment, substitution = bindingSubstitution, supply = bindingSupply, names = _bindingNames, error = None } -> inferOrPatternAlternatives(tail)(baseEnvironment)(Some(firstEnvironment))(expectedNames)(Some(bindingType))(bindingSubstitution)(bindingSupply)(outerNames)
                                            | failure -> failure
                                    | failure -> failure
                            else patternFailure(SemNever)(firstEnvironment)(alternativeSubstitution)(alternativeSupply)(expectedNames)(InconsistentOrPatternBindings)
                        | _ -> patternFailure(SemNever)(baseEnvironment)(alternativeSubstitution)(alternativeSupply)(outerNames)(InconsistentOrPatternBindings)
                | failure -> failure
and inferPattern pattern environment substitution supply names =
    match pattern with
        | PatternAt(_span, inner) -> inferPattern(inner)(environment)(substitution)(supply)(names)
        | PatternEmptyList ->
            match freshTypeVariable(supply) with
                | (elementType, nextSupply) -> patternSuccess(SemList(elementType))(environment)(substitution)(nextSupply)(names)
        | PatternVar(name) ->
            if patternNameExists(name)(names)
            then patternFailure(SemNever)(environment)(substitution)(supply)(names)(DuplicatePatternBinding(name))
            else
                match freshTypeVariable(supply) with
                    | (variableType, nextSupply) ->
                        let scheme = TypeScheme(quantified = [], body = variableType, constraints = [])
                        in patternSuccess(variableType)(addTypeBinding(name)(scheme)(environment))(substitution)(nextSupply)(name :: names)
        | PatternWildcard ->
            match freshTypeVariable(supply) with
                | (wildcardType, nextSupply) -> patternSuccess(wildcardType)(environment)(substitution)(nextSupply)(names)
        | PatternCons(head, tail) ->
            match inferPattern(head)(environment)(substitution)(supply)(names) with
                | PatternInferenceResult { semanticType = headType, environment = headEnvironment, substitution = headSubstitution, supply = headSupply, names = headNames, error = None } ->
                    match inferPattern(tail)(headEnvironment)(headSubstitution)(headSupply)(headNames) with
                        | PatternInferenceResult { semanticType = tailType, environment = tailEnvironment, substitution = tailSubstitution, supply = tailSupply, names = tailNames, error = None } ->
                            let listType = SemList(applySubstitution(tailSubstitution)(headType))
                            in mergePatternUnification(tailSubstitution)(unify(applySubstitution(tailSubstitution)(tailType))(listType))(tailSupply)(listType)(tailEnvironment)(tailNames)
                        | failure -> failure
                | failure -> failure
        | PatternTuple(elements) ->
            match inferPatternList(elements)(environment)(substitution)(supply)(names)([]) with
                | PatternInferenceResult { semanticType = SemTuple(reversedTypes), environment = tupleEnvironment, substitution = tupleSubstitution, supply = tupleSupply, names = tupleNames, error = None } -> patternSuccess(SemTuple(reverse(reversedTypes)))(tupleEnvironment)(tupleSubstitution)(tupleSupply)(tupleNames)
                | failure -> failure
        | PatternAs(inner, name) ->
            match inferPattern(inner)(environment)(substitution)(supply)(names) with
                | PatternInferenceResult { semanticType = innerType, environment = innerEnvironment, substitution = innerSubstitution, supply = innerSupply, names = innerNames, error = None } ->
                    if patternNameExists(name)(innerNames)
                    then patternFailure(innerType)(innerEnvironment)(innerSubstitution)(innerSupply)(innerNames)(DuplicatePatternBinding(name))
                    else
                        let scheme = TypeScheme(quantified = [], body = innerType, constraints = [])
                        in patternSuccess(innerType)(addTypeBinding(name)(scheme)(innerEnvironment))(innerSubstitution)(innerSupply)(name :: innerNames)
                | failure -> failure
        | PatternConstructor(name, patterns) ->
            match resolveConstructorBinding(name)(environment) with
                | None -> patternFailure(SemNever)(environment)(substitution)(supply)(names)(UnknownPatternConstructor(name))
                | Some(ConstructorInferenceDefinition { name = _constructorName, scheme = scheme, fieldNames = _fieldNames }) ->
                    match instantiate(scheme)(supply) with
                        | InstantiationResult { semanticType = constructorType, constraints = _constraints, supply = constructorSupply } ->
                            match splitConstructorType(constructorType)([]) with
                                | ConstructorTypeShape { parameters = parameterTypes, resultType = resultType } -> inferConstructorPatternArguments(name)(patterns)(parameterTypes)(resultType)(environment)(substitution)(constructorSupply)(names)
        | PatternRecord(name, fields) ->
            match resolveConstructorBinding(name)(environment) with
                | None -> patternFailure(SemNever)(environment)(substitution)(supply)(names)(UnknownPatternConstructor(name))
                | Some(ConstructorInferenceDefinition { name = _constructorName, scheme = scheme, fieldNames = fieldNames }) ->
                    match instantiate(scheme)(supply) with
                        | InstantiationResult { semanticType = constructorType, constraints = _constraints, supply = constructorSupply } ->
                            match splitConstructorType(constructorType)([]) with
                                | ConstructorTypeShape { parameters = fieldTypes, resultType = resultType } -> inferRecordPatternFields(name)(fields)(fieldNames)(fieldTypes)(resultType)(environment)(substitution)(constructorSupply)(names)([])
        | PatternOr(alternatives) -> inferOrPatternAlternatives(alternatives)(environment)(None)([])(None)(substitution)(supply)(names)
        | PatternInt(_) -> patternSuccess(SemInt)(environment)(substitution)(supply)(names)
        | PatternString(_) -> patternSuccess(SemString)(environment)(substitution)(supply)(names)
        | PatternRune(_) -> patternSuccess(SemRune)(environment)(substitution)(supply)(names)
        | PatternBool(_) -> patternSuccess(SemBool)(environment)(substitution)(supply)(names)
        | _ -> patternFailure(SemNever)(environment)(substitution)(supply)(names)(UnsupportedInferencePattern("pattern case is not implemented yet"))
and inferMatchCases cases scrutineeType resultType environment substitution supply accumulatedConstraints =
    match cases with
        | [] -> addConstraints(accumulatedConstraints)(inferenceSuccess(applySubstitution(substitution)(resultType))(substitution)(supply))
        | (pattern, body, guard) :: tail ->
            match inferPattern(pattern)(environment)(substitution)(supply)([]) with
                | PatternInferenceResult { semanticType = patternType, environment = patternEnvironment, substitution = patternSubstitution, supply = patternSupply, names = _patternNames, error = None } ->
                    match mergePatternUnification(patternSubstitution)(unify(applySubstitution(patternSubstitution)(scrutineeType))(applySubstitution(patternSubstitution)(patternType)))(patternSupply)(scrutineeType)(patternEnvironment)([]) with
                        | PatternInferenceResult { semanticType = _matchedType, environment = matchedEnvironment, substitution = matchedSubstitution, supply = matchedSupply, names = _matchedNames, error = None } -> inferMatchGuard(guard)(body)(tail)(scrutineeType)(resultType)(environment)(matchedEnvironment)(matchedSubstitution)(matchedSupply)(accumulatedConstraints)
                        | PatternInferenceResult { semanticType = failedType, environment = _failedEnvironment, substitution = failedSubstitution, supply = failedSupply, names = _failedNames, error = Some(error) } -> inferenceFailure(failedType)(failedSubstitution)(failedSupply)(error)
                | PatternInferenceResult { semanticType = failedType, environment = _failedEnvironment, substitution = failedSubstitution, supply = failedSupply, names = _failedNames, error = Some(error) } -> inferenceFailure(failedType)(failedSubstitution)(failedSupply)(error)
and inferMatchGuard guard body tail scrutineeType resultType environment patternEnvironment substitution supply accumulatedConstraints =
    match guard with
        | None -> inferMatchBody(body)(tail)(scrutineeType)(resultType)(environment)(patternEnvironment)(substitution)(supply)(accumulatedConstraints)
        | Some(guardExpression) ->
            match inferWith(guardExpression)(patternEnvironment)(substitution)(supply) with
                | TypeInferenceResult { semanticType = guardType, substitution = guardSubstitution, supply = guardSupply, constraints = guardConstraints, error = None } ->
                    match mergeUnification(guardSubstitution)(unify(applySubstitution(guardSubstitution)(guardType))(SemBool))(guardSupply)(SemBool) with
                        | TypeInferenceResult { semanticType = _booleanType, substitution = booleanSubstitution, supply = booleanSupply, constraints = _unificationConstraints, error = None } -> inferMatchBody(body)(tail)(scrutineeType)(resultType)(environment)(patternEnvironment)(booleanSubstitution)(booleanSupply)(appendConstraints(accumulatedConstraints)(guardConstraints))
                        | failure -> failure
                | failure -> failure
and inferMatchBody body tail scrutineeType resultType environment patternEnvironment substitution supply accumulatedConstraints =
    match inferWith(body)(patternEnvironment)(substitution)(supply) with
        | TypeInferenceResult { semanticType = bodyType, substitution = bodySubstitution, supply = bodySupply, constraints = bodyConstraints, error = None } ->
            match mergeUnification(bodySubstitution)(unify(applySubstitution(bodySubstitution)(resultType))(applySubstitution(bodySubstitution)(bodyType)))(bodySupply)(resultType) with
                | TypeInferenceResult { semanticType = _unifiedResult, substitution = resultSubstitution, supply = resultSupply, constraints = _unificationConstraints, error = None } -> inferMatchCases(tail)(scrutineeType)(resultType)(environment)(resultSubstitution)(resultSupply)(appendConstraints(accumulatedConstraints)(bodyConstraints))
                | failure -> failure
        | failure -> failure
and inferBinaryTrait traitName returnsBool left right environment substitution supply =
    match inferWith(left)(environment)(substitution)(supply) with
        | TypeInferenceResult { semanticType = leftType, substitution = leftSubstitution, supply = leftSupply, constraints = leftConstraints, error = None } ->
            match inferWith(right)(environment)(leftSubstitution)(leftSupply) with
                | TypeInferenceResult { semanticType = rightType, substitution = rightSubstitution, supply = rightSupply, constraints = rightConstraints, error = None } ->
                    let unification = unify(applySubstitution(rightSubstitution)(leftType))(applySubstitution(rightSubstitution)(rightType))
                    in
                        match mergeUnification(rightSubstitution)(unification)(rightSupply)(leftType) with
                            | TypeInferenceResult { semanticType = unifiedType, substitution = unifiedSubstitution, supply = unifiedSupply, constraints = _unificationConstraints, error = None } ->
                                let resultType =
                                    if returnsBool
                                    then SemBool
                                    else unifiedType
                                in
                                    let constraint = TraitConstraint(traitName = traitName, typeArguments = [applySubstitution(unifiedSubstitution)(unifiedType)])
                                    in addConstraints(appendConstraints(leftConstraints)(appendConstraints(rightConstraints)([constraint])))(inferenceSuccess(resultType)(unifiedSubstitution)(unifiedSupply))
                            | failure -> failure
                | failure -> failure
        | failure -> failure
and inferUnaryTrait traitName operand environment substitution supply =
    match inferWith(operand)(environment)(substitution)(supply) with
        | TypeInferenceResult { semanticType = operandType, substitution = operandSubstitution, supply = operandSupply, constraints = operandConstraints, error = None } ->
            let resolvedOperand = applySubstitution(operandSubstitution)(operandType)
            in
                let constraint = TraitConstraint(traitName = traitName, typeArguments = [resolvedOperand])
                in addConstraints(appendConstraints(operandConstraints)([constraint]))(inferenceSuccess(resolvedOperand)(operandSubstitution)(operandSupply))
        | failure -> failure
and inferWith expression environment substitution supply =
    match expression with
        | ExprAt(_span, inner) -> inferWith(inner)(environment)(substitution)(supply)
        | ExprInt(_) -> inferenceSuccess(SemInt)(substitution)(supply)
        | ExprBigInt(_) -> inferenceSuccess(SemBigInt)(substitution)(supply)
        | ExprUInt(_value, bits, _text) -> inferenceSuccess(SemUInt(bits))(substitution)(supply)
        | ExprFloat(_value, _text) -> inferenceSuccess(SemFloat)(substitution)(supply)
        | ExprString(_) -> inferenceSuccess(SemString)(substitution)(supply)
        | ExprRune(_) -> inferenceSuccess(SemRune)(substitution)(supply)
        | ExprBool(_) -> inferenceSuccess(SemBool)(substitution)(supply)
        | ExprVar(name) ->
            match resolveTypeBinding(name)(environment) with
                | None -> inferenceFailure(SemNever)(substitution)(supply)(UnknownValue(name))
                | Some(scheme) ->
                    match instantiate(scheme)(supply) with
                        | InstantiationResult { semanticType = instantiatedType, constraints = instantiatedConstraints, supply = nextSupply } -> addConstraints(instantiatedConstraints)(inferenceSuccess(applySubstitution(substitution)(instantiatedType))(substitution)(nextSupply))
        | ExprQualifiedVar(moduleName, name) -> inferWith(ExprVar(moduleName + "." + name))(environment)(substitution)(supply)
        | ExprLambda(name, body, annotation) ->
            match freshTypeVariable(supply) with
                | (parameterType, afterParameter) ->
                    let annotationResult =
                        match annotation with
                            | None -> inferenceSuccess(parameterType)(substitution)(afterParameter)
                            | Some(typeExpression) -> checkInferenceAnnotation(typeExpression)(parameterType)(environment)(substitution)(afterParameter)
                    in
                        match annotationResult with
                            | TypeInferenceResult { semanticType = checkedParameter, substitution = annotationSubstitution, supply = annotationSupply, constraints = _annotationConstraints, error = None } ->
                                let parameterScheme = TypeScheme(quantified = [], body = checkedParameter, constraints = [])
                                in
                                    let bodyEnvironment = addTypeBinding(name)(parameterScheme)(environment)
                                    in
                                        match inferWith(body)(bodyEnvironment)(annotationSubstitution)(annotationSupply) with
                                            | TypeInferenceResult { semanticType = bodyType, substitution = bodySubstitution, supply = bodySupply, constraints = bodyConstraints, error = None } ->
                                                let resolvedParameter = applySubstitution(bodySubstitution)(checkedParameter)
                                                in
                                                    let resolvedBody = applySubstitution(bodySubstitution)(bodyType)
                                                    in addConstraints(bodyConstraints)(inferenceSuccess(SemFunction(resolvedParameter)(resolvedBody)(None))(bodySubstitution)(bodySupply))
                                            | failure -> failure
                            | failure -> failure
        | ExprCall(function, argument, _whitespace) ->
            match inferWith(function)(environment)(substitution)(supply) with
                | TypeInferenceResult { semanticType = functionType, substitution = functionSubstitution, supply = functionSupply, constraints = functionConstraints, error = None } ->
                    match inferWith(argument)(environment)(functionSubstitution)(functionSupply) with
                        | TypeInferenceResult { semanticType = argumentType, substitution = argumentSubstitution, supply = argumentSupply, constraints = argumentConstraints, error = None } ->
                            match freshTypeVariable(argumentSupply) with
                                | (resultType, resultSupply) ->
                                    let expectedFunction = SemFunction(argumentType)(resultType)(None)
                                    in
                                        let unification = unify(applySubstitution(argumentSubstitution)(functionType))(expectedFunction)
                                        in addConstraints(appendConstraints(functionConstraints)(argumentConstraints))(mergeUnification(argumentSubstitution)(unification)(resultSupply)(resultType))
                        | failure -> failure
                | failure -> failure
        | ExprLet(name, value, body, _parameters, annotation, _requirements) ->
            match inferWith(value)(environment)(substitution)(supply) with
                | TypeInferenceResult { semanticType = valueType, substitution = valueSubstitution, supply = valueSupply, constraints = valueConstraints, error = None } ->
                    let annotationResult =
                        match annotation with
                            | None -> inferenceSuccess(valueType)(valueSubstitution)(valueSupply)
                            | Some(typeExpression) -> checkInferenceAnnotation(typeExpression)(valueType)(environment)(valueSubstitution)(valueSupply)
                    in
                        match annotationResult with
                            | TypeInferenceResult { semanticType = checkedValue, substitution = checkedSubstitution, supply = checkedSupply, constraints = _annotationConstraints, error = None } ->
                                let resolvedValue = applySubstitution(checkedSubstitution)(checkedValue)
                                in
                                    let resolvedConstraints = applyInferenceConstraints(checkedSubstitution)(valueConstraints)
                                    in
                                        let scheme = generalize(inferenceEnvironmentSchemes(environment))(resolvedValue)(resolvedConstraints)
                                        in
                                            let bodyEnvironment = addTypeBinding(name)(scheme)(environment)
                                            in inferWith(body)(bodyEnvironment)(checkedSubstitution)(checkedSupply)
                            | failure -> failure
                | failure -> failure
        | ExprLetRecursive(name, value, body, _parameters, annotation, _requirements) ->
            match freshTypeVariable(supply) with
                | (recursiveType, afterRecursive) ->
                    let recursiveScheme = TypeScheme(quantified = [], body = recursiveType, constraints = [])
                    in
                        let recursiveEnvironment = addTypeBinding(name)(recursiveScheme)(environment)
                        in
                            match inferWith(value)(recursiveEnvironment)(substitution)(afterRecursive) with
                                | TypeInferenceResult { semanticType = valueType, substitution = valueSubstitution, supply = valueSupply, constraints = valueConstraints, error = None } ->
                                    let unification = unify(applySubstitution(valueSubstitution)(recursiveType))(applySubstitution(valueSubstitution)(valueType))
                                    in
                                        match mergeUnification(valueSubstitution)(unification)(valueSupply)(recursiveType) with
                                            | TypeInferenceResult { semanticType = unifiedType, substitution = unifiedSubstitution, supply = unifiedSupply, constraints = _unificationConstraints, error = None } ->
                                                let annotationResult =
                                                    match annotation with
                                                        | None -> inferenceSuccess(unifiedType)(unifiedSubstitution)(unifiedSupply)
                                                        | Some(typeExpression) -> checkInferenceAnnotation(typeExpression)(unifiedType)(environment)(unifiedSubstitution)(unifiedSupply)
                                                in
                                                    match annotationResult with
                                                        | TypeInferenceResult { semanticType = checkedType, substitution = checkedSubstitution, supply = checkedSupply, constraints = _annotationConstraints, error = None } ->
                                                            let resolvedType = applySubstitution(checkedSubstitution)(checkedType)
                                                            in
                                                                let resolvedConstraints = applyInferenceConstraints(checkedSubstitution)(valueConstraints)
                                                                in
                                                                    let scheme = generalize(inferenceEnvironmentSchemes(environment))(resolvedType)(resolvedConstraints)
                                                                    in inferWith(body)(addTypeBinding(name)(scheme)(environment))(checkedSubstitution)(checkedSupply)
                                                        | failure -> failure
                                            | failure -> failure
                                | failure -> failure
        | ExprIf(condition, thenBranch, elseBranch) ->
            match inferWith(condition)(environment)(substitution)(supply) with
                | TypeInferenceResult { semanticType = conditionType, substitution = conditionSubstitution, supply = conditionSupply, constraints = conditionConstraints, error = None } ->
                    let conditionUnification = unify(applySubstitution(conditionSubstitution)(conditionType))(SemBool)
                    in
                        match mergeUnification(conditionSubstitution)(conditionUnification)(conditionSupply)(SemBool) with
                            | TypeInferenceResult { semanticType = _booleanType, substitution = booleanSubstitution, supply = booleanSupply, constraints = _unificationConstraints, error = None } ->
                                match inferWith(thenBranch)(environment)(booleanSubstitution)(booleanSupply) with
                                    | TypeInferenceResult { semanticType = thenType, substitution = thenSubstitution, supply = thenSupply, constraints = thenConstraints, error = None } ->
                                        match inferWith(elseBranch)(environment)(thenSubstitution)(thenSupply) with
                                            | TypeInferenceResult { semanticType = elseType, substitution = elseSubstitution, supply = elseSupply, constraints = elseConstraints, error = None } ->
                                                let branchUnification = unify(applySubstitution(elseSubstitution)(thenType))(applySubstitution(elseSubstitution)(elseType))
                                                in addConstraints(appendConstraints(conditionConstraints)(appendConstraints(thenConstraints)(elseConstraints)))(mergeUnification(elseSubstitution)(branchUnification)(elseSupply)(thenType))
                                            | failure -> failure
                                    | failure -> failure
                            | failure -> failure
                | failure -> failure
        | ExprTuple(elements) ->
            match inferExpressions(elements)(environment)(substitution)(supply)([]) with
                | TypeInferenceResult { semanticType = SemTuple(reversedTypes), substitution = tupleSubstitution, supply = tupleSupply, constraints = tupleConstraints, error = None } -> addConstraints(tupleConstraints)(inferenceSuccess(SemTuple(reverse(reversedTypes)))(tupleSubstitution)(tupleSupply))
                | failure -> failure
        | ExprList(elements) ->
            match freshTypeVariable(supply) with
                | (elementType, nextSupply) -> inferListElements(elements)(elementType)(environment)(substitution)(nextSupply)
        | ExprCons(head, tail) ->
            match inferWith(head)(environment)(substitution)(supply) with
                | TypeInferenceResult { semanticType = headType, substitution = headSubstitution, supply = headSupply, constraints = headConstraints, error = None } ->
                    match inferWith(tail)(environment)(headSubstitution)(headSupply) with
                        | TypeInferenceResult { semanticType = tailType, substitution = tailSubstitution, supply = tailSupply, constraints = tailConstraints, error = None } ->
                            let unification = unify(applySubstitution(tailSubstitution)(tailType))(SemList(applySubstitution(tailSubstitution)(headType)))
                            in addConstraints(appendConstraints(headConstraints)(tailConstraints))(mergeUnification(tailSubstitution)(unification)(tailSupply)(tailType))
                        | failure -> failure
                | failure -> failure
        | ExprMatch(scrutinee, cases, _position) ->
            match inferWith(scrutinee)(environment)(substitution)(supply) with
                | TypeInferenceResult { semanticType = scrutineeType, substitution = scrutineeSubstitution, supply = scrutineeSupply, constraints = scrutineeConstraints, error = None } ->
                    match freshTypeVariable(scrutineeSupply) with
                        | (resultType, resultSupply) -> inferMatchCases(cases)(scrutineeType)(resultType)(environment)(scrutineeSubstitution)(resultSupply)(scrutineeConstraints)
                | failure -> failure
        | ExprAdd(left, right) -> inferBinaryTrait("Add")(false)(left)(right)(environment)(substitution)(supply)
        | ExprSubtract(left, right) -> inferBinaryTrait("Subtract")(false)(left)(right)(environment)(substitution)(supply)
        | ExprMultiply(left, right) -> inferBinaryTrait("Multiply")(false)(left)(right)(environment)(substitution)(supply)
        | ExprDivide(left, right) -> inferBinaryTrait("Divide")(false)(left)(right)(environment)(substitution)(supply)
        | ExprModulo(left, right) -> inferBinaryTrait("Remainder")(false)(left)(right)(environment)(substitution)(supply)
        | ExprBitwiseAnd(left, right) -> inferBinaryTrait("BitAnd")(false)(left)(right)(environment)(substitution)(supply)
        | ExprBitwiseOr(left, right) -> inferBinaryTrait("BitOr")(false)(left)(right)(environment)(substitution)(supply)
        | ExprBitwiseXor(left, right) -> inferBinaryTrait("BitXor")(false)(left)(right)(environment)(substitution)(supply)
        | ExprShiftLeft(left, right) -> inferBinaryTrait("ShiftLeft")(false)(left)(right)(environment)(substitution)(supply)
        | ExprShiftRight(left, right) -> inferBinaryTrait("ShiftRight")(false)(left)(right)(environment)(substitution)(supply)
        | ExprEqual(left, right) -> inferBinaryTrait("Eq")(true)(left)(right)(environment)(substitution)(supply)
        | ExprNotEqual(left, right) -> inferBinaryTrait("Eq")(true)(left)(right)(environment)(substitution)(supply)
        | ExprLessThan(left, right) -> inferBinaryTrait("Ord")(true)(left)(right)(environment)(substitution)(supply)
        | ExprLessOrEqual(left, right) -> inferBinaryTrait("Ord")(true)(left)(right)(environment)(substitution)(supply)
        | ExprGreaterThan(left, right) -> inferBinaryTrait("Ord")(true)(left)(right)(environment)(substitution)(supply)
        | ExprGreaterOrEqual(left, right) -> inferBinaryTrait("Ord")(true)(left)(right)(environment)(substitution)(supply)
        | ExprBitwiseNot(operand) -> inferUnaryTrait("BitwiseNot")(operand)(environment)(substitution)(supply)
        | ExprLogicalNot(operand) -> inferUnaryTrait("Not")(operand)(environment)(substitution)(supply)
        | _ -> inferenceFailure(SemNever)(substitution)(supply)(UnsupportedInferenceExpression("expression case is not implemented yet"))

let inferExpressionFrom expression environment substitution supply = inferWith(expression)(environment)(substitution)(supply)

let inferTopLevelBinding name value annotation environment substitution supply =
    match inferWith(value)(environment)(substitution)(supply) with
        | TypeInferenceResult { semanticType = valueType, substitution = valueSubstitution, supply = valueSupply, constraints = valueConstraints, error = None } ->
            let annotationResult =
                match annotation with
                    | None -> inferenceSuccess(valueType)(valueSubstitution)(valueSupply)
                    | Some(typeExpression) -> checkInferenceAnnotation(typeExpression)(valueType)(environment)(valueSubstitution)(valueSupply)
            in
                match annotationResult with
                    | TypeInferenceResult { semanticType = checkedValue, substitution = checkedSubstitution, supply = checkedSupply, constraints = _annotationConstraints, error = None } ->
                        let resolvedValue = applySubstitution(checkedSubstitution)(checkedValue)
                        in
                            let resolvedConstraints = applyInferenceConstraints(checkedSubstitution)(valueConstraints)
                            in
                                let scheme = generalize(inferenceEnvironmentSchemes(environment))(resolvedValue)(resolvedConstraints)
                                in TopLevelBindingInferenceResult(environment = addTypeBinding(name)(scheme)(environment), semanticType = resolvedValue, substitution = checkedSubstitution, supply = checkedSupply, error = None)
                    | TypeInferenceResult { semanticType = failedType, substitution = failedSubstitution, supply = failedSupply, constraints = _failedConstraints, error = Some(error) } -> TopLevelBindingInferenceResult(environment = environment, semanticType = failedType, substitution = failedSubstitution, supply = failedSupply, error = Some(error))
        | TypeInferenceResult { semanticType = failedType, substitution = failedSubstitution, supply = failedSupply, constraints = _failedConstraints, error = Some(error) } -> TopLevelBindingInferenceResult(environment = environment, semanticType = failedType, substitution = failedSubstitution, supply = failedSupply, error = Some(error))

let inferExpression expression environment = inferExpressionFrom(expression)(environment)([])(initialTypeVariableSupply(Unit))
