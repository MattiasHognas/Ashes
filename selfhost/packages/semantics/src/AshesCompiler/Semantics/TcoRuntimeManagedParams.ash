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
import AshesCompiler.Semantics.ExprMentions.exprReadsName
import AshesCompiler.Semantics.TcoAffineAppend.affineSelfAppendOrdinals
// The positions every self-call hands on, conses onto, or replaces with a call's result or a part
// matched out of one, no single shape describing them, stage 0's `AccumulatorRebuild` there: a list
// of the contract's values at such a position is an accumulator.
export (
    type TcoArgumentShape(..),
    value runtimeManagedStrOrdinals,
    value shapeIsFreshList,
    value tcoSelfCallShapes,
    value tcoAccumulatorRebuildOrdinals,
    value isTcoListShape,
    value selfCallOrdinalPassing,
    value tcoSelfCallParameterFlow,
    value tcoSelfCallSpineFlow,
    value shapePatternNames,
    value flowSuccessorsNamingNoPartOf,
    value tcoOwnReferencePositions,
    value flowCallKeyOf,
    value flowNodes,
)

// The reference-ownership shape shared by every exact tail self-call argument at one parameter
// position, stage 0's `TcoSelfCallArgumentShape`: the parameter's own unchanged read, a cons cell
// whose tail is that read (the accumulator grows by one cell per iteration), a name bound as the
// cons-tail of a `match` on that parameter one level up (the parameter shrinks by one cell), a
// choice between handing the parameter on and consing onto it, the parameter's consumed tail at
// some self-calls and a call's result at others (a worklist walked and refilled), the parameter
// handed on at some self-calls and a fresh list at the others, a call's result among them (a list
// replaced by a helper's result in one arm) or not, an accumulator the self-calls hand on, cons
// onto, or replace with a call's result or a part matched out of one, or anything else — a
// different shape at some self-call, a fresh rebuild, or a value the walk does not classify.
type TcoArgumentShape =
    | TcoPassThroughShape
    | TcoGrownConsShape
    | TcoConsumedTailShape
    | TcoFreshListShape
    | TcoChoiceAccumulatorShape
    | TcoWalkedOrRefilledShape
    | TcoRefilledFreshListShape
    | TcoHandedOnFreshListShape
    | TcoAccumulatorRebuildShape
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
// Stage 0's `IsArenaSelfContainedListRebuildExpr`: a tail-call argument that rebuilds its list this
// iteration. A call result counts, because a function's list result is copied out of the callee's
// arena scope on return and so shares nothing with the previous iteration.
let recursive shapeIsFreshList (expression: Expr) =
    match shapeUnspan(expression) with
        | ExprList(_elements, _isMultiline) -> true
        | ExprCall(_function, _argument, _whitespace, _layout) -> true
        | ExprCons(_head, tail) -> shapeIsFreshList(tail)
        | ExprLet(_name, _value, body, _quantified, _annotation, _constraints) -> shapeIsFreshList(body)
        | _ -> false

// Stage 0's `GrownConsChainTail`: the last tail of a chain of cons cells written out
// (`first :: separator :: acc`), the list the chain is consed onto.
let recursive shapeConsChainTail (expression: Expr) =
    match shapeUnspan(expression) with
        | ExprCons(_head, tail) -> shapeConsChainTail(tail)
        | other -> other

// Stage 0's `ReadsLoopParameter`: a head that is a sibling parameter, or is built from one, is
// released with that parameter's predecessor at the back edge; the accumulator would be left
// holding it.
let recursive shapeReadsLoopParameter (expression: Expr) (shadowed: List(Str)) (remaining: List(Str)) =
    match remaining with
        | [] -> false
        | parameter :: rest -> !shapeContainsName(parameter)(shadowed) && exprReadsName(parameter)(expression) || shapeReadsLoopParameter(expression)(shadowed)(rest)

// A chain grows the accumulator only when none of its heads reads a loop parameter, stage 0's
// condition in `ClassifyTcoSelfCallArgument`.
let recursive shapeChainHeadsReadNoParameter (expression: Expr) (shadowed: List(Str)) (parameters: List(Str)) =
    match shapeUnspan(expression) with
        | ExprCons(head, tail) -> !shapeReadsLoopParameter(head)(shadowed)(parameters) && shapeChainHeadsReadNoParameter(tail)(shadowed)(parameters)
        | _ -> true

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
                | ExprCons(_inner, innerTail) ->
                    match shapeConsChainTail(innerTail) with
                        | ExprVar(name) ->
                            if shapeResolveParameter(name)(shadowed)(parameters) == Some(index) && shapeChainHeadsReadNoParameter(argument)(shadowed)(parameters)
                            then TcoGrownConsShape
                            else TcoOtherShape
                        | last ->
                            if shapeIsFreshList(last)
                            then TcoFreshListShape
                            else TcoOtherShape
                | rest ->
                    if shapeIsFreshList(rest)
                    then TcoFreshListShape
                    else TcoOtherShape
        | ExprList(_elements, _isMultiline) -> TcoFreshListShape
        | ExprCall(_function, _argument, _whitespace, _layout) -> TcoFreshListShape
        | ExprLet(_name, _value, body, _quantified, _annotation, _constraints) ->
            if shapeIsFreshList(body)
            then TcoFreshListShape
            else TcoOtherShape
        | _ -> TcoOtherShape

let recursive shapeObserveArguments (arguments: List(Expr)) (index: Int) (shadowed: List(Str)) (tailOwners: List((Str, Int))) (parameters: List(Str)) (observed: List(Maybe(TcoArgumentShape))) =
    match (arguments, observed) with
        | (argument :: restArguments, current :: restObserved) ->
            shapeMerge(current)(shapeOfArgument(argument)(index)(shadowed)(tailOwners)(parameters)) :: shapeObserveArguments(restArguments)(index + 1)(shadowed)(tailOwners)(parameters)(restObserved)
        | _ -> observed

let recursive shapeElementsReadNoLoopParameter (elements: List(Expr)) (shadowed: List(Str)) (parameters: List(Str)) =
    match elements with
        | [] -> true
        | element :: rest -> !shapeReadsLoopParameter(element)(shadowed)(parameters) && shapeElementsReadNoLoopParameter(rest)(shadowed)(parameters)

// Stage 0's `IsAccumulatorEdgeArm` and `IsBranchingAccumulatorEdge`: an arm hands the parameter at
// `index` on, conses onto it a head that reads no loop parameter, or replaces it with a fresh list
// none of whose elements reads one (nil resets the accumulator), and a choice between such arms
// (an `if`, or a `match` none of whose patterns rebinds the parameter's name) is an accumulator
// edge itself.
let recursive shapeIsAccumulatorArm (arm: Expr) (index: Int) (name: Str) (shadowed: List(Str)) (parameters: List(Str)) =
    match shapeUnspan(arm) with
        | ExprVar(passed) -> shapeResolveParameter(passed)(shadowed)(parameters) == Some(index)
        | ExprCons(head, tail) ->
            match shapeUnspan(tail) with
                | ExprVar(tailName) -> shapeResolveParameter(tailName)(shadowed)(parameters) == Some(index) && !shapeReadsLoopParameter(head)(shadowed)(parameters)
                | _ -> false
        | ExprList(elements, _isMultiline) -> shapeElementsReadNoLoopParameter(elements)(shadowed)(parameters)
        | other -> shapeIsChoiceEdge(other)(index)(name)(shadowed)(parameters)
