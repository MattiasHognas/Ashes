import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import ParserExpressionTests
let unspanType value =
    match value with
        | TypeAt(_span, inner) -> inner
        | _ -> value

let expectCleanType source =
    (let result = parseTypeExpression(source)
    in
        let diagnosticsChecked = test.assertEqual([])(result.diagnostics)
        in result.typeExpression)

let checkCapabilityArrow unit =
    match "Int -> Result(Str, e) needs {Clock, State(Int) | effects}"
    |> expectCleanType
    |> unspanType with
        | TypeArrow(from, destination, ("Clock", []) :: ("State", stateArguments) :: [], Some("effects")) ->
            match (unspanType(from), unspanType(destination), stateArguments) with
                | (TypeNamed("Int"), TypeApplied("Result", _), stateArgument :: []) ->
                    match unspanType(stateArgument) with
                        | TypeNamed("Int") -> Unit
                        | _ -> test.fail("expected State type argument")
                | _ -> test.fail("expected arrow type structure")
        | _ -> test.fail("expected capability-bearing arrow")

let checkRightAssociativeArrow unit =
    match "Int -> Str -> Bool"
    |> expectCleanType
    |> unspanType with
        | TypeArrow(_, destination, [], None) ->
            match unspanType(destination) with
                | TypeArrow(_, _, [], None) -> Unit
                | _ -> test.fail("expected nested result arrow")
        | _ -> test.fail("expected outer arrow")

let checkTupleType unit =
    match "(Int, Str, List(a))"
    |> expectCleanType
    |> unspanType with
        | TypeTuple(_first :: _second :: _third :: []) -> Unit
        | _ -> test.fail("expected tuple type")

let checkUnitType unit =
    match "()"
    |> expectCleanType
    |> unspanType with
        | TypeUnit -> Unit
        | _ -> test.fail("expected unit type")

let checkBareEffectRow unit =
    match "Int -> Int needs effects"
    |> expectCleanType
    |> unspanType with
        | TypeArrow(_, _, [], Some("effects")) -> Unit
        | _ -> test.fail("expected bare effect-row variable")

let checkDetachedEffectRow unit =
    match parseTypeExpression("Int needs {Clock}") with
        | TypeExpressionParseResult { typeExpression = _typeExpression, diagnostics = diagnostic :: [] } ->
            test.assertEqual(
                "'needs' requires a function type to attach to.",
                diagnostic.message
            )
        | _ -> test.fail("expected detached-row diagnostic")

let run unit =
    unit
    |> checkCapabilityArrow
    |> checkRightAssociativeArrow
    |> checkTupleType
    |> checkUnitType
    |> checkBareEffectRow
    |> checkDetachedEffectRow
