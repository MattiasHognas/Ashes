// Runtime-managed TCO loop parameter eligibility (the tail PR #847 left open — OPT-26/OPT-27/
// OPT-29's "the runtime-managed loop parameters" gap): which of a self-recursive function's
// loop parameters are rebuilt, at every tail self-call, only through `+` on the parameter's own
// (possibly aliased) value or passed straight through unchanged — the shape a `Str` accumulator
// needs before `CoreLowering.ash` may place it on the reference-counted heap instead of leaving
// it arena-typed and un-tracked.
//
// This reuses `TcoAffineAppend.ash`'s own walk (the same "leftmost leaf of the append chain, or
// an untouched pass-through, on every continuing path" proof its ConcatStrTip in-place-growth
// reservation relies on) rather than duplicating it, but relaxes what counts as an alias first:
// the reservation only aliases a `let` whose OWN value is itself an append
// (`let acc2 = acc + rhs in ...`), while a plain `let r = acc in ...` — this port's target shape
// — leaves `r` shadowed instead of aliased, disqualifying `acc` from the reservation walk even
// though runtime-managed placement needs nothing stronger than "still just `+`, or unchanged,
// once `r` is read back as `acc`". `inlinePlainVarAliases` substitutes every such plain-variable
// `let` binding by name before handing the body to the existing walk, so `let r = acc in r + "x"`
// reads exactly like `acc + "x"` to it. A case whose own pattern rebinds the alias name first is
// left untouched — conservative, since the alias is then simply not recognized there rather than
// misidentified with an unrelated shadowed binding of the same name.
//
// A parameter this walk never sees as the argument to `+` or a bare pass-through anywhere in the
// self-call spine is never a candidate in the first place (the same disqualification rule that
// already drops a `match`/`unconsText` scrutinee from the affine ordinals covers a
// pattern-consumed parameter here too), so this can never collide with the pattern-owner
// mechanism a consumed list or text tail already uses.

import Ashes.Collection.List.append
import Ashes.Collection.List.length
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.TcoAffineAppend.affineSelfAppendOrdinals
export (
    type TcoArgumentShape(..),
    value runtimeManagedStrOrdinals,
    value tcoSelfCallShapes,
    value isTcoListShape,
)

// The reference-ownership shape shared by every exact tail self-call argument at one parameter
// position, stage 0's `TcoSelfCallArgumentShape`: the parameter's own unchanged read, a cons cell
// whose tail is that read (the accumulator grows by one cell per iteration), a name bound as the
// cons-tail of a `match` on that parameter one level up (the parameter shrinks by one cell), or
// anything else — a different shape at some self-call, a fresh rebuild, or a value the walk does
// not classify.
type TcoArgumentShape =
    | TcoPassThroughShape
    | TcoGrownConsShape
    | TcoConsumedTailShape
    | TcoFreshListShape
    | TcoOtherShape
    deriving {Eq, Show}

let recursive patternBindsName (name: Str) (pattern: Pattern) =
    match pattern with
        | PatternAt(_span, inner) -> patternBindsName(name)(inner)
        | PatternVar(candidate) -> candidate == name
        | PatternCons(head, tail) -> patternBindsName(name)(head) || patternBindsName(name)(tail)
        | PatternTuple(patterns) -> patternListBindsName(name)(patterns)
        | PatternConstructor(_constructorName, patterns) -> patternListBindsName(name)(patterns)
        | PatternRecord(_constructorName, fields) -> patternFieldsBindName(name)(fields)
        | PatternAs(inner, alias) -> alias == name || patternBindsName(name)(inner)
        | PatternOr(patterns) -> patternListBindsName(name)(patterns)
        | _ -> false
and patternListBindsName (name: Str) (patterns: List(Pattern)) =
    match patterns with
        | [] -> false
        | pattern :: rest -> patternBindsName(name)(pattern) || patternListBindsName(name)(rest)
and patternFieldsBindName (name: Str) (fields: List((Str, Pattern))) =
    match fields with
        | [] -> false
        | (_fieldName, pattern) :: rest -> patternBindsName(name)(pattern) || patternFieldsBindName(name)(rest)

