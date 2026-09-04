// The syntactic questions the lowering asks before it places an aggregate on the reference-counted
// heap or retains the children an aggregate stores: whether a `let` body consumes its binding in one
// of the immediate shapes stage 0 accepts for a runtime-managed list, record, or ADT, whether a
// list is built fresh by a literal or a cons chain ending in one, and which terminal expressions a
// function body's escaping result can be (looking through `let` bodies and every `if`/`match` arm).
//
// Invariants:
// - Every walk is over the parsed tree with spans looked through; no type information is consulted.
// - A name is mentioned only where it is not shadowed: a `let`, a lambda parameter, or a pattern
//   that rebinds it hides the outer binding from the walk below it.
// - A list is fresh only when it is syntactically a literal or a cons chain bottoming out in one; a
//   cons onto a variable or a call is a shared tail and never fresh.
// - The terminal reconciliation accepts an arm as making the whole escape fresh only when every
//   other arm of its group is independently fresh too, and a keyless arm conflicts unless it is a
//   funnel back into the function being lowered.

import Ashes.Collection.List.length
import Ashes.Collection.List.reverse
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.Pattern
import AshesCompiler.Semantics.ResultReach.patternBindsName
export (
    value mentionsName,
    value isFreshListConstruction,
    value bodyReturnsBinding,
    value isImmediateListMatchUse,
    value isTailConsumedByImmediateListMatch,
    value isImmediateAdtMatchUse,
    value isImmediateRecordMatchUse,
    value isImmediateCopyUseOfRecord,
    value freshEscapeTerminals,
    value allTerminals,
    value anyArmConsistentlyFresh,
)

