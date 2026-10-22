capability Source(a) =
    | get : Unit -> a

let recursive loop (remaining: Int) (checksum: Int) =
    if remaining == 0
    then checksum
    else
        let observed : Str =
            handle (match perform Source.get(Unit) with
                | Ok(text) -> text
                | Error(_) -> "ash") with
                | Source.get(_) -> resume(Error("owned"))
                | return(result) -> result
        in loop(remaining - 1)(checksum + Ashes.Text.byteLength(observed))

0
|> loop(ROUNDS)
|> Ashes.IO.print
