// A list taken from a call's tuple result, returned by one if branch and consed onto by the other,
// then consed onto again and walked: the consing branch must still see the list.
// expect: 101
let recursive build (n: Int) (acc: List(Bytes)) =
    if n == 0
    then (acc, 5)
    else build(n - 1)(Ashes.Byte.allocate(n) :: acc)

let recursive total (items: List(Bytes)) =
    match items with
        | [] -> 0
        | item :: rest -> Ashes.Byte.length(item) + total(rest)

let recursive count (items: List(Int)) (acc: List(Bytes)) =
    match items with
        | [] -> acc
        | item :: rest -> count(rest)(Ashes.Byte.allocate(item) :: acc)

let assemble (offsets: List(Int)) =
    match build(3)([]) with
        | (pieces, cursor) ->
            let padded =
                if cursor > 2
                then Ashes.Byte.allocate(1) :: pieces
                else pieces
            in
                Ashes.Byte.allocate(64) :: padded
                |> count(offsets)
                |> total

[10, 20]
|> assemble
|> Ashes.IO.print
