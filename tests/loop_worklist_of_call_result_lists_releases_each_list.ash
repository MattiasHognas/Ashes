// expect: 115
// A loop pushes a call's owned list of block successors onto an arena worklist of lists; the worklist
// cell stores an arena copy of the list and the original is released at once.
type Block =
    | start: Int
    | successors: List(Int)

let recursive listAt (items: List(Block)) (index: Int) =
    match items with
        | [] -> None
        | head :: rest ->
            if index == 0
            then Some(head)
            else listAt(rest)(index - 1)

let successorsOfIndex (blocks: List(Block)) (index: Int) =
    match listAt(blocks)(index) with
        | Some(Block { successors = found }) -> found
        | None -> []

let recursive postorder (blocks: List(Block)) (pending: List(List(Int))) (order: List(Int)) =
    match pending with
        | [] :: pendingRest -> postorder(blocks)(pendingRest)(order)
        | (successor :: more) :: pendingRest -> postorder(blocks)(successorsOfIndex(blocks)(successor) :: more :: pendingRest)(successor :: order)
        | _ -> order

let recursive total (xs: List(Int)) =
    match xs with
        | [] -> 0
        | x :: rest -> x + total(rest)

let probe (n: Int) =
    (let blocks = [Block(start = n, successors = [1]), Block(start = 1, successors = [2]), Block(start = 2, successors = [3]), Block(start = 3, successors = [])]
    in
        [n]
        |> postorder(blocks)([successorsOfIndex(blocks)(0)])
        |> total)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

0
|> loop(10)
|> Ashes.IO.print
