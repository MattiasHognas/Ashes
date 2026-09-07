// expect: 15
let x = 5
in
    Ashes.IO.print(match (match await async 10 with
        | Error(_) -> 0
        | Ok(a) -> x + a)
    |> async
    |> Ashes.Task.run with
        | Ok(n) -> n
        | Error(_) -> 0)