and shapeIsChoiceEdge (expression: Expr) (index: Int) (name: Str) (shadowed: List(Str)) (parameters: List(Str)) =
    match shapeUnspan(expression) with
        | ExprIf(_condition, thenBranch, elseBranch) -> shapeIsAccumulatorArm(thenBranch)(index)(name)(shadowed)(parameters) && shapeIsAccumulatorArm(elseBranch)(index)(name)(shadowed)(parameters)
        | ExprMatch(_value, cases, _position) -> shapeCasesAccumulate(cases)(index)(name)(shadowed)(parameters)
        | _ -> false
and shapeCasesAccumulate (cases: List((Pattern, Expr, Maybe(Expr)))) (index: Int) (name: Str) (shadowed: List(Str)) (parameters: List(Str)) =
    match cases with
        | [] -> true
        | (pattern, body, _guard) :: rest -> !patternBindsName(name)(pattern) && shapeIsAccumulatorArm(body)(index)(name)(shadowed)(parameters) && shapeCasesAccumulate(rest)(index)(name)(shadowed)(parameters)

let recursive shapeLookupLetValue (name: Str) (letValues: List((Str, Expr))) =
    match letValues with
        | [] -> None
        | (candidate, value) :: rest ->
            if candidate == name
            then Some(value)
            else shapeLookupLetValue(name)(rest)

let recursive shapeRemoveLetValues (names: List(Str)) (letValues: List((Str, Expr))) =
    match letValues with
        | [] -> []
        | (candidate, value) :: rest ->
            if shapeContainsName(candidate)(names)
            then shapeRemoveLetValues(names)(rest)
            else (candidate, value) :: shapeRemoveLetValues(names)(rest)

// What a self-call argument was built as: a let-bound name stands for its bound value, unless the
// name is a parameter or a consumed tail, which are classified by the binding they refer to.
let shapeStructuralArgument (argument: Expr) (shadowed: List(Str)) (tailOwners: List((Str, Int))) (letValues: List((Str, Expr))) (parameters: List(Str)) =
    match shapeUnspan(argument) with
        | ExprVar(name) ->
            if shapeResolveParameter(name)(shadowed)(parameters) != None || shapeLookupTailOwner(name)(tailOwners) != None
            then argument
            else
                match shapeLookupLetValue(name)(letValues) with
                    | Some(value) -> value
                    | None -> argument
        | _ -> argument

// A site that hands the parameter at `index` on as it is or conses onto it a head that reads no
// loop parameter, stage 0's `HandedOnOrGrownEdges`; a fresh list or a same-named rebinding is
// neither.
let recursive shapeIsGrownCons (structural: Expr) (index: Int) (shadowed: List(Str)) (parameters: List(Str)) =
    match shapeUnspan(structural) with
        | ExprCons(head, tail) ->
            match shapeUnspan(tail) with
                | ExprVar(tailName) -> shapeResolveParameter(tailName)(shadowed)(parameters) == Some(index) && !shapeReadsLoopParameter(head)(shadowed)(parameters)
                | ExprCons(_inner, _innerTail) -> !shapeReadsLoopParameter(head)(shadowed)(parameters) && shapeIsGrownCons(tail)(index)(shadowed)(parameters)
                | _ -> false
        | _ -> false

// An empty list literal empties the parameter, which holds nothing older afterwards.
let shapeIsHandedOnOrGrown (argument: Expr) (structural: Expr) (index: Int) (shadowed: List(Str)) (parameters: List(Str)) =
    match (shapeUnspan(argument), shapeUnspan(structural)) with
        | (_argument, ExprList([], _isMultiline)) -> true
        | (ExprVar(name), _structural) ->
            if shapeResolveParameter(name)(shadowed)(parameters) == Some(index)
            then true
            else shapeIsGrownCons(structural)(index)(shadowed)(parameters)
        | _ -> shapeIsGrownCons(structural)(index)(shadowed)(parameters)

// Per parameter position, whether every self-call so far passed an accumulator edge, whether any
// of them was a choice, and whether every one handed the parameter on or consed onto it, stage 0's
// `ChoiceAccumulatorEdges`, `AnyChoiceEdge` and `HandedOnOrGrownEdges`.
let recursive shapeObserveChoices (arguments: List(Expr)) (index: Int) (names: List(Str)) (shadowed: List(Str)) (tailOwners: List((Str, Int))) (letValues: List((Str, Expr))) (parameters: List(Str)) (choices: List((Bool, Bool, Bool))) =
    match (arguments, names, choices) with
        | (argument :: restArguments, name :: restNames, (everyEdge, anyChoice, everyHandedOnOrGrown) :: restChoices) ->
            (let structural = shapeStructuralArgument(argument)(shadowed)(tailOwners)(letValues)(parameters)
            in
                let choiceEdge = shapeIsChoiceEdge(structural)(index)(name)(shadowed)(parameters)
                in (everyEdge && (choiceEdge || shapeIsAccumulatorArm(structural)(index)(name)(shadowed)(parameters)), anyChoice || choiceEdge, everyHandedOnOrGrown && shapeIsHandedOnOrGrown(argument)(structural)(index)(shadowed)(parameters))) :: shapeObserveChoices(restArguments)(index + 1)(restNames)(shadowed)(tailOwners)(letValues)(parameters)(restChoices)
        | _ -> choices

let recursive shapeCallSpine (expression: Expr) (arguments: List(Expr)) =
    match expression with
        | ExprAt(_span, inner) -> shapeCallSpine(inner)(arguments)
        | ExprCall(function, argument, _sugar, _layout) -> shapeCallSpine(function)(argument :: arguments)
        | root -> (root, arguments)

// An empty list literal, the edge that empties a list refilled at other edges.
let shapeIsEmptyList (expression: Expr) =
    match shapeUnspan(expression) with
        | ExprList([], _isMultiline) -> true
        | _ -> false

// Per parameter position, whether every self-call so far passed the parameter's consumed tail, the
// parameter itself, a call's result, or an empty list, whether one passed the consumed tail,
// whether one passed a call's result, and whether every one passed the parameter itself or a fresh
// list, stage 0's `WalkOrRefillEdges`, `AnyWalkEdge`, `AnyRefillEdge` and
// `FreshOrHandedOnListRebuild`.
let recursive shapeObserveWalks (arguments: List(Expr)) (index: Int) (shadowed: List(Str)) (tailOwners: List((Str, Int))) (letValues: List((Str, Expr))) (parameters: List(Str)) (walks: List((Bool, Bool, Bool, Bool))) =
    match (arguments, walks) with
        | (argument :: restArguments, (everyEdge, anyWalk, anyRefill, everyFreshOrHandedOn) :: restWalks) ->
            (let local = shapeOfArgument(argument)(index)(shadowed)(tailOwners)(parameters)
            in
                let refill =
                    local != TcoConsumedTailShape && local != TcoPassThroughShape && (match parameters
                    |> shapeStructuralArgument(argument)(shadowed)(tailOwners)(letValues)
                    |> shapeUnspan with
                        | ExprCall(_function, _argument, _sugar, _layout) -> true
                        | _ -> false)
                in
                    let walk = local == TcoConsumedTailShape
                    in
                        let emptied =
                            parameters
                            |> shapeStructuralArgument(argument)(shadowed)(tailOwners)(letValues)
                            |> shapeIsEmptyList
                        in
                            let fresh =
                                parameters
                                |> shapeStructuralArgument(argument)(shadowed)(tailOwners)(letValues)
                                |> shapeIsFreshList
                            in (everyEdge && (walk || refill || emptied || local == TcoPassThroughShape), anyWalk || walk, anyRefill || refill, everyFreshOrHandedOn && (fresh || local == TcoPassThroughShape))) :: shapeObserveWalks(restArguments)(index + 1)(shadowed)(tailOwners)(letValues)(parameters)(restWalks)
        | _ -> walks

