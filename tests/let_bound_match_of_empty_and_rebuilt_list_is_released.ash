// A `let` bound to a match whose arms are the empty list and a rebuilt list owns the list it
// holds, inside a loop.
// expect: 3
let recursive loop (remaining: Int) (checksum: Int) =
    if remaining == 0
    then checksum
    else
        let observed =
            let generated =
                let reuseList =
                    let element = 9
                    in [element]
                in
                    match reuseList with
                        | [] -> []
                        | head :: tail -> head :: tail
            in Ashes.Collection.List.length(generated)
        in loop(remaining - 1)(checksum + observed)

0
|> loop(3)
|> Ashes.IO.print
