// Stage 0's affine self-append analysis (`ComputeAffineSelfAppendOrdinals`): the loop parameters
// a self-recursive function consumes at most once on every loop-continuing path, only as the
// leftmost leaf of the addition chain producing the parameter's own argument to an exact tail
// self-call, or passed through unchanged. A single-use `let acc2 = acc + rhs in ...` binding
// aliases the parameter's append, so its one use as the self-call argument still qualifies.
// Uses on an exit-only path are unrestricted, since no later iteration can observe them. The
// loop entry reserves a pair of watermark slots for every parameter the analysis keeps.

import Ashes.Collection.List.append
import Ashes.Collection.List.length
import AshesCompiler.Frontend.Syntax
export (
    value affineSelfAppendOrdinals,
)

type AffineWalk =
    | candidates: List(Int)
    | sawSelfCall: Bool

let recursive affineContainsName (name: Str) (names: List(Str)) =
    match names with
        | [] -> false
        | candidate :: rest -> candidate == name || affineContainsName(name)(rest)

let recursive affineContainsOrdinal (ordinal: Int) (ordinals: List(Int)) =
    match ordinals with
        | [] -> false
        | candidate :: rest -> candidate == ordinal || affineContainsOrdinal(ordinal)(rest)

let recursive affineRemoveOrdinal (ordinal: Int) (ordinals: List(Int)) =
    match ordinals with
        | [] -> []
        | candidate :: rest ->
            if candidate == ordinal
            then affineRemoveOrdinal(ordinal)(rest)
            else candidate :: affineRemoveOrdinal(ordinal)(rest)

let recursive affineLookupAlias (name: Str) (aliases: List((Str, Int))) =
    match aliases with
        | [] -> None
        | (alias, ordinal) :: rest ->
            if alias == name
            then Some(ordinal)
            else affineLookupAlias(name)(rest)

// The ordinal of the last parameter named `name`: curried lambdas nest left to right, so a later
// same-named parameter is the live binding in the innermost body.
let recursive affineParameterOrdinal (name: Str) (parameters: List(Str)) (ordinal: Int) (found: Maybe(Int)) =
    match parameters with
        | [] -> found
        | parameter :: rest ->
            if parameter == name
            then affineParameterOrdinal(name)(rest)(ordinal + 1)(Some(ordinal))
            else affineParameterOrdinal(name)(rest)(ordinal + 1)(found)

// The ordinals with a live binding in the innermost body, in parameter order.
let recursive affineLiveOrdinals (parameters: List(Str)) (ordinal: Int) =
    match parameters with
        | [] -> []
        | parameter :: rest ->
            if affineContainsName(parameter)(rest)
            then affineLiveOrdinals(rest)(ordinal + 1)
            else ordinal :: affineLiveOrdinals(rest)(ordinal + 1)

// A name resolves to a candidate ordinal unless rebound: a single-use alias first, then the
// parameter it names.
let affineResolve (name: Str) (shadowed: List(Str)) (aliases: List((Str, Int))) (parameters: List(Str)) =
    if affineContainsName(name)(shadowed)
    then None
    else
        match affineLookupAlias(name)(aliases) with
            | Some(ordinal) -> Some(ordinal)
            | None -> affineParameterOrdinal(name)(parameters)(0)(None)