// Per parameter position, whether every self-call so far passed an accumulator edge (the parameter
// itself, a cons onto it, a call's result, or a name a match bound out of a call's result) and
// whether any of them rebuilt the parameter, stage 0's `AccumulatorEdges` and `AnyRebuildEdge`.
let recursive shapeObserveAccumulators (arguments: List(Expr)) (index: Int) (shadowed: List(Str)) (tailOwners: List((Str, Int))) (letValues: List((Str, Expr))) (parameters: List(Str)) (callParts: List(Str)) (accumulators: List((Bool, Bool))) =
    match (arguments, accumulators) with
        | (argument :: restArguments, (everyEdge, anyRebuild) :: restAccumulators) ->
            (let local = shapeOfArgument(argument)(index)(shadowed)(tailOwners)(parameters)
            in
                let passesParameterItself =
                    match shapeUnspan(argument) with
                        | ExprVar(name) -> shapeResolveParameter(name)(shadowed)(parameters) != None || shapeLookupTailOwner(name)(tailOwners) != None
                        | _ -> false
                in
                    let edge =
                        local == TcoPassThroughShape || local == TcoGrownConsShape || local != TcoConsumedTailShape && (match parameters
                        |> shapeStructuralArgument(argument)(shadowed)(tailOwners)(letValues)
                        |> shapeUnspan with
                            | ExprCall(_function, _argument, _sugar, _layout) -> true
                            | ExprVar(part) -> !passesParameterItself && shapeContainsName(part)(callParts)
                            | _ -> false)
                    in (everyEdge && edge, anyRebuild || local != TcoPassThroughShape)) :: shapeObserveAccumulators(restArguments)(index + 1)(shadowed)(tailOwners)(letValues)(parameters)(callParts)(restAccumulators)
        | _ -> accumulators

type ShapeWalk =
    | observed: List(Maybe(TcoArgumentShape))
    | choices: List((Bool, Bool, Bool))
    | walks: List((Bool, Bool, Bool, Bool))
    | accumulators: List((Bool, Bool))
    | sawSelfCall: Bool

let recursive shapeWithoutNames (names: List(Str)) (parts: List(Str)) =
    match parts with
        | [] -> []
        | part :: rest ->
            if shapeContainsName(part)(names)
            then shapeWithoutNames(names)(rest)
            else part :: shapeWithoutNames(names)(rest)

// The call-result parts in an arm's body: the arm's own names are parts when the match is over a
// call's result, and shadow any part of the same name otherwise.
let shapeArmCallParts (scrutinee: Expr) (names: List(Str)) (callParts: List(Str)) =
    match shapeUnspan(scrutinee) with
        | ExprCall(_function, _argument, _sugar, _layout) ->
            callParts
            |> shapeWithoutNames(names)
            |> append(names)
        | _ -> shapeWithoutNames(names)(callParts)

let recursive shapeWalk (self: Str) (parameters: List(Str)) (expression: Expr) (shadowed: List(Str)) (tailOwners: List((Str, Int))) (letValues: List((Str, Expr))) (callParts: List(Str)) (walk: ShapeWalk) =
    match expression with
        | ExprAt(_span, inner) -> shapeWalk(self)(parameters)(inner)(shadowed)(tailOwners)(letValues)(callParts)(walk)
        | ExprIf(_condition, thenBranch, elseBranch) ->
            walk
            |> shapeWalk(self)(parameters)(thenBranch)(shadowed)(tailOwners)(letValues)(callParts)
            |> shapeWalk(self)(parameters)(elseBranch)(shadowed)(tailOwners)(letValues)(callParts)
        | ExprMatch(value, cases, _position) ->
            shapeWalkCases(self)(parameters)(value)(shapeScrutineeOrdinal(value)(shadowed)(parameters))(cases)(shadowed)(tailOwners)(letValues)(callParts)(walk)
        | ExprLet(name, value, body, [], _annotation, _requirements) ->
            shapeWalk(self)(parameters)(body)(name :: shadowed)(shapeRemoveTailOwners([name])(tailOwners))((name, value) :: letValues)(shapeWithoutNames([name])(callParts))(walk)
        | ExprLet(name, _value, body, _parameters, _annotation, _requirements) ->
            shapeWalk(self)(parameters)(body)(name :: shadowed)(shapeRemoveTailOwners([name])(tailOwners))(shapeRemoveLetValues([name])(letValues))(shapeWithoutNames([name])(callParts))(walk)
        | ExprLetResult(name, _value, body) ->
            shapeWalk(self)(parameters)(body)(name :: shadowed)(shapeRemoveTailOwners([name])(tailOwners))(shapeRemoveLetValues([name])(letValues))(shapeWithoutNames([name])(callParts))(walk)
        | ExprLetRecursive(name, _value, body, _parameters, _annotation, _requirements) ->
            shapeWalk(self)(parameters)(body)(name :: shadowed)(shapeRemoveTailOwners([name])(tailOwners))(shapeRemoveLetValues([name])(letValues))(shapeWithoutNames([name])(callParts))(walk)
        | ExprCall(_function, _argument, _sugar, _layout) -> shapeWalkCall(self)(parameters)(expression)(shadowed)(tailOwners)(letValues)(callParts)(walk)
        | _ -> walk
and shapeWalkCases (self: Str) (parameters: List(Str)) (value: Expr) (scrutinee: Maybe(Int)) (cases: List((Pattern, Expr, Maybe(Expr)))) (shadowed: List(Str)) (tailOwners: List((Str, Int))) (letValues: List((Str, Expr))) (callParts: List(Str)) (walk: ShapeWalk) =
    match cases with
        | [] -> walk
        | (pattern, body, _guard) :: rest ->
            walk
            |> shapeWalk(self)(parameters)(body)(append(shapePatternNames(pattern)([]))(shadowed))(tailOwners
            |> shapeRemoveTailOwners(shapePatternNames(pattern)([]))
            |> append(shapeCaseTailOwners(pattern)(scrutinee)))(shapeRemoveLetValues(shapePatternNames(pattern)([]))(letValues))(shapeArmCallParts(value)(shapePatternNames(pattern)([]))(callParts))
            |> shapeWalkCases(self)(parameters)(value)(scrutinee)(rest)(shadowed)(tailOwners)(letValues)(callParts)
and shapeWalkCall (self: Str) (parameters: List(Str)) (expression: Expr) (shadowed: List(Str)) (tailOwners: List((Str, Int))) (letValues: List((Str, Expr))) (callParts: List(Str)) (walk: ShapeWalk) =
    match shapeCallSpine(expression)([]) with
        | (ExprVar(callee), arguments) ->
            if callee == self && !shapeContainsName(self)(shadowed) && length(arguments) == length(parameters)
            then ShapeWalk(observed = shapeObserveArguments(arguments)(0)(shadowed)(tailOwners)(parameters)(walk.observed), choices = shapeObserveChoices(arguments)(0)(parameters)(shadowed)(tailOwners)(letValues)(parameters)(walk.choices), walks = shapeObserveWalks(arguments)(0)(shadowed)(tailOwners)(letValues)(parameters)(walk.walks), accumulators = shapeObserveAccumulators(arguments)(0)(shadowed)(tailOwners)(letValues)(parameters)(callParts)(walk.accumulators), sawSelfCall = true)
            else walk
        | _ -> walk

let recursive shapeInitial (count: Int) =
    if count == 0
    then []
    else None :: shapeInitial(count - 1)

let recursive shapeInitialChoices (count: Int) =
    if count == 0
    then []
    else (true, false, true) :: shapeInitialChoices(count - 1)

let recursive shapeInitialWalks (count: Int) =
    if count == 0
    then []
    else (true, false, false, true) :: shapeInitialWalks(count - 1)

