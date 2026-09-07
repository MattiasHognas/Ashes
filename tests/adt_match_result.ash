// expect: 1
let getOrDefault res def =
    match res with
        | Ok(x) -> x
        | Error(_) -> def
in
    0
    |> getOrDefault(Ok(1))
    |> Ashes.IO.print
