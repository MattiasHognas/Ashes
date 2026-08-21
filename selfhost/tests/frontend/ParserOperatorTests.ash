import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import ParserExpressionTests
let operatorRootName expression =
    match ParserExpressionTests.unspan(expression) with
        | ExprAdd(_, _) -> "add"
        | ExprSubtract(_, _) -> "subtract"
        | ExprMultiply(_, _) -> "multiply"
        | ExprDivide(_, _) -> "divide"
        | ExprModulo(_, _) -> "modulo"
        | ExprBitwiseAnd(_, _) -> "and"
        | ExprBitwiseOr(_, _) -> "or"
        | ExprBitwiseXor(_, _) -> "xor"
        | ExprShiftLeft(_, _) -> "shift-left"
        | ExprShiftRight(_, _) -> "shift-right"
        | ExprBitwiseNot(_) -> "not-bits"
        | ExprLogicalNot(_) -> "not"
        | ExprGreaterThan(_, _) -> "greater"
        | ExprLessThan(_, _) -> "less"
        | ExprGreaterOrEqual(_, _) -> "greater-equal"
        | ExprLessOrEqual(_, _) -> "less-equal"
        | ExprEqual(_, _) -> "equal"
        | ExprNotEqual(_, _) -> "not-equal"
        | ExprResultPipe(_, _) -> "result-pipe"
        | ExprResultMapErrorPipe(_, _) -> "error-pipe"
        | ExprCall(_, _, _, _) -> "call"
        | ExprAwait(_) -> "await"
        | ExprPerform(_) -> "perform"
        | _ -> "other"

let recursive assertOperatorCases cases =
    match cases with
        | [] -> Unit
        | (source, expected) :: tail ->
            let expression = ParserExpressionTests.expectClean(source)
            in
                let checked =
                    expression
                    |> operatorRootName
                    |> test.assertEqual(expected)
                in assertOperatorCases(tail)

let checkNegativeFloat unit =
    match "-3.5"
    |> ParserExpressionTests.expectClean
    |> ParserExpressionTests.unspan with
        | ExprFloat(value, text) -> test.assertEqual((-3.5, "-3.5"))((value, text))
        | _ -> test.fail("expected folded negative float")

let checkNegativeInteger unit =
    match "-3"
    |> ParserExpressionTests.expectClean
    |> ParserExpressionTests.unspan with
        | ExprSubtract(zero, value) ->
            match (ParserExpressionTests.unspan(zero), ParserExpressionTests.unspan(value)) with
                | (ExprInt(0), ExprInt(3)) -> Unit
                | _ -> test.fail("expected integer negation operands")
        | _ -> test.fail("expected integer subtraction desugaring")

let run unit =
    [
        ("1 + 2", "add"),
        ("1 - 2", "subtract"),
        ("1 * 2", "multiply"),
        ("1 / 2", "divide"),
        ("1 % 2", "modulo"),
        ("1 & 2", "and"),
        ("1 | 2", "or"),
        ("1 ^ 2", "xor"),
        ("1 << 2", "shift-left"),
        ("1 >> 2", "shift-right"),
        ("~1", "not-bits"),
        ("!true", "not"),
        ("1 > 2", "greater"),
        ("1 < 2", "less"),
        ("1 >= 2", "greater-equal"),
        ("1 <= 2", "less-equal"),
        ("1 == 2", "equal"),
        ("1 != 2", "not-equal"),
        ("value |> f", "call"),
        ("value |?> f", "result-pipe"),
        ("value |!> f", "error-pipe"),
        ("await task", "await"),
        ("perform Clock.now(Unit)", "perform")
    ]
    |> assertOperatorCases
    |> checkNegativeFloat
    |> checkNegativeInteger
