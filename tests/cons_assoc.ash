// expect: 2
let recursive len xs =
    match xs with
        | [] -> 0
        | _ :: rest -> 1 + len(rest)
in
    1 :: 2 :: []
    |> len
    |> Ashes.IO.print
