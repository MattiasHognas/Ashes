// expect: [-10]
Ashes.IO.print(let recursive render : List(Int) -> Str =
    given (items: List(Int)) ->
        match items with
            | [] -> "]"
            | head :: tail ->
                Ashes.Text.fromInt(head) + (match tail with
                    | [] -> "]"
                    | _ -> "," + render(tail))
in
    "[" + render(let generatedResult : List(Int) =
        let captured =
            let element = -10
            in [element]
        in
            match -1 < -2 with
                | true ->
                    ((given (_: Unit) -> captured))(Unit)
                | false -> captured
    in generatedResult))
