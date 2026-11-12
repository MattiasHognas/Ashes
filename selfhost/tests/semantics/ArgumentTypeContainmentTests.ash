import Ashes.Test as test
import Ashes.Collection.List.append
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.ArgumentTypeContainment.mayContainArgumentType
export (
    value runArgumentTypeContainmentTests,
)

let unchanged (semanticType: SemanticType) = semanticType

let diamondType depth = SemNamed(depth)("Diamond" + Ashes.Text.fromInt(depth))([])

let diamondFields semanticType =
    match semanticType with
        | SemNamed(0, _name, []) -> [SemInt]
        | SemNamed(depth, _name, []) -> [diamondType(depth - 1), diamondType(depth - 1)]
        | _ -> []

let boxType argument = SemNamed(100)("Box")([argument])

let boxFields semanticType =
    match semanticType with
        | SemNamed(100, "Box", argument :: []) -> [SemList(argument)]
        | _ -> []

let recursiveType = SemNamed(200)("Recursive")([])

let recursiveFields semanticType =
    match semanticType with
        | SemNamed(200, "Recursive", []) -> [recursiveType]
        | _ -> []

let recursiveFieldsWithString semanticType =
    match semanticType with
        | SemNamed(200, "Recursive", []) -> [recursiveType, SemString]
        | _ -> []

let growingFields semanticType =
    match semanticType with
        | SemNamed(100, "Box", argument :: []) -> [boxType(SemList(argument))]
        | _ -> []

let graphType node = SemNamed(node)("Graph" + Ashes.Text.fromInt(node))([])

let recursive powerOfTwo exponent =
    if exponent == 0
    then 1
    else 2 * powerOfTwo(exponent - 1)

let graphEdge graph source target = graph / powerOfTwo(source * 3 + target) % 2 == 1

let graphFields graph semanticType =
    match semanticType with
        | SemNamed(node, _name, []) ->
            append(if graphEdge(graph)(node)(0)
            then [graphType(0)]
            else [])(
                append(if graphEdge(graph)(node)(1)
                then [graphType(1)]
                else [])(
                    append(if graphEdge(graph)(node)(2)
                    then [graphType(2)]
                    else [])(
                        if node == 2
                        then [SemString]
                        else []
                    )
                )
            )
        | _ -> []

// Every reachable node in a three-node graph has a simple path of at most two edges.
// This bounded enumeration is independent of the visited-set implementation being tested.
let graphReachesString graph source = source == 2 || graphEdge(graph)(source)(2) || graphEdge(graph)(source)(0) && graphEdge(graph)(0)(2) || graphEdge(graph)(source)(1) && graphEdge(graph)(1)(2)

let checkGraphSource graph source =
    SemString
    |> mayContainArgumentType(unchanged)(graphFields(graph))([])(graphType(source))
    |> test.assertEqual(graphReachesString(graph)(source))

let recursive checkAllGraphs graph =
    if graph == 512
    then Unit
    else
        0
        |> checkGraphSource(graph)
        |> (given (_) -> checkGraphSource(graph)(1))
        |> (given (_) -> checkGraphSource(graph)(2))
        |> (given (_) -> checkAllGraphs(graph + 1))

// Twenty-six shared levels have over sixty million paths but only twenty-seven named types.
// The old path-local expansion revisits descendants at every diamond.
let runArgumentTypeContainmentTests unit =
    SemString
    |> mayContainArgumentType(unchanged)(diamondFields)([])(diamondType(26))
    |> test.assertEqual(false)
    |> (given (_) ->
        SemInt
        |> mayContainArgumentType(unchanged)(diamondFields)([])(diamondType(26))
        |> test.assertEqual(true))
    |> (given (_) ->
        SemString
        |> mayContainArgumentType(unchanged)(recursiveFields)([])(recursiveType)
        |> test.assertEqual(false))
    |> (given (_) ->
        SemString
        |> mayContainArgumentType(unchanged)(recursiveFieldsWithString)([])(recursiveType)
        |> test.assertEqual(true))
    |> (given (_) ->
        SemList(SemString)
        |> mayContainArgumentType(unchanged)(boxFields)([])(SemTuple([boxType(SemInt), boxType(SemString)]))
        |> test.assertEqual(true))
    |> (given (_) ->
        SemList(SemString)
        |> mayContainArgumentType(unchanged)(boxFields)([])(SemTuple([boxType(SemInt), boxType(SemInt)]))
        |> test.assertEqual(false))
    |> (given (_) ->
        SemString
        |> mayContainArgumentType(unchanged)(growingFields)([])(boxType(SemInt))
        |> test.assertEqual(true))
    |> (given (_) ->
        SemString
        |> mayContainArgumentType(unchanged)(boxFields)(["Box"])(boxType(SemString))
        |> test.assertEqual(true))
    |> (given (_) ->
        SemList(SemString)
        |> mayContainArgumentType(unchanged)(boxFields)(["Box"])(boxType(SemString))
        |> test.assertEqual(false))
    |> (given (_) ->
        SemString
        |> mayContainArgumentType(unchanged)(boxFields)([])(SemVariable(1))
        |> test.assertEqual(true))
    |> (given (_) ->
        SemString
        |> mayContainArgumentType(unchanged)(boxFields)([])(SemFunction(SemInt)(SemInt)(None))
        |> test.assertEqual(true))
    |> (given (_) ->
        SemString
        |> mayContainArgumentType(unchanged)(boxFields)([])(SemPointer(SemString))
        |> test.assertEqual(true))
    |> (given (_) -> checkAllGraphs(0))
    |> (given (_) ->
        SemString
        |> mayContainArgumentType(unchanged)(boxFields)([])(SemTuple([boxType(SemInt), boxType(SemBool)]))
        |> test.assertEqual(false))
    |> (given (_) -> Ashes.IO.print("all argument type containment tests passed"))
