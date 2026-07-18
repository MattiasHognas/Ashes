// expect: 42
Ashes.IO.print(match Ashes.Task.run(async(let _ = await Ashes.Task.sleep(5)
in
    let _ = await Ashes.Task.sleep(5)
    in 42)) with
    | Ok(n) -> n
    | Error(_) -> 0)
