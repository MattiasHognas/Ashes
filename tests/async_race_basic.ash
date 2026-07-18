// expect: 42
let task = Ashes.Task.race([Ashes.Task.task(42), Ashes.Task.task(99)])
in
    match await task with
        | Ok(n) -> Ashes.IO.print(n)
        | Error(msg) -> Ashes.IO.print(msg)
