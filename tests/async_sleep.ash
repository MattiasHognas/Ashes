// expect: 0
Ashes.IO.print(match Ashes.Async.run(async
    await Ashes.Async.sleep(10)) with
    | Ok(n) -> n
    | Error(_) -> 1)
