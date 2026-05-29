// expect: 42
let task = async
    let x = 40 in
    let result = await Ashes.Async.all([async 1, async 1]) in
    match result with
        | a :: b :: [] -> x + a + b
        | _ -> 0
in Ashes.IO.print(match Ashes.Async.run(task) with
    | Ok(n) -> n
    | Error(_) -> 0)
