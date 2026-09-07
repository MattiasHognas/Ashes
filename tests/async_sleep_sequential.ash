// expect: 42
Ashes.IO.print(match (let _ = await Ashes.Task.sleep(5)
in
    let _ = await Ashes.Task.sleep(5)
    in 42)
|> async
|> Ashes.Task.run with
    | Ok(n) -> n
    | Error(_) -> 0)