// A position no single shape describes is a choice accumulator when every self-call passed an
// accumulator edge there (the parameter, a cons onto it, or a choice between those), stage 0's
// `ChoiceAccumulatorRebuild`: one self-call chooses between two, or every one hands the parameter
// on or conses onto it, some doing each; and a walked-or-refilled list when every self-call passed
// the parameter's consumed tail, the parameter itself, a call's result or an empty list, a call's
// result among them, stage 0's `WalkedOrRefilled`. One whose self-calls all hand the parameter on
// or pass a fresh list is stage 0's `FreshOrHandedOnListRebuild`, a fresh-list rebuild of a list
// of scalars; when those fresh lists include a call's result it is also walked-or-refilled, which
// decides for a list of strings.
let shapeResolveWalk (walk: (Bool, Bool, Bool, Bool)) =
    match walk with
        | (true, _anyWalk, true, true) -> TcoRefilledFreshListShape
        | (true, _anyWalk, true, false) -> TcoWalkedOrRefilledShape
        | (_everyWalk, _anyWalk, _anyRefill, true) -> TcoHandedOnFreshListShape
        | _ -> TcoOtherShape

let shapeResolveOther (choice: (Bool, Bool, Bool)) (walk: (Bool, Bool, Bool, Bool)) =
    match choice with
        | (true, anyChoice, everyHandedOnOrGrown) ->
            if anyChoice || everyHandedOnOrGrown
            then TcoChoiceAccumulatorShape
            else shapeResolveWalk(walk)
        | _ -> shapeResolveWalk(walk)

let recursive shapeResolveObserved (observed: List(Maybe(TcoArgumentShape))) (choices: List((Bool, Bool, Bool))) (walks: List((Bool, Bool, Bool, Bool))) =
    match (observed, choices, walks) with
        | (Some(TcoOtherShape) :: rest, choice :: restChoices, walk :: restWalks) -> shapeResolveOther(choice)(walk) :: shapeResolveObserved(rest)(restChoices)(restWalks)
        | (Some(shape) :: rest, _choice :: restChoices, _walk :: restWalks) -> shape :: shapeResolveObserved(rest)(restChoices)(restWalks)
        | (None :: rest, _choice :: restChoices, _walk :: restWalks) -> TcoOtherShape :: shapeResolveObserved(rest)(restChoices)(restWalks)
        | _ -> []

// The positions no single shape describes whose every self-call hands the parameter on, conses
// onto it, or replaces it with a call's result or a part matched out of one, some rebuilding it,
// stage 0's `AccumulatorRebuild`: a fact of its own, which holds beside whatever the other rules
// resolve such a position to.
let recursive accumulatorRebuildOrdinalsOf (observed: List(Maybe(TcoArgumentShape))) (accumulators: List((Bool, Bool))) (ordinal: Int) =
    match (observed, accumulators) with
        | (Some(TcoOtherShape) :: rest, (true, true) :: restAccumulators) -> ordinal :: accumulatorRebuildOrdinalsOf(rest)(restAccumulators)(ordinal + 1)
        | (_shape :: rest, _accumulator :: restAccumulators) -> accumulatorRebuildOrdinalsOf(rest)(restAccumulators)(ordinal + 1)
        | _ -> []

let recursive shapeInitialAccumulators (count: Int) =
    if count == 0
    then []
    else (true, false) :: shapeInitialAccumulators(count - 1)

let recursive shapeAllOther (count: Int) =
    if count == 0
    then []
    else TcoOtherShape :: shapeAllOther(count - 1)

let shapeWalkOf (self: Str) (parameters: List(Str)) (body: Expr) =
    shapeWalk(self)(parameters)(body)([])([])([])([])(ShapeWalk(observed = parameters
    |> length
    |> shapeInitial, choices = parameters
    |> length
    |> shapeInitialChoices, walks = parameters
    |> length
    |> shapeInitialWalks, accumulators = parameters
    |> length
    |> shapeInitialAccumulators, sawSelfCall = false))

// The shape of every parameter position of the loop function `self` over `parameters` (the whole
// curried chain, in order) with the innermost body `body`; every position is `TcoOtherShape` when
// the body has no exact tail self-call.
let tcoSelfCallShapes (self: Str) (parameters: List(Str)) (body: Expr) =
    match shapeWalkOf(self)(parameters)(body) with
        | ShapeWalk { observed = observed, choices = choices, walks = walks, sawSelfCall = true } -> shapeResolveObserved(observed)(choices)(walks)
        | _ ->
            parameters
            |> length
            |> shapeAllOther

let tcoAccumulatorRebuildOrdinals (self: Str) (parameters: List(Str)) (body: Expr) =
    match shapeWalkOf(self)(parameters)(body) with
        | ShapeWalk { observed = observed, accumulators = accumulators, sawSelfCall = true } -> accumulatorRebuildOrdinalsOf(observed)(accumulators)(0)
        | _ -> []

// The shapes under which a list-typed parameter may live on the reference-counted heap: grown by
// a cons per iteration, consumed through its own pattern-bound tail, or grown by a cons in some
// arms of a choice and handed on in the others.
let isTcoListShape (shape: TcoArgumentShape) =
    match shape with
        | TcoGrownConsShape -> true
        | TcoConsumedTailShape -> true
        | TcoChoiceAccumulatorShape -> true
        | TcoWalkedOrRefilledShape -> true
        | TcoRefilledFreshListShape -> true
        | _ -> false

// Stage 0's `SelfCallArgumentOrdinal`: the position a call's argument takes in a call of `self`,
// the number of applications under it.
let recursive selfCallArgumentOrdinal (function: Expr) (self: Str) (ordinal: Int) =
    match function with
        | ExprAt(_span, inner) -> selfCallArgumentOrdinal(inner)(self)(ordinal)
        | ExprCall(inner, _argument, _sugar, _layout) -> selfCallArgumentOrdinal(inner)(self)(ordinal + 1)
        | ExprVar(name) ->
            if name == self
            then Some(ordinal)
            else None
        | _ -> None

let shapeFirstSome (first: Maybe(Int)) (second: Maybe(Int)) =
    match first with
        | Some(_found) -> first
        | None -> second

// Stage 0's `SelfCallOrdinalPassing`: the position at which a call of `self` in the expression
// passes the name as it is, through the branches, arms and bindings a loop body is made of.
let recursive selfCallOrdinalPassing (expression: Expr) (name: Str) (self: Str) =
    match expression with
        | ExprAt(_span, inner) -> selfCallOrdinalPassing(inner)(name)(self)
        | ExprCall(function, argument, _sugar, _layout) ->
            self
            |> selfCallOrdinalPassing(argument)(name)
            |> shapeFirstSome(selfCallOrdinalPassing(function)(name)(self))
            |> shapeFirstSome(match shapeUnspan(argument) with
                | ExprVar(passed) ->
                    if passed == name
                    then selfCallArgumentOrdinal(function)(self)(0)
                    else None
                | _ -> None)
        | ExprIf(condition, thenBranch, elseBranch) ->
            self
            |> selfCallOrdinalPassing(elseBranch)(name)
            |> shapeFirstSome(selfCallOrdinalPassing(thenBranch)(name)(self))
            |> shapeFirstSome(selfCallOrdinalPassing(condition)(name)(self))
        | ExprMatch(value, cases, _position) ->
            self
            |> selfCallOrdinalPassingCases(cases)(name)
            |> shapeFirstSome(selfCallOrdinalPassing(value)(name)(self))
        | ExprLet(_bound, value, body, _parameters, _annotation, _requirements) ->
            self
            |> selfCallOrdinalPassing(body)(name)
            |> shapeFirstSome(selfCallOrdinalPassing(value)(name)(self))
        | ExprLetResult(_bound, value, body) ->
            self
            |> selfCallOrdinalPassing(body)(name)
            |> shapeFirstSome(selfCallOrdinalPassing(value)(name)(self))
        | ExprLetRecursive(_bound, value, body, _parameters, _annotation, _requirements) ->
            self
            |> selfCallOrdinalPassing(body)(name)
            |> shapeFirstSome(selfCallOrdinalPassing(value)(name)(self))
        | _ -> None
