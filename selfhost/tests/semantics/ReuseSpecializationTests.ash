// Unit tests for OPT-42's structural reuse-eligibility analysis
// (`AshesCompiler.Semantics.ReuseSpecialization`): the pure Expr/Pattern-shape predicates
// CoreLowering.ash's match-arm and constructor-allocation hooks use to decide whether a dead
// matched cell may become an arena/RC in-place-reuse token, and whether a same-name rebuild may
// safely consume one. Exercised directly against hand-built AST fragments rather than through the
// full lowering pipeline: end-to-end `DropReuse`/`AllocReusing` parity is blocked on a pre-existing
// gap this module does not own — selfhost's own constructor-placement lowering does not yet mark
// an ordinary `let`-bound or TCO-parameter ADT scrutinee `RuntimeManaged` the way stage 0 does, so
// the hooks' own precondition (`isRuntimeTemp` on the scrutinee) never holds yet for a plain match
// on a named-ADT value. See the self-hosting milestone notes for the tracked follow-up.
//
// `Expr` derives neither `Eq` nor `Show`, so a returned argument list is described as a list of
// discriminator strings (`describeExpr`) rather than compared directly.

import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.ReuseSpecialization
export (
    value runReuseSpecializationTests,
)

let consPattern unit = PatternConstructor("Cons")([PatternVar("head"), PatternVar("tail")])

let consRebuildBody unit =
    ExprCall(ExprCall(ExprVar("Cons"))(ExprMultiply(ExprVar("head"))(ExprInt(2)))(false)(callArgumentsInline))(ExprVar("tail"))(false)(callArgumentsInline)

let describeExpr (expression: Expr) =
    match expression with
        | ExprVar(name) -> "Var:" + name
        | ExprInt(value) -> "Int:" + Ashes.Text.fromInt(value)
        | _ -> "Other"

let recursive describeExprList (expressions: List(Expr)) =
    match expressions with
        | [] -> []
        | expression :: rest -> describeExpr(expression) :: describeExprList(rest)

let describeRebuild (result: Maybe(List(Expr))) =
    match result with
        | Some(args) ->
            args
            |> describeExprList
            |> Some
        | None -> None

let testPatternConstructorArityMatchesPositionalPattern unit =
    Unit
    |> consPattern
    |> reusePatternConstructorArity
    |> test.assertEqual(Some(("Cons", 2)))

let testPatternConstructorArityDeclinesWildcard unit =
    PatternWildcard
    |> reusePatternConstructorArity
    |> test.assertEqual(None)

let testPatternFieldBindingsRecordsEveryVarSubPattern unit =
    Unit
    |> consPattern
    |> reusePatternFieldBindings
    |> test.assertEqual([(0, "head"), (1, "tail")])

let testPatternFieldBindingsSkipsWildcardAndLiteralFields unit =
    [PatternWildcard, PatternVar("tail")]
    |> PatternConstructor("Cons")
    |> reusePatternFieldBindings
    |> test.assertEqual([(1, "tail")])

let testArmBodyRebuildsSameConstructorThroughPositionalCall unit =
    Unit
    |> consRebuildBody
    |> reuseArmBodyRebuildsSameConstructor("Cons")(["head", "tail"])
    |> describeRebuild
    |> test.assertEqual(Some(["Other", "Var:tail"]))

let testArmBodyRebuildDeclinesDifferentConstructor unit =
    Unit
    |> consRebuildBody
    |> reuseArmBodyRebuildsSameConstructor("Nil")(["head", "tail"])
    |> describeRebuild
    |> test.assertEqual(None)

let testArmBodyRebuildDeclinesWrongArity unit =
    Unit
    |> consRebuildBody
    |> reuseArmBodyRebuildsSameConstructor("Cons")(["head", "tail", "extra"])
    |> describeRebuild
    |> test.assertEqual(None)

let testArmBodyRebuildSeesThroughNestedLet unit =
    (let wrapped =
        ExprLet("scratch")(ExprInt(1))(consRebuildBody(Unit))([])(None)([])
    in
        wrapped
        |> reuseArmBodyRebuildsSameConstructor("Cons")(["head", "tail"])
        |> describeRebuild
        |> test.assertEqual(Some(["Other", "Var:tail"])))

