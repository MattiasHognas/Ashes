// expect: 30 60
type FlowCount =
    | calls: Int
    | flow: List(List(Int))

let recursive recordCall (arguments: List(Int)) (flow: List(List(Int))) =
    match (arguments, flow) with
        | (argument :: restArguments, sources :: restFlow) -> (argument :: sources) :: recordCall(restArguments)(restFlow)
        | _ -> flow

let recursive countNodes (nodes: List(Int)) (flow: List(List(Int))) =
    match nodes with
        | [] -> flow
        | node :: rest ->
            flow
            |> recordCall([node, node + 1, node + 2])
            |> countNodes(rest)

let recursive countRecord (nodes: List(Int)) (count: FlowCount) =
    match nodes with
        | [] -> count
        | node :: rest -> countRecord(rest)((count with calls = count.calls + 1, flow = recordCall([node, node + 1, node + 2])(count.flow)))

let recursive cells (lists: List(List(Int))) (acc: Int) =
    match lists with
        | [] -> acc
        | first :: rest -> cells(rest)(acc + Ashes.Collection.List.length(first))

let plain (n: Int) =
    cells(countNodes([n])([[], [], []]))(0)

let counted (n: Int) =
    match FlowCount(calls = 0, flow = [[], [], []]) |> countRecord([n, n + 1]) with
        | FlowCount { calls = calls, flow = flow } -> cells(flow)(0)

let recursive sum (f: Int -> Int) (n: Int) (acc: Int) =
    if n == 0
    then acc
    else sum(f)(n - 1)(acc + f(n))

Ashes.IO.print(Ashes.Text.fromInt(sum(plain)(10)(0)) + " " + Ashes.Text.fromInt(sum(counted)(10)(0)))
