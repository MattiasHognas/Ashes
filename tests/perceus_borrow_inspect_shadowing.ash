// expect: 3

type Body =
    | value: Int

let recursive sum values fallback total =
    match values with
        | [] -> total
        | body :: tail ->
            match body with
                | Body(value) ->
                    if value > 0
                    then sum(tail)(fallback)(total + value)
                    else
                        match fallback with
                            | [] -> total
                            | tail :: _ ->
                                match tail with
                                    | Body(other) -> total + other

Ashes.IO.print(sum([Body(value = 1), Body(value = 0)])([Body(value = 2)])(0))
