// expect: 0
Ashes.IO.print(match Ashes.Async.run(async await Ashes.Async.sleep(10)) with
    | Ok(Ok(n)) -> n
    | Ok(Error(_)) -> 1
    | Error(_) -> 1)
