// A recursive binding is monomorphic within its own definition. Every free self
// reference therefore receives the same ordered dictionary parameters, before
// call-shape/TCO analysis. Apply this once to the source value, before wrapping
// dictionary lambdas; the Core self binding must subsequently use its physical ABI.
import AshesCompiler.Frontend.Syntax
import Ashes.Collection.List.append
export (
    value rewriteRecursiveTraitEvidence,
)

let recursive containsName (name: Str) (names: List(Str)) =
    match names with
        | [] -> false
        | candidate :: rest -> candidate == name || containsName(name)(rest)

let recursive patternNames pattern =
    match pattern with
        | PatternAt(_span, inner) -> patternNames(inner)
        | PatternVar(name) -> Some([name])
        | PatternCons(head, tail) -> patternListNames([head, tail])
        | PatternTuple(elements) -> patternListNames(elements)
        | PatternConstructor(_constructor, arguments) -> patternListNames(arguments)
        | PatternRecord(_constructor, fields) -> patternFieldNames(fields)
        | PatternAs(inner, name) ->
            match patternNames(inner) with
                | Some(names) -> Some(name :: names)
                | None -> None
        | PatternOr(alternatives) -> patternListNames(alternatives)
        | PatternEmptyList -> Some([])
        | PatternWildcard -> Some([])
        | PatternInt(_value) -> Some([])
        | PatternString(_value) -> Some([])
        | PatternRune(_value) -> Some([])
        | PatternBool(_value) -> Some([])
        | _ -> None
and patternListNames patterns =
    match patterns with
        | [] -> Some([])
        | pattern :: rest ->
            match (patternNames(pattern), patternListNames(rest)) with
                | (Some(head), Some(tail)) ->
                    tail
                    |> append(head)
                    |> Some
                | _ -> None
and patternFieldNames fields =
    match fields with
        | [] -> Some([])
        | (_field, pattern) :: rest ->
            match (patternNames(pattern), patternFieldNames(rest)) with
                | (Some(head), Some(tail)) ->
                    tail
                    |> append(head)
                    |> Some
                | _ -> None

let recursive namesShadowEvidence evidence names =
    match names with
        | [] -> false
        | name :: rest -> containsName(name)(evidence) || namesShadowEvidence(evidence)(rest)

let recursive applyEvidence reference evidence =
    match evidence with
        | [] -> reference
        | name :: rest ->
            applyEvidence(ExprCall(reference)(ExprVar(name))(false)(callArgumentsInline))(rest)

let recursive rewriteExpressions self evidence blocked expressions =
    match expressions with
        | [] -> Some([])
        | expression :: rest ->
            match (rewrite(self)(evidence)(blocked)(expression), rewriteExpressions(self)(evidence)(blocked)(rest)) with
                | (Some(head), Some(tail)) -> Some(head :: tail)
                | _ -> None
and rewriteFields self evidence blocked fields =
    match fields with
        | [] -> Some([])
        | (name, expression) :: rest ->
            match (rewrite(self)(evidence)(blocked)(expression), rewriteFields(self)(evidence)(blocked)(rest)) with
                | (Some(value), Some(tail)) -> Some((name, value) :: tail)
                | _ -> None
and rewriteOptional self evidence blocked expression =
    match expression with
        | None -> Some(None)
        | Some(value) ->
            match rewrite(self)(evidence)(blocked)(value) with
                | Some(rewritten) -> Some(Some(rewritten))
                | None -> None
and rewriteCases self evidence blocked cases =
    match cases with
        | [] -> Some([])
        | (pattern, body, guard) :: rest ->
            let rewritten =
                match patternNames(pattern) with
                    | None -> None
                    | Some(names) ->
                        if containsName(self)(names)
                        then Some((body, guard))
                        else
                            let shadowed = blocked || namesShadowEvidence(evidence)(names)
                            in
                                match (rewrite(self)(evidence)(shadowed)(body), rewriteOptional(self)(evidence)(shadowed)(guard)) with
                                    | (Some(nextBody), Some(nextGuard)) -> Some((nextBody, nextGuard))
                                    | _ -> None
            in
                match (rewritten, rewriteCases(self)(evidence)(blocked)(rest)) with
                    | (Some((nextBody, nextGuard)), Some(tail)) -> Some((pattern, nextBody, nextGuard) :: tail)
                    | _ -> None
and rewriteArms self evidence blocked arms =
    match arms with
        | [] -> Some([])
        | (instance, operation, patterns, body) :: rest ->
            let rewritten =
                match patternListNames(patterns) with
                    | None -> None
                    | Some(names) ->
                        if containsName(self)(names)
                        then Some(body)
                        else rewrite(self)(evidence)(blocked || namesShadowEvidence(evidence)(names))(body)
            in
                match (rewritten, rewriteArms(self)(evidence)(blocked)(rest)) with
                    | (Some(nextBody), Some(tail)) -> Some((instance, operation, patterns, nextBody) :: tail)
                    | _ -> None
