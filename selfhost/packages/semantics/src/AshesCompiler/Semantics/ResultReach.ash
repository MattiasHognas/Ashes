// Decides whether a function body's result always reaches one of its variables: the value the
// function returns carries the variable itself, or an aggregate built around it, on every path.
// A function whose parameter always reaches its result keeps the argument past the call, so the
// lowering normalizes that argument into an owned value at entry.
//
// Invariants:
// - The walk is syntactic over the parsed tree: only the constructions and control flow that
//   forward the variable to every result count; a `let`, a field read, or an operator answers false.
// - A `match` reaches only when every arm does and no arm rebinds the variable; a lambda reaches
//   through its body unless its parameter shadows the variable.
// - A saturated call to a known let-bound function is followed into the callee body through each
//   argument position the variable reaches, at most 32 call levels deep; a constructor
//   application reaches through any argument.

import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.Pattern
export (
    value patternBindsName,
    value resultAlwaysReachesVariable,
)

let maximumCallDepth = 32

let recursive sameLength (left: List(Expr)) (right: List(Str)) =
    match (left, right) with
        | ([], []) -> true
        | (_ :: leftRest, _ :: rightRest) -> sameLength(leftRest)(rightRest)
        | _ -> false

let recursive containsName (name: Str) (names: List(Str)) =
    match names with
        | [] -> false
        | candidate :: rest -> candidate == name || containsName(name)(rest)

let recursive lookupKnownCallee (name: Str) (callees: List((Str, List(Str), Expr))) =
    match callees with
        | [] -> None
        | (candidate, parameters, body) :: rest ->
            if candidate == name
            then Some((parameters, body))
            else lookupKnownCallee(name)(rest)

// Whether a pattern binds the name anywhere in its structure.
let recursive patternBindsName (pattern: Pattern) (name: Str) =
    match pattern with
        | PatternAt(_span, inner) -> patternBindsName(inner)(name)
        | PatternVar(bound) -> bound == name
        | PatternCons(head, tail) -> patternBindsName(head)(name) || patternBindsName(tail)(name)
        | PatternTuple(elements) -> anyPatternBindsName(elements)(name)
        | PatternConstructor(_constructor, fields) -> anyPatternBindsName(fields)(name)
        | PatternRecord(_record, fields) -> anyRecordFieldBindsName(fields)(name)
        | PatternAs(inner, bound) -> bound == name || patternBindsName(inner)(name)
        | PatternOr(alternatives) -> anyPatternBindsName(alternatives)(name)
        | _ -> false
and anyPatternBindsName (patterns: List(Pattern)) (name: Str) =
    match patterns with
        | [] -> false
        | pattern :: rest -> patternBindsName(pattern)(name) || anyPatternBindsName(rest)(name)
and anyRecordFieldBindsName (fields: List((Str, Pattern))) (name: Str) =
    match fields with
        | [] -> false
        | (_field, pattern) :: rest -> patternBindsName(pattern)(name) || anyRecordFieldBindsName(rest)(name)

// The root and the arguments of a curried application, in source order.
let recursive callSpineOf (expression: Expr) (arguments: List(Expr)) =
    match expression with
        | ExprAt(_span, inner) -> callSpineOf(inner)(arguments)
        | ExprCall(callee, argument, _sugar, _layout) -> callSpineOf(callee)(argument :: arguments)
        | root -> (root, arguments)

