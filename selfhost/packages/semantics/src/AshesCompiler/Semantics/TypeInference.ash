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

let inferenceSuccess semanticType substitution supply = TypeInferenceResult(semanticType = semanticType, substitution = substitution, supply = supply, error = None)

let inferenceFailure semanticType substitution supply error = TypeInferenceResult(semanticType = semanticType, substitution = substitution, supply = supply, error = Some(error))

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
                | TypeInferenceResult { semanticType = inferredType, substitution = nextSubstitution, supply = nextSupply, error = None } -> inferExpressions(tail)(environment)(nextSubstitution)(nextSupply)(inferredType :: reversedTypes)
                | failure -> failure
and inferListElements expressions elementType environment substitution supply =
    match expressions with
        | [] -> inferenceSuccess(SemList(applySubstitution(substitution)(elementType)))(substitution)(supply)
        | head :: tail ->
            match inferWith(head)(environment)(substitution)(supply) with
                | TypeInferenceResult { semanticType = inferredType, substitution = nextSubstitution, supply = nextSupply, error = None } ->
                    let unification = unify(applySubstitution(nextSubstitution)(elementType))(applySubstitution(nextSubstitution)(inferredType))
                    in
                        match mergeUnification(nextSubstitution)(unification)(nextSupply)(elementType) with
                            | TypeInferenceResult { semanticType = unifiedElement, substitution = unifiedSubstitution, supply = unifiedSupply, error = None } -> inferListElements(tail)(unifiedElement)(environment)(unifiedSubstitution)(unifiedSupply)
                            | failure -> failure
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
                        | InstantiationResult { semanticType = instantiatedType, constraints = _constraints, supply = nextSupply } -> inferenceSuccess(applySubstitution(substitution)(instantiatedType))(substitution)(nextSupply)
        | ExprQualifiedVar(moduleName, name) -> inferWith(ExprVar(moduleName + "." + name))(environment)(substitution)(supply)
        | ExprLambda(name, body, _annotation) ->
            match freshTypeVariable(supply) with
                | (parameterType, afterParameter) ->
                    let parameterScheme = TypeScheme(quantified = [], body = parameterType, constraints = [])
                    in
                        let bodyEnvironment = addTypeBinding(name)(parameterScheme)(environment)
                        in
                            match inferWith(body)(bodyEnvironment)(substitution)(afterParameter) with
                                | TypeInferenceResult { semanticType = bodyType, substitution = bodySubstitution, supply = bodySupply, error = None } ->
                                    let resolvedParameter = applySubstitution(bodySubstitution)(parameterType)
                                    in
                                        let resolvedBody = applySubstitution(bodySubstitution)(bodyType)
                                        in inferenceSuccess(SemFunction(resolvedParameter)(resolvedBody)(None))(bodySubstitution)(bodySupply)
                                | failure -> failure
        | ExprCall(function, argument, _whitespace) ->
            match inferWith(function)(environment)(substitution)(supply) with
                | TypeInferenceResult { semanticType = functionType, substitution = functionSubstitution, supply = functionSupply, error = None } ->
                    match inferWith(argument)(environment)(functionSubstitution)(functionSupply) with
                        | TypeInferenceResult { semanticType = argumentType, substitution = argumentSubstitution, supply = argumentSupply, error = None } ->
                            match freshTypeVariable(argumentSupply) with
                                | (resultType, resultSupply) ->
                                    let expectedFunction = SemFunction(argumentType)(resultType)(None)
                                    in
                                        let unification = unify(applySubstitution(argumentSubstitution)(functionType))(expectedFunction)
                                        in mergeUnification(argumentSubstitution)(unification)(resultSupply)(resultType)
                        | failure -> failure
                | failure -> failure
        | ExprLet(name, value, body, _parameters, _annotation, _requirements) ->
            match inferWith(value)(environment)(substitution)(supply) with
                | TypeInferenceResult { semanticType = valueType, substitution = valueSubstitution, supply = valueSupply, error = None } ->
                    let resolvedValue = applySubstitution(valueSubstitution)(valueType)
                    in
                        let scheme = generalize(environmentSchemes(environment))(resolvedValue)([])
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
                                | TypeInferenceResult { semanticType = valueType, substitution = valueSubstitution, supply = valueSupply, error = None } ->
                                    let unification = unify(applySubstitution(valueSubstitution)(recursiveType))(applySubstitution(valueSubstitution)(valueType))
                                    in
                                        match mergeUnification(valueSubstitution)(unification)(valueSupply)(recursiveType) with
                                            | TypeInferenceResult { semanticType = unifiedType, substitution = unifiedSubstitution, supply = unifiedSupply, error = None } ->
                                                let scheme = generalize(environmentSchemes(environment))(unifiedType)([])
                                                in inferWith(body)(addTypeBinding(name)(scheme)(environment))(unifiedSubstitution)(unifiedSupply)
                                            | failure -> failure
                                | failure -> failure
        | ExprIf(condition, thenBranch, elseBranch) ->
            match inferWith(condition)(environment)(substitution)(supply) with
                | TypeInferenceResult { semanticType = conditionType, substitution = conditionSubstitution, supply = conditionSupply, error = None } ->
                    let conditionUnification = unify(applySubstitution(conditionSubstitution)(conditionType))(SemBool)
                    in
                        match mergeUnification(conditionSubstitution)(conditionUnification)(conditionSupply)(SemBool) with
                            | TypeInferenceResult { semanticType = _booleanType, substitution = booleanSubstitution, supply = booleanSupply, error = None } ->
                                match inferWith(thenBranch)(environment)(booleanSubstitution)(booleanSupply) with
                                    | TypeInferenceResult { semanticType = thenType, substitution = thenSubstitution, supply = thenSupply, error = None } ->
                                        match inferWith(elseBranch)(environment)(thenSubstitution)(thenSupply) with
                                            | TypeInferenceResult { semanticType = elseType, substitution = elseSubstitution, supply = elseSupply, error = None } ->
                                                let branchUnification = unify(applySubstitution(elseSubstitution)(thenType))(applySubstitution(elseSubstitution)(elseType))
                                                in mergeUnification(elseSubstitution)(branchUnification)(elseSupply)(thenType)
                                            | failure -> failure
                                    | failure -> failure
                            | failure -> failure
                | failure -> failure
        | ExprTuple(elements) ->
            match inferExpressions(elements)(environment)(substitution)(supply)([]) with
                | TypeInferenceResult { semanticType = SemTuple(reversedTypes), substitution = tupleSubstitution, supply = tupleSupply, error = None } -> inferenceSuccess(SemTuple(reverse(reversedTypes)))(tupleSubstitution)(tupleSupply)
                | failure -> failure
        | ExprList(elements) ->
            match freshTypeVariable(supply) with
                | (elementType, nextSupply) -> inferListElements(elements)(elementType)(environment)(substitution)(nextSupply)
        | ExprCons(head, tail) ->
            match inferWith(head)(environment)(substitution)(supply) with
                | TypeInferenceResult { semanticType = headType, substitution = headSubstitution, supply = headSupply, error = None } ->
                    match inferWith(tail)(environment)(headSubstitution)(headSupply) with
                        | TypeInferenceResult { semanticType = tailType, substitution = tailSubstitution, supply = tailSupply, error = None } ->
                            let unification = unify(applySubstitution(tailSubstitution)(tailType))(SemList(applySubstitution(tailSubstitution)(headType)))
                            in mergeUnification(tailSubstitution)(unification)(tailSupply)(tailType)
                        | failure -> failure
                | failure -> failure
        | _ -> inferenceFailure(SemNever)(substitution)(supply)(UnsupportedInferenceExpression("expression case is not implemented yet"))

let inferExpression expression environment = inferWith(expression)(environment)([])(initialTypeVariableSupply(Unit))
