// expect: 600
// exit: 0

let recursive aliasLoop : Int -> Int -> Int =
    given (remaining: Int) ->
        given (checksum: Int) ->
            if remaining == 0
            then checksum
            else
                let observed =
                    match let aliasA =
                        let first = ""
                        in
                            let second = ""
                            in (first, second)
                    in
                        let aliasB = aliasA
                        in aliasB with
                        | (left, right) -> "(" + left + "," + right + ")"
                in aliasLoop(remaining - 1)(checksum + Ashes.Text.byteLength(observed))

0
|> aliasLoop(200)
|> Ashes.Text.fromInt
|> Ashes.IO.print