let recursive reachesAt (constructors: List(Str)) (callees: List((Str, List(Str), Expr))) (depth: Int) (expression: Expr) (variable: Str) =
    if depth > maximumCallDepth
    then false
    else
        match expression with
            | ExprAt(_span, inner) -> reachesAt(constructors)(callees)(depth)(inner)(variable)
            | ExprVar(name) -> name == variable
            | ExprLambda(parameter, body, _annotation) -> parameter != variable && reachesAt(constructors)(callees)(depth)(body)(variable)
            | ExprIf(_condition, thenBranch, elseBranch) -> reachesAt(constructors)(callees)(depth)(thenBranch)(variable) && reachesAt(constructors)(callees)(depth)(elseBranch)(variable)
            | ExprMatch(_scrutinee, [], _defaultArm) -> false
            | ExprMatch(_scrutinee, arms, _defaultArm) -> everyArmReaches(constructors)(callees)(depth)(arms)(variable)
            | ExprRecord(_name, fields, _multiline) -> anyFieldReaches(constructors)(callees)(depth)(fields)(variable)
            | ExprRecordUpdate(target, updates) -> reachesAt(constructors)(callees)(depth)(target)(variable) || anyFieldReaches(constructors)(callees)(depth)(updates)(variable)
            | ExprTuple(elements) -> anyReaches(constructors)(callees)(depth)(elements)(variable)
            | ExprList(elements, _multiline) -> anyReaches(constructors)(callees)(depth)(elements)(variable)
            | ExprCons(head, tail) -> reachesAt(constructors)(callees)(depth)(head)(variable) || reachesAt(constructors)(callees)(depth)(tail)(variable)
            | ExprCall(_callee, _argument, _sugar, _layout) -> callReaches(constructors)(callees)(depth)(callSpineOf(expression)([]))(variable)
            | _ -> false
and anyReaches (constructors: List(Str)) (callees: List((Str, List(Str), Expr))) (depth: Int) (expressions: List(Expr)) (variable: Str) =
    match expressions with
        | [] -> false
        | expression :: rest -> reachesAt(constructors)(callees)(depth)(expression)(variable) || anyReaches(constructors)(callees)(depth)(rest)(variable)
and anyFieldReaches (constructors: List(Str)) (callees: List((Str, List(Str), Expr))) (depth: Int) (fields: List((Str, Expr))) (variable: Str) =
    match fields with
        | [] -> false
        | (_field, value) :: rest -> reachesAt(constructors)(callees)(depth)(value)(variable) || anyFieldReaches(constructors)(callees)(depth)(rest)(variable)
and everyArmReaches (constructors: List(Str)) (callees: List((Str, List(Str), Expr))) (depth: Int) (arms: List((Pattern, Expr, Maybe(Expr)))) (variable: Str) =
    match arms with
        | [] -> true
        | (pattern, body, _guard) :: rest -> !patternBindsName(pattern)(variable) && reachesAt(constructors)(callees)(depth)(body)(variable) && everyArmReaches(constructors)(callees)(depth)(rest)(variable)
and callReaches (constructors: List(Str)) (callees: List((Str, List(Str), Expr))) (depth: Int) (spine: (Expr, List(Expr))) (variable: Str) =
    match spine with
        | (ExprVar(name), arguments) ->
            if containsName(name)(constructors)
            then anyReaches(constructors)(callees)(depth)(arguments)(variable)
            else
                match lookupKnownCallee(name)(callees) with
                    | Some((parameters, body)) -> sameLength(arguments)(parameters) && anyArgumentReachesThrough(constructors)(callees)(depth)(arguments)(parameters)(body)(variable)
                    | None -> false
        | _ -> false
and anyArgumentReachesThrough (constructors: List(Str)) (callees: List((Str, List(Str), Expr))) (depth: Int) (arguments: List(Expr)) (parameters: List(Str)) (body: Expr) (variable: Str) =
    match (arguments, parameters) with
        | ([], []) -> false
        | (argument :: argumentRest, parameter :: parameterRest) -> reachesAt(constructors)(callees)(depth)(argument)(variable) && reachesAt(constructors)(callees)(depth + 1)(body)(parameter) || anyArgumentReachesThrough(constructors)(callees)(depth)(argumentRest)(parameterRest)(body)(variable)
        | _ -> false

// Whether `body`'s result always reaches `variable`. `constructors` names the ADT constructors
// in scope and `callees` the known let-bound functions as (name, parameter chain, innermost
// body); a call is followed into a callee only when it is saturated.
let resultAlwaysReachesVariable (constructors: List(Str)) (callees: List((Str, List(Str), Expr))) (body: Expr) (variable: Str) = reachesAt(constructors)(callees)(0)(body)(variable)
