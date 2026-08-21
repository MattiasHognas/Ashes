import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
let run unit =
    (let intType = TypeNamed("Int")
    in
        let functionType = TypeArrow(intType)(TypeNamed("Int"))([])(None)
        in
            let expression =
                ExprLet(
                    "increment",
                    ExprLambda("value")(ExprAdd(ExprVar("value"))(ExprInt(1)))(Some(intType)),
                    ExprCall(ExprVar("increment"))(ExprInt(41))(false)(callArgumentsInline),
                    ["value"],
                    Some(functionType),
                    []
                )
            in
                ((given (_) ->
                    match PatternOr([PatternConstructor("Some")([PatternVar("value")]), PatternEmptyList]) with
                        | PatternOr(PatternConstructor(constructorName, PatternVar(bindingName) :: []) :: PatternEmptyList :: []) ->
                            test.assertEqual(
                                ("Some", "value"),
                                (constructorName, bindingName)
                            )
                        | _ -> test.fail("expected composed pattern syntax")))(match expression with
                    | ExprLet(name, _value, _body, _sugarParameters, _typeAnnotation, _requirements) ->
                        test.assertEqual(
                            "increment",
                            name
                        )
                    | _ -> test.fail("expected let expression")))
