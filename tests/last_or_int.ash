// expect: 3
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
    0
    |> lastOr([1, 2, 3])
    |> Ashes.IO.print
