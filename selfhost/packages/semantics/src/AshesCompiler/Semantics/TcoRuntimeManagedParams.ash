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

import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.TcoAffineAppend.affineSelfAppendOrdinals
export (
    value runtimeManagedStrOrdinals,
)

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
        | ExprAt(span, inner) -> ExprAt(span)(inlineName(target)(replacement)(inner))
        | ExprVar(name) ->
            if name == target
            then ExprVar(replacement)
            else expression
        | ExprAdd(left, right) -> ExprAdd(inlineName(target)(replacement)(left))(inlineName(target)(replacement)(right))
        | ExprSubtract(left, right) -> ExprSubtract(inlineName(target)(replacement)(left))(inlineName(target)(replacement)(right))
        | ExprMultiply(left, right) -> ExprMultiply(inlineName(target)(replacement)(left))(inlineName(target)(replacement)(right))
        | ExprDivide(left, right) -> ExprDivide(inlineName(target)(replacement)(left))(inlineName(target)(replacement)(right))
        | ExprModulo(left, right) -> ExprModulo(inlineName(target)(replacement)(left))(inlineName(target)(replacement)(right))
        | ExprEqual(left, right) -> ExprEqual(inlineName(target)(replacement)(left))(inlineName(target)(replacement)(right))
        | ExprNotEqual(left, right) -> ExprNotEqual(inlineName(target)(replacement)(left))(inlineName(target)(replacement)(right))
        | ExprIf(condition, thenBranch, elseBranch) -> ExprIf(inlineName(target)(replacement)(condition))(inlineName(target)(replacement)(thenBranch))(inlineName(target)(replacement)(elseBranch))
        | ExprCall(function, argument, sugar, layout) -> ExprCall(inlineName(target)(replacement)(function))(inlineName(target)(replacement)(argument))(sugar)(layout)
        | ExprCons(head, tail) -> ExprCons(inlineName(target)(replacement)(head))(inlineName(target)(replacement)(tail))
        | ExprLet(name, value, body, parameters, annotation, requirements) ->
            if name == target
            then ExprLet(name)(inlineName(target)(replacement)(value))(body)(parameters)(annotation)(requirements)
            else ExprLet(name)(inlineName(target)(replacement)(value))(inlineName(target)(replacement)(body))(parameters)(annotation)(requirements)
        | ExprMatch(scrutinee, cases, position) -> ExprMatch(inlineName(target)(replacement)(scrutinee))(inlineNameCases(target)(replacement)(cases))(position)
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
        | Some(expression) -> Some(inlineName(target)(replacement)(expression))
        | None -> None

// Every plain-variable `let` binding in `expression`, inlined by name: `let r = acc in ... r ...`
// becomes `... acc ...` wherever `r`'s own pattern does not rebind it first. The `let` itself is
// removed; a non-plain-variable binding is left as it is and only its body is walked further.
let recursive inlinePlainVarAliases (expression: Expr) =
    match expression with
        | ExprAt(span, inner) -> ExprAt(span)(inlinePlainVarAliases(inner))
        | ExprLet(name, ExprVar(source), body, _parameters, _annotation, _requirements) -> inlinePlainVarAliases(inlineName(name)(source)(body))
        | ExprLet(name, ExprAt(_span, ExprVar(source)), body, _parameters, _annotation, _requirements) -> inlinePlainVarAliases(inlineName(name)(source)(body))
        | ExprLet(name, value, body, parameters, annotation, requirements) -> ExprLet(name)(value)(inlinePlainVarAliases(body))(parameters)(annotation)(requirements)
        | ExprIf(condition, thenBranch, elseBranch) -> ExprIf(condition)(inlinePlainVarAliases(thenBranch))(inlinePlainVarAliases(elseBranch))
        | ExprMatch(scrutinee, cases, position) -> ExprMatch(scrutinee)(inlinePlainVarAliasesCases(cases))(position)
        | other -> other
and inlinePlainVarAliasesCases (cases: List((Pattern, Expr, Maybe(Expr)))) =
    match cases with
        | [] -> []
        | (pattern, body, guard) :: rest -> (pattern, inlinePlainVarAliases(body), guard) :: inlinePlainVarAliasesCases(rest)

// The ordinals (in `parameters` order) of every parameter of the self-recursive function `self`
// that is, on every tail self-call the innermost body `body` makes, either passed through
// unchanged or rebuilt as the leftmost leaf of a `+` chain — read through a plain-variable alias
// first, unlike `affineSelfAppendOrdinals`'s own, stricter reservation-growth eligibility.
let runtimeManagedStrOrdinals (self: Str) (parameters: List(Str)) (body: Expr) = affineSelfAppendOrdinals(self)(parameters)(inlinePlainVarAliases(body))
