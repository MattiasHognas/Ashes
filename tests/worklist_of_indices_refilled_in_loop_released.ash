// expect: 3000

type Block =
    | blockStart: Int
    | blockEnd: Int
    | blockSuccessors: List(Int)
    | blockPredecessors: List(Int)

let recursive listAt items index =
    match items with
        | [] -> None
        | head :: rest ->
            if index == 0
            then Some(head)
            else listAt(rest)(index - 1)

let recursive containsInt (item: Int) (items: List(Int)) =
    match items with
        | [] -> false
        | head :: rest ->
            if head == item
            then true
            else containsInt(item)(rest)

let recursive reach (blocks: List(Block)) (dominated: List(Int)) (pending: List(Int)) (reachable: List(Int)) =
    match pending with
        | [] -> Ashes.Collection.List.length(reachable)
        | current :: rest ->
            if containsInt(current)(dominated) == false || containsInt(current)(reachable)
            then reach(blocks)(dominated)(rest)(reachable)
            else
                match listAt(blocks)(current) with
                    | Some(Block { blockSuccessors = successors }) ->
                        reach(blocks)(dominated)(Ashes.Collection.List.append(successors)(rest))(current :: reachable)
                    | None -> reach(blocks)(dominated)(rest)(current :: reachable)

let blocksFor (seed: Int) = [Block(blockStart = 0, blockEnd = 3, blockSuccessors = [1, 2], blockPredecessors = []), Block(blockStart = 3, blockEnd = 5, blockSuccessors = [2], blockPredecessors = [0]), Block(blockStart = 5, blockEnd = seed + 6, blockSuccessors = [], blockPredecessors = [0, 1])]

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + reach(blocksFor(n))([0, 1, 2])([0])([]))

0
|> rounds(1000)
|> Ashes.IO.print
