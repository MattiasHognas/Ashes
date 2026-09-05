// The ownership rules of a `match` over its arms' syntax, stage 0's
// `ShouldNormalizeStaticStringMatchArms`, `MarkRuntimeManagedMatchResult`, and
// `TrackRuntimeManagedMatchScrutineeOwner` decisions: whether a literal string arm is copied to
// the reference-counted heap so the join is uniformly runtime-managed, whether the join of every
// arm's result is itself runtime-managed and freshly produced, and whether a fresh scrutinee is
// owned by the arm that matched it.
//
// Invariants:
// - Every rule is pure over the arms' syntax and facts the lowering already computed; emission
//   stays in `CoreLowering`.
// - A rule that cannot see a fact answers conservatively: a join it cannot prove uniform is not
//   runtime-managed, and a scrutinee it cannot prove safely owned gets no owner.

import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.Pattern
export (
    type MatchArmResult(..),
    value shouldNormalizeStaticStringArms,
    value staticStringArmBody,
    value joinIsRuntimeManaged,
    value joinIsNewlyProduced,
    value joinIsRetainedOwner,
    value branchIsEmptyListLiteral,
    value scrutineeHasOwnerCandidate,
    value patternBindsWholeScrutinee,
)

// What one arm contributed to the match's result: whether its stored value lives on the
// reference-counted heap, whether that value was freshly produced by the arm rather than
// handed on from a binding, and whether it is a pattern owner's retained reference (or a back
// edge that never reaches the join), which crosses the arm's reset without a copy.
type MatchArmResult =
    | armRuntimeManaged: Bool
    | armNewlyProduced: Bool
    | armRetainedOwner: Bool

let recursive unspanned (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> unspanned(inner)
        | other -> other

let combineStringArms (left: Maybe(Bool)) (right: Maybe(Bool)) =
    match (left, right) with
        | (Some(leftFresh), Some(rightFresh)) -> Some(leftFresh || rightFresh)
        | _ -> None

// Stage 0's `IsRuntimeManagedStringMatchArm`: a literal string is runtime-managed but not
// fresh, a fresh string producer is both, a `let` chain is what its body is when that body is
// fresh, an `if` is what both its branches are, and a funnel arm (a tail self-call, which never
// reaches the join) is runtime-managed but not fresh. `Some(fresh)` for a runtime-managed string
// arm, `None` for any other arm.
let recursive runtimeManagedStringArm (isFreshStringProducer: Expr -> Bool) (isFunnel: Expr -> Bool) (body: Expr) =
    match unspanned(body) with
        | ExprString(_value) -> Some(false)
        | ExprLet(_name, _value, letBody, _parameters, _annotation, _requirements) ->
            match runtimeManagedStringArm(isFreshStringProducer)(isFunnel)(letBody) with
                | Some(true) -> Some(true)
                | _ -> None
        | ExprIf(_condition, thenBranch, elseBranch) -> combineStringArms(runtimeManagedStringArm(isFreshStringProducer)(isFunnel)(thenBranch))(runtimeManagedStringArm(isFreshStringProducer)(isFunnel)(elseBranch))
        | other ->
            if isFreshStringProducer(other)
            then Some(true)
            else
                if isFunnel(other)
                then Some(false)
                else None

let recursive normalizeStaticStringArmsFrom (isFreshStringProducer: Expr -> Bool) (isFunnel: Expr -> Bool) (cases: List((Pattern, Expr, Maybe(Expr)))) (anyFresh: Bool) =
    match cases with
        | [] -> anyFresh
        | (_pattern, _body, Some(_guard)) :: _rest -> false
        | (_pattern, body, None) :: rest ->
            match runtimeManagedStringArm(isFreshStringProducer)(isFunnel)(body) with
                | Some(fresh) -> normalizeStaticStringArmsFrom(isFreshStringProducer)(isFunnel)(rest)(anyFresh || fresh)
                | None -> false

// Stage 0's `ShouldNormalizeStaticStringMatchArms`: every arm is guard-free and produces a
// runtime-managed string, and at least one produces a fresh one, so the literal arms are copied
// to the reference-counted heap and the join is uniformly runtime-managed.
let shouldNormalizeStaticStringArms (isFreshStringProducer: Expr -> Bool) (isFunnel: Expr -> Bool) (cases: List((Pattern, Expr, Maybe(Expr)))) = normalizeStaticStringArmsFrom(isFreshStringProducer)(isFunnel)(cases)(false)

// The literal an arm body is, when the whole body is one string literal.
let staticStringArmBody (body: Expr) =
    match unspanned(body) with
        | ExprString(value) -> Some(value)
        | _ -> None

let recursive allArms (holds: MatchArmResult -> Bool) (arms: List(MatchArmResult)) =
    match arms with
        | [] -> true
        | arm :: rest -> holds(arm) && allArms(holds)(rest)

// Stage 0's `MarkRuntimeManagedMatchResult`: the join is runtime-managed when every arm's result
// is; a match without arms proves nothing.
let joinIsRuntimeManaged (arms: List(MatchArmResult)) =
    match arms with
        | [] -> false
        | _ ->
            allArms(given (arm: MatchArmResult) -> arm.armRuntimeManaged)(arms)

// The join is newly produced only when every arm's result is: one borrowed or unknown arm poisons
// it, since the merged value may then be a live binding's.
let joinIsNewlyProduced (arms: List(MatchArmResult)) =
    match arms with
        | [] -> false
        | _ ->
            allArms(given (arm: MatchArmResult) -> arm.armNewlyProduced)(arms)

// The join holds a pattern owner's retained reference when every arm that reaches it does.
let joinIsRetainedOwner (arms: List(MatchArmResult)) =
    match arms with
        | [] -> false
        | _ ->
            allArms(given (arm: MatchArmResult) -> arm.armRetainedOwner)(arms)

// Stage 0's `BranchJoinsRuntimeManagedResult` for a list-typed join: the empty list literal
// (through a `let` chain) is a valid runtime-managed list value, since every list retain and
// release is nil-guarded, so it joins a runtime-managed result without carrying the fact itself.
let recursive branchIsEmptyListLiteral (body: Expr) =
    match unspanned(body) with
        | ExprList([], _isSugar) -> true
        | ExprLet(_name, _value, letBody, _parameters, _annotation, _requirements) -> branchIsEmptyListLiteral(letBody)
        | _ -> false

// A scrutinee that reads a binding resolves to that binding's own owner; any other scrutinee is a
// fresh value only the match refers to, which the matching arm may own.
let scrutineeHasOwnerCandidate (scrutinee: Expr) =
    match unspanned(scrutinee) with
        | ExprVar(_name) -> false
        | _ -> true

// A plain variable pattern binds the whole scrutinee: stage 0 makes that binding's own slot the
// owner instead of a separate owner slot.
let recursive patternBindsWholeScrutinee (pattern: Pattern) =
    match pattern with
        | PatternAt(_span, inner) -> patternBindsWholeScrutinee(inner)
        | PatternVar(_name) -> true
        | _ -> false
