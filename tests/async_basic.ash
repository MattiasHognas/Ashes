// expect: 42
Ashes.IO.print(match Ashes.Async.run(async
    42) with
    | Ok(n) -> n
    | Error(_) -> 0)
