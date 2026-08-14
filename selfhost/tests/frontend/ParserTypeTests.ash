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

let run unit =
    (let arrow = expectCleanType("Int -> Result(Str, e) needs {Clock, State(Int) | effects}")
    in
        let arrowChecked =
            match unspanType(arrow) with
                | TypeArrow(from, destination, ("Clock", []) :: ("State", stateArguments) :: [], Some("effects")) ->
                    match (unspanType(from), unspanType(destination), stateArguments) with
                        | (TypeNamed("Int"), TypeApplied("Result", _), stateArgument :: []) ->
                            match unspanType(stateArgument) with
                                | TypeNamed("Int") -> Unit
                                | _ -> test.fail("expected State type argument")
                        | _ -> test.fail("expected arrow type structure")
                | _ -> test.fail("expected capability-bearing arrow")
        in
            let rightAssociative = expectCleanType("Int -> Str -> Bool")
            in
                let rightAssociativeChecked =
                    match unspanType(rightAssociative) with
                        | TypeArrow(_, destination, [], None) ->
                            match unspanType(destination) with
                                | TypeArrow(_, _, [], None) -> Unit
                                | _ -> test.fail("expected nested result arrow")
                        | _ -> test.fail("expected outer arrow")
                in
                    let tuple = expectCleanType("(Int, Str, List(a))")
                    in
                        let tupleChecked =
                            match unspanType(tuple) with
                                | TypeTuple(_first :: _second :: _third :: []) -> Unit
                                | _ -> test.fail("expected tuple type")
                        in
                            let unitType = expectCleanType("()")
                            in
                                let unitChecked =
                                    match unspanType(unitType) with
                                        | TypeUnit -> Unit
                                        | _ -> test.fail("expected unit type")
                                in
                                    let bareRow = expectCleanType("Int -> Int needs effects")
                                    in
                                        let bareRowChecked =
                                            match unspanType(bareRow) with
                                                | TypeArrow(_, _, [], Some("effects")) -> Unit
                                                | _ -> test.fail("expected bare effect-row variable")
                                        in
                                            let invalidRow = parseTypeExpression("Int needs {Clock}")
                                            in
                                                match invalidRow.diagnostics with
                                                    | diagnostic :: [] -> test.assertEqual("'needs' requires a function type to attach to.")(diagnostic.message)
                                                    | _ -> test.fail("expected detached-row diagnostic"))
