import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import ParserExpressionTests
let run unit =
    (let annotatedLet = ParserExpressionTests.expectClean("let identity : a -> a = given (value: a) -> value in identity")
    in
        let annotatedLetChecked =
            match ParserExpressionTests.unspan(annotatedLet) with
                | ExprLet("identity", value, _, [], Some(_annotation), []) ->
                    match ParserExpressionTests.unspan(value) with
                        | ExprLambda("value", _, Some(_parameterType)) -> Unit
                        | _ -> test.fail("expected annotated lambda parameter")
                | _ -> test.fail("expected annotated let")
        in
            let constrained = ParserExpressionTests.expectClean("let same : a -> a -> Bool requires {Ashes.Trait.Eq(a)} = value in same")
            in
                let constrainedChecked =
                    match ParserExpressionTests.unspan(constrained) with
                        | ExprLet("same", _, _, [], Some(_annotation), requirement :: []) -> test.assertEqual("Ashes.Trait.Eq")(requirement.traitName)
                        | _ -> test.fail("expected constrained annotation")
                in
                    let sugar = ParserExpressionTests.expectClean("let render (value: Int) suffix = value in render")
                    in
                        match ParserExpressionTests.unspan(sugar) with
                            | ExprLet("render", value, _, "value" :: "suffix" :: [], None, []) ->
                                match ParserExpressionTests.unspan(value) with
                                    | ExprLambda("value", _, Some(_)) -> Unit
                                    | _ -> test.fail("expected annotated sugar parameter")
                            | _ -> test.fail("expected sugar parameters"))
