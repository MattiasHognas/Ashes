// expect: 15
let x = 5
in
    Ashes.IO.print(match Ashes.Task.run(async(match await async 10 with
        | Error(_) -> 0
        | Ok(a) -> x + a)) with
        | Ok(n) -> n
        | Error(_) -> 0)
