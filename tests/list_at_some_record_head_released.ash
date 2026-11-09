// expect: 15
type Block =
    | blockStart: Int
    | blockSuccessors: List(Int)

let recursive listAt (items: List(Block)) (index: Int) =
    match items with
        | [] -> None
        | head :: rest ->
            if index == 0
            then Some(head)
            else listAt(rest)(index - 1)

let successorsOfIndex (blocks: List(Block)) (index: Int) =
    match listAt(blocks)(index) with
        | Some(Block { blockSuccessors = successors }) -> successors
        | None -> []

let recursive total (xs: List(Int)) =
    match xs with
        | [] -> 0
        | x :: rest -> x + total(rest)

let probe (n: Int) =
    (let blocks = [Block(blockStart = n, blockSuccessors = [1]), Block(blockStart = 1, blockSuccessors = [n, 3])]
    in
        1
        |> successorsOfIndex(blocks)
        |> total)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

0
|> loop(3)
|> Ashes.IO.print
