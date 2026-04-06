// expect: 42
Ashes.IO.print(match Ashes.Async.run(async
    await Ashes.Async.race([async 42, async 99])) with
    | Ok(n) -> n
    | Error(_) -> 0)
