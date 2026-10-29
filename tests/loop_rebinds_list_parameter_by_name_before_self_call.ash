// expect: [2222]
let recursive build : List(Str) -> Int -> Str =
    given (table) ->
        given (remaining) ->
            if remaining <= 0
            then
                match table with
                    | head :: _ -> "[" + head + "]"
                    | [] -> "none"
            else
                if remaining % 3 == 1
                then build(table)(remaining - 1)
                else
                    let table = [Ashes.Text.fromInt(remaining * 1111), "next"]
                    in build(table)(remaining - 1)

7
|> build(["1"])
|> Ashes.IO.print