and selfCallOrdinalPassingCases (cases: List((Pattern, Expr, Maybe(Expr)))) (name: Str) (self: Str) =
    match cases with
        | [] -> None
        | (_pattern, body, _guard) :: rest ->
            self
            |> selfCallOrdinalPassingCases(rest)(name)
            |> shapeFirstSome(selfCallOrdinalPassing(body)(name)(self))

let recursive flowMatchChildren (cases: List((Pattern, Expr, Maybe(Expr)))) (children: List(Expr)) =
    match cases with
        | [] -> children
        | (_pattern, body, Some(guard)) :: rest -> flowMatchChildren(rest)(body :: guard :: children)
        | (_pattern, body, None) :: rest -> flowMatchChildren(rest)(body :: children)

let recursive flowFieldValues (fields: List((Str, Expr))) (children: List(Expr)) =
    match fields with
        | [] -> children
        | (_name, value) :: rest -> flowFieldValues(rest)(value :: children)

// The operands of an operator node, stage 0's `EnumerateChildren` over the operators the flow walk
// knows; None for any other node.
let flowOperatorChildren (expression: Expr) =
    match expression with
        | ExprAdd(left, right) -> Some([left, right])
        | ExprSubtract(left, right) -> Some([left, right])
        | ExprMultiply(left, right) -> Some([left, right])
        | ExprDivide(left, right) -> Some([left, right])
        | ExprModulo(left, right) -> Some([left, right])
        | ExprBitwiseAnd(left, right) -> Some([left, right])
        | ExprBitwiseOr(left, right) -> Some([left, right])
        | ExprBitwiseXor(left, right) -> Some([left, right])
        | ExprShiftLeft(left, right) -> Some([left, right])
        | ExprShiftRight(left, right) -> Some([left, right])
        | ExprLogicalAnd(left, right) -> Some([left, right])
        | ExprLogicalOr(left, right) -> Some([left, right])
        | ExprGreaterThan(left, right) -> Some([left, right])
        | ExprLessThan(left, right) -> Some([left, right])
        | ExprGreaterOrEqual(left, right) -> Some([left, right])
        | ExprLessOrEqual(left, right) -> Some([left, right])
        | ExprEqual(left, right) -> Some([left, right])
        | ExprNotEqual(left, right) -> Some([left, right])
        | ExprCons(left, right) -> Some([left, right])
        | ExprResultPipe(left, right) -> Some([left, right])
        | ExprResultMapErrorPipe(left, right) -> Some([left, right])
        | ExprBitwiseNot(operand) -> Some([operand])
        | ExprLogicalNot(operand) -> Some([operand])
        | ExprAwait(operand) -> Some([operand])
        | _ -> None

// The subexpressions the self-call flow walk descends into, stage 0's `FlowChildren`: None for a
// node the walk does not know, which leaves the flow unclaimed.
let flowChildren (expression: Expr) =
    match expression with
        | ExprLet(_name, value, body, _parameters, _annotation, _requirements) -> Some([value, body])
        | ExprLetResult(_name, value, body) -> Some([value, body])
        | ExprLetRecursive(_name, value, body, _parameters, _annotation, _requirements) -> Some([value, body])
        | ExprIf(condition, thenBranch, elseBranch) -> Some([condition, thenBranch, elseBranch])
        | ExprLambda(_parameter, body, _annotation) -> Some([body])
        | ExprMatch(value, cases, _position) -> Some(value :: flowMatchChildren(cases)([]))
        | ExprVar(_name) -> Some([])
        | ExprQualifiedVar(_qualifier, _member) -> Some([])
        | ExprInt(_value) -> Some([])
        | ExprUInt(_value, _bits, _text) -> Some([])
        | ExprBigInt(_text) -> Some([])
        | ExprFloat(_value, _text) -> Some([])
        | ExprString(_value) -> Some([])
        | ExprRune(_value) -> Some([])
        | ExprBool(_value) -> Some([])
        | ExprTuple(elements) -> Some(elements)
        | ExprList(elements, _isMultiline) -> Some(elements)
        | ExprRecord(_constructorName, fields, _isMultiline) ->
            []
            |> flowFieldValues(fields)
            |> Some
        | ExprRecordUpdate(target, fields) -> Some(target :: flowFieldValues(fields)([]))
        | _ -> flowOperatorChildren(expression)

// Every node of a body, stage 0's `EnumerateFlowNodes`: an application chain is one node, its
// callee and arguments nodes of their own. None when the body holds a node the walk does not know.
let recursive flowNodes (pending: List(Expr)) (nodes: List(Expr)) =
    match pending with
        | [] -> Some(nodes)
        | ExprAt(_span, inner) :: rest -> flowNodes(inner :: rest)(nodes)
        | (ExprCall(_function, _argument, _sugar, _layout) as call) :: rest ->
            match shapeCallSpine(call)([]) with
                | (root, arguments) -> flowNodes(root :: append(arguments)(rest))(call :: nodes)
        | expression :: rest ->
            match flowChildren(expression) with
                | Some(children) ->
                    flowNodes(append(children)(rest))(expression :: nodes)
                | None -> None

let recursive flowCaseBinders (cases: List((Pattern, Expr, Maybe(Expr)))) (names: List(Str)) =
    match cases with
        | [] -> names
        | (pattern, _body, _guard) :: rest ->
            names
            |> shapePatternNames(pattern)
            |> flowCaseBinders(rest)

// Each `match` and `let` of the body with the names it binds from its value: a parameter's parts
// are what these bind from a value that reads the parameter or one of its parts.
let recursive flowDerivations (nodes: List(Expr)) (derivations: List((Expr, List(Str)))) =
    match nodes with
        | [] -> derivations
        | ExprMatch(value, cases, _position) :: rest -> flowDerivations(rest)((value, flowCaseBinders(cases)([])) :: derivations)
        | ExprLet(name, value, _body, _parameters, _annotation, _requirements) :: rest -> flowDerivations(rest)((value, [name]) :: derivations)
        | ExprLetResult(name, value, _body) :: rest -> flowDerivations(rest)((value, [name]) :: derivations)
        | ExprLetRecursive(name, value, _body, _parameters, _annotation, _requirements) :: rest -> flowDerivations(rest)((value, [name]) :: derivations)
        | (ExprCall(_function, _argument, _sugar, _layout) as call) :: rest ->
            match shapeCallSpine(call)([]) with
                | (root, arguments) ->
                    derivations
                    |> flowLambdaApplicationDerivations(root)(arguments)
                    |> flowDerivations(rest)
        | _node :: rest -> flowDerivations(rest)(derivations)
// Stage 0's `AddLambdaApplicationBinders`: a lambda applied where it is written binds each of its
// parameters to the argument at that position.
and flowLambdaApplicationDerivations (root: Expr) (arguments: List(Expr)) (derivations: List((Expr, List(Str)))) =
    match (shapeUnspan(root), arguments) with
        | (ExprLambda(parameter, body, _annotation), argument :: rest) -> flowLambdaApplicationDerivations(body)(rest)((argument, [parameter]) :: derivations)
        | _ -> derivations