// Substitutes every free occurrence of `target` with `replacement`, stopping inside any
// construct that rebinds `target` (a `let` of the same name, a `match` case whose pattern binds
// it). Constructs this port's target shapes never route an accumulator alias through (tuples,
// lists, lambdas, records, `await`, `handle`, ...) are left as they are — safe, since an
// unsubstituted occurrence simply is not recognized as the alias rather than being
// misidentified.
let recursive inlineName (target: Str) (replacement: Str) (expression: Expr) =
    match expression with
        | ExprAt(span, inner) ->
            inner
            |> inlineName(target)(replacement)
            |> ExprAt(span)
        | ExprVar(name) ->
            if name == target
            then ExprVar(replacement)
            else expression
        | ExprAdd(left, right) ->
            right
            |> inlineName(target)(replacement)
            |> ExprAdd(inlineName(target)(replacement)(left))
        | ExprSubtract(left, right) ->
            right
            |> inlineName(target)(replacement)
            |> ExprSubtract(inlineName(target)(replacement)(left))
        | ExprMultiply(left, right) ->
            right
            |> inlineName(target)(replacement)
            |> ExprMultiply(inlineName(target)(replacement)(left))
        | ExprDivide(left, right) ->
            right
            |> inlineName(target)(replacement)
            |> ExprDivide(inlineName(target)(replacement)(left))
        | ExprModulo(left, right) ->
            right
            |> inlineName(target)(replacement)
            |> ExprModulo(inlineName(target)(replacement)(left))
        | ExprEqual(left, right) ->
            right
            |> inlineName(target)(replacement)
            |> ExprEqual(inlineName(target)(replacement)(left))
        | ExprNotEqual(left, right) ->
            right
            |> inlineName(target)(replacement)
            |> ExprNotEqual(inlineName(target)(replacement)(left))
        | ExprIf(condition, thenBranch, elseBranch) ->
            elseBranch
            |> inlineName(target)(replacement)
            |> ExprIf(inlineName(target)(replacement)(condition))(inlineName(target)(replacement)(thenBranch))
        | ExprCall(function, argument, sugar, layout) ->
            ExprCall(inlineName(target)(replacement)(function))(inlineName(target)(replacement)(argument))(sugar)(layout)
        | ExprCons(head, tail) ->
            tail
            |> inlineName(target)(replacement)
            |> ExprCons(inlineName(target)(replacement)(head))
        | ExprLet(name, value, body, parameters, annotation, requirements) ->
            if name == target
            then
                ExprLet(name)(inlineName(target)(replacement)(value))(body)(parameters)(annotation)(requirements)
            else
                ExprLet(name)(inlineName(target)(replacement)(value))(inlineName(target)(replacement)(body))(parameters)(annotation)(requirements)
        | ExprMatch(scrutinee, cases, position) ->
            ExprMatch(inlineName(target)(replacement)(scrutinee))(inlineNameCases(target)(replacement)(cases))(position)
        | other -> other
and inlineNameCases (target: Str) (replacement: Str) (cases: List((Pattern, Expr, Maybe(Expr)))) =
    match cases with
        | [] -> []
        | (pattern, body, guard) :: rest ->
            (if patternBindsName(target)(pattern)
            then (pattern, body, guard)
            else (pattern, inlineName(target)(replacement)(body), inlineNameGuard(target)(replacement)(guard))) :: inlineNameCases(target)(replacement)(rest)
and inlineNameGuard (target: Str) (replacement: Str) (guard: Maybe(Expr)) =
    match guard with
        | Some(expression) ->
            expression
            |> inlineName(target)(replacement)
            |> Some
        | None -> None

// Every plain-variable `let` binding in `expression`, inlined by name: `let r = acc in ... r ...`
// becomes `... acc ...` wherever `r`'s own pattern does not rebind it first. The `let` itself is
// removed; a non-plain-variable binding is left as it is and only its body is walked further.
let recursive inlinePlainVarAliases (expression: Expr) =
    match expression with
        | ExprAt(span, inner) ->
            inner
            |> inlinePlainVarAliases
            |> ExprAt(span)
        | ExprLet(name, ExprVar(source), body, _parameters, _annotation, _requirements) ->
            body
            |> inlineName(name)(source)
            |> inlinePlainVarAliases
        | ExprLet(name, ExprAt(_span, ExprVar(source)), body, _parameters, _annotation, _requirements) ->
            body
            |> inlineName(name)(source)
            |> inlinePlainVarAliases
        | ExprLet(name, value, body, parameters, annotation, requirements) ->
            ExprLet(name)(value)(inlinePlainVarAliases(body))(parameters)(annotation)(requirements)
        | ExprIf(condition, thenBranch, elseBranch) ->
            elseBranch
            |> inlinePlainVarAliases
            |> ExprIf(condition)(inlinePlainVarAliases(thenBranch))
        | ExprMatch(scrutinee, cases, position) ->
            ExprMatch(scrutinee)(inlinePlainVarAliasesCases(cases))(position)
        | other -> other
