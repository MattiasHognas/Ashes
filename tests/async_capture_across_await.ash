// expect: 15
let x = 5
in Ashes.IO.print(match Ashes.Async.run(async
    let a =
        await (async
            10)
    in x + a) with
    | Ok(n) -> n
    | Error(_) -> 0)
