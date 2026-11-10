import Ashes.Test as test
import Ashes.Collection.List.append
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeGraphSearch.typeGraphAny
export (
    value runTypeGraphSearchTests,
)

let unchanged value = value

let expectResult (expected: Bool) (actual: Maybe(Bool)) =
    match actual with
        | Some(result) -> test.assertEqual(expected)(result)
        | None -> test.fail("unexpected nonregular recursion")

let expectFallback (actual: Maybe(Bool)) =
    match actual with
        | None -> Unit
        | Some(_result) -> test.fail("expected nonregular recursion fallback")

let isString value = value == SemString

let node depth = SemNamed(depth)("Node" + Ashes.Text.fromInt(depth))([])

let diamondFields value =
    match value with
        | SemNamed(0, _name, []) -> [SemInt]
        | SemNamed(depth, _name, []) -> [node(depth - 1), node(depth - 1)]
        | _ -> []

let recursive powerOfTwo exponent =
    if exponent == 0
    then 1
    else 2 * powerOfTwo(exponent - 1)

let edge graph source target = graph / powerOfTwo(source * 3 + target) % 2 == 1

let graphFields graph value =
    match value with
        | SemNamed(source, _name, []) ->
            append(if edge(graph)(source)(0)
            then [node(0)]
            else [])(
                append(if edge(graph)(source)(1)
                then [node(1)]
                else [])(
                    append(if edge(graph)(source)(2)
                    then [node(2)]
                    else [])(
                        if source == 2
                        then [SemString]
                        else []
                    )
                )
            )
        | _ -> []

// Independent bounded-path oracle: three nodes need at most two edges to reach node 2.
let reachable graph source = source == 2 || edge(graph)(source)(2) || edge(graph)(source)(0) && edge(graph)(0)(2) || edge(graph)(source)(1) && edge(graph)(1)(2)

let checkSource graph source =
    source
    |> node
    |> typeGraphAny(unchanged)(isString)(graphFields(graph))
    |> expectResult(reachable(graph)(source))

let recursive checkGraphs graph =
    if graph == 512
    then Unit
    else
        0
        |> checkSource(graph)
        |> (given (_) -> checkSource(graph)(1))
        |> (given (_) -> checkSource(graph)(2))
        |> (given (_) -> checkGraphs(graph + 1))

let box argument = SemNamed(100)("Box")([argument])

let boxFields value =
    match value with
        | SemNamed(100, "Box", argument :: []) -> [argument]
        | SemTuple(elements) -> elements
        | _ -> []

let growingFields value =
    match value with
        | SemNamed(100, "Box", argument :: []) -> [box(SemList(argument))]
        | _ -> []

let sharedIdFields value =
    match value with
        | SemNamed(0, "Outer", []) -> [SemNamed(0)("Inner")([])]
        | SemNamed(0, "Inner", []) -> [SemString]
        | _ -> []

let runTypeGraphSearchTests unit =
    26
    |> node
    |> typeGraphAny(unchanged)(isString)(diamondFields)
    |> expectResult(false)
    |> (given (_) -> checkGraphs(0))
    |> (given (_) ->
        SemTuple([box(SemInt), box(SemString)])
        |> typeGraphAny(unchanged)(isString)(boxFields)
        |> expectResult(true))
    |> (given (_) ->
        SemTuple([box(SemInt), box(SemBool)])
        |> typeGraphAny(unchanged)(isString)(boxFields)
        |> expectResult(false))
    |> (given (_) ->
        SemInt
        |> box
        |> typeGraphAny(unchanged)(isString)(growingFields)
        |> expectFallback)
    |> (given (_) ->
        []
        |> SemNamed(0)("Outer")
        |> typeGraphAny(unchanged)(isString)(sharedIdFields)
        |> expectResult(true))
