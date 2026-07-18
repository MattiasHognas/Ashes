// expect: 42
Ashes.IO.print(match Ashes.Task.run(async 42) with
    | Ok(n) -> n
    | Error(_) -> 0)