let recursive affineUnspan (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> affineUnspan(inner)
        | other -> other

let recursive affinePatternNames (pattern: Pattern) (names: List(Str)) =
    match pattern with
        | PatternAt(_span, inner) -> affinePatternNames(inner)(names)
        | PatternVar(name) -> name :: names
        | PatternCons(head, tail) ->
            names
            |> affinePatternNames(head)
            |> affinePatternNames(tail)
        | PatternTuple(patterns) -> affinePatternListNames(patterns)(names)
        | PatternConstructor(_name, patterns) -> affinePatternListNames(patterns)(names)
        | PatternRecord(_name, fields) -> affinePatternFieldNames(fields)(names)
        | PatternAs(inner, name) -> affinePatternNames(inner)(name :: names)
        | PatternOr(patterns) -> affinePatternListNames(patterns)(names)
        | _ -> names
and affinePatternListNames (patterns: List(Pattern)) (names: List(Str)) =
    match patterns with
        | [] -> names
        | pattern :: rest ->
            names
            |> affinePatternNames(pattern)
            |> affinePatternListNames(rest)
and affinePatternFieldNames (fields: List((Str, Pattern))) (names: List(Str)) =
    match fields with
        | [] -> names
        | (_field, pattern) :: rest ->
            names
            |> affinePatternNames(pattern)
            |> affinePatternFieldNames(rest)

// The names an expression mentions free of `bound`, stage 0's `FreeVars(expression, shadowed)`.
let recursive affineFreeNames (expression: Expr) (bound: List(Str)) (free: List(Str)) =
    match expression with
        | ExprAt(_span, inner) -> affineFreeNames(inner)(bound)(free)
        | ExprVar(name) ->
            if affineContainsName(name)(bound)
            then free
            else name :: free
        | ExprAdd(left, right) -> affineFreeNamesPair(left)(right)(bound)(free)
        | ExprSubtract(left, right) -> affineFreeNamesPair(left)(right)(bound)(free)
        | ExprMultiply(left, right) -> affineFreeNamesPair(left)(right)(bound)(free)
        | ExprDivide(left, right) -> affineFreeNamesPair(left)(right)(bound)(free)
        | ExprModulo(left, right) -> affineFreeNamesPair(left)(right)(bound)(free)
        | ExprBitwiseAnd(left, right) -> affineFreeNamesPair(left)(right)(bound)(free)
        | ExprBitwiseOr(left, right) -> affineFreeNamesPair(left)(right)(bound)(free)
        | ExprBitwiseXor(left, right) -> affineFreeNamesPair(left)(right)(bound)(free)
        | ExprShiftLeft(left, right) -> affineFreeNamesPair(left)(right)(bound)(free)
        | ExprShiftRight(left, right) -> affineFreeNamesPair(left)(right)(bound)(free)
        | ExprBitwiseNot(operand) -> affineFreeNames(operand)(bound)(free)
        | ExprLogicalNot(operand) -> affineFreeNames(operand)(bound)(free)
        | ExprLogicalAnd(left, right) -> affineFreeNamesPair(left)(right)(bound)(free)
        | ExprLogicalOr(left, right) -> affineFreeNamesPair(left)(right)(bound)(free)
        | ExprGreaterThan(left, right) -> affineFreeNamesPair(left)(right)(bound)(free)
        | ExprLessThan(left, right) -> affineFreeNamesPair(left)(right)(bound)(free)
        | ExprGreaterOrEqual(left, right) -> affineFreeNamesPair(left)(right)(bound)(free)
        | ExprLessOrEqual(left, right) -> affineFreeNamesPair(left)(right)(bound)(free)
        | ExprEqual(left, right) -> affineFreeNamesPair(left)(right)(bound)(free)
        | ExprNotEqual(left, right) -> affineFreeNamesPair(left)(right)(bound)(free)
        | ExprResultPipe(left, right) -> affineFreeNamesPair(left)(right)(bound)(free)
        | ExprResultMapErrorPipe(left, right) -> affineFreeNamesPair(left)(right)(bound)(free)
        | ExprCons(left, right) -> affineFreeNamesPair(left)(right)(bound)(free)
        | ExprLet(name, value, body, _parameters, _annotation, _requirements) ->
            free
            |> affineFreeNames(value)(bound)
            |> affineFreeNames(body)(name :: bound)
        | ExprLetResult(name, value, body) ->
            free
            |> affineFreeNames(value)(bound)
            |> affineFreeNames(body)(name :: bound)
        | ExprLetRecursive(name, value, body, _parameters, _annotation, _requirements) ->
            free
            |> affineFreeNames(value)(name :: bound)
            |> affineFreeNames(body)(name :: bound)
        | ExprIf(condition, thenBranch, elseBranch) ->
            free
            |> affineFreeNames(condition)(bound)
            |> affineFreeNames(thenBranch)(bound)
            |> affineFreeNames(elseBranch)(bound)
        | ExprLambda(parameter, body, _annotation) -> affineFreeNames(body)(parameter :: bound)(free)
        | ExprCall(function, argument, _sugar, _layout) -> affineFreeNamesPair(function)(argument)(bound)(free)
        | ExprTuple(elements) -> affineFreeNamesList(elements)(bound)(free)
        | ExprList(elements, _multiline) -> affineFreeNamesList(elements)(bound)(free)
        | ExprMatch(value, cases, _position) ->
            free
            |> affineFreeNames(value)(bound)
            |> affineFreeNamesCases(cases)(bound)
        | ExprAwait(operand) -> affineFreeNames(operand)(bound)(free)
        | ExprRecord(_name, fields, _multiline) -> affineFreeNamesFields(fields)(bound)(free)
        | ExprRecordUpdate(target, fields) ->
            free
            |> affineFreeNames(target)(bound)
            |> affineFreeNamesFields(fields)(bound)
        | ExprPerform(operand) -> affineFreeNames(operand)(bound)(free)
        | ExprHandle(body, clauses) ->
            free
            |> affineFreeNames(body)(bound)
            |> affineFreeNamesClauses(clauses)(bound)
        | _ -> free
and affineFreeNamesPair (left: Expr) (right: Expr) (bound: List(Str)) (free: List(Str)) =
    free
    |> affineFreeNames(left)(bound)
    |> affineFreeNames(right)(bound)
and affineFreeNamesList (expressions: List(Expr)) (bound: List(Str)) (free: List(Str)) =
    match expressions with
        | [] -> free
        | expression :: rest ->
            free
            |> affineFreeNames(expression)(bound)
            |> affineFreeNamesList(rest)(bound)
and affineFreeNamesFields (fields: List((Str, Expr))) (bound: List(Str)) (free: List(Str)) =
    match fields with
        | [] -> free
        | (_field, expression) :: rest ->
            free
            |> affineFreeNames(expression)(bound)
            |> affineFreeNamesFields(rest)(bound)
and affineFreeNamesCases (cases: List((Pattern, Expr, Maybe(Expr)))) (bound: List(Str)) (free: List(Str)) =
    match cases with
        | [] -> free
        | (pattern, body, guard) :: rest ->
            free
            |> affineFreeNamesGuard(guard)(append(affinePatternNames(pattern)([]))(bound))
            |> affineFreeNames(body)(append(affinePatternNames(pattern)([]))(bound))
            |> affineFreeNamesCases(rest)(bound)
and affineFreeNamesGuard (guard: Maybe(Expr)) (bound: List(Str)) (free: List(Str)) =
    match guard with
        | Some(expression) -> affineFreeNames(expression)(bound)(free)
        | None -> free
and affineFreeNamesClauses (clauses: List((Maybe(Str), Str, List(Pattern), Expr))) (bound: List(Str)) (free: List(Str)) =
    match clauses with
        | [] -> free
        | (resumption, _operation, patterns, body) :: rest ->
            free
            |> affineFreeNames(body)(affineClauseBound(resumption)(patterns)(bound))
            |> affineFreeNamesClauses(rest)(bound)
and affineClauseBound (resumption: Maybe(Str)) (patterns: List(Pattern)) (bound: List(Str)) =
    match resumption with
        | Some(name) -> name :: affinePatternListNames(patterns)(bound)
        | None -> affinePatternListNames(patterns)(bound)

let recursive affineAnyResolvesTo (candidate: Int) (shadowed: List(Str)) (aliases: List((Str, Int))) (parameters: List(Str)) (names: List(Str)) =
    match names with
        | [] -> false
        | name :: rest ->
            match affineResolve(name)(shadowed)(aliases)(parameters) with
                | Some(ordinal) -> ordinal == candidate || affineAnyResolvesTo(candidate)(shadowed)(aliases)(parameters)(rest)
                | None -> affineAnyResolvesTo(candidate)(shadowed)(aliases)(parameters)(rest)

let affineReferences (expression: Expr) (shadowed: List(Str)) (aliases: List((Str, Int))) (parameters: List(Str)) (candidate: Int) =
    []
    |> affineFreeNames(expression)(shadowed)
    |> affineAnyResolvesTo(candidate)(shadowed)(aliases)(parameters)

// `acc + r1 + ... + rn` whose leftmost leaf is the candidate and whose right operands never
// mention it, or the candidate itself passed through unchanged.
let recursive affineOwnArgument (argument: Expr) (shadowed: List(Str)) (aliases: List((Str, Int))) (parameters: List(Str)) (candidate: Int) =
    match affineUnspan(argument) with
        | ExprAdd(left, right) ->
            if affineReferences(right)(shadowed)(aliases)(parameters)(candidate)
            then false
            else affineOwnArgument(left)(shadowed)(aliases)(parameters)(candidate)
        | ExprVar(name) ->
            match affineResolve(name)(shadowed)(aliases)(parameters) with
                | Some(ordinal) -> ordinal == candidate
                | None -> false
        | _ -> false

let recursive affineLeftmostLeaf (expression: Expr) =
    match affineUnspan(expression) with
        | ExprAdd(left, _right) -> affineLeftmostLeaf(left)
        | leaf -> leaf

// The candidate whose own affine append `value` is (`acc + rhs`, no other reference to `acc` in
// the chain), when the value is an addition at all.
let affineAliasOrdinal (value: Expr) (shadowed: List(Str)) (aliases: List((Str, Int))) (parameters: List(Str)) (candidates: List(Int)) =
    match (affineUnspan(value), affineLeftmostLeaf(value)) with
        | (ExprAdd(_left, _right), ExprVar(leaf)) ->
            match affineResolve(leaf)(shadowed)(aliases)(parameters) with
                | Some(candidate) ->
                    if affineContainsOrdinal(candidate)(candidates) && affineOwnArgument(value)(shadowed)(aliases)(parameters)(candidate)
                    then Some(candidate)
                    else None
                | None -> None
        | _ -> None

let affineSaturate (left: Int) (right: Int) =
    if left + right >= 2
    then 2
    else left + right

// Free occurrences of `name`, saturating at 2; a node shape not walked here counts as 2, so a use
// can never be under-counted before the binding is treated as a single-use alias.
let recursive affineCountUses (expression: Expr) (name: Str) =
    match expression with
        | ExprAt(_span, inner) -> affineCountUses(inner)(name)
        | ExprInt(_value) -> 0
        | ExprUInt(_value, _bits, _text) -> 0
        | ExprBigInt(_text) -> 0
        | ExprFloat(_value, _text) -> 0
        | ExprString(_text) -> 0
        | ExprRune(_code) -> 0
        | ExprBool(_value) -> 0
        | ExprQualifiedVar(_module, _member) -> 0
        | ExprVar(candidate) ->
            if candidate == name
            then 1
            else 0
        | ExprAdd(left, right) ->
            name
            |> affineCountUses(right)
            |> affineSaturate(affineCountUses(left)(name))
        | ExprSubtract(left, right) ->
            name
            |> affineCountUses(right)
            |> affineSaturate(affineCountUses(left)(name))
        | ExprMultiply(left, right) ->
            name
            |> affineCountUses(right)
            |> affineSaturate(affineCountUses(left)(name))
        | ExprDivide(left, right) ->
            name
            |> affineCountUses(right)
            |> affineSaturate(affineCountUses(left)(name))
        | ExprModulo(left, right) ->
            name
            |> affineCountUses(right)
            |> affineSaturate(affineCountUses(left)(name))
        | ExprCall(function, argument, _sugar, _layout) ->
            name
            |> affineCountUses(argument)
            |> affineSaturate(affineCountUses(function)(name))
        | ExprIf(condition, thenBranch, elseBranch) ->
            name
            |> affineCountUses(elseBranch)
            |> affineSaturate(affineCountUses(thenBranch)(name))
            |> affineSaturate(affineCountUses(condition)(name))
        | ExprLet(bound, value, body, _parameters, _annotation, _requirements) ->
            if bound == name
            then affineCountUses(value)(name)
            else
                name
                |> affineCountUses(body)
                |> affineSaturate(affineCountUses(value)(name))
        | ExprMatch(value, cases, _position) ->
            name
            |> affineCountUses(value)
            |> affineCountUsesCases(cases)(name)
        | _ -> 2
and affineCountUsesCases (cases: List((Pattern, Expr, Maybe(Expr)))) (name: Str) (total: Int) =
    match cases with
        | [] -> total
        | (pattern, body, guard) :: rest ->
            if total >= 2
            then total
            else
                if []
                |> affinePatternNames(pattern)
                |> affineContainsName(name)
                then affineCountUsesCases(rest)(name)(total)
                else
                    name
                    |> affineCountUses(body)
                    |> affineSaturate(affineCountUsesGuard(guard)(name)(total))
                    |> affineCountUsesCases(rest)(name)
and affineCountUsesGuard (guard: Maybe(Expr)) (name: Str) (total: Int) =
    match guard with
        | Some(expression) ->
            name
            |> affineCountUses(expression)
            |> affineSaturate(total)
        | None -> total

let affineOnlyAllows (only: Maybe(Int)) (ordinal: Int) =
    match only with
        | Some(candidate) -> candidate == ordinal
        | None -> true

let recursive affineDisqualifyNames (shadowed: List(Str)) (aliases: List((Str, Int))) (parameters: List(Str)) (only: Maybe(Int)) (walk: AffineWalk) (names: List(Str)) =
    match names with
        | [] -> walk
        | name :: rest ->
            match affineResolve(name)(shadowed)(aliases)(parameters) with
                | Some(ordinal) ->
                    if affineOnlyAllows(only)(ordinal)
                    then affineDisqualifyNames(shadowed)(aliases)(parameters)(only)((walk with candidates = affineRemoveOrdinal(ordinal)(walk.candidates)))(rest)
                    else affineDisqualifyNames(shadowed)(aliases)(parameters)(only)(walk)(rest)
                | None -> affineDisqualifyNames(shadowed)(aliases)(parameters)(only)(walk)(rest)

// Every candidate the expression mentions (all of them, or only `only`) stops being affine.
let affineDisqualify (expression: Expr) (shadowed: List(Str)) (aliases: List((Str, Int))) (parameters: List(Str)) (only: Maybe(Int)) (walk: AffineWalk) =
    []
    |> affineFreeNames(expression)(shadowed)
    |> affineDisqualifyNames(shadowed)(aliases)(parameters)(only)(walk)

let affineDisqualifyIf (continues: Bool) (expression: Expr) (shadowed: List(Str)) (aliases: List((Str, Int))) (parameters: List(Str)) (walk: AffineWalk) =
    if continues
    then affineDisqualify(expression)(shadowed)(aliases)(parameters)(None)(walk)
    else walk

let affineDisqualifyGuard (continues: Bool) (guard: Maybe(Expr)) (shadowed: List(Str)) (aliases: List((Str, Int))) (parameters: List(Str)) (walk: AffineWalk) =
    match guard with
        | Some(expression) -> affineDisqualifyIf(continues)(expression)(shadowed)(aliases)(parameters)(walk)
        | None -> walk

let recursive affineCallSpine (expression: Expr) (arguments: List(Expr)) =
    match expression with
        | ExprAt(_span, inner) -> affineCallSpine(inner)(arguments)
        | ExprCall(function, argument, _sugar, _layout) -> affineCallSpine(function)(argument :: arguments)
        | root -> (root, arguments)

// The self name still reaches the recursive binding: no enclosing `let`, alias, pattern, or
// lambda rebound it.
let affineSelfVisible (self: Str) (shadowed: List(Str)) (aliases: List((Str, Int))) =
    match affineLookupAlias(self)(aliases) with
        | Some(_ordinal) -> false
        | None -> affineContainsName(self)(shadowed) == false

// At an exact self-call, argument `index` keeps candidate `index` only when it is the candidate's
// own affine append; any other mention of a candidate in any argument disqualifies it.
let recursive affineWalkArgument (argument: Expr) (index: Int) (candidates: List(Int)) (shadowed: List(Str)) (aliases: List((Str, Int))) (parameters: List(Str)) (walk: AffineWalk) =
    match candidates with
        | [] -> walk
        | candidate :: rest ->
            if index == candidate && affineOwnArgument(argument)(shadowed)(aliases)(parameters)(candidate)
            then affineWalkArgument(argument)(index)(rest)(shadowed)(aliases)(parameters)(walk)
            else
                walk
                |> affineDisqualify(argument)(shadowed)(aliases)(parameters)(Some(candidate))
                |> affineWalkArgument(argument)(index)(rest)(shadowed)(aliases)(parameters)

let recursive affineWalkArguments (arguments: List(Expr)) (index: Int) (shadowed: List(Str)) (aliases: List((Str, Int))) (parameters: List(Str)) (walk: AffineWalk) =
    match arguments with
        | [] -> walk
        | argument :: rest ->
            walk
            |> affineWalkArgument(argument)(index)(walk.candidates)(shadowed)(aliases)(parameters)
            |> affineWalkArguments(rest)(index + 1)(shadowed)(aliases)(parameters)

// Returns whether the subtree contains an exact tail self-call, together with the candidates
// that survive it. Conditions, scrutinees, guards, and binding values are checked only when a
// descendant continues the loop.
let recursive affineWalk (self: Str) (parameters: List(Str)) (expression: Expr) (shadowed: List(Str)) (aliases: List((Str, Int))) (walk: AffineWalk) =
    match expression with
        | ExprAt(_span, inner) -> affineWalk(self)(parameters)(inner)(shadowed)(aliases)(walk)
        | ExprIf(condition, thenBranch, elseBranch) ->
            match affineWalk(self)(parameters)(thenBranch)(shadowed)(aliases)(walk) with
                | (thenContinues, afterThen) ->
                    match affineWalk(self)(parameters)(elseBranch)(shadowed)(aliases)(afterThen) with
                        | (elseContinues, afterElse) -> (thenContinues || elseContinues, affineDisqualifyIf(thenContinues || elseContinues)(condition)(shadowed)(aliases)(parameters)(afterElse))
        | ExprMatch(value, cases, _position) ->
            match affineWalkCases(self)(parameters)(cases)(shadowed)(aliases)(false)(walk) with
                | (anyContinues, afterCases) -> (anyContinues, affineDisqualifyIf(anyContinues)(value)(shadowed)(aliases)(parameters)(afterCases))
        | ExprLet(name, value, body, _parameters, _annotation, _requirements) -> affineWalkLet(self)(parameters)(name)(value)(body)(shadowed)(aliases)(walk)
        | ExprLetResult(name, value, body) ->
            match affineWalk(self)(parameters)(body)(name :: shadowed)(aliases)(walk) with
                | (continues, after) -> (continues, affineDisqualifyIf(continues)(value)(shadowed)(aliases)(parameters)(after))
        | ExprLetRecursive(name, value, body, _parameters, _annotation, _requirements) ->
            match affineWalk(self)(parameters)(body)(name :: shadowed)(aliases)(walk) with
                | (continues, after) -> (continues, affineDisqualifyIf(continues)(value)(name :: shadowed)(aliases)(parameters)(after))
        | ExprCall(_function, _argument, _sugar, _layout) -> affineWalkCall(self)(parameters)(expression)(shadowed)(aliases)(walk)
        | _ -> (false, walk)
and affineWalkCases (self: Str) (parameters: List(Str)) (cases: List((Pattern, Expr, Maybe(Expr)))) (shadowed: List(Str)) (aliases: List((Str, Int))) (anyContinues: Bool) (walk: AffineWalk) =
    match cases with
        | [] -> (anyContinues, walk)
        | (pattern, body, guard) :: rest ->
            match affineWalk(self)(parameters)(body)(append(affinePatternNames(pattern)([]))(shadowed))(aliases)(walk) with
                | (continues, after) ->
                    after
                    |> affineDisqualifyGuard(continues)(guard)(append(affinePatternNames(pattern)([]))(shadowed))(aliases)(parameters)
                    |> affineWalkCases(self)(parameters)(rest)(shadowed)(aliases)(anyContinues || continues)
and affineWalkLet (self: Str) (parameters: List(Str)) (name: Str) (value: Expr) (body: Expr) (shadowed: List(Str)) (aliases: List((Str, Int))) (walk: AffineWalk) =
    match affineAliasOrdinal(value)(shadowed)(aliases)(parameters)(walk.candidates) with
        | Some(ordinal) ->
            if affineCountUses(body)(name) == 1
            then affineWalk(self)(parameters)(body)(shadowed)((name, ordinal) :: aliases)(walk)
            else affineWalkBoundLet(self)(parameters)(name)(value)(body)(shadowed)(aliases)(walk)
        | None -> affineWalkBoundLet(self)(parameters)(name)(value)(body)(shadowed)(aliases)(walk)
and affineWalkBoundLet (self: Str) (parameters: List(Str)) (name: Str) (value: Expr) (body: Expr) (shadowed: List(Str)) (aliases: List((Str, Int))) (walk: AffineWalk) =
    match affineWalk(self)(parameters)(body)(name :: shadowed)(aliases)(walk) with
        | (continues, after) -> (continues, affineDisqualifyIf(continues)(value)(shadowed)(aliases)(parameters)(after))
and affineWalkCall (self: Str) (parameters: List(Str)) (expression: Expr) (shadowed: List(Str)) (aliases: List((Str, Int))) (walk: AffineWalk) =
    match affineCallSpine(expression)([]) with
        | (ExprVar(callee), arguments) ->
            if callee == self && affineSelfVisible(self)(shadowed)(aliases) && length(arguments) == length(parameters)
            then (true, affineWalkArguments(arguments)(0)(shadowed)(aliases)(parameters)((walk with sawSelfCall = true)))
            else (false, walk)
        | _ -> (false, walk)

// The affine parameters of the loop function `self` over `parameters` (the whole curried chain,
// in order) with the innermost body `body`, as ordinals in parameter order; empty when the body
// has no exact self-call.
let affineSelfAppendOrdinals (self: Str) (parameters: List(Str)) (body: Expr) =
    match affineWalk(self)(parameters)(body)([])([])(AffineWalk(candidates = affineLiveOrdinals(parameters)(0), sawSelfCall = false)) with
        | (_continues, AffineWalk { candidates = candidates, sawSelfCall = true }) -> candidates
        | _ -> []