let recursive unspan (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> unspan(inner)
        | other -> other

let recursive anyMentions (name: Str) (expressions: List(Expr)) =
    match expressions with
        | [] -> false
        | expression :: rest -> mentionsName(name)(expression) || anyMentions(name)(rest)
and anyFieldMentions (name: Str) (fields: List((Str, Expr))) =
    match fields with
        | [] -> false
        | (_field, expression) :: rest -> mentionsName(name)(expression) || anyFieldMentions(name)(rest)
and anyCaseMentions (name: Str) (cases: List((Pattern, Expr, Maybe(Expr)))) =
    match cases with
        | [] -> false
        | (pattern, body, guard) :: rest ->
            if patternBindsName(pattern)(name)
            then anyCaseMentions(name)(rest)
            else guardMentions(name)(guard) || mentionsName(name)(body) || anyCaseMentions(name)(rest)
and guardMentions (name: Str) (guard: Maybe(Expr)) =
    match guard with
        | None -> false
        | Some(condition) -> mentionsName(name)(condition)
and anyHandlerMentions (name: Str) (arms: List((Maybe(Str), Str, List(Pattern), Expr))) =
    match arms with
        | [] -> false
        | (_capability, _operation, _patterns, body) :: rest -> mentionsName(name)(body) || anyHandlerMentions(name)(rest)
// Whether `expression` reads `name` anywhere it is not shadowed.
and mentionsName (name: Str) (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> mentionsName(name)(inner)
        | ExprVar(candidate) -> candidate == name
        | ExprAdd(left, right) -> anyMentions(name)([left, right])
        | ExprSubtract(left, right) -> anyMentions(name)([left, right])
        | ExprMultiply(left, right) -> anyMentions(name)([left, right])
        | ExprDivide(left, right) -> anyMentions(name)([left, right])
        | ExprModulo(left, right) -> anyMentions(name)([left, right])
        | ExprBitwiseAnd(left, right) -> anyMentions(name)([left, right])
        | ExprBitwiseOr(left, right) -> anyMentions(name)([left, right])
        | ExprBitwiseXor(left, right) -> anyMentions(name)([left, right])
        | ExprShiftLeft(left, right) -> anyMentions(name)([left, right])
        | ExprShiftRight(left, right) -> anyMentions(name)([left, right])
        | ExprBitwiseNot(operand) -> mentionsName(name)(operand)
        | ExprLogicalNot(operand) -> mentionsName(name)(operand)
        | ExprLogicalAnd(left, right) -> anyMentions(name)([left, right])
        | ExprLogicalOr(left, right) -> anyMentions(name)([left, right])
        | ExprGreaterThan(left, right) -> anyMentions(name)([left, right])
        | ExprLessThan(left, right) -> anyMentions(name)([left, right])
        | ExprGreaterOrEqual(left, right) -> anyMentions(name)([left, right])
        | ExprLessOrEqual(left, right) -> anyMentions(name)([left, right])
        | ExprEqual(left, right) -> anyMentions(name)([left, right])
        | ExprNotEqual(left, right) -> anyMentions(name)([left, right])
        | ExprResultPipe(left, right) -> anyMentions(name)([left, right])
        | ExprResultMapErrorPipe(left, right) -> anyMentions(name)([left, right])
        | ExprLet(bound, value, body, _parameters, _annotation, _requirements) -> mentionsName(name)(value) || bound != name && mentionsName(name)(body)
        | ExprLetResult(bound, value, body) -> mentionsName(name)(value) || bound != name && mentionsName(name)(body)
        | ExprLetRecursive(bound, value, body, _parameters, _annotation, _requirements) -> bound != name && (mentionsName(name)(value) || mentionsName(name)(body))
        | ExprIf(condition, thenBranch, elseBranch) -> anyMentions(name)([condition, thenBranch, elseBranch])
        | ExprLambda(parameter, body, _annotation) -> parameter != name && mentionsName(name)(body)
        | ExprCall(function, argument, _isSugar, _layout) -> anyMentions(name)([function, argument])
        | ExprTuple(elements) -> anyMentions(name)(elements)
        | ExprList(elements, _isMultiline) -> anyMentions(name)(elements)
        | ExprCons(head, tail) -> anyMentions(name)([head, tail])
        | ExprMatch(value, cases, _position) -> mentionsName(name)(value) || anyCaseMentions(name)(cases)
        | ExprAwait(operand) -> mentionsName(name)(operand)
        | ExprRecord(_typeName, fields, _isMultiline) -> anyFieldMentions(name)(fields)
        | ExprRecordUpdate(target, fields) -> mentionsName(name)(target) || anyFieldMentions(name)(fields)
        | ExprPerform(operation) -> mentionsName(name)(operation)
        | ExprHandle(body, arms) -> mentionsName(name)(body) || anyHandlerMentions(name)(arms)
        | _ -> false

// A list literal, or a cons chain bottoming out in one, stage 0's `IsFreshListConstructionExpression`.
let recursive isFreshListConstruction (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> isFreshListConstruction(inner)
        | ExprList(_elements, _isMultiline) -> true
        | ExprCons(_head, tail) -> isFreshListConstruction(tail)
        | _ -> false

// The body is the bare read of the binding, stage 0's `IsDirectBindingResult`.
let bodyReturnsBinding (name: Str) (body: Expr) =
    match unspan(body) with
        | ExprVar(candidate) -> candidate == name
        | _ -> false

// The names a list pattern binds to a spine: the whole list, or the tail of a cons.
let recursive listTailBindingNames (pattern: Pattern) =
    match pattern with
        | PatternAt(_span, inner) -> listTailBindingNames(inner)
        | PatternVar(name) -> [name]
        | PatternCons(_head, tail) -> listTailBindingNames(tail)
        | _ -> []

let recursive anyNameMentioned (names: List(Str)) (guard: Maybe(Expr)) (body: Expr) =
    match names with
        | [] -> false
        | name :: rest -> guardMentions(name)(guard) || mentionsName(name)(body) || anyNameMentioned(rest)(guard)(body)

let recursive noCaseMentions (name: Str) (cases: List((Pattern, Expr, Maybe(Expr)))) =
    match cases with
        | [] -> true
        | (pattern, body, guard) :: rest ->
            if patternBindsName(pattern)(name)
            then noCaseMentions(name)(rest)
            else guardMentions(name)(guard) == false && mentionsName(name)(body) == false && noCaseMentions(name)(rest)

let recursive noCaseKeepsTail (cases: List((Pattern, Expr, Maybe(Expr)))) =
    match cases with
        | [] -> true
        | (pattern, body, guard) :: rest ->
            anyNameMentioned(listTailBindingNames(pattern))(guard)(body) == false && noCaseKeepsTail(rest)

// The body matches the binding directly through at least two arms, none of which reads the binding
// again, stage 0's `IsImmediateAdtMatchUse`.
let isImmediateAdtMatchUse (name: Str) (body: Expr) =
    match unspan(body) with
        | ExprMatch(value, cases, _position) -> bodyReturnsBinding(name)(value) && length(cases) >= 2 && noCaseMentions(name)(cases)
        | _ -> false

// An immediate match that also keeps no list tail alive past its arm, stage 0's
// `IsImmediateCopyListMatchUse`.
let isImmediateListMatchUse (name: Str) (body: Expr) =
    match unspan(body) with
        | ExprMatch(_value, cases, _position) -> isImmediateAdtMatchUse(name)(body) && noCaseKeepsTail(cases)
        | _ -> false

// A nested `let` conses onto the binding and matches the extended list immediately, stage 0's
// `IsTailConsumedByImmediateListMatch`.
let isTailConsumedByImmediateListMatch (name: Str) (body: Expr) =
    match unspan(body) with
        | ExprLet(child, value, childBody, _parameters, _annotation, _requirements) ->
            match unspan(value) with
                | ExprCons(_head, tail) -> bodyReturnsBinding(name)(tail) && isImmediateListMatchUse(child)(childBody)
                | _ -> false
        | _ -> false

// A single unguarded constructor-pattern arm matching the binding whose body never reads it again,
// stage 0's `IsImmediateRuntimeRecordMatchUse`.
let isImmediateRecordMatchUse (name: Str) (body: Expr) =
    match unspan(body) with
        | ExprMatch(value, (pattern, armBody, None) :: [], _position) ->
            match pattern with
                | PatternConstructor(_constructor, _patterns) -> bodyReturnsBinding(name)(value) && mentionsName(name)(armBody) == false
                | PatternAt(_span, PatternConstructor(_constructor, _patterns)) -> bodyReturnsBinding(name)(value) && mentionsName(name)(armBody) == false
                | _ -> false
        | _ -> false

// The record appears only as a qualified field receiver inside an immediate scalar expression,
// stage 0's `IsImmediateCopyUseOfRecord`.
let recursive isImmediateCopyUseOfRecord (name: Str) (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> isImmediateCopyUseOfRecord(name)(inner)
        | ExprInt(_value) -> true
        | ExprUInt(_value, _bits, _text) -> true
        | ExprBigInt(_digits) -> true
        | ExprFloat(_value, _text) -> true
        | ExprString(_text) -> true
        | ExprRune(_value) -> true
        | ExprBool(_value) -> true
        | ExprVar(candidate) -> candidate != name
        | ExprQualifiedVar(_module, _member) -> true
        | ExprAdd(left, right) -> bothCopyUses(name)(left)(right)
        | ExprSubtract(left, right) -> bothCopyUses(name)(left)(right)
        | ExprMultiply(left, right) -> bothCopyUses(name)(left)(right)
        | ExprDivide(left, right) -> bothCopyUses(name)(left)(right)
        | ExprModulo(left, right) -> bothCopyUses(name)(left)(right)
        | ExprBitwiseAnd(left, right) -> bothCopyUses(name)(left)(right)
        | ExprBitwiseOr(left, right) -> bothCopyUses(name)(left)(right)
        | ExprBitwiseXor(left, right) -> bothCopyUses(name)(left)(right)
        | ExprShiftLeft(left, right) -> bothCopyUses(name)(left)(right)
        | ExprShiftRight(left, right) -> bothCopyUses(name)(left)(right)
        | ExprBitwiseNot(operand) -> isImmediateCopyUseOfRecord(name)(operand)
        | ExprLogicalNot(operand) -> isImmediateCopyUseOfRecord(name)(operand)
        | ExprGreaterThan(left, right) -> bothCopyUses(name)(left)(right)
        | ExprGreaterOrEqual(left, right) -> bothCopyUses(name)(left)(right)
        | ExprLessThan(left, right) -> bothCopyUses(name)(left)(right)
        | ExprLessOrEqual(left, right) -> bothCopyUses(name)(left)(right)
        | ExprEqual(left, right) -> bothCopyUses(name)(left)(right)
        | ExprNotEqual(left, right) -> bothCopyUses(name)(left)(right)
        | ExprCall(function, argument, _isSugar, _layout) -> bothCopyUses(name)(function)(argument)
        | _ -> false
and bothCopyUses (name: Str) (left: Expr) (right: Expr) = isImmediateCopyUseOfRecord(name)(left) && isImmediateCopyUseOfRecord(name)(right)

let recursive collectTerminals (body: Expr) (reversed: List(Expr)) =
    match body with
        | ExprAt(_span, inner) -> collectTerminals(inner)(reversed)
        | ExprIf(_condition, thenBranch, elseBranch) ->
            reversed
            |> collectTerminals(thenBranch)
            |> collectTerminals(elseBranch)
        | ExprMatch(_value, cases, _position) -> collectCaseTerminals(cases)(reversed)
        | ExprLet(_name, _value, letBody, _parameters, _annotation, _requirements) -> collectTerminals(letBody)(reversed)
        | ExprLetResult(_name, _value, letBody) -> collectTerminals(letBody)(reversed)
        | ExprLetRecursive(_name, _value, letBody, _parameters, _annotation, _requirements) -> collectTerminals(letBody)(reversed)
        | other -> other :: reversed
and collectCaseTerminals (cases: List((Pattern, Expr, Maybe(Expr)))) (reversed: List(Expr)) =
    match cases with
        | [] -> reversed
        | (_pattern, armBody, _guard) :: rest ->
            reversed
            |> collectTerminals(armBody)
            |> collectCaseTerminals(rest)

// Every terminal expression an escaping result can be, in source order: the walk looks through a
// `let`'s body and every `if`/`match` arm, stage 0's `CollectFreshEscapeTerminals`.
let freshEscapeTerminals (body: Expr) =
    []
    |> collectTerminals(body)
    |> reverse

// Whether every terminal satisfies `isFresh`; an empty terminal list never does.
let recursive allTerminals (isFresh: Expr -> Bool) (terminals: List(Expr)) =
    match terminals with
        | [] -> false
        | terminal :: [] -> isFresh(terminal)
        | terminal :: rest -> isFresh(terminal) && allTerminals(isFresh)(rest)

let recursive othersConsistent (key: Str) (index: Int) (position: Int) (isFresh: Expr -> Bool) (groupKey: Expr -> Maybe(Str)) (isFunnel: Expr -> Bool) (arms: List(Expr)) =
    match arms with
        | [] -> true
        | other :: rest ->
            if position == index || isFunnel(other)
            then othersConsistent(key)(index)(position + 1)(isFresh)(groupKey)(isFunnel)(rest)
            else
                match groupKey(other) with
                    | Some(otherKey) ->
                        if otherKey == key && isFresh(other) == false
                        then false
                        else othersConsistent(key)(index)(position + 1)(isFresh)(groupKey)(isFunnel)(rest)
                    | None -> isFresh(other) && othersConsistent(key)(index)(position + 1)(isFresh)(groupKey)(isFunnel)(rest)

let recursive anyArmConsistentlyFreshFrom (index: Int) (candidates: List(Expr)) (isFresh: Expr -> Bool) (groupKey: Expr -> Maybe(Str)) (isFunnel: Expr -> Bool) (arms: List(Expr)) =
    match candidates with
        | [] -> false
        | arm :: rest ->
            match groupKey(arm) with
                | Some(key) ->
                    if isFresh(arm) && othersConsistent(key)(index)(0)(isFresh)(groupKey)(isFunnel)(arms)
                    then true
                    else anyArmConsistentlyFreshFrom(index + 1)(rest)(isFresh)(groupKey)(isFunnel)(arms)
                | None -> anyArmConsistentlyFreshFrom(index + 1)(rest)(isFresh)(groupKey)(isFunnel)(arms)

// Stage 0's `AnyArmConsistentlyFresh`: some fresh arm whose every non-funnel sibling of the same
// group, and every keyless sibling, is fresh as well.
let anyArmConsistentlyFresh (arms: List(Expr)) (isFresh: Expr -> Bool) (groupKey: Expr -> Maybe(Str)) (isFunnel: Expr -> Bool) = anyArmConsistentlyFreshFrom(0)(arms)(isFresh)(groupKey)(isFunnel)(arms)
