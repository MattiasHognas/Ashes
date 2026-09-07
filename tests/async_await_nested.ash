// expect: 10
Ashes.IO.print(match (match await async 10 with
    | Error(_) -> 0
    | Ok(x) -> x)
|> async
|> Ashes.Task.run with
    | Ok(n) -> n
    | Error(_) -> 0)