// Stage 0's `CollectFlowBinders` followed through a list's spine alone: a cons pattern's head binds
// an element, not a part of the list, so only its tail's names and the whole values' names count.
let recursive flowSpinePatternNames (pattern: Pattern) (names: List(Str)) =
    match pattern with
        | PatternAt(_span, inner) -> flowSpinePatternNames(inner)(names)
        | PatternCons(_head, tail) -> flowSpinePatternNames(tail)(names)
        | PatternAs(inner, alias) -> alias :: flowSpinePatternNames(inner)(names)
        | PatternTuple(patterns) -> flowSpinePatternListNames(patterns)(names)
        | _ -> shapePatternNames(pattern)(names)
and flowSpinePatternListNames (patterns: List(Pattern)) (names: List(Str)) =
    match patterns with
        | [] -> names
        | pattern :: rest ->
            names
            |> flowSpinePatternNames(pattern)
            |> flowSpinePatternListNames(rest)

let recursive flowCaseSpineBinders (cases: List((Pattern, Expr, Maybe(Expr)))) (names: List(Str)) =
    match cases with
        | [] -> names
        | (pattern, _body, _guard) :: rest ->
            names
            |> flowSpinePatternNames(pattern)
            |> flowCaseSpineBinders(rest)

// The body's derivations with each `match` binding only the names its patterns bind along a list's
// spine.
let recursive flowSpineDerivations (nodes: List(Expr)) (derivations: List((Expr, List(Str)))) =
    match nodes with
        | [] -> derivations
        | ExprMatch(value, cases, _position) :: rest -> flowSpineDerivations(rest)((value, flowCaseSpineBinders(cases)([])) :: derivations)
        | node :: rest ->
            derivations
            |> flowDerivations([node])
            |> flowSpineDerivations(rest)

// The name a call's callee root reads as: a bare or qualified name, or none for any other root.
let flowCallRootName (root: Expr) =
    match shapeUnspan(root) with
        | ExprVar(name) -> Some(name)
        | ExprQualifiedVar(qualifier, member) -> Some(qualifier + "." + member)
        | _ -> None

// The name a call's last argument, its accumulator when the callee is a reuse specialization
// candidate, is keyed by: a bare name, the callee of a call, or none.
let recursive flowCallAccumulatorKey (arguments: List(Expr)) =
    match arguments with
        | [] -> ""
        | argument :: [] ->
            match shapeUnspan(argument) with
                | ExprVar(name) -> name
                | ExprCall(_function, _argument, _sugar, _layout) as call ->
                    match shapeCallSpine(call)([]) with
                        | (root, _callArguments) ->
                            match flowCallRootName(root) with
                                | Some(callee) -> callee
                                | None -> ""
                | _ -> ""
        | _argument :: rest -> flowCallAccumulatorKey(rest)

// A call's key in the policy: the callee's name, the argument count and the accumulator's key,
// the features the policy's verdict for a call is decided by (stage 0 keys the call itself).
let flowCallKeyOf (call: Expr) =
    match shapeCallSpine(call)([]) with
        | (root, arguments) ->
            match flowCallRootName(root) with
                | Some(callee) -> Some((callee, length(arguments), flowCallAccumulatorKey(arguments)))
                | None -> None

// Stage 0's `FlowCallPolicy.FlowingArguments`: per argument, whether the call's result may hold
// what it holds; None for a call the policy knows nothing about, whose every part flows.
let recursive flowCallFlagsFor (callee: Str) (count: Int) (accumulator: Str) (policy: List((Str, Int, Str, List(Bool)))) =
    match policy with
        | [] -> None
        | (candidate, candidateCount, candidateAccumulator, flags) :: rest ->
            if candidate == callee && candidateCount == count && candidateAccumulator == accumulator
            then Some(flags)
            else flowCallFlagsFor(callee)(count)(accumulator)(rest)

let flowCallFlags (call: Expr) (policy: List((Str, Int, Str, List(Bool)))) =
    match policy with
        | [] -> None
        | _ ->
            match flowCallKeyOf(call) with
                | Some((callee, count, accumulator)) -> flowCallFlagsFor(callee)(count)(accumulator)(policy)
                | None -> None

// Stage 0's `FlowMentionsName`: whether a value computed by `expression` may hold `name`'s value or
// a part of it. An operator's result holds nothing of its operands (a number, a truth value, a
// fresh string), except that a string append grows a loop parameter on its left in place. A
// call's result holds what its callee and the arguments the policy admits hold.
let recursive flowReadsName (name: Str) (parameters: List(Str)) (policy: List((Str, Int, Str, List(Bool)))) (depth: Int) (expression: Expr) =
    if depth > 256
    then true
    else
        match expression with
            | ExprAt(_span, inner) -> flowReadsName(name)(parameters)(policy)(depth)(inner)
            | ExprAdd(left, _right) ->
                match shapeUnspan(left) with
                    | ExprVar(leftName) -> shapeContainsName(leftName)(parameters) && leftName == name
                    | _ -> false
            | ExprSubtract(_left, _right) -> false
            | ExprMultiply(_left, _right) -> false
            | ExprDivide(_left, _right) -> false
            | ExprModulo(_left, _right) -> false
            | ExprBitwiseAnd(_left, _right) -> false
            | ExprBitwiseOr(_left, _right) -> false
            | ExprBitwiseXor(_left, _right) -> false
            | ExprShiftLeft(_left, _right) -> false
            | ExprShiftRight(_left, _right) -> false
            | ExprBitwiseNot(_operand) -> false
            | ExprLogicalNot(_operand) -> false
            | ExprLogicalAnd(_left, _right) -> false
            | ExprLogicalOr(_left, _right) -> false
            | ExprGreaterThan(_left, _right) -> false
            | ExprLessThan(_left, _right) -> false
            | ExprGreaterOrEqual(_left, _right) -> false
            | ExprLessOrEqual(_left, _right) -> false
            | ExprEqual(_left, _right) -> false
            | ExprNotEqual(_left, _right) -> false
            | ExprCall(function, argument, _sugar, _layout) as call ->
                match flowCallFlags(call)(policy) with
                    | None -> flowReadsName(name)(parameters)(policy)(depth + 1)(function) || flowReadsName(name)(parameters)(policy)(depth + 1)(argument)
                    | Some(flags) ->
                        match shapeCallSpine(call)([]) with
                            | (root, arguments) -> flowReadsName(name)(parameters)(policy)(depth + 1)(root) || flowFlaggedReadsName(name)(parameters)(policy)(depth + 1)(arguments)(flags)
            | ExprCons(head, tail) -> flowReadsName(name)(parameters)(policy)(depth + 1)(head) || flowReadsName(name)(parameters)(policy)(depth + 1)(tail)
            | ExprTuple(elements) -> flowListReadsName(name)(parameters)(policy)(depth + 1)(elements)
            | ExprList(elements, _isMultiline) -> flowListReadsName(name)(parameters)(policy)(depth + 1)(elements)
            | _ -> exprReadsName(name)(expression)
and flowListReadsName (name: Str) (parameters: List(Str)) (policy: List((Str, Int, Str, List(Bool)))) (depth: Int) (expressions: List(Expr)) =
    match expressions with
        | [] -> false
        | expression :: rest -> flowReadsName(name)(parameters)(policy)(depth)(expression) || flowListReadsName(name)(parameters)(policy)(depth)(rest)
// The arguments the policy admits into a call's result, each read where its flag is set.
and flowFlaggedReadsName (name: Str) (parameters: List(Str)) (policy: List((Str, Int, Str, List(Bool)))) (depth: Int) (arguments: List(Expr)) (flags: List(Bool)) =
    match (arguments, flags) with
        | (argument :: restArguments, flag :: restFlags) -> flag && flowReadsName(name)(parameters)(policy)(depth)(argument) || flowFlaggedReadsName(name)(parameters)(policy)(depth)(restArguments)(restFlags)
        | _ -> false

