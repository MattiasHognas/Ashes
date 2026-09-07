// expect: 1
let last xs =
    match xs with
        | [] -> Ashes.IO.panic("empty")
        | x :: rest -> x
in
    [1]
    |> last
    |> Ashes.IO.print