and inlinePlainVarAliasesCases (cases: List((Pattern, Expr, Maybe(Expr)))) =
    match cases with
        | [] -> []
        | (pattern, body, guard) :: rest -> (pattern, inlinePlainVarAliases(body), guard) :: inlinePlainVarAliasesCases(rest)

// The ordinals (in `parameters` order) of every parameter of the self-recursive function `self`
// that is, on every tail self-call the innermost body `body` makes, either passed through
// unchanged or rebuilt as the leftmost leaf of a `+` chain — read through a plain-variable alias
// first, unlike `affineSelfAppendOrdinals`'s own, stricter reservation-growth eligibility.
let runtimeManagedStrOrdinals (self: Str) (parameters: List(Str)) (body: Expr) =
    body
    |> inlinePlainVarAliases
    |> affineSelfAppendOrdinals(self)(parameters)

// Stage 0's `TcoParamFactsWalk`: the self-call argument shapes, observed at every exact tail
// self-call the loop body reaches through `if` branches, `match` arms, and `let` bodies. A
// `match` on a parameter whose case pattern is a cons with a plain-variable tail binds that name
// as the parameter's consumed tail for the arm; any binder shadows the parameters and tail owners
// it names.
let recursive shapeContainsName (name: Str) (names: List(Str)) =
    match names with
        | [] -> false
        | candidate :: rest -> candidate == name || shapeContainsName(name)(rest)

let recursive shapeParameterOrdinal (name: Str) (parameters: List(Str)) (ordinal: Int) (found: Maybe(Int)) =
    match parameters with
        | [] -> found
        | parameter :: rest ->
            if parameter == name
            then shapeParameterOrdinal(name)(rest)(ordinal + 1)(Some(ordinal))
            else shapeParameterOrdinal(name)(rest)(ordinal + 1)(found)

let shapeResolveParameter (name: Str) (shadowed: List(Str)) (parameters: List(Str)) =
    if shapeContainsName(name)(shadowed)
    then None
    else shapeParameterOrdinal(name)(parameters)(0)(None)