and rewriteBinary self evidence blocked constructor left right =
    match (rewrite(self)(evidence)(blocked)(left), rewrite(self)(evidence)(blocked)(right)) with
        | (Some(nextLeft), Some(nextRight)) ->
            nextRight
            |> constructor(nextLeft)
            |> Some
        | _ -> None
and rewriteUnary self evidence blocked constructor operand =
    match rewrite(self)(evidence)(blocked)(operand) with
        | Some(next) ->
            next
            |> constructor
            |> Some
        | None -> None
and rewriteLet self evidence blocked isRecursive name value body parameters annotation requirements =
    (let nextValue =
        if isRecursive && name == self || containsName(self)(parameters)
        then Some(value)
        else rewrite(self)(evidence)(blocked || namesShadowEvidence(evidence)(parameters) || isRecursive && containsName(name)(evidence))(value)
    in
        let nextBody =
            if name == self
            then Some(body)
            else rewrite(self)(evidence)(blocked || containsName(name)(evidence))(body)
        in
            match (nextValue, nextBody) with
                | (Some(rewrittenValue), Some(rewrittenBody)) ->
                    Some(if isRecursive
                    then ExprLetRecursive(name)(rewrittenValue)(rewrittenBody)(parameters)(annotation)(requirements)
                    else ExprLet(name)(rewrittenValue)(rewrittenBody)(parameters)(annotation)(requirements))
                | _ -> None)
