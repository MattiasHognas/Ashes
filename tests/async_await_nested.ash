// expect: 10
Ashes.IO.print(match Ashes.Async.run(async(match await async 10 with
    | Error(_) -> 0
    | Ok(x) -> x)) with
    | Ok(n) -> n
    | Error(_) -> 0)
