// expect: 0
Ashes.IO.print(match Ashes.Task.run(async await Ashes.Task.sleep(10)) with
    | Ok(Ok(n)) -> n
    | Ok(Error(_)) -> 1
    | Error(_) -> 1)
