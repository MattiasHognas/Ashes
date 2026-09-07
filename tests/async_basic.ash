// expect: 42
Ashes.IO.print(match 42
|> async
|> Ashes.Task.run with
    | Ok(n) -> n
    | Error(_) -> 0)
