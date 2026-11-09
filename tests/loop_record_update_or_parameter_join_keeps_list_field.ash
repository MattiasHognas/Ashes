// expect: 40001
type FlowCount =
    | references: Int
    | calls: Int
    | flow: List(List(Int))

let recursive countNodes (self: Int) (nodes: List(Int)) (count: FlowCount) =
    match nodes with
        | [] -> count
        | name :: rest ->
            countNodes(self)(rest)((if name == self
            then count with references = count.references + 1
            else count))

let recursive emptyLists (count: Int) =
    if count == 0
    then []
    else [] :: emptyLists(count - 1)

let recursive size (lists: List(List(Int))) (acc: Int) =
    match lists with
        | [] -> acc
        | _first :: rest -> size(rest)(acc + 1)

let flowWith (nodes: List(Int)) =
    match FlowCount(references = 0, calls = 0, flow = emptyLists(3)) |> countNodes(7)(nodes) with
        | FlowCount { references = references, calls = _calls, flow = flow } -> size(flow)(references - 1)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + flowWith([7, n, 7]))

0
|> loop(10000)
|> Ashes.IO.print
