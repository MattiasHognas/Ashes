let recursive traverse : List(Int) -> Float =
    given (items: List(Int)) ->
        match items with
            | [] -> 0.0
            | _ :: tail -> traverse(tail)
in 0.0
