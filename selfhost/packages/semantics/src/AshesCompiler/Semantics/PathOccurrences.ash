// How many times a name can be evaluated on one path through an expression, stage 0's
// `MaxPathOccurrences`: the move analysis's linearity test and the reuse specialization's
// path-rebuild test both read it.
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.ResultReach.patternBindsName
export (
    value occurrenceEscape,
    value maxPathOccurrences,
)

// Stage 0's `MaxPathOccurrences`: how many times a name can be evaluated on one path through an
// expression. Branches take their worst arm, sequenced parts add up, a capturing lambda or a
// node the walk does not model counts as unbounded, and a binding of the same name shadows it.
let occurrenceEscape = 1048576

let largerOf (left: Int) (right: Int) =
    if left > right
    then left
    else right

let recursive maxPathOccurrences (name: Str) (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> maxPathOccurrences(name)(inner)
        | ExprVar(candidate) ->
            if candidate == name
            then 1
            else 0
        | ExprQualifiedVar(_module, _member) -> 0
        | ExprInt(_value) -> 0
        | ExprBigInt(_value) -> 0
        | ExprUInt(_value, _width, _text) -> 0
        | ExprFloat(_value, _text) -> 0
        | ExprString(_value) -> 0
        | ExprRune(_value) -> 0
        | ExprBool(_value) -> 0
        | ExprIf(cond, thenBranch, elseBranch) ->
            maxPathOccurrences(name)(cond) + largerOf(maxPathOccurrences(name)(thenBranch))(maxPathOccurrences(name)(elseBranch))
        | ExprMatch(scrutinee, arms, _defaultArm) -> maxPathOccurrences(name)(scrutinee) + maxPathOccurrencesArms(name)(arms)(0)
        | ExprLet(bound, value, body, _params, _annotation, _traits) ->
            maxPathOccurrences(name)(value) + (if bound == name
            then 0
            else maxPathOccurrences(name)(body))
        | ExprLetResult(bound, value, body) ->
            maxPathOccurrences(name)(value) + (if bound == name
            then 0
            else maxPathOccurrences(name)(body))
        | ExprLetRecursive(bound, value, body, _params, _annotation, _traits) ->
            if bound == name
            then 0
            else maxPathOccurrences(name)(value) + maxPathOccurrences(name)(body)
        | ExprLambda(parameter, body, _annotation) ->
            if parameter == name
            then 0
            else
                if maxPathOccurrences(name)(body) > 0
                then occurrenceEscape
                else 0
        | ExprCall(function, argument, _isSugar, _layout) -> maxPathOccurrences(name)(function) + maxPathOccurrences(name)(argument)
        | ExprAdd(left, right) -> maxPathOccurrencesPair(name)(left)(right)
        | ExprSubtract(left, right) -> maxPathOccurrencesPair(name)(left)(right)
        | ExprMultiply(left, right) -> maxPathOccurrencesPair(name)(left)(right)
        | ExprDivide(left, right) -> maxPathOccurrencesPair(name)(left)(right)
        | ExprModulo(left, right) -> maxPathOccurrencesPair(name)(left)(right)
        | ExprBitwiseAnd(left, right) -> maxPathOccurrencesPair(name)(left)(right)
        | ExprBitwiseOr(left, right) -> maxPathOccurrencesPair(name)(left)(right)
        | ExprBitwiseXor(left, right) -> maxPathOccurrencesPair(name)(left)(right)
        | ExprShiftLeft(left, right) -> maxPathOccurrencesPair(name)(left)(right)
        | ExprShiftRight(left, right) -> maxPathOccurrencesPair(name)(left)(right)
        | ExprBitwiseNot(operand) -> maxPathOccurrences(name)(operand)
        | ExprLogicalNot(operand) -> maxPathOccurrences(name)(operand)
        | ExprLogicalAnd(left, right) -> maxPathOccurrencesPair(name)(left)(right)
        | ExprLogicalOr(left, right) -> maxPathOccurrencesPair(name)(left)(right)
        | ExprGreaterThan(left, right) -> maxPathOccurrencesPair(name)(left)(right)
        | ExprLessThan(left, right) -> maxPathOccurrencesPair(name)(left)(right)
        | ExprGreaterOrEqual(left, right) -> maxPathOccurrencesPair(name)(left)(right)
        | ExprLessOrEqual(left, right) -> maxPathOccurrencesPair(name)(left)(right)
        | ExprEqual(left, right) -> maxPathOccurrencesPair(name)(left)(right)
        | ExprNotEqual(left, right) -> maxPathOccurrencesPair(name)(left)(right)
        | ExprResultPipe(left, right) -> maxPathOccurrencesPair(name)(left)(right)
        | ExprResultMapErrorPipe(left, right) -> maxPathOccurrencesPair(name)(left)(right)
        | ExprCons(head, tail) -> maxPathOccurrencesPair(name)(head)(tail)
        | ExprAwait(task) -> maxPathOccurrences(name)(task)
        | ExprTuple(elements) -> maxPathOccurrencesList(name)(elements)(0)
        | ExprList(elements, _isMultiline) -> maxPathOccurrencesList(name)(elements)(0)
        | ExprRecord(_constructor, fields, _isMultiline) -> maxPathOccurrencesFields(name)(fields)(0)
        | ExprRecordUpdate(target, fields) -> maxPathOccurrences(name)(target) + maxPathOccurrencesFields(name)(fields)(0)
        | _ -> occurrenceEscape
and maxPathOccurrencesPair (name: Str) (left: Expr) (right: Expr) = maxPathOccurrences(name)(left) + maxPathOccurrences(name)(right)
and maxPathOccurrencesArms (name: Str) (arms: List((Pattern, Expr, Maybe(Expr)))) (worst: Int) =
    match arms with
        | [] -> worst
        | (pattern, body, guard) :: rest ->
            let arm =
                if patternBindsName(pattern)(name)
                then 0
                else
                    match guard with
                        | Some(guardExpression) -> maxPathOccurrences(name)(body) + maxPathOccurrences(name)(guardExpression)
                        | None -> maxPathOccurrences(name)(body)
            in
                arm
                |> largerOf(worst)
                |> maxPathOccurrencesArms(name)(rest)
and maxPathOccurrencesList (name: Str) (elements: List(Expr)) (total: Int) =
    match elements with
        | [] -> total
        | element :: rest -> maxPathOccurrencesList(name)(rest)(total + maxPathOccurrences(name)(element))
and maxPathOccurrencesFields (name: Str) (fields: List((Str, Expr))) (total: Int) =
    match fields with
        | [] -> total
        | (_field, value) :: rest -> maxPathOccurrencesFields(name)(rest)(total + maxPathOccurrences(name)(value))
