// expect: c
let recursive lastOr xs default =
    let recursive loop ys =
        match ys with
            | [] -> default
            | x :: rest ->
                match rest with
                    | [] -> x
                    | _ -> loop(rest)
    in loop(xs)
in
    ""
    |> lastOr(["a", "b", "c"])
    |> Ashes.IO.print
