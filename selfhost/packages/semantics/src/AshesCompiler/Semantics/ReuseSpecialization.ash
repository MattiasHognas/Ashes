// Detects and tracks the arena/RC in-place-reuse opportunity a match's dead scrutinee cell offers
// a same-shape constructor rebuild inside one of its own arms: OPT-42's top-cell freshness and
// uniqueness consumer for the reuse eligibility HeapLayoutClassification.ash already computes.
// Every predicate here is structural (Expr/Pattern shape only) so CoreLowering.ash's hooks can call
// it without exposing CoreLoweringState across the module boundary; the token itself, the emission
// of DropReuse/AllocReusing, and every uniqueness/exhaustiveness fact that needs a type environment
// stay in CoreLowering.ash.
//
// A token is produced only for a guard-free match arm whose pattern is a constructor of the type
// being matched, whose body does not mention the scrutinee's name again (the matched cell is dead),
// and only consumed by a same-name, same-arity rebuild whose every pointer-typed field is either
// scalar (nothing to release) or passed straight through, unchanged, from the matching pattern
// binding at the same field position — the one shape this module can prove safe without walking
// the old cell's non-transferred children before the overwrite. The runtime `DropReuse`/
// `AllocReusing` pair itself still decides, at every call, whether the matched cell is actually
// unique; this module only ever decides whether it is SAFE to ask.
//
// `exprMentionsName` (`ExprMentions.ash`) is a conservative, shadow-blind reference check: a
// coincidental rebinding of the scrutinee's own name inside the arm counts as "referenced", never
// as "safe to reuse" — sound, just occasionally more conservative than a shadow-aware walk would be.

import Ashes.Collection.List.append
import Ashes.Collection.List.length
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.Pattern
import AshesCompiler.Semantics.ExprMentions.exprMentionsName
export (
    type CoreReuseToken(..),
    value reusePatternConstructorArity,
    value reusePatternFieldBindings,
    value reuseArmBodyRebuildsSameConstructor,
    value reuseTransferredFieldsSafe,
)

// One live reuse token: the arena/RC cell `temp` names, its field shape (`fieldCount`, `tagless`),
// whether it may be an RC allocation the backend's own uniqueness check can decline at runtime
// (`runtimeManaged`), the constructor it was minted from (only a rebuild of the SAME constructor
// may consume it — narrower than stage 0's cross-constructor same-arity reuse, chosen so a token's
// pointer fields never need a per-constructor remap), and the pattern-bound field name at each
// 0-based field index a rebuild may pass straight through unchanged.
type CoreReuseToken =
    | temp: Int
    | fieldCount: Int
    | tagless: Bool
    | runtimeManaged: Bool
    | constructorName: Str
    | fieldBindings: List((Int, Str))

