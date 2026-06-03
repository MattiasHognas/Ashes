// expect: 99
Ashes.IO.print(match Ashes.Async.run(async
    await Ashes.Async.fromResult(Ok(99))) with
    | Ok(Ok(n)) -> n
    | Ok(Error(_)) -> 0
    | Error(_) -> 0)
