// expect: 0
Ashes.IO.print(match await Ashes.Task.sleep(10)
|> async
|> Ashes.Task.run with
    | Ok(Ok(n)) -> n
    | Ok(Error(_)) -> 1
    | Error(_) -> 1)