// Strips a source-location wrapper so the shapes below match through it, stage 0's `unspanArgument`.
let recursive reuseUnspanExpr (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> reuseUnspanExpr(inner)
        | _ -> expression

let recursive reuseUnspanPattern (pattern: Pattern) =
    match pattern with
        | PatternAt(_span, inner) -> reuseUnspanPattern(inner)
        | _ -> pattern

// The constructor name and field count a pattern matches, when it is (through a source-location
// wrapper) a positional constructor pattern — `None` for every other pattern shape, including a
// bare nullary-constructor `PatternVar` (the caller already knows the constructor from the
// scrutinee's match plan in that case).
let reusePatternConstructorArity (pattern: Pattern) =
    match reuseUnspanPattern(pattern) with
        | PatternConstructor(name, subPatterns) -> Some((name, length(subPatterns)))
        | _ -> None

// The 0-based field index and bound name of every plain-variable sub-pattern of a constructor
// pattern — the token's transferable field map. A wildcard, literal, or nested pattern at a field
// contributes no binding (that field's value cannot be read back out as a bare name, so it can
// never be a "pass the same pointer straight through" rebuild argument).
let recursive reusePatternFieldBindings (pattern: Pattern) =
    match reuseUnspanPattern(pattern) with
        | PatternConstructor(_name, subPatterns) -> reuseIndexedFieldBindings(0)(subPatterns)
        | _ -> []
and reuseIndexedFieldBindings (index: Int) (subPatterns: List(Pattern)) =
    match subPatterns with
        | [] -> []
        | subPattern :: rest -> append(reuseFieldBindingFor(index)(subPattern))(reuseIndexedFieldBindings(index + 1)(rest))
and reuseFieldBindingFor (index: Int) (subPattern: Pattern) =
    match reuseUnspanPattern(subPattern) with
        | PatternVar(name) -> [(index, name)]
        | _ -> []

let recursive reuseFindField (name: Str) (fields: List((Str, Expr))) =
    match fields with
        | [] -> None
        | (fieldName, expression) :: rest ->
            if fieldName == name
            then Some(expression)
            else reuseFindField(name)(rest)

let recursive reuseOrderedFieldExpressions (fieldNames: List(Str)) (fields: List((Str, Expr))) =
    match fieldNames with
        | [] -> Some([])
        | name :: rest ->
            match (reuseFindField(name)(fields), reuseOrderedFieldExpressions(rest)(fields)) with
                | (Some(expression), Some(restExpressions)) -> Some(expression :: restExpressions)
                | _ -> None

let recursive reuseCollectCallArgs (expression: Expr) (args: List(Expr)) =
    match reuseUnspanExpr(expression) with
        | ExprCall(function, argument, _isSugar, _layout) -> reuseCollectCallArgs(function)(argument :: args)
        | ExprVar(name) -> (Some(name), args)
        | _ -> (None, args)

// The ordered field-argument expressions a match arm's body rebuilds `expectedName` with, seen
// through `let`/`let result`/`let recursive` bodies (never their bound values), when the body is
// (optionally through those lets) a saturated positional call chain or record literal naming
// `expectedName` with exactly `expectedFieldNames`'s arity — `None` for every other shape,
// including a call to a different constructor or a wrong arity. `expectedFieldNames` supplies the
// declared field order a record literal's named arguments are projected against; a positional call
// is order-independent and only its length is used.
let recursive reuseArmBodyRebuildsSameConstructor (expectedName: Str) (expectedFieldNames: List(Str)) (body: Expr) =
    match reuseUnspanExpr(body) with
        | ExprLet(_name, _value, inner, _params, _annotation, _requirements) -> reuseArmBodyRebuildsSameConstructor(expectedName)(expectedFieldNames)(inner)
        | ExprLetResult(_name, _value, inner) -> reuseArmBodyRebuildsSameConstructor(expectedName)(expectedFieldNames)(inner)
        | ExprLetRecursive(_name, _value, inner, _params, _annotation, _requirements) -> reuseArmBodyRebuildsSameConstructor(expectedName)(expectedFieldNames)(inner)
        | ExprRecord(name, fields, _isMultiline) ->
            if name == expectedName && length(fields) == length(expectedFieldNames)
            then reuseOrderedFieldExpressions(expectedFieldNames)(fields)
            else None
        | ExprVar(name) ->
            if name == expectedName && length(expectedFieldNames) == 0
            then Some([])
            else None
        | ExprCall(_function, _argument, _isSugar, _layout) as call ->
            match reuseCollectCallArgs(call)([]) with
                | (Some(name), args) ->
                    if name == expectedName && length(args) == length(expectedFieldNames)
                    then Some(args)
                    else None
                | (None, _args) -> None
        | _ -> None

// Whether every 0-based field index in `pointerFieldIndices` is passed straight through, unchanged,
// from the matching pattern binding into the rebuild's argument at the same position: the one
// shape this module can prove safe without walking the matched cell's non-transferred pointer
// children before the overwrite (a rebuild that instead drops, ignores, or replaces a pointer field
// is declined outright, never attempted).
let recursive reuseTransferredFieldsSafe (pointerFieldIndices: List(Int)) (fieldBindings: List((Int, Str))) (args: List(Expr)) =
    match pointerFieldIndices with
        | [] -> true
        | index :: rest -> reuseArgIsTransferredVar(index)(fieldBindings)(args) && reuseTransferredFieldsSafe(rest)(fieldBindings)(args)
and reuseArgIsTransferredVar (index: Int) (fieldBindings: List((Int, Str))) (args: List(Expr)) =
    match (reuseFieldBindingAt(index)(fieldBindings), reuseNthExpr(index)(args)) with
        | (Some(boundName), Some(argExpression)) ->
            match reuseUnspanExpr(argExpression) with
                | ExprVar(name) -> name == boundName
                | _ -> false
        | _ -> false
and reuseFieldBindingAt (index: Int) (fieldBindings: List((Int, Str))) =
    match fieldBindings with
        | [] -> None
        | (candidateIndex, name) :: rest ->
            if candidateIndex == index
            then Some(name)
            else reuseFieldBindingAt(index)(rest)
and reuseNthExpr (index: Int) (args: List(Expr)) =
    match args with
        | [] -> None
        | head :: rest ->
            if index == 0
            then Some(head)
            else reuseNthExpr(index - 1)(rest)
