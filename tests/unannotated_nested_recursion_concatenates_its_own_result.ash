// A nested recursive let without an annotation concatenates its own self call's result inside a
// loop body.
// expect: 15
let recursive loop (remaining: Int) (checksum: Int) =
    if remaining == 0
    then checksum
    else
        let observed =
            let recursive render (items: List(Int)) =
                match items with
                    | [] -> "]"
                    | head :: tail -> Ashes.Text.fromInt(head) + render(tail)
            in render([-6, -8])
        in loop(remaining - 1)(checksum + Ashes.Text.byteLength(observed))

0
|> loop(3)
|> Ashes.IO.print
