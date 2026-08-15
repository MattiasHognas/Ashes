import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import ParserExpressionTests
let checkAnnotatedLet unit =
    match "let identity : a -> a = given (value: a) -> value in identity"
    |> ParserExpressionTests.expectClean
    |> ParserExpressionTests.unspan with
        | ExprLet("identity", value, _, [], Some(_annotation), []) ->
            match ParserExpressionTests.unspan(value) with
                | ExprLambda("value", _, Some(_parameterType)) -> Unit
                | _ -> test.fail("expected annotated lambda parameter")
        | _ -> test.fail("expected annotated let")

let checkConstrainedLet unit =
    match "let same : a -> a -> Bool requires {Ashes.Trait.Eq(a)} = value in same"
    |> ParserExpressionTests.expectClean
    |> ParserExpressionTests.unspan with
        | ExprLet("same", _, _, [], Some(_annotation), requirement :: []) -> test.assertEqual("Ashes.Trait.Eq")(requirement.traitName)
        | _ -> test.fail("expected constrained annotation")

let checkAnnotatedSugar unit =
    match "let render (value: Int) suffix = value in render"
    |> ParserExpressionTests.expectClean
    |> ParserExpressionTests.unspan with
        | ExprLet("render", value, _, "value" :: "suffix" :: [], None, []) ->
            match ParserExpressionTests.unspan(value) with
                | ExprLambda("value", _, Some(_)) -> Unit
                | _ -> test.fail("expected annotated sugar parameter")
        | _ -> test.fail("expected sugar parameters")

let run unit =
    unit
    |> checkAnnotatedLet
    |> checkConstrainedLet
    |> checkAnnotatedSugar
