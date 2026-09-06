// The shapes stage 0's entry-helper inlining registers (`ExprHasCallOrAggregate`,
// `TryGetNestedRecursiveReturn`): a let-bound, non-recursive function whose body allocates or
// calls is a candidate for splicing into a call site, unless it is the nested-recursive-return
// shape (`let recursive go ... in go`) that stage 0 specializes instead. Every predicate here is
// structural; the call-site decision (a live reuse token, or a fresh result under a loop's back
// edge, and whether the spliced body's references resolve there) stays in CoreLowering.ash.

import AshesCompiler.Frontend.Syntax
export (
    value exprHasCallOrAggregate,
    value isNestedRecursiveReturn,
    value isInlinableHelperValue,
)

let recursive unspanExpression (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> unspanExpression(inner)
        | other -> other

// The parameters of a curried lambda value, innermost first, and its innermost body.
let recursive lambdaChain (value: Expr) (reversedParameters: List(Str)) =
    match value with
        | ExprAt(_span, inner) -> lambdaChain(inner)(reversedParameters)
        | ExprLambda(parameter, body, _annotation) -> lambdaChain(body)(parameter :: reversedParameters)
        | body -> (reversedParameters, body)

let either (left: Expr) (right: Expr) test = test(left) || test(right)

let recursive anyCaseHasCallOrAggregate (cases: List((Pattern, Expr, Maybe(Expr)))) =
    match cases with
        | [] -> false
        | (_pattern, body, guard) :: rest -> exprHasCallOrAggregate(body) || guardHasCallOrAggregate(guard) || anyCaseHasCallOrAggregate(rest)
and guardHasCallOrAggregate (guard: Maybe(Expr)) =
    match guard with
        | None -> false
        | Some(expression) -> exprHasCallOrAggregate(expression)
// Whether an expression may allocate or call: a call or an aggregate literal anywhere in it.
// Only provably allocation-free leaves and their pure compositions are false; any shape not
// listed counts as allocating.
and exprHasCallOrAggregate (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> exprHasCallOrAggregate(inner)
        | ExprCall(_function, _argument, _isSugar, _layout) -> true
        | ExprTuple(_elements) -> true
        | ExprList(_elements, _trailing) -> true
        | ExprCons(_head, _tail) -> true
        | ExprRecord(_name, _fields, _trailing) -> true
        | ExprRecordUpdate(_target, _fields) -> true
        | ExprInt(_value) -> false
        | ExprUInt(_value, _bits, _text) -> false
        | ExprBigInt(_text) -> false
        | ExprFloat(_value, _text) -> false
        | ExprString(_text) -> false
        | ExprRune(_value) -> false
        | ExprBool(_value) -> false
        | ExprVar(_name) -> false
        | ExprQualifiedVar(_module, _name) -> false
        | ExprAdd(left, right) -> either(left)(right)(exprHasCallOrAggregate)
        | ExprSubtract(left, right) -> either(left)(right)(exprHasCallOrAggregate)
        | ExprMultiply(left, right) -> either(left)(right)(exprHasCallOrAggregate)
        | ExprDivide(left, right) -> either(left)(right)(exprHasCallOrAggregate)
        | ExprModulo(left, right) -> either(left)(right)(exprHasCallOrAggregate)
        | ExprBitwiseAnd(left, right) -> either(left)(right)(exprHasCallOrAggregate)
        | ExprBitwiseOr(left, right) -> either(left)(right)(exprHasCallOrAggregate)
        | ExprBitwiseXor(left, right) -> either(left)(right)(exprHasCallOrAggregate)
        | ExprShiftLeft(left, right) -> either(left)(right)(exprHasCallOrAggregate)
        | ExprShiftRight(left, right) -> either(left)(right)(exprHasCallOrAggregate)
        | ExprBitwiseNot(operand) -> exprHasCallOrAggregate(operand)
        | ExprLogicalNot(operand) -> exprHasCallOrAggregate(operand)
        | ExprGreaterThan(left, right) -> either(left)(right)(exprHasCallOrAggregate)
        | ExprLessThan(left, right) -> either(left)(right)(exprHasCallOrAggregate)
        | ExprGreaterOrEqual(left, right) -> either(left)(right)(exprHasCallOrAggregate)
        | ExprLessOrEqual(left, right) -> either(left)(right)(exprHasCallOrAggregate)
        | ExprEqual(left, right) -> either(left)(right)(exprHasCallOrAggregate)
        | ExprNotEqual(left, right) -> either(left)(right)(exprHasCallOrAggregate)
        | ExprLogicalAnd(left, right) -> either(left)(right)(exprHasCallOrAggregate)
        | ExprLogicalOr(left, right) -> either(left)(right)(exprHasCallOrAggregate)
        | ExprIf(condition, thenBranch, elseBranch) -> exprHasCallOrAggregate(condition) || either(thenBranch)(elseBranch)(exprHasCallOrAggregate)
        | ExprLet(_name, value, body, _parameters, _annotation, _constraints) -> either(value)(body)(exprHasCallOrAggregate)
        | ExprLetRecursive(_name, value, body, _parameters, _annotation, _constraints) -> either(value)(body)(exprHasCallOrAggregate)
        | ExprLetResult(_name, value, body) -> either(value)(body)(exprHasCallOrAggregate)
        | ExprLambda(_parameter, body, _annotation) -> exprHasCallOrAggregate(body)
        | ExprAwait(task) -> exprHasCallOrAggregate(task)
        | ExprMatch(value, cases, _position) -> exprHasCallOrAggregate(value) || anyCaseHasCallOrAggregate(cases)
        | _ -> true

// The body past the leading non-recursive `let`s of a function body.
let recursive pastLeadingLets (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> pastLeadingLets(inner)
        | ExprLet(_name, _value, body, _parameters, _annotation, _constraints) -> pastLeadingLets(body)
        | other -> other

// Whether the recursive worker `name` is returned bare (`in go`) or applied to the last outer
// parameter (`in go(acc)`), the two nested-recursive-return shapes.
let returnsRecursiveWorker (name: Str) (letBody: Expr) (outerReversed: List(Str)) =
    match unspanExpression(letBody) with
        | ExprVar(reference) -> reference == name
        | ExprCall(function, argument, _isSugar, _layout) ->
            match (unspanExpression(function), unspanExpression(argument), outerReversed) with
                | (ExprVar(reference), ExprVar(forwarded), last :: _rest) -> reference == name && forwarded == last
                | _ -> false
        | _ -> false

// A function that returns a nested single-parameter recursive worker (`let recursive go node =
// ... in go`, the Map.set shape), which stage 0 specializes for in-place reuse rather than
// inlines.
let isNestedRecursiveReturn (value: Expr) =
    match lambdaChain(value)([]) with
        | (outerReversed, body) ->
            match pastLeadingLets(body) with
                | ExprLetRecursive(name, recursiveValue, letBody, _parameters, _annotation, _constraints) ->
                    match unspanExpression(recursiveValue) with
                        | ExprLambda(_parameter, lambdaBody, _annotation) ->
                            match unspanExpression(lambdaBody) with
                                | ExprLambda(_inner, _innerBody, _innerAnnotation) -> false
                                | _ -> returnsRecursiveWorker(name)(letBody)(outerReversed)
                        | _ -> false
                | _ -> false

// A let-bound lambda whose body can contribute an allocation and is not the specialized shape.
let isInlinableHelperValue (value: Expr) =
    match lambdaChain(value)([]) with
        | (_parameters, body) -> isNestedRecursiveReturn(value) == false && exprHasCallOrAggregate(body)
