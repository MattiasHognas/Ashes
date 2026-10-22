let recursive rebuild (items: List(Int)) =
    match items with
        | [] -> []
        | head :: tail -> head :: rebuild(tail)

let recursive loop (remaining: Int) (checksum: Int) =
    if remaining == 0
    then checksum
    else
        let observed =
            let recursive render (items: List(Int)) =
                match items with
                    | [] -> "]"
                    | head :: tail -> Ashes.Text.fromInt(head) + render(tail)
            in
                [-6, -8]
                |> rebuild
                |> render
        in loop(remaining - 1)(checksum + Ashes.Text.byteLength(observed))

0
|> loop(ROUNDS)
|> Ashes.IO.print
