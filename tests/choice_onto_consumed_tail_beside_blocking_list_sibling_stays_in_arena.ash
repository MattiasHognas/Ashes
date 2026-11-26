// expect: 14
// A loop parameter of nested lists passed on as a choice between a cell consed onto its consumed
// tail and that tail, beside a list sibling that keeps it in the arena: the join's arms read the
// parameter as the arena value it is placed as, so neither arm is retained or copied onto the
// reference-counted heap for a parameter that never releases them.
let recursive skip (items: List(List(Int))) (kept: List(Int)) (acc: Int) =
    match (items, kept) with
        | ((x :: xs) :: rest, same) ->
            skip(if x > 3
            then (x - 1 :: xs) :: rest
            else rest)(same)(acc + x)
        | (_, k) -> acc + Ashes.Collection.List.length(k)

0
|> skip([[5, 9], [1, 2]])([7])
|> Ashes.IO.print
