// expect: 10
let unwrapOr opt def =
    match opt with
        | None -> def
        | Some(x) -> x
in
    0
    |> unwrapOr(Some(10))
    |> Ashes.IO.print
