// expect: 1234
let recursive copy xs =
    match xs with
        | [] -> []
        | head :: tail ->
            if true
            then head :: copy(tail)
            else copy(tail)
in
    let recursive digits acc xs =
        match xs with
            | [] -> acc
            | head :: tail -> digits(acc * 10 + head)(tail)
    in
        [1, 2, 3, 4]
        |> copy
        |> digits(0)
        |> Ashes.IO.print