let recursive flowReadsAny (names: List(Str)) (parameters: List(Str)) (policy: List((Str, Int, Str, List(Bool)))) (expression: Expr) =
    match names with
        | [] -> false
        | name :: rest -> flowReadsName(name)(parameters)(policy)(0)(expression) || flowReadsAny(rest)(parameters)(policy)(expression)

let recursive flowAddNew (candidates: List(Str)) (names: List(Str)) =
    match candidates with
        | [] -> names
        | candidate :: rest ->
            if shapeContainsName(candidate)(names)
            then flowAddNew(rest)(names)
            else flowAddNew(rest)(candidate :: names)

let recursive flowDerivePass (derivations: List((Expr, List(Str)))) (parameters: List(Str)) (policy: List((Str, Int, Str, List(Bool)))) (names: List(Str)) =
    match derivations with
        | [] -> names
        | (value, binders) :: rest ->
            flowDerivePass(rest)(parameters)(policy)(if flowReadsAny(names)(parameters)(policy)(value)
            then flowAddNew(binders)(names)
            else names)

// A parameter's name with every name derived from it, to a fixed point.
let recursive flowDerivedNames (derivations: List((Expr, List(Str)))) (parameters: List(Str)) (policy: List((Str, Int, Str, List(Bool)))) (names: List(Str)) =
    (let next = flowDerivePass(derivations)(parameters)(policy)(names)
    in
        if length(next) == length(names)
        then names
        else flowDerivedNames(derivations)(parameters)(policy)(next))

let recursive flowDerivedOf (derivations: List((Expr, List(Str)))) (allParameters: List(Str)) (policy: List((Str, Int, Str, List(Bool)))) (parameters: List(Str)) =
    match parameters with
        | [] -> []
        | parameter :: rest -> flowDerivedNames(derivations)(allParameters)(policy)([parameter]) :: flowDerivedOf(derivations)(allParameters)(policy)(rest)

let recursive flowContainsInt (value: Int) (values: List(Int)) =
    match values with
        | [] -> false
        | candidate :: rest -> candidate == value || flowContainsInt(value)(rest)

// Stage 0's `CollectDerivedNames`: the seed names with every name the body binds from a value
// reading one of them, to a fixed point; None when the body holds a node the walk does not know.
let flowDerivedFrom (body: Expr) (seeds: List(Str)) (parameters: List(Str)) (policy: List((Str, Int, Str, List(Bool)))) =
    match flowNodes([body])([]) with
        | None -> None
        | Some(nodes) ->
            Some((nodes, flowDerivedNames(flowDerivations(nodes)([]))(parameters)(policy)(seeds)))

let recursive flowPositionsReading (arguments: List(Expr)) (position: Int) (names: List(Str)) (parameters: List(Str)) (policy: List((Str, Int, Str, List(Bool)))) (positions: List(Int)) =
    match arguments with
        | [] -> positions
        | argument :: rest ->
            flowPositionsReading(rest)(position + 1)(names)(parameters)(policy)(if !flowContainsInt(position)(positions) && flowReadsAny(names)(parameters)(policy)(argument)
            then position :: positions
            else positions)

// Stage 0's `CollectSelfCallPositionsMentioning`: the parameter positions at which some full self
// call among the nodes passes a value reading one of the names.
let recursive flowSelfCallPositionsReading (self: Str) (parameters: List(Str)) (policy: List((Str, Int, Str, List(Bool)))) (names: List(Str)) (nodes: List(Expr)) (positions: List(Int)) =
    match nodes with
        | [] -> positions
        | (ExprCall(_function, _argument, _sugar, _layout) as call) :: rest ->
            match shapeCallSpine(call)([]) with
                | (ExprVar(callee), arguments) ->
                    if callee == self && length(arguments) == length(parameters)
                    then
                        positions
                        |> flowPositionsReading(arguments)(0)(names)(parameters)(policy)
                        |> flowSelfCallPositionsReading(self)(parameters)(policy)(names)(rest)
                    else flowSelfCallPositionsReading(self)(parameters)(policy)(names)(rest)(positions)
                | _ -> flowSelfCallPositionsReading(self)(parameters)(policy)(names)(rest)(positions)
        | _node :: rest -> flowSelfCallPositionsReading(self)(parameters)(policy)(names)(rest)(positions)

let recursive flowAnyHolder (sources: List(Int)) (holders: List(Int)) =
    match sources with
        | [] -> false
        | source :: rest -> flowContainsInt(source)(holders) || flowAnyHolder(rest)(holders)

let recursive flowHolderPass (flow: List(List(Int))) (cuts: List(Bool)) (position: Int) (holders: List(Int)) =
    match (flow, cuts) with
        | (sources :: restFlow, cut :: restCuts) ->
            flowHolderPass(restFlow)(restCuts)(position + 1)(if !cut && !flowContainsInt(position)(holders) && flowAnyHolder(sources)(holders)
            then position :: holders
            else holders)
        | _ -> holders

// The positions that may receive, whole or in part, what a holder position received, to a fixed
// point, a cut position (one normalized onto the reference-counted heap) passing nothing on.
let recursive flowHolderClosure (flow: List(List(Int))) (cuts: List(Bool)) (holders: List(Int)) =
    (let next = flowHolderPass(flow)(cuts)(0)(holders)
    in
        if length(next) == length(holders)
        then holders
        else flowHolderClosure(flow)(cuts)(next))

let recursive flowParameterNamesAt (positions: List(Int)) (parameters: List(Str)) (names: List(Str)) =
    match positions with
        | [] -> names
        | position :: rest ->
            names
            |> flowAddNew(flowNameAt(position)(parameters))
            |> flowParameterNamesAt(rest)(parameters)
and flowNameAt (position: Int) (parameters: List(Str)) =
    match parameters with
        | [] -> []
        | parameter :: rest ->
            if position == 0
            then [parameter]
            else flowNameAt(position - 1)(rest)

let recursive flowPositionsReadingNone (arguments: List(Expr)) (position: Int) (names: List(Str)) (parameters: List(Str)) (policy: List((Str, Int, Str, List(Bool)))) =
    match arguments with
        | [] -> []
        | argument :: rest ->
            if flowReadsAny(names)(parameters)(policy)(argument)
            then flowPositionsReadingNone(rest)(position + 1)(names)(parameters)(policy)
            else position :: flowPositionsReadingNone(rest)(position + 1)(names)(parameters)(policy)

// Stage 0's `SuccessorsNamingNoPartOf`: the positions whose successor expression names nothing
// that can hold a value bound to the binders or a part of it: none of the names derived from the
// binders, and none of the parameters a self call may hand such a name, whole or through another
// parameter; `cuts` marks the parameters normalized onto the reference-counted heap, which hold
// references of their own. Nothing is claimed without binders or without the body's flow.
// A successor that is the parameter itself or a call's result other than a constructor's.
let flowIsOwnReferenceSuccessor (argument: Expr) (parameter: Str) (constructors: List(Str)) =
    match shapeUnspan(argument) with
        | ExprVar(name) -> name == parameter
        | ExprCall(_function, _argument, _sugar, _layout) as call ->
            match shapeCallSpine(call)([]) with
                | (root, _arguments) ->
                    match shapeUnspan(root) with
                        | ExprVar(name) -> !shapeContainsName(name)(constructors)
                        | ExprQualifiedVar(qualifier, member) -> !shapeContainsName(member)(constructors) && !shapeContainsName(qualifier + "." + member)(constructors)
                        | _ -> true
        | _ -> false

