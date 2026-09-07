// expect: 99
Ashes.IO.print(match await Ashes.Task.fromResult(Ok(99))
|> async
|> Ashes.Task.run with
    | Ok(Ok(n)) -> n
    | Ok(Error(_)) -> 0
    | Error(_) -> 0)
