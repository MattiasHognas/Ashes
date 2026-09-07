// expect: 10
let unwrapOr opt def =
    match opt with
        | None -> def
        | Some(x) -> x
in
    10
    |> unwrapOr(None)
    |> Ashes.IO.print
