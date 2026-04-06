// expect: 99
Ashes.IO.print(match Ashes.Async.run(async
    await Ashes.Async.fromResult(Ok(99))) with
    | Ok(n) -> n
    | Error(_) -> 0)