let recursive shapeUnspan (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> shapeUnspan(inner)
        | other -> other

let recursive shapeUnspanPattern (pattern: Pattern) =
    match pattern with
        | PatternAt(_span, inner) -> shapeUnspanPattern(inner)
        | other -> other

let recursive shapePatternNames (pattern: Pattern) (names: List(Str)) =
    match pattern with
        | PatternAt(_span, inner) -> shapePatternNames(inner)(names)
        | PatternVar(name) -> name :: names
        | PatternCons(head, tail) ->
            names
            |> shapePatternNames(head)
            |> shapePatternNames(tail)
        | PatternTuple(patterns) -> shapePatternListNames(patterns)(names)
        | PatternConstructor(_constructorName, patterns) -> shapePatternListNames(patterns)(names)
        | PatternRecord(_constructorName, fields) -> shapePatternFieldNames(fields)(names)
        | PatternAs(inner, alias) -> alias :: shapePatternNames(inner)(names)
        | PatternOr(patterns) -> shapePatternListNames(patterns)(names)
        | _ -> names
and shapePatternListNames (patterns: List(Pattern)) (names: List(Str)) =
    match patterns with
        | [] -> names
        | pattern :: rest ->
            names
            |> shapePatternNames(pattern)
            |> shapePatternListNames(rest)
and shapePatternFieldNames (fields: List((Str, Pattern))) (names: List(Str)) =
    match fields with
        | [] -> names
        | (_fieldName, pattern) :: rest ->
            names
            |> shapePatternNames(pattern)
            |> shapePatternFieldNames(rest)

let recursive shapeLookupTailOwner (name: Str) (owners: List((Str, Int))) =
    match owners with
        | [] -> None
        | (candidate, ordinal) :: rest ->
            if candidate == name
            then Some(ordinal)
            else shapeLookupTailOwner(name)(rest)

let recursive shapeRemoveTailOwners (names: List(Str)) (owners: List((Str, Int))) =
    match owners with
        | [] -> []
        | (candidate, ordinal) :: rest ->
            if shapeContainsName(candidate)(names)
            then shapeRemoveTailOwners(names)(rest)
            else (candidate, ordinal) :: shapeRemoveTailOwners(names)(rest)

let shapeScrutineeOrdinal (value: Expr) (shadowed: List(Str)) (parameters: List(Str)) =
    match shapeUnspan(value) with
        | ExprVar(name) -> shapeResolveParameter(name)(shadowed)(parameters)
        | _ -> None

// The cons-tail name a case binds from a `match` on the parameter `scrutinee`, when the pattern
// is a cons whose tail is a plain variable.
let shapeCaseTailOwners (pattern: Pattern) (scrutinee: Maybe(Int)) =
    match (scrutinee, shapeUnspanPattern(pattern)) with
        | (Some(ordinal), PatternCons(_head, tail)) ->
            match shapeUnspanPattern(tail) with
                | PatternVar(name) -> [(name, ordinal)]
                | _ -> []
        | _ -> []

let shapeMerge (observed: Maybe(TcoArgumentShape)) (local: TcoArgumentShape) =
    match observed with
        | None -> Some(local)
        | Some(previous) ->
            if previous == local
            then Some(previous)
            else Some(TcoOtherShape)

// A list built fresh in the argument itself: a list literal, or a cons chain ending in one.
let recursive shapeIsFreshList (expression: Expr) =
    match shapeUnspan(expression) with
        | ExprList(_elements, _isMultiline) -> true
        | ExprCons(_head, tail) -> shapeIsFreshList(tail)
        | _ -> false

let shapeOfArgument (argument: Expr) (index: Int) (shadowed: List(Str)) (tailOwners: List((Str, Int))) (parameters: List(Str)) =
    match shapeUnspan(argument) with
        | ExprVar(name) ->
            if shapeResolveParameter(name)(shadowed)(parameters) == Some(index)
            then TcoPassThroughShape
            else
                if shapeLookupTailOwner(name)(tailOwners) == Some(index)
                then TcoConsumedTailShape
                else TcoOtherShape
        | ExprCons(_head, tail) ->
            match shapeUnspan(tail) with
                | ExprVar(name) ->
                    if shapeResolveParameter(name)(shadowed)(parameters) == Some(index)
                    then TcoGrownConsShape
                    else TcoOtherShape
                | rest ->
                    if shapeIsFreshList(rest)
                    then TcoFreshListShape
                    else TcoOtherShape
        | ExprList(_elements, _isMultiline) -> TcoFreshListShape
        | _ -> TcoOtherShape

let recursive shapeObserveArguments (arguments: List(Expr)) (index: Int) (shadowed: List(Str)) (tailOwners: List((Str, Int))) (parameters: List(Str)) (observed: List(Maybe(TcoArgumentShape))) =
    match (arguments, observed) with
        | (argument :: restArguments, current :: restObserved) ->
            shapeMerge(current)(shapeOfArgument(argument)(index)(shadowed)(tailOwners)(parameters)) :: shapeObserveArguments(restArguments)(index + 1)(shadowed)(tailOwners)(parameters)(restObserved)
        | _ -> observed

let recursive shapeCallSpine (expression: Expr) (arguments: List(Expr)) =
    match expression with
        | ExprAt(_span, inner) -> shapeCallSpine(inner)(arguments)
        | ExprCall(function, argument, _sugar, _layout) -> shapeCallSpine(function)(argument :: arguments)
        | root -> (root, arguments)

type ShapeWalk =
    | observed: List(Maybe(TcoArgumentShape))
    | sawSelfCall: Bool

let recursive shapeWalk (self: Str) (parameters: List(Str)) (expression: Expr) (shadowed: List(Str)) (tailOwners: List((Str, Int))) (walk: ShapeWalk) =
    match expression with
        | ExprAt(_span, inner) -> shapeWalk(self)(parameters)(inner)(shadowed)(tailOwners)(walk)
        | ExprIf(_condition, thenBranch, elseBranch) ->
            walk
            |> shapeWalk(self)(parameters)(thenBranch)(shadowed)(tailOwners)
            |> shapeWalk(self)(parameters)(elseBranch)(shadowed)(tailOwners)
        | ExprMatch(value, cases, _position) ->
            shapeWalkCases(self)(parameters)(shapeScrutineeOrdinal(value)(shadowed)(parameters))(cases)(shadowed)(tailOwners)(walk)
        | ExprLet(name, _value, body, _parameters, _annotation, _requirements) ->
            shapeWalk(self)(parameters)(body)(name :: shadowed)(shapeRemoveTailOwners([name])(tailOwners))(walk)
        | ExprLetResult(name, _value, body) ->
            shapeWalk(self)(parameters)(body)(name :: shadowed)(shapeRemoveTailOwners([name])(tailOwners))(walk)
        | ExprLetRecursive(name, _value, body, _parameters, _annotation, _requirements) ->
            shapeWalk(self)(parameters)(body)(name :: shadowed)(shapeRemoveTailOwners([name])(tailOwners))(walk)
        | ExprCall(_function, _argument, _sugar, _layout) -> shapeWalkCall(self)(parameters)(expression)(shadowed)(tailOwners)(walk)
        | _ -> walk
and shapeWalkCases (self: Str) (parameters: List(Str)) (scrutinee: Maybe(Int)) (cases: List((Pattern, Expr, Maybe(Expr)))) (shadowed: List(Str)) (tailOwners: List((Str, Int))) (walk: ShapeWalk) =
    match cases with
        | [] -> walk
        | (pattern, body, _guard) :: rest ->
            walk
            |> shapeWalk(self)(parameters)(body)(append(shapePatternNames(pattern)([]))(shadowed))(tailOwners
            |> shapeRemoveTailOwners(shapePatternNames(pattern)([]))
            |> append(shapeCaseTailOwners(pattern)(scrutinee)))
            |> shapeWalkCases(self)(parameters)(scrutinee)(rest)(shadowed)(tailOwners)
and shapeWalkCall (self: Str) (parameters: List(Str)) (expression: Expr) (shadowed: List(Str)) (tailOwners: List((Str, Int))) (walk: ShapeWalk) =
    match shapeCallSpine(expression)([]) with
        | (ExprVar(callee), arguments) ->
            if callee == self && !shapeContainsName(self)(shadowed) && length(arguments) == length(parameters)
            then ShapeWalk(observed = shapeObserveArguments(arguments)(0)(shadowed)(tailOwners)(parameters)(walk.observed), sawSelfCall = true)
            else walk
        | _ -> walk

let recursive shapeInitial (count: Int) =
    if count == 0
    then []
    else None :: shapeInitial(count - 1)

let recursive shapeResolveObserved (observed: List(Maybe(TcoArgumentShape))) =
    match observed with
        | [] -> []
        | Some(shape) :: rest -> shape :: shapeResolveObserved(rest)
        | None :: rest -> TcoOtherShape :: shapeResolveObserved(rest)

let recursive shapeAllOther (count: Int) =
    if count == 0
    then []
    else TcoOtherShape :: shapeAllOther(count - 1)

// The shape of every parameter position of the loop function `self` over `parameters` (the whole
// curried chain, in order) with the innermost body `body`; every position is `TcoOtherShape` when
// the body has no exact tail self-call.
let tcoSelfCallShapes (self: Str) (parameters: List(Str)) (body: Expr) =
    match shapeWalk(self)(parameters)(body)([])([])(ShapeWalk(observed = parameters
    |> length
    |> shapeInitial, sawSelfCall = false)) with
        | ShapeWalk { observed = observed, sawSelfCall = true } -> shapeResolveObserved(observed)
        | _ ->
            parameters
            |> length
            |> shapeAllOther

// The shapes under which a list-typed parameter may live on the reference-counted heap: grown by
// a cons per iteration, or consumed through its own pattern-bound tail.
let isTcoListShape (shape: TcoArgumentShape) =
    match shape with
        | TcoGrownConsShape -> true
        | TcoConsumedTailShape -> true
        | _ -> false