let recursive flowKeepOwnReferencePositions (arguments: List(Expr)) (parameters: List(Str)) (constructors: List(Str)) (position: Int) (candidates: List(Int)) =
    match (arguments, parameters) with
        | (argument :: restArguments, parameter :: restParameters) ->
            flowKeepOwnReferencePositions(restArguments)(restParameters)(constructors)(position + 1)(if flowIsOwnReferenceSuccessor(argument)(parameter)(constructors)
            then candidates
            else flowWithoutPositions([position])(candidates))
        | _ -> candidates
and flowWithoutPositions (removed: List(Int)) (positions: List(Int)) =
    match positions with
        | [] -> []
        | position :: rest ->
            if flowContainsInt(position)(removed)
            then flowWithoutPositions(removed)(rest)
            else position :: flowWithoutPositions(removed)(rest)

let recursive flowOwnReferenceNodes (self: Str) (parameters: List(Str)) (constructors: List(Str)) (nodes: List(Expr)) (candidates: List(Int)) =
    match nodes with
        | [] -> candidates
        | (ExprCall(_function, _argument, _sugar, _layout) as call) :: rest ->
            match shapeCallSpine(call)([]) with
                | (ExprVar(callee), arguments) ->
                    if callee == self && length(arguments) == length(parameters)
                    then
                        candidates
                        |> flowKeepOwnReferencePositions(arguments)(parameters)(constructors)(0)
                        |> flowOwnReferenceNodes(self)(parameters)(constructors)(rest)
                    else flowOwnReferenceNodes(self)(parameters)(constructors)(rest)(candidates)
                | _ -> flowOwnReferenceNodes(self)(parameters)(constructors)(rest)(candidates)
        | _node :: rest -> flowOwnReferenceNodes(self)(parameters)(constructors)(rest)(candidates)

// Stage 0's `PositionsHoldingOwnReferences` after its type test (`candidates`, the positions of a
// value of the contract's own types): the candidates every full self call hands the parameter
// itself or a call's result, whose owned value holds references of its own.
let tcoOwnReferencePositions (self: Str) (parameters: List(Str)) (constructors: List(Str)) (body: Expr) (candidates: List(Int)) =
    match flowNodes([body])([]) with
        | None -> []
        | Some(nodes) -> flowOwnReferenceNodes(self)(parameters)(constructors)(nodes)(candidates)

let recursive flowCutsWith (positions: List(Int)) (position: Int) (cuts: List(Bool)) =
    match cuts with
        | [] -> []
        | cut :: rest -> (cut || flowContainsInt(position)(positions)) :: flowCutsWith(positions)(position + 1)(rest)

// `ownReferences` marks the parameters holding references of their own to whatever they keep
// (stage 0's `PositionsHoldingOwnReferences`): like a cut, and never a holder.
let flowSuccessorsNamingNoPartOf (self: Str) (parameters: List(Str)) (policy: List((Str, Int, Str, List(Bool)))) (body: Expr) (flow: Maybe(List(List(Int)))) (binders: List(Str)) (ownReferences: List(Int)) (cuts: List(Bool)) (arguments: List(Expr)) =
    match (binders, flow) with
        | ([], _) -> []
        | (_, None) -> []
        | (_, Some(sources)) ->
            match flowDerivedFrom(body)(binders)(parameters)(policy) with
                | None -> []
                | Some((nodes, named)) ->
                    match []
                    |> flowSelfCallPositionsReading(self)(parameters)(policy)(named)(nodes)
                    |> flowWithoutPositions(ownReferences)
                    |> flowHolderClosure(sources)(flowCutsWith(ownReferences)(0)(cuts)) with
                        | holders ->
                            flowPositionsReadingNone(arguments)(0)(flowParameterNamesAt(holders)(parameters)(named))(parameters)(policy)

// The source parameters an argument reads, by ordinal, added to the sources a position already has.
let recursive flowArgumentSources (argument: Expr) (parameters: List(Str)) (policy: List((Str, Int, Str, List(Bool)))) (derived: List(List(Str))) (source: Int) (sources: List(Int)) =
    match derived with
        | [] -> sources
        | names :: rest ->
            flowArgumentSources(argument)(parameters)(policy)(rest)(source + 1)(if !flowContainsInt(source)(sources) && flowReadsAny(names)(parameters)(policy)(argument)
            then source :: sources
            else sources)

let recursive flowRecordCall (arguments: List(Expr)) (parameters: List(Str)) (policy: List((Str, Int, Str, List(Bool)))) (derived: List(List(Str))) (flow: List(List(Int))) =
    match (arguments, flow) with
        | (argument :: restArguments, sources :: restFlow) -> flowArgumentSources(argument)(parameters)(policy)(derived)(0)(sources) :: flowRecordCall(restArguments)(parameters)(policy)(derived)(restFlow)
        | _ -> flow

type FlowCount =
    | selfReferences: Int
    | recognizedCalls: Int
    | flow: List(List(Int))

let flowCountCall (self: Str) (parameters: List(Str)) (policy: List((Str, Int, Str, List(Bool)))) (derived: List(List(Str))) (call: Expr) (count: FlowCount) =
    match shapeCallSpine(call)([]) with
        | (ExprVar(callee), arguments) ->
            if callee == self && length(arguments) == length(parameters)
            then count with recognizedCalls = count.recognizedCalls + 1, flow = flowRecordCall(arguments)(parameters)(policy)(derived)(count.flow)
            else count
        | _ -> count

let recursive flowCountNodes (self: Str) (parameters: List(Str)) (policy: List((Str, Int, Str, List(Bool)))) (derived: List(List(Str))) (nodes: List(Expr)) (count: FlowCount) =
    match nodes with
        | [] -> count
        | ExprVar(name) :: rest ->
            flowCountNodes(self)(parameters)(policy)(derived)(rest)((if name == self
            then count with selfReferences = count.selfReferences + 1
            else count))
        | (ExprCall(_function, _argument, _sugar, _layout) as call) :: rest ->
            count
            |> flowCountCall(self)(parameters)(policy)(derived)(call)
            |> flowCountNodes(self)(parameters)(policy)(derived)(rest)
        | _node :: rest -> flowCountNodes(self)(parameters)(policy)(derived)(rest)(count)

let recursive flowEmpty (count: Int) =
    if count == 0
    then []
    else [] :: flowEmpty(count - 1)

// Stage 0's `CollectSelfCallParameterFlow`: for every parameter position of a self-call, the source
// parameters whose value, or a part of it, the position may receive. None when some reference to
// the function is not a full self-call, or the body holds a node the walk does not know, so nothing
// is claimed about the flow.
let tcoSelfCallFlowWith (spineOnly: Bool) (self: Str) (parameters: List(Str)) (policy: List((Str, Int, Str, List(Bool)))) (body: Expr) =
    match flowNodes([body])([]) with
        | None -> None
        | Some(nodes) ->
            match FlowCount(selfReferences = 0, recognizedCalls = 0, flow = parameters
            |> length
            |> flowEmpty) |> flowCountNodes(self)(parameters)(policy)(parameters |> flowDerivedOf(if spineOnly
            then flowSpineDerivations(nodes)([])
            else flowDerivations(nodes)([]))(parameters)(policy))(nodes) with
                | FlowCount { selfReferences = references, recognizedCalls = calls, flow = flow } ->
                    if references == calls
                    then Some(flow)
                    else None

let tcoSelfCallParameterFlow (self: Str) (parameters: List(Str)) (policy: List((Str, Int, Str, List(Bool)))) (body: Expr) = tcoSelfCallFlowWith(false)(self)(parameters)(policy)(body)

// Stage 0's `SelfCallSpineFlow`: the same flow followed through a list's spine alone.
let tcoSelfCallSpineFlow (self: Str) (parameters: List(Str)) (policy: List((Str, Int, Str, List(Bool)))) (body: Expr) = tcoSelfCallFlowWith(true)(self)(parameters)(policy)(body)
