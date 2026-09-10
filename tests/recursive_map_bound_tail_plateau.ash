// expect: 3688895
let decorate (value: Str) = value + "!"

let recursive mapText (values: List(Str)) =
    match values with
        | [] -> []
        | head :: tail ->
            let decorated = decorate(head)
            in
                let mappedTail = mapText(tail)
                in
                    let alias = mappedTail
                    in decorated :: alias

let recursive sumLengths values accumulator =
    match values with
        | [] -> accumulator
        | head :: tail -> sumLengths(tail)(accumulator + Ashes.Text.byteLength(head))

let recursive loop count checksum =
    if count == 0
    then checksum
    else
        let values = mapText([Ashes.Text.fromInt(count), "hello", "world"])
        in loop(count - 1)(checksum + sumLengths(values)(0))

0
|> loop(200000)
|> Ashes.Text.fromInt
|> Ashes.IO.print
