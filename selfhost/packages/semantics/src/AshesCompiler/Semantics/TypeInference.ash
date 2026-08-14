import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.Unification
import AshesCompiler.Semantics.TypeSchemes
import Ashes.Collection.List.reverse
export (
    type TypeEnvironment(..),
    type TypeInferenceError(..),
    type TypeInferenceResult(..),
    value emptyTypeEnvironment,
    value addTypeBinding,
    value inferExpression,
)

type TypeEnvironment =
    | bindings: List((Str, TypeScheme))

type TypeInferenceError =
    | UnknownValue(Str)
    | InferenceUnificationError(UnificationError)
    | UnsupportedInferenceExpression(Str)
    deriving {Eq, Show}

type TypeInferenceResult =
    | semanticType: SemanticType
    | substitution: List((Int, SemanticType))
    | supply: TypeVariableSupply
    | constraints: List(TraitConstraint)
    | error: Maybe(TypeInferenceError)
    deriving {Eq, Show}

let emptyTypeEnvironment unit = TypeEnvironment(bindings = [])

let addTypeBinding name scheme environment =
    match environment with
        | TypeEnvironment { bindings = bindings } -> TypeEnvironment(bindings = (name, scheme) :: bindings)

let recursive findTypeBinding name bindings =
    match bindings with
        | [] -> None
        | (candidateName, scheme) :: tail ->
            if name == candidateName
            then Some(scheme)
            else findTypeBinding(name)(tail)

let resolveTypeBinding name environment =
    match environment with
        | TypeEnvironment { bindings = bindings } -> findTypeBinding(name)(bindings)

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

let mergeUnification currentSubstitution result supply fallbackType =
    match result with
        | UnificationResult { substitution = unificationSubstitution, error = None } ->
            let combined = appendSubstitution(unificationSubstitution)(currentSubstitution)
            in inferenceSuccess(applySubstitution(combined)(fallbackType))(combined)(supply)
        | UnificationResult { substitution = _unificationSubstitution, error = Some(error) } -> inferenceFailure(fallbackType)(currentSubstitution)(supply)(InferenceUnificationError(error))

let environmentSchemes environment =
    match environment with
        | TypeEnvironment { bindings = bindings } ->
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
        | ExprLambda(name, body, _annotation) ->
            match freshTypeVariable(supply) with
                | (parameterType, afterParameter) ->
                    let parameterScheme = TypeScheme(quantified = [], body = parameterType, constraints = [])
                    in
                        let bodyEnvironment = addTypeBinding(name)(parameterScheme)(environment)
                        in
                            match inferWith(body)(bodyEnvironment)(substitution)(afterParameter) with
                                | TypeInferenceResult { semanticType = bodyType, substitution = bodySubstitution, supply = bodySupply, constraints = bodyConstraints, error = None } ->
                                    let resolvedParameter = applySubstitution(bodySubstitution)(parameterType)
                                    in
                                        let resolvedBody = applySubstitution(bodySubstitution)(bodyType)
                                        in addConstraints(bodyConstraints)(inferenceSuccess(SemFunction(resolvedParameter)(resolvedBody)(None))(bodySubstitution)(bodySupply))
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
        | ExprLet(name, value, body, _parameters, _annotation, _requirements) ->
            match inferWith(value)(environment)(substitution)(supply) with
                | TypeInferenceResult { semanticType = valueType, substitution = valueSubstitution, supply = valueSupply, constraints = valueConstraints, error = None } ->
                    let resolvedValue = applySubstitution(valueSubstitution)(valueType)
                    in
                        let resolvedConstraints = applyInferenceConstraints(valueSubstitution)(valueConstraints)
                        in
                            let scheme = generalize(environmentSchemes(environment))(resolvedValue)(resolvedConstraints)
                            in
                                let bodyEnvironment = addTypeBinding(name)(scheme)(environment)
                                in inferWith(body)(bodyEnvironment)(valueSubstitution)(valueSupply)
                | failure -> failure
        | ExprLetRecursive(name, value, body, _parameters, _annotation, _requirements) ->
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
                                                let resolvedConstraints = applyInferenceConstraints(unifiedSubstitution)(valueConstraints)
                                                in
                                                    let scheme = generalize(environmentSchemes(environment))(unifiedType)(resolvedConstraints)
                                                    in inferWith(body)(addTypeBinding(name)(scheme)(environment))(unifiedSubstitution)(unifiedSupply)
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

let inferExpression expression environment = inferWith(expression)(environment)([])(initialTypeVariableSupply(Unit))
