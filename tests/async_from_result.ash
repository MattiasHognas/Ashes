// expect: 99
Ashes.IO.print(match Ashes.Task.run(async await Ashes.Task.fromResult(Ok(99))) with
    | Ok(Ok(n)) -> n
    | Ok(Error(_)) -> 0
    | Error(_) -> 0)
