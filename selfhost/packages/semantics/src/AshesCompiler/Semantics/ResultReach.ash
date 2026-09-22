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
// - A saturated call to a known let-bound function reaches through each argument position the
//   variable reaches whose parameter the callee's result always reaches, read from the must-reach
//   table the reach analysis computed as a fixpoint (a recursive or mutually recursive callee
//   included); a constructor application reaches through any argument.

import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.Pattern
export (
    value patternBindsName,
    value resultAlwaysReachesVariable,
)

let recursive sameLength (left: List(Expr)) (right: List(Str)) =
    match (left, right) with
        | ([], []) -> true
        | (_ :: leftRest, _ :: rightRest) -> sameLength(leftRest)(rightRest)
        | _ -> false

let recursive containsName (name: Str) (names: List(Str)) =
    match names with
        | [] -> false
        | candidate :: rest -> candidate == name || containsName(name)(rest)

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

let recursive reachesAt (constructors: List(Str)) (callees: Str -> Maybe((List(Str), List(Str)))) (expression: Expr) (variable: Str) =
    match expression with
        | ExprAt(_span, inner) -> reachesAt(constructors)(callees)(inner)(variable)
        | ExprVar(name) -> name == variable
        | ExprLambda(parameter, body, _annotation) -> parameter != variable && reachesAt(constructors)(callees)(body)(variable)
        | ExprLet(name, _value, body, _parameters, _annotation, _constraints) -> name != variable && reachesAt(constructors)(callees)(body)(variable)
        | ExprIf(_condition, thenBranch, elseBranch) -> reachesAt(constructors)(callees)(thenBranch)(variable) && reachesAt(constructors)(callees)(elseBranch)(variable)
        | ExprMatch(_scrutinee, [], _defaultArm) -> false
        | ExprMatch(_scrutinee, arms, _defaultArm) -> everyArmReaches(constructors)(callees)(arms)(variable)
        | ExprRecord(_name, fields, _multiline) -> anyFieldReaches(constructors)(callees)(fields)(variable)
        | ExprRecordUpdate(target, updates) -> reachesAt(constructors)(callees)(target)(variable) || anyFieldReaches(constructors)(callees)(updates)(variable)
        | ExprTuple(elements) -> anyReaches(constructors)(callees)(elements)(variable)
        | ExprList(elements, _multiline) -> anyReaches(constructors)(callees)(elements)(variable)
        | ExprCons(head, tail) -> reachesAt(constructors)(callees)(head)(variable) || reachesAt(constructors)(callees)(tail)(variable)
        | ExprCall(_callee, _argument, _sugar, _layout) ->
            callReaches(constructors)(callees)(callSpineOf(expression)([]))(variable)
        | _ -> false
and anyReaches (constructors: List(Str)) (callees: Str -> Maybe((List(Str), List(Str)))) (expressions: List(Expr)) (variable: Str) =
    match expressions with
        | [] -> false
        | expression :: rest -> reachesAt(constructors)(callees)(expression)(variable) || anyReaches(constructors)(callees)(rest)(variable)
and anyFieldReaches (constructors: List(Str)) (callees: Str -> Maybe((List(Str), List(Str)))) (fields: List((Str, Expr))) (variable: Str) =
    match fields with
        | [] -> false
        | (_field, value) :: rest -> reachesAt(constructors)(callees)(value)(variable) || anyFieldReaches(constructors)(callees)(rest)(variable)
and everyArmReaches (constructors: List(Str)) (callees: Str -> Maybe((List(Str), List(Str)))) (arms: List((Pattern, Expr, Maybe(Expr)))) (variable: Str) =
    match arms with
        | [] -> true
        | (pattern, body, _guard) :: rest -> !patternBindsName(pattern)(variable) && reachesAt(constructors)(callees)(body)(variable) && everyArmReaches(constructors)(callees)(rest)(variable)
and callReaches (constructors: List(Str)) (callees: Str -> Maybe((List(Str), List(Str)))) (spine: (Expr, List(Expr))) (variable: Str) =
    match spine with
        | (ExprVar(name), arguments) ->
            if containsName(name)(constructors)
            then anyReaches(constructors)(callees)(arguments)(variable)
            else
                match callees(name) with
                    | Some((parameters, reached)) -> sameLength(arguments)(parameters) && anyArgumentReachesThrough(constructors)(callees)(arguments)(parameters)(reached)(variable)
                    | None -> false
        | _ -> false
and anyArgumentReachesThrough (constructors: List(Str)) (callees: Str -> Maybe((List(Str), List(Str)))) (arguments: List(Expr)) (parameters: List(Str)) (reached: List(Str)) (variable: Str) =
    match (arguments, parameters) with
        | ([], []) -> false
        | (argument :: argumentRest, parameter :: parameterRest) -> containsName(parameter)(reached) && reachesAt(constructors)(callees)(argument)(variable) || anyArgumentReachesThrough(constructors)(callees)(argumentRest)(parameterRest)(reached)(variable)
        | _ -> false

// Whether `body`'s result always reaches `variable`. `constructors` names the ADT constructors
// in scope and `callees` answers a let-bound function's parameter chain and the parameters its
// result always reaches; a call is followed only when it is saturated.
let resultAlwaysReachesVariable (constructors: List(Str)) (callees: Str -> Maybe((List(Str), List(Str)))) (body: Expr) (variable: Str) = reachesAt(constructors)(callees)(body)(variable)
