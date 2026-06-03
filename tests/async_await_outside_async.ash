// expect: 42
match await Ashes.Async.task(42) with
    | Ok(n) -> Ashes.IO.print(n)
    | Error(msg) -> Ashes.IO.print(msg)
