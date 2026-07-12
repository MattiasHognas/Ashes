// expect: 42
let task = Ashes.Async.race([Ashes.Async.task(42), Ashes.Async.task(99)])
in
    match await task with
        | Ok(n) -> Ashes.IO.print(n)
        | Error(msg) -> Ashes.IO.print(msg)