let testArmBodyRebuildProjectsRecordLiteralByDeclaredFieldOrder unit =
    (let countExpr = ExprAdd(ExprVar("count"))(ExprInt(1))
    in
        let totalExpr = ExprAdd(ExprVar("total"))(ExprVar("count"))
        in
            let body = ExprRecord("Counter")([("total", totalExpr), ("count", countExpr)])(false)
            in
                body
                |> reuseArmBodyRebuildsSameConstructor("Counter")(["count", "total"])
                |> describeRebuild
                |> test.assertEqual(Some(["Other", "Other"])))

let testArmBodyRebuildDeclinesNullaryVarOfWrongArity unit =
    ExprVar("Nil")
    |> reuseArmBodyRebuildsSameConstructor("Nil")(["head", "tail"])
    |> describeRebuild
    |> test.assertEqual(None)

let testArmBodyRebuildAcceptsNullaryVarAtZeroArity unit =
    ExprVar("Nil")
    |> reuseArmBodyRebuildsSameConstructor("Nil")([])
    |> describeRebuild
    |> test.assertEqual(Some([]))

let testTransferredFieldsSafeWhenPointerFieldPassedThroughUnchanged unit =
    [ExprMultiply(ExprVar("head"))(ExprInt(2)), ExprVar("tail")]
    |> reuseTransferredFieldsSafe([1])([(0, "head"), (1, "tail")])
    |> test.assertEqual(true)

let testTransferredFieldsUnsafeWhenPointerFieldReplaced unit =
    [ExprMultiply(ExprVar("head"))(ExprInt(2)), ExprVar("fresh")]
    |> reuseTransferredFieldsSafe([1])([(0, "head"), (1, "tail")])
    |> test.assertEqual(false)

let testTransferredFieldsUnsafeWhenPatternNeverBoundThatField unit =
    [ExprMultiply(ExprVar("head"))(ExprInt(2)), ExprVar("tail")]
    |> reuseTransferredFieldsSafe([1])([(0, "head")])
    |> test.assertEqual(false)

let testTransferredFieldsSafeWithNoPointerFields unit =
    [ExprInt(1), ExprInt(2)]
    |> reuseTransferredFieldsSafe([])([(0, "count"), (1, "total")])
    |> test.assertEqual(true)

let testExprMentionsNameFindsBareVar unit =
    ExprVar("lst")
    |> exprMentionsName("lst")
    |> test.assertEqual(true)

let testExprMentionsNameMissesUnrelatedLiteral unit =
    ExprInt(5)
    |> exprMentionsName("lst")
    |> test.assertEqual(false)

let testExprMentionsNameSeesThroughConsAndCall unit =
    callArgumentsInline
    |> ExprCall(ExprVar("sum"))(ExprCons(ExprInt(1))(ExprVar("lst")))(false)
    |> exprMentionsName("lst")
    |> test.assertEqual(true)

let testExprMentionsNameIsShadowBlindInsideLet unit =
    []
    |> ExprLet("lst")(ExprInt(0))(ExprVar("lst"))([])(None)
    |> exprMentionsName("lst")
    |> test.assertEqual(true)

let reportSuccess unit = Ashes.IO.print("all self-hosted reuse specialization tests passed")

let runReuseSpecializationTests unit =
    unit
    |> testPatternConstructorArityMatchesPositionalPattern
    |> testPatternConstructorArityDeclinesWildcard
    |> testPatternFieldBindingsRecordsEveryVarSubPattern
    |> testPatternFieldBindingsSkipsWildcardAndLiteralFields
    |> testArmBodyRebuildsSameConstructorThroughPositionalCall
    |> testArmBodyRebuildDeclinesDifferentConstructor
    |> testArmBodyRebuildDeclinesWrongArity
    |> testArmBodyRebuildSeesThroughNestedLet
    |> testArmBodyRebuildProjectsRecordLiteralByDeclaredFieldOrder
    |> testArmBodyRebuildDeclinesNullaryVarOfWrongArity
    |> testArmBodyRebuildAcceptsNullaryVarAtZeroArity
    |> testTransferredFieldsSafeWhenPointerFieldPassedThroughUnchanged
    |> testTransferredFieldsUnsafeWhenPointerFieldReplaced
    |> testTransferredFieldsUnsafeWhenPatternNeverBoundThatField
    |> testTransferredFieldsSafeWithNoPointerFields
    |> testExprMentionsNameFindsBareVar
    |> testExprMentionsNameMissesUnrelatedLiteral
    |> testExprMentionsNameSeesThroughConsAndCall
    |> testExprMentionsNameIsShadowBlindInsideLet
    |> reportSuccess
