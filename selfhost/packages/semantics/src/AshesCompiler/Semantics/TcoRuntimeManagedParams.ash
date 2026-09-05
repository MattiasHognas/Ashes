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
    value escapingConsumedHeadOrdinals,
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
                | _ -> TcoOtherShape
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
// The builtin modules whose functions read their arguments without keeping them: a value
// handed to one is borrowed for the call and never reachable afterwards.
let borrowingBuiltinModule (moduleName: Str) = moduleName == "Ashes.Text" || moduleName == "Ashes.IO" || moduleName == "Ashes.Rune" || moduleName == "Ashes.Number.Int" || moduleName == "Ashes.Number.Float" || moduleName == "Ashes.Number.UInt" || moduleName == "Ashes.Number.BigInt" || moduleName == "Ashes.Number.Math"

let recursive borrowedCallee (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> borrowedCallee(inner)
        | ExprQualifiedVar(moduleName, _member) -> borrowingBuiltinModule(moduleName)
        | ExprCall(function, _argument, _sugar, _layout) -> borrowedCallee(function)
        | _ -> false

let recursive shapeRemoveName (name: Str) (names: List(Str)) =
    match names with
        | [] -> []
        | candidate :: rest ->
            if candidate == name
            then shapeRemoveName(name)(rest)
            else candidate :: shapeRemoveName(name)(rest)

let recursive shapeRemoveNames (removed: List(Str)) (names: List(Str)) =
    match removed with
        | [] -> names
        | name :: rest ->
            names
            |> shapeRemoveName(name)
            |> shapeRemoveNames(rest)

// Whether `expression` mentions any of `names`, through every construct, counting a field read
// of the name too.
let recursive borrowedNamesMentioned (names: List(Str)) (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> borrowedNamesMentioned(names)(inner)
        | ExprVar(name) -> shapeContainsName(name)(names)
        | ExprQualifiedVar(moduleName, _member) -> shapeContainsName(moduleName)(names)
        | ExprInt(_value) -> false
        | ExprBigInt(_value) -> false
        | ExprUInt(_value, _bits, _text) -> false
        | ExprFloat(_value, _text) -> false
        | ExprString(_value) -> false
        | ExprRune(_value) -> false
        | ExprBool(_value) -> false
        | ExprAdd(left, right) -> borrowedNamesMentionedEither(names)(left)(right)
        | ExprSubtract(left, right) -> borrowedNamesMentionedEither(names)(left)(right)
        | ExprMultiply(left, right) -> borrowedNamesMentionedEither(names)(left)(right)
        | ExprDivide(left, right) -> borrowedNamesMentionedEither(names)(left)(right)
        | ExprModulo(left, right) -> borrowedNamesMentionedEither(names)(left)(right)
        | ExprEqual(left, right) -> borrowedNamesMentionedEither(names)(left)(right)
        | ExprNotEqual(left, right) -> borrowedNamesMentionedEither(names)(left)(right)
        | ExprGreaterThan(left, right) -> borrowedNamesMentionedEither(names)(left)(right)
        | ExprLessThan(left, right) -> borrowedNamesMentionedEither(names)(left)(right)
        | ExprGreaterOrEqual(left, right) -> borrowedNamesMentionedEither(names)(left)(right)
        | ExprLessOrEqual(left, right) -> borrowedNamesMentionedEither(names)(left)(right)
        | ExprLogicalAnd(left, right) -> borrowedNamesMentionedEither(names)(left)(right)
        | ExprLogicalOr(left, right) -> borrowedNamesMentionedEither(names)(left)(right)
        | ExprLogicalNot(operand) -> borrowedNamesMentioned(names)(operand)
        | ExprIf(condition, thenBranch, elseBranch) -> borrowedNamesMentioned(names)(condition) || borrowedNamesMentionedEither(names)(thenBranch)(elseBranch)
        | ExprCall(function, argument, _sugar, _layout) -> borrowedNamesMentionedEither(names)(function)(argument)
        | ExprCons(head, tail) -> borrowedNamesMentionedEither(names)(head)(tail)
        | ExprTuple(elements) -> borrowedNamesMentionedAny(names)(elements)
        | ExprList(elements, _isMultiline) -> borrowedNamesMentionedAny(names)(elements)
        | ExprLet(_name, value, body, _parameters, _annotation, _requirements) -> borrowedNamesMentionedEither(names)(value)(body)
        | ExprLetResult(_name, value, body) -> borrowedNamesMentionedEither(names)(value)(body)
        | ExprLetRecursive(_name, value, body, _parameters, _annotation, _requirements) -> borrowedNamesMentionedEither(names)(value)(body)
        | ExprLambda(_parameter, body, _annotation) -> borrowedNamesMentioned(names)(body)
        | ExprMatch(value, cases, _position) -> borrowedNamesMentioned(names)(value) || borrowedNamesMentionedCases(names)(cases)
        | ExprRecord(_constructorName, fields, _isMultiline) -> borrowedNamesMentionedFields(names)(fields)
        | ExprRecordUpdate(target, fields) -> borrowedNamesMentioned(names)(target) || borrowedNamesMentionedFields(names)(fields)
        | _ -> true
and borrowedNamesMentionedEither (names: List(Str)) (left: Expr) (right: Expr) = borrowedNamesMentioned(names)(left) || borrowedNamesMentioned(names)(right)
and borrowedNamesMentionedAny (names: List(Str)) (expressions: List(Expr)) =
    match expressions with
        | [] -> false
        | expression :: rest -> borrowedNamesMentioned(names)(expression) || borrowedNamesMentionedAny(names)(rest)
and borrowedNamesMentionedFields (names: List(Str)) (fields: List((Str, Expr))) =
    match fields with
        | [] -> false
        | (_fieldName, expression) :: rest -> borrowedNamesMentioned(names)(expression) || borrowedNamesMentionedFields(names)(rest)
and borrowedNamesMentionedCases (names: List(Str)) (cases: List((Pattern, Expr, Maybe(Expr)))) =
    match cases with
        | [] -> false
        | (_pattern, body, guard) :: rest -> borrowedNamesMentioned(names)(body) || borrowedNamesMentionedGuard(names)(guard) || borrowedNamesMentionedCases(names)(rest)
and borrowedNamesMentionedGuard (names: List(Str)) (guard: Maybe(Expr)) =
    match guard with
        | Some(expression) -> borrowedNamesMentioned(names)(expression)
        | None -> false

// Whether every use of `names` in `expression` only borrows the value for the duration of a
// read: an operator operand, a scrutinee, a condition, a field read, or an argument of a
// borrowing builtin (`borrowed` says whether the expression itself sits in such a position). A
// bare read anywhere else — a self-call or user-function argument, a cons head, an aggregate
// element, a `let` value, a returned value, a closure capture — lets the value outlive the arm,
// and so does any construct not listed here that mentions a name.
let recursive namesBorrowedOnly (names: List(Str)) (borrowed: Bool) (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> namesBorrowedOnly(names)(borrowed)(inner)
        | ExprVar(name) -> borrowed || !shapeContainsName(name)(names)
        | ExprQualifiedVar(moduleName, _member) -> borrowed || !shapeContainsName(moduleName)(names)
        | ExprAdd(left, right) -> namesBorrowedOnlyBoth(names)(left)(right)
        | ExprSubtract(left, right) -> namesBorrowedOnlyBoth(names)(left)(right)
        | ExprMultiply(left, right) -> namesBorrowedOnlyBoth(names)(left)(right)
        | ExprDivide(left, right) -> namesBorrowedOnlyBoth(names)(left)(right)
        | ExprModulo(left, right) -> namesBorrowedOnlyBoth(names)(left)(right)
        | ExprEqual(left, right) -> namesBorrowedOnlyBoth(names)(left)(right)
        | ExprNotEqual(left, right) -> namesBorrowedOnlyBoth(names)(left)(right)
        | ExprGreaterThan(left, right) -> namesBorrowedOnlyBoth(names)(left)(right)
        | ExprLessThan(left, right) -> namesBorrowedOnlyBoth(names)(left)(right)
        | ExprGreaterOrEqual(left, right) -> namesBorrowedOnlyBoth(names)(left)(right)
        | ExprLessOrEqual(left, right) -> namesBorrowedOnlyBoth(names)(left)(right)
        | ExprLogicalAnd(left, right) -> namesBorrowedOnlyBoth(names)(left)(right)
        | ExprLogicalOr(left, right) -> namesBorrowedOnlyBoth(names)(left)(right)
        | ExprLogicalNot(operand) -> namesBorrowedOnly(names)(true)(operand)
        | ExprIf(condition, thenBranch, elseBranch) -> namesBorrowedOnly(names)(true)(condition) && namesBorrowedOnly(names)(borrowed)(thenBranch) && namesBorrowedOnly(names)(borrowed)(elseBranch)
        | ExprCall(function, argument, _sugar, _layout) ->
            if borrowedCallee(function)
            then namesBorrowedOnly(names)(true)(function) && namesBorrowedOnly(names)(true)(argument)
            else namesBorrowedOnly(names)(false)(function) && namesBorrowedOnly(names)(false)(argument)
        | ExprMatch(value, cases, _position) -> namesBorrowedOnly(names)(true)(value) && namesBorrowedOnlyCases(names)(borrowed)(cases)
        | ExprLet(name, value, body, _parameters, _annotation, _requirements) ->
            namesBorrowedOnly(names)(false)(value) && namesBorrowedOnly(shapeRemoveName(name)(names))(borrowed)(body)
        | ExprLetResult(name, value, body) ->
            namesBorrowedOnly(names)(false)(value) && namesBorrowedOnly(shapeRemoveName(name)(names))(borrowed)(body)
        | ExprLetRecursive(name, value, body, _parameters, _annotation, _requirements) ->
            namesBorrowedOnly(names)(false)(value) && namesBorrowedOnly(shapeRemoveName(name)(names))(borrowed)(body)
        | ExprLambda(_parameter, body, _annotation) -> !borrowedNamesMentioned(names)(body)
        | ExprCons(head, tail) -> namesBorrowedOnly(names)(false)(head) && namesBorrowedOnly(names)(false)(tail)
        | ExprTuple(elements) -> namesBorrowedOnlyAll(names)(elements)
        | ExprList(elements, _isMultiline) -> namesBorrowedOnlyAll(names)(elements)
        | ExprRecord(_constructorName, fields, _isMultiline) -> namesBorrowedOnlyFields(names)(fields)
        | ExprRecordUpdate(target, fields) -> namesBorrowedOnly(names)(false)(target) && namesBorrowedOnlyFields(names)(fields)
        | other -> !borrowedNamesMentioned(names)(other)
and namesBorrowedOnlyBoth (names: List(Str)) (left: Expr) (right: Expr) = namesBorrowedOnly(names)(true)(left) && namesBorrowedOnly(names)(true)(right)
and namesBorrowedOnlyAll (names: List(Str)) (expressions: List(Expr)) =
    match expressions with
        | [] -> true
        | expression :: rest -> namesBorrowedOnly(names)(false)(expression) && namesBorrowedOnlyAll(names)(rest)
and namesBorrowedOnlyFields (names: List(Str)) (fields: List((Str, Expr))) =
    match fields with
        | [] -> true
        | (_fieldName, expression) :: rest -> namesBorrowedOnly(names)(false)(expression) && namesBorrowedOnlyFields(names)(rest)
and namesBorrowedOnlyCases (names: List(Str)) (borrowed: Bool) (cases: List((Pattern, Expr, Maybe(Expr)))) =
    match cases with
        | [] -> true
        | (pattern, body, guard) :: rest ->
            namesBorrowedOnlyGuard(shapeRemoveNames(shapePatternNames(pattern)([]))(names))(guard) && namesBorrowedOnly(shapeRemoveNames(shapePatternNames(pattern)([]))(names))(borrowed)(body) && namesBorrowedOnlyCases(names)(borrowed)(rest)
and namesBorrowedOnlyGuard (names: List(Str)) (guard: Maybe(Expr)) =
    match guard with
        | Some(expression) -> namesBorrowedOnly(names)(true)(expression)
        | None -> true

// The names a cons pattern binds out of the matched cell's head.
let consumedHeadNames (pattern: Pattern) =
    match shapeUnspanPattern(pattern) with
        | PatternCons(head, _tail) -> shapePatternNames(head)([])
        | _ -> []

// Whether every `match` on the parameter `ordinal` anywhere in `expression` keeps the names its
// cons patterns bind out of the matched cell's head borrowed within the arm: the cell is freed
// once the loop moves on to its tail, so a head that outlived its arm would be read freed.
let recursive consumedHeadsBorrowed (ordinal: Int) (parameters: List(Str)) (shadowed: List(Str)) (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> consumedHeadsBorrowed(ordinal)(parameters)(shadowed)(inner)
        | ExprMatch(value, cases, _position) -> consumedHeadsBorrowed(ordinal)(parameters)(shadowed)(value) && consumedHeadsBorrowedCases(ordinal)(parameters)(shadowed)(shapeScrutineeOrdinal(value)(shadowed)(parameters) == Some(ordinal))(cases)
        | ExprIf(condition, thenBranch, elseBranch) -> consumedHeadsBorrowed(ordinal)(parameters)(shadowed)(condition) && consumedHeadsBorrowed(ordinal)(parameters)(shadowed)(thenBranch) && consumedHeadsBorrowed(ordinal)(parameters)(shadowed)(elseBranch)
        | ExprLet(name, value, body, _parameters, _annotation, _requirements) -> consumedHeadsBorrowed(ordinal)(parameters)(shadowed)(value) && consumedHeadsBorrowed(ordinal)(parameters)(name :: shadowed)(body)
        | ExprLetResult(name, value, body) -> consumedHeadsBorrowed(ordinal)(parameters)(shadowed)(value) && consumedHeadsBorrowed(ordinal)(parameters)(name :: shadowed)(body)
        | ExprLetRecursive(name, value, body, _parameters, _annotation, _requirements) -> consumedHeadsBorrowed(ordinal)(parameters)(shadowed)(value) && consumedHeadsBorrowed(ordinal)(parameters)(name :: shadowed)(body)
        | ExprCall(function, argument, _sugar, _layout) -> consumedHeadsBorrowed(ordinal)(parameters)(shadowed)(function) && consumedHeadsBorrowed(ordinal)(parameters)(shadowed)(argument)
        | ExprLambda(parameter, body, _annotation) -> consumedHeadsBorrowed(ordinal)(parameters)(parameter :: shadowed)(body)
        | ExprCons(head, tail) -> consumedHeadsBorrowed(ordinal)(parameters)(shadowed)(head) && consumedHeadsBorrowed(ordinal)(parameters)(shadowed)(tail)
        | ExprAdd(left, right) -> consumedHeadsBorrowed(ordinal)(parameters)(shadowed)(left) && consumedHeadsBorrowed(ordinal)(parameters)(shadowed)(right)
        | ExprTuple(elements) -> consumedHeadsBorrowedAll(ordinal)(parameters)(shadowed)(elements)
        | ExprList(elements, _isMultiline) -> consumedHeadsBorrowedAll(ordinal)(parameters)(shadowed)(elements)
        | ExprRecord(_constructorName, fields, _isMultiline) -> consumedHeadsBorrowedFields(ordinal)(parameters)(shadowed)(fields)
        | _ -> true
and consumedHeadsBorrowedAll (ordinal: Int) (parameters: List(Str)) (shadowed: List(Str)) (expressions: List(Expr)) =
    match expressions with
        | [] -> true
        | expression :: rest -> consumedHeadsBorrowed(ordinal)(parameters)(shadowed)(expression) && consumedHeadsBorrowedAll(ordinal)(parameters)(shadowed)(rest)
and consumedHeadsBorrowedFields (ordinal: Int) (parameters: List(Str)) (shadowed: List(Str)) (fields: List((Str, Expr))) =
    match fields with
        | [] -> true
        | (_fieldName, expression) :: rest -> consumedHeadsBorrowed(ordinal)(parameters)(shadowed)(expression) && consumedHeadsBorrowedFields(ordinal)(parameters)(shadowed)(rest)
and consumedHeadsBorrowedCases (ordinal: Int) (parameters: List(Str)) (shadowed: List(Str)) (onParameter: Bool) (cases: List((Pattern, Expr, Maybe(Expr)))) =
    match cases with
        | [] -> true
        | (pattern, body, _guard) :: rest ->
            (!onParameter || namesBorrowedOnly(consumedHeadNames(pattern))(false)(body)) && consumedHeadsBorrowed(ordinal)(parameters)(append(shapePatternNames(pattern)([]))(shadowed))(body) && consumedHeadsBorrowedCases(ordinal)(parameters)(shadowed)(onParameter)(rest)

// The parameter positions of `shapes` (a consumed tail each) whose matched heads may outlive
// their arm. Such a list lives on the reference-counted heap only when its heads are protected
// by their pattern owners' retains; the placement decides that from the element type.
let recursive escapingConsumedHeadOrdinals (ordinal: Int) (parameters: List(Str)) (body: Expr) (shapes: List(TcoArgumentShape)) =
    match shapes with
        | [] -> []
        | TcoConsumedTailShape :: rest ->
            if consumedHeadsBorrowed(ordinal)(parameters)([])(body)
            then escapingConsumedHeadOrdinals(ordinal + 1)(parameters)(body)(rest)
            else ordinal :: escapingConsumedHeadOrdinals(ordinal + 1)(parameters)(body)(rest)
        | _shape :: rest -> escapingConsumedHeadOrdinals(ordinal + 1)(parameters)(body)(rest)

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
