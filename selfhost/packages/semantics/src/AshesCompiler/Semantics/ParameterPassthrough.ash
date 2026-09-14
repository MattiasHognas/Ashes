// Decides whether a plain function body returns its parameter itself on some terminal arms and a
// freshly built value on every other arm, stage 0's `ReturnsParameterOrFreshValue`: such a
// function's result is reference-counted on every path once the arm returning the parameter
// copies it into an owned graph.
//
// Invariants:
// - The walk is syntactic over the parsed tree: a `let` chain is what its body is, an `if` is
//   what both its branches are, and a `match` is what every arm is unless an arm rebinds the
//   parameter; any other expression is a terminal arm.
// - A terminal arm qualifies as the bare parameter or as a value the caller's `isFresh` judges
//   freshly built; a `let` that rebinds the parameter disqualifies the body.
// - At least one arm must return the parameter, otherwise the body is an ordinary producer.

import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.Pattern
import AshesCompiler.Semantics.ResultReach.patternBindsName
export (
    value returnsParameterOrFreshValue,
)

let maximumDepth = 32

let combineArms (left: (Bool, Bool)) (right: (Bool, Bool)) =
    match (left, right) with
        | ((leftQualifies, leftReturns), (rightQualifies, rightReturns)) -> (leftQualifies && rightQualifies, leftReturns || rightReturns)

// Whether every terminal arm is the parameter or a fresh value, and whether any arm is the
// parameter.
let recursive terminalArmsAt (isFresh: Expr -> Bool) (parameter: Str) (depth: Int) (expression: Expr) =
    if depth > maximumDepth
    then (false, false)
    else
        match expression with
            | ExprAt(_span, inner) -> terminalArmsAt(isFresh)(parameter)(depth)(inner)
            | ExprLet(name, _value, body, _parameters, _annotation, _requirements) ->
                if name == parameter
                then (false, false)
                else terminalArmsAt(isFresh)(parameter)(depth + 1)(body)
            | ExprVar(name) -> (name == parameter, name == parameter)
            | ExprIf(_condition, thenBranch, elseBranch) ->
                elseBranch
                |> terminalArmsAt(isFresh)(parameter)(depth + 1)
                |> combineArms(terminalArmsAt(isFresh)(parameter)(depth + 1)(thenBranch))
            | ExprMatch(_scrutinee, [], _defaultArm) -> (false, false)
            | ExprMatch(_scrutinee, arms, _defaultArm) -> everyArmAt(isFresh)(parameter)(depth + 1)(arms)
            | other -> (isFresh(other), false)
and everyArmAt (isFresh: Expr -> Bool) (parameter: Str) (depth: Int) (arms: List((Pattern, Expr, Maybe(Expr)))) =
    match arms with
        | [] -> (true, false)
        | (pattern, body, _guard) :: rest ->
            if patternBindsName(pattern)(parameter)
            then (false, false)
            else
                rest
                |> everyArmAt(isFresh)(parameter)(depth)
                |> combineArms(terminalArmsAt(isFresh)(parameter)(depth)(body))

let returnsParameterOrFreshValue (isFresh: Expr -> Bool) (parameter: Str) (body: Expr) =
    match terminalArmsAt(isFresh)(parameter)(0)(body) with
        | (qualifies, returnsParameter) -> qualifies && returnsParameter
