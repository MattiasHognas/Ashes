// expect: 1516
type Parts =
    | front: List(Int)
    | back: List(Int)

let produce (k: Int) = [k, k + 1]

let recursive pairs (remaining: Int) (acc: List((List(Int), List(Int)))) (other: List(Int)) (total: Int) =
    if remaining == 0
    then total + Ashes.Collection.List.length(acc) + Ashes.Collection.List.length(other)
    else
        pairs(remaining - 1)([(other, [remaining])])(produce(remaining))(total + Ashes.Collection.List.length(other))

let recursive records (remaining: Int) (acc: List(Parts)) (other: List(Int)) (total: Int) =
    if remaining == 0
    then total + Ashes.Collection.List.length(acc) + Ashes.Collection.List.length(other)
    else
        records(remaining - 1)([Parts(front = other, back = [remaining])])(produce(remaining))(total + Ashes.Collection.List.length(other))

Ashes.IO.print(pairs(300)([])([7])(0) + records(456)([])([7])(0))
