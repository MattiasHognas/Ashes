// The same join at a loop's exit in tail position, and in a function returning the join.
// expect: 10 13
let recursive build (n: Int) (acc: List(Bytes)) =
    if n == 0
    then (acc, 5)
    else build(n - 1)(Ashes.Byte.allocate(n) :: acc)

let recursive total (items: List(Bytes)) =
    match items with
        | [] -> 0
        | item :: rest -> Ashes.Byte.length(item) + total(rest)

let recursive settle (k: Int) (items: List(Bytes)) =
    match build(2)(items) with
        | (pieces, cursor) ->
            if k == 0
            then
                if cursor > 2
                then Ashes.Byte.allocate(1) :: pieces
                else pieces
            else settle(k - 1)(pieces)

let pick (flag: Bool) =
    match build(3)([]) with
        | (pieces, cursor) ->
            if flag
            then Ashes.Byte.allocate(1) :: pieces
            else pieces

Ashes.IO.print(Ashes.Text.fromInt([]
|> settle(2)
|> total) + " " + Ashes.Text.fromInt(total(pick(true)) + total(pick(false))))
