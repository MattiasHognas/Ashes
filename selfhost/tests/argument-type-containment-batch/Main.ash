import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.ArgumentTypeContainment.mayContainArgumentType
import AshesCompiler.Semantics.ArgumentTypeContainment.mayContainAnyArgumentType
import FrozenContainment.mayContainArgumentType as frozenMayContainArgumentType
let unchanged (semanticType: SemanticType) = semanticType

let recursive oldAny resolve fields blocked result candidates =
    match candidates with
        | [] -> false
        | candidate :: rest -> frozenMayContainArgumentType(resolve)(fields)(blocked)(result)(candidate) || oldAny(resolve)(fields)(blocked)(result)(rest)

let check resolve fields blocked result candidates =
    if oldAny(resolve)(fields)(blocked)(result)(candidates) == mayContainAnyArgumentType(resolve)(fields)(blocked)(result)(candidates)
    then Unit
    else Ashes.IO.panic("containment batch differs from frozen oracle")

let graphType node = SemNamed(node)("Graph" + Ashes.Text.fromInt(node))([])

let recursive powerOfTwo exponent =
    if exponent == 0
    then 1
    else 2 * powerOfTwo(exponent - 1)

let graphEdge graph source target = graph / powerOfTwo(source * 3 + target) % 2 == 1

let recursive graphNeighbors graph source target =
    if target == 3
    then
        if source == 2
        then [SemString]
        else []
    else
        if graphEdge(graph)(source)(target)
        then graphType(target) :: graphNeighbors(graph)(source)(target + 1)
        else graphNeighbors(graph)(source)(target + 1)

let graphFields graph semanticType =
    match semanticType with
        | SemNamed(node, _name, []) -> graphNeighbors(graph)(node)(0)
        | _ -> []

let targetAt index =
    match index with
        | 0 -> graphType(0)
        | 1 -> graphType(1)
        | 2 -> SemString
        | _ -> SemBytes

let recursive graphTargets mask index =
    if index == 4
    then []
    else
        if mask / powerOfTwo(index) % 2 == 1
        then targetAt(index) :: graphTargets(mask)(index + 1)
        else graphTargets(mask)(index + 1)

let recursive checkMasks graph source mask =
    if mask == 16
    then Unit
    else
        0
        |> graphTargets(mask)
        |> check(unchanged)(graphFields(graph))([])(graphType(source))
        |> (given (_) -> checkMasks(graph)(source)(mask + 1))

let recursive checkGraphs graph =
    if graph == 512
    then Unit
    else
        0
        |> checkMasks(graph)(0)
        |> (given (_) -> checkMasks(graph)(1)(0))
        |> (given (_) -> checkMasks(graph)(2)(0))
        |> (given (_) -> checkGraphs(graph + 1))

let box argument = SemNamed(100)("Box")([argument])

let fields semanticType =
    match semanticType with
        | SemNamed(100, "Box", argument :: []) -> [SemList(argument)]
        | _ -> []

let growingFields semanticType =
    match semanticType with
        | SemNamed(100, "Box", argument :: []) -> [box(SemList(argument))]
        | _ -> []

let resolveVariable replacement semanticType = applySubstitution([(7, replacement)])(semanticType)

let checkSpecialCases unit =
    []
    |> check(unchanged)(fields)([])(SemVariable(7))
    |> (given (_) ->
        check(unchanged)(fields)([])(SemFunction(SemInt)(SemInt)(None))([]))
    |> (given (_) ->
        check(unchanged)(growingFields)([])(box(SemInt))([]))
    |> (given (_) -> check(unchanged)(fields)([])(SemVariable(7))([SemBytes, SemString]))
    |> (given (_) ->
        check(unchanged)(fields)([])(SemParameter(7)("a"))([SemBytes]))
    |> (given (_) ->
        check(unchanged)(fields)([])(SemFunction(SemInt)(SemInt)(None))([SemString]))
    |> (given (_) ->
        check(unchanged)(fields)(["Box"])(box(SemString))([SemList(SemString), SemBytes]))
    |> (given (_) ->
        check(unchanged)(fields)(["Box"])(box(SemString))([SemBytes, SemString]))
    |> (given (_) ->
        check(unchanged)(growingFields)([])(box(SemInt))([SemBytes, SemString]))
    |> (given (_) -> check(unchanged)(fields)([])(SemTuple([box(SemInt), box(SemString)]))([SemBytes, SemList(SemString)]))
    |> (given (_) -> check(unchanged)(fields)([])(SemTuple([box(SemInt), box(SemInt)]))([SemBytes, SemList(SemString)]))
    |> (given (_) -> check(unchanged)(fields)([])(SemPointer(SemString))([SemBytes, SemString, SemString]))
    |> (given (_) ->
        check(resolveVariable(SemInt))(fields)([])(box(SemVariable(7)))([SemString, SemList(SemInt)]))
    |> (given (_) ->
        check(resolveVariable(SemString))(fields)([])(box(SemVariable(7)))([SemList(SemInt)]))
    |> (given (_) ->
        check(resolveVariable(SemInt))(fields)([])(SemVariable(7))([SemVariable(7)]))
    |> (given (_) ->
        check(unchanged)(fields)([])(SemNamed(101)("Box")([SemInt]))([box(SemInt)]))
    |> (given (_) ->
        check(unchanged)(fields)([])(SemCapability("Cap")([SemBytes]))([SemString, SemBytes]))
    |> (given (_) ->
        check(unchanged)(fields)([])(SemRow([SemCapability("Cap")([SemBytes])])(None))([SemBytes]))

let diamondType depth = SemNamed(depth)("Diamond" + Ashes.Text.fromInt(depth))([])

let diamondFields semanticType =
    match semanticType with
        | SemNamed(0, _name, []) -> [SemInt]
        | SemNamed(depth, _name, []) -> [diamondType(depth - 1), diamondType(depth - 1)]
        | _ -> []

let recursive benchmarkTargets count =
    if count == 0
    then []
    else SemNamed(1000 + count)("Absent" + Ashes.Text.fromInt(count))([SemList(SemString)]) :: benchmarkTargets(count - 1)

let recursive run old remaining =
    if remaining == 0
    then Ashes.IO.print("containment batch passed")
    else
        let result = diamondType(remaining % 2 + 31)
        in
            let candidates = benchmarkTargets(16)
            in
                let answer =
                    if old
                    then oldAny(unchanged)(diamondFields)([])(result)(candidates)
                    else mayContainAnyArgumentType(unchanged)(diamondFields)([])(result)(candidates)
                in
                    if answer
                    then Ashes.IO.panic("absent candidate became reachable")
                    else
                        if mayContainArgumentType(unchanged)(diamondFields)([])(result)(SemInt) && mayContainAnyArgumentType(unchanged)(diamondFields)([])(SemTuple([result, box(SemString)]))([SemBytes, SemInt])
                        then run(old)(remaining - 1)
                        else Ashes.IO.panic("singleton containment changed")

let iterations text =
    match text with
        | "1" -> 1
        | "1000" -> 1000
        | "10000" -> 10000
        | _ -> Ashes.IO.panic("expected 1, 1000 or 10000 iterations")

match Ashes.IO.args with
    | "check" :: [] ->
        0
        |> checkGraphs
        |> (given (_) -> checkSpecialCases(Unit))
        |> (given (_) -> Ashes.IO.print("24576 graph decisions and special cases passed"))
    | "old" :: count :: [] ->
        count
        |> iterations
        |> run(true)
    | "new" :: count :: [] ->
        count
        |> iterations
        |> run(false)
    | _ -> Ashes.IO.panic("usage: containment-batch check | old|new 1|1000|10000")