and rewrite self evidence blocked expression =
    match expression with
        | ExprVar(name) ->
            if name == self
            then
                if blocked
                then None
                else
                    evidence
                    |> applyEvidence(expression)
                    |> Some
            else Some(expression)
        | ExprAt(span, inner) ->
            rewriteUnary(self)(evidence)(blocked)(given (next) -> ExprAt(span)(next))(inner)
        | ExprInt(_value) -> Some(expression)
        | ExprBigInt(_value) -> Some(expression)
        | ExprUInt(_value, _bits, _raw) -> Some(expression)
        | ExprFloat(_value, _raw) -> Some(expression)
        | ExprString(_value) -> Some(expression)
        | ExprRune(_value) -> Some(expression)
        | ExprBool(_value) -> Some(expression)
        | ExprQualifiedVar(_module, _name) -> Some(expression)
        | ExprAdd(left, right) -> rewriteBinary(self)(evidence)(blocked)(ExprAdd)(left)(right)
        | ExprSubtract(left, right) -> rewriteBinary(self)(evidence)(blocked)(ExprSubtract)(left)(right)
        | ExprMultiply(left, right) -> rewriteBinary(self)(evidence)(blocked)(ExprMultiply)(left)(right)
        | ExprDivide(left, right) -> rewriteBinary(self)(evidence)(blocked)(ExprDivide)(left)(right)
        | ExprModulo(left, right) -> rewriteBinary(self)(evidence)(blocked)(ExprModulo)(left)(right)
        | ExprBitwiseAnd(left, right) -> rewriteBinary(self)(evidence)(blocked)(ExprBitwiseAnd)(left)(right)
        | ExprBitwiseOr(left, right) -> rewriteBinary(self)(evidence)(blocked)(ExprBitwiseOr)(left)(right)
        | ExprBitwiseXor(left, right) -> rewriteBinary(self)(evidence)(blocked)(ExprBitwiseXor)(left)(right)
        | ExprShiftLeft(left, right) -> rewriteBinary(self)(evidence)(blocked)(ExprShiftLeft)(left)(right)
        | ExprShiftRight(left, right) -> rewriteBinary(self)(evidence)(blocked)(ExprShiftRight)(left)(right)
        | ExprBitwiseNot(operand) -> rewriteUnary(self)(evidence)(blocked)(ExprBitwiseNot)(operand)
        | ExprLogicalNot(operand) -> rewriteUnary(self)(evidence)(blocked)(ExprLogicalNot)(operand)
        | ExprLogicalAnd(left, right) -> rewriteBinary(self)(evidence)(blocked)(ExprLogicalAnd)(left)(right)
        | ExprLogicalOr(left, right) -> rewriteBinary(self)(evidence)(blocked)(ExprLogicalOr)(left)(right)
        | ExprGreaterThan(left, right) -> rewriteBinary(self)(evidence)(blocked)(ExprGreaterThan)(left)(right)
        | ExprLessThan(left, right) -> rewriteBinary(self)(evidence)(blocked)(ExprLessThan)(left)(right)
        | ExprGreaterOrEqual(left, right) -> rewriteBinary(self)(evidence)(blocked)(ExprGreaterOrEqual)(left)(right)
        | ExprLessOrEqual(left, right) -> rewriteBinary(self)(evidence)(blocked)(ExprLessOrEqual)(left)(right)
        | ExprEqual(left, right) -> rewriteBinary(self)(evidence)(blocked)(ExprEqual)(left)(right)
        | ExprNotEqual(left, right) -> rewriteBinary(self)(evidence)(blocked)(ExprNotEqual)(left)(right)
        | ExprResultPipe(left, right) -> rewriteBinary(self)(evidence)(blocked)(ExprResultPipe)(left)(right)
        | ExprResultMapErrorPipe(left, right) -> rewriteBinary(self)(evidence)(blocked)(ExprResultMapErrorPipe)(left)(right)
        | ExprCons(left, right) -> rewriteBinary(self)(evidence)(blocked)(ExprCons)(left)(right)
        | ExprLet(name, value, body, parameters, annotation, requirements) -> rewriteLet(self)(evidence)(blocked)(false)(name)(value)(body)(parameters)(annotation)(requirements)
        | ExprLetRecursive(name, value, body, parameters, annotation, requirements) -> rewriteLet(self)(evidence)(blocked)(true)(name)(value)(body)(parameters)(annotation)(requirements)
        | ExprLetResult(name, value, body) ->
            match (rewrite(self)(evidence)(blocked)(value), if name == self
            then Some(body)
            else rewrite(self)(evidence)(blocked || containsName(name)(evidence))(body)) with
                | (Some(nextValue), Some(nextBody)) ->
                    nextBody
                    |> ExprLetResult(name)(nextValue)
                    |> Some
                | _ -> None
        | ExprLambda(name, body, annotation) ->
            if name == self
            then Some(expression)
            else
                rewriteUnary(self)(evidence)(blocked || containsName(name)(evidence))(given (next) -> ExprLambda(name)(next)(annotation))(body)
        | ExprCall(function, argument, sugar, layout) ->
            rewriteBinary(self)(evidence)(blocked)(given (nextFunction) ->
                given (nextArgument) -> ExprCall(nextFunction)(nextArgument)(sugar)(layout))(function)(argument)
        | ExprIf(condition, yes, no) ->
            match (rewrite(self)(evidence)(blocked)(condition), rewrite(self)(evidence)(blocked)(yes), rewrite(self)(evidence)(blocked)(no)) with
                | (Some(nextCondition), Some(nextYes), Some(nextNo)) ->
                    nextNo
                    |> ExprIf(nextCondition)(nextYes)
                    |> Some
                | _ -> None
        | ExprTuple(elements) ->
            match rewriteExpressions(self)(evidence)(blocked)(elements) with
                | Some(next) -> Some(ExprTuple(next))
                | None -> None
        | ExprList(elements, trailing) ->
            match rewriteExpressions(self)(evidence)(blocked)(elements) with
                | Some(next) ->
                    trailing
                    |> ExprList(next)
                    |> Some
                | None -> None
        | ExprMatch(scrutinee, cases, matchId) ->
            match (rewrite(self)(evidence)(blocked)(scrutinee), rewriteCases(self)(evidence)(blocked)(cases)) with
                | (Some(nextScrutinee), Some(nextCases)) ->
                    matchId
                    |> ExprMatch(nextScrutinee)(nextCases)
                    |> Some
                | _ -> None
        | ExprAwait(operand) -> rewriteUnary(self)(evidence)(blocked)(ExprAwait)(operand)
        | ExprPerform(operand) -> rewriteUnary(self)(evidence)(blocked)(ExprPerform)(operand)
        | ExprRecord(name, fields, trailing) ->
            match rewriteFields(self)(evidence)(blocked)(fields) with
                | Some(next) ->
                    trailing
                    |> ExprRecord(name)(next)
                    |> Some
                | None -> None
        | ExprRecordUpdate(record, fields) ->
            match (rewrite(self)(evidence)(blocked)(record), rewriteFields(self)(evidence)(blocked)(fields)) with
                | (Some(nextRecord), Some(nextFields)) ->
                    nextFields
                    |> ExprRecordUpdate(nextRecord)
                    |> Some
                | _ -> None
        | ExprHandle(body, arms) ->
            match (rewrite(self)(evidence)(blocked)(body), rewriteArms(self)(evidence)(blocked)(arms)) with
                | (Some(nextBody), Some(nextArms)) ->
                    nextArms
                    |> ExprHandle(nextBody)
                    |> Some
                | _ -> None
        | _ -> None

let rewriteRecursiveTraitEvidence self evidence expression =
    match evidence with
        | [] -> Some(expression)
        | _ ->
            if containsName(self)(evidence)
            then None
            else rewrite(self)(evidence)(false)(expression)
